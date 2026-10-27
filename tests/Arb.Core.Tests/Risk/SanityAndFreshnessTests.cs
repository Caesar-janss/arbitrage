using Arb.Core.MarketData;
using Arb.Core.Primitives;
using Arb.Core.Risk;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Risk;

public class WalletStatusAndSanityTests
{
    private const long Now = 1_800_000_000_000;
    private static readonly NetworkId Erc20 = new(1);
    private static readonly NetworkId Trc20 = new(2);
    private static readonly NetworkId Bep20 = new(3);
    private readonly TestMarkets _m = TestMarkets.Create();
    private readonly SanityLimits _limits = SanityLimits.From(new RiskLimits());

    private WalletStatusBook AllOpen()
    {
        var w = new WalletStatusBook(_m.Assets);
        foreach (Venue v in new[] { Venue.Binance, Venue.Bybit, Venue.Bitget })
        {
            w.Update(v, _m.Btc, new[] { new WalletNetworkStatus(Erc20, true, true, Now) });
            w.Update(v, _m.Usdt, new[] { new WalletNetworkStatus(Trc20, true, true, Now), new WalletNetworkStatus(Erc20, true, true, Now) });
        }

        return w;
    }

    private SanityInput Spot(double edgeBps) => new()
    {
        Strategy = StrategyKind.Spatial,
        Buy = _m.BitgetBtcSpot,
        Sell = _m.BybitBtcSpot,
        GrossEdgeBps = edgeBps,
        SellVenueBaseInventory = 0.2,
        BuyVenueQuoteInventory = 12_000,
        ReferencePx = 60_000,
        SellVenueBaseBudget = new NonReplenishableBudget(0.2, 0),   // route just closed: snapshot = current (R-11)
        BuyVenueQuoteBudget = new NonReplenishableBudget(12_000, 0),
    };

    [Fact]
    public void Flags_DepositAndWithdrawPerVenue()
    {
        var w = new WalletStatusBook(_m.Assets);
        w.Update(Venue.Bitget, _m.Btc, new[] { new WalletNetworkStatus(Erc20, isDepositEnabled: true, isWithdrawEnabled: false, Now) });
        Assert.True(w.IsDepositEnabled(Venue.Bitget, _m.Btc, Now, 60_000));
        Assert.False(w.IsWithdrawEnabled(Venue.Bitget, _m.Btc, Now, 60_000));
        Assert.False(w.IsDepositEnabled(Venue.Bitget, _m.Btc, Now + 120_000, 60_000)); // stale → not trusted
        Assert.False(w.IsDepositEnabled(Venue.Bybit, _m.Btc, Now, 60_000));            // never reported
    }

    [Fact]
    public void Route_RequiresSameNetworkOpenOnBothEnds()
    {
        var w = new WalletStatusBook(_m.Assets);
        // Bitget: withdraw open only on TRC20; Bybit: deposit open only on ERC20 → no route
        w.Update(Venue.Bitget, _m.Btc, new[] { new WalletNetworkStatus(Erc20, true, false, Now), new WalletNetworkStatus(Trc20, true, true, Now) });
        w.Update(Venue.Bybit, _m.Btc, new[] { new WalletNetworkStatus(Erc20, true, true, Now), new WalletNetworkStatus(Trc20, false, true, Now) });
        Assert.True(w.IsWithdrawEnabled(Venue.Bitget, _m.Btc, Now, 60_000));
        Assert.True(w.IsDepositEnabled(Venue.Bybit, _m.Btc, Now, 60_000));
        Assert.Equal(RouteStatus.Closed, w.FindRoute(_m.Btc, Venue.Bitget, Venue.Bybit, Now, 60_000).Status);

        w.Update(Venue.Bybit, _m.Btc, new[] { new WalletNetworkStatus(Bep20, true, true, Now), new WalletNetworkStatus(Trc20, true, true, Now) });
        TransferRoute r = w.FindRoute(_m.Btc, Venue.Bitget, Venue.Bybit, Now, 60_000);
        Assert.True(r.IsOpen);
        Assert.Equal(Trc20, r.Network);

        Assert.Equal(RouteStatus.Unknown, w.FindRoute(_m.Btc, Venue.Bitget, Venue.Bybit, Now + 120_000, 60_000).Status);
        Assert.Equal(RouteStatus.Unknown, w.FindRoute(_m.Btc, Venue.Bitget, Venue.Binance, Now, 60_000).Status);
    }

    [Fact]
    public void SpotSpot_AllRoutesOpen_Passes_Uncapped()
    {
        SanityResult s = SanityGuard.Evaluate(Spot(25), AllOpen(), Now, _limits);
        Assert.Equal(RejectReason.Execute, s.Decision);
        Assert.True(double.IsPositiveInfinity(s.QtyCapCanon));
        Assert.Equal(SanityFlags.None, s.Flags);
    }

