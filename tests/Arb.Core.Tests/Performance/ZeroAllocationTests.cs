using Arb.Core.Execution;
using Arb.Core.Inventory;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Risk;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Performance;

/// <summary>
/// Deterministic CI guard for the hot path: after warm-up, N calls must allocate exactly 0 bytes on the
/// calling thread (GC.GetAllocatedBytesForCurrentThread). BenchmarkDotNet (tests/Arb.Benchmarks) reports the
/// same with timing; this test makes a regression fail the build instead of a report nobody reads.
/// </summary>
public class ZeroAllocationTests
{
    private const int Iterations = 20_000;
    private readonly TestMarkets _m = TestMarkets.Create();

    private static void AssertZeroAlloc(string name, Action body)
    {
        for (int i = 0; i < 2_000; i++)
        {
            body();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            body();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"{name}: {allocated} bytes over {Iterations} calls ({(double)allocated / Iterations:F2} B/op)");
    }

    [Fact]
    public void OrderBook_ApplyDelta_And_TryCopyTo()
    {
        var book = new OrderBookL2(_m.BybitBtcSpot.Key, 1024);
        var bids = new PriceLevel[200];
        var asks = new PriceLevel[200];
        for (int i = 0; i < 200; i++)
        {
            bids[i] = new PriceLevel(6_000_000 - i, 1_000 + i);
            asks[i] = new PriceLevel(6_000_001 + i, 1_000 + i);
        }

        book.ApplySnapshot(bids, asks, new BookStamp(1, 0, 0, 0));
        var updA = new[] { new PriceLevel(6_000_000, 5), new PriceLevel(5_999_995, 0), new PriceLevel(5_999_990, 7) };
        var updB = new[] { new PriceLevel(6_000_000, 9), new PriceLevel(5_999_995, 3), new PriceLevel(5_999_990, 0) };
        var askUpd = new[] { new PriceLevel(6_000_003, 11) };
        long seq = 1;
        var view = new BookView(32);

        AssertZeroAlloc("ApplyDelta", () =>
        {
            seq++;
            book.ApplyDelta((seq & 1) == 0 ? updA : updB, askUpd, new BookStamp(seq, seq, seq, seq));
        });
        AssertZeroAlloc("TryCopyTo", () => book.TryCopyTo(view, 20));
    }

    [Fact]
    public void Pricing_HotPath()
    {
        var ctx = new SpatialPairContext(_m.BinanceBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (59995m, 0.006m), (60007.50m, 0.010m), (60010m, 0.5m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60150m, 0.010m), (60100m, 0.020m), (60000m, 1m) }, new[] { (60160m, 1m) });
        var p = new SpatialParams
        {
            BuyFee = new ResolvedFee(0.00075, FeeAssetKind.DiscountToken),
            SellFee = new ResolvedFee(0.001, FeeAssetKind.Quote),
            BuySlipBps = 0.5,
            SellSlipBps = 0.7,
            RebalancePerUnitQuote = 2,
            MinMarginalEdgeBps = 0.5,
            MinProfitQuote = 0.05,
            MaxQtyCanon = double.PositiveInfinity,
            MaxBuyNotional = 5_000,
            HaircutPpm = 700_000,
            MaxLevelsPerSide = 10,
        };
        var costs = new CrossCosts(0.00075, 0.001, 0.5, 0.7, 2, 0.5);
        var limits = new CrossLimits(double.PositiveInfinity, 5_000, 700_000, 10);
        var fees = new FeeModel(_m.Registry);
        fees.Set(_m.BinanceBtcSpot.Key, new FeeTier(10, 10, 25, _m.Bnb, true));
        var vol = new EwmaVolatility(5_000, 1);
        var slip = new SlippageModel(0.5, 2, 20, 5, 0);
        long t = 0;

