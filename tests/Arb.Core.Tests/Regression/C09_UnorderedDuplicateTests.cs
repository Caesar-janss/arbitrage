using Arb.Core.Inventory;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// C-09, second half (found by the builder while extending the C-07 model; pre-existing API, exact as-of snapshots).
/// Inside ms 1017 the true balance goes 21 → 26 → 31 → 26. The ordered stream delivers 21 and 26; an exact snapshot of
/// the LAST state (26) arrives, equal to the current value → Duplicate; then the delayed stream event 31 (older than
/// the snapshot) arrives. 98901fb still treated the slot as "ordered" after the unordered duplicate and applied 31:
/// overstated by 5. An observation without a delivery order must end same-ms ordering even when its value matches.
/// The C-07 model test had not hit this sequence with its 1,500 seeds; it surfaced once the model's random stream
/// changed (seed 56).
/// </summary>
public class C09_UnorderedDuplicateTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    [Fact]
    public void UnorderedDuplicate_EndsSameMsOrdering()
    {
        var l = new InventoryLedger(_m.Assets, 16);
        int s = l.SlotOf(Venue.Bitget, AccountKind.Spot, _m.Usdt);
        l.OnBalance(s, 21m, 1017, 18);
        l.OnBalance(s, 26m, 1017, 19);
        l.OnBalance(s, 26m, 1017);          // exact snapshot of the newest state, equal value
        l.OnBalance(s, 31m, 1017, 20);      // delayed stream event, older than that snapshot
        Assert.Equal(26m, l.VenueFree(s));  // 98901fb: 31
        Assert.False(l.TryReserve(s, 27m, out _));
    }

    [Fact]
    public void ExactModel_OrderedStream_MoreSeeds()
    {
        int sameMs = 0;
        for (int seed = 0; seed < 5_000; seed++)
        {
            string? failure = LedgerModel.Run(_m, seed, steps: 160, (ledger, slot, v, ms, seq) => ledger.OnBalance(slot, v, ms, seq), ref sameMs);
            Assert.True(failure is null, failure);
        }
    }
}
