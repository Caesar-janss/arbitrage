namespace Arb.Core.Primitives;

/// <summary>
/// Outcome of evaluating one opportunity. 1:1 with the CHECK constraint on trd.opportunity.decision
/// (a unit test parses the SQL migration and asserts the two lists are identical).
/// </summary>
public enum RejectReason : byte
{
    Execute = 0,
    RejectThreshold = 1,
    RejectStale = 2,
    RejectInventory = 3,
    RejectRiskHalt = 4,
    RejectSanity = 5,
    RejectCooldown = 6,
    RejectMinNotional = 7,
}

public static class RejectReasonExtensions
{
    public static string ToDbCode(this RejectReason reason) => reason switch
    {
        RejectReason.Execute => "EXECUTE",
        RejectReason.RejectThreshold => "REJECT_THRESHOLD",
        RejectReason.RejectStale => "REJECT_STALE",
        RejectReason.RejectInventory => "REJECT_INVENTORY",
        RejectReason.RejectRiskHalt => "REJECT_RISK_HALT",
        RejectReason.RejectSanity => "REJECT_SANITY",
        RejectReason.RejectCooldown => "REJECT_COOLDOWN",
        RejectReason.RejectMinNotional => "REJECT_MIN_NOTIONAL",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}