        AssertZeroAlloc("VwapWalker", () => VwapWalker.WalkLots(buy.Asks, 1_200));
        AssertZeroAlloc("CrossedBookOptimizer", () => CrossedBookOptimizer.Optimize(buy.Asks, _m.BinanceBtcSpot, sell.Bids, _m.BybitBtcSpot, costs, limits));
        AssertZeroAlloc("SpatialArbPricer", () => SpatialArbPricer.Evaluate(ctx, buy, sell, p));
        AssertZeroAlloc("FeeModel.Resolve", () => fees.Resolve(_m.BinanceBtcSpot.Key, Side.Buy, false, true));
        AssertZeroAlloc("Slippage", () =>
        {
            t += 20_000;
            vol.Update(60_000 + (t % 7), t);
            slip.LegBps(vol, 70);
        });
        var gate = new FundingGate { MaxBreakEvenHours = 16, MinNetApr = 0.1, MaxLeverage = 3, MinEntryLiqDistanceBps = 3_000 };
        var fx = new FundingInputs
        {
            Strategy = StrategyKind.FundingPerpPerp,
            ShortLegHourlyRate = 6.25e-5,
            LongLegHourlyRate = 5e-6,
            Costs = new FundingCosts { EntryFeeLong = 0.0002, EntryFeeShort = 0.00018, ExitFeeLong = 0.0002, ExitFeeShort = 0.00018 },
            ExpectedHoldHours = 72,
            Leverage = 3,
            NotionalQuote = 1_000,
        };
        AssertZeroAlloc("FundingYield", () => FundingYieldCalculator.Evaluate(fx, gate, 3_500));
    }

    [Fact]
    public void Risk_And_Execution_HotPath()
    {
        MarginTier[] tiers = { new(1_000, 0.01, 0), new(5_000, 0.02, 10) };
        var thresholds = new MarginThresholds(3_500, 2_500, 1_500, 800);
        var exposure = new PerpExposure(Side.Sell, 100_000_000, 0.000012, 180, MarginMode.Cross);
        AssertZeroAlloc("Liquidation", () => LiquidationCalculator.Estimate(exposure, tiers, 0.00055));
        AssertZeroAlloc("MarginGuard", () => MarginGuard.Evaluate(exposure, tiers, 0.00055, 0.00055, thresholds, 50));
        AccountPosition[] account =
        {
            new(Side.Sell, 100_000_000, 0.000012, tiers, 0.00055, 0.00055),
            new(Side.Sell, 0.02, 60_000, tiers, 0.00055, 0.00055),
        };
        AssertZeroAlloc("MarginGuard.EvaluateAccount", () => MarginGuard.EvaluateAccount(account, 500, thresholds, 50));

        AssertZeroAlloc("TradeStateMachine", () =>
        {
            TradeStateRecord r = TradeStateRecord.Create(0);
            TradeStateMachine.TryTransition(ref r, TradeState.InFlight, StrategyKind.Spatial, 1);
            TradeStateMachine.TryTransition(ref r, TradeState.Completed, StrategyKind.Spatial, 2);
        });

        var group = new TradeGroup();
        AssertZeroAlloc("TradeGroup", () =>
        {
            group.Initialize(1, StrategyKind.Spatial, _m.BitgetBtcSpot.Key, _m.BybitBtcSpot.Key, 0.01m, 0);
            group.ApplyFill(Side.Buy, 0.01m, 600m, 0.00001m, FeeAssetKind.Base, 0.6m);
            group.TransitionTo(TradeState.InFlight, 1);
        });

        RepairCandidate[] candidates =
        {
            new() { Method = RepairMethod.CompleteSellLeg, Spec = _m.BybitBtcSpot, Book = TestMarkets.View(_m.BybitBtcSpot, new[] { (60050m, 0.02m) }, new[] { (60060m, 1m) }), FeeRate = 0.001, Available = true },
            new() { Method = RepairMethod.Unwind, Spec = _m.BitgetBtcSpot, Book = TestMarkets.View(_m.BitgetBtcSpot, new[] { (59970m, 0.05m) }, new[] { (59980m, 1m) }), FeeRate = 0.0008, Available = true },
        };
        var policy = new RepairPolicy { MaxStopLossBps = 30, SevereLossBps = 100, LadderBps = new double[] { 10, 30, 80 }, DustCanon = 1e-6 };
        AssertZeroAlloc("RepairPlanner", () => RepairPlanner.Plan(0.01m, 60_000, candidates, policy, 1, 42));

        var wallets = new WalletStatusBook(_m.Assets);
        var net = new NetworkId(1);
        foreach (Venue v in new[] { Venue.Bitget, Venue.Bybit })
        {
            wallets.Update(v, _m.Btc, new[] { new WalletNetworkStatus(net, true, true, 1_000) });
            wallets.Update(v, _m.Usdt, new[] { new WalletNetworkStatus(net, true, true, 1_000) });
        }

        var sanityIn = new SanityInput { Strategy = StrategyKind.Spatial, Buy = _m.BitgetBtcSpot, Sell = _m.BybitBtcSpot, GrossEdgeBps = 20, SellVenueBaseInventory = 1, BuyVenueQuoteInventory = 10_000, ReferencePx = 60_000 };
        SanityLimits sanityLimits = SanityLimits.From(new RiskLimits());
        AssertZeroAlloc("SanityGuard", () => SanityGuard.Evaluate(sanityIn, wallets, 2_000, sanityLimits));

        BookView book = TestMarkets.View(_m.BybitBtcSpot, new[] { (60050m, 0.02m) }, new[] { (60060m, 1m) });
        var clock = new VenueClock(3, 1, true);
        FreshnessLimits fl = FreshnessLimits.From(new RiskLimits());
        AssertZeroAlloc("StaleDataGuard", () => StaleDataGuard.Check(book, 1_001_000, 1_001_000, clock, fl, out _));

        var ledger = new InventoryLedger(_m.Assets, 64);
        int usdt = ledger.SlotOf(Venue.Bitget, AccountKind.Spot, _m.Usdt);
        int btc = ledger.SlotOf(Venue.Bybit, AccountKind.Unified, _m.Btc);
        long ev = 0;
        ledger.OnBalance(usdt, 10_000m, 0);
        ledger.OnBalance(btc, 1m, 0);
        AssertZeroAlloc("InventoryLedger", () =>
        {
            ev++;
            ledger.TryReservePair(usdt, 600m, btc, 0.01m, out ReservationHandle a, out ReservationHandle b);
            ledger.MarkTerminal(a, ev, 0);
            ledger.MarkTerminal(b, ev, 0);
            ledger.OnBalance(usdt, 10_000m, ev);
            ledger.OnBalance(btc, 1m, ev);
        });
        Assert.Equal(0, ledger.ActiveReservations);
    }

    [Fact]
    public void Parsing_And_Ids()
    {
        var gen = new SnowflakeIdGenerator(1, SystemClock.Instance);
        byte[] buffer = new byte[32];
        AssertZeroAlloc("Snowflake+ClientOrderId", () => ClientOrderId.TryFormat(gen.Next(), buffer, out _));
        AssertZeroAlloc("Utf8Decimal+Grid", () =>
        {
            Utf8Decimal.TryParse("60007.50"u8, out long m, out int e);
            _m.BinanceBtcSpot.Tick.TryToUnits(m, e, out _);
        });
    }
}
