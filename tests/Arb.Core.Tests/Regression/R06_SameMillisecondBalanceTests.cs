using Arb.Core.Inventory;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-06 (S2), same-millisecond half (P07, pre-existing API): order and wallet events come from different venue
/// services; a balance event stamped in the SAME millisecond as the fill may have been generated before the fill was
/// booked. Releasing on "balance time ≥ terminal time" overstated Available (0.05 vs a true 0.02 BTC) and let a second
/// opportunity reserve coins that are gone → its sell IOC is rejected while the buy leg fills → residual.
/// Release only on a STRICTLY later balance event.
/// </summary>
public class R06_SameMillisecondBalanceTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    private (InventoryLedger L, int Slot) Ledger()
    {
        var l = new InventoryLedger(_m.Assets, 16);
        int btc = l.SlotOf(Venue.Bybit, AccountKind.Unified, _m.Btc);
        l.OnBalance(btc, 0.05m, 100);
        return (l, btc);
    }

    [Fact]
    public void BalanceEventInTheSameMs_DoesNotReleaseTheFilledReservation()
    {
        (InventoryLedger l, int btc) = Ledger();
        Assert.True(l.TryReserve(btc, 0.03m, out ReservationHandle h));
        l.MarkTerminal(h, venueEventMs: 200, nowMonoMicros: 0); // sell filled 0.03 at venue time 200
        l.OnBalance(btc, 0.05m, 200);                          // same ms, does NOT reflect the fill yet
        Assert.Equal(0.02m, l.Available(btc));                 // v0.2: 0.05 (overstated by 0.03)
        Assert.True(l.IsActive(h));
        Assert.False(l.TryReserve(btc, 0.04m, out _));         // v0.2: true → second sell leg would be rejected by the venue
    }

    [Fact]
    public void TerminalReportAfterASameMsBalance_DoesNotReleaseImmediately()
    {
        (InventoryLedger l, int btc) = Ledger();
        Assert.True(l.TryReserve(btc, 0.03m, out ReservationHandle h));
        l.OnBalance(btc, 0.05m, 200);
        l.MarkTerminal(h, venueEventMs: 200, nowMonoMicros: 0);
        Assert.True(l.IsActive(h));                            // v0.2: released at once
        Assert.Equal(0.02m, l.Available(btc));
    }

    [Fact]
    public void StrictlyLaterBalance_ReleasesAsBefore()
    {
        (InventoryLedger l, int btc) = Ledger();
        Assert.True(l.TryReserve(btc, 0.03m, out ReservationHandle h));
        l.MarkTerminal(h, venueEventMs: 200, nowMonoMicros: 0);
        l.OnBalance(btc, 0.02m, 201);
        Assert.False(l.IsActive(h));
        Assert.Equal(0.02m, l.Available(btc));
    }
}
