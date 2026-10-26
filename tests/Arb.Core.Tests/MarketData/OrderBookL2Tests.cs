using Arb.Core.MarketData;
using Arb.Core.Primitives;

namespace Arb.Core.Tests.MarketData;

public class OrderBookL2Tests
{
    private static readonly InstrumentKey Key = new(Venue.Bybit, MarketType.Spot, 1);

    private static BookStamp Stamp(long seq) => new(seq, seq * 1000, seq * 1000, seq * 1000);

    private static PriceLevel L(long px, long qty) => new(px, qty);

    private static BookView Copy(OrderBookL2 book, int depth = 32)
    {
        var v = new BookView(64);
        Assert.True(book.TryCopyTo(v, depth));
        return v;
    }

    private static OrderBookL2 Seeded(int capacity = 64)
    {
        var book = new OrderBookL2(Key, capacity);
        book.ApplySnapshot(
            new[] { L(100, 5), L(99, 3), L(98, 7) },
            new[] { L(101, 2), L(102, 4), L(103, 6) },
            Stamp(1));
        return book;
    }

    [Fact]
    public void Snapshot_ViewIsBestFirst_OnBothSides()
    {
        BookView v = Copy(Seeded());
        Assert.Equal(BookHealth.Synced, v.Health);
        Assert.True(v.IsUsable);
        Assert.Equal(3, v.BidCount);
        Assert.Equal(L(100, 5), v.Bids[0]);
        Assert.Equal(L(98, 7), v.Bids[2]);
        Assert.Equal(L(101, 2), v.Asks[0]);
        Assert.Equal(L(103, 6), v.Asks[2]);
        Assert.Equal(1L, v.Stamp.Sequence);
        Assert.Equal(Key, v.Instrument);
    }

    [Fact]
    public void Delta_UpdateInsertDelete()
    {
        OrderBookL2 book = Seeded();
        Assert.True(book.ApplyDelta(
            new[] { L(100, 9), L(100, 0), L(97, 1), L(99, 0) }, // update then delete best, add deep, delete 99
            new[] { L(101, 0), L(100, 0), L(101, 8) },          // delete best, delete unknown (ignored), re-add
            Stamp(2)));
        BookView v = Copy(book);
        Assert.Equal(2, v.BidCount);
        Assert.Equal(L(98, 7), v.Bids[0]);
        Assert.Equal(L(97, 1), v.Bids[1]);
        Assert.Equal(L(101, 8), v.Asks[0]);
        Assert.Equal(3, v.AskCount);
        Assert.Equal(2L, v.Stamp.Sequence);
    }

    [Fact]
    public void NewBestLevel_InsertedAtTop()
    {
        OrderBookL2 book = Seeded();
        // remove best ask 101, then a new best bid at 101 (still below best ask 102)
        Assert.True(book.ApplyDelta(Array.Empty<PriceLevel>(), new[] { L(101, 0) }, Stamp(2)));
        Assert.True(book.ApplyDelta(new[] { L(101, 3) }, Array.Empty<PriceLevel>(), Stamp(3)));
        BookView v = Copy(book);
        Assert.Equal(L(101, 3), v.Bids[0]);
        Assert.Equal(L(100, 5), v.Bids[1]);
        Assert.Equal(4, v.BidCount);
        Assert.Equal(L(102, 4), v.Asks[0]);
    }

    [Fact]
    public void UnsortedSnapshot_IsAccepted_AndDuplicatePriceLastWins()
    {
        var book = new OrderBookL2(Key, 16);
        book.ApplySnapshot(new[] { L(98, 1), L(100, 2), L(99, 3), L(100, 4) }, new[] { L(103, 1), L(101, 2) }, Stamp(1));
        BookView v = Copy(book);
        Assert.Equal(3, v.BidCount);
        Assert.Equal(L(100, 4), v.Bids[0]);
        Assert.Equal(L(99, 3), v.Bids[1]);
        Assert.Equal(L(101, 2), v.Asks[0]);
    }

