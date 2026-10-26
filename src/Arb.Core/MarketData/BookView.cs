using Arb.Core.Primitives;

namespace Arb.Core.MarketData;

/// <summary>
/// Reader-owned, preallocated copy of the top-N of an <see cref="OrderBookL2"/> (best-first on both sides).
/// One instance per (reader thread, instrument); refilled with <see cref="OrderBookL2.TryCopyTo"/> without allocation.
/// </summary>
public sealed class BookView
{
    private readonly PriceLevel[] _bids;
    private readonly PriceLevel[] _asks;

    public BookView(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "must be > 0");
        }

        _bids = new PriceLevel[capacity];
        _asks = new PriceLevel[capacity];
    }

    public int Capacity => _bids.Length;

    public InstrumentKey Instrument { get; private set; }

    public int BidCount { get; private set; }

    public int AskCount { get; private set; }

    public BookStamp Stamp { get; private set; }

    public BookHealth Health { get; private set; }

    public BookFault Fault { get; private set; }

    /// <summary>Seqlock version of the source book at copy time.</summary>
    public long Version { get; private set; }

    public bool IsTruncated { get; private set; }

    /// <summary>Bids best (highest) first.</summary>
    public ReadOnlySpan<PriceLevel> Bids => new(_bids, 0, BidCount);

    /// <summary>Asks best (lowest) first.</summary>
    public ReadOnlySpan<PriceLevel> Asks => new(_asks, 0, AskCount);

    public bool IsUsable => Health == BookHealth.Synced && BidCount > 0 && AskCount > 0;

    /// <summary>Liquidity a taker on <paramref name="takerSide"/> consumes: Buy lifts asks, Sell hits bids.</summary>
    public ReadOnlySpan<PriceLevel> LiquidityFor(Side takerSide) => takerSide == Side.Buy ? Asks : Bids;

    internal PriceLevel[] BidBuffer => _bids;

    internal PriceLevel[] AskBuffer => _asks;

    /// <summary>Load explicit content (tests, replay, paper-trading book history). Inputs best-first.</summary>
    public void Load(
        InstrumentKey instrument,
        ReadOnlySpan<PriceLevel> bidsBestFirst,
        ReadOnlySpan<PriceLevel> asksBestFirst,
        in BookStamp stamp,
        BookHealth health = BookHealth.Synced)
    {
        if (bidsBestFirst.Length > Capacity || asksBestFirst.Length > Capacity)
        {
            throw new ArgumentException("more levels than view capacity");
        }

        bidsBestFirst.CopyTo(_bids);
        asksBestFirst.CopyTo(_asks);
        Commit(instrument, bidsBestFirst.Length, asksBestFirst.Length, stamp, health, BookFault.None, 0, false);
    }

    internal void Commit(
        InstrumentKey instrument,
        int bidCount,
        int askCount,
        in BookStamp stamp,
        BookHealth health,
        BookFault fault,
        long version,
        bool truncated)
    {
        Instrument = instrument;
        BidCount = bidCount;
        AskCount = askCount;
        Stamp = stamp;
        Health = health;
        Fault = fault;
        Version = version;
        IsTruncated = truncated;
    }
}
