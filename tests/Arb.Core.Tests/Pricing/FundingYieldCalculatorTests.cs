using Arb.Core.Pricing;
using Arb.Core.Primitives;

namespace Arb.Core.Tests.Pricing;

public class FundingYieldCalculatorTests
{
    private static readonly FundingGate Gate = new()
    {
        MaxBreakEvenHours = 16,
        MinNetApr = 0.10,
        MaxLeverage = 3,
        MinEntryLiqDistanceBps = 3_000,
    };

    [Fact]
    public void DynamicInterval_NormalizesToHourly()
    {
        Assert.Equal(1.25e-5, FundingYieldCalculator.HourlyRate(0.0001, 8), 1e-18);
        Assert.Equal(2.5e-5, FundingYieldCalculator.HourlyRate(0.0001, 4), 1e-18);
        Assert.Equal(1e-4, FundingYieldCalculator.HourlyRate(0.0001, 1), 1e-18); // Bybit switched to hourly after hitting the cap
        Assert.Throws<ArgumentOutOfRangeException>(() => FundingYieldCalculator.HourlyRate(0.0001, 0));
    }

    [Fact]
    public void AllTakerLegs_BreakEvenFarAbove16h_Rejected()
    {
        // short Binance 1000PEPE: 0.0100%/8h; long Bybit: 0.0020%/4h → g = 7.5e-6 /h
        // 4 taker legs (4.5 + 5.5 bps each way) + 0.5 bp spread each way → C = 21 bps
        var x = new FundingInputs
        {
            Strategy = StrategyKind.FundingPerpPerp,
            ShortLegHourlyRate = FundingYieldCalculator.HourlyRate(0.0001, 8),
            LongLegHourlyRate = FundingYieldCalculator.HourlyRate(0.00002, 4),
            Costs = new FundingCosts
            {
                EntrySpread = 0.00005,
                ExpectedExitSpread = 0.00005,
                EntryFeeShort = 0.00045,
                EntryFeeLong = 0.00055,
                ExitFeeShort = 0.00045,
                ExitFeeLong = 0.00055,
            },
            ExpectedHoldHours = 16,
            Leverage = 3,
            NotionalQuote = 1_200,
        };
        FundingEvaluation e = FundingYieldCalculator.Evaluate(x, Gate, 3_194);
        Assert.Equal(7.5e-6, e.IncomePerHour, 1e-15);
        Assert.Equal(0.0021, e.RoundTripCost, 1e-15);
        Assert.Equal(280.0, e.BreakEvenHours, 1e-9);
        Assert.Equal(FundingVerdict.RejectBreakEven, e.Verdict);
        Assert.True(e.ExpectedNetQuote < 0);
    }

    [Fact]
    public void MakerEntry_HighCarry_Enters_WithCorrectYields()
    {
        // short 0.05%/8h, long 0.002%/4h → g = 5.75e-5 /h; maker fees 1.8 + 2.0 bps per side → C = 8.6 bps
        var x = new FundingInputs
        {
            Strategy = StrategyKind.FundingPerpPerp,
            ShortLegHourlyRate = FundingYieldCalculator.HourlyRate(0.0005, 8),
            LongLegHourlyRate = FundingYieldCalculator.HourlyRate(0.00002, 4),
            Costs = new FundingCosts
            {
                EntrySpread = 0.00005,
                ExpectedExitSpread = 0.00005,
                EntryFeeShort = 0.00018,
                EntryFeeLong = 0.0002,
                ExitFeeShort = 0.00018,
                ExitFeeLong = 0.0002,
            },
            ExpectedHoldHours = 72,
            Leverage = 3,
            NotionalQuote = 1_000,
        };
        FundingEvaluation e = FundingYieldCalculator.Evaluate(x, Gate, 3_194);
        Assert.Equal(FundingVerdict.Enter, e.Verdict);
        Assert.Equal(5.75e-5, e.IncomePerHour, 1e-15);
        Assert.Equal(0.00086, e.RoundTripCost, 1e-15);
        Assert.Equal(14.956521739130435, e.BreakEvenHours, 1e-9);
        Assert.Equal(0.3990666666666667, e.NetApr, 1e-9);
        Assert.Equal(666.6666666666666, e.CapitalRequiredQuote, 1e-9);
        Assert.Equal(0.3990666666666667 * 1.5, e.CapitalApr, 1e-9);
        Assert.Equal(3.28, e.ExpectedNetQuote, 1e-9);

        Assert.Equal(FundingVerdict.RejectLiquidationDistance, FundingYieldCalculator.Evaluate(x, Gate, 2_000).Verdict);
        Assert.Equal(FundingVerdict.RejectLiquidationDistance, FundingYieldCalculator.Evaluate(x, Gate, double.NaN).Verdict);
        Assert.Equal(FundingVerdict.RejectLeverage, FundingYieldCalculator.Evaluate(x with { Leverage = 5 }, Gate, 3_194).Verdict);
    }

