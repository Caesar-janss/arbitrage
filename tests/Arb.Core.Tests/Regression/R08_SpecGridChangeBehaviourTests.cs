using Arb.Core.Instruments;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-08 (S2), behaviour on the pre-existing API: InstrumentRegistryBuilder.Replace changes tick/step under the SAME
/// key while live books still hold ticks/lots parsed with the OLD grid. Scenario P19: Bybit BTC spot step 1e-6 → 1e-5;
/// a level of 10,000 lots (0.010 BTC real) is read as 0.1 BTC → 10× phantom depth, orders sized 10× too large.
/// A book parsed under another grid must never be priced.
/// </summary>
public class R08_SpecGridChangeBehaviourTests
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
    public void StepChange_BookParsedWithOldStep_IsNotPriced()
    {
        InstrumentRegistryBuilder b = InstrumentRegistryBuilder.From(_m.Registry);
        b.Replace(_m.BybitBtcSpot.Key, 0.01m, 0.00001m, 0m, 5m, InstrumentStatus.Trading);
        InstrumentSpec newSpec = b.Build()[_m.BybitBtcSpot.Key];

        PriceLevel oldLevel = TestMarkets.Level(_m.BybitBtcSpot, 60150m, 0.010m); // 10_000 lots of 1e-6
        var sellView = new BookView(8);
        sellView.Load(newSpec.Key, new[] { oldLevel }, new[] { new PriceLevel(6_016_000, 100_000) }, new BookStamp(1, 1, 1, 1));
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 1m) });

        SpatialQuote q = SpatialArbPricer.Evaluate(new SpatialPairContext(_m.BinanceBtcSpot, newSpec), buy, sellView, Params);
        Assert.Equal(RejectReason.RejectStale, q.Decision); // v0.2: Execute, buy 0.1 BTC against 0.010 BTC real depth
    }

    [Fact]
    public void TickChange_BookParsedWithOldTick_IsNotPriced()
    {
        InstrumentRegistryBuilder b = InstrumentRegistryBuilder.From(_m.Registry);
        b.Replace(_m.BybitBtcSpot.Key, 0.1m, 0.000001m, 0m, 5m, InstrumentStatus.Trading);
        InstrumentSpec newSpec = b.Build()[_m.BybitBtcSpot.Key];

        BookView sellOld = TestMarkets.View(_m.BybitBtcSpot, new[] { (60150m, 0.010m) }, new[] { (60160m, 1m) }); // ticks of 0.01
        var sellView = new BookView(8);
        sellView.Load(newSpec.Key, sellOld.Bids, sellOld.Asks, sellOld.Stamp);
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 1m) });

        SpatialQuote q = SpatialArbPricer.Evaluate(new SpatialPairContext(_m.BinanceBtcSpot, newSpec), buy, sellView, Params);
        Assert.Equal(RejectReason.RejectStale, q.Decision); // v0.2: bid read as 601,500 → huge fake edge
    }

    [Fact]
    public void StatusOnlyChange_KeepsBooksUsable()
    {
        InstrumentRegistryBuilder b = InstrumentRegistryBuilder.From(_m.Registry);
        b.Replace(_m.BybitBtcSpot.Key, 0.01m, 0.000001m, 0m, 10m, InstrumentStatus.Trading); // min notional only
        InstrumentSpec newSpec = b.Build()[_m.BybitBtcSpot.Key];
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60150m, 0.010m) }, new[] { (60160m, 1m) });
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 1m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(new SpatialPairContext(_m.BinanceBtcSpot, newSpec), buy, sell, Params);
        Assert.Equal(RejectReason.Execute, q.Decision);
    }
}
