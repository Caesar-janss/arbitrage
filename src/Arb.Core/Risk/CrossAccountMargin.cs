using Arb.Core.Primitives;

namespace Arb.Core.Risk;

/// <summary>
/// One position on a CROSS-margin account (all positions share the account equity).
/// Tiers are the instrument's maintenance brackets; fees are fractions of notional.
/// </summary>
public readonly struct AccountPosition
{
    public AccountPosition(Side side, double qtyCanon, double markPx, MarginTier[] tiers, double closeFeeRate, double reduceFeeRate)
    {
        if (!(qtyCanon > 0) || !(markPx > 0) || !double.IsFinite(qtyCanon) || !double.IsFinite(markPx))
        {
            throw new ArgumentOutOfRangeException(nameof(qtyCanon), "qty and mark must be finite and > 0");
        }

        if (tiers is null || tiers.Length == 0)
        {
            throw new ArgumentException("at least one margin tier required", nameof(tiers));
        }

        if (closeFeeRate is < 0 or >= 0.1 || reduceFeeRate is < 0 or >= 0.1 || double.IsNaN(closeFeeRate) || double.IsNaN(reduceFeeRate))
        {
            throw new ArgumentOutOfRangeException(nameof(closeFeeRate), "fee rates 0..0.1");
        }

        Side = side;
        QtyCanon = qtyCanon;
        MarkPx = markPx;
        Tiers = tiers;
        CloseFeeRate = closeFeeRate;
        ReduceFeeRate = reduceFeeRate;
    }

    /// <summary>Buy = long, Sell = short.</summary>
    public Side Side { get; }

    public double QtyCanon { get; }

    public double MarkPx { get; }

    public MarginTier[] Tiers { get; }

    /// <summary>Fee buffer the venue reserves to close at liquidation (part of maintenance margin).</summary>
    public double CloseFeeRate { get; }

    /// <summary>Taker fee paid when the engine reduces this position.</summary>
    public double ReduceFeeRate { get; }

    public double NotionalQuote => QtyCanon * MarkPx;
}

public readonly struct AccountMarginEstimate
{
    /// <summary>
    /// Smallest uniform adverse move (bps) — every short's price up AND every long's price down by the same
    /// fraction — at which account equity falls to account maintenance margin. 0 = already liquidatable,
    /// +∞ = cannot be liquidated (e.g. unlevered longs only).
    /// </summary>
    public double DistanceBps { get; init; }

    public double MaintenanceMarginQuote { get; init; }

    /// <summary>MM / equity at mark (≥ 1 = liquidation).</summary>
    public double MarginRatio { get; init; }

    public double GrossNotionalQuote { get; init; }

    public bool CanLiquidate { get; init; }
}

/// <summary>
/// Account-level distance to liquidation for CROSS margin (Bybit UTA, Binance cross, Bitget crossed).
///
/// All positions draw on one equity E (collateral + Σ uPnL at mark, venue-reported). Under a uniform adverse
/// move s (fraction) on every leg, scaled by a keep-factor k (pro-rata reduce):
///   surplus(s; k, E) = E − (1−k)·Σ Nᵢ·fᵢ − k·Σ Nᵢ·min(s, 1 for longs) − Σ MMᵢ(k·Nᵢ·(1 ± s))
///   MMᵢ(n) = max(0, n·(mmrₜ + closeFeeᵢ) − cumₜ),  tier t chosen at the shocked notional n.
/// Liquidation distance = root of surplus(s; 1, E) = 0. The uniform all-adverse move dominates every one-leg-only
/// move (equity falls faster and short MM rises), so it is the conservative distance for a guard.
/// surplus is strictly decreasing in s and (for s ≥ fee) in k; roots are found by bracketing + bisection
/// (deterministic, allocation-free, ≈ 1e-12 relative). For one position this equals
/// <see cref="LiquidationCalculator.Estimate"/> (tested).
/// </summary>
public static class CrossAccountMargin
{
    private const int BisectionIterations = 200;
    private const double MaxShock = 1e6; // 10^10 bps: beyond this treated as "cannot liquidate"

    public static AccountMarginEstimate Estimate(ReadOnlySpan<AccountPosition> positions, double equityQuote)
    {
        Validate(positions, equityQuote);
        double gross = 0;
        double mmAtMark = 0;
        for (int i = 0; i < positions.Length; i++)
        {
            gross += positions[i].NotionalQuote;
            mmAtMark += MaintenanceMargin(positions[i], positions[i].NotionalQuote);
        }

        double ratio = equityQuote > 0 ? mmAtMark / equityQuote : double.PositiveInfinity;
        if (positions.IsEmpty)
        {
            return new AccountMarginEstimate { DistanceBps = double.PositiveInfinity, MarginRatio = 0, CanLiquidate = false };
        }

        double s = LiquidationShock(positions, equityQuote);
        return new AccountMarginEstimate
        {
            DistanceBps = double.IsPositiveInfinity(s) ? double.PositiveInfinity : s * 1e4,
            MaintenanceMarginQuote = mmAtMark,
            MarginRatio = ratio,
            GrossNotionalQuote = gross,
            CanLiquidate = !double.IsPositiveInfinity(s),
        };
    }

