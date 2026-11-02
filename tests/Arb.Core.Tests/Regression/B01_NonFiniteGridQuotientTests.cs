using Arb.Core.Primitives;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// B-01 (S3, builder recheck before closing Tahap 1; pre-existing API). <c>FixedPoint.ToUnits</c> with finite inputs whose
/// quotient overflows (value / unit = ±∞) returned <c>long.MinValue</c> instead of throwing: ∞ − ∞ in the snap tolerance
/// is NaN, and NaN passes "r ≥ 9.2e18 || r ≤ −9.2e18" because every comparison with NaN is false. Rounding.Down broke with
/// C-01 (122309d: tol became ulp-based); Rounding.Up had already returned long.MinValue in v0.2 (ceil(∞ − ∞) = NaN).
/// Fail-open on invalid input, same class as R-18. Contract: a quotient outside Int64 range, or non-finite, throws.
/// </summary>
public class B01_NonFiniteGridQuotientTests
{
    [Theory]
    [InlineData(1e300, 1e-300)]
    [InlineData(-1e300, 1e-300)]
    [InlineData(double.MaxValue, 0.5)]
    [InlineData(-double.MaxValue, 0.25)]
    public void QuotientOverflowingToInfinity_Throws(double value, double unit)
    {
        Assert.Throws<OverflowException>(() => FixedPoint.ToUnits(value, unit, Rounding.Down)); // 30417db+: long.MinValue
        Assert.Throws<OverflowException>(() => FixedPoint.ToUnits(value, unit, Rounding.Up));   // v0.2+: long.MinValue
    }

    [Fact]
    public void RandomExtremeInputs_EitherThrowOrStayWithinOneUnit()
    {
        var rng = new Random(7);
        int threw = 0;
        for (int i = 0; i < 100_000; i++)
        {
            double value = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-300, 309));
            double unit = Math.Pow(10, rng.Next(-300, 300)) * (0.5 + rng.NextDouble());
            if (!double.IsFinite(value) || !(unit > 0) || !double.IsFinite(unit))
            {
                continue;
            }

            double x = value / unit;
            foreach (Rounding r in new[] { Rounding.Down, Rounding.Up })
            {
                long u;
                try
                {
                    u = FixedPoint.ToUnits(value, unit, r);
                }
                catch (OverflowException)
                {
                    Assert.True(!double.IsFinite(x) || Math.Abs(x) >= 9.2e18 - 2, $"threw for in-range x {x:R}");
                    threw++;
                    continue;
                }

                Assert.True(double.IsFinite(x) && Math.Abs(u - x) <= 1 + 1e-6 + Math.Abs(x) * 1e-15, $"value {value:R} unit {unit:R} x {x:R} → {u}");
            }
        }

        Assert.True(threw >= 10_000, $"coverage: only {threw} overflowing inputs");
    }
}
