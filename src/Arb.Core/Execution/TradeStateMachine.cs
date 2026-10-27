using Arb.Core.Primitives;

namespace Arb.Core.Execution;

public enum TransitionError : byte
{
    None = 0,
    IllegalTransition = 1,
    StrategyNotAllowed = 2,
    TimeWentBackwards = 3,
    UninitializedState = 4,

    /// <summary>Terminal state requested while a perp position (entry leg or hedge) is still open (TradeGroup).</summary>
    OpenDerivativePosition = 5,

    /// <summary>SPATIAL → POSITION_OPEN requested without an open perp hedge (TradeGroup).</summary>
    NotHedged = 6,

    /// <summary>Terminal state requested while net delta ≠ the residual explicitly accepted as dust (TradeGroup, R-14).</summary>
    UnacceptedResidual = 7,
}

/// <summary>
/// Authoritative trade state machine (in memory). The SAME table lives in trd.trade_state_transition and the
/// DB trigger enforces it as a second line of defence; a unit test asserts both tables are identical.
///
///  CREATED → IN_FLIGHT → COMPLETED                         (spatial, both legs flat)
///          ↘ MISSED     ↘ MISSED                            (nothing filled)
///                       ↘ POSITION_OPEN ⇄ EXITING → CLOSED  (funding; EXITING → POSITION_OPEN = partial reduce)
///                       ↘ RESIDUAL → REPAIRING → REPAIRED / POSITION_OPEN / CLOSED
///                       ↘ STUCK ←──────────── (repair failed) → REPAIRING | MANUAL_RESOLVED
///   POSITION_OPEN → RESIDUAL (a leg liquidated / ADL'd) | STUCK (venue unreachable while holding)
///
/// Strategy rules (also in the DB trigger):
///   funding  : never COMPLETED (it ends CLOSED, or REPAIRED when an entry legging is unwound).
///   spatial  : POSITION_OPEN only from REPAIRING = a residual hedged on a perp (R-02). The hedged spot + perp legs
///              are then owned by the trade until EXITING → CLOSED; a perp hedge never ends REPAIRED.
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
        (TradeState.PositionOpen, TradeState.Residual), // R-10: one leg liquidated / ADL'd while holding
        (TradeState.PositionOpen, TradeState.Stuck),    // R-10: venue unreachable / reconcile failed while holding
        (TradeState.Exiting, TradeState.Closed),
        (TradeState.Exiting, TradeState.PositionOpen),  // R-10: partial reduce done, the rest stays open
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
    public static bool IsAllowedFor(StrategyKind strategy, TradeState from, TradeState to)
    {
        if (strategy == StrategyKind.Spatial)
        {
            // a spatial trade holds a position only as a perp-hedged residual (REPAIRING → POSITION_OPEN)
            return to != TradeState.PositionOpen || from == TradeState.Repairing;
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

        if (!IsAllowedFor(strategy, record.State, to))
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
