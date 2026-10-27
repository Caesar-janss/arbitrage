using System.Diagnostics;
using Arb.Core.MarketData;
using Arb.Core.Primitives;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-04 (S2): MarkInvalid / MarkResyncing are called from supervisor / timer / operator threads, not only from the
/// book's writer thread. In v0.2 they entered the seqlock writer section, so concurrent calls lost version increments
/// and could leave the version permanently ODD-inverted: afterwards the version is even only WHILE a write is in
/// progress and readers accept torn books as Synced (P12, P22: 99.3 % of accepted reads torn).
/// Contract now: external marks are requests (no seqlock write), visible to readers immediately, applied/cleared by
/// the writer (ApplySnapshot).
/// </summary>
public class R04_ExternalBookMarksTests
{
    private const int Levels = 20;
    private static readonly InstrumentKey Key = new(Venue.Bybit, MarketType.Spot, 1);

    [Fact]
    public void ConcurrentExternalMarks_NeverCorruptTheSeqlockVersion()
    {
        var book = Seeded();
        long before = book.Version;
        for (int round = 0; round < 20; round++)
        {
            Storm(book, perThread: 200_000);
            Assert.True((book.Version & 1) == 0, $"round {round}: version {book.Version} left ODD by concurrent marks");
        }

        Assert.Equal(before, book.Version); // external threads never enter the writer section
    }

    [Fact]
    public void AfterExternalMarks_AndResync_ReadersNeverAcceptATornBook()
    {
        var book = Seeded();
        Storm(book, perThread: 300_000);
        var seedB = new PriceLevel[Levels];
        var seedA = new PriceLevel[Levels];
        Fill(seedB, seedA, 2);
        book.ApplySnapshot(seedB, seedA, new BookStamp(2, 0, 0, 0)); // the synchronizer's recovery path
        Assert.Equal(BookHealth.Synced, book.Health);

        long torn = 0;
        long accepted = 0;
        int done = 0;
        using var go = new ManualResetEventSlim(false);
        var writer = new Thread(() =>
        {
            var b = new PriceLevel[Levels];
            var a = new PriceLevel[Levels];
            go.Wait();
            var sw = Stopwatch.StartNew();
            long m = 3;
            while (sw.ElapsedMilliseconds < 250)
            {
                Fill(b, a, m);
                book.ApplyDelta(b, a, new BookStamp(m, 0, 0, 0));
                if ((m & 7) == 0)
                {
                    Thread.SpinWait(30);
                }

                m++;
            }

            Volatile.Write(ref done, 1);
        });
        var reader = new Thread(() =>
        {
            var v = new BookView(Levels + 4);
            go.Wait();
            while (Volatile.Read(ref done) == 0)
            {
                if (!book.TryCopyTo(v, Levels) || v.Health != BookHealth.Synced)
                {
                    continue;
                }

                accepted++;
                if (!Consistent(v))
                {
                    torn++;
                }
            }
        });
        writer.Start();
        reader.Start();
        go.Set();
        writer.Join();
        reader.Join();
        Assert.True(accepted > 0, "reader never got a copy");
        Assert.True(torn == 0, $"{torn} torn reads among {accepted} accepted");
    }

    [Fact]
    public void MarkFromAnotherThread_IsVisibleToReaders_AndBlocksDeltas_UntilSnapshot()
    {
        var book = Seeded();
        var t = new Thread(() => book.MarkInvalid(BookFault.Disconnected));
        t.Start();
        t.Join();

        var v = new BookView(8);
        Assert.True(book.TryCopyTo(v, 8));
        Assert.Equal(BookHealth.Invalid, v.Health);
        Assert.Equal(BookFault.Disconnected, v.Fault);
        Assert.False(v.IsUsable);
        Assert.Equal(BookHealth.Invalid, book.Health);
        Assert.False(book.ApplyDelta(new[] { new PriceLevel(99, 1) }, ReadOnlySpan<PriceLevel>.Empty, new BookStamp(5, 0, 0, 0)));

        book.ApplySnapshot(new[] { new PriceLevel(100, 1) }, new[] { new PriceLevel(101, 1) }, new BookStamp(6, 0, 0, 0));
        Assert.True(book.TryCopyTo(v, 8));
        Assert.True(v.IsUsable);
        Assert.Equal(BookFault.None, book.Fault);
    }

    [Fact]
    public void InvalidOutranksResync_AndFirstFaultIsKept()
    {
        var book = Seeded();
        book.MarkResyncing();
        book.MarkInvalid(BookFault.SequenceGap);
        book.MarkInvalid(BookFault.Manual);
        book.MarkResyncing();
        Assert.Equal(BookHealth.Invalid, book.Health);
        Assert.Equal(BookFault.SequenceGap, book.Fault);
    }

    private static void Storm(OrderBookL2 book, int perThread)
    {
        using var go = new ManualResetEventSlim(false);
        var t1 = new Thread(() => { go.Wait(); for (int i = 0; i < perThread; i++) { book.MarkResyncing(); } });
        var t2 = new Thread(() => { go.Wait(); for (int i = 0; i < perThread; i++) { book.MarkInvalid(BookFault.Disconnected); } });
        t1.Start();
        t2.Start();
        go.Set();
        t1.Join();
        t2.Join();
    }

    private static OrderBookL2 Seeded()
    {
        var book = new OrderBookL2(Key, 64);
        var b = new PriceLevel[Levels];
        var a = new PriceLevel[Levels];
        Fill(b, a, 2);
        book.ApplySnapshot(b, a, new BookStamp(2, 0, 0, 0));
        return book;
    }

    // every qty == message number; one level per side is absent on odd messages (array shifts)
    private static void Fill(PriceLevel[] bids, PriceLevel[] asks, long m)
    {
        bool odd = (m & 1) == 1;
        for (int i = 0; i < Levels; i++)
        {
            bids[i] = new PriceLevel(1_000 - i, odd && i == 10 ? 0 : m);
            asks[i] = new PriceLevel(1_001 + i, odd && i == 10 ? 0 : m);
        }
    }

    private static bool Consistent(BookView v)
    {
        long s = v.Stamp.Sequence;
        int expected = (s & 1) == 1 ? Levels - 1 : Levels;
        if (v.BidCount != expected || v.AskCount != expected)
        {
            return false;
        }

        foreach (PriceLevel l in v.Bids)
        {
            if (l.QtyLots != s)
            {
                return false;
            }
        }

        foreach (PriceLevel l in v.Asks)
        {
            if (l.QtyLots != s)
            {
                return false;
            }
        }

        return true;
    }
}
