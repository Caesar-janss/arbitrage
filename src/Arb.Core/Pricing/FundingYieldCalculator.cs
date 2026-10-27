using Arb.Core.Primitives;

namespace Arb.Core.Pricing;

/// <summary>Round-trip costs as fractions of notional (0.0005 = 5 bps).</summary>
public readonly struct FundingCosts
{
    public double EntrySpread { get; init; }

    public double ExpectedExitSpread { get; init; }

    public double EntryFeeLong { get; init; }

    public double EntryFeeShort { get; init; }

    public double ExitFeeLong { get; init; }

    public double ExitFeeShort { get; init; }

    public double Slippage { get; init; }

    public double RoundTrip => EntrySpread + ExpectedExitSpread + EntryFeeLong + EntryFeeShort + ExitFeeLong + ExitFeeShort + Slippage;
}

public readonly struct FundingInputs
{
    public StrategyKind Strategy { get; init; }

    /// <summary>Expected funding rate per hour paid by longs on the LONG leg venue (ignored for spot-perp: spot pays no funding).</summary>
    public double LongLegHourlyRate { get; init; }

    /// <summary>Expected funding rate per hour received by shorts on the SHORT leg venue.</summary>
    public double ShortLegHourlyRate { get; init; }

    public FundingCosts Costs { get; init; }

    public double ExpectedHoldHours { get; init; }

    public double Leverage { get; init; }

    public double NotionalQuote { get; init; }
}

public readonly struct FundingGate
{
    public double MaxBreakEvenHours { get; init; }

    public double MinNetApr { get; init; }

    public double MaxLeverage { get; init; }

    public double MinEntryLiqDistanceBps { get; init; }

    /// <summary>All limits finite, break-even / leverage / liq distance &gt; 0 (a NaN limit must never mean "no limit", R-18).</summary>
    public bool IsValid =>
        double.IsFinite(MaxBreakEvenHours) && MaxBreakEvenHours > 0
        && double.IsFinite(MinNetApr)
        && double.IsFinite(MaxLeverage) && MaxLeverage >= 1
        && double.IsFinite(MinEntryLiqDistanceBps) && MinEntryLiqDistanceBps > 0;
}

public enum FundingVerdict : byte
{
    Enter = 0,
    RejectInvalidInput = 1,
    RejectNonPositiveCarry = 2,
    RejectBreakEven = 3,
    RejectNegativeExpectedPnl = 4,
    RejectYield = 5,
    RejectLeverage = 6,
    RejectLiquidationDistance = 7,
}

public readonly struct FundingEvaluation
{
    /// <summary>g: net funding income per hour as fraction of notional.</summary>
    public double IncomePerHour { get; init; }

    /// <summary>C: one round-trip cost as fraction of notional.</summary>
    public double RoundTripCost { get; init; }

    public double BreakEvenHours { get; init; }

    /// <summary>(g·T − C)/T × 8760 — yield on NOTIONAL.</summary>
    public double NetApr { get; init; }

    /// <summary>Yield on capital actually tied up (spot notional + margins).</summary>
    public double CapitalApr { get; init; }

    public double CapitalRequiredQuote { get; init; }

    public double ExpectedNetQuote { get; init; }

    public double EntryLiqDistanceBps { get; init; }

    public FundingVerdict Verdict { get; init; }

    public bool ShouldEnter => Verdict == FundingVerdict.Enter;
}

/// <summary>
/// Funding-rate arbitrage economics with DYNAMIC intervals (Bybit switches 8/4/2/1h when the rate hits its cap;
/// Binance lists 4h symbols). Every rate is normalized to per-hour before combining legs:
///     g  = r_short/I_short − r_long/I_long           (spot-perp: r_long = 0)
///     C  = entry spread + E[exit spread] + 4 fees + slippage
///     BreakEvenHours = C / g
///     NetApr(T) = (g·T − C) / T · 8760
///     Capital: perp-perp = 2·N/lev, spot-perp = N + N/lev
/// The spec's "ΔFR × 3 × 365 / costs" is a dimensionless ratio, not a yield, and assumes 8h intervals.
/// Feed EXPECTED rates (see <see cref="FundingRateEstimator"/>), not the current spike: funding mean-reverts.
/// </summary>
public static class FundingYieldCalculator
{
    public const double HoursPerYear = 8760;

