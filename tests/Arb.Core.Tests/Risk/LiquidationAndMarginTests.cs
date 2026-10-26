using Arb.Core.Primitives;
using Arb.Core.Risk;

namespace Arb.Core.Tests.Risk;

/// <summary>
/// Short leg of a 1000PEPE funding position: 100,000,000 PEPE @ mark 0.000012 (notional 1200 USDT).
/// Expected values computed independently with 40-digit decimals (see docs/STAGE1_CORE.md).
/// </summary>
public class LiquidationAndMarginTests
{
    private const double CloseFee = 0.00055;
    private const double Mark = 0.000012;
    private const double Qty = 100_000_000;
    private static readonly MarginTier[] SingleTier = { new(double.PositiveInfinity, 0.01, 0) };
    private static readonly MarginTier[] Tiers = { new(1_000, 0.01, 0), new(5_000, 0.02, 10), new(double.PositiveInfinity, 0.05, 160) };
    private static readonly MarginThresholds Thresholds = new(targetBps: 3_500, warnBps: 2_500, reduceBps: 1_500, criticalBps: 800);

    private static PerpExposure Short(double equity, MarginMode mode = MarginMode.Cross) => new(Side.Sell, Qty, Mark, equity, mode);

    [Fact]
    public void Short_SingleTier_MatchesClosedForm()
    {
        LiquidationEstimate e = LiquidationCalculator.Estimate(Short(400), SingleTier, CloseFee);
        Assert.Equal(1.583296224828064e-5, e.LiquidationPx, 1e-17);
        Assert.Equal(3194.1352069005327, e.DistanceBps, 1e-6);
        Assert.True(e.CanLiquidate);

        // equity at the liquidation price equals maintenance margin there
        double eqAtLiq = 400 + (Qty * (Mark - e.LiquidationPx));
        double mmAtLiq = Qty * e.LiquidationPx * (0.01 + CloseFee);
        Assert.Equal(mmAtLiq, eqAtLiq, 1e-9);
    }

    [Fact]
    public void Long_SingleTier_AndFullyCollateralizedLongCannotLiquidate()
    {
        var longBtc = new PerpExposure(Side.Buy, 0.02, 60_000, 400, MarginMode.Cross);
        LiquidationEstimate e = LiquidationCalculator.Estimate(longBtc, new[] { new MarginTier(double.PositiveInfinity, 0.005, 0) }, CloseFee);
        Assert.Equal(40223.23897631857, e.LiquidationPx, 1e-8);
        Assert.Equal(3296.1268372802387, e.DistanceBps, 1e-6);

        var unlevered = new PerpExposure(Side.Buy, 0.02, 60_000, 1_300, MarginMode.Cross);
        LiquidationEstimate u = LiquidationCalculator.Estimate(unlevered, SingleTier, CloseFee);
        Assert.False(u.CanLiquidate);
        Assert.True(double.IsPositiveInfinity(u.DistanceBps));
    }

    [Fact]
    public void Tiers_UseBracketAtLiquidationNotional()
    {
        LiquidationEstimate e = LiquidationCalculator.Estimate(Short(400), Tiers, CloseFee);
        Assert.Equal(1, e.TierIndex);
        Assert.Equal(1.5775807162804370e-5, e.LiquidationPx, 1e-17);
        Assert.Equal(3146.5059690036418, e.DistanceBps, 1e-6);

        // notional at mark 900 is bracket 0, but at the liquidation price it is bracket 1 → must iterate
        var small = new PerpExposure(Side.Sell, 75_000_000, Mark, 300, MarginMode.Cross);
        LiquidationEstimate s = LiquidationCalculator.Estimate(small, Tiers, CloseFee);
        Assert.Equal(1, s.TierIndex);
        Assert.Equal(1.580846928943544e-5, s.LiquidationPx, 1e-17);
    }

    [Fact]
    public void CollateralToReach_RestoresTargetDistance()
    {
        double target = LiquidationCalculator.TargetPrice(Side.Sell, Mark, 4_000);
        Assert.Equal(0.0000168, target, 1e-18);
        double add = LiquidationCalculator.CollateralToReach(Short(400), target, Tiers, CloseFee);
        Assert.Equal(104.524, add, 1e-9);
        LiquidationEstimate after = LiquidationCalculator.Estimate(Short(400 + add), Tiers, CloseFee);
        Assert.Equal(4_000, after.DistanceBps, 1e-6);
        Assert.Equal(0, LiquidationCalculator.CollateralToReach(Short(10_000), target, Tiers, CloseFee));
    }