    [Fact]
    public void SpotPerp_IgnoresLongRate_CapitalIsSpotPlusMargin()
    {
        var x = new FundingInputs
        {
            Strategy = StrategyKind.FundingSpotPerp,
            ShortLegHourlyRate = 6e-5,
            LongLegHourlyRate = 1.0, // must be ignored: spot pays no funding
            Costs = new FundingCosts { EntryFeeLong = 0.0002, EntryFeeShort = 0.0002, ExitFeeLong = 0.0002, ExitFeeShort = 0.0002 },
            ExpectedHoldHours = 48,
            Leverage = 2,
            NotionalQuote = 1_000,
        };
        FundingEvaluation e = FundingYieldCalculator.Evaluate(x, Gate, 4_000);
        Assert.Equal(6e-5, e.IncomePerHour, 1e-15);
        Assert.Equal(1_500, e.CapitalRequiredQuote, 1e-9);
        Assert.Equal(e.NetApr * 1_000 / 1_500, e.CapitalApr, 1e-12);
        Assert.Equal(FundingVerdict.Enter, e.Verdict);
    }

    [Fact]
    public void NegativeCarry_And_InvalidInput()
    {
        var x = new FundingInputs
        {
            Strategy = StrategyKind.FundingPerpPerp,
            ShortLegHourlyRate = 1e-6,
            LongLegHourlyRate = 2e-6,
            ExpectedHoldHours = 24,
            Leverage = 2,
            NotionalQuote = 1_000,
        };
        Assert.Equal(FundingVerdict.RejectNonPositiveCarry, FundingYieldCalculator.Evaluate(x, Gate, 4_000).Verdict);
        Assert.Equal(FundingVerdict.RejectInvalidInput, FundingYieldCalculator.Evaluate(x with { Strategy = StrategyKind.Spatial }, Gate, 4_000).Verdict);
        Assert.Equal(FundingVerdict.RejectInvalidInput, FundingYieldCalculator.Evaluate(x with { ExpectedHoldHours = 0 }, Gate, 4_000).Verdict);
    }

    [Fact]
    public void Estimator_BlendsPredictedAndRealized_PerHour()
    {
        // predicted 0.08%/8h = 1e-4/h, realized EWMA 4e-5/h, 50/50, 20% mean-reversion haircut → 5.6e-5
        Assert.Equal(5.6e-5, FundingRateEstimator.ExpectedHourly(0.0008, 8, 4e-5, 0.5, 0.2), 1e-15);
        // half-life = interval → alpha 0.5
        Assert.Equal(6.25e-6, FundingRateEstimator.UpdateRealizedEwma(0, 0.0001, 8, 8), 1e-15);
        Assert.Throws<ArgumentOutOfRangeException>(() => FundingRateEstimator.ExpectedHourly(0.001, 8, 0, 1.5, 0));
    }
}
