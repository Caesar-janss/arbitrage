using Arb.Core.Execution;
using Arb.Core.Instruments;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>R-08 (S2), mechanism: a grid generation on the spec, stamped into the book at snapshot time.</summary>
public class R08_GridGenerationTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    [Fact]
    public void Replace_BumpsGeneration_OnlyWhenTickOrStepChanges()
    {
        Assert.Equal(0, _m.BybitBtcSpot.GridGeneration);
        InstrumentRegistryBuilder b = InstrumentRegistryBuilder.From(_m.Registry);
        b.Replace(_m.BybitBtcSpot.Key, 0.01m, 0.000001m, 0m, 10m, InstrumentStatus.Halted);
        InstrumentRegistry r1 = b.Build();
        Assert.Equal(0, r1[_m.BybitBtcSpot.Key].GridGeneration);

        InstrumentRegistryBuilder b2 = InstrumentRegistryBuilder.From(r1);
        b2.Replace(_m.BybitBtcSpot.Key, 0.01m, 0.00001m, 0m, 10m, InstrumentStatus.Trading);
        InstrumentRegistry r2 = b2.Build();
        Assert.Equal(1, r2[_m.BybitBtcSpot.Key].GridGeneration);
        Assert.Equal(0, r2[_m.BinanceBtcSpot.Key].GridGeneration);
    }

    [Fact]
    public void Resnapshot_WithNewGeneration_IsPricedAgain()
    {
        InstrumentRegistryBuilder b = InstrumentRegistryBuilder.From(_m.Registry);
        b.Replace(_m.BybitBtcSpot.Key, 0.01m, 0.00001m, 0m, 5m, InstrumentStatus.Trading);
        InstrumentSpec newSpec = b.Build()[_m.BybitBtcSpot.Key];

        var book = new OrderBookL2(newSpec.Key, 16);
        book.ApplySnapshot(new[] { TestMarkets.Level(newSpec, 60150m, 0.01m) }, new[] { TestMarkets.Level(newSpec, 60160m, 1m) }, new BookStamp(1, 1, 1, 1), newSpec.GridGeneration);
        var view = new BookView(8);
        Assert.True(book.TryCopyTo(view, 8));
        Assert.Equal(1, view.GridGeneration);

        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 1m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(new SpatialPairContext(_m.BinanceBtcSpot, newSpec), buy, view, new SpatialParams
        {
            BuyFee = new(0.00075, FeeAssetKind.DiscountToken),
            SellFee = new(0.001, FeeAssetKind.Quote),
            MinProfitQuote = 0.05,
            MaxQtyCanon = double.PositiveInfinity,
            MaxBuyNotional = double.PositiveInfinity,
            HaircutPpm = VwapWalker.FullLiquidityPpm,
            MaxLevelsPerSide = 20,
        });
        Assert.Equal(RejectReason.Execute, q.Decision);
        Assert.Equal(1_000L, q.SellLots); // 0.01 BTC = 1_000 lots of the NEW step 1e-5
    }

    [Fact]
    public void RepairPlanner_SkipsACandidateWhoseBookIsFromAnotherGrid()
    {
        InstrumentRegistryBuilder b = InstrumentRegistryBuilder.From(_m.Registry);
        b.Replace(_m.BybitBtcSpot.Key, 0.01m, 0.00001m, 0m, 5m, InstrumentStatus.Trading);
        InstrumentSpec newSpec = b.Build()[_m.BybitBtcSpot.Key];
        BookView oldBook = TestMarkets.View(_m.BybitBtcSpot, new[] { (60050m, 0.02m) }, new[] { (60060m, 1m) }); // generation 0
        var c = new RepairCandidate { Method = RepairMethod.CompleteSellLeg, Spec = newSpec, Book = oldBook, FeeRate = 0.001, Available = true };
        var policy = new RepairPolicy { MaxStopLossBps = 30, SevereLossBps = 100, LadderBps = new double[] { 10 }, DustCanon = 0.000001 };
        Assert.Equal(RepairVerdict.NoViableVenue, RepairPlanner.Plan(0.01m, 60000, new[] { c }, policy, 0, 1).Verdict);
    }
}
