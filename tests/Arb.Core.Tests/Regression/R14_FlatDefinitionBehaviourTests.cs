using Arb.Core.Execution;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-14 (S3), behaviour on the pre-existing API: "flat" had three definitions (TradeGroup dust param, RepairPlanner's
/// venue minimum, DB 1.0 USDT). A residual of 0.00008 BTC (4.8 USDT, below Binance's 5 USDT minimum, P13) could be
/// moved to terminal REPAIRED by C# without any record that it was written off — the DB then never saw it as flat.
/// A terminal state must not accept an unaccounted residual.
/// </summary>
public class R14_FlatDefinitionBehaviourTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    [Fact]
    public void Terminal_WithAnUnaccountedResidual_IsRefused()
    {
        var g = new TradeGroup();
        g.Initialize(51, StrategyKind.Spatial, _m.BinanceBtcSpot.Key, _m.BybitBtcSpot.Key, 0.01m, 0);
        g.TransitionTo(TradeState.InFlight, 1);
        g.ApplyFill(_m.BinanceBtcSpot.Key, Side.Buy, 0.01m, 600m, 0m, FeeAssetKind.Quote, 0.6m);
        g.ApplyFill(_m.BybitBtcSpot.Key, Side.Sell, 0.00992m, 595.2m, 0.5952m, FeeAssetKind.Quote, 0.5952m);
        g.TransitionTo(TradeState.Residual, 2);
        g.TransitionTo(TradeState.Repairing, 3);

        var unwind = new RepairCandidate
        {
            Method = RepairMethod.Unwind,
            Spec = _m.BinanceBtcSpot,
            Available = true,
            FeeRate = 0.001,
            Book = TestMarkets.View(_m.BinanceBtcSpot, new[] { (60000m, 1m) }, new[] { (60001m, 1m) }),
        };
        var policy = new RepairPolicy { MaxStopLossBps = 30, SevereLossBps = 100, LadderBps = new double[] { 10 }, DustCanon = 0.000001 };
        Assert.Equal(RepairVerdict.BelowVenueMinimum, RepairPlanner.Plan(g.NetBaseDelta, 60000, new[] { unwind }, policy, 0, 1).Verdict);

        TransitionError e = g.TransitionTo(TradeState.Repaired, 4);
        Assert.True(e != TransitionError.None, "REPAIRED accepted with 0.00008 BTC unaccounted"); // v0.2: None
        Assert.Equal(TradeState.Repairing, g.State.State);
    }
}