    [Fact]
    public void Capacity_KeepsBestLevels_DropsWorst_FlagsTruncation()
    {
        var book = new OrderBookL2(Key, 3);
        book.ApplySnapshot(new[] { L(100, 1), L(99, 1), L(98, 1) }, new[] { L(101, 1) }, Stamp(1));
        Assert.False(book.IsTruncated);
        Assert.True(book.ApplyDelta(new[] { L(97, 1) }, Array.Empty<PriceLevel>(), Stamp(2))); // worse than worst → dropped
        Assert.True(book.IsTruncated);
        Assert.True(book.ApplyDelta(new[] { L(99, 0), L(100, 5) }, Array.Empty<PriceLevel>(), Stamp(3)));
        Assert.True(book.ApplyDelta(new[] { L(99, 2), L(96, 1) }, Array.Empty<PriceLevel>(), Stamp(4)));
        BookView v = Copy(book);
        Assert.Equal(3, v.BidCount);
        Assert.Equal(L(100, 5), v.Bids[0]);
        Assert.Equal(L(99, 2), v.Bids[1]);
        Assert.Equal(L(98, 1), v.Bids[2]);
        Assert.True(v.IsTruncated);

        // full side + new best level → worst level evicted
        var wide = new OrderBookL2(Key, 3);
        wide.ApplySnapshot(new[] { L(100, 1), L(99, 1), L(98, 1) }, new[] { L(110, 1), L(111, 1), L(112, 1) }, Stamp(1));
        Assert.True(wide.ApplyDelta(new[] { L(105, 4) }, new[] { L(108, 2) }, Stamp(2)));
        v = Copy(wide);
        Assert.Equal(3, v.BidCount);
        Assert.Equal(L(105, 4), v.Bids[0]);
        Assert.Equal(L(99, 1), v.Bids[2]);
        Assert.Equal(L(108, 2), v.Asks[0]);
        Assert.Equal(L(111, 1), v.Asks[2]);
        Assert.True(v.IsTruncated);
    }

    [Fact]
    public void CrossedBook_IsInvalid_UntilNewSnapshot()
    {
        OrderBookL2 book = Seeded();
        Assert.False(book.ApplyDelta(new[] { L(101, 1) }, Array.Empty<PriceLevel>(), Stamp(2)));
        Assert.Equal(BookHealth.Invalid, book.Health);
        Assert.Equal(BookFault.Crossed, book.Fault);
        Assert.False(Copy(book).IsUsable);
        Assert.False(book.ApplyDelta(new[] { L(90, 1) }, Array.Empty<PriceLevel>(), Stamp(3)));

        book.ApplySnapshot(new[] { L(100, 1) }, new[] { L(101, 1) }, Stamp(10));
        Assert.Equal(BookHealth.Synced, book.Health);
        Assert.True(Copy(book).IsUsable);
    }

    [Fact]
    public void MalformedUpdates_InvalidateBook()
    {
        OrderBookL2 a = Seeded();
        Assert.False(a.ApplyDelta(new[] { L(95, -1) }, Array.Empty<PriceLevel>(), Stamp(2)));
        Assert.Equal(BookFault.NegativeQuantity, a.Fault);

        OrderBookL2 b = Seeded();
        Assert.False(b.ApplyDelta(Array.Empty<PriceLevel>(), new[] { L(0, 1) }, Stamp(2)));
        Assert.Equal(BookFault.InvalidPrice, b.Fault);

        var c = new OrderBookL2(Key);
        c.ApplySnapshot(new[] { L(100, 1) }, new[] { L(100, 1) }, Stamp(1));
        Assert.Equal(BookFault.Crossed, c.Fault);
    }

    [Fact]
    public void MarkInvalid_And_Resyncing_MakeViewUnusable()
    {
        OrderBookL2 book = Seeded();
        book.MarkInvalid(BookFault.SequenceGap);
        BookView v = Copy(book);
        Assert.False(v.IsUsable);
        Assert.Equal(BookFault.SequenceGap, v.Fault);

        OrderBookL2 r = Seeded();
        r.MarkResyncing();
        Assert.Equal(BookHealth.Resyncing, Copy(r).Health);
        Assert.False(r.ApplyDelta(new[] { L(99, 1) }, Array.Empty<PriceLevel>(), Stamp(2)));
    }

    [Fact]
    public void Version_IsEvenAfterEveryWrite_AndIncreases()
    {
        OrderBookL2 book = Seeded();
        long v1 = book.Version;
        Assert.Equal(0L, v1 % 2);
        book.ApplyDelta(new[] { L(99, 4) }, Array.Empty<PriceLevel>(), Stamp(2));
        long v2 = book.Version;
        Assert.Equal(0L, v2 % 2);
        Assert.True(v2 > v1);
    }

    [Fact]
    public void DepthLimitedCopy_And_WriterThreadTop()
    {
        OrderBookL2 book = Seeded();
        var v = new BookView(2);
        Assert.True(book.TryCopyTo(v, 10));
        Assert.Equal(2, v.BidCount);
        Assert.Equal(2, v.AskCount);

        Span<PriceLevel> top = stackalloc PriceLevel[2];
        Assert.Equal(2, book.CopyTopFromWriterThread(Side.Sell, top));
        Assert.Equal(L(101, 2), top[0]);
        Assert.Equal(L(102, 4), top[1]);
    }
}
