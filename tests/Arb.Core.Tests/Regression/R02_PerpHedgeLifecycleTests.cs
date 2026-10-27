using Arb.Core.Execution;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-02 (S1), exposure half (scenario P18): buy 0.01 BTC on Bitget filled, sell on Bybit missed, repair = short 0.01 BTC
/// on the Bybit perp. Net delta is 0 but TWO gross positions stay open (spot long + perp short: funding, liquidation,
/// later unwind). The trade group must keep owning them: no terminal state while a perp position is open.
/// </summary>
public class R02_PerpHedgeLifecycleTests
{
    private static readonly RepairPolicy Policy = new() { MaxStopLossBps = 30, SevereLossBps = 100, LadderBps = new double[] { 10, 30, 80 }, DustCanon = 0.000001 };
    private readonly TestMarkets _m = TestMarkets.Create();

    private TradeGroup HedgedSpatial()
    {
        var hedge = new RepairCandidate
        {
            Method = RepairMethod.PerpHedge,
            Spec = _m.BybitBtcPerp,
            Available = true,
            FeeRate = 0.00055,
            ExtraCostBps = 12,
            Book = TestMarkets.View(_m.BybitBtcPerp, new[] { (59990.0m, 0.5m) }, new[] { (59990.1m, 0.5m) }),
        };
        var g = new TradeGroup();
        g.Initialize(42, StrategyKind.Spatial, _m.BitgetBtcSpot.Key, _m.BybitBtcSpot.Key, 0.01m, 0);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.InFlight, 1));
        g.ApplyFill(_m.BitgetBtcSpot.Key, Side.Buy, 0.01m, 600m, 0m, FeeAssetKind.DiscountToken, 0.48m);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Residual, 2));
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Repairing, 3));

        RepairPlan p = RepairPlanner.Plan(g.NetBaseDelta, 60000, new[] { hedge }, Policy, 0, 7);
        Assert.Equal(RepairMethod.PerpHedge, p.Method);
        Assert.Equal(_m.BybitBtcPerp.Key, p.Order.Instrument);
        g.ApplyFill(p.Order.Instrument, Side.Sell, _m.BybitBtcPerp.CanonicalQtyExact(p.Order.Lots), 599.9m, 0.33m, FeeAssetKind.Quote, 0.33m);
        return g;
    }

    [Fact]
    public void AfterPerpHedge_DeltaIsZero_ButTheTradeIsNotFlat()
    {
        TradeGroup g = HedgedSpatial();
        Assert.Equal(0m, g.NetBaseDelta);
        Assert.True(g.HasOpenPerpPosition);
        Assert.Equal(-0.01m, g.PerpPositionCanon(_m.BybitBtcPerp.Key));
        Assert.False(g.IsFlat(0.000001m));
        Assert.True(g.IsDeltaNeutral(0.000001m));
    }

    [Fact]
    public void AfterPerpHedge_TerminalRepaired_IsRefused_AndStateIsUnchanged()
    {
        TradeGroup g = HedgedSpatial();
        Assert.Equal(TransitionError.OpenDerivativePosition, g.TransitionTo(TradeState.Repaired, 4));
        Assert.Equal(TradeState.Repairing, g.State.State);
        Assert.Equal(3, g.State.Version);
    }

    [Fact]
    public void HedgedResidual_IsOwnedAsPositionOpen_UntilBothLegsAreUnwound()
    {
        TradeGroup g = HedgedSpatial();
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 4));
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Exiting, 5));

        g.ApplyFill(_m.BitgetBtcSpot.Key, Side.Sell, 0.01m, 600.2m, 0.48m, FeeAssetKind.Quote, 0.48m);
        Assert.Equal(TransitionError.OpenDerivativePosition, g.TransitionTo(TradeState.Closed, 6)); // perp still short
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Buy, 0.01m, 599.95m, 0.33m, FeeAssetKind.Quote, 0.33m);

        Assert.False(g.HasOpenPerpPosition);
        Assert.True(g.IsFlat(0m));
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Closed, 6));
        Assert.True(g.State.State.IsTerminal());
    }

    [Fact]
    public void Spatial_PositionOpen_WithoutAPerpHedge_IsRefused()
    {
        var g = new TradeGroup();
        g.Initialize(43, StrategyKind.Spatial, _m.BitgetBtcSpot.Key, _m.BybitBtcSpot.Key, 0.01m, 0);
        g.TransitionTo(TradeState.InFlight, 1);
        g.ApplyFill(_m.BitgetBtcSpot.Key, Side.Buy, 0.01m, 600m, 0m, FeeAssetKind.DiscountToken, 0.48m);
        g.TransitionTo(TradeState.Residual, 2);
        g.TransitionTo(TradeState.Repairing, 3);
        Assert.Equal(TransitionError.NotHedged, g.TransitionTo(TradeState.PositionOpen, 4));
        Assert.Equal(TradeState.Repairing, g.State.State);
    }

    [Fact]
    public void Funding_Closed_IsRefused_WhileOnePerpLegIsStillOpen()
    {
        var g = new TradeGroup();
        g.Initialize(44, StrategyKind.FundingPerpPerp, _m.BybitPepePerp.Key, _m.BinancePepePerp.Key, 100_000_000m, 0);
        g.TransitionTo(TradeState.InFlight, 1);
        g.ApplyFill(_m.BybitPepePerp.Key, Side.Buy, 100_000_000m, 1200m, 0.66m, FeeAssetKind.Quote, 0.66m);
        g.ApplyFill(_m.BinancePepePerp.Key, Side.Sell, 100_000_000m, 1200.6m, 0.54m, FeeAssetKind.DiscountToken, 0.54m);
        Assert.True(g.IsDeltaNeutral(0m));
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 2));
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Exiting, 3));
        g.ApplyFill(_m.BybitPepePerp.Key, Side.Sell, 100_000_000m, 1195m, 0.66m, FeeAssetKind.Quote, 0.66m);
        Assert.False(g.IsDeltaNeutral(0m));
        Assert.Equal(TransitionError.OpenDerivativePosition, g.TransitionTo(TradeState.Closed, 4));
        g.ApplyFill(_m.BinancePepePerp.Key, Side.Buy, 100_000_000m, 1195.3m, 0.54m, FeeAssetKind.DiscountToken, 0.54m);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Closed, 4));
    }

    [Fact]
    public void Fill_OnAnInstrumentOutsideTheTrade_SpotIsRejected_PerpIsTrackedAsHedge()
    {
        var g = new TradeGroup();
        g.Initialize(45, StrategyKind.Spatial, _m.BitgetBtcSpot.Key, _m.BybitBtcSpot.Key, 0.01m, 0);
        Assert.Throws<ArgumentException>(() => g.ApplyFill(_m.BinanceBtcSpot.Key, Side.Buy, 0.01m, 600m, 0m, FeeAssetKind.Quote, 0m));
        Assert.Throws<ArgumentException>(() => g.ApplyFill(default, Side.Buy, 0.01m, 600m, 0m, FeeAssetKind.Quote, 0m));
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Sell, 0.01m, 600m, 0m, FeeAssetKind.Quote, 0m);
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Buy, 0.004m, 240m, 0m, FeeAssetKind.Quote, 0m);
        Assert.Equal(-0.006m, g.PerpPositionCanon(_m.BybitBtcPerp.Key));
        Assert.Equal(0m, g.PerpPositionCanon(_m.BinancePepePerp.Key));
    }
}