    public static double HourlyRate(double ratePerInterval, int intervalHours)
    {
        if (intervalHours <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalHours), intervalHours, "must be > 0");
        }

        return ratePerInterval / intervalHours;
    }

    public static FundingEvaluation Evaluate(in FundingInputs x, in FundingGate gate, double entryLiqDistanceBps)
    {
        // every input and limit finite (R-18): +∞ hold made NetApr NaN and still entered; NaN limits passed `>=`/`<`
        bool valid = x.Strategy.IsFunding()
            && gate.IsValid
            && double.IsFinite(x.ExpectedHoldHours) && x.ExpectedHoldHours > 0
            && double.IsFinite(x.Leverage) && x.Leverage > 0
            && double.IsFinite(x.NotionalQuote) && x.NotionalQuote > 0
            && double.IsFinite(x.Costs.RoundTrip) && x.Costs.RoundTrip >= 0
            && double.IsFinite(x.LongLegHourlyRate)
            && double.IsFinite(x.ShortLegHourlyRate);
        // entryLiqDistanceBps: +∞ = cannot be liquidated (allowed); NaN is rejected by the NaN-safe liquidation gate below
        if (!valid)
        {
            return new FundingEvaluation { Verdict = FundingVerdict.RejectInvalidInput };
        }

        double longRate = x.Strategy == StrategyKind.FundingSpotPerp ? 0 : x.LongLegHourlyRate;
        double g = x.ShortLegHourlyRate - longRate;
        double c = x.Costs.RoundTrip;
        double t = x.ExpectedHoldHours;
        double breakEven = g > 0 ? c / g : double.PositiveInfinity;
        double netApr = ((g * t) - c) / t * HoursPerYear;
        double capital = x.Strategy == StrategyKind.FundingPerpPerp
            ? 2 * x.NotionalQuote / x.Leverage
            : x.NotionalQuote + (x.NotionalQuote / x.Leverage);
        double capitalApr = netApr * x.NotionalQuote / capital;
        double expectedNet = ((g * t) - c) * x.NotionalQuote;

        // every gate written as "!(value within limit)": a NaN anywhere rejects instead of passing
        FundingVerdict verdict;
        if (!(g > 0))
        {
            verdict = FundingVerdict.RejectNonPositiveCarry;
        }
        else if (!(breakEven < gate.MaxBreakEvenHours))
        {
            verdict = FundingVerdict.RejectBreakEven;
        }
        else if (!(expectedNet > 0))
        {
            verdict = FundingVerdict.RejectNegativeExpectedPnl;
        }
        else if (!(netApr >= gate.MinNetApr))
        {
            verdict = FundingVerdict.RejectYield;
        }
        else if (!(x.Leverage <= gate.MaxLeverage))
        {
            verdict = FundingVerdict.RejectLeverage;
        }
        else if (!(entryLiqDistanceBps >= gate.MinEntryLiqDistanceBps))
        {
            verdict = FundingVerdict.RejectLiquidationDistance;
        }
        else
        {
            verdict = FundingVerdict.Enter;
        }

        return new FundingEvaluation
        {
            IncomePerHour = g,
            RoundTripCost = c,
            BreakEvenHours = breakEven,
            NetApr = netApr,
            CapitalApr = capitalApr,
            CapitalRequiredQuote = capital,
            ExpectedNetQuote = expectedNet,
            EntryLiqDistanceBps = entryLiqDistanceBps,
            Verdict = verdict,
        };
    }
}

/// <summary>
/// Expected hourly funding: blend of the venue's predicted next rate and an EWMA of realized hourly rates,
/// then a mean-reversion haircut. All inputs normalized per hour (interval-aware).
/// </summary>
public static class FundingRateEstimator
{
    public static double ExpectedHourly(
        double predictedRate,
        int predictedIntervalHours,
        double realizedEwmaHourly,
        double predictedWeight,
        double meanReversionHaircut)
    {
        if (predictedWeight < 0 || predictedWeight > 1 || meanReversionHaircut < 0 || meanReversionHaircut >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(predictedWeight), "weights must be in [0,1), haircut in [0,1)");
        }

        double predictedHourly = FundingYieldCalculator.HourlyRate(predictedRate, predictedIntervalHours);
        double blended = (predictedWeight * predictedHourly) + ((1 - predictedWeight) * realizedEwmaHourly);
        return blended * (1 - meanReversionHaircut);
    }

    /// <summary>EWMA update of realized hourly funding with a half-life in hours (call once per settlement).</summary>
    public static double UpdateRealizedEwma(double currentEwmaHourly, double settledRate, int intervalHours, double halfLifeHours)
    {
        if (!(halfLifeHours > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(halfLifeHours), halfLifeHours, "must be > 0");
        }

        double hourly = FundingYieldCalculator.HourlyRate(settledRate, intervalHours);
        double alpha = 1 - Math.Exp(-intervalHours * Math.Log(2) / halfLifeHours);
        return currentEwmaHourly + (alpha * (hourly - currentEwmaHourly));
    }
}
