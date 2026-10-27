using Arb.Core.Execution;
using Arb.Core.Primitives;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-02 (S1), state-machine half: a SPATIAL residual hedged on a perp must have a live, non-terminal state that owns the
/// spot + perp legs until both are unwound (REPAIRING → POSITION_OPEN → EXITING → CLOSED). Uses only the pre-existing
/// state-machine API, so it is a behavioural red on v0.2.
/// </summary>
public class R02_SpatialHedgeStateTests
{
    private static TradeStateRecord RepairingSpatial()
    {
        TradeStateRecord r = TradeStateRecord.Create(0);
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.InFlight, StrategyKind.Spatial, 1));
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.Residual, StrategyKind.Spatial, 2));
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.Repairing, StrategyKind.Spatial, 3));
        return r;
    }

    [Fact]
    public void Spatial_Repairing_To_PositionOpen_IsAllowed_ForAPerpHedge()
    {
        TradeStateRecord r = RepairingSpatial();
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.PositionOpen, StrategyKind.Spatial, 4));
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.Exiting, StrategyKind.Spatial, 5));
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.Closed, StrategyKind.Spatial, 6));
        Assert.True(r.State.IsTerminal());
    }

    [Fact]
    public void Spatial_PositionOpen_IsOnlyReachableFromRepairing()
    {
        TradeStateRecord r = TradeStateRecord.Create(0);
        TradeStateMachine.TryTransition(ref r, TradeState.InFlight, StrategyKind.Spatial, 1);
        Assert.Equal(TransitionError.StrategyNotAllowed, TradeStateMachine.TryTransition(ref r, TradeState.PositionOpen, StrategyKind.Spatial, 2));
        Assert.Equal(TradeState.InFlight, r.State);
    }
}