    [Fact]
    public void WithdrawClosedOnBuyVenue_WithLargeSpread_IsTheTrap()
    {
        WalletStatusBook w = AllOpen();
        w.Update(Venue.Bitget, _m.Btc, new[] { new WalletNetworkStatus(Erc20, true, false, Now) });
        SanityResult s = SanityGuard.Evaluate(Spot(120), w, Now, _limits);
        Assert.Equal(RejectReason.RejectSanity, s.Decision);
        Assert.Equal(SanityFlags.SuspectedWalletTrap, s.Flags & SanityFlags.SuspectedWalletTrap);
        Assert.Equal(SanityFlags.BaseRouteClosed, s.Flags & SanityFlags.BaseRouteClosed);
    }

    [Fact]
    public void RouteClosed_NormalSpread_AllowedOnPrepositionedInventory_Capped()
    {
        WalletStatusBook w = AllOpen();
        w.Update(Venue.Bybit, _m.Btc, new[] { new WalletNetworkStatus(Erc20, false, true, Now) }); // deposit closed on sell venue
        SanityResult s = SanityGuard.Evaluate(Spot(20), w, Now, _limits);
        Assert.Equal(RejectReason.Execute, s.Decision);
        Assert.Equal(0.2 * 0.25, s.QtyCapCanon, 1e-12);
        Assert.Equal(SanityFlags.InventoryCapApplied, s.Flags & SanityFlags.InventoryCapApplied);

        SanityResult empty = SanityGuard.Evaluate(Spot(20) with { SellVenueBaseInventory = 0 }, w, Now, _limits);
        Assert.Equal(RejectReason.RejectInventory, empty.Decision);
    }

    [Fact]
    public void QuoteRouteClosed_CapsByQuoteInventory()
    {
        WalletStatusBook w = AllOpen();
        w.Update(Venue.Bybit, _m.Usdt, new[] { new WalletNetworkStatus(Trc20, true, false, Now), new WalletNetworkStatus(Erc20, true, false, Now) });
        SanityResult s = SanityGuard.Evaluate(Spot(20), w, Now, _limits);
        Assert.Equal(RejectReason.Execute, s.Decision);
        Assert.Equal(12_000 * 0.25 / 60_000, s.QtyCapCanon, 1e-12);
        Assert.Equal(SanityFlags.QuoteRouteClosed, s.Flags & SanityFlags.QuoteRouteClosed);
    }

    [Fact]
    public void StaleWalletStatus_IsFailSafe()
    {
        SanityResult s = SanityGuard.Evaluate(Spot(60), AllOpen(), Now + 10_000_000, _limits);
        Assert.Equal(RejectReason.RejectSanity, s.Decision);
        Assert.Equal(SanityFlags.BaseRouteUnknown, s.Flags & SanityFlags.BaseRouteUnknown);
    }

    [Fact]
    public void PerpLegs_IgnoreCoinWallet()
    {
        var w = new WalletStatusBook(_m.Assets); // nothing known at all
        var perpPerp = new SanityInput { Strategy = StrategyKind.Spatial, Buy = _m.BinancePepePerp, Sell = _m.BybitPepePerp, GrossEdgeBps = 120 };
        Assert.Equal(RejectReason.Execute, SanityGuard.Evaluate(perpPerp, w, Now, _limits).Decision);
        var basis = new SanityInput { Strategy = StrategyKind.FundingSpotPerp, Buy = _m.BitgetPepeSpot, Sell = _m.BybitPepePerp, GrossEdgeBps = 5 };
        Assert.Equal(RejectReason.Execute, SanityGuard.Evaluate(basis, w, Now, _limits).Decision);
    }

    [Fact]
    public void EdgeTooLarge_And_NotTrading_And_MarketMismatch()
    {
        Assert.Equal(RejectReason.RejectSanity, SanityGuard.Evaluate(Spot(301), AllOpen(), Now, _limits).Decision);
        Assert.Equal(RejectReason.RejectSanity, SanityGuard.Evaluate(Spot(double.NaN), AllOpen(), Now, _limits).Decision);

        var mismatch = new SanityInput { Strategy = StrategyKind.Spatial, Buy = _m.BitgetPepeSpot, Sell = _m.BybitPepePerp, GrossEdgeBps = 10 };
        SanityResult m = SanityGuard.Evaluate(mismatch, AllOpen(), Now, _limits);
        Assert.Equal(SanityFlags.MarketTypeMismatch, m.Flags & SanityFlags.MarketTypeMismatch);

        var halted = Arb.Core.Instruments.InstrumentRegistryBuilder.From(_m.Registry);
        halted.Replace(_m.BybitBtcSpot.Key, 0.01m, 0.000001m, 0m, 5m, Arb.Core.Instruments.InstrumentStatus.Halted);
        var reg = halted.Build();
        SanityResult h = SanityGuard.Evaluate(Spot(10) with { Sell = reg[_m.BybitBtcSpot.Key] }, AllOpen(), Now, _limits);
        Assert.Equal(SanityFlags.InstrumentNotTrading, h.Flags);
    }
}

