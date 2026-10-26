using System.Runtime.InteropServices;

namespace Arb.Core.MarketData;

/// <summary>One L2 level in exact venue units (16 bytes). QtyLots = 0 in a delta means "delete level".</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct PriceLevel : IEquatable<PriceLevel>
{
    public readonly long PxTicks;
    public readonly long QtyLots;

    public PriceLevel(long pxTicks, long qtyLots)
    {
        PxTicks = pxTicks;
        QtyLots = qtyLots;
    }

    public bool Equals(PriceLevel other) => PxTicks == other.PxTicks && QtyLots == other.QtyLots;

    public override bool Equals(object? obj) => obj is PriceLevel other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(PxTicks, QtyLots);

    public static bool operator ==(PriceLevel left, PriceLevel right) => left.Equals(right);

    public static bool operator !=(PriceLevel left, PriceLevel right) => !left.Equals(right);

    public override string ToString() => $"{PxTicks}x{QtyLots}";
}

/// <summary>
/// Timing of the last applied book message.
///   Sequence            : venue update id (Binance u, Bybit u, Bitget seq) — continuity checked by the synchronizer.
///   ExchangeUnixMicros  : venue's event timestamp (VENUE clock).
///   LocalRecvUnixMicros : local wall clock at socket receive (for venue-clock comparison after offset correction).
///   LocalRecvMonoMicros : local monotonic clock at receive (for ages / silence).
/// </summary>
public readonly struct BookStamp
{
    public BookStamp(long sequence, long exchangeUnixMicros, long localRecvUnixMicros, long localRecvMonoMicros)
    {
        Sequence = sequence;
        ExchangeUnixMicros = exchangeUnixMicros;
        LocalRecvUnixMicros = localRecvUnixMicros;
        LocalRecvMonoMicros = localRecvMonoMicros;
    }

    public long Sequence { get; }

    public long ExchangeUnixMicros { get; }

    public long LocalRecvUnixMicros { get; }

    public long LocalRecvMonoMicros { get; }
}

public enum BookHealth : byte
{
    Empty = 0,
    Synced = 1,
    Resyncing = 2,
    Invalid = 3,
}

/// <summary>Why a book is not usable. A corrupted book with a fresh timestamp is MORE dangerous than a stale one.</summary>
public enum BookFault : byte
{
    None = 0,
    SequenceGap = 1,
    ChecksumMismatch = 2,
    Crossed = 3,
    NegativeQuantity = 4,
    InvalidPrice = 5,
    Disconnected = 6,
    SnapshotTimeout = 7,
    Manual = 8,
}
