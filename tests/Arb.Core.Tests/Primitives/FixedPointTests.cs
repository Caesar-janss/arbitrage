using System.Text;
using Arb.Core.Primitives;

namespace Arb.Core.Tests.Primitives;

public class FixedPointTests
{
    [Theory]
    [InlineData("0.00001", 1L, -5)]
    [InlineData("0.01", 1L, -2)]
    [InlineData("100", 1L, 2)]
    [InlineData("0.5", 5L, -1)]
    [InlineData("0.0000001", 1L, -7)]
    [InlineData("25", 25L, 0)]
    public void DecimalGrid_NormalizesMantissaAndExponent(string step, long mantissa, int exponent)
    {
        DecimalGrid g = DecimalGrid.FromDecimal(decimal.Parse(step, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(mantissa, g.Mantissa);
        Assert.Equal(exponent, g.Exponent);
    }

    [Fact]
    public void DecimalGrid_TrailingZerosInDecimalAreIrrelevant()
    {
        Assert.Equal(DecimalGrid.FromDecimal(0.010m), DecimalGrid.FromDecimal(0.01m));
        Assert.Equal(0.01m, DecimalGrid.FromDecimal(0.0100m).Value);
    }

    [Fact]
    public void DecimalGrid_RejectsNonPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DecimalGrid.FromDecimal(0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => DecimalGrid.FromDecimal(-0.01m));
    }

    [Fact]
    public void TryToUnitsExact_AcceptsOnGrid_RejectsOffGrid()
    {
        DecimalGrid tick = DecimalGrid.FromDecimal(0.01m);
        Assert.True(tick.TryToUnitsExact(60000.01m, out long t));
        Assert.Equal(6000001L, t);
        Assert.True(tick.TryToUnitsExact(60000.010m, out t));
        Assert.Equal(6000001L, t);
        Assert.False(tick.TryToUnitsExact(60000.015m, out _));

        DecimalGrid lot100 = DecimalGrid.FromDecimal(100m);
        Assert.True(lot100.TryToUnitsExact(1500m, out long l));
        Assert.Equal(15L, l);
        Assert.False(lot100.TryToUnitsExact(150m, out _));
    }

    [Fact]
    public void TryToUnits_FromParsedMantissa_NoDecimalNoDouble()
    {
        DecimalGrid tick = DecimalGrid.FromDecimal(0.0000001m);
        Assert.True(Utf8Decimal.TryParse("0.0120100"u8, out long m, out int e));
        Assert.True(tick.TryToUnits(m, e, out long ticks));
        Assert.Equal(120100L, ticks);

        Assert.True(Utf8Decimal.TryParse("0.01201005"u8, out m, out e));
        Assert.False(tick.TryToUnits(m, e, out _));
    }

    [Fact]
    public void ToUnits_RoundsInRequestedDirection()
    {
        DecimalGrid step = DecimalGrid.FromDecimal(0.001m);
        Assert.Equal(12L, step.ToUnits(0.0129m, Rounding.Down));
        Assert.Equal(13L, step.ToUnits(0.0121m, Rounding.Up));
        Assert.Equal(12L, step.ToUnits(0.012m, Rounding.Up));
    }

    [Fact]
    public void Lcm_And_UnitsPer_GiveCommonLot()
    {
        DecimalGrid binance = DecimalGrid.FromDecimal(0.00001m);
        DecimalGrid bybit = DecimalGrid.FromDecimal(0.000001m);
        DecimalGrid common = DecimalGrid.Lcm(binance, bybit);
        Assert.Equal(0.00001m, common.Value);
        Assert.Equal(1L, common.UnitsPer(binance));
        Assert.Equal(10L, common.UnitsPer(bybit));

        DecimalGrid bybitPepe = DecimalGrid.FromDecimal(100_000m);
        DecimalGrid binancePepe = DecimalGrid.FromDecimal(1_000m);
        DecimalGrid pepeCommon = DecimalGrid.Lcm(bybitPepe, binancePepe);
        Assert.Equal(100_000m, pepeCommon.Value);
        Assert.Equal(100L, pepeCommon.UnitsPer(binancePepe));

        DecimalGrid odd = DecimalGrid.Lcm(DecimalGrid.FromDecimal(0.4m), DecimalGrid.FromDecimal(0.6m));
        Assert.Equal(1.2m, odd.Value);
        Assert.Throws<ArgumentException>(() => DecimalGrid.FromDecimal(0.4m).UnitsPer(DecimalGrid.FromDecimal(0.3m)));
    }

    [Fact]
    public void FixedPointToUnits_AbsorbsFloatingNoiseAtLargeMagnitudes()
    {
        // 6005000 × 0.999 is not exactly representable; must still floor to 5998995 (not 5998994).
        Assert.Equal(5998995L, FixedPoint.ToUnits(6005000 * 0.999, 1.0, Rounding.Down));
        Assert.Equal(1000L, FixedPoint.ToUnits(0.01, 0.00001, Rounding.Down));
        Assert.Equal(1000L, FixedPoint.ToUnits(0.01, 0.00001, Rounding.Up));
        Assert.Equal(2L, FixedPoint.ToUnits(2.5, 1.0, Rounding.Down));
        Assert.Equal(3L, FixedPoint.ToUnits(2.5, 1.0, Rounding.Up));
        Assert.Throws<ArgumentOutOfRangeException>(() => FixedPoint.ToUnits(double.NaN, 1.0, Rounding.Down));
    }

    [Theory]
    [InlineData("60000.01", 6000001L, -2)]
    [InlineData("0.00001234", 1234L, -8)]
    [InlineData("-12.5", -125L, -1)]
    [InlineData("+7", 7L, 0)]
    [InlineData("1E-7", 1L, -7)]
    [InlineData("1.5e3", 15L, 2)]
    [InlineData("007.100", 7100L, -3)]
    [InlineData(".5", 5L, -1)]
    [InlineData("0.000", 0L, 0)]
    [InlineData("123456789012345678.000", 123456789012345678L, 0)]
    public void Utf8Decimal_ParsesExchangeFormats(string text, long mantissa, int exponent)
    {
        Assert.True(Utf8Decimal.TryParse(Encoding.UTF8.GetBytes(text), out long m, out int e));
        Assert.Equal(mantissa, m);
        Assert.Equal(exponent, e);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("1e")]
    [InlineData("12x")]
    [InlineData("1234567890123456789")]
    [InlineData("0.1234567890123456789")]
    public void Utf8Decimal_RejectsMalformedOrLossy(string text)
    {
        Assert.False(Utf8Decimal.TryParse(Encoding.UTF8.GetBytes(text), out _, out _));
    }
}
