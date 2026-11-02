using Arb.Core.Execution;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Risk;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// C-10 (S1), new API: the holding tolerance is part of the trade (Initialize), not an optional host call. It comes
/// from a risk budget in quote (<see cref="RiskLimits.MaxHoldingDeltaQuote"/> → <see cref="TradeGroup.HoldingDeltaTolerance"/>),
/// never from "one perp lot is fine": BTC perp lot 0.001 ≈ 60 USDT, far above a 1 USDT budget.
/// </summary>
public class C10_HoldingDeltaToleranceTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    private TradeGroup Hedged(decimal maxHoldingDeltaCanon, decimal baseFee, decimal hedgeQty)
    {
        var g = new TradeGroup();
        g.Initialize(46, StrategyKind.Spatial, _m.BitgetBtcSpot.Key, _m.BybitBtcSpot.Key, 0.01m, 0, maxHoldingDeltaCanon);
        g.TransitionTo(TradeState.InFlight, 1);
        g.ApplyFill(_m.BitgetBtcSpot.Key, Side.Buy, 0.01m, 600m, baseFee, FeeAssetKind.Base, baseFee * 60000m);
        g.TransitionTo(TradeState.Residual, 2);
        g.TransitionTo(TradeState.Repairing, 3);
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Sell, hedgeQty, hedgeQty * 59990m, 0.33m, FeeAssetKind.Quote, 0.33m);
        return g;
    }

    [Fact]
    public void UnbalancedHolding_IsRefusedWithItsOwnCode_AndStateIsUnchanged()
    {
        TradeGroup g = Hedged(0m, 0m, 0.006m);
        Assert.Equal(TransitionError.UnhedgedDelta, g.TransitionTo(TradeState.PositionOpen, 4));
        Assert.Equal(TradeState.Repairing, g.State.State);
        Assert.Equal(3, g.State.Version);
    }

    [Fact]
    public void NoPerpAtAll_IsStillNotHedged()
    {
        TradeGroup g = Hedged(1m, 0m, 0.006m);
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Buy, 0.006m, 359.9m, 0.2m, FeeAssetKind.Quote, 0.2m);
        Assert.Equal(TransitionError.NotHedged, g.TransitionTo(TradeState.PositionOpen, 4));
    }

    [Fact]
    public void BaseFeeDust_WithinTheRiskBudget_IsAccepted()
    {
        decimal tol = TradeGroup.HoldingDeltaTolerance(maxHoldingDeltaQuote: 1.0, referencePx: 60_000);
        Assert.Equal(0.000016666666m, tol);                                  // 1 USDT / 60,000, truncated to 1e-12
        TradeGroup g = Hedged(tol, 0.00001m, 0.01m);                         // holds 0.00999, short 0.01 → −0.00001
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 4));
        Assert.Equal(tol, g.MaxHoldingDeltaCanon);
    }

    [Fact]
    public void ToleranceIsInclusive_AndExact()
    {
        Assert.Equal(TransitionError.None, Hedged(0.00001m, 0.00001m, 0.01m).TransitionTo(TradeState.PositionOpen, 4));
        Assert.Equal(TransitionError.UnhedgedDelta, Hedged(0.000009999999m, 0.00001m, 0.01m).TransitionTo(TradeState.PositionOpen, 4));
    }

    [Fact]
    public void OnePerpLot_IsNotAutomaticallySafe()
    {
        decimal tol = TradeGroup.HoldingDeltaTolerance(1.0, 60_000);
        TradeGroup g = Hedged(tol, 0m, 0.009m);                              // one 0.001 lot short of the 0.01 held
        Assert.Equal(TransitionError.UnhedgedDelta, g.TransitionTo(TradeState.PositionOpen, 4));
    }

    [Fact]
    public void FundingEntry_UsesTheSameRule()
    {
        var g = new TradeGroup();
        g.Initialize(79, StrategyKind.FundingPerpPerp, _m.BybitPepePerp.Key, _m.BinancePepePerp.Key, 100_000_000m, 0, maxHoldingDeltaCanon: 100_000m);
        g.TransitionTo(TradeState.InFlight, 1);
        g.ApplyFill(_m.BybitPepePerp.Key, Side.Buy, 100_000_000m, 1200m, 0.66m, FeeAssetKind.Quote, 0.66m);
        g.ApplyFill(_m.BinancePepePerp.Key, Side.Sell, 99_900_000m, 1198.8m, 0.54m, FeeAssetKind.Quote, 0.54m);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 2)); // 100,000 PEPE ≤ tolerance
        g.TransitionTo(TradeState.Exiting, 3);
        g.ApplyFill(_m.BybitPepePerp.Key, Side.Sell, 50_000_000m, 600m, 0.33m, FeeAssetKind.Quote, 0.33m);
        Assert.Equal(TransitionError.UnhedgedDelta, g.TransitionTo(TradeState.PositionOpen, 4));
    }

    [Fact]
    public void Validation()
    {
        var g = new TradeGroup();
        Assert.Throws<ArgumentOutOfRangeException>(() => g.Initialize(1, StrategyKind.Spatial, _m.BitgetBtcSpot.Key, _m.BybitBtcSpot.Key, 0.01m, 0, -0.000001m));
        g.Initialize(1, StrategyKind.Spatial, _m.BitgetBtcSpot.Key, _m.BybitBtcSpot.Key, 0.01m, 0);
        Assert.Equal(0m, g.MaxHoldingDeltaCanon);                                      // pre-existing overload: fail-closed
        Assert.Throws<ArgumentOutOfRangeException>(() => TradeGroup.HoldingDeltaTolerance(-1, 60_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => TradeGroup.HoldingDeltaTolerance(double.NaN, 60_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => TradeGroup.HoldingDeltaTolerance(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TradeGroup.HoldingDeltaTolerance(1, double.PositiveInfinity));
        Assert.True(new RiskLimits { MaxHoldingDeltaQuote = -1 }.Validate().Any(e => e.Contains("MaxHoldingDeltaQuote")));
        Assert.True(new RiskLimits { MaxHoldingDeltaQuote = double.NaN }.Validate().Any(e => e.Contains("MaxHoldingDeltaQuote")));
        Assert.Empty(new RiskLimits().Validate());
    }
}
