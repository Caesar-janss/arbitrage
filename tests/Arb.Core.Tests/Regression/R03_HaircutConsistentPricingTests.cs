using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-03 (S2): Q* is sized assuming only HaircutPpm of displayed depth is takeable, so the IOC limit, the VWAP and the
/// net edge must be computed under the SAME assumption. Scenario P01: Binance asks 0.010 @ 60000 and 0.010 @ 60006,
/// Bybit bid 0.05 @ 60200, haircut 50 % → Q* = 0.01 BTC = 0.005 + 0.005 from TWO levels.
/// </summary>
public class R03_HaircutConsistentPricingTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    private static SpatialParams Params(int haircutPpm) => new()
    {
        BuyFee = new(0.00075, FeeAssetKind.DiscountToken),
        SellFee = new(0.001, FeeAssetKind.Quote),
        MinProfitQuote = 0.05,
        MaxQtyCanon = double.PositiveInfinity,
        MaxBuyNotional = double.PositiveInfinity,
        HaircutPpm = haircutPpm,
        MaxLevelsPerSide = 20,
    };

    [Fact]
    public void BuyLimit_CoversTheLevelsTheOptimizerCounted()
    {
        var ctx = new SpatialPairContext(_m.BinanceBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 0.010m), (60006m, 0.010m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60200m, 0.050m) }, new[] { (60210m, 1m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, Params(500_000));

        Assert.Equal(RejectReason.Execute, q.Decision);
        Assert.Equal(1_000L, q.BuyLots);
        Assert.Equal(6_000_600L, q.BuyLimitTicks);   // v0.2: 6_000_000 (L1 only)
        Assert.Equal(2, q.BuyLevels);
        Assert.Equal(60003.0, q.BuyVwap, 1e-9);      // v0.2: 60000

        // under the optimizer's own assumption, an IOC at this limit fills the whole buy leg
        VwapFill atLimit = VwapWalker.WalkLotsWithinLimit(buy.Asks, q.BuyLots, q.BuyLimitTicks, Side.Buy, 500_000);
        Assert.Equal(q.BuyLots, atLimit.FilledLots); // v0.2: 500 of 1000 → 0.005 BTC residual
    }

    [Fact]
    public void NetEdge_IsComputedAtTheHaircutVwap_NotOverstated()
    {
        var ctx = new SpatialPairContext(_m.BinanceBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 0.010m), (60006m, 0.010m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60200m, 0.050m) }, new[] { (60210m, 1m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, Params(500_000));

        // 0.01 × (60200 − 60003) − 0.01 × 60003 × 7.5 bps − 0.01 × 60200 × 10 bps
        double expected = (0.01 * (60200 - 60003)) - (0.01 * 60003 * 0.00075) - (0.01 * 60200 * 0.001);
        Assert.Equal(expected, q.Edge.NetQuote, 1e-9);   // 0.91797775; v0.2 reported 0.948
    }

    [Fact]
    public void SellSide_HaircutAlsoMovesTheSellLimit()
    {
        var ctx = new SpatialPairContext(_m.BinanceBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 1m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60200m, 0.010m), (60195m, 0.010m) }, new[] { (60210m, 1m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, Params(500_000));

        Assert.Equal(RejectReason.Execute, q.Decision);
        Assert.Equal(10_000L, q.SellLots);
        Assert.Equal(6_019_500L, q.SellLimitTicks);
        Assert.Equal(2, q.SellLevels);
        VwapFill atLimit = VwapWalker.WalkLotsWithinLimit(sell.Bids, q.SellLots, q.SellLimitTicks, Side.Sell, 500_000);
        Assert.Equal(q.SellLots, atLimit.FilledLots);
    }

    [Fact]
    public void FullLiquidity_IsUnchanged()
    {
        var ctx = new SpatialPairContext(_m.BinanceBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 0.010m), (60006m, 0.010m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60200m, 0.010m) }, new[] { (60210m, 1m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, Params(VwapWalker.FullLiquidityPpm));
        Assert.Equal(6_000_000L, q.BuyLimitTicks);
        Assert.Equal(1, q.BuyLevels);
    }
}
