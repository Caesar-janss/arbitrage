using Arb.Core.Execution;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// C-10 (S1, ChatGPT re-review of 30417db; confirms the builder's round-2 observation; pre-existing API). POSITION_OPEN
/// means "delta-neutral holding", but TradeGroup only checked that a perp position EXISTS (spatial) or nothing at all
/// (funding). A partial exit where only the perp buy-back filled (spot +0.010 / perp −0.006 → +0.004 BTC ≈ 240 USDT
/// naked), a partial repair hedge, or a funding entry with one leg partly filled all re-entered POSITION_OPEN.
/// Contract after the fix: every transition to POSITION_OPEN requires |net delta − accepted dust| ≤ the trade's holding
/// tolerance; the pre-existing Initialize sets that tolerance to 0 (fail-closed). The rest goes RESIDUAL / stays EXITING.
/// </summary>
public class C10_UnbalancedHoldingTests
{
    private static readonly RepairPolicy Policy = new() { MaxStopLossBps = 30, SevereLossBps = 100, LadderBps = new double[] { 10, 30, 80 }, DustCanon = 0.000001 };
    private readonly TestMarkets _m = TestMarkets.Create();

    private TradeGroup Repairing(decimal boughtCanon = 0.01m, decimal baseFee = 0m)
    {
        var g = new TradeGroup();
        g.Initialize(45, StrategyKind.Spatial, _m.BitgetBtcSpot.Key, _m.BybitBtcSpot.Key, 0.01m, 0);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.InFlight, 1));
        g.ApplyFill(_m.BitgetBtcSpot.Key, Side.Buy, boughtCanon, boughtCanon * 60000m, baseFee, baseFee > 0 ? FeeAssetKind.Base : FeeAssetKind.DiscountToken, baseFee > 0 ? baseFee * 60000m : 0.48m);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Residual, 2));
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Repairing, 3));
        return g;
    }

    private TradeGroup HeldHedge()
    {
        TradeGroup g = Repairing();
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Sell, 0.01m, 599.9m, 0.33m, FeeAssetKind.Quote, 0.33m);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 4));
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Exiting, 5));
        return g;
    }

    [Fact]
    public void ReviewerCase_OnlyThePerpBuyBackFilled_DoesNotReturnToPositionOpen()
    {
        TradeGroup g = HeldHedge();
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Buy, 0.004m, 239.98m, 0.13m, FeeAssetKind.Quote, 0.13m);
        Assert.Equal(0.004m, g.NetBaseDelta);
        Assert.True(g.HasOpenPerpPosition);
        Assert.NotEqual(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 6)); // 30417db: None
        Assert.Equal(TradeState.Exiting, g.State.State);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Residual, 6));        // the unhedged part is a residual
    }

    [Fact]
    public void BothLegsReduced_ByDifferentSizes_DoesNotReturnToPositionOpen()
    {
        TradeGroup g = HeldHedge();
        g.ApplyFill(_m.BitgetBtcSpot.Key, Side.Sell, 0.004m, 240.08m, 0.19m, FeeAssetKind.Quote, 0.19m);
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Buy, 0.002m, 119.99m, 0.07m, FeeAssetKind.Quote, 0.07m);
        Assert.Equal(-0.002m, g.NetBaseDelta);                                            // spot +0.006, perp −0.008: over-hedged
        Assert.NotEqual(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 6)); // 30417db: None
        Assert.Equal(TradeState.Exiting, g.State.State);
    }

    [Fact]
    public void PartialRepairHedge_DoesNotEnterPositionOpen()
    {
        TradeGroup g = Repairing();
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Sell, 0.006m, 359.94m, 0.2m, FeeAssetKind.Quote, 0.2m); // hedge IOC 60 %
        Assert.Equal(0.004m, g.NetBaseDelta);
        Assert.NotEqual(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 4)); // 30417db: None
        Assert.Equal(TradeState.Repairing, g.State.State);
    }

    [Fact]
    public void BaseFeeLeavesADelta_AndTheDefaultToleranceIsZero()
    {
        // spot buy 0.01 with 0.1 % fee in base → holds 0.00999; hedge short 0.01 → over-hedged by 0.00001
        TradeGroup g = Repairing(baseFee: 0.00001m);
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Sell, 0.01m, 599.9m, 0.33m, FeeAssetKind.Quote, 0.33m);
        Assert.Equal(-0.00001m, g.NetBaseDelta);
        Assert.NotEqual(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 4)); // 30417db: None
    }

    [Fact]
    public void FundingEntry_WithOneLegPartlyFilled_DoesNotEnterPositionOpen()
    {
        // same invariant, other strategy (builder extension): long 1e8 PEPE filled, short only 6e7
        var g = new TradeGroup();
        g.Initialize(78, StrategyKind.FundingPerpPerp, _m.BybitPepePerp.Key, _m.BinancePepePerp.Key, 100_000_000m, 0);
        g.TransitionTo(TradeState.InFlight, 1);
        g.ApplyFill(_m.BybitPepePerp.Key, Side.Buy, 100_000_000m, 1200m, 0.66m, FeeAssetKind.Quote, 0.66m);
        g.ApplyFill(_m.BinancePepePerp.Key, Side.Sell, 60_000_000m, 720.36m, 0.32m, FeeAssetKind.DiscountToken, 0.32m);
        Assert.Equal(40_000_000m, g.NetBaseDelta);
        Assert.NotEqual(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 2)); // 30417db: None
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.Residual, 2));
    }

    [Fact]
    public void BalancedHoldings_StillEnterPositionOpen()
    {
        TradeGroup g = HeldHedge();
        g.ApplyFill(_m.BitgetBtcSpot.Key, Side.Sell, 0.004m, 240.08m, 0.19m, FeeAssetKind.Quote, 0.19m);
        g.ApplyFill(_m.BybitBtcPerp.Key, Side.Buy, 0.004m, 239.98m, 0.13m, FeeAssetKind.Quote, 0.13m);
        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.PositionOpen, 6));
    }
}
