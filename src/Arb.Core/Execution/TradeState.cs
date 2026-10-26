namespace Arb.Core.Execution;

/// <summary>Two-legged trade lifecycle. 1:1 with trd.trade_state (test parses the SQL and compares).</summary>
public enum TradeState : byte
{
    Created = 1,
    InFlight = 2,
    Completed = 3,
    Missed = 4,
    Residual = 5,
    Repairing = 6,
    Repaired = 7,
    PositionOpen = 8,
    Exiting = 9,
    Closed = 10,
    Stuck = 11,
    ManualResolved = 12,
}

public static class TradeStateInfo
{
    public const int Count = 12;

    public static bool IsTerminal(this TradeState state) =>
        state is TradeState.Completed or TradeState.Missed or TradeState.Repaired or TradeState.Closed or TradeState.ManualResolved;

    public static string ToDbCode(this TradeState state) => state switch
    {
        TradeState.Created => "CREATED",
        TradeState.InFlight => "IN_FLIGHT",
        TradeState.Completed => "COMPLETED",
        TradeState.Missed => "MISSED",
        TradeState.Residual => "RESIDUAL",
        TradeState.Repairing => "REPAIRING",
        TradeState.Repaired => "REPAIRED",
        TradeState.PositionOpen => "POSITION_OPEN",
        TradeState.Exiting => "EXITING",
        TradeState.Closed => "CLOSED",
        TradeState.Stuck => "STUCK",
        TradeState.ManualResolved => "MANUAL_RESOLVED",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static bool TryParseDbCode(string code, out TradeState state)
    {
        for (int i = 1; i <= Count; i++)
        {
            var s = (TradeState)i;
            if (string.Equals(s.ToDbCode(), code, StringComparison.Ordinal))
            {
                state = s;
                return true;
            }
        }

        state = default;
        return false;
    }
}

/// <summary>
/// State + optimistic version. Version increments by exactly 1 per transition — the persistence writer
/// sends it as trd.trade_group.state_version, and the DB trigger rejects gaps/stale writes.
/// </summary>
public struct TradeStateRecord
{
    public TradeState State;
    public int Version;
    public long ChangedAtMicros;

    public static TradeStateRecord Create(long atMicros) => new()
    {
        State = TradeState.Created,
        Version = 0,
        ChangedAtMicros = atMicros,
    };
}
