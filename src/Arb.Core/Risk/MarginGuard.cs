namespace Arb.Core.Risk;

public enum MarginZone : byte
{
    Healthy = 0,
    Warn = 1,
    Reduce = 2,
    Critical = 3,
}

public enum MarginAction : byte
{
    None = 0,

    /// <summary>Move collateral from another wallet on the SAME venue (seconds).</summary>
    AddMargin = 1,

    /// <summary>Not enough local collateral: start a cross-venue USDT transfer (minutes) — the hedge leg is in profit.</summary>
    RebalanceCollateral = 2,

    /// <summary>Reduce BOTH legs by the same canonical qty (stays delta-neutral).</summary>
    ReducePair = 3,

    /// <summary>Close both legs.</summary>
    FlattenPair = 4,
}

public readonly struct MarginThresholds
{
    public MarginThresholds(double targetBps, double warnBps, double reduceBps, double criticalBps)
    {
        if (!(targetBps > warnBps && warnBps > reduceBps && reduceBps > criticalBps && criticalBps > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(targetBps), "target > warn > reduce > critical > 0");
        }

        TargetBps = targetBps;
        WarnBps = warnBps;
        ReduceBps = reduceBps;
        CriticalBps = criticalBps;
    }

    /// <summary>Distance restored by any corrective action.</summary>
    public double TargetBps { get; }

    public double WarnBps { get; }

    public double ReduceBps { get; }

    public double CriticalBps { get; }

    public static MarginThresholds From(RiskLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return new MarginThresholds(limits.MarginTargetBps, limits.MarginWarnBps, limits.MarginReduceBps, limits.MarginCriticalBps);
    }
}

public readonly struct MarginDecision
{
    public MarginAction Action { get; init; }

    public MarginZone Zone { get; init; }

    public LiquidationEstimate Estimate { get; init; }

    /// <summary>Collateral needed at current size to restore TargetBps.</summary>
    public double CollateralNeededQuote { get; init; }

    public double AddMarginQuote { get; init; }

    /// <summary>Cross-venue transfer to request (in parallel with any reduce).</summary>
    public double TransferQuote { get; init; }

    /// <summary>Canonical qty to reduce on BOTH legs (FlattenPair: whole position).</summary>
    public double ReduceQtyCanon { get; init; }
}

/// <summary>
/// Distance-to-liquidation controller for funding-arb perp legs (typically the SHORT leg when price spikes;
/// in perp-perp also the long leg on a dump). Zones by distance d:
///   d &gt; Warn                 → None
///   Reduce &lt; d ≤ Warn        → AddMargin if local collateral covers it, else RebalanceCollateral (start early)
///   Critical &lt; d ≤ Reduce    → AddMargin if covered; else Cross: ReducePair to restore Target (+ transfer);
///                              Isolated: FlattenPair (reducing an isolated position does not move its liq price)
///   d ≤ Critical             → FlattenPair
/// Funding arb should run CROSS margin: it is the only mode where partial reduce actually buys distance.
/// </summary>
public static class MarginGuard
{
    public static MarginDecision Evaluate(
        in PerpExposure x,
        ReadOnlySpan<MarginTier> tiers,
        double closeFeeRate,
        double reduceFeeRate,
        in MarginThresholds t,
        double freeLocalCollateralQuote)
    {
        LiquidationEstimate est = LiquidationCalculator.Estimate(x, tiers, closeFeeRate);
        double d = est.DistanceBps;
        if (d > t.WarnBps)
        {
            return new MarginDecision { Action = MarginAction.None, Zone = MarginZone.Healthy, Estimate = est };
        }

        if (d <= t.CriticalBps)
        {
            return new MarginDecision
            {
                Action = MarginAction.FlattenPair,
                Zone = MarginZone.Critical,
                Estimate = est,
                ReduceQtyCanon = x.QtyCanon,
            };
        }

        MarginZone zone = d <= t.ReduceBps ? MarginZone.Reduce : MarginZone.Warn;
        double target = LiquidationCalculator.TargetPrice(x.Side, x.MarkPx, t.TargetBps);
        double need = LiquidationCalculator.CollateralToReach(x, target, tiers, closeFeeRate);
        double local = Math.Min(need, Math.Max(0, freeLocalCollateralQuote));

        if (local >= need * (1 - 1e-12))
        {
            return new MarginDecision
            {
                Action = MarginAction.AddMargin,
                Zone = zone,
                Estimate = est,
                CollateralNeededQuote = need,
                AddMarginQuote = need,
            };
        }

        if (zone == MarginZone.Warn)
        {
            return new MarginDecision
            {
                Action = MarginAction.RebalanceCollateral,
                Zone = zone,
                Estimate = est,
                CollateralNeededQuote = need,
                AddMarginQuote = local,
                TransferQuote = need - local,
            };
        }

        if (x.Mode == MarginMode.Cross)
        {
            PerpExposure afterTopUp = x.WithEquity(x.EquityQuote + local);
            double keep = LiquidationCalculator.QtyToReach(afterTopUp, target, tiers, closeFeeRate, reduceFeeRate);
            return new MarginDecision
            {
                Action = keep <= 0 ? MarginAction.FlattenPair : MarginAction.ReducePair,
                Zone = zone,
                Estimate = est,
                CollateralNeededQuote = need,
                AddMarginQuote = local,
                TransferQuote = need - local,
                ReduceQtyCanon = x.QtyCanon - keep,
            };
        }

        return new MarginDecision
        {
            Action = MarginAction.FlattenPair,
            Zone = zone,
            Estimate = est,
            CollateralNeededQuote = need,
            AddMarginQuote = 0,
            ReduceQtyCanon = x.QtyCanon,
        };
    }
}
