using Arb.Core.Inventory;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// C-07 (S1), new API: outcome codes and the ordered-stream overload. Without a delivery sequence every same-ms change
/// would be a Conflict (safe, but each multi-level IOC would trigger a REST reconcile). The slot's ONE ordered user
/// stream passes its delivery sequence, so same-ms events of that stream apply in order; anything that cannot be
/// ordered against it still resolves to the lower value.
/// </summary>
public class C07_BalanceSequencingTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    private (InventoryLedger L, int Slot) Ledger()
    {
        var l = new InventoryLedger(_m.Assets, 16);
        return (l, l.SlotOf(Venue.Bybit, AccountKind.Unified, _m.Btc));
    }

    [Fact]
    public void UnsequencedOutcomes()
    {
        (InventoryLedger l, int s) = Ledger();
        Assert.Equal(BalanceUpdate.Applied, l.OnBalance(s, 1m, 100));
        Assert.Equal(BalanceUpdate.Duplicate, l.OnBalance(s, 1m, 100));
        Assert.Equal(BalanceUpdate.Stale, l.OnBalance(s, 5m, 99));
        Assert.Equal(BalanceUpdate.Conflict, l.OnBalance(s, 2m, 100));
        Assert.Equal(1m, l.VenueFree(s));
        Assert.True(l.HasBalanceConflict(s));
        Assert.Equal(BalanceUpdate.Applied, l.OnBalance(s, 2m, 101));
        Assert.False(l.HasBalanceConflict(s));
        Assert.Equal(2m, l.VenueFree(s));
    }

    [Fact]
    public void OrderedStream_SameMsIncreases_ApplyInDeliveryOrder_WithoutConflict()
    {
        // A buy IOC sweeping three levels: base credited three times inside one venue millisecond.
        (InventoryLedger l, int s) = Ledger();
        Assert.Equal(BalanceUpdate.Applied, l.OnBalance(s, 0m, 500, 1));
        Assert.Equal(BalanceUpdate.Applied, l.OnBalance(s, 0.003m, 501, 2));
        Assert.Equal(BalanceUpdate.Applied, l.OnBalance(s, 0.006m, 501, 3));
        Assert.Equal(BalanceUpdate.Applied, l.OnBalance(s, 0.010m, 501, 4));
        Assert.Equal(0.010m, l.VenueFree(s));
        Assert.False(l.NeedsReconcile(s));
    }

    [Fact]
    public void OrderedStream_ReplayedSequence_FallsBackToVenueTime()
    {
        (InventoryLedger l, int s) = Ledger();
        l.OnBalance(s, 0.006m, 501, 3);
        l.OnBalance(s, 0.010m, 501, 4);
        Assert.Equal(BalanceUpdate.Conflict, l.OnBalance(s, 0.006m, 501, 3)); // replay: no order, same ms, other value
        Assert.Equal(0.006m, l.VenueFree(s));
        Assert.Equal(BalanceUpdate.Stale, l.OnBalance(s, 0.003m, 500, 2));
        Assert.Equal(BalanceUpdate.Duplicate, l.OnBalance(s, 0.006m, 501, 4));
    }

    [Fact]
    public void OrderedStream_VenueTimeGoingBackwards_IsAConflict_NotADrop()
    {
        (InventoryLedger l, int s) = Ledger();
        l.OnBalance(s, 100m, 1002, 7);
        Assert.Equal(BalanceUpdate.Conflict, l.OnBalance(s, 20m, 1001, 8)); // delivered later, stamped earlier: a debit?
        Assert.Equal(20m, l.VenueFree(s));
        Assert.True(l.NeedsReconcile(s));
        Assert.Equal(BalanceUpdate.Applied, l.OnBalance(s, 20m, 1003, 9));
        Assert.False(l.NeedsReconcile(s));
    }

    [Fact]
    public void StreamAndSnapshot_InTheSameMs_CannotBeOrdered()
    {
        (InventoryLedger a, int sa) = Ledger();
        a.OnBalance(sa, 20m, 1002, 1);
        Assert.Equal(BalanceUpdate.Conflict, a.OnBalance(sa, 100m, 1002));      // late snapshot after the stream
        Assert.Equal(20m, a.VenueFree(sa));
        Assert.Equal(BalanceUpdate.Conflict, a.OnBalance(sa, 30m, 1002, 2));    // stream can no longer be ordered either
        Assert.Equal(20m, a.VenueFree(sa));

        (InventoryLedger b, int sb) = Ledger();
        b.OnBalance(sb, 100m, 1002);                                            // snapshot first
        Assert.Equal(BalanceUpdate.Conflict, b.OnBalance(sb, 20m, 1002, 1));
        Assert.Equal(20m, b.VenueFree(sb));
    }

    [Fact]
    public void StreamEventOlderThanTheLastSnapshot_IsStale()
    {
        (InventoryLedger l, int s) = Ledger();
        l.OnBalance(s, 50m, 1005);                                              // reconcile as-of 1005
        Assert.Equal(BalanceUpdate.Stale, l.OnBalance(s, 60m, 1003, 1));        // already reflected by the snapshot
        Assert.Equal(50m, l.VenueFree(s));
        Assert.False(l.NeedsReconcile(s));
    }

    [Fact]
    public void OrderedStreamInModel_NeverOverstates()
    {
        int reorders = 0;
        for (int seed = 0; seed < 1_500; seed++)
        {
            string? failure = LedgerModel.Run(_m, seed, steps: 160, Sequenced, ref reorders);
            Assert.True(failure is null, failure);
        }

        Assert.True(reorders >= 200, $"coverage: only {reorders} same-ms reorders with differing values");
    }

    [Fact]
    public void OrderedStreamAlone_NeverConflicts_AndTracksTheLastEvent()
    {
        var rng = new Random(7);
        (InventoryLedger l, int s) = Ledger();
        long ms = 1_000;
        decimal v = 1m;
        for (long seq = 1; seq <= 5_000; seq++)
        {
            if (rng.Next(3) == 0)
            {
                ms++;
            }

            v += rng.Next(-3, 4) / 1000m;
            Assert.Equal(BalanceUpdate.Applied, l.OnBalance(s, v, ms, seq));
            Assert.Equal(v, l.VenueFree(s));
            Assert.False(l.HasBalanceConflict(s));
        }
    }

    private static void Sequenced(InventoryLedger l, int slot, decimal value, long ms, long seq) => l.OnBalance(slot, value, ms, seq);
}
