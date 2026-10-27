using System.Diagnostics;
using Arb.Core.MarketData;
using Arb.Core.Primitives;

namespace Arb.Core.Tests.MarketData;

/// <summary>
/// Proves readers never observe a torn book. Every message m writes ALL levels with qty = m and toggles the
/// presence of one level per side (odd m: deleted, even m: re-inserted → array shifts). A consistent snapshot
/// therefore has (a) every qty == stamp.Sequence and (b) a level count determined by the parity of Sequence.
///
/// Duration-based with a start barrier: readers are running before the writer starts (a message-count loop
/// can finish before reader threads are even scheduled on a 2-vCPU box). The writer pauses briefly every
/// 8 messages (~network cadence) — a writer that NEVER pauses starves seqlock readers by design; in the engine
/// a failed TryCopyTo just means "re-evaluate on the next dirty signal".
/// </summary>
public class SeqlockConcurrencyTests
{
    private const int Levels = 20;
    private const long BidTop = 1_000;
    private const long AskTop = 1_001;
    private const long ToggledBid = BidTop - 10;
    private const long ToggledAsk = AskTop + 10;
    private const int Readers = 2;
    private const int DurationMs = 300;

    [Fact]
    public void Readers_NeverSeeTornBook_UnderConcurrentWrites()
    {
        var key = new InstrumentKey(Venue.Binance, MarketType.Spot, 0);
        var book = new OrderBookL2(key, 64);
        var seedBids = new PriceLevel[Levels];
        var seedAsks = new PriceLevel[Levels];
        Fill(seedBids, seedAsks, 2);
        book.ApplySnapshot(seedBids, seedAsks, new BookStamp(2, 0, 0, 0));

        long torn = 0;
        long validated = 0;
        long messages = 0;
        int writerDone = 0;
        using var ready = new CountdownEvent(Readers);
        using var go = new ManualResetEventSlim(false);

        var writer = new Thread(() =>
        {
            var b = new PriceLevel[Levels];
            var a = new PriceLevel[Levels];
            go.Wait();
            var sw = Stopwatch.StartNew();
            long m = 3;
            while (sw.ElapsedMilliseconds < DurationMs)
            {
                Fill(b, a, m);
                book.ApplyDelta(b, a, new BookStamp(m, 0, 0, 0));
                if ((m & 7) == 0)
                {
                    Thread.SpinWait(30);
                }

                m++;
            }

            Interlocked.Exchange(ref messages, m - 3);
            Volatile.Write(ref writerDone, 1);
        });

        var readers = new Thread[Readers];
        for (int r = 0; r < Readers; r++)
        {
            readers[r] = new Thread(() =>
            {
                var view = new BookView(Levels + 4);
                ready.Signal();
                go.Wait();
                while (Volatile.Read(ref writerDone) == 0)
                {
                    if (!book.TryCopyTo(view, Levels))
                    {
                        continue;
                    }

                    if (!IsConsistent(view))
                    {
                        Interlocked.Increment(ref torn);
                    }

                    Interlocked.Increment(ref validated);
                }
            });
        }

        writer.Start();
        foreach (Thread t in readers)
        {
            t.Start();
        }

        ready.Wait();
        go.Set();
        writer.Join();
        foreach (Thread t in readers)
        {
            t.Join();
        }

        Assert.Equal(0L, Interlocked.Read(ref torn));
        Assert.True(Interlocked.Read(ref messages) >= 10_000, $"writer applied only {messages} messages");
        Assert.True(Interlocked.Read(ref validated) >= 1_000, $"only {validated} reads validated");
        Assert.Equal(BookHealth.Synced, book.Health);
    }

    private static void Fill(PriceLevel[] bids, PriceLevel[] asks, long m)
    {
        bool odd = (m & 1) == 1;
        for (int i = 0; i < Levels; i++)
        {
            long bidPx = BidTop - i;
            long askPx = AskTop + i;
            bids[i] = new PriceLevel(bidPx, odd && bidPx == ToggledBid ? 0 : m);
            asks[i] = new PriceLevel(askPx, odd && askPx == ToggledAsk ? 0 : m);
        }
    }

    private static bool IsConsistent(BookView v)
    {
        long seq = v.Stamp.Sequence;
        bool odd = (seq & 1) == 1;
        int expectedCount = odd ? Levels - 1 : Levels;
        if (v.BidCount != expectedCount || v.AskCount != expectedCount)
        {
            return false;
        }

        long prevBid = long.MaxValue;
        foreach (PriceLevel l in v.Bids)
        {
            if (l.QtyLots != seq || l.PxTicks >= prevBid || (odd && l.PxTicks == ToggledBid))
            {
                return false;
            }

            prevBid = l.PxTicks;
        }

        long prevAsk = long.MinValue;
        foreach (PriceLevel l in v.Asks)
        {
            if (l.QtyLots != seq || l.PxTicks <= prevAsk || (odd && l.PxTicks == ToggledAsk))
            {
                return false;
            }

            prevAsk = l.PxTicks;
        }

        return true;
    }
}
