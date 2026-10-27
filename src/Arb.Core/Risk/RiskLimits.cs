namespace Arb.Core.Risk;

/// <summary>Halt scope/level — mirrors risk.halt_state.</summary>
public enum HaltScope : byte
{
    Global = 1,
    Venue = 2,
    Pair = 3,
}

/// <summary>NO_NEW_ENTRIES never blocks repair/unwind. FLATTEN_AND_STOP actively closes exposure, then stops.</summary>
public enum HaltLevel : byte
{
    None = 0,
    NoNewEntries = 1,
    FlattenAndStop = 2,
}

/// <summary>
/// All risk parameters in one validated, immutable object (bound from configuration by the host; the host
/// fails fast at startup when <see cref="Validate"/> returns errors).
/// </summary>
public sealed record RiskLimits
{
    // ── Data quality ───────────────────────────────────────────────────────────
    public double MaxExchangeLatencyMs { get; init; } = 150;

    public double MaxBookSilenceMs { get; init; } = 5_000;

    public double MaxConnectionSilenceMs { get; init; } = 3_000;

    public double MaxClockUncertaintyMs { get; init; } = 20;

    /// <summary>Spatial prices both books NOW: each must have been updated within this (R-07).</summary>
    public double MaxSpatialBookAgeMs { get; init; } = 1_000;

    /// <summary>Spatial: max receive-time skew between the two books.</summary>
    public double MaxSpatialPairSkewMs { get; init; } = 500;

    /// <summary>Funding entry/exit tolerates quieter books (hold time is hours).</summary>
    public double MaxFundingBookAgeMs { get; init; } = 5_000;

    public double MaxFundingPairSkewMs { get; init; } = 2_000;

    // ── Sizing / edge ──────────────────────────────────────────────────────────
    public double MaxNotionalPerTradeQuote { get; init; } = 500;

    public double MinProfitQuote { get; init; } = 0.05;

    public double MinMarginalEdgeBps { get; init; } = 1.0;

    public int MaxLevelsPerLeg { get; init; } = 10;

    public int LiquidityHaircutPpm { get; init; } = 700_000;

    // ── Repair ─────────────────────────────────────────────────────────────────
    public double MaxStopLossBps { get; init; } = 30;

    public double SevereLossBps { get; init; } = 100;

    public IReadOnlyList<double> RepairLadderBps { get; init; } = new[] { 10.0, 30.0, 80.0 };

    public int RepairDeadlineMs { get; init; } = 500;

    // ── Circuit breaker ────────────────────────────────────────────────────────
    public int MaxConsecutiveLosses { get; init; } = 3;

    public double MaxDailyDrawdownPct { get; init; } = 1.0;

    // ── Sanity / wallet ────────────────────────────────────────────────────────
    public double SanityMaxGrossEdgeBps { get; init; } = 300;

    /// <summary>With a closed transfer route, a spread above this is treated as a wallet-suspension trap.</summary>
    public double WalletClosedMaxEdgeBps { get; init; } = 40;

    /// <summary>With a closed route, use at most this fraction of the non-replenishable inventory.</summary>
    public double NonReplenishableInventoryFraction { get; init; } = 0.25;

    public long MaxWalletStatusAgeMs { get; init; } = 300_000;

    // ── Margin / liquidation (funding legs) ────────────────────────────────────
    public double MarginTargetBps { get; init; } = 3_500;

    public double MarginWarnBps { get; init; } = 2_500;

    public double MarginReduceBps { get; init; } = 1_500;

    public double MarginCriticalBps { get; init; } = 800;

    public double MaxLeverage { get; init; } = 3;

    // ── Funding entry ──────────────────────────────────────────────────────────
    public double MaxBreakEvenHours { get; init; } = 16;

    public double MinFundingNetApr { get; init; } = 0.10;

    public double MinEntryLiqDistanceBps { get; init; } = 3_000;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        void Require(bool ok, string message)
        {
            if (!ok)
            {
                errors.Add(message);
            }
        }

        // R-18: every double must be finite — NaN fails ordinary comparisons in some places and passes them in others
        foreach ((string name, double value) in Doubles())
        {
            Require(double.IsFinite(value), name + " must be finite");
        }

