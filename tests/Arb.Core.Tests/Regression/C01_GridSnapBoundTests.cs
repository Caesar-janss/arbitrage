using Arb.Core.Instruments;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// C-01 (S2, ChatGPT; pre-existing API). <c>FixedPoint.ToUnits</c> snapped with tol = 1e-9 + |x|·1e-12. From
/// |x| ≈ 1e12 the tolerance is a whole unit or more, so Rounding.Down moved UP (1e12 → 1e12 + 1) and Rounding.Up moved
/// DOWN (1e12 → 1e12 − 1), even for exactly representable integers. Callers use Down for "never more than" (common lots
/// of a spatial trade, repair sell lots ≤ residual) and Up for "at least".
/// Contract after the fix: the snap only absorbs floating-point noise (a few ulps of x, plus 1e-9 absolute) and is
/// clamped below 1e-6 of a unit, so it can never cross an integer; outside that band rounding is exactly the requested
/// direction (the safe one).
/// </summary>
public class C01_GridSnapBoundTests
{
    [Fact]
    public void ReviewerCase_ExactIntegerAt1e12_IsUnchanged()
    {
        Assert.Equal(1_000_000_000_000L, FixedPoint.ToUnits(1_000_000_000_000d, 1d, Rounding.Down)); // v0.3: …001
        Assert.Equal(1_000_000_000_000L, FixedPoint.ToUnits(1_000_000_000_000d, 1d, Rounding.Up));   // v0.3: 999_999_999_999
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(7d)]
    [InlineData(6_000_000d)]
    [InlineData(999_999_999d)]
    [InlineData(5e11)]
    [InlineData(1e12)]
    [InlineData(3e13)]
    [InlineData(1e15)]
    [InlineData(4503599627370496d)]    // 2^52
    [InlineData(9007199254740992d)]    // 2^53
    [InlineData(1e18)]
    public void ExactIntegers_AreFixedPoints_AtEveryMagnitude(double n)
    {
        Assert.Equal((long)n, FixedPoint.ToUnits(n, 1d, Rounding.Down));
        Assert.Equal((long)n, FixedPoint.ToUnits(n, 1d, Rounding.Up));
        Assert.Equal((long)n, FixedPoint.ToUnits(n * 0.25, 0.25, Rounding.Down)); // power-of-two unit: exact division
        Assert.Equal((long)n, FixedPoint.ToUnits(n * 0.25, 0.25, Rounding.Up));
    }

    [Fact]
    public void GenuineFractionsBelowAnInteger_AreNotPulledUp()
    {
        // 0.25 / 0.001 below the next unit is a real fraction, not noise: Down keeps the lower unit at any magnitude.
        foreach (double n in new[] { 10d, 1e6, 1e9, 1e11, 1e12, 1e13 })
        {
            Assert.Equal((long)n - 1, FixedPoint.ToUnits(n - 0.25, 1d, Rounding.Down));
            Assert.Equal((long)n, FixedPoint.ToUnits(n - 0.25, 1d, Rounding.Up));
            Assert.Equal((long)n, FixedPoint.ToUnits(n + 0.25, 1d, Rounding.Down));
            Assert.Equal((long)n + 1, FixedPoint.ToUnits(n + 0.25, 1d, Rounding.Up));
        }

        Assert.Equal(999_999L, FixedPoint.ToUnits(999_999.999, 1d, Rounding.Down));          // 1e-3 below: a real fraction
        Assert.Equal(1_000_000_001L, FixedPoint.ToUnits(1_000_000_000.0001, 1d, Rounding.Up)); // 1e-4 above: needs the next unit
    }

