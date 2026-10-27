using Arb.Core.MarketData;

namespace Arb.Core.Risk;

public enum Freshness : byte
{
    Fresh = 0,
    BookNotUsable = 1,
    ConnectionSilent = 2,
    ClockUncertain = 3,
    ExchangeLatency = 4,
    BookSilent = 5,
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
    public FreshnessLimits(double maxExchangeLatencyMs, double maxBookSilenceMs, double maxConnectionSilenceMs, double maxClockUncertaintyMs)
    {
        MaxExchangeLatencyMs = maxExchangeLatencyMs;
        MaxBookSilenceMs = maxBookSilenceMs;
        MaxConnectionSilenceMs = maxConnectionSilenceMs;
        MaxClockUncertaintyMs = maxClockUncertaintyMs;
    }

    public double MaxExchangeLatencyMs { get; }

    public double MaxBookSilenceMs { get; }

    public double MaxConnectionSilenceMs { get; }

    public double MaxClockUncertaintyMs { get; }

    public static FreshnessLimits From(RiskLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return new FreshnessLimits(limits.MaxExchangeLatencyMs, limits.MaxBookSilenceMs, limits.MaxConnectionSilenceMs, limits.MaxClockUncertaintyMs);
    }
}

/// <summary>
/// Stale-data / latency guard, ordered by severity:
///   1. integrity (book not Synced: seq gap, checksum, crossed) — corrupt-but-fresh is the worst case
///   2. connection liveness (any frame incl. pong) — a frozen socket looks like a quiet market
///   3. clock offset known — without it "exchangeTs vs localTs &gt; 150 ms" compares two different clocks
///   4. transport latency of the last update, offset-corrected
///   5. book silence (event-driven feeds: quiet ≠ stale, so this limit is generous / per-instrument)
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
        if (transportLatencyMs > limits.MaxExchangeLatencyMs)
        {
            return Freshness.ExchangeLatency;
        }

        if ((nowMonoMicros - s.LocalRecvMonoMicros) / 1000.0 > limits.MaxBookSilenceMs)
        {
            return Freshness.BookSilent;
        }

        return Freshness.Fresh;
    }

    /// <summary>Both legs must be fresh; returns the first failing check (leg A first).</summary>
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
        return fa != Freshness.Fresh ? fa : fb;
    }
}
