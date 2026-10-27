using Arb.Core.Primitives;
using Arb.Core.Risk;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-01 (S1): on a CROSS account several positions share one equity. Distance to liquidation must be computed for
/// the account (every position moving against us at once), not per position against the whole equity.
/// Scenario P11 of the Claude-R review: Bybit UTA, equity 1000 USDT, two funding shorts of 3000 USDT notional each,
/// mmr 1 % + close fee 5.5 bps. Expected values: Python Decimal (prec 40), see docs/review/stage1/REGRESSION_LOG.md.
/// </summary>
public class R01_CrossAccountMarginTests
{
    private const double CloseFee = 0.00055;
    private static readonly MarginTier[] OnePct = { new(double.PositiveInfinity, 0.01, 0) };
    private static readonly MarginTier[] Tiers = { new(1_000, 0.01, 0), new(5_000, 0.02, 10), new(double.PositiveInfinity, 0.05, 160) };
    private static readonly MarginThresholds Thresholds = new(targetBps: 3_500, warnBps: 2_500, reduceBps: 1_500, criticalBps: 800);

    private static AccountPosition[] TwoShorts() => new[]
    {
        new AccountPosition(Side.Sell, 0.05, 60_000, OnePct, CloseFee, CloseFee), // BTC short, 3000 notional
        new AccountPosition(Side.Sell, 1.0, 3_000, OnePct, CloseFee, CloseFee),   // ETH short, 3000 notional
    };

    [Fact]
    public void TwoShortsSharingEquity_AccountDistanceIs1545Bps_WarnZone()
    {
        AccountMarginEstimate e = CrossAccountMargin.Estimate(TwoShorts(), equityQuote: 1_000);
        Assert.True(e.CanLiquidate);
        Assert.Equal(1544.868306037966, e.DistanceBps, 1e-6);

        AccountMarginDecision d = MarginGuard.EvaluateAccount(TwoShorts(), 1_000, Thresholds, freeLocalCollateralQuote: 0);
        Assert.Equal(MarginZone.Warn, d.Zone);                     // 1500 < 1545 <= 2500
        Assert.Equal(MarginAction.RebalanceCollateral, d.Action);  // the per-position view said Healthy / None
        Assert.Equal(1185.455, d.CollateralNeededQuote, 1e-9);
        Assert.Equal(1185.455, d.TransferQuote, 1e-9);
    }

    [Fact]
    public void PerPositionView_OfTheSameAccount_IsTheUnsafeNumber()
    {
        // documents WHY the account API exists: each position alone "sees" the whole 1000 USDT
        var alone = new PerpExposure(Side.Sell, 0.05, 60_000, 1_000, MarginMode.Cross);
        MarginDecision perPosition = MarginGuard.Evaluate(alone, OnePct, CloseFee, CloseFee, Thresholds, 0);
        Assert.Equal(MarginAction.None, perPosition.Action);
        Assert.True(perPosition.Estimate.DistanceBps > 3_000);
        Assert.True(CrossAccountMargin.Estimate(TwoShorts(), 1_000).DistanceBps < 1_600);
    }

    [Fact]
    public void AddMargin_FromLocalCollateral_RestoresTargetForTheWholeAccount()
    {
        AccountMarginDecision d = MarginGuard.EvaluateAccount(TwoShorts(), 1_000, Thresholds, freeLocalCollateralQuote: 5_000);
        Assert.Equal(MarginAction.AddMargin, d.Action);
        Assert.Equal(1185.455, d.AddMarginQuote, 1e-9);
        AccountMarginEstimate after = CrossAccountMargin.Estimate(TwoShorts(), 1_000 + d.AddMarginQuote);
        Assert.Equal(3_500, after.DistanceBps, 1e-6);
    }

