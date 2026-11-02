using Arb.Core.Execution;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-14 (S3), one definition: a trade is flat iff no perp is open AND net delta equals the residual the engine
/// EXPLICITLY accepted as dust (TradeGroup.AcceptResidualAsDust, persisted as trd.trade_group.dust_accepted_base;
/// the DB's is_flat uses the same rule).
/// </summary>
public class R14_DustAcceptanceTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    private TradeGroup Residual8e5()
    {
        var g = new TradeGroup();
        g.Initialize(52, StrategyKind.Spatial, _m.BinanceBtcSpot.Key, _m.BybitBtcSpot.Key, 0.01m, 0);
        g.TransitionTo(TradeState.InFlight, 1);
        g.ApplyFill(_m.BinanceBtcSpot.Key, Side.Buy, 0.01m, 600m, 0m, FeeAssetKind.Quote, 0.6m);
        g.ApplyFill(_m.BybitBtcSpot.Key, Side.Sell, 0.00992m, 595.2m, 0.5952m, FeeAssetKind.Quote, 0.5952m);
        g.TransitionTo(TradeState.Residual, 2);
        g.TransitionTo(TradeState.Repairing, 3);
        return g;
    }

    [Fact]
    public void BelowVenueMinimum_AcceptedAsDust_ThenRepaired()
    {
        TradeGroup g = Residual8e5();
        Assert.Equal(TransitionError.UnacceptedResidual, g.TransitionTo(TradeState.Repaired, 4));
        Assert.False(g.IsFlat(0m));

        Assert.Equal(0.00008m, g.AcceptResidualAsDust());
        Assert.Equal(0.00008m, g.DustAcceptedCanon);
        Assert.True(g.IsFlat(0m));
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Repaired, 4));
    }

    [Fact]
    public void AFillAfterAcceptingDust_MakesTheTradeNotFlatAgain()
    {
        TradeGroup g = Residual8e5();
        g.AcceptResidualAsDust();
        g.ApplyFill(_m.BybitBtcSpot.Key, Side.Sell, 0.00001m, 0.6m, 0m, FeeAssetKind.Quote, 0m); // late fill
        Assert.False(g.IsFlat(0m));
        Assert.Equal(TransitionError.UnacceptedResidual, g.TransitionTo(TradeState.Repaired, 4));
    }

    [Fact]
    public void Dust_CannotHideAnOpenPerp()
    {
        var g = new TradeGroup();
        g.Initialize(53, StrategyKind.Spatial, _m.BitgetBtcSpot.Key, _m.BybitBtcSpot.Key, 0.01m, 0);
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Sell, 0.001m, 60m, 0m, FeeAssetKind.Quote, 0m);
        Assert.Throws<InvalidOperationException>(() => g.AcceptResidualAsDust());
    }

    [Fact]
    public void RepairPlan_ReportsTheDustValue()
    {
        var unwind = new RepairCandidate
        {
            Method = RepairMethod.Unwind,
            Spec = _m.BinanceBtcSpot,
            Available = true,
            FeeRate = 0.001,
            Book = TestMarkets.View(_m.BinanceBtcSpot, new[] { (60000m, 1m) }, new[] { (60001m, 1m) }),
        };
        var policy = new RepairPolicy { MaxStopLossBps = 30, SevereLossBps = 100, LadderBps = new double[] { 10 }, DustCanon = 0.000001 };
        RepairPlan p = RepairPlanner.Plan(0.00008m, 60000, new[] { unwind }, policy, 0, 1);
        Assert.Equal(RepairVerdict.BelowVenueMinimum, p.Verdict);
        Assert.Equal(4.8, p.DustQuote, 1e-9);
    }
}