    [Fact]
    public void QtyToReach_CrossMargin_PartialReduceRestoresTarget()
    {
        double target = LiquidationCalculator.TargetPrice(Side.Sell, Mark, 4_000);
        double keep = LiquidationCalculator.QtyToReach(Short(400), target, Tiers, CloseFee, CloseFee);
        Assert.Equal(79_659_209.44063021, keep, 1e-4);

        // verify by simulation: reduce at mark paying the fee, re-estimate
        double reduced = Qty - keep;
        double equityAfter = 400 - (reduced * Mark * CloseFee);
        LiquidationEstimate after = LiquidationCalculator.Estimate(new PerpExposure(Side.Sell, keep, Mark, equityAfter, MarginMode.Cross), Tiers, CloseFee);
        Assert.Equal(4_000, after.DistanceBps, 1e-6);

        Assert.Equal(Qty, LiquidationCalculator.QtyToReach(Short(10_000), target, Tiers, CloseFee, CloseFee));
    }

    [Fact]
    public void MarginGuard_Healthy()
    {
        MarginDecision d = MarginGuard.Evaluate(Short(400), Tiers, CloseFee, CloseFee, Thresholds, 0);
        Assert.Equal(MarginZone.Healthy, d.Zone);
        Assert.Equal(MarginAction.None, d.Action);
    }

    [Fact]
    public void MarginGuard_WarnZone_AddsLocalMargin_OrRequestsCrossVenueRebalance()
    {
        // equity 300 → distance 2330 bps (Warn). Need 143.291 USDT to get back to 3500 bps.
        MarginDecision local = MarginGuard.Evaluate(Short(300), Tiers, CloseFee, CloseFee, Thresholds, freeLocalCollateralQuote: 500);
        Assert.Equal(MarginZone.Warn, local.Zone);
        Assert.Equal(2329.9528032270181, local.Estimate.DistanceBps, 1e-6);
        Assert.Equal(MarginAction.AddMargin, local.Action);
        Assert.Equal(143.291, local.AddMarginQuote, 1e-9);

        MarginDecision transfer = MarginGuard.Evaluate(Short(300), Tiers, CloseFee, CloseFee, Thresholds, freeLocalCollateralQuote: 50);
        Assert.Equal(MarginAction.RebalanceCollateral, transfer.Action);
        Assert.Equal(50, transfer.AddMarginQuote, 1e-12);
        Assert.Equal(93.291, transfer.TransferQuote, 1e-9);
        Assert.Equal(0, transfer.ReduceQtyCanon);
    }

    [Fact]
    public void MarginGuard_ReduceZone_Cross_PartialReducePair()
    {
        // equity 180 → 1350 bps (Reduce zone), no local collateral.
        MarginDecision d = MarginGuard.Evaluate(Short(180), Tiers, CloseFee, CloseFee, Thresholds, 0);
        Assert.Equal(MarginZone.Reduce, d.Zone);
        Assert.Equal(1350.0890042950697, d.Estimate.DistanceBps, 1e-6);
        Assert.Equal(MarginAction.ReducePair, d.Action);
        Assert.Equal(58_907_593.64023179, d.ReduceQtyCanon, 1e-4); // remaining size falls into the lower bracket
        Assert.Equal(263.291, d.TransferQuote, 1e-9);

        // with 50 USDT local top-up first, less needs to be reduced
        MarginDecision withLocal = MarginGuard.Evaluate(Short(180), Tiers, CloseFee, CloseFee, Thresholds, 50);
        Assert.Equal(MarginAction.ReducePair, withLocal.Action);
        Assert.Equal(50, withLocal.AddMarginQuote, 1e-12);
        Assert.Equal(47_451_028.914078056, withLocal.ReduceQtyCanon, 1e-4);
    }

    [Fact]
    public void MarginGuard_ReduceZone_Isolated_FlattensBecauseReducingDoesNotHelp()
    {
        MarginDecision d = MarginGuard.Evaluate(Short(180, MarginMode.Isolated), Tiers, CloseFee, CloseFee, Thresholds, 0);
        Assert.Equal(MarginZone.Reduce, d.Zone);
        Assert.Equal(MarginAction.FlattenPair, d.Action);
        Assert.Equal(Qty, d.ReduceQtyCanon);

        MarginDecision funded = MarginGuard.Evaluate(Short(180, MarginMode.Isolated), Tiers, CloseFee, CloseFee, Thresholds, 1_000);
        Assert.Equal(MarginAction.AddMargin, funded.Action);
        Assert.Equal(263.291, funded.AddMarginQuote, 1e-9);
    }

    [Fact]
    public void MarginGuard_Critical_Flattens()
    {
        MarginDecision d = MarginGuard.Evaluate(Short(100), Tiers, CloseFee, CloseFee, Thresholds, 10_000);
        Assert.Equal(MarginZone.Critical, d.Zone);
        Assert.Equal(696.8464716737707, d.Estimate.DistanceBps, 1e-6);
        Assert.Equal(MarginAction.FlattenPair, d.Action);
    }

    [Fact]
    public void Validation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarginThresholds(3_000, 3_500, 1_500, 800));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarginTier(0, 0.01, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LiquidationCalculator.Estimate(new PerpExposure(Side.Sell, 0, Mark, 1, MarginMode.Cross), Tiers, CloseFee));
        Assert.Throws<ArgumentException>(() => LiquidationCalculator.Estimate(Short(400), ReadOnlySpan<MarginTier>.Empty, CloseFee));
    }
}
