using Arb.Core.Primitives;

namespace Arb.Core.Risk;

/// <summary>
/// One maintenance-margin bracket (venue risk-limit tiers). MM = notional × Mmr − MaintenanceAmount,
/// where MaintenanceAmount is the Binance-style "cum" that keeps MM continuous across brackets.
/// </summary>
public readonly struct MarginTier
{
    public MarginTier(double maxNotional, double maintenanceMarginRate, double maintenanceAmount)
    {
        if (!(maxNotional > 0) || maintenanceMarginRate is < 0 or >= 1 || maintenanceAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNotional), "invalid margin tier");
        }

        MaxNotional = maxNotional;
        MaintenanceMarginRate = maintenanceMarginRate;
        MaintenanceAmount = maintenanceAmount;
    }

    public double MaxNotional { get; }

    public double MaintenanceMarginRate { get; }

    public double MaintenanceAmount { get; }
}

public enum MarginMode : byte
{
    /// <summary>Whole wallet backs the position; reducing at mark keeps equity → raises liq distance.</summary>
    Cross = 1,

    /// <summary>Venue releases margin pro-rata on reduce → liq PRICE barely moves; only adding margin helps.</summary>
    Isolated = 2,
}

/// <summary>
/// One perp leg expressed at the current mark: equity = collateral + unrealized PnL (both venue-reported).
/// Using equity-at-mark (not entry price) makes "what if I reduce now" exact: PnL is realized, equity unchanged.
/// </summary>
public readonly struct PerpExposure
{
    public PerpExposure(Side side, double qtyCanon, double markPx, double equityQuote, MarginMode mode)
    {
        Side = side;
        QtyCanon = qtyCanon;
        MarkPx = markPx;
        EquityQuote = equityQuote;
        Mode = mode;
    }

    /// <summary>Buy = long, Sell = short.</summary>
    public Side Side { get; }

    public double QtyCanon { get; }

    public double MarkPx { get; }

    public double EquityQuote { get; }

    public MarginMode Mode { get; }

    public double NotionalQuote => QtyCanon * MarkPx;

    public PerpExposure WithEquity(double equityQuote) => new(Side, QtyCanon, MarkPx, equityQuote, Mode);
}

public readonly struct LiquidationEstimate
{
    public double LiquidationPx { get; init; }

    /// <summary>Distance from mark to liquidation in the adverse direction (bps). +∞ if it cannot be liquidated.</summary>
    public double DistanceBps { get; init; }

    public double MaintenanceMarginQuote { get; init; }

    /// <summary>MM / equity at mark (venue "margin ratio"); ≥ 1 = liquidation.</summary>
    public double MarginRatio { get; init; }

    public int TierIndex { get; init; }

    public bool CanLiquidate { get; init; }
}

/// <summary>
/// Liquidation price / distance-to-liquidation for linear (USDT) perps, venue-agnostic and conservative:
/// m = mmr + closeFeeRate (Bybit-style "fee to close" buffer), MM = Q·P·m − cum.
///   short: Eq + Q(P0 − P) = Q·P·m − cum  ⇒  P_liq = (Eq + Q·P0 + cum) / (Q(1 + m))
///   long : Eq + Q(P − P0) = Q·P·m − cum  ⇒  P_liq = (Q·P0 − Eq − cum) / (Q(1 − m))   (≤ 0 ⇒ cannot liquidate)
/// The tier is chosen at the LIQUIDATION notional (it moves with price), resolved by fixed-point iteration.
/// </summary>
public static class LiquidationCalculator
{
    private const int MaxIterations = 8;

    public static double TargetPrice(Side side, double markPx, double distanceBps) =>
        side == Side.Sell ? markPx * (1 + (distanceBps * 1e-4)) : markPx * (1 - (distanceBps * 1e-4));

    public static int TierFor(double notional, ReadOnlySpan<MarginTier> tiers)
    {
        if (tiers.IsEmpty)
        {
            throw new ArgumentException("at least one margin tier required", nameof(tiers));
        }

        for (int i = 0; i < tiers.Length; i++)
        {
            if (notional <= tiers[i].MaxNotional)
            {
                return i;
            }
        }

        return tiers.Length - 1;
    }

    public static LiquidationEstimate Estimate(in PerpExposure x, ReadOnlySpan<MarginTier> tiers, double closeFeeRate)
    {
        Validate(x, tiers, closeFeeRate);
        double q = x.QtyCanon;
        double p0 = x.MarkPx;
        double eq = x.EquityQuote;

        int t = TierFor(q * p0, tiers);
        int tMark = t;
        double px = LiqPx(x.Side, q, p0, eq, tiers[t], closeFeeRate);
        double closest = px;
        for (int iter = 0; iter < MaxIterations; iter++)
        {
            int t2 = TierFor(q * Math.Max(px, 0), tiers);
            if (t2 == t)
            {
                closest = px;
                break;
            }

            t = t2;
            px = LiqPx(x.Side, q, p0, eq, tiers[t], closeFeeRate);
            // keep the most conservative (closest to mark) candidate if brackets oscillate
            closest = Math.Abs(px - p0) < Math.Abs(closest - p0) ? px : closest;
        }

        bool canLiq = !(x.Side == Side.Buy && closest <= 0);
        double distance = !canLiq
            ? double.PositiveInfinity
            : x.Side == Side.Sell ? (closest - p0) / p0 * 1e4 : (p0 - closest) / p0 * 1e4;

        MarginTier mt = tiers[tMark];
        double mm = Math.Max(0, (q * p0 * (mt.MaintenanceMarginRate + closeFeeRate)) - mt.MaintenanceAmount);
        return new LiquidationEstimate
        {
            LiquidationPx = canLiq ? closest : 0,
            DistanceBps = distance,
            MaintenanceMarginQuote = mm,
            MarginRatio = eq > 0 ? mm / eq : double.PositiveInfinity,
            TierIndex = t,
            CanLiquidate = canLiq,
        };
    }