public class StaleDataGuardTests
{
    private const long T = 5_000_000_000; // local mono/unix origin (µs)
    private static readonly FreshnessLimits Limits = FreshnessLimits.From(new RiskLimits());
    private static readonly InstrumentKey Key = new(Venue.Bybit, MarketType.Spot, 1);

    private static BookView Book(long exchangeUnixUs, long recvUnixUs, long recvMonoUs, BookHealth health = BookHealth.Synced)
    {
        var v = new BookView(4);
        v.Load(Key, new[] { new PriceLevel(100, 1) }, new[] { new PriceLevel(101, 1) }, new BookStamp(1, exchangeUnixUs, recvUnixUs, recvMonoUs), health);
        return v;
    }

    [Fact]
    public void VenueClockAhead_NaiveCheckWouldMissLatency_CorrectedCatchesIt()
    {
        // venue clock 40 ms ahead; true transport 160 ms → naive (recv − exch) = 120 ms looks OK
        var clock = new VenueClock(offsetMs: 40, uncertaintyMs: 2, isValid: true);
        long eventLocal = T;
        BookView b = Book(eventLocal + 40_000, eventLocal + 160_000, T + 160_000);
        Freshness f = StaleDataGuard.Check(b, T + 161_000, T + 161_000, clock, Limits, out double lat);
        Assert.Equal(Freshness.ExchangeLatency, f);
        Assert.Equal(160, lat, 1e-6);
    }

    [Fact]
    public void VenueClockBehind_NaiveCheckWouldReject_CorrectedAccepts()
    {
        // venue clock 100 ms behind; true transport 30 ms → naive = 130 ms; with skew ±… still fresh after correction
        var clock = new VenueClock(offsetMs: -100, uncertaintyMs: 2, isValid: true);
        BookView b = Book(T - 100_000, T + 30_000, T + 30_000);
        Assert.Equal(Freshness.Fresh, StaleDataGuard.Check(b, T + 31_000, T + 31_000, clock, Limits, out double lat));
        Assert.Equal(30, lat, 1e-6);
    }

    [Fact]
    public void OrderOfChecks_IntegrityFirst()
    {
        var clock = new VenueClock(0, 1, true);
        Assert.Equal(Freshness.BookNotUsable, StaleDataGuard.Check(Book(T, T, T, BookHealth.Invalid), T, T, clock, Limits, out _));
        Assert.Equal(Freshness.ConnectionSilent, StaleDataGuard.Check(Book(T, T, T), T + 4_000_000, T, clock, Limits, out _));
        Assert.Equal(Freshness.ClockUncertain, StaleDataGuard.Check(Book(T, T, T), T, T, new VenueClock(0, 50, true), Limits, out _));
        Assert.Equal(Freshness.ClockUncertain, StaleDataGuard.Check(Book(T, T, T), T, T, default, Limits, out _));
        Assert.Equal(Freshness.BookSilent, StaleDataGuard.Check(Book(T, T, T), T + 6_000_000, T + 6_000_000, clock, Limits, out _));
        Assert.Equal(Freshness.Fresh, StaleDataGuard.Check(Book(T, T + 5_000, T + 5_000), T + 6_000, T + 6_000, clock, Limits, out _));
    }

    [Fact]
    public void Pair_ReportsFirstFailure_AndWorstLatency()
    {
        var clock = new VenueClock(0, 1, true);
        BookView a = Book(T, T + 5_000, T + 5_000);
        BookView b = Book(T, T + 90_000, T + 90_000);
        Assert.Equal(Freshness.Fresh, StaleDataGuard.CheckPair(a, T + 91_000, clock, b, T + 91_000, clock, T + 91_000, Limits, out double worst));
        Assert.Equal(90, worst, 1e-6);
        BookView c = Book(T, T + 200_000, T + 200_000);
        Assert.Equal(Freshness.ExchangeLatency, StaleDataGuard.CheckPair(a, T + 201_000, clock, c, T + 201_000, clock, T + 201_000, Limits, out _));
    }
}

public class RiskLimitsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        Assert.Empty(new RiskLimits().Validate());
    }

    [Fact]
    public void InvalidCombinations_AreReported()
    {
        var bad = new RiskLimits
        {
            MarginWarnBps = 4_000,
            RepairLadderBps = new[] { 30.0, 10.0 },
            SevereLossBps = 10,
            LiquidityHaircutPpm = 0,
            MinEntryLiqDistanceBps = 1_000,
        };
        IReadOnlyList<string> errors = bad.Validate();
        Assert.True(errors.Count >= 5, string.Join("; ", errors));
    }
}
