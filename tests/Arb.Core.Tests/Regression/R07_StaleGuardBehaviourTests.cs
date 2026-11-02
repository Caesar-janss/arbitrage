using Arb.Core.MarketData;
using Arb.Core.Primitives;
using Arb.Core.Risk;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-07 (S2), behaviour on the pre-existing API (scenario P08, default RiskLimits): a depth stream frozen for 4.9 s on
/// a live socket, two books 4.9 s apart, and a NEGATIVE transport latency (−500 ms with a claimed ±2 ms clock) were all
/// reported Fresh. FreshnessLimits.From(limits) keeps working and now means the STRICTEST (spatial) limits.
/// </summary>
public class R07_StaleGuardBehaviourTests
{
    private const long T = 5_000_000_000;
    private static readonly InstrumentKey Key = new(Venue.Bybit, MarketType.Spot, 1);
    private static readonly VenueClock Clock = new(0, 2, true);
    private static readonly FreshnessLimits Limits = FreshnessLimits.From(new RiskLimits());

    internal static BookView Book(long exch, long recvUnix, long recvMono)
    {
        var v = new BookView(4);
        v.Load(Key, new[] { new PriceLevel(100, 1) }, new[] { new PriceLevel(101, 1) }, new BookStamp(1, exch, recvUnix, recvMono));
        return v;
    }

    [Fact]
    public void BookFrozen4900ms_WithLiveSocket_IsNotFresh()
    {
        BookView stale = Book(T, T + 1_000, T + 1_000);
        Freshness f = StaleDataGuard.Check(stale, T + 4_901_000, T + 4_891_000, Clock, Limits, out _);
        Assert.True(f != Freshness.Fresh, $"4.9 s old book on a live socket reported {f}");
    }

    [Fact]
    public void PairWithOneBook4900msOlder_IsNotFresh()
    {
        BookView fresh = Book(T + 4_899_000, T + 4_900_000, T + 4_900_000);
        BookView stale = Book(T, T + 1_000, T + 1_000);
        Freshness f = StaleDataGuard.CheckPair(fresh, T + 4_901_000, Clock, stale, T + 4_891_000, Clock, T + 4_901_000, Limits, out _);
        Assert.True(f != Freshness.Fresh, $"pair skew 4.9 s reported {f}");
    }

    [Fact]
    public void NegativeLatency_BeyondClockUncertainty_IsClockUncertain()
    {
        BookView future = Book(T + 500_000, T, T); // venue stamp 500 ms AFTER local receive
        Assert.Equal(Freshness.ClockUncertain, StaleDataGuard.Check(future, T + 1_000, T + 1_000, Clock, Limits, out double lat));
        Assert.Equal(-500, lat, 1e-9);
    }
}
