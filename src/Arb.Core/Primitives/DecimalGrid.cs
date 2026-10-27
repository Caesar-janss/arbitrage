using System.Globalization;

namespace Arb.Core.Primitives;

/// <summary>
/// Exact decimal grid step (tick size, lot step, canonical lot): value = Mantissa × 10^Exponent,
/// normalized so Mantissa is not divisible by 10. Converts values onto the grid exactly with
/// integer arithmetic (no floating point, no allocation).
/// </summary>
public readonly struct DecimalGrid : IEquatable<DecimalGrid>
{
    private const int MaxShift = 30;

    private DecimalGrid(long mantissa, int exponent)
    {
        Mantissa = mantissa;
        Exponent = exponent;
        Value = ComputeValue(mantissa, exponent);
        ValueD = (double)Value;
    }

    public long Mantissa { get; }

    public int Exponent { get; }

    /// <summary>Exact step size.</summary>
    public decimal Value { get; }

    /// <summary>Step size as double (hot-path economics).</summary>
    public double ValueD { get; }

    public static DecimalGrid Create(long mantissa, int exponent)
    {
        if (mantissa <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mantissa), mantissa, "grid step must be > 0");
        }

        while (mantissa % 10 == 0)
        {
            mantissa /= 10;
            exponent++;
        }

        if (exponent < -28 || exponent > 18)
        {
            throw new ArgumentOutOfRangeException(nameof(exponent), exponent, "grid exponent out of range");
        }

        return new DecimalGrid(mantissa, exponent);
    }

    public static DecimalGrid FromDecimal(decimal step)
    {
        if (step <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "grid step must be > 0");
        }

        Decompose(step, out Int128 m, out int e);
        if (m > long.MaxValue)
        {
            throw new OverflowException("grid mantissa exceeds Int64");
        }

        return Create((long)m, e);
    }

    /// <summary>Least common multiple of two grids (e.g. common lot of two venues in canonical units).</summary>
    public static DecimalGrid Lcm(DecimalGrid a, DecimalGrid b)
    {
        int e = Math.Min(a.Exponent, b.Exponent);
        int sa = a.Exponent - e;
        int sb = b.Exponent - e;
        if (sa > MaxShift || sb > MaxShift)
        {
            throw new OverflowException("grids too far apart for LCM");
        }

        Int128 ma = a.Mantissa * FixedPoint.Pow10Wide(sa);
        Int128 mb = b.Mantissa * FixedPoint.Pow10Wide(sb);
        Int128 l = ma / FixedPoint.Gcd(ma, mb) * mb;
        if (l > long.MaxValue)
        {
            throw new OverflowException("LCM exceeds Int64 mantissa");
        }

        return Create((long)l, e);
    }

    /// <summary>How many <paramref name="finer"/> steps make one step of this grid. Throws if not an exact multiple.</summary>
    public long UnitsPer(DecimalGrid finer)
    {
        Int128 num = Mantissa;
        Int128 den = finer.Mantissa;
        int shift = Exponent - finer.Exponent;
        if (Math.Abs(shift) > MaxShift)
        {
            throw new OverflowException("grids too far apart");
        }

        if (shift >= 0)
        {
            num *= FixedPoint.Pow10Wide(shift);
        }
        else
        {
            den *= FixedPoint.Pow10Wide(-shift);
        }

        if (num % den != 0)
        {
            throw new ArgumentException($"grid {this} is not a multiple of {finer}", nameof(finer));
        }

        Int128 r = num / den;
        if (r > long.MaxValue)
        {
            throw new OverflowException("ratio exceeds Int64");
        }

        return (long)r;
    }

    /// <summary>Exact: value (mantissa × 10^exponent, e.g. from <see cref="Utf8Decimal"/>) → grid units. False if off-grid or overflow.</summary>
    public bool TryToUnits(long mantissa, int exponent, out long units) => TryToUnitsCore(mantissa, exponent, out units);

    /// <summary>Exact: decimal value → grid units. False if off-grid or overflow.</summary>
    public bool TryToUnitsExact(decimal value, out long units)
    {
        Decompose(value, out Int128 m, out int e);
        return TryToUnitsCore(m, e, out units);
    }

    /// <summary>Decimal value → grid units with explicit rounding (for values that may be off-grid).</summary>
    public long ToUnits(decimal value, Rounding rounding)
    {
        decimal q = value / Value;
        decimal r = rounding == Rounding.Down ? decimal.Floor(q) : decimal.Ceiling(q);
        if (r > long.MaxValue || r < long.MinValue)
        {
            throw new OverflowException("grid units out of Int64 range");
        }

        return (long)r;
    }

    public decimal ToDecimal(long units) => units * Value;

    public double ToDouble(long units) => units * ValueD;

    public bool Equals(DecimalGrid other) => Mantissa == other.Mantissa && Exponent == other.Exponent;

    public override bool Equals(object? obj) => obj is DecimalGrid other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Mantissa, Exponent);

    public static bool operator ==(DecimalGrid left, DecimalGrid right) => left.Equals(right);

    public static bool operator !=(DecimalGrid left, DecimalGrid right) => !left.Equals(right);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    internal static void Decompose(decimal value, out Int128 mantissa, out int exponent)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        bool negative = bits[3] < 0;
        int scale = (bits[3] >> 16) & 0xFF;
        UInt128 m = ((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0];
        exponent = -scale;
        if (m == 0)
        {
            mantissa = 0;
            exponent = 0;
            return;
        }

        while (m % 10 == 0)
        {
            m /= 10;
            exponent++;
        }

        mantissa = negative ? -(Int128)m : (Int128)m;
    }

    private bool TryToUnitsCore(Int128 mantissa, int exponent, out long units)
    {
        units = 0;
        if (mantissa == 0)
        {
            return true;
        }

        int shift = exponent - Exponent;
        Int128 num;
        if (shift >= 0)
        {
            if (shift > FixedPoint.MaxPow10)
            {
                return false;
            }

            long p = FixedPoint.Pow10(shift);
            if (Int128.Abs(mantissa) > Int128.MaxValue / p)
            {
                return false;
            }

            num = mantissa * p;
        }
        else
        {
            int down = -shift;
            if (down > 38)
            {
                return false;
            }

            Int128 d = FixedPoint.Pow10Wide(down);
            if (mantissa % d != 0)
            {
                return false;
            }

            num = mantissa / d;
        }

        if (num % Mantissa != 0)
        {
            return false;
        }

        Int128 u = num / Mantissa;
        if (u > long.MaxValue || u < long.MinValue)
        {
            return false;
        }

        units = (long)u;
        return true;
    }

    private static decimal ComputeValue(long mantissa, int exponent)
    {
        if (exponent >= 0)
        {
            decimal v = mantissa;
            for (int i = 0; i < exponent; i++)
            {
                v *= 10m;
            }

            return v;
        }

        ulong um = (ulong)mantissa;
        return new decimal((int)(uint)um, (int)(uint)(um >> 32), 0, false, (byte)(-exponent));
    }
}