    /// <summary>Extra equity (≥ 0) needed so the liquidation price moves to <paramref name="targetLiqPx"/> at the current size.</summary>
    public static double CollateralToReach(in PerpExposure x, double targetLiqPx, ReadOnlySpan<MarginTier> tiers, double closeFeeRate)
    {
        Validate(x, tiers, closeFeeRate);
        MarginTier tier = tiers[TierFor(x.QtyCanon * targetLiqPx, tiers)];
        double m = tier.MaintenanceMarginRate + closeFeeRate;
        double q = x.QtyCanon;
        double requiredEquity = x.Side == Side.Sell
            ? (q * targetLiqPx * (1 + m)) - (q * x.MarkPx) - tier.MaintenanceAmount
            : (q * x.MarkPx) - tier.MaintenanceAmount - (q * targetLiqPx * (1 - m));
        return Math.Max(0, requiredEquity - x.EquityQuote);
    }

    /// <summary>
    /// CROSS margin: largest size Q' ≤ Q (reducing at mark, paying <paramref name="reduceFeeRate"/>) whose liquidation
    /// price is at/beyond <paramref name="targetLiqPx"/>. Returns Q when no reduction is needed, 0 when only a full close works.
    ///   short: Q' = (Eq + cum − Q·P0·f) / (Pt(1+m) − P0(1+f))
    ///   long : Q' = (Eq + cum − Q·P0·f) / (P0(1−f) − Pt(1−m))
    /// </summary>
    public static double QtyToReach(in PerpExposure x, double targetLiqPx, ReadOnlySpan<MarginTier> tiers, double closeFeeRate, double reduceFeeRate)
    {
        Validate(x, tiers, closeFeeRate);
        if (reduceFeeRate < 0 || reduceFeeRate >= 0.1)
        {
            throw new ArgumentOutOfRangeException(nameof(reduceFeeRate), reduceFeeRate, "0..0.1");
        }

        double targetBps = x.Side == Side.Sell ? (targetLiqPx - x.MarkPx) / x.MarkPx * 1e4 : (x.MarkPx - targetLiqPx) / x.MarkPx * 1e4;
        if (Estimate(x, tiers, closeFeeRate).DistanceBps >= targetBps)
        {
            return x.QtyCanon;
        }

        double q = x.QtyCanon;
        double p0 = x.MarkPx;
        double f = reduceFeeRate;
        int t = TierFor(q * targetLiqPx, tiers);
        double qPrime = 0;
        for (int iter = 0; iter < MaxIterations; iter++)
        {
            MarginTier tier = tiers[t];
            double m = tier.MaintenanceMarginRate + closeFeeRate;
            double num = x.EquityQuote + tier.MaintenanceAmount - (q * p0 * f);
            double den = x.Side == Side.Sell ? (targetLiqPx * (1 + m)) - (p0 * (1 + f)) : (p0 * (1 - f)) - (targetLiqPx * (1 - m));
            if (num <= 0)
            {
                return 0;
            }

            qPrime = den <= 0 ? q : Math.Clamp(num / den, 0, q);
            int t2 = TierFor(qPrime * targetLiqPx, tiers);
            if (t2 == t)
            {
                break;
            }

            t = t2;
        }

        return qPrime;
    }

    private static double LiqPx(Side side, double q, double p0, double eq, in MarginTier tier, double closeFeeRate)
    {
        double m = tier.MaintenanceMarginRate + closeFeeRate;
        return side == Side.Sell
            ? (eq + (q * p0) + tier.MaintenanceAmount) / (q * (1 + m))
            : ((q * p0) - eq - tier.MaintenanceAmount) / (q * (1 - m));
    }

    private static void Validate(in PerpExposure x, ReadOnlySpan<MarginTier> tiers, double closeFeeRate)
    {
        if (!(x.QtyCanon > 0) || !(x.MarkPx > 0) || double.IsNaN(x.EquityQuote))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "qty and mark must be > 0, equity finite");
        }

        if (tiers.IsEmpty)
        {
            throw new ArgumentException("at least one margin tier required", nameof(tiers));
        }

        if (closeFeeRate < 0 || closeFeeRate >= 0.1)
        {
            throw new ArgumentOutOfRangeException(nameof(closeFeeRate), closeFeeRate, "0..0.1");
        }
    }
}
