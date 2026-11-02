using Arb.Core.Inventory;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// C-07 (S1, ChatGPT review of 4bf87f7; pre-existing API). Two balance observations stamped in the SAME venue
/// millisecond cannot be ordered by their timestamp: a late REST snapshot (or a second source) may describe the slot
/// before a change booked in that millisecond. v0.3 rejected only strictly older stamps, so the stale observation
/// overwrote the newer one after the reservation had been released: Available 20 → 100 and an 80 reservation succeeded
/// on funds that are gone.
///
/// Contract after the fix: an unsequenced observation at the same millisecond with a DIFFERENT value is a conflict.
/// The ledger keeps the lower of the two values (one of them is the true latest, so the lower one cannot overstate),
/// flags the slot for a reconcile, and the next strictly later observation resolves it.
/// </summary>
public class C07_StaleBalanceOverwriteTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    private (InventoryLedger L, int Slot) Ledger(int capacity = 16)
    {
        var l = new InventoryLedger(_m.Assets, capacity);
        int usdt = l.SlotOf(Venue.Bitget, AccountKind.Spot, _m.Usdt);
        return (l, usdt);
    }

    [Fact]
    public void ReviewerSequence_StaleSameMsSnapshot_DoesNotRevivePaidFunds()
    {
        (InventoryLedger l, int slot) = Ledger();
        l.OnBalance(slot, 100m, 1000);
        Assert.True(l.TryReserve(slot, 80m, out ReservationHandle h));
        l.MarkTerminal(h, venueEventMs: 1001, nowMonoMicros: 10);
        l.OnBalance(slot, 20m, 1002);                          // reflects the fill → releases the reservation
        Assert.False(l.IsActive(h));
        Assert.Equal(20m, l.Available(slot));

        l.OnBalance(slot, 100m, 1002);                         // late snapshot, same ms, older content
        Assert.Equal(20m, l.Available(slot));                  // v0.3: 100
        Assert.False(l.TryReserve(slot, 80m, out _));          // v0.3: true → order on funds that are gone
        Assert.True(l.NeedsReconcile(slot));                   // v0.3: false
    }

    [Fact]
    public void UnreservedChangeInTheSameMs_IsNotUndoneByAnOlderSnapshot()
    {
        // A change no reservation covers (funding fee, transfer, interest): nothing else protects Available.
        (InventoryLedger l, int slot) = Ledger();
        l.OnBalance(slot, 100m, 1000);
        l.OnBalance(slot, 95m, 1002);                          // funding fee −5 booked in ms 1002
        l.OnBalance(slot, 100m, 1002);                         // snapshot taken earlier in ms 1002
        Assert.Equal(95m, l.Available(slot));                  // v0.3: 100
        Assert.False(l.TryReserve(slot, 96m, out _));
    }

    [Fact]
    public void ConflictKeepsTheLowerValue_InEitherArrivalOrder()
    {
        (InventoryLedger a, int sa) = Ledger();
        a.OnBalance(sa, 50m, 1000);
        a.OnBalance(sa, 70m, 1000);
        Assert.Equal(50m, a.VenueFree(sa));                    // v0.3: 70

        (InventoryLedger b, int sb) = Ledger();
        b.OnBalance(sb, 70m, 1000);
        b.OnBalance(sb, 50m, 1000);
        Assert.Equal(50m, b.VenueFree(sb));
    }

    [Fact]
    public void StrictlyLaterObservation_ResolvesTheConflict()
    {
        (InventoryLedger l, int slot) = Ledger();
        l.OnBalance(slot, 20m, 1002);
        l.OnBalance(slot, 100m, 1002);
        Assert.True(l.NeedsReconcile(slot));                   // v0.3: false
        l.OnBalance(slot, 100m, 1003);                         // reconcile result stamped with its venue as-of time
        Assert.False(l.NeedsReconcile(slot));
        Assert.Equal(100m, l.Available(slot));
    }

    [Fact]
    public void IdenticalSameMsObservation_IsADuplicate_NotAConflict()
    {
        (InventoryLedger l, int slot) = Ledger();
        l.OnBalance(slot, 20m, 1002);
        l.OnBalance(slot, 20m, 1002);
        Assert.False(l.NeedsReconcile(slot));
        Assert.Equal(20m, l.Available(slot));
    }

    /// <summary>
    /// Model check (pre-existing API, every observation unsequenced). A true venue history is generated: our fills
    /// (reserved first), unreserved changes (fees, transfers, deposits), several changes per millisecond. Stream events
    /// are delivered in order but late; snapshots are stamped with the venue time they were taken (their as-of time) and
    /// delivered in any order. Invariant after every step, with P = the causally newest observation delivered so far:
    ///   Available ≤ F(P) − Σ our fills booked after P.
    /// A ledger cannot know unreserved changes it has not seen, so this is the strongest bound available to it.
    /// </summary>
    [Fact]
    public void RandomInterleavings_NeverOverstateRelativeToTheNewestDeliveredObservation()
    {
        int sameMsReorders = 0;
        for (int seed = 0; seed < 1_500; seed++)
        {
            string? failure = LedgerModel.Run(_m, seed, steps: 160, Unsequenced, ref sameMsReorders);
            Assert.True(failure is null, failure);
        }

        Assert.True(sameMsReorders >= 200, $"coverage: only {sameMsReorders} same-ms reorders with differing values");
    }

    private static void Unsequenced(InventoryLedger l, int slot, decimal value, long ms, long seq) => l.OnBalance(slot, value, ms);
}

