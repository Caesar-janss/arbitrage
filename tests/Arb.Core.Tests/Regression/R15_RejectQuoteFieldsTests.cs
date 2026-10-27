using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-15 (S3), C# half: a rejected SpatialQuote carried 0 for VWAP / worst px / levels / TOB, and trd.opportunity declared
/// those NOT NULL with px &gt; 0 and levels &gt; 0 → a reject row could not be written, and one bad row aborts a whole COPY
/// batch (EXECUTE rows included). Contract: a value the pricer did not compute is NaN (→ NULL), never 0 (P20).
/// </summary>
public class R15_RejectQuoteFieldsTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    private static SpatialParams Params => new()
    {
        BuyFee = new(0.00075, FeeAssetKind.DiscountToken),
        SellFee = new(0.001, FeeAssetKind.Quote),
        MinProfitQuote = 0.05,
        MaxQtyCanon = double.PositiveInfinity,
        MaxBuyNotional = double.PositiveInfinity,
        HaircutPpm = VwapWalker.FullLiquidityPpm,
        MaxLevelsPerSide = 20,
    };

    [Fact]
    public void NearMiss_RejectThreshold_HasTobButNoFakeZeroVwap()
    {
        var ctx = new SpatialPairContext(_m.BinanceBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 1m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60010m, 1m) }, new[] { (60020m, 1m) }); // +1.67 bps TOB, fees 17.5
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, Params);

        Assert.Equal(RejectReason.RejectThreshold, q.Decision);
        Assert.Equal(60000, q.BuyTobPx, 1e-9);
        Assert.Equal(60010, q.SellTobPx, 1e-9);
        Assert.True(double.IsNaN(q.BuyVwap) && double.IsNaN(q.SellVwap), $"vwap {q.BuyVwap}/{q.SellVwap}"); // v0.2: 0 / 0
        Assert.True(double.IsNaN(q.BuyWorstPx) && double.IsNaN(q.SellWorstPx));
        Assert.True(double.IsNaN(q.Edge.GrossEdgeBps));
    }

    [Fact]
    public void StaleBook_RejectStale_HasNoFakeZeroPrices()
    {
        var ctx = new SpatialPairContext(_m.BinanceBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 1m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60300m, 1m) }, new[] { (60310m, 1m) }, health: BookHealth.Resyncing);
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, Params);

        Assert.Equal(RejectReason.RejectStale, q.Decision);
        Assert.True(double.IsNaN(q.BuyTobPx) && double.IsNaN(q.SellTobPx), $"tob {q.BuyTobPx}/{q.SellTobPx}"); // v0.2: 0 / 0
        Assert.True(double.IsNaN(q.BuyVwap));
    }
}
