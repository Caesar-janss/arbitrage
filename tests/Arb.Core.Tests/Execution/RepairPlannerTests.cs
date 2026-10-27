using Arb.Core.Execution;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Execution;

/// <summary>
/// Scenario: buy leg on Bitget filled 0.01 BTC @ 60000, sell leg on Bybit filled 0 → residual +0.01 BTC.
/// </summary>
public class RepairPlannerTests
{
    private static readonly double[] Ladder = { 10, 30, 80 };
    private readonly TestMarkets _m = TestMarkets.Create();

    private static RepairPolicy Policy => new() { MaxStopLossBps = 30, SevereLossBps = 100, LadderBps = Ladder, DustCanon = 0.000001 };

    private RepairCandidate CompleteOnBybit(decimal bid, bool available = true) => new()
    {
        Method = RepairMethod.CompleteSellLeg,
        Spec = _m.BybitBtcSpot,
        Book = TestMarkets.View(_m.BybitBtcSpot, new[] { (bid, 0.02m) }, new[] { (bid + 10m, 1m) }),
        FeeRate = 0.001,
        Available = available,
    };

    private RepairCandidate UnwindOnBitget(bool available = true) => new()
    {
        Method = RepairMethod.Unwind,
        Spec = _m.BitgetBtcSpot,
        Book = TestMarkets.View(_m.BitgetBtcSpot, new[] { (59970m, 0.05m) }, new[] { (59980m, 1m) }),
        FeeRate = 0.0008,
        Available = available,
    };

    private RepairCandidate HedgeOnBybitPerp() => new()
    {
        Method = RepairMethod.PerpHedge,
        Spec = _m.BybitBtcPerp,
        Book = TestMarkets.View(_m.BybitBtcPerp, new[] { (59990.0m, 0.5m) }, new[] { (59990.1m, 0.5m) }),
        FeeRate = 0.00055,
        Available = true,
        ExtraCostBps = 12,
    };

    [Fact]
    public void LongResidual_CompletesSellLeg_WhenItIsCheapest()
    {
        RepairCandidate[] c = { CompleteOnBybit(60050m), UnwindOnBitget(), HedgeOnBybitPerp() };
        RepairPlan p = RepairPlanner.Plan(0.01m, 60000, c, Policy, 0, 777);

        Assert.Equal(RepairVerdict.Planned, p.Verdict);
        Assert.Equal(RepairMethod.CompleteSellLeg, p.Method);
        Assert.Equal(Side.Sell, p.Order.Side);
        Assert.Equal(TimeInForce.Ioc, p.Order.TimeInForce);
        Assert.Equal(OrderType.Limit, p.Order.Type);
        Assert.Equal(10_000L, p.Order.Lots);
        Assert.Equal(5998995L, p.Order.LimitPxTicks); // 60050 × (1 − 10 bps) = 59989.95, floored to tick
        Assert.Equal(777L, p.Order.ClientOrderId);
        Assert.Equal(1.675, p.ExpectedCostBps, 1e-9);
        Assert.False(p.ExceedsStopLoss);
    }

    [Fact]
    public void VenueHalted_FallsBackToUnwind()
    {
        RepairCandidate[] c = { CompleteOnBybit(60050m, available: false), UnwindOnBitget() };
        RepairPlan p = RepairPlanner.Plan(0.01m, 60000, c, Policy, 0, 1);
        Assert.Equal(RepairMethod.Unwind, p.Method);
        Assert.Equal(_m.BitgetBtcSpot.Key, p.Order.Instrument);
        Assert.Equal(5991003L, p.Order.LimitPxTicks); // 59970 × 0.999 = 59910.03
        Assert.Equal(12.996, p.ExpectedCostBps, 1e-9);
    }

    [Fact]
    public void PriceMovedOnB_UnwindBeatsCompletion()
    {
        RepairCandidate[] c = { CompleteOnBybit(59800m), UnwindOnBitget() };
        RepairPlan p = RepairPlanner.Plan(0.01m, 60000, c, Policy, 0, 1);
        Assert.Equal(RepairMethod.Unwind, p.Method);
        Assert.False(p.ExceedsStopLoss);
    }

    [Fact]
    public void BothSpotVenuesDown_HedgesOnPerp()
    {
        RepairCandidate[] c = { CompleteOnBybit(60050m, available: false), UnwindOnBitget(available: false), HedgeOnBybitPerp() };
        RepairPlan p = RepairPlanner.Plan(0.01m, 60000, c, Policy, 0, 1);
        Assert.Equal(RepairMethod.PerpHedge, p.Method);
        Assert.Equal(10L, p.Order.Lots); // 0.01 BTC / 0.001
        Assert.Equal(599300L, p.Order.LimitPxTicks); // 59990.0 → 599900 ticks × 0.999 = 599300.1 → floor
        Assert.Equal(((60000 - (59990 * (1 - 0.00055))) / 60000 * 1e4) + 12, p.ExpectedCostBps, 1e-9);
    }

