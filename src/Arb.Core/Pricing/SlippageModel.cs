namespace Arb.Core.Pricing;

/// <summary>
/// Time-decayed EWMA of mid-price variance per millisecond, sampled on a minimum interval to suppress
/// microstructure noise (bid/ask bounce). σ over a horizon L = sqrt(var/ms × L).
/// Mutable struct: keep it in an array/field and update via ref (one owner thread per instrument).
/// </summary>
public struct EwmaVolatility
{
    private readonly double _halfLifeMs;
    private readonly double _minSampleMs;
    private double _varPerMs;
    private double _lastMid;
    private long _lastMicros;
    private int _samples;

    public EwmaVolatility(double halfLifeMs, double minSampleMs, double initialVarPerMs = 0)
    {
        if (!(halfLifeMs > 0) || !(minSampleMs > 0) || initialVarPerMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(halfLifeMs), "half-life and sample interval must be > 0");
        }

        _halfLifeMs = halfLifeMs;
        _minSampleMs = minSampleMs;
        _varPerMs = initialVarPerMs;
        _lastMid = 0;
        _lastMicros = 0;
        _samples = 0;
    }

    public readonly int Samples => _samples;

    public readonly double VarPerMs => _varPerMs;

    public readonly bool IsWarm(int minSamples) => _samples >= minSamples;

    public void Update(double mid, long monoMicros)
    {
        if (!(mid > 0))
        {
            return;
        }

        if (_lastMid <= 0)
        {
            _lastMid = mid;
            _lastMicros = monoMicros;
            return;
        }

        double dtMs = (monoMicros - _lastMicros) / 1000.0;
        if (dtMs < _minSampleMs)
        {
            return;
        }

        double r = Math.Log(mid / _lastMid);
        double inst = r * r / dtMs;
        double alpha = 1 - Math.Exp(-dtMs * Math.Log(2) / _halfLifeMs);
        _varPerMs += alpha * (inst - _varPerMs);
        _lastMid = mid;
        _lastMicros = monoMicros;
        _samples++;
    }

    /// <summary>Std-dev of log return over <paramref name="horizonMs"/> (fraction, 0.0001 = 1 bp).</summary>
    public readonly double SigmaOver(double horizonMs) => Math.Sqrt(_varPerMs * Math.Max(0, horizonMs));
}

/// <summary>
/// Slippage buffer per leg = base + k × σ(horizon), capped. Horizon = how stale the book was when we decided
/// + one-way order latency to that venue. Consequence for a Tokyo host: a Singapore venue leg (~35 ms each way)
/// automatically carries a larger buffer than a Tokyo venue leg (~1-3 ms).
/// </summary>
public sealed class SlippageModel
{
    public SlippageModel(double baseBps, double volMultiplier, double maxBps, double coldStartBps, int minWarmSamples)
    {
        if (baseBps < 0 || volMultiplier < 0 || maxBps < baseBps || coldStartBps < 0 || minWarmSamples < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseBps), "invalid slippage parameters");
        }

        BaseBps = baseBps;
        VolMultiplier = volMultiplier;
        MaxBps = maxBps;
        ColdStartBps = coldStartBps;
        MinWarmSamples = minWarmSamples;
    }

    public double BaseBps { get; }

    public double VolMultiplier { get; }

    public double MaxBps { get; }

    public double ColdStartBps { get; }

    public int MinWarmSamples { get; }

    public static double HorizonMs(double dataAgeMs, double oneWayOrderLatencyMs) =>
        Math.Max(0, dataAgeMs) + Math.Max(0, oneWayOrderLatencyMs);

    public double LegBps(in EwmaVolatility vol, double horizonMs)
    {
        if (!vol.IsWarm(MinWarmSamples))
        {
            return ColdStartBps;
        }

        double bps = BaseBps + (VolMultiplier * vol.SigmaOver(horizonMs) * 1e4);
        return Math.Min(MaxBps, bps);
    }
}
