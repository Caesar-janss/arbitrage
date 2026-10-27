using Arb.Core.Execution;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-10 (S2): the transition table (identical in C# and SQL) could not express what the risk code produces while a
/// position is held: one leg liquidated / ADL'd (POSITION_OPEN → RESIDUAL), venue unreachable (POSITION_OPEN → STUCK),
/// and a MarginGuard partial reduce after which the position stays open (EXITING → POSITION_OPEN).
/// Scenario P06 + funding perp-perp 1000PEPE legs (Bybit long, Binance short).
/// </summary>
public class R10_PositionLifecycleTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    [Theory]
    [InlineData(TradeState.PositionOpen, TradeState.Residual)]
    [InlineData(TradeState.PositionOpen, TradeState.Stuck)]
    [InlineData(TradeState.Exiting, TradeState.PositionOpen)]
    public void TransitionExists(TradeState from, TradeState to) =>
        Assert.True(TradeStateMachine.IsAllowed(from, to), $"{from} -> {to} must be legal");

    [Fact]
    public void ShortLegLiquidated_WhileHolding_BecomesResidual_ThenRepairedBackToPosition()
    {
        TradeStateRecord r = Open();
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.Residual, StrategyKind.FundingPerpPerp, 10));
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.Repairing, StrategyKind.FundingPerpPerp, 11));
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.PositionOpen, StrategyKind.FundingPerpPerp, 12));
        Assert.False(r.State.IsTerminal());
    }

    [Fact]
    public void PartialReduce_ReturnsToPositionOpen_AndStaysNonTerminal()
    {
        TradeStateRecord r = Open();
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.Exiting, StrategyKind.FundingPerpPerp, 10));
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.PositionOpen, StrategyKind.FundingPerpPerp, 11));
        Assert.Equal(TradeState.PositionOpen, r.State);
        Assert.False(r.State.IsTerminal());
    }

    [Fact]
    public void VenueUnreachable_WhileHolding_IsStuck_NotSilentlyOpen()
    {
        TradeStateRecord r = Open();
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.Stuck, StrategyKind.FundingSpotPerp, 10));
        Assert.Equal(TransitionError.None, TradeStateMachine.TryTransition(ref r, TradeState.Repairing, StrategyKind.FundingSpotPerp, 11));
    }

    [Fact]
    public void TradeGroup_Liquidation_And_PartialReduce_EndToEnd()
    {
        var g = new TradeGroup();
        g.Initialize(77, StrategyKind.FundingPerpPerp, _m.BybitPepePerp.Key, _m.BinancePepePerp.Key, 100_000_000m, 0);
        g.TransitionTo(TradeState.InFlight, 1);
        g.ApplyFill(_m.BybitPepePerp.Key, Side.Buy, 100_000_000m, 1200m, 0.66m, FeeAssetKind.Quote, 0.66m);
        g.ApplyFill(_m.BinancePepePerp.Key, Side.Sell, 100_000_000m, 1200.6m, 0.54m, FeeAssetKind.DiscountToken, 0.54m);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 2));

        // MarginGuard ReducePair 40 %: both legs reduced by the same canonical qty → still open, still delta-neutral
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Exiting, 3));
        g.ApplyFill(_m.BybitPepePerp.Key, Side.Sell, 40_000_000m, 482m, 0.27m, FeeAssetKind.Quote, 0.27m);
        g.ApplyFill(_m.BinancePepePerp.Key, Side.Buy, 40_000_000m, 482.2m, 0.22m, FeeAssetKind.DiscountToken, 0.22m);
        Assert.True(g.IsDeltaNeutral(0m));
        Assert.True(g.HasOpenPerpPosition);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 4));
        Assert.Equal(60_000_000m, g.PerpPositionCanon(_m.BybitPepePerp.Key));

        // the venue liquidates the remaining short (forced BUY on the short leg) → naked long = residual
        g.ApplyFill(_m.BinancePepePerp.Key, Side.Buy, 60_000_000m, 780m, 0.4m, FeeAssetKind.Quote, 0.4m);
        Assert.Equal(60_000_000m, g.NetBaseDelta);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Residual, 5));
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Repairing, 6));
        g.ApplyFill(_m.BybitPepePerp.Key, Side.Sell, 60_000_000m, 778m, 0.43m, FeeAssetKind.Quote, 0.43m);
        Assert.True(g.IsFlat(0m));
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Closed, 7));
    }

    private static TradeStateRecord Open()
    {
        TradeStateRecord r = TradeStateRecord.Create(0);
        TradeStateMachine.TryTransition(ref r, TradeState.InFlight, StrategyKind.FundingPerpPerp, 1);
        TradeStateMachine.TryTransition(ref r, TradeState.PositionOpen, StrategyKind.FundingPerpPerp, 2);
        Assert.Equal(TradeState.PositionOpen, r.State);
        return r;
    }
}
