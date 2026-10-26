using System.Diagnostics;

namespace Arb.Core.Primitives;

/// <summary>
/// Two clocks, never mixed:
///   MonoMicros — monotonic (Stopwatch) for ages, timeouts, latency budgets. Never jumps.
///   UnixMicros — wall clock (NTP/chrony-disciplined) for comparing against venue timestamps.
/// </summary>
public interface IClock
{
    long MonoMicros { get; }

    long UnixMicros { get; }
}

public sealed class SystemClock : IClock
{
    private static readonly long Frequency = Stopwatch.Frequency;
    private static readonly long TicksPerMicro = Stopwatch.Frequency % 1_000_000 == 0 ? Stopwatch.Frequency / 1_000_000 : 0;

    private SystemClock()
    {
    }

    public static SystemClock Instance { get; } = new();

    public long MonoMicros
    {
        get
        {
            long ts = Stopwatch.GetTimestamp();
            return TicksPerMicro != 0 ? ts / TicksPerMicro : (long)((Int128)ts * 1_000_000 / Frequency);
        }
    }

    public long UnixMicros => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
}

/// <summary>Deterministic clock for tests, replay and paper simulation.</summary>
public sealed class ManualClock : IClock
{
    public ManualClock(long monoMicros = 0, long unixMicros = 0)
    {
        MonoMicros = monoMicros;
        UnixMicros = unixMicros;
    }

    public long MonoMicros { get; set; }

    public long UnixMicros { get; set; }

    public void Advance(long micros)
    {
        MonoMicros += micros;
        UnixMicros += micros;
    }
}
