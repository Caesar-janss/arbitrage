using Arb.Core.Primitives;

namespace Arb.Core.MarketData;

/// <summary>
/// L2 order book: SINGLE WRITER (the venue connection thread), any number of lock-free readers via seqlock.
///
/// Layout: each side is a sorted PriceLevel[] with the BEST level at the END of the array, so the
/// frequent top-of-book inserts/deletes shift only a few elements. Asks are stored with key = −price,
/// which makes both sides "ascending key, best at end" and lets them share one code path.
///
/// Seqlock protocol:
///   writer: Interlocked.Increment(version) [odd, full fence] → mutate → Volatile.Write(version+1) [even, release]
///   reader: v1 = Volatile.Read (acquire) → copy → full fence → v2 = Volatile.Read → accept iff v1 == v2 and even.
/// Arrays are fixed-capacity and never reallocated, so a torn read can produce garbage values (discarded
/// by the version check) but never an out-of-range access.
///
/// Sequence-number continuity / checksum validation is venue-specific and lives in the synchronizer
/// (Venues.Common); it calls <see cref="MarkInvalid"/> on a gap and <see cref="ApplySnapshot"/> to recover.
/// </summary>
public sealed class OrderBookL2
{
    private readonly PriceLevel[] _bids;
    private readonly PriceLevel[] _asks;
    private int _bidCount;
    private int _askCount;
    private long _version;
    private BookStamp _stamp;
    private BookHealth _health;
    private BookFault _fault;
    private bool _truncated;

