using Arb.Core.Execution;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-13 (S3): a repair BUY on a spot venue that takes its fee from the RECEIVED base (Bitget without BGB: 10 bps in
/// BTC) must be grossed up like the entry leg, otherwise it under-delivers by lots × fee and leaves dust below the
/// venue minimum that stays unhedged (P09: −0.01 BTC → buy 0.010000, receive 0.009990, −0.00001 BTC left, next plan
/// BelowVenueMinimum).
/// </summary>
public class R13_RepairBaseFeeGrossUpTests
{
    private static readonly RepairPolicy Policy = new() { MaxStopLossBps = 30, SevereLossBps = 100, LadderBps = new double[] { 10, 30, 80 }, DustCanon = 0.000001 };
    private readonly TestMarkets _m = TestMarkets.Create();

    private RepairCandidate BuyOnBitget(FeeAssetKind feeAsset) => new()
    {
        Method = RepairMethod.CompleteBuyLeg,
        Spec = _m.BitgetBtcSpot,
        Available = true,
        FeeRate = 0.001,
        FeeAsset = feeAsset,
        Book = TestMarkets.View(_m.BitgetBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 1m) }),
    };

    [Fact]
    public void BuyRepair_WithFeeInBase_IsGrossedUp_AndFlattensInOneOrder()
    {
        RepairPlan p = RepairPlanner.Plan(-0.01m, 60150, new[] { BuyOnBitget(FeeAssetKind.Base) }, Policy, 0, 1);
        Assert.Equal(RepairVerdict.Planned, p.Verdict);
        decimal bought = _m.BitgetBtcSpot.CanonicalQtyExact(p.Order.Lots);
        decimal received = bought * (1m - 0.001m);
        Assert.Equal(0.010011m, bought);           // 0.01 / 0.999 rounded UP to the 1e-6 lot
        Assert.True(received >= 0.01m, $"received {received} < 0.01");
        Assert.True(received - 0.01m < 0.000001m); // over-delivery below one lot
        Assert.Equal(0, p.UntradeableCanon);
    }

    [Fact]
    public void BuyRepair_CostModel_ChargesThePerReceivedUnitPrice()
    {
        RepairPlan p = RepairPlanner.Plan(-0.01m, 60000, new[] { BuyOnBitget(FeeAssetKind.Base) }, Policy, 0, 1);
        Assert.Equal((60000 / 0.999 - 60000) / 60000 * 1e4, p.ExpectedCostBps, 1e-9); // 10.01 bps, not 10.0
    }

    [Fact]
    public void QuoteFee_IsUnchanged()
    {
        RepairPlan p = RepairPlanner.Plan(-0.01m, 60150, new[] { BuyOnBitget(FeeAssetKind.Quote) }, Policy, 0, 1);
        Assert.Equal(10_000L, p.Order.Lots);
    }
}
