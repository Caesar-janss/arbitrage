namespace Arb.Core.Primitives;

/// <summary>
/// Integer helpers for the exact (tick/lot) domain.
/// Numeric policy of Arb.Core:
///   * Inside one venue (book, VWAP walk, order construction) everything is exact integer:
///     price = ticks, qty = lots, notional = Σ ticks×lots accumulated in Int128.
///   * Cross-venue economics (edge, fees, slippage, Q* search) use double in canonical units.
///     Relative error ~1e-15 vs. decision granularity ~1e-5 (0.1 bps) → 10 orders of margin.
///   * Anything that becomes an order field is snapped back onto the venue grid with explicit
///     rounding direction (never implicit).
/// </summary>
public static class FixedPoint
{
    public const int MaxPow10 = 18;

    /// <summary>Absolute tolerance (grid units) when snapping a double onto a grid; absorbs FP noise like 2.9999999999.</summary>
    public const double GridEpsilon = 1e-9;

    /// <summary>Relative tolerance added on top (double ulp is ~2e-16·|x|; 1e-12 gives 4 orders of headroom for large unit counts).</summary>
    public const double GridRelativeEpsilon = 1e-12;

    private static readonly long[] Pow10Table = BuildPow10();

    public static long Pow10(int exponent)
    {
        if ((uint)exponent > MaxPow10)
        {
            throw new ArgumentOutOfRangeException(nameof(exponent), exponent, "0..18");
        }

        return Pow10Table[exponent];
    }

    public static Int128 Pow10Wide(int exponent)
    {
        if ((uint)exponent > 38)
        {
            throw new ArgumentOutOfRangeException(nameof(exponent), exponent, "0..38");
        }

        Int128 r = 1;
        for (int i = 0; i < exponent; i++)
        {
            r *= 10;
        }

        return r;
    }

    public static Int128 Gcd(Int128 a, Int128 b)
    {
        a = Int128.Abs(a);
        b = Int128.Abs(b);
        while (b != 0)
        {
            Int128 t = a % b;
            a = b;
            b = t;
        }

        return a;
    }

    /// <summary>Snap a double value onto a grid of size <paramref name="unit"/> with explicit rounding.</summary>
    public static long ToUnits(double value, double unit, Rounding rounding)
    {
        if (!(unit > 0) || double.IsInfinity(unit))
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "unit must be finite and > 0");
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "value must be finite");
        }

        double x = value / unit;
        double tol = GridEpsilon + (Math.Abs(x) * GridRelativeEpsilon);
        double r = rounding == Rounding.Down ? Math.Floor(x + tol) : Math.Ceiling(x - tol);
        if (r >= 9.2e18 || r <= -9.2e18)
        {
            throw new OverflowException("grid units out of Int64 range");
        }

        return (long)r;
    }

    private static long[] BuildPow10()
    {
        var t = new long[MaxPow10 + 1];
        t[0] = 1;
        for (int i = 1; i < t.Length; i++)
        {
            t[i] = t[i - 1] * 10;
        }

        return t;
    }
}