        Require(MaxExchangeLatencyMs > 0, "MaxExchangeLatencyMs > 0");
        Require(MaxBookSilenceMs > 0 && MaxConnectionSilenceMs > 0, "silence limits > 0");
        Require(MaxClockUncertaintyMs > 0 && MaxClockUncertaintyMs < MaxExchangeLatencyMs, "0 < MaxClockUncertaintyMs < MaxExchangeLatencyMs");
        Require(MaxSpatialBookAgeMs > 0 && MaxSpatialBookAgeMs <= MaxBookSilenceMs, "0 < MaxSpatialBookAgeMs <= MaxBookSilenceMs");
        Require(MaxFundingBookAgeMs > 0 && MaxFundingBookAgeMs <= MaxBookSilenceMs, "0 < MaxFundingBookAgeMs <= MaxBookSilenceMs");
        Require(MaxSpatialPairSkewMs > 0 && MaxSpatialPairSkewMs <= MaxSpatialBookAgeMs, "0 < MaxSpatialPairSkewMs <= MaxSpatialBookAgeMs");
        Require(MaxFundingPairSkewMs > 0 && MaxFundingPairSkewMs <= MaxFundingBookAgeMs, "0 < MaxFundingPairSkewMs <= MaxFundingBookAgeMs");
        Require(MaxNotionalPerTradeQuote > 0, "MaxNotionalPerTradeQuote > 0");
        Require(MinProfitQuote >= 0, "MinProfitQuote >= 0");
        Require(MinMarginalEdgeBps >= 0, "MinMarginalEdgeBps >= 0");
        Require(MaxLevelsPerLeg is > 0 and <= 200, "MaxLevelsPerLeg 1..200");
        Require(LiquidityHaircutPpm is > 0 and <= 1_000_000, "LiquidityHaircutPpm 1..1_000_000");
        Require(MaxStopLossBps > 0 && SevereLossBps > MaxStopLossBps, "0 < MaxStopLossBps < SevereLossBps");
        Require(RepairLadderBps is { Count: > 0 } && IsStrictlyIncreasingPositive(RepairLadderBps), "RepairLadderBps non-empty, positive, strictly increasing");
        Require(RepairDeadlineMs is > 0 and <= 5_000, "RepairDeadlineMs 1..5000");
        Require(MaxConsecutiveLosses > 0, "MaxConsecutiveLosses > 0");
        Require(MaxDailyDrawdownPct is > 0 and < 100, "MaxDailyDrawdownPct 0..100");
        Require(SanityMaxGrossEdgeBps > WalletClosedMaxEdgeBps && WalletClosedMaxEdgeBps > 0, "0 < WalletClosedMaxEdgeBps < SanityMaxGrossEdgeBps");
        Require(NonReplenishableInventoryFraction is > 0 and <= 1, "NonReplenishableInventoryFraction (0,1]");
        Require(MaxWalletStatusAgeMs > 0, "MaxWalletStatusAgeMs > 0");
        Require(
            MarginTargetBps > MarginWarnBps && MarginWarnBps > MarginReduceBps && MarginReduceBps > MarginCriticalBps && MarginCriticalBps > 0,
            "MarginTarget > Warn > Reduce > Critical > 0");
        Require(MaxLeverage is >= 1 and <= 20, "MaxLeverage 1..20");
        Require(MaxBreakEvenHours > 0, "MaxBreakEvenHours > 0");
        Require(MinFundingNetApr is >= -1 and <= 10, "MinFundingNetApr in [-1, 10] (fraction per year)");
        Require(MinEntryLiqDistanceBps > MarginWarnBps, "MinEntryLiqDistanceBps > MarginWarnBps (entry must start healthy)");
        return errors;
    }

    private IEnumerable<(string Name, double Value)> Doubles()
    {
        yield return (nameof(MaxExchangeLatencyMs), MaxExchangeLatencyMs);
        yield return (nameof(MaxBookSilenceMs), MaxBookSilenceMs);
        yield return (nameof(MaxConnectionSilenceMs), MaxConnectionSilenceMs);
        yield return (nameof(MaxClockUncertaintyMs), MaxClockUncertaintyMs);
        yield return (nameof(MaxSpatialBookAgeMs), MaxSpatialBookAgeMs);
        yield return (nameof(MaxSpatialPairSkewMs), MaxSpatialPairSkewMs);
        yield return (nameof(MaxFundingBookAgeMs), MaxFundingBookAgeMs);
        yield return (nameof(MaxFundingPairSkewMs), MaxFundingPairSkewMs);
        yield return (nameof(MaxNotionalPerTradeQuote), MaxNotionalPerTradeQuote);
        yield return (nameof(MinProfitQuote), MinProfitQuote);
        yield return (nameof(MinMarginalEdgeBps), MinMarginalEdgeBps);
        yield return (nameof(MaxStopLossBps), MaxStopLossBps);
        yield return (nameof(SevereLossBps), SevereLossBps);
        yield return (nameof(MaxDailyDrawdownPct), MaxDailyDrawdownPct);
        yield return (nameof(SanityMaxGrossEdgeBps), SanityMaxGrossEdgeBps);
        yield return (nameof(WalletClosedMaxEdgeBps), WalletClosedMaxEdgeBps);
        yield return (nameof(NonReplenishableInventoryFraction), NonReplenishableInventoryFraction);
        yield return (nameof(MarginTargetBps), MarginTargetBps);
        yield return (nameof(MarginWarnBps), MarginWarnBps);
        yield return (nameof(MarginReduceBps), MarginReduceBps);
        yield return (nameof(MarginCriticalBps), MarginCriticalBps);
        yield return (nameof(MaxLeverage), MaxLeverage);
        yield return (nameof(MaxBreakEvenHours), MaxBreakEvenHours);
        yield return (nameof(MinFundingNetApr), MinFundingNetApr);
        yield return (nameof(MinEntryLiqDistanceBps), MinEntryLiqDistanceBps);
        if (RepairLadderBps is not null)
        {
            for (int i = 0; i < RepairLadderBps.Count; i++)
            {
                yield return (nameof(RepairLadderBps) + "[" + i + "]", RepairLadderBps[i]);
            }
        }
    }

    private static bool IsStrictlyIncreasingPositive(IReadOnlyList<double> values)
    {
        double prev = 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (!(values[i] > prev))
            {
                return false;
            }

            prev = values[i];
        }

        return true;
    }
}