    [Fact]
    public void ReduceZone_WithoutCollateral_ReducesEveryPairProRata_AndRestoresTarget()
    {
        AccountMarginDecision d = MarginGuard.EvaluateAccount(TwoShorts(), 600, Thresholds, freeLocalCollateralQuote: 0);
        Assert.Equal(885.1615456929395, d.Estimate.DistanceBps, 1e-6);
        Assert.Equal(MarginZone.Reduce, d.Zone);
        Assert.Equal(MarginAction.ReducePair, d.Action);
        Assert.Equal(0.7265547131161627, d.ReduceFraction, 1e-9);

        // execute it: keep (1 − f) of every position, pay the reduce fee on the closed part
        double keep = 1 - d.ReduceFraction;
        AccountPosition[] reduced =
        {
            new(Side.Sell, 0.05 * keep, 60_000, OnePct, CloseFee, CloseFee),
            new(Side.Sell, 1.0 * keep, 3_000, OnePct, CloseFee, CloseFee),
        };
        double equityAfter = 600 - (6_000 * d.ReduceFraction * CloseFee);
        Assert.True(CrossAccountMargin.Estimate(reduced, equityAfter).DistanceBps >= 3_500 - 1e-6);
    }

    [Fact]
    public void CriticalZone_FlattensEveryPair()
    {
        AccountMarginDecision d = MarginGuard.EvaluateAccount(TwoShorts(), 450, Thresholds, freeLocalCollateralQuote: 10_000);
        Assert.Equal(637.7715105635545, d.Estimate.DistanceBps, 1e-6);
        Assert.Equal(MarginZone.Critical, d.Zone);
        Assert.Equal(MarginAction.FlattenPair, d.Action);
        Assert.Equal(1.0, d.ReduceFraction);
    }

    [Fact]
    public void MixedLongAndShort_UsesEveryLegsAdverseDirection()
    {
        AccountPosition[] mixed =
        {
            new(Side.Buy, 0.05, 60_000, OnePct, CloseFee, CloseFee),
            new(Side.Sell, 1.0, 3_000, OnePct, CloseFee, CloseFee),
        };
        Assert.Equal(1561.1666666666667, CrossAccountMargin.Estimate(mixed, 1_000).DistanceBps, 1e-6);
    }

    [Fact]
    public void SinglePositionAccount_EqualsTheClosedFormPerPositionEstimate()
    {
        var pepeShort = new PerpExposure(Side.Sell, 100_000_000, 0.000012, 400, MarginMode.Cross);
        double closedForm = LiquidationCalculator.Estimate(pepeShort, Tiers, CloseFee).DistanceBps;
        AccountPosition[] one = { new(Side.Sell, 100_000_000, 0.000012, Tiers, CloseFee, CloseFee) };
        Assert.Equal(closedForm, CrossAccountMargin.Estimate(one, 400).DistanceBps, 1e-6);

        var btcLong = new PerpExposure(Side.Buy, 0.02, 60_000, 400, MarginMode.Cross);
        MarginTier[] half = { new(double.PositiveInfinity, 0.005, 0) };
        double longClosed = LiquidationCalculator.Estimate(btcLong, half, CloseFee).DistanceBps;
        AccountPosition[] oneLong = { new(Side.Buy, 0.02, 60_000, half, CloseFee, CloseFee) };
        Assert.Equal(longClosed, CrossAccountMargin.Estimate(oneLong, 400).DistanceBps, 1e-6);
    }

    [Fact]
    public void UnleveredLongOnlyAccount_CannotLiquidate_AndEmptyAccountIsHealthy()
    {
        AccountPosition[] unlevered = { new(Side.Buy, 0.02, 60_000, OnePct, CloseFee, CloseFee) };
        AccountMarginEstimate e = CrossAccountMargin.Estimate(unlevered, 1_300);
        Assert.False(e.CanLiquidate);
        Assert.True(double.IsPositiveInfinity(e.DistanceBps));

        AccountMarginDecision none = MarginGuard.EvaluateAccount(ReadOnlySpan<AccountPosition>.Empty, 100, Thresholds, 0);
        Assert.Equal(MarginAction.None, none.Action);
        Assert.Equal(MarginZone.Healthy, none.Zone);
    }

    [Fact]
    public void NegativeEquity_IsCriticalAtZeroDistance()
    {
        AccountMarginEstimate e = CrossAccountMargin.Estimate(TwoShorts(), -5);
        Assert.Equal(0, e.DistanceBps);
        Assert.Equal(MarginAction.FlattenPair, MarginGuard.EvaluateAccount(TwoShorts(), -5, Thresholds, 0).Action);
    }
}