    public OrderBookL2(InstrumentKey instrument, int capacity = 1024)
    {
        if (!instrument.IsValid)
        {
            throw new ArgumentException("invalid instrument", nameof(instrument));
        }

        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "must be >= 1");
        }

        Instrument = instrument;
        _bids = new PriceLevel[capacity];
        _asks = new PriceLevel[capacity];
        _health = BookHealth.Empty;
    }

    public InstrumentKey Instrument { get; }

    public int Capacity => _bids.Length;

    public long Version => Volatile.Read(ref _version);

    /// <summary>Writer-thread view of health (monitoring may read it racily).</summary>
    public BookHealth Health => _health;

    public BookFault Fault => _fault;

    /// <summary>True if levels were dropped because the side reached capacity (only far-from-touch depth is lost).</summary>
    public bool IsTruncated => _truncated;

    public int BidCount => _bidCount;

    public int AskCount => _askCount;

    public BookStamp Stamp => _stamp;

    /// <summary>Replace the whole book. Inputs best-first (fast path); unsorted input is accepted (slow path).</summary>
    public void ApplySnapshot(ReadOnlySpan<PriceLevel> bidsBestFirst, ReadOnlySpan<PriceLevel> asksBestFirst, in BookStamp stamp)
    {
        BeginWrite();
        try
        {
            _bidCount = 0;
            _askCount = 0;
            _truncated = false;
            _fault = BookFault.None;
            _health = BookHealth.Synced;
            _stamp = stamp;
            BookFault f = LoadSide(_bids, ref _bidCount, bidsBestFirst, 1);
            if (f == BookFault.None)
            {
                f = LoadSide(_asks, ref _askCount, asksBestFirst, -1);
            }

            if (f != BookFault.None)
            {
                SetFault(f);
                return;
            }

            CheckCrossed();
        }
        finally
        {
            EndWrite();
        }
    }

    /// <summary>
    /// Apply one venue message of level updates (qty 0 = delete). One seqlock section per message, so readers
    /// never observe half a message. Returns false (and changes nothing) if the book is not Synced, or marks the
    /// book Invalid on a malformed update / crossed result.
    /// </summary>
    public bool ApplyDelta(ReadOnlySpan<PriceLevel> bidUpdates, ReadOnlySpan<PriceLevel> askUpdates, in BookStamp stamp)
    {
        if (_health != BookHealth.Synced)
        {
            return false;
        }

        BeginWrite();
        try
        {
            for (int i = 0; i < bidUpdates.Length; i++)
            {
                BookFault f = Upsert(_bids, ref _bidCount, bidUpdates[i].PxTicks, bidUpdates[i].QtyLots, 1);
                if (f != BookFault.None)
                {
                    SetFault(f);
                    return false;
                }
            }

            for (int i = 0; i < askUpdates.Length; i++)
            {
                BookFault f = Upsert(_asks, ref _askCount, askUpdates[i].PxTicks, askUpdates[i].QtyLots, -1);
                if (f != BookFault.None)
                {
                    SetFault(f);
                    return false;
                }
            }

            _stamp = stamp;
            CheckCrossed();
            return _health == BookHealth.Synced;
        }
        finally
        {
            EndWrite();
        }
    }

    public void MarkInvalid(BookFault fault)
    {
        BeginWrite();
        SetFault(fault == BookFault.None ? BookFault.Manual : fault);
        EndWrite();
    }

    /// <summary>Snapshot requested; contents retained but unusable until <see cref="ApplySnapshot"/>.</summary>
    public void MarkResyncing()
    {
        BeginWrite();
        _health = BookHealth.Resyncing;
        EndWrite();
    }

    /// <summary>
    /// Lock-free consistent copy of the top <paramref name="depth"/> levels into a reader-owned view.
    /// Returns false only if the writer kept the book busy for all attempts (view left unchanged).
    /// </summary>
    public bool TryCopyTo(BookView view, int depth, int maxAttempts = 64)
    {
        ArgumentNullException.ThrowIfNull(view);
        int cap = _bids.Length;
        depth = Math.Min(depth, view.Capacity);
        PriceLevel[] vb = view.BidBuffer;
        PriceLevel[] va = view.AskBuffer;
        var spinner = default(SpinWait);

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            long v1 = Volatile.Read(ref _version);
            if ((v1 & 1) != 0)
            {
                spinner.SpinOnce(-1);
                continue;
            }

            int bc = Math.Clamp(_bidCount, 0, cap);
            int ac = Math.Clamp(_askCount, 0, cap);
            int nb = Math.Min(bc, depth);
            int na = Math.Min(ac, depth);
            for (int i = 0; i < nb; i++)
            {
                vb[i] = _bids[bc - 1 - i];
            }

            for (int i = 0; i < na; i++)
            {
                PriceLevel k = _asks[ac - 1 - i];
                va[i] = new PriceLevel(-k.PxTicks, k.QtyLots);
            }

            BookStamp stamp = _stamp;
            BookHealth health = _health;
            BookFault fault = _fault;
            bool truncated = _truncated;

            Interlocked.MemoryBarrier();
            long v2 = Volatile.Read(ref _version);
            if (v1 == v2)
            {
                view.Commit(Instrument, nb, na, stamp, health, fault, v1, truncated);
                return true;
            }

            spinner.SpinOnce(-1);
        }

        return false;
    }

    /// <summary>WRITER THREAD ONLY (no seqlock): copy top levels best-first, e.g. for Bitget CRC32 checksum.</summary>
    public int CopyTopFromWriterThread(Side bookSide, Span<PriceLevel> destinationBestFirst)
    {
        PriceLevel[] arr = bookSide == Side.Buy ? _bids : _asks;
        int count = bookSide == Side.Buy ? _bidCount : _askCount;
        int sign = bookSide == Side.Buy ? 1 : -1;
        int n = Math.Min(count, destinationBestFirst.Length);
        for (int i = 0; i < n; i++)
        {
            PriceLevel k = arr[count - 1 - i];
            destinationBestFirst[i] = new PriceLevel(sign * k.PxTicks, k.QtyLots);
        }

        return n;
    }

    private void BeginWrite() => Interlocked.Increment(ref _version);

    private void EndWrite() => Volatile.Write(ref _version, _version + 1);

    private void SetFault(BookFault fault)
    {
        _health = BookHealth.Invalid;
        _fault = fault;
    }

    private void CheckCrossed()
    {
        if (_bidCount > 0 && _askCount > 0 && _bids[_bidCount - 1].PxTicks >= -_asks[_askCount - 1].PxTicks)
        {
            SetFault(BookFault.Crossed);
        }
    }

    private BookFault LoadSide(PriceLevel[] arr, ref int count, ReadOnlySpan<PriceLevel> input, int sign)
    {
        int nonZero = 0;
        bool sorted = true;
        long prevKey = long.MaxValue;
        for (int i = 0; i < input.Length; i++)
        {
            long qty = input[i].QtyLots;
            if (qty < 0)
            {
                return BookFault.NegativeQuantity;
            }

            if (input[i].PxTicks <= 0)
            {
                return BookFault.InvalidPrice;
            }

            if (qty == 0)
            {
                continue;
            }

            long key = sign * input[i].PxTicks;
            if (key >= prevKey)
            {
                sorted = false;
            }

            prevKey = key;
            nonZero++;
        }

        if (sorted && nonZero <= arr.Length)
        {
            int w = nonZero - 1;
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i].QtyLots == 0)
                {
                    continue;
                }

                arr[w--] = new PriceLevel(sign * input[i].PxTicks, input[i].QtyLots);
            }

            count = nonZero;
            return BookFault.None;
        }

        for (int i = 0; i < input.Length; i++)
        {
            if (input[i].QtyLots == 0)
            {
                continue;
            }

            BookFault f = Upsert(arr, ref count, input[i].PxTicks, input[i].QtyLots, sign);
            if (f != BookFault.None)
            {
                return f;
            }
        }

        return BookFault.None;
    }

    private BookFault Upsert(PriceLevel[] arr, ref int count, long pxTicks, long qty, int sign)
    {
        if (pxTicks <= 0)
        {
            return BookFault.InvalidPrice;
        }

        if (qty < 0)
        {
            return BookFault.NegativeQuantity;
        }

        long key = sign * pxTicks;
        int idx = Search(arr, count, key);
        if (idx >= 0)
        {
            if (qty == 0)
            {
                arr.AsSpan(idx + 1, count - idx - 1).CopyTo(arr.AsSpan(idx));
                count--;
            }
            else
            {
                arr[idx] = new PriceLevel(key, qty);
            }

            return BookFault.None;
        }

        if (qty == 0)
        {
            return BookFault.None; // delete of an unknown level (normal after truncation / window shift)
        }

        int ins = ~idx;
        if (count == arr.Length)
        {
            _truncated = true;
            if (ins == 0)
            {
                return BookFault.None; // worse than the worst level we keep
            }

            // evict worst (index 0), shift [1, ins) left, insert at ins-1
            arr.AsSpan(1, ins - 1).CopyTo(arr.AsSpan(0));
            arr[ins - 1] = new PriceLevel(key, qty);
            return BookFault.None;
        }

        arr.AsSpan(ins, count - ins).CopyTo(arr.AsSpan(ins + 1));
        arr[ins] = new PriceLevel(key, qty);
        count++;
        return BookFault.None;
    }

    private static int Search(PriceLevel[] arr, int count, long key)
    {
        int lo = 0;
        int hi = count - 1;
        while (lo <= hi)
        {
            int mid = (int)((uint)(lo + hi) >> 1);
            long k = arr[mid].PxTicks;
            if (k == key)
            {
                return mid;
            }

            if (k < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ~lo;
    }
}
