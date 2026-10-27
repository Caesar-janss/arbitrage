using Arb.Core.Execution;
using Arb.Core.Inventory;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Risk;
using BenchmarkDotNet.Attributes;

namespace Arb.Benchmarks;

[MemoryDiagnoser]
public class OrderBookBenchmarks
{
    private OrderBookL2 _book = null!;
    private BookView _view = null!;
    private PriceLevel[] _bidsA = null!;
    private PriceLevel[] _bidsB = null!;
    private PriceLevel[] _asks = null!;
    private long _seq;

    [GlobalSetup]
    public void Setup()
    {
        var f = new BenchFixtures();
        _book = new OrderBookL2(f.Buy.Key, 1024);
        _book.ApplySnapshot(f.BuyBids, f.BuyAsks, new BookStamp(1, 0, 0, 0));
        _view = new BookView(64);
        // a typical diff message: 6 bid changes (incl. one delete + re-insert toggle) and 4 ask changes
        _bidsA = new[] { new PriceLevel(5_999_990, 300), new PriceLevel(5_999_985, 0), new PriceLevel(5_999_980, 12), new PriceLevel(5_999_900, 40), new PriceLevel(5_999_500, 9), new PriceLevel(5_999_000, 77) };
        _bidsB = new[] { new PriceLevel(5_999_990, 280), new PriceLevel(5_999_985, 210), new PriceLevel(5_999_980, 15), new PriceLevel(5_999_900, 41), new PriceLevel(5_999_500, 0), new PriceLevel(5_999_000, 70) };
        _asks = new[] { new PriceLevel(6_000_000, 120), new PriceLevel(6_000_005, 160), new PriceLevel(6_000_050, 300), new PriceLevel(6_000_500, 1) };
    }

    [Benchmark(Description = "OrderBookL2.ApplyDelta (10 levels)")]
    public bool ApplyDelta()
    {
        _seq++;
        return _book.ApplyDelta((_seq & 1) == 0 ? _bidsA : _bidsB, _asks, new BookStamp(_seq, _seq, _seq, _seq));
    }

    [Benchmark(Description = "OrderBookL2.TryCopyTo (top 20)")]
    public bool CopyTop20() => _book.TryCopyTo(_view, 20);
}

[MemoryDiagnoser]
public class PricingBenchmarks
{
    private BenchFixtures _f = null!;
    private SpatialPairContext _ctx = null!;
    private SpatialParams _params;
    private CrossCosts _costs;
    private CrossLimits _limits;
    private FundingInputs _funding;
    private FundingGate _gate;

    [GlobalSetup]
    public void Setup()
    {
        _f = new BenchFixtures();
        _ctx = new SpatialPairContext(_f.Buy, _f.Sell);
        _params = new SpatialParams
        {
            BuyFee = new ResolvedFee(0.00075, FeeAssetKind.DiscountToken),
            SellFee = new ResolvedFee(0.001, FeeAssetKind.Quote),
            BuySlipBps = 0.5,
            SellSlipBps = 1.5,
            RebalancePerUnitQuote = 2,
            MinMarginalEdgeBps = 0.5,
            MinProfitQuote = 0.05,
            MaxQtyCanon = double.PositiveInfinity,
            MaxBuyNotional = 5_000,
            HaircutPpm = 700_000,
            MaxLevelsPerSide = 20,
        };
        _costs = new CrossCosts(0.00075, 0.001, 0.5, 1.5, 2, 0.5);
        _limits = new CrossLimits(double.PositiveInfinity, 5_000, 700_000, 20);
        _funding = new FundingInputs
        {
            Strategy = StrategyKind.FundingPerpPerp,
            ShortLegHourlyRate = 6.25e-5,
            LongLegHourlyRate = 5e-6,
            Costs = new FundingCosts { EntryFeeLong = 0.0002, EntryFeeShort = 0.00018, ExitFeeLong = 0.0002, ExitFeeShort = 0.00018, EntrySpread = 0.00005, ExpectedExitSpread = 0.00005 },
            ExpectedHoldHours = 72,
            Leverage = 3,
            NotionalQuote = 1_000,
        };
        _gate = new FundingGate { MaxBreakEvenHours = 16, MinNetApr = 0.1, MaxLeverage = 3, MinEntryLiqDistanceBps = 3_000 };
    }

    [Benchmark(Description = "VwapWalker.WalkLots (~20 levels)")]
    public long VwapWalk20() => VwapWalker.WalkLots(_f.BuyView.Asks, 5_000).FilledLots;

    [Benchmark(Description = "CrossedBookOptimizer.Optimize (Q*)")]
    public double OptimizeQStar() => CrossedBookOptimizer.Optimize(_f.BuyView.Asks, _f.Buy, _f.SellView.Bids, _f.Sell, _costs, _limits).QtyCanon;

    [Benchmark(Description = "SpatialArbPricer.Evaluate (end-to-end)")]
    public double SpatialEvaluate() => SpatialArbPricer.Evaluate(_ctx, _f.BuyView, _f.SellView, _params).Edge.NetQuote;

    [Benchmark(Description = "FundingYieldCalculator.Evaluate")]
    public double FundingEvaluate() => FundingYieldCalculator.Evaluate(_funding, _gate, 3_500).NetApr;
}

[MemoryDiagnoser]
public class RiskBenchmarks
{
    private readonly MarginTier[] _tiers = { new(1_000, 0.01, 0), new(5_000, 0.02, 10), new(double.PositiveInfinity, 0.05, 160) };
    private BenchFixtures _f = null!;
    private PerpExposure _exposure;
    private MarginThresholds _thresholds;
    private RepairCandidate[] _candidates = null!;
    private RepairPolicy _policy;
    private WalletStatusBook _wallets = null!;
    private SanityInput _sanity;
    private SanityLimits _sanityLimits;
    private InventoryLedger _ledger = null!;
    private int _slotUsdt;
    private int _slotBtc;
    private long _event;
    private FreshnessLimits _freshness;
    private VenueClock _clock;

