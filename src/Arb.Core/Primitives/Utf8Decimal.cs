namespace Arb.Core.Primitives;

/// <summary>
/// Allocation-free parser: UTF-8 decimal text (exchange JSON) → mantissa × 10^exponent.
/// Accepts optional sign, digits, optional '.', optional exponent (e/E). Up to 18 significant digits;
/// surplus trailing fractional zeros are tolerated, any other precision loss FAILS (never rounds silently).
/// Combine with <see cref="DecimalGrid.TryToUnits(long, int, out long)"/> to get ticks/lots without decimal/double.
/// </summary>
public static class Utf8Decimal
{
    private const int MaxSignificantDigits = 18;
    private const int MaxExponentMagnitude = 1000;

    public static bool TryParse(ReadOnlySpan<byte> text, out long mantissa, out int exponent)
    {
        mantissa = 0;
        exponent = 0;
        int n = text.Length;
        if (n == 0)
        {
            return false;
        }

        int i = 0;
        bool negative = false;
        if (text[0] == (byte)'-')
        {
            negative = true;
            i = 1;
        }
        else if (text[0] == (byte)'+')
        {
            i = 1;
        }

        long m = 0;
        int significant = 0;
        int fractionDigits = 0;
        bool inFraction = false;
        bool anyDigit = false;
        int exp = 0;

        for (; i < n; i++)
        {
            byte c = text[i];
            if (c >= (byte)'0' && c <= (byte)'9')
            {
                anyDigit = true;
                int d = c - '0';
                if (m == 0 && d == 0)
                {
                    if (inFraction)
                    {
                        fractionDigits++;
                    }

                    continue;
                }

                if (significant < MaxSignificantDigits)
                {
                    m = (m * 10) + d;
                    significant++;
                    if (inFraction)
                    {
                        fractionDigits++;
                    }
                }
                else if (!inFraction || d != 0)
                {
                    return false;
                }
            }
            else if (c == (byte)'.' && !inFraction)
            {
                inFraction = true;
            }
            else if ((c == (byte)'e' || c == (byte)'E') && anyDigit)
            {
                if (!TryParseExponent(text[(i + 1)..], out exp))
                {
                    return false;
                }

                break;
            }
            else
            {
                return false;
            }
        }

        if (!anyDigit)
        {
            return false;
        }

        if (m == 0)
        {
            return true;
        }

        exponent = exp - fractionDigits;
        mantissa = negative ? -m : m;
        return true;
    }

    private static bool TryParseExponent(ReadOnlySpan<byte> text, out int exponent)
    {
        exponent = 0;
        if (text.Length == 0)
        {
            return false;
        }

        int i = 0;
        bool negative = false;
        if (text[0] == (byte)'-')
        {
            negative = true;
            i = 1;
        }
        else if (text[0] == (byte)'+')
        {
            i = 1;
        }

        if (i >= text.Length)
        {
            return false;
        }

        int v = 0;
        for (; i < text.Length; i++)
        {
            byte c = text[i];
            if (c < (byte)'0' || c > (byte)'9')
            {
                return false;
            }

            v = (v * 10) + (c - '0');
            if (v > MaxExponentMagnitude)
            {
                return false;
            }
        }

        exponent = negative ? -v : v;
        return true;
    }
}
