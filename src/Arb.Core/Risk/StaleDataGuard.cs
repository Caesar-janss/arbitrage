using Arb.Core.MarketData;
using Arb.Core.Primitives;

namespace Arb.Core.Risk;

public enum Freshness : byte
{
    Fresh = 0,
    BookNotUsable = 1,
    ConnectionSilent = 2,
    ClockUncertain = 3,
    ExchangeLatency = 4,
    BookSilent = 5,

    /// <summary>Older than the strategy's max book age (a stalled depth stream on a live socket looks quiet, not dead).</summary>
    BookTooOld = 6,

    /// <summary>Both books fresh on their own, but received too far apart to be compared (cross-book skew).</summary>
    PairSkew = 7,
}

/// <summary>Estimated venue clock offset (venue − local), from ClockOffsetEstimator (min-RTT filtered).</summary>
public readonly struct VenueClock
{
    public VenueClock(double offsetMs, double uncertaintyMs, bool isValid)
    {
        OffsetMs = offsetMs;
        UncertaintyMs = uncertaintyMs;
        IsValid = isValid;
    }

    public double OffsetMs { get; }

    public double UncertaintyMs { get; }

    public bool IsValid { get; }
}

public readonly struct FreshnessLimits
{
    /// <summary>Venue event timestamps have millisecond resolution: a −1 ms "latency" is rounding, not a clock error.</summary>
    public const double TimestampResolutionMs = 1.0;

    public FreshnessLimits(
        double maxExchangeLatencyMs,
        double maxBookSilenceMs,
        double maxConnectionSilenceMs,
        double maxClockUncertaintyMs,
        double maxBookAgeMs,
        double maxPairSkewMs)
    {
        MaxExchangeLatencyMs = maxExchangeLatencyMs;
        MaxBookSilenceMs = maxBookSilenceMs;
        MaxConnectionSilenceMs = maxConnectionSilenceMs;
        MaxClockUncertaintyMs = maxClockUncertaintyMs;
        MaxBookAgeMs = maxBookAgeMs;
        MaxPairSkewMs = maxPairSkewMs;
    }

    public double MaxExchangeLatencyMs { get; }

    public double MaxBookSilenceMs { get; }

    public double MaxConnectionSilenceMs { get; }

    public double MaxClockUncertaintyMs { get; }

    /// <summary>Strategy-specific max age of the last book update (spatial: hundreds of ms; funding: seconds).</summary>
    public double MaxBookAgeMs { get; }

    /// <summary>Max |receive time A − receive time B| for a pair decision.</summary>
    public double MaxPairSkewMs { get; }

    /// <summary>STRICTEST limits (spatial). Use <see cref="From(RiskLimits, StrategyKind)"/> to be explicit.</summary>
    public static FreshnessLimits From(RiskLimits limits) => From(limits, StrategyKind.Spatial);

    public static FreshnessLimits From(RiskLimits limits, StrategyKind strategy)
    {
        ArgumentNullException.ThrowIfNull(limits);
        bool spatial = strategy == StrategyKind.Spatial;
        return new FreshnessLimits(
            limits.MaxExchangeLatencyMs,
            limits.MaxBookSilenceMs,
            limits.MaxConnectionSilenceMs,
            limits.MaxClockUncertaintyMs,
            spatial ? limits.MaxSpatialBookAgeMs : limits.MaxFundingBookAgeMs,
            spatial ? limits.MaxSpatialPairSkewMs : limits.MaxFundingPairSkewMs);
    }
}

/// <summary>
/// Stale-data / latency guard, ordered by severity:
///   1. integrity (book not Synced: seq gap, checksum, crossed) — corrupt-but-fresh is the worst case
///   2. connection liveness (any frame incl. pong) — a frozen socket looks like a quiet market
///   3. clock offset known — without it "exchangeTs vs localTs &gt; 150 ms" compares two different clocks
///   4. transport latency of the last update, offset-corrected; NEGATIVE beyond clock uncertainty + 1 ms = the offset
///      estimate is wrong → ClockUncertain (R-07: −500 ms used to pass)
///   5. book silence (event-driven feeds: quiet ≠ stale, so this limit is generous / per-instrument)
///   6. strategy max book age (spatial must price on books updated within ~1 s even if the socket is alive)
///   pair: both legs, then cross-book receive skew
/// </summary>
public static class StaleDataGuard
{
    public static Freshness Check(
        BookView book,
        long nowMonoMicros,
        long connectionLastFrameMonoMicros,
        in VenueClock clock,
        in FreshnessLimits limits,
        out double transportLatencyMs)
    {
        ArgumentNullException.ThrowIfNull(book);
        transportLatencyMs = double.NaN;
        if (!book.IsUsable)
        {
            return Freshness.BookNotUsable;
        }

        if ((nowMonoMicros - connectionLastFrameMonoMicros) / 1000.0 > limits.MaxConnectionSilenceMs)
        {
            return Freshness.ConnectionSilent;
        }

        if (!clock.IsValid || clock.UncertaintyMs > limits.MaxClockUncertaintyMs)
        {
            return Freshness.ClockUncertain;
        }

        BookStamp s = book.Stamp;
        double exchangeInLocalMs = (s.ExchangeUnixMicros / 1000.0) - clock.OffsetMs;
        transportLatencyMs = (s.LocalRecvUnixMicros / 1000.0) - exchangeInLocalMs;
        if (!(transportLatencyMs >= -(clock.UncertaintyMs + FreshnessLimits.TimestampResolutionMs)))
        {
            return Freshness.ClockUncertain; // event "arrived before it happened": offset estimate is wrong
        }

        if (!(transportLatencyMs <= limits.MaxExchangeLatencyMs))
        {
            return Freshness.ExchangeLatency;
        }

        double ageMs = (nowMonoMicros - s.LocalRecvMonoMicros) / 1000.0;
        if (!(ageMs <= limits.MaxBookSilenceMs))
        {
            return Freshness.BookSilent;
        }

        if (!(ageMs <= limits.MaxBookAgeMs))
        {
            return Freshness.BookTooOld;
        }

        return Freshness.Fresh;
    }

    /// <summary>Both legs must be fresh (first failing check, leg A first) and received within MaxPairSkewMs of each other.</summary>
    public static Freshness CheckPair(
        BookView a,
        long connectionLastFrameA,
        in VenueClock clockA,
        BookView b,
        long connectionLastFrameB,
        in VenueClock clockB,
        long nowMonoMicros,
        in FreshnessLimits limits,
        out double worstLatencyMs)
    {
        Freshness fa = Check(a, nowMonoMicros, connectionLastFrameA, clockA, limits, out double la);
        Freshness fb = Check(b, nowMonoMicros, connectionLastFrameB, clockB, limits, out double lb);
        worstLatencyMs = double.IsNaN(la) ? lb : double.IsNaN(lb) ? la : Math.Max(la, lb);
        if (fa != Freshness.Fresh)
        {
            return fa;
        }

        if (fb != Freshness.Fresh)
        {
            return fb;
        }

        double skewMs = Math.Abs(a.Stamp.LocalRecvMonoMicros - b.Stamp.LocalRecvMonoMicros) / 1000.0;
        return skewMs <= limits.MaxPairSkewMs ? Freshness.Fresh : Freshness.PairSkew;
    }
}
