using Arb.Core.Inventory;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// C-09 (S1, ChatGPT re-review of 30417db). The C-07 host contract told adapters to stamp a REST snapshot without an exact
/// as-of with a LOWER bound ("errs on the safe side"). It does not: the ledger then treats the snapshot as older than it
/// is, and a delayed stream event stamped between the lower bound and the real as-of overwrites it. Reviewer sequence:
/// 100 (stream @1000) → snapshot 20 read at 1010, stamped 1001 → delayed stream 100 @1005 → Available 100,
/// reserve 80 succeeds, no reconcile flag.
/// Contract after the fix: a snapshot is an INTERVAL [asOfNotBefore, asOfNotAfter] (request sent minus cache lag,
/// response time). An observation raises the balance only when it is definitely newer than everything the slot holds
/// (its lower bound > their upper bound, or the next event of the ordered stream); overlapping observations are a
/// Conflict that keeps the lower value; reservations are released only by fills strictly before the lower bound.
/// </summary>
public class C09_SnapshotIntervalTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    private (InventoryLedger L, int Slot) Ledger()
    {
        var l = new InventoryLedger(_m.Assets, 16);
        return (l, l.SlotOf(Venue.Bitget, AccountKind.Spot, _m.Usdt));
    }

    [Fact]
    public void ReviewerSequence_DelayedStreamEventInsideTheSnapshotInterval_CannotRaiseTheBalance()
    {
        (InventoryLedger l, int s) = Ledger();
        Assert.Equal(BalanceUpdate.Applied, l.OnBalance(s, 100m, 1000, 1));
        Assert.Equal(BalanceUpdate.Applied, l.OnBalanceSnapshot(s, 20m, asOfNotBeforeMs: 1001, asOfNotAfterMs: 1010));
        Assert.Equal(BalanceUpdate.Conflict, l.OnBalance(s, 100m, 1005, 2));
        Assert.Equal(20m, l.Available(s));
        Assert.True(l.NeedsReconcile(s));
        Assert.False(l.TryReserve(s, 80m, out _));

        Assert.Equal(BalanceUpdate.Applied, l.OnBalance(s, 20m, 1011, 3)); // after the interval: provably newer
        Assert.False(l.NeedsReconcile(s));
        Assert.Equal(20m, l.Available(s));
    }

    [Fact]
    public void StreamBeforeTheInterval_IsStale_AfterIt_IsNewer()
    {
        (InventoryLedger l, int s) = Ledger();
        l.OnBalanceSnapshot(s, 50m, 1001, 1010);
        Assert.Equal(BalanceUpdate.Stale, l.OnBalance(s, 70m, 1000, 1));
        Assert.Equal(50m, l.VenueFree(s));
        Assert.Equal(BalanceUpdate.Applied, l.OnBalance(s, 70m, 1011, 2));
        Assert.Equal(70m, l.VenueFree(s));
    }

    [Fact]
    public void SnapshotRaisesOnlyWhenItsLowerBoundIsAfterTheSlotsUpperBound()
    {
        (InventoryLedger l, int s) = Ledger();
        l.OnBalance(s, 20m, 1005, 1);
        Assert.Equal(BalanceUpdate.Conflict, l.OnBalanceSnapshot(s, 100m, 1003, 1009)); // as-of may be before 1005
        Assert.Equal(20m, l.VenueFree(s));
        Assert.Equal(BalanceUpdate.Applied, l.OnBalanceSnapshot(s, 100m, 1010, 1012));  // as-of ≥ 1010 > 1009
        Assert.Equal(100m, l.VenueFree(s));
    }

    [Fact]
    public void Release_NeedsTheFillStrictlyBeforeTheLowerBound()
    {
        (InventoryLedger l, int s) = Ledger();
        l.OnBalance(s, 100m, 1000, 1);
        Assert.True(l.TryReserve(s, 80m, out ReservationHandle h));
        l.MarkTerminal(h, venueEventMs: 1003, nowMonoMicros: 0);
        l.OnBalanceSnapshot(s, 100m, 1001, 1010);      // the read may predate the fill at 1003
        Assert.True(l.IsActive(h));
        Assert.Equal(20m, l.Available(s));
        l.OnBalanceSnapshot(s, 20m, 1011, 1015);       // as-of ≥ 1011 > 1003: reflects the fill
        Assert.False(l.IsActive(h));
        Assert.Equal(20m, l.Available(s));
    }

    [Fact]
    public void MarkTerminal_ReleasesAtOnceOnlyWhenTheSlotsLowerBoundIsAfterTheFill()
    {
        (InventoryLedger l, int s) = Ledger();
        l.OnBalance(s, 100m, 1000, 1);
        Assert.True(l.TryReserve(s, 30m, out ReservationHandle a));
        Assert.True(l.TryReserve(s, 30m, out ReservationHandle b));
        l.OnBalanceSnapshot(s, 100m, 1004, 1010);
        l.MarkTerminal(a, venueEventMs: 1006, nowMonoMicros: 0); // inside the interval: maybe not reflected
        l.MarkTerminal(b, venueEventMs: 1003, nowMonoMicros: 0); // before the lower bound: reflected (unfilled IOC)
        Assert.True(l.IsActive(a));
        Assert.False(l.IsActive(b));
    }

    [Fact]
    public void InConflict_AFillReflectedByAnyHeldObservation_IsReleased()
    {
        // Held: A [1006,1010] = 20 (after the fill at 1005) and B [1003,1008] = 100 (may predate it) → Conflict, value 20.
        // A reflects the fill, so the newest delivered state does too; the value (the minimum) is not above it.
        (InventoryLedger l, int s) = Ledger();
        l.OnBalance(s, 100m, 1000, 1);
        Assert.True(l.TryReserve(s, 80m, out ReservationHandle h));
        l.OnBalanceSnapshot(s, 20m, 1006, 1010);
        Assert.Equal(BalanceUpdate.Conflict, l.OnBalanceSnapshot(s, 100m, 1003, 1008));
        l.MarkTerminal(h, venueEventMs: 1005, nowMonoMicros: 0);
        Assert.False(l.IsActive(h));
        Assert.Equal(20m, l.Available(s));
    }

    [Fact]
    public void DefinitelyOlderThanAnyHeldObservation_IsStale_EvenInConflict()
    {
        (InventoryLedger l, int s) = Ledger();
        l.OnBalance(s, 50m, 1000, 1);
        l.OnBalanceSnapshot(s, 40m, 1005, 1010);
        Assert.Equal(BalanceUpdate.Conflict, l.OnBalanceSnapshot(s, 30m, 1000, 1007)); // holds [1000..1010], value 30
        Assert.Equal(BalanceUpdate.Stale, l.OnBalance(s, 10m, 1003));                   // ends before 1005: older than the 40
        Assert.Equal(30m, l.VenueFree(s));
    }

    [Fact]
    public void AnOverlappingDuplicate_WidensWhatANewerObservationMustBeat()
    {
        (InventoryLedger l, int s) = Ledger();
        l.OnBalanceSnapshot(s, 20m, 1001, 1010);
        Assert.Equal(BalanceUpdate.Duplicate, l.OnBalanceSnapshot(s, 20m, 1005, 1020)); // as-of may be as late as 1020
        Assert.Equal(BalanceUpdate.Conflict, l.OnBalance(s, 100m, 1012, 1));             // beats 1010, not 1020
        Assert.Equal(20m, l.VenueFree(s));
        Assert.Equal(BalanceUpdate.Applied, l.OnBalance(s, 100m, 1021, 2));
        Assert.Equal(100m, l.VenueFree(s));
    }

    [Fact]
    public void IntervalMustBeOrdered()
    {
        (InventoryLedger l, int s) = Ledger();
        Assert.Throws<ArgumentOutOfRangeException>(() => l.OnBalanceSnapshot(s, 1m, 1010, 1001));
        Assert.Equal(BalanceUpdate.Applied, l.OnBalanceSnapshot(s, 1m, 1005, 1005)); // exact as-of is the degenerate interval
    }

    [Fact]
    public void IntervalModel_OrderedStream_NeverOverstates()
    {
        int sameMs = 0;
        int interval = 0;
        for (int seed = 0; seed < 3_000; seed++)
        {
            string? failure = LedgerModel.Run(_m, seed, steps: 160, Sequenced, Interval, maxJitterMs: 4, ref sameMs, ref interval);
            Assert.True(failure is null, failure);
        }

        Assert.True(interval >= 1_000, $"coverage: only {interval} delayed stream events inside a newer snapshot's interval");
    }

    [Fact]
    public void IntervalModel_UnsequencedStream_NeverOverstates()
    {
        int sameMs = 0;
        int interval = 0;
        for (int seed = 0; seed < 3_000; seed++)
        {
            string? failure = LedgerModel.Run(_m, seed, steps: 160, Unsequenced, Interval, maxJitterMs: 4, ref sameMs, ref interval);
            Assert.True(failure is null, failure);
        }

        Assert.True(interval >= 1_000, $"coverage: only {interval} delayed stream events inside a newer snapshot's interval");
    }

    private static void Sequenced(InventoryLedger l, int slot, decimal value, long ms, long seq) => l.OnBalance(slot, value, ms, seq);

    private static void Unsequenced(InventoryLedger l, int slot, decimal value, long ms, long seq) => l.OnBalance(slot, value, ms);

    private static void Interval(InventoryLedger l, int slot, decimal value, long lo, long hi) => l.OnBalanceSnapshot(slot, value, lo, hi);
}
