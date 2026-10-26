using Arb.Core.Instruments;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Risk;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Pricing;

public class SpatialArbPricerTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    private static SpatialParams Params(ResolvedFee buyFee, ResolvedFee sellFee, double rebalance = 0, double minProfit = 0, double maxNotional = double.PositiveInfinity) => new()
    {
        BuyFee = buyFee,
        SellFee = sellFee,
        BuySlipBps = 0,
        SellSlipBps = 0,
        RebalancePerUnitQuote = rebalance,
        MinMarginalEdgeBps = 0,
        MinProfitQuote = minProfit,
        MaxQtyCanon = double.PositiveInfinity,
        MaxBuyNotional = maxNotional,
        HaircutPpm = VwapWalker.FullLiquidityPpm,
        MaxLevelsPerSide = 20,
    };

    [Fact]
    public void Btc_EndToEnd_ReproducesDbScenario()
    {
        var ctx = new SpatialPairContext(_m.BinanceBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (59995m, 0.006m), (60007.50m, 0.010m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60150m, 0.010m), (60100m, 0.020m) }, new[] { (60160m, 1m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, Params(new(0.00075, FeeAssetKind.DiscountToken), new(0.001, FeeAssetKind.Quote), rebalance: 2.0, minProfit: 0.05));

        Assert.Equal(RejectReason.Execute, q.Decision);
        Assert.Equal(1000L, q.BuyLots);          // 0.01 BTC on Binance (1e-5)
        Assert.Equal(10_000L, q.SellLots);       // 0.01 BTC on Bybit (1e-6)
        Assert.Equal(6000750L, q.BuyLimitTicks); // marginal level, not VWAP
        Assert.Equal(6015000L, q.SellLimitTicks);
        Assert.Equal(60000.0, q.BuyVwap, 1e-9);
        Assert.Equal(60150.0, q.SellVwap, 1e-9);
        Assert.Equal(600.00m, q.BuyNotionalExact);
        Assert.Equal(0.01m, q.SellQtyExact);
        Assert.Equal(0.4285, q.Edge.NetQuote, 1e-9);
        Assert.Equal(25.0, q.Edge.GrossEdgeBps, 1e-9);
        Assert.Equal(25.835486290524, q.TobEdgeBps, 1e-9);
        Assert.Equal(2, q.BuyLevels);
        Assert.Equal(1, q.SellLevels);
    }

    [Fact]
    public void ThousandPepePerp_BuyLeg_vs_PepeSpot_SellLeg()
    {
        var ctx = new SpatialPairContext(_m.BybitPepePerp, _m.BitgetPepeSpot);
        BookView buy = TestMarkets.View(_m.BybitPepePerp, new[] { (0.0119900m, 1000m) }, new[] { (0.0120000m, 1000m), (0.0120100m, 2000m) });
        BookView sell = TestMarkets.View(_m.BitgetPepeSpot, new[] { (0.00001215m, 1_500_000m), (0.00001200m, 3_000_000m) }, new[] { (0.00001216m, 1_000_000m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, Params(new(0.00055, FeeAssetKind.Quote), new(0.0008, FeeAssetKind.Quote)));

        Assert.Equal(RejectReason.Execute, q.Decision);
        Assert.Equal(15L, q.BuyLots);             // 15 × 100 contracts × 1000 = 1,500,000 PEPE
        Assert.Equal(1_500_000L, q.SellLots);     // 1,500,000 PEPE
        Assert.Equal(1_500_000m, q.SellQtyExact);
        Assert.Equal(_m.BybitPepePerp.CanonicalQtyExact(q.BuyLots), q.SellQtyExact); // identical canonical qty both legs
        Assert.Equal(120100L, q.BuyLimitTicks);
        Assert.Equal(1215L, q.SellLimitTicks);
        Assert.Equal(18.005m, q.BuyNotionalExact);
        Assert.Equal(0.19551725, q.Edge.NetQuote, 1e-12);
    }

    [Fact]
    public void MissingMultiplier_ShowsFakeEdge_AndSanityGuardRejectsIt()
    {
        // Misconfigured: Bybit 1000PEPE registered with multiplier 1 → prices look 1000× higher.
        TestMarkets wrong = TestMarkets.Create(bybitPepeMultiplier: 1m);
        var ctx = new SpatialPairContext(wrong.BitgetPepeSpot, wrong.BybitPepePerp);
        BookView buy = TestMarkets.View(wrong.BitgetPepeSpot, new[] { (0.00001204m, 1_000_000m) }, new[] { (0.00001205m, 2_000_000m) });
        BookView sell = TestMarkets.View(wrong.BybitPepePerp, new[] { (0.0119900m, 200_000m) }, new[] { (0.0120000m, 1000m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, Params(new(0.001, FeeAssetKind.Base), new(0.00055, FeeAssetKind.Quote)));
        Assert.Equal(RejectReason.Execute, q.Decision); // the pricer alone would fire!
        Assert.True(q.Edge.GrossEdgeBps > 9_000_000, "a ~1000× 'edge' appears without the multiplier");

        var wallets = new WalletStatusBook(wrong.Assets);
        SanityResult s = SanityGuard.Evaluate(
            new SanityInput { Strategy = StrategyKind.Spatial, Buy = wrong.BitgetPepeSpot, Sell = wrong.BybitPepePerp, GrossEdgeBps = q.Edge.GrossEdgeBps },
            wallets,
            0,
            SanityLimits.From(new RiskLimits()));
        Assert.Equal(RejectReason.RejectSanity, s.Decision);
        Assert.Equal(SanityFlags.EdgeTooLarge, s.Flags & SanityFlags.EdgeTooLarge);

        // Correct multiplier: same books → no edge at all
        var right = new SpatialPairContext(_m.BitgetPepeSpot, _m.BybitPepePerp);
        BookView buyOk = TestMarkets.View(_m.BitgetPepeSpot, new[] { (0.00001204m, 1_000_000m) }, new[] { (0.00001205m, 2_000_000m) });
        BookView sellOk = TestMarkets.View(_m.BybitPepePerp, new[] { (0.0119900m, 200_000m) }, new[] { (0.0120000m, 1000m) });
        SpatialQuote ok = SpatialArbPricer.Evaluate(right, buyOk, sellOk, Params(new(0.001, FeeAssetKind.Base), new(0.00055, FeeAssetKind.Quote)));
        Assert.Equal(RejectReason.RejectThreshold, ok.Decision);
        Assert.True(ok.TobEdgeBps < 0);
    }

    [Fact]
    public void BaseFeeOnBuy_GrossesUpBuyLeg()
    {
        // Bitget spot buy, fee in BTC (no BGB) → buy 0.010011 to deliver 0.01 on Bybit
        var ctx = new SpatialPairContext(_m.BitgetBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BitgetBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 0.05m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60200m, 0.01m) }, new[] { (60210m, 1m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, Params(new(0.001, FeeAssetKind.Base), new(0.001, FeeAssetKind.Quote)));
        Assert.Equal(RejectReason.Execute, q.Decision);
        Assert.Equal(10_000L, q.SellLots);
        Assert.Equal(10_011L, q.BuyLots);
        Assert.Equal(0.000000989, q.Edge.ResidualBase, 1e-15);
        Assert.True(q.Edge.ResidualBase >= 0, "never short on the sell venue");
    }

    [Fact]
    public void Rejections_Stale_Threshold_MinNotional()
    {
        var ctx = new SpatialPairContext(_m.BinanceBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (59995m, 0.006m), (60007.50m, 0.010m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60150m, 0.010m), (60100m, 0.020m) }, new[] { (60160m, 1m) });
        BookView invalid = TestMarkets.View(_m.BybitBtcSpot, new[] { (60150m, 0.010m) }, new[] { (60160m, 1m) }, health: BookHealth.Invalid);
        var fees = Params(new(0.00075, FeeAssetKind.DiscountToken), new(0.001, FeeAssetKind.Quote));

        Assert.Equal(RejectReason.RejectStale, SpatialArbPricer.Evaluate(ctx, buy, invalid, fees).Decision);
        Assert.Equal(RejectReason.RejectThreshold, SpatialArbPricer.Evaluate(ctx, buy, sell, Params(new(0.00075, FeeAssetKind.DiscountToken), new(0.001, FeeAssetKind.Quote), minProfit: 1.0)).Decision);

        // 3 USDT notional cap → 0.00005 BTC ≈ 3 USDT < Binance min notional 5
        Assert.Equal(RejectReason.RejectMinNotional, SpatialArbPricer.Evaluate(ctx, buy, sell, Params(new(0.00075, FeeAssetKind.DiscountToken), new(0.001, FeeAssetKind.Quote), maxNotional: 3)).Decision);

        // Q* below one common lot (1e-5 BTC)
        BookView tiny = TestMarkets.View(_m.BybitBtcSpot, new[] { (60150m, 0.000005m) }, new[] { (60160m, 1m) });
        Assert.Equal(RejectReason.RejectMinNotional, SpatialArbPricer.Evaluate(ctx, buy, tiny, fees).Decision);

        Assert.Throws<ArgumentException>(() => SpatialArbPricer.Evaluate(ctx, sell, buy, fees));
    }

    [Fact]
    public void SlowLeg_GetsLargerSlippageBuffer_AndCanKillTheTrade()
    {
        // Tokyo host: Bitget leg ~2 ms horizon, Bybit (Singapore) leg ~35 ms stale data + ~35 ms order transit.
        // Deterministic ±3 bps moves every 50 ms → var/ms = 9e-8 / 50 = 1.8e-9.
        var vol = new EwmaVolatility(halfLifeMs: 5_000, minSampleMs: 1, initialVarPerMs: 0);
        long t = 0;
        for (int i = 0; i < 2_000; i++)
        {
            t += 50_000;
            vol.Update(i % 2 == 0 ? 60000.0 : 60000.0 * Math.Exp(0.0003), t);
        }

        Assert.Equal(1.8e-9, vol.VarPerMs, 1e-12);
        var model = new SlippageModel(baseBps: 0.5, volMultiplier: 2.0, maxBps: 50, coldStartBps: 10, minWarmSamples: 100);
        double fast = model.LegBps(vol, SlippageModel.HorizonMs(1, 1));   // 0.5 + 2·sqrt(1.8e-9·2)·1e4  ≈ 1.70 bps
        double slow = model.LegBps(vol, SlippageModel.HorizonMs(35, 35)); // 0.5 + 2·sqrt(1.8e-9·70)·1e4 ≈ 7.60 bps
        Assert.Equal(0.5 + (2 * Math.Sqrt(1.8e-9 * 2) * 1e4), fast, 1e-3);
        Assert.Equal(0.5 + (2 * Math.Sqrt(1.8e-9 * 70) * 1e4), slow, 1e-3);

        var ctx = new SpatialPairContext(_m.BitgetBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BitgetBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 0.05m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60150m, 0.01m) }, new[] { (60160m, 1m) });
        SpatialParams p = Params(new(0.0008, FeeAssetKind.DiscountToken), new(0.001, FeeAssetKind.Quote), minProfit: 0.01) with { BuySlipBps = fast, SellSlipBps = fast };
        Assert.Equal(RejectReason.Execute, SpatialArbPricer.Evaluate(ctx, buy, sell, p).Decision);

        // Same 25 bps gross edge, but the Singapore leg's honest buffer kills it.
        SpatialQuote qs = SpatialArbPricer.Evaluate(ctx, buy, sell, p with { SellSlipBps = slow });
        Assert.Equal(RejectReason.RejectThreshold, qs.Decision);
    }
}
