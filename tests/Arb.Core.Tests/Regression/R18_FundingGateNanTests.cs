using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Risk;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-18 (S3): the funding entry gate compared against an unvalidated FundingGate with `&gt;=`/`&lt;`, so NaN limits
/// passed every check except the liquidation one (Enter with BE 220 h and leverage 10), ExpectedHoldHours = +∞ gave
/// NetApr NaN and Enter, and RiskLimits.Validate never checked MinFundingNetApr (P05).
/// </summary>
public class R18_FundingGateNanTests
{
    private static readonly FundingGate Sane = new() { MaxBreakEvenHours = 16, MinNetApr = 0.10, MaxLeverage = 3, MinEntryLiqDistanceBps = 3000 };

    private static FundingInputs Inputs => new()
    {
        Strategy = StrategyKind.FundingPerpPerp,
        ShortLegHourlyRate = 1e-5,
        LongLegHourlyRate = 0,
        Costs = new FundingCosts { EntryFeeLong = 0.00055, EntryFeeShort = 0.00055, ExitFeeLong = 0.00055, ExitFeeShort = 0.00055 },
        ExpectedHoldHours = 16,
        Leverage = 10,
        NotionalQuote = 1000,
    };

    [Fact]
    public void NaNGate_NeverEnters()
    {
        var nan = new FundingGate { MaxBreakEvenHours = double.NaN, MinNetApr = double.NaN, MaxLeverage = double.NaN, MinEntryLiqDistanceBps = 3000 };
        FundingEvaluation e = FundingYieldCalculator.Evaluate(Inputs with { ExpectedHoldHours = 1000 }, nan, 5000);
        Assert.True(e.Verdict != FundingVerdict.Enter, $"NaN gate → {e.Verdict}"); // v0.2: Enter (BE 220 h, leverage 10)
        Assert.Equal(FundingVerdict.RejectInvalidInput, e.Verdict);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void NonFiniteHold_IsInvalidInput(double hold)
    {
        FundingEvaluation e = FundingYieldCalculator.Evaluate(Inputs with { ExpectedHoldHours = hold, Leverage = 2 }, Sane with { MaxBreakEvenHours = 1e9 }, 5000);
        Assert.Equal(FundingVerdict.RejectInvalidInput, e.Verdict); // v0.2 (+∞): Enter with NetApr NaN
    }

    [Fact]
    public void NonFiniteInputs_AreInvalid()
    {
        FundingGate open = Sane with { MaxBreakEvenHours = 1e9, MaxLeverage = 20 };
        Assert.Equal(FundingVerdict.RejectInvalidInput, FundingYieldCalculator.Evaluate(Inputs with { Leverage = double.PositiveInfinity }, open, 5000).Verdict);
        Assert.Equal(FundingVerdict.RejectInvalidInput, FundingYieldCalculator.Evaluate(Inputs with { NotionalQuote = double.PositiveInfinity }, open, 5000).Verdict);
        Assert.Equal(FundingVerdict.RejectInvalidInput, FundingYieldCalculator.Evaluate(Inputs with { ShortLegHourlyRate = double.PositiveInfinity }, open, 5000).Verdict);
        Assert.Equal(FundingVerdict.RejectInvalidInput, FundingYieldCalculator.Evaluate(Inputs with { Costs = new FundingCosts { Slippage = double.NaN } }, open, 5000).Verdict);
    }

    [Fact]
    public void SaneGate_IsUnchanged()
    {
        FundingEvaluation e = FundingYieldCalculator.Evaluate(Inputs, Sane, 5000);
        Assert.Equal(FundingVerdict.RejectBreakEven, e.Verdict);
        Assert.Equal(220, e.BreakEvenHours, 1e-9);
    }

    [Fact]
    public void RiskLimits_EveryDoubleProperty_IsCheckedForNaN()
    {
        foreach (System.Reflection.PropertyInfo prop in typeof(RiskLimits).GetProperties())
        {
            if (prop.PropertyType != typeof(double) || !prop.CanWrite)
            {
                continue;
            }

            var limits = new RiskLimits();
            prop.SetValue(limits, double.NaN);
            IReadOnlyList<string> errors = limits.Validate();
            Assert.True(errors.Any(e => e.Contains(prop.Name, StringComparison.Ordinal)), $"{prop.Name} = NaN is not reported: [{string.Join("; ", errors)}]");
        }
    }

    [Fact]
    public void RiskLimits_RejectNonFiniteFundingAndOtherDoubles()
    {
        Assert.True(new RiskLimits { MinFundingNetApr = double.NaN }.Validate().Count >= 1);       // v0.2: 0 errors
        Assert.True(new RiskLimits { MaxNotionalPerTradeQuote = double.PositiveInfinity }.Validate().Count >= 1);
        Assert.True(new RiskLimits { MaxBreakEvenHours = double.PositiveInfinity }.Validate().Count >= 1);
        Assert.Empty(new RiskLimits().Validate());
    }
}