    [Fact]
    public void NoiseIsStillAbsorbed()
    {
        Assert.Equal(1000L, FixedPoint.ToUnits(0.01, 0.00001, Rounding.Down));          // 999.9999999999999
        Assert.Equal(1000L, FixedPoint.ToUnits(0.01, 0.00001, Rounding.Up));
        Assert.Equal(3L, FixedPoint.ToUnits(0.1 + 0.2, 0.1, Rounding.Up));              // 3.0000000000000004
        Assert.Equal(3L, FixedPoint.ToUnits(0.1 + 0.2, 0.1, Rounding.Down));
        Assert.Equal(3L, FixedPoint.ToUnits(2.9999999999, 1d, Rounding.Down));         // 1e-10 below: noise band (1e-9)
        Assert.Equal(5_998_995L, FixedPoint.ToUnits(60050 * (1 - 0.001), 0.01, Rounding.Down));
        // noise of 1–2 ulps ABOVE the 1e-9 absolute band (found by search over n·unit·k / k chains): needs the ulp term
        Assert.Equal(517_069_464L, FixedPoint.ToUnits(517.0694639999999, 0.000001, Rounding.Down));   // x = 517069463.99999994
        Assert.Equal(474_680_097L, FixedPoint.ToUnits(474.68009700000005, 0.000001, Rounding.Up));    // x = 474680097.00000006
        Assert.Equal(525_639_791L, FixedPoint.ToUnits(5256397.910000001, 0.01, Rounding.Up));         // x = 525639791.0000001
    }

    /// <summary>
    /// Property: for x = value / unit (as computed in double), Down ≤ x + s and Up ≥ x − s with s = 1e-6 + 2·ulp(x),
    /// i.e. the result never crosses into the next unit beyond the snap band; and Down ≥ x − 1 − s, Up ≤ x + 1 + s.
    /// Values: random decimal quantities on BTC / PEPE grids from 1 unit up to 1e15 units.
    /// </summary>
    [Fact]
    public void RandomValues_NeverCrossAnIntegerBeyondTheSnapBand()
    {
        var rng = new Random(101);
        double[] units = { 1, 0.01, 0.00001, 0.000001, 1e-8, 100_000, 0.1 };
        int checkedLarge = 0;
        for (int i = 0; i < 200_000; i++)
        {
            double unit = units[rng.Next(units.Length)];
            double magnitude = Math.Pow(10, rng.NextDouble() * 15);
            double frac = rng.Next(4) switch { 0 => 0, 1 => 1e-7, 2 => 1 - 1e-7, _ => rng.NextDouble() };
            double value = (Math.Floor(magnitude) + frac) * unit;
            double x = value / unit;
            double s = 1e-6 + (2 * (Math.BitIncrement(Math.Abs(x)) - Math.Abs(x)));
            long down = FixedPoint.ToUnits(value, unit, Rounding.Down);
            long up = FixedPoint.ToUnits(value, unit, Rounding.Up);
            string at = $"value {value:R} unit {unit:R} x {x:R}";
            Assert.True(down <= x + s, $"Down {down} > x + snap ({at})");
            Assert.True(up >= x - s, $"Up {up} < x − snap ({at})");
            Assert.True(down >= x - 1 - s && up <= x + 1 + s, $"moved more than one unit ({at})");
            if (x >= 1e12)
            {
                checkedLarge++;
            }
        }

        Assert.True(checkedLarge >= 10_000, $"coverage: {checkedLarge} values ≥ 1e12 units");
    }

    [Fact]
    public void PairLotGrid_UnitsFloor_NeverExceedsTheQuantity()
    {
        // Common lot 1e-8 BTC: 10,000 BTC = 1e12 common lots (the C-01 magnitude).
        var assets = AssetRegistry.Create("BTC", "USDT");
        var b = new InstrumentRegistryBuilder(assets);
        InstrumentKey ka = b.Add(Venue.Bitget, MarketType.Spot, "BTCUSDT", "BTC", "USDT", AccountKind.Spot, 0.01m, 0.00000001m, minNotional: 1m);
        InstrumentKey kb = b.Add(Venue.Bybit, MarketType.Spot, "BTCUSDT", "BTC", "USDT", AccountKind.Unified, 0.01m, 0.00000001m, minNotional: 1m);
        InstrumentRegistry reg = b.Build();
        var grid = new PairLotGrid(reg[ka], reg[kb]);
        long units = grid.UnitsFloor(9_999.999_999_995);                                    // 999,999,999,999.5 lots
        Assert.Equal(999_999_999_999L, units);                                              // v0.3: 1_000_000_000_000
    }
}
