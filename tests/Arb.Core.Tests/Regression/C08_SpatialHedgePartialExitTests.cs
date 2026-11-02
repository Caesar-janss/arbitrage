using Arb.Core.Execution;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// C-08 (S2, ChatGPT review of 4bf87f7; pre-existing API). A perp-hedged spatial residual (spot +0.01 / perp −0.01,
/// R-02) that unwinds PART of both legs must return to POSITION_OPEN: the rest is still hedged and still owned by the
/// trade. v0.3 accepted EXITING → POSITION_OPEN in the transition table (R-10) but the spatial rule only allowed
/// POSITION_OPEN from REPAIRING → StrategyNotAllowed (SQL: AR002), leaving no legal state for a partially unwound hedge.
/// The entry rule stays: a spatial trade never goes IN_FLIGHT → POSITION_OPEN, and never holds POSITION_OPEN unhedged.
/// </summary>
public class C08_SpatialHedgePartialExitTests
{
    private static readonly RepairPolicy Policy = new() { MaxStopLossBps = 30, SevereLossBps = 100, LadderBps = new double[] { 10, 30, 80 }, DustCanon = 0.000001 };
    private readonly TestMarkets _m = TestMarkets.Create();

    /// <summary>Same fixture as R02_PerpHedgeLifecycleTests: buy filled, sell missed, residual hedged on the Bybit perp, then held.</summary>
    private TradeGroup HeldHedge()
    {
        var hedge = new RepairCandidate
        {
            Method = RepairMethod.PerpHedge,
            Spec = _m.BybitBtcPerp,
            Available = true,
            FeeRate = 0.00055,
            ExtraCostBps = 12,
            Book = TestMarkets.View(_m.BybitBtcPerp, new[] { (59990.0m, 0.5m) }, new[] { (59990.1m, 0.5m) }),
        };
        var g = new TradeGroup();
        g.Initialize(44, StrategyKind.Spatial, _m.BitgetBtcSpot.Key, _m.BybitBtcSpot.Key, 0.01m, 0);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.InFlight, 1));
        g.ApplyFill(_m.BitgetBtcSpot.Key, Side.Buy, 0.01m, 600m, 0m, FeeAssetKind.DiscountToken, 0.48m);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Residual, 2));
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Repairing, 3));
        RepairPlan p = RepairPlanner.Plan(g.NetBaseDelta, 60000, new[] { hedge }, Policy, 0, 7);
        g.ApplyFill(p.Order.Instrument, Side.Sell, _m.BybitBtcPerp.CanonicalQtyExact(p.Order.Lots), 599.9m, 0.33m, FeeAssetKind.Quote, 0.33m);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 4));
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Exiting, 5));
        return g;
    }

    [Fact]
    public void PartialUnwindOfBothLegs_ReturnsToPositionOpen()
    {
        TradeGroup g = HeldHedge();
        g.ApplyFill(_m.BitgetBtcSpot.Key, Side.Sell, 0.004m, 240.08m, 0.19m, FeeAssetKind.Quote, 0.19m);
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Buy, 0.004m, 239.98m, 0.13m, FeeAssetKind.Quote, 0.13m);
        Assert.Equal(0m, g.NetBaseDelta);
        Assert.Equal(-0.006m, g.PerpPositionCanon(_m.BybitBtcPerp.Key));

        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 6)); // v0.3: StrategyNotAllowed
        Assert.Equal(TradeState.PositionOpen, g.State.State);
        Assert.True(g.HasOpenPerpPosition);
    }

    [Fact]
    public void AfterAPartialUnwind_TheRestUnwindsToClosed()
    {
        TradeGroup g = HeldHedge();
        g.ApplyFill(_m.BitgetBtcSpot.Key, Side.Sell, 0.004m, 240.08m, 0.19m, FeeAssetKind.Quote, 0.19m);
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Buy, 0.004m, 239.98m, 0.13m, FeeAssetKind.Quote, 0.13m);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 6)); // v0.3: StrategyNotAllowed
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Exiting, 7));
        g.ApplyFill(_m.BitgetBtcSpot.Key, Side.Sell, 0.006m, 360.12m, 0.29m, FeeAssetKind.Quote, 0.29m);
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Buy, 0.006m, 359.97m, 0.2m, FeeAssetKind.Quote, 0.2m);
        Assert.True(g.IsFlat(0m));
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Closed, 8));
    }

    [Fact]
    public void StrategyRule_AllowsExitingToPositionOpen_ForSpatial()
    {
        Assert.True(TradeStateMachine.IsAllowedFor(StrategyKind.Spatial, TradeState.Exiting, TradeState.PositionOpen)); // v0.3: false
        Assert.True(TradeStateMachine.IsAllowedFor(StrategyKind.Spatial, TradeState.Repairing, TradeState.PositionOpen));
        Assert.False(TradeStateMachine.IsAllowedFor(StrategyKind.Spatial, TradeState.InFlight, TradeState.PositionOpen));
    }

    [Fact]
    public void PerpFullyBoughtBack_SpotLeft_IsNotHedged_AndMustGoResidual()
    {
        TradeGroup g = HeldHedge();
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Buy, 0.01m, 599.95m, 0.33m, FeeAssetKind.Quote, 0.33m); // only the perp leg filled
        Assert.False(g.HasOpenPerpPosition);
        Assert.Equal(TransitionError.NotHedged, g.TransitionTo(TradeState.PositionOpen, 6));
        Assert.Equal(TradeState.Exiting, g.State.State);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Residual, 6));
    }

    [Fact]
    public void SpatialEntry_StillNeverOpensAPosition()
    {
        TradeStateRecord r = TradeStateRecord.Create(0);
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.InFlight, StrategyKind.Spatial, 1));
        Assert.Equal(TransitionError.StrategyNotAllowed, TradeStateMachine.TryTransition(ref r, TradeState.PositionOpen, StrategyKind.Spatial, 2));
    }
}
