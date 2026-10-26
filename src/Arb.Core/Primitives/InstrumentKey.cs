using System.Globalization;

namespace Arb.Core.Primitives;

/// <summary>
/// 4-byte instrument handle. <see cref="Index"/> is a dense, registry-assigned index used to address
/// per-instrument arrays on the hot path (no dictionary lookups). Venue/Market are carried for cheap
/// validation and routing.
/// </summary>
public readonly struct InstrumentKey : IEquatable<InstrumentKey>
{
    public InstrumentKey(Venue venue, MarketType market, ushort index)
    {
        Venue = venue;
        Market = market;
        Index = index;
    }

    public Venue Venue { get; }

    public MarketType Market { get; }

    public ushort Index { get; }

    public bool IsValid => Venue != Venue.Unknown && Market != MarketType.Unknown;

    public bool Equals(InstrumentKey other) =>
        Index == other.Index && Venue == other.Venue && Market == other.Market;

    public override bool Equals(object? obj) => obj is InstrumentKey other && Equals(other);

    public override int GetHashCode() => Index | ((int)Venue << 16) | ((int)Market << 24);

    public static bool operator ==(InstrumentKey left, InstrumentKey right) => left.Equals(right);

    public static bool operator !=(InstrumentKey left, InstrumentKey right) => !left.Equals(right);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Venue}.{Market}#{Index}");
}

/// <summary>Dense asset handle (index into <see cref="Instruments.AssetRegistry"/>). 0 = none.</summary>
public readonly struct AssetId : IEquatable<AssetId>
{
    public AssetId(ushort value)
    {
        Value = value;
    }

    public ushort Value { get; }

    public bool IsValid => Value != 0;

    public bool Equals(AssetId other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is AssetId other && Equals(other);

    public override int GetHashCode() => Value;

    public static bool operator ==(AssetId left, AssetId right) => left.Equals(right);

    public static bool operator !=(AssetId left, AssetId right) => !left.Equals(right);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
