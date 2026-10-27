using Arb.Core.Primitives;

namespace Arb.Core.Execution;

public enum TransitionError : byte
{
    None = 0,
    IllegalTransition = 1,
    StrategyNotAllowed = 2,
    TimeWentBackwards = 3,
    UninitializedState = 4,
}

/// <summary>
/// Authoritative trade state machine (in memory). The SAME table lives in trd.trade_state_transition and the
/// DB trigger enforces it as a second line of defence; a unit test asserts both tables are identical.
///
///  CREATED → IN_FLIGHT → COMPLETED                         (spatial, both legs flat)
///          ↘ MISSED     ↘ MISSED                            (nothing filled)
///                       ↘ POSITION_OPEN → EXITING → CLOSED  (funding)
///                       ↘ RESIDUAL → REPAIRING → REPAIRED / POSITION_OPEN / CLOSED
///                       ↘ STUCK ←──────────── (repair failed) → REPAIRING | MANUAL_RESOLVED
/// </summary>
public static class TradeStateMachine
{
    private static readonly (TradeState From, TradeState To)[] TransitionList =
    {
        (TradeState.Created, TradeState.InFlight),
        (TradeState.Created, TradeState.Missed),
        (TradeState.InFlight, TradeState.Completed),
        (TradeState.InFlight, TradeState.Missed),
        (TradeState.InFlight, TradeState.Residual),
        (TradeState.InFlight, TradeState.PositionOpen),
        (TradeState.InFlight, TradeState.Stuck),
        (TradeState.Residual, TradeState.Repairing),
        (TradeState.Residual, TradeState.Stuck),
        (TradeState.Repairing, TradeState.Repaired),
        (TradeState.Repairing, TradeState.PositionOpen),
        (TradeState.Repairing, TradeState.Closed),
        (TradeState.Repairing, TradeState.Residual),
        (TradeState.Repairing, TradeState.Stuck),
        (TradeState.PositionOpen, TradeState.Exiting),
        (TradeState.Exiting, TradeState.Closed),
        (TradeState.Exiting, TradeState.Residual),
        (TradeState.Exiting, TradeState.Stuck),
        (TradeState.Stuck, TradeState.Repairing),
        (TradeState.Stuck, TradeState.ManualResolved),
    };

    private const int Dim = TradeStateInfo.Count + 1;
    private static readonly bool[] Allowed = BuildTable();

    public static ReadOnlySpan<(TradeState From, TradeState To)> Transitions => TransitionList;

    public static bool IsAllowed(TradeState from, TradeState to)
    {
        int f = (int)from;
        int t = (int)to;
        return (uint)(f - 1) < TradeStateInfo.Count && (uint)(t - 1) < TradeStateInfo.Count && Allowed[(f * Dim) + t];
    }

    /// <summary>Strategy constraints (also enforced by the DB trigger).</summary>
    public static bool IsAllowedFor(StrategyKind strategy, TradeState to)
    {
        if (strategy == StrategyKind.Spatial)
        {
            return to is not (TradeState.PositionOpen or TradeState.Exiting or TradeState.Closed);
        }

        return to != TradeState.Completed;
    }

    public static TransitionError TryTransition(ref TradeStateRecord record, TradeState to, StrategyKind strategy, long atMicros)
    {
        if (record.State == 0)
        {
            return TransitionError.UninitializedState;
        }

        if (!IsAllowed(record.State, to))
        {
            return TransitionError.IllegalTransition;
        }

        if (!IsAllowedFor(strategy, to))
        {
            return TransitionError.StrategyNotAllowed;
        }

        if (atMicros < record.ChangedAtMicros)
        {
            return TransitionError.TimeWentBackwards;
        }

        record.State = to;
        record.Version++;
        record.ChangedAtMicros = atMicros;
        return TransitionError.None;
    }

    private static bool[] BuildTable()
    {
        var table = new bool[Dim * Dim];
        foreach ((TradeState from, TradeState to) in TransitionList)
        {
            table[((int)from * Dim) + (int)to] = true;
        }

        return table;
    }
}