    [GlobalSetup]
    public void Setup()
    {
        _f = new BenchFixtures();
        _exposure = new PerpExposure(Side.Sell, 100_000_000, 0.000012, 180, MarginMode.Cross);
        _thresholds = new MarginThresholds(3_500, 2_500, 1_500, 800);
        _candidates = new[]
        {
            new RepairCandidate { Method = RepairMethod.CompleteSellLeg, Spec = _f.Sell, Book = _f.SellView, FeeRate = 0.001, Available = true },
            new RepairCandidate { Method = RepairMethod.Unwind, Spec = _f.Third, Book = _f.ThirdView, FeeRate = 0.0008, Available = true },
        };
        _policy = new RepairPolicy { MaxStopLossBps = 30, SevereLossBps = 100, LadderBps = new double[] { 10, 30, 80 }, DustCanon = 1e-6 };

        _wallets = new WalletStatusBook(_f.Assets);
        var net = new NetworkId(1);
        foreach (Venue v in new[] { Venue.Binance, Venue.Bybit })
        {
            _wallets.Update(v, _f.Assets["BTC"], new[] { new WalletNetworkStatus(new NetworkId(9), true, false, 1_000), new WalletNetworkStatus(net, true, true, 1_000) });
            _wallets.Update(v, _f.Assets["USDT"], new[] { new WalletNetworkStatus(net, true, true, 1_000) });
        }

        _sanity = new SanityInput { Strategy = StrategyKind.Spatial, Buy = _f.Buy, Sell = _f.Sell, GrossEdgeBps = 15, SellVenueBaseInventory = 1, BuyVenueQuoteInventory = 10_000, ReferencePx = 60_000 };
        _sanityLimits = SanityLimits.From(new RiskLimits());

        _ledger = new InventoryLedger(_f.Assets, 256);
        _slotUsdt = _ledger.SlotOf(Venue.Binance, AccountKind.Spot, _f.Assets["USDT"]);
        _slotBtc = _ledger.SlotOf(Venue.Bybit, AccountKind.Unified, _f.Assets["BTC"]);
        _ledger.OnBalance(_slotUsdt, 100_000m, 0);
        _ledger.OnBalance(_slotBtc, 10m, 0);

        _freshness = FreshnessLimits.From(new RiskLimits());
        _clock = new VenueClock(3, 1, true);
    }

    [Benchmark(Description = "LiquidationCalculator.Estimate (tiered)")]
    public double Liquidation() => LiquidationCalculator.Estimate(_exposure, _tiers, 0.00055).DistanceBps;

    [Benchmark(Description = "MarginGuard.Evaluate (reduce zone)")]
    public double MarginGuardEval() => MarginGuard.Evaluate(_exposure, _tiers, 0.00055, 0.00055, _thresholds, 50).ReduceQtyCanon;

    [Benchmark(Description = "TradeStateMachine 2 transitions")]
    public int StateMachine()
    {
        TradeStateRecord r = TradeStateRecord.Create(0);
        TradeStateMachine.TryTransition(ref r, TradeState.InFlight, StrategyKind.Spatial, 1);
        TradeStateMachine.TryTransition(ref r, TradeState.Completed, StrategyKind.Spatial, 2);
        return r.Version;
    }

    [Benchmark(Description = "RepairPlanner.Plan (2 venues)")]
    public long Repair() => RepairPlanner.Plan(0.01m, 60_000, _candidates, _policy, 1, 42).Order.LimitPxTicks;

    [Benchmark(Description = "SanityGuard.Evaluate (wallet routes)")]
    public double Sanity() => SanityGuard.Evaluate(_sanity, _wallets, 2_000, _sanityLimits).QtyCapCanon;

    [Benchmark(Description = "StaleDataGuard.Check")]
    public Freshness Freshness() => StaleDataGuard.Check(_f.SellView, 1_000, 1_000, _clock, _freshness, out _);

    [Benchmark(Description = "InventoryLedger reserve→terminal→settle (pair)")]
    public int LedgerCycle()
    {
        _event++;
        _ledger.TryReservePair(_slotUsdt, 600m, _slotBtc, 0.01m, out ReservationHandle a, out ReservationHandle b);
        _ledger.MarkTerminal(a, _event, 0);
        _ledger.MarkTerminal(b, _event, 0);
        _ledger.OnBalance(_slotUsdt, 100_000m, _event);
        _ledger.OnBalance(_slotBtc, 10m, _event);
        return _ledger.ActiveReservations;
    }
}

[MemoryDiagnoser]
public class ParsingBenchmarks
{
    private readonly byte[] _clientId = new byte[32];
    private SnowflakeIdGenerator _ids = null!;
    private BenchFixtures _f = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ids = new SnowflakeIdGenerator(1, SystemClock.Instance, resumeAfterUnixMs: 0);
        _f = new BenchFixtures();
    }

    [Benchmark(Description = "Utf8Decimal.TryParse + DecimalGrid.TryToUnits")]
    public long ParsePriceToTicks()
    {
        Utf8Decimal.TryParse("60007.50"u8, out long m, out int e);
        _f.Buy.Tick.TryToUnits(m, e, out long ticks);
        return ticks;
    }

    [Benchmark(Description = "Snowflake.Next + ClientOrderId.TryFormat")]
    public int NextClientOrderId()
    {
        ClientOrderId.TryFormat(_ids.Next(), _clientId, out int n);
        return n;
    }
}
