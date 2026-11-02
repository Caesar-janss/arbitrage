using Arb.Core.Instruments;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-19 (S3): base-fee gross-up leaves dust on the BUY venue, but v0.2 credited it at the SELL venue's VWAP. P21: coin
/// XYZ, buy venue lot = 1 coin, 10 bps fee in base → sell 20, buy 21, 0.979 XYZ left on the buy venue; credited at
/// 5.03 instead of the buy-venue bid 4.990 → net overstated by 0.03916 (9.2 % of net, enough to flip MinProfit trades).
/// </summary>
public class R19_ResidualMarkTests
{
    [Fact]
    public void LongDust_IsValuedAtTheBuyVenueBid()
    {
        var assets = AssetRegistry.Create("XYZ", "USDT");
        var b = new InstrumentRegistryBuilder(assets);
        InstrumentKey ka = b.Add(Venue.Bitget, MarketType.Spot, "XYZUSDT", "XYZ", "USDT", AccountKind.Spot, 0.001m, 1m, minNotional: 1m);
        InstrumentKey kb = b.Add(Venue.Bybit, MarketType.Spot, "XYZUSDT", "XYZ", "USDT", AccountKind.Unified, 0.001m, 0.01m, minNotional: 1m);
        InstrumentRegistry reg = b.Build();
        InstrumentSpec a = reg[ka];
        InstrumentSpec s = reg[kb];
        BookView buy = TestMarkets.View(a, new[] { (4.990m, 1000m) }, new[] { (5.000m, 1000m) });
        BookView sell = TestMarkets.View(s, new[] { (5.030m, 20m) }, new[] { (5.040m, 1000m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(new SpatialPairContext(a, s), buy, sell, new SpatialParams
        {
            BuyFee = new(0.001, FeeAssetKind.Base),
            SellFee = new(0.001, FeeAssetKind.Quote),
            MinProfitQuote = 0.05,
            MaxQtyCanon = double.PositiveInfinity,
            MaxBuyNotional = double.PositiveInfinity,
            HaircutPpm = VwapWalker.FullLiquidityPpm,
            MaxLevelsPerSide = 20,
        });

        Assert.Equal(21L, q.BuyLots);
        Assert.Equal(0.979, q.Edge.ResidualBase, 1e-12);
        Assert.Equal(0.979 * 4.990, q.Edge.ResidualValueQuote, 1e-9);       // v0.2: 0.979 × 5.03
        double cash = (20 * 5.03) - (21 * 5.0) - (20 * 5.03 * 0.001);
        Assert.Equal(cash + (0.979 * 4.990), q.Edge.NetQuote, 1e-9);        // 0.38461, v0.2: 0.42377
    }

    [Fact]
    public void ShortDust_IsValuedAtTheSellVenueAsk()
    {
        // fee in base on the SELL leg: the sell venue delivers qty + fee → short dust that must be bought back there
        var m = TestMarkets.Create();
        BookView buy = TestMarkets.View(m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 1m) });
        BookView sell = TestMarkets.View(m.BybitBtcSpot, new[] { (60200m, 1m) }, new[] { (60250m, 1m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(new SpatialPairContext(m.BinanceBtcSpot, m.BybitBtcSpot), buy, sell, new SpatialParams
        {
            BuyFee = new(0.00075, FeeAssetKind.DiscountToken),
            SellFee = new(0.001, FeeAssetKind.Base),
            MinProfitQuote = 0,
            MaxQtyCanon = 0.01,
            MaxBuyNotional = double.PositiveInfinity,
            HaircutPpm = VwapWalker.FullLiquidityPpm,
            MaxLevelsPerSide = 20,
        });
        Assert.True(q.Edge.ResidualBase < 0);
        Assert.Equal(q.Edge.ResidualBase * 60250, q.Edge.ResidualValueQuote, 1e-9);
    }
}