    [Fact]
    public void ShortResidual_BuysBack_LimitRoundedUp()
    {
        RepairCandidate[] c =
        {
            new()
            {
                Method = RepairMethod.CompleteBuyLeg,
                Spec = _m.BitgetBtcSpot,
                Book = TestMarkets.View(_m.BitgetBtcSpot, new[] { (59990m, 1m) }, new[] { (60010.01m, 0.05m) }),
                FeeRate = 0.0008,
                Available = true,
            },
        };
        RepairPlan p = RepairPlanner.Plan(-0.005m, 60150, c, Policy, 1, 1);
        Assert.Equal(Side.Buy, p.Order.Side);
        Assert.Equal(5_000L, p.Order.Lots);
        Assert.Equal(30, p.ToleranceBps);
        Assert.Equal(6019005L, p.Order.LimitPxTicks); // 6001001 × 1.003 = 6019004.003 → ceil
        Assert.True(p.ExpectedCostBps < 0, "buying back below the sell price is a gain");
    }

    [Fact]
    public void LargeLoss_IsFlaggedButStillFlattens()
    {
        RepairCandidate[] c = { CompleteOnBybit(59300m) }; // 59300 × 0.999 → ≈126.6 bps loss
        RepairPlan p = RepairPlanner.Plan(0.01m, 60000, c, Policy, 2, 1);
        Assert.Equal(RepairVerdict.Planned, p.Verdict);
        Assert.True(p.ExceedsStopLoss);
        Assert.True(p.ExceedsSevere);
        Assert.Equal(80, p.ToleranceBps);
    }

    [Fact]
    public void PartialDepth_PrefersVenueThatAbsorbsEverything()
    {
        RepairCandidate thin = CompleteOnBybit(60050m) with
        {
            Book = TestMarkets.View(_m.BybitBtcSpot, new[] { (60050m, 0.002m) }, new[] { (60060m, 1m) }),
        };
        RepairPlan p = RepairPlanner.Plan(0.01m, 60000, new[] { thin, UnwindOnBitget() }, Policy, 0, 1);
        Assert.Equal(RepairMethod.Unwind, p.Method);
        Assert.Equal(1.0, p.ExpectedFillRatio, 1e-12);
    }

    [Fact]
    public void Dust_And_NoVenue_And_BelowMinimum()
    {
        RepairPlan flat = RepairPlanner.Plan(0.0000005m, 60000, new[] { UnwindOnBitget() }, Policy, 0, 1);
        Assert.Equal(RepairVerdict.Flat, flat.Verdict);

        RepairPlan none = RepairPlanner.Plan(0.01m, 60000, new[] { UnwindOnBitget(available: false) }, Policy, 0, 1);
        Assert.Equal(RepairVerdict.NoViableVenue, none.Verdict);

        // 0.000009 BTC on Binance (lot 1e-5) → 0 lots
        RepairCandidate binance = new()
        {
            Method = RepairMethod.Unwind,
            Spec = _m.BinanceBtcSpot,
            Book = TestMarkets.View(_m.BinanceBtcSpot, new[] { (60000m, 1m) }, new[] { (60001m, 1m) }),
            FeeRate = 0.001,
            Available = true,
        };
        RepairPlan small = RepairPlanner.Plan(0.000009m, 60000, new[] { binance }, Policy, 0, 1);
        Assert.Equal(RepairVerdict.BelowVenueMinimum, small.Verdict);

        // 0.00005 BTC ≈ 3 USDT < 5 USDT min notional
        RepairPlan underNotional = RepairPlanner.Plan(0.00005m, 60000, new[] { binance }, Policy, 0, 1);
        Assert.Equal(RepairVerdict.BelowVenueMinimum, underNotional.Verdict);
    }

    [Fact]
    public void UntradeableRemainder_IsReported()
    {
        RepairPlan p = RepairPlanner.Plan(0.0100005m, 60000, new[] { CompleteOnBybit(60050m) }, Policy, 0, 1);
        Assert.Equal(10_000L, p.Order.Lots);
        Assert.Equal(0.0000005, p.UntradeableCanon, 1e-12);
    }
}

public class TradeGroupTests
{
    [Fact]
    public void TracksCanonicalExposure_IncludingBaseFees()
    {
        var buy = new InstrumentKey(Venue.Bitget, MarketType.Spot, 2);
        var sell = new InstrumentKey(Venue.Bybit, MarketType.Spot, 1);
        var g = new TradeGroup();
        g.Initialize(1002, StrategyKind.Spatial, buy, sell, 0.01m, 0);

        g.ApplyFill(buy, Side.Buy, 0.01m, 600m, 0.00001m, FeeAssetKind.Base, 0.6m);
        Assert.Equal(0.00999m, g.NetBaseDelta);
        Assert.False(g.IsFlat(0.000001m));

        g.ApplyFill(buy, Side.Sell, 0.00999m, 599.1003m, 0.5991003m, FeeAssetKind.Quote, 0.5991003m); // unwind on the buy venue
        Assert.Equal(0m, g.NetBaseDelta);
        Assert.True(g.IsFlat(0m));
        Assert.Equal(-1.4988003m, g.CashFlowQuote); // == db/tests trade 1002 realized PnL

        Assert.Equal(TransitionError.None, g.TransitionTo(TradeState.InFlight, 1));
        Assert.Equal(1, g.State.Version);

        g.Initialize(1003, StrategyKind.Spatial, buy, sell, 0.005m, 10);
        Assert.Equal(0m, g.NetBaseDelta);
        Assert.Equal(TradeState.Created, g.State.State);
        Assert.Throws<ArgumentOutOfRangeException>(() => g.ApplyFill(buy, Side.Buy, 0m, 1m, 0m, FeeAssetKind.Quote, 0m));
    }
}