    /// <summary>Extra equity (≥ 0) so the account distance becomes <paramref name="targetBps"/> at current sizes.</summary>
    public static double CollateralToReach(ReadOnlySpan<AccountPosition> positions, double equityQuote, double targetBps)
    {
        Validate(positions, equityQuote);
        ValidateTarget(targetBps);

        // surplus is linear in E with slope 1 → exact.
        return Math.Max(0, -Surplus(positions, targetBps * 1e-4, 1, equityQuote));
    }

    /// <summary>
    /// Largest keep-factor k ∈ [0, 1] (every position scaled by k, the closed part paying its reduce fee) whose
    /// account distance is at least <paramref name="targetBps"/>. 1 = no reduction needed, 0 = close everything.
    /// </summary>
    public static double KeepFractionToReach(ReadOnlySpan<AccountPosition> positions, double equityQuote, double targetBps)
    {
        Validate(positions, equityQuote);
        ValidateTarget(targetBps);
        if (positions.IsEmpty)
        {
            return 1;
        }

        double s = targetBps * 1e-4;
        if (Surplus(positions, s, 1, equityQuote) >= 0)
        {
            return 1;
        }

        if (!(Surplus(positions, s, 0, equityQuote) > 0))
        {
            return 0; // equity cannot even pay the fees to close: flatten
        }

        double lo = 0; // surplus(lo) > 0
        double hi = 1; // surplus(hi) < 0
        for (int it = 0; it < BisectionIterations && hi - lo > 1e-15; it++)
        {
            double mid = 0.5 * (lo + hi);
            if (Surplus(positions, s, mid, equityQuote) >= 0)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return lo; // conservative side of the root
    }

    private static double LiquidationShock(ReadOnlySpan<AccountPosition> positions, double equity)
    {
        if (!(Surplus(positions, 0, 1, equity) > 0))
        {
            return 0;
        }

        bool anyShort = false;
        for (int i = 0; i < positions.Length; i++)
        {
            anyShort |= positions[i].Side == Side.Sell;
        }

        // bracket: first shock where surplus ≤ 0
        double lo = 0;
        double hi = 1e-4;
        while (Surplus(positions, hi, 1, equity) > 0)
        {
            lo = hi;
            if (!anyShort && hi >= 1)
            {
                return double.PositiveInfinity; // longs only: price cannot fall below 0
            }

            hi = anyShort ? hi * 2 : Math.Min(1, hi * 2);
            if (hi > MaxShock)
            {
                return double.PositiveInfinity;
            }
        }

        for (int it = 0; it < BisectionIterations && hi - lo > hi * 1e-15; it++)
        {
            double mid = 0.5 * (lo + hi);
            if (Surplus(positions, mid, 1, equity) > 0)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return hi; // conservative side (surplus ≤ 0 here)
    }

    private static double Surplus(ReadOnlySpan<AccountPosition> positions, double shock, double keep, double equity)
    {
        double surplus = equity;
        for (int i = 0; i < positions.Length; i++)
        {
            ref readonly AccountPosition p = ref positions[i];
            double n = p.NotionalQuote;
            surplus -= (1 - keep) * n * p.ReduceFeeRate;
            double kept = keep * n;
            if (p.Side == Side.Sell)
            {
                surplus -= kept * shock;
                surplus -= MaintenanceMargin(p, kept * (1 + shock));
            }
            else
            {
                double s = Math.Min(shock, 1);
                surplus -= kept * s;
                surplus -= MaintenanceMargin(p, kept * (1 - s));
            }
        }

        return surplus;
    }

    private static double MaintenanceMargin(in AccountPosition p, double notional)
    {
        if (notional <= 0)
        {
            return 0;
        }

        MarginTier t = p.Tiers[LiquidationCalculator.TierFor(notional, p.Tiers)];
        return Math.Max(0, (notional * (t.MaintenanceMarginRate + p.CloseFeeRate)) - t.MaintenanceAmount);
    }

    private static void Validate(ReadOnlySpan<AccountPosition> positions, double equity)
    {
        if (double.IsNaN(equity) || double.IsInfinity(equity))
        {
            throw new ArgumentOutOfRangeException(nameof(equity), equity, "account equity must be finite");
        }

        for (int i = 0; i < positions.Length; i++)
        {
            if (positions[i].Tiers is null)
            {
                throw new ArgumentException("uninitialized AccountPosition", nameof(positions));
            }
        }
    }

    private static void ValidateTarget(double targetBps)
    {
        if (!(targetBps > 0) || double.IsInfinity(targetBps))
        {
            throw new ArgumentOutOfRangeException(nameof(targetBps), targetBps, "must be finite and > 0");
        }
    }
}
