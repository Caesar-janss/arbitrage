using Arb.Core.Inventory;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-06 (S2), timeout half (P07): when no balance event arrives, v0.2 ExpireSettled RELEASED the filled reservation →
/// Available 0.05 BTC while the venue holds 0.02. A timeout is a reason to reconcile (REST), never to release: the
/// reservation stays, the slot is flagged, and a strictly later balance (stream or REST) settles it.
/// </summary>
public class R06_UnsettledReservationTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    [Fact]
    public void Timeout_FlagsForReconcile_ButKeepsTheReservation()
    {
        var l = new InventoryLedger(_m.Assets, 16);
        int btc = l.SlotOf(Venue.Bybit, AccountKind.Unified, _m.Btc);
        l.OnBalance(btc, 0.05m, 100);
        Assert.True(l.TryReserve(btc, 0.03m, out ReservationHandle h));
        l.MarkTerminal(h, venueEventMs: 200, nowMonoMicros: 0);

        Assert.Equal(0, l.FlagUnsettled(nowMonoMicros: 1_000_000, settleTimeoutMicros: 5_000_000));
        Assert.False(l.NeedsReconcile(btc));
        Assert.Equal(1, l.FlagUnsettled(nowMonoMicros: 5_000_000, settleTimeoutMicros: 5_000_000));
        Assert.Equal(0, l.FlagUnsettled(nowMonoMicros: 9_000_000, settleTimeoutMicros: 5_000_000)); // reported once
        Assert.True(l.NeedsReconcile(btc));
        Assert.True(l.IsActive(h));
        Assert.Equal(0.02m, l.Available(btc));                 // v0.2: 0.05
        Assert.False(l.TryReserve(btc, 0.04m, out _));

        l.OnBalance(btc, 0.02m, 5_300);                        // REST reconcile result (server time) or late stream event
        Assert.False(l.IsActive(h));
        Assert.False(l.NeedsReconcile(btc));
        Assert.Equal(0.02m, l.Available(btc));
    }

    [Fact]
    public void InFlightReservations_AreNeverFlagged()
    {
        var l = new InventoryLedger(_m.Assets, 16);
        int usdt = l.SlotOf(Venue.Bitget, AccountKind.Spot, _m.Usdt);
        l.OnBalance(usdt, 1_000m, 100);
        Assert.True(l.TryReserve(usdt, 100m, out ReservationHandle open));
        Assert.Equal(0, l.FlagUnsettled(nowMonoMicros: 60_000_000, settleTimeoutMicros: 5_000_000));
        Assert.True(l.IsActive(open));
    }
}