/// <summary>
/// Shared discrete-event model for C-07 / C-09. <paramref name="deliverStream"/> decides how the user-stream events reach
/// the ledger (unsequenced, or with a delivery sequence). Snapshots carry an as-of INTERVAL [t − jitter, t + jitter]
/// around the venue time t they were taken (C-09: a REST snapshot's as-of is only known to lie in such an interval);
/// with jitter 0 they are exact-as-of points delivered through the pre-existing 3-argument API.
/// </summary>
internal static class LedgerModel
{
    private enum Op { Fill, Unreserved, Snapshot, DeliverStream, DeliverTerminal, DeliverSnapshot }

    public delegate void StreamDelivery(InventoryLedger ledger, int slot, decimal value, long venueEventMs, long sequence);

    public delegate void SnapshotDelivery(InventoryLedger ledger, int slot, decimal value, long asOfNotBeforeMs, long asOfNotAfterMs);

    public static string? Run(TestMarkets m, int seed, int steps, StreamDelivery deliverStream, ref int sameMsReorders)
    {
        int unused = 0;
        return Run(m, seed, steps, deliverStream, ExactSnapshot, maxJitterMs: 0, ref sameMsReorders, ref unused);
    }

    /// <param name="intervalReorders">
    /// Coverage of the C-09 case: a stream event delivered after a snapshot whose interval contains the event's time,
    /// although the event is causally OLDER than the snapshot and carries another value.
    /// </param>
    public static string? Run(TestMarkets m, int seed, int steps, StreamDelivery deliverStream, SnapshotDelivery deliverSnapshot, int maxJitterMs, ref int sameMsReorders, ref int intervalReorders)
    {
        var rng = new Random(seed);
        var l = new InventoryLedger(m.Assets, 256);
        int slot = l.SlotOf(Venue.Bitget, AccountKind.Spot, m.Usdt);

        var history = new List<(long Ms, decimal Value)>();     // value AFTER each change, in true order
        var ourFills = new List<(int Pos, decimal Amount)>();
        var stream = new Queue<int>();
        var terminals = new Queue<(ReservationHandle H, long Ms)>();
        var snapshots = new List<(int Pos, long Lo, long Hi)>();
        var deliveredSnapshots = new List<(int Pos, long Lo, long Hi)>();
        long ms = 1_000;
        decimal f = 100m;
        long seq = 0;
        history.Add((ms, f));
        deliverStream(l, slot, f, ms, ++seq);
        int newest = 0;
        long newestStampMs = ms;

        for (int step = 0; step < steps; step++)
        {
            if (rng.Next(4) == 0)
            {
                ms++;
            }

            Op op = (Op)rng.Next(6);
            switch (op)
            {
                case Op.Fill:
                {
                    decimal amount = rng.Next(1, 30);
                    if (l.TryReserve(slot, amount, out ReservationHandle h))
                    {
                        f -= amount;
                        history.Add((ms, f));
                        ourFills.Add((history.Count - 1, amount));
                        terminals.Enqueue((h, ms));
                        stream.Enqueue(history.Count - 1);
                    }

                    break;
                }

                case Op.Unreserved:
                {
                    int delta = rng.Next(-6, 7);
                    if (delta == 0)
                    {
                        break;
                    }

                    f += delta;
                    history.Add((ms, f));
                    stream.Enqueue(history.Count - 1);
                    break;
                }

                case Op.Snapshot:
                    // as-of = the venue time it was taken; the host only knows an interval around it
                    snapshots.Add((history.Count - 1, ms - rng.Next(0, maxJitterMs + 1), ms + rng.Next(0, maxJitterMs + 1)));
                    break;

                case Op.DeliverStream:
                    if (stream.Count > 0)
                    {
                        int pos = stream.Dequeue();
                        Track(pos, history[pos].Ms, history, ref newest, ref newestStampMs, ref sameMsReorders);
                        foreach ((int sp, long lo, long hi) in deliveredSnapshots)
                        {
                            if (sp > pos && lo <= history[pos].Ms && history[pos].Ms <= hi && history[sp].Value != history[pos].Value)
                            {
                                intervalReorders++;
                                break;
                            }
                        }

                        deliverStream(l, slot, history[pos].Value, history[pos].Ms, ++seq);
                    }

                    break;

                case Op.DeliverTerminal:
                    if (terminals.Count > 0)
                    {
                        (ReservationHandle h, long tms) = terminals.Dequeue();
                        l.MarkTerminal(h, tms, 0);
                    }

                    break;

                case Op.DeliverSnapshot:
                    if (snapshots.Count > 0)
                    {
                        int i = rng.Next(snapshots.Count);
                        (int pos, long lo, long hi) = snapshots[i];
                        snapshots.RemoveAt(i);
                        Track(pos, lo, history, ref newest, ref newestStampMs, ref sameMsReorders);
                        deliveredSnapshots.Add((pos, lo, hi));
                        deliverSnapshot(l, slot, history[pos].Value, lo, hi);
                    }

                    break;
            }

            decimal bound = history[newest].Value;
            foreach ((int pos, decimal amount) in ourFills)
            {
                if (pos > newest)
                {
                    bound -= amount;
                }
            }

            if (l.Available(slot) > bound)
            {
                return $"seed {seed} step {step} ({op}): Available {l.Available(slot)} > bound {bound} (newest delivered position {newest}, true value {history[newest].Value})";
            }
        }

        return null;
    }

    private static void ExactSnapshot(InventoryLedger l, int slot, decimal value, long asOfNotBeforeMs, long asOfNotAfterMs) =>
        l.OnBalance(slot, value, asOfNotBeforeMs); // jitter 0: lo == hi == exact as-of

    private static void Track(int pos, long stamp, List<(long Ms, decimal Value)> history, ref int newest, ref long newestStampMs, ref int sameMsReorders)
    {
        // The case C-07 is about: an OLDER observation arrives carrying the newest stamp seen so far, with another value.
        if (stamp == newestStampMs && pos < newest && history[pos].Value != history[newest].Value)
        {
            sameMsReorders++;
        }

        if (pos > newest)
        {
            newest = pos;
        }

        if (stamp > newestStampMs)
        {
            newestStampMs = stamp;
        }
    }
}
