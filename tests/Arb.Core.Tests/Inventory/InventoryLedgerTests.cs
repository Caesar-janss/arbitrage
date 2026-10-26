using Arb.Core.Inventory;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Inventory;

public class InventoryLedgerTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    private (InventoryLedger Ledger, int UsdtA, int BtcB) Setup(int capacity = 16)
    {
        var ledger = new InventoryLedger(_m.Assets, capacity);
        int usdtA = ledger.SlotOf(Venue.Bitget, AccountKind.Spot, _m.Usdt);
        int btcB = ledger.SlotOf(Venue.Bybit, AccountKind.Unified, _m.Btc);
        ledger.OnBalance(usdtA, 1_000m, 100);
        ledger.OnBalance(btcB, 0.05m, 100);
        return (ledger, usdtA, btcB);
    }

    [Fact]
    public void ReservePair_IsAtomic_AndPreventsDoubleSpend()
    {
        (InventoryLedger l, int usdtA, int btcB) = Setup();
        Assert.True(l.TryReservePair(usdtA, 600.6m, btcB, 0.01m, out ReservationHandle a, out ReservationHandle b));
        Assert.Equal(399.4m, l.Available(usdtA));
        Assert.Equal(0.04m, l.Available(btcB));

        // a second opportunity wanting 600 USDT must fail even though venue still reports 1000 free
        Assert.False(l.TryReservePair(usdtA, 600m, btcB, 0.01m, out _, out _));
        Assert.Equal(2, l.ActiveReservations);

        // second leg short → nothing reserved at all
        Assert.False(l.TryReservePair(usdtA, 100m, btcB, 1m, out _, out _));
        Assert.Equal(399.4m, l.Available(usdtA));
        Assert.True(l.IsActive(a) && l.IsActive(b));
    }

    [Fact]
    public void TerminalReservation_ReleasedOnlyByLaterBalanceEvent()
    {
        (InventoryLedger l, int usdtA, _) = Setup();
        Assert.True(l.TryReserve(usdtA, 600m, out ReservationHandle h));
        Assert.True(l.MarkTerminal(h, venueEventMs: 200, nowMonoMicros: 0));
        Assert.Equal(400m, l.Available(usdtA));

        l.OnBalance(usdtA, 1_000m, 150); // older than the fill → keep reservation (conservative)
        Assert.Equal(400m, l.Available(usdtA));

        l.OnBalance(usdtA, 399.4m, 205); // reflects the fill
        Assert.Equal(399.4m, l.Available(usdtA));
        Assert.Equal(0m, l.Reserved(usdtA));
        Assert.False(l.IsActive(h));
    }

    [Fact]
    public void BalanceBeforeTerminalReport_ReleasesImmediatelyOnTerminal()
    {
        (InventoryLedger l, int usdtA, _) = Setup();
        Assert.True(l.TryReserve(usdtA, 600m, out ReservationHandle h));
        l.OnBalance(usdtA, 399.4m, 300); // user-stream balance arrived first
        Assert.Equal(-200.6m, l.Available(usdtA)); // double-counted: safe direction
        Assert.True(l.MarkTerminal(h, venueEventMs: 290, nowMonoMicros: 0));
        Assert.Equal(399.4m, l.Available(usdtA));
    }

    [Fact]
    public void OutOfOrderBalance_IsIgnored()
    {
        (InventoryLedger l, int usdtA, _) = Setup();
        l.OnBalance(usdtA, 500m, 50);
        Assert.Equal(1_000m, l.VenueFree(usdtA));
    }

    [Fact]
    public void Release_And_StaleHandles()
    {
        (InventoryLedger l, int usdtA, _) = Setup();
        Assert.True(l.TryReserve(usdtA, 100m, out ReservationHandle h));
        Assert.True(l.Release(h));
        Assert.False(l.Release(h)); // double release rejected
        Assert.True(l.TryReserve(usdtA, 50m, out ReservationHandle h2)); // slot reused, new generation
        Assert.False(l.Release(h));
        Assert.Equal(950m, l.Available(usdtA));
        Assert.True(l.Release(h2));
        Assert.False(l.Release(default));
    }

    [Fact]
    public void SettleTimeout_ReleasesTerminalReservations()
    {
        (InventoryLedger l, int usdtA, _) = Setup();
        Assert.True(l.TryReserve(usdtA, 100m, out ReservationHandle h));
        Assert.True(l.TryReserve(usdtA, 100m, out ReservationHandle open));
        l.MarkTerminal(h, venueEventMs: 1_000, nowMonoMicros: 10_000);
        Assert.Equal(0, l.ExpireSettled(nowMonoMicros: 1_000_000, settleTimeoutMicros: 5_000_000));
        Assert.Equal(1, l.ExpireSettled(nowMonoMicros: 6_000_000, settleTimeoutMicros: 5_000_000));
        Assert.True(l.IsActive(open)); // never expire a non-terminal (in-flight) reservation
        Assert.Equal(900m, l.Available(usdtA));
    }

    [Fact]
    public void PoolExhaustion_FailsClosed()
    {
        (InventoryLedger l, int usdtA, _) = Setup(capacity: 2);
        Assert.True(l.TryReserve(usdtA, 1m, out _));
        Assert.True(l.TryReserve(usdtA, 1m, out _));
        Assert.False(l.TryReserve(usdtA, 1m, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => l.TryReserve(usdtA, 0m, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => l.SlotOf(Venue.Unknown, AccountKind.Spot, _m.Usdt));
    }

    [Fact]
    public void SameSlotPair_NeedsCombinedBalance()
    {
        (InventoryLedger l, int usdtA, _) = Setup();
        Assert.False(l.TryReservePair(usdtA, 600m, usdtA, 600m, out _, out _));
        Assert.True(l.TryReservePair(usdtA, 500m, usdtA, 500m, out _, out _));
        Assert.Equal(0m, l.Available(usdtA));
    }
}
