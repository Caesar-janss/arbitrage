using Arb.Core.MarketData;
using Arb.Core.Primitives;
using Arb.Core.Risk;

namespace Arb.Core.Tests.Regression;

/// <summary>R-07 (S2), new limits: per-strategy max book age and a cross-book skew limit.</summary>
public class R07_StaleGuardLimitsTests
{
    private const long T = 5_000_000_000;
    private static readonly VenueClock Clock = new(0, 2, true);
    private static readonly RiskLimits Defaults = new();

    [Fact]
    public void SpatialAndFunding_HaveDifferentAgeLimits()
    {
        FreshnessLimits spatial = FreshnessLimits.From(Defaults, StrategyKind.Spatial);
        FreshnessLimits funding = FreshnessLimits.From(Defaults, StrategyKind.FundingPerpPerp);
        BookView stale = R07_StaleGuardBehaviourTests.Book(T, T + 1_000, T + 1_000);
        Assert.Equal(Freshness.BookTooOld, StaleDataGuard.Check(stale, T + 4_901_000, T + 4_891_000, Clock, spatial, out _));
        Assert.Equal(Freshness.Fresh, StaleDataGuard.Check(stale, T + 4_901_000, T + 4_891_000, Clock, funding, out _));
        Assert.Equal(Defaults.MaxSpatialBookAgeMs, spatial.MaxBookAgeMs);
        Assert.Equal(FreshnessLimits.From(Defaults).MaxBookAgeMs, spatial.MaxBookAgeMs); // From(limits) = strictest
    }

    [Fact]
    public void BothBooksYoungerThanTheAgeLimit_ButFarApart_IsPairSkew()
    {
        FreshnessLimits spatial = FreshnessLimits.From(Defaults, StrategyKind.Spatial);
        BookView a = R07_StaleGuardBehaviourTests.Book(T + 800_000, T + 801_000, T + 801_000);  // 1 ms old
        BookView b = R07_StaleGuardBehaviourTests.Book(T + 1_000, T + 2_000, T + 2_000);        // 800 ms old
        Assert.Equal(Freshness.Fresh, StaleDataGuard.Check(b, T + 802_000, T + 802_000, Clock, spatial, out _));
        Assert.Equal(Freshness.PairSkew, StaleDataGuard.CheckPair(a, T + 802_000, Clock, b, T + 802_000, Clock, T + 802_000, spatial, out _));
    }

    [Fact]
    public void SmallNegativeLatency_WithinUncertaintyPlusTimestampResolution_IsFresh()
    {
        FreshnessLimits spatial = FreshnessLimits.From(Defaults, StrategyKind.Spatial);
        BookView b = R07_StaleGuardBehaviourTests.Book(T + 2_500, T, T); // −2.5 ms with ±2 ms clock + 1 ms venue ts resolution
        Assert.Equal(Freshness.Fresh, StaleDataGuard.Check(b, T + 1_000, T + 1_000, Clock, spatial, out _));
        BookView c = R07_StaleGuardBehaviourTests.Book(T + 3_500, T, T); // −3.5 ms
        Assert.Equal(Freshness.ClockUncertain, StaleDataGuard.Check(c, T + 1_000, T + 1_000, Clock, spatial, out _));
    }

    [Fact]
    public void Validate_RejectsInconsistentFreshnessLimits()
    {
        var bad = new RiskLimits { MaxSpatialBookAgeMs = 0, MaxSpatialPairSkewMs = -1, MaxFundingBookAgeMs = 60_000 };
        IReadOnlyList<string> errors = bad.Validate();
        Assert.True(errors.Count >= 3, string.Join("; ", errors));
    }
}
