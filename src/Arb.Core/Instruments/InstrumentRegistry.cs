using Arb.Core.Primitives;

namespace Arb.Core.Instruments;

/// <summary>Canonical (venue-independent) pair, e.g. PEPE/USDT regardless of 1000PEPE contract naming.</summary>
public readonly struct CanonicalPair : IEquatable<CanonicalPair>
{
    public CanonicalPair(AssetId baseAsset, AssetId quoteAsset)
    {
        Base = baseAsset;
        Quote = quoteAsset;
    }

    public AssetId Base { get; }

    public AssetId Quote { get; }

    public bool Equals(CanonicalPair other) => Base == other.Base && Quote == other.Quote;

    public override bool Equals(object? obj) => obj is CanonicalPair other && Equals(other);

    public override int GetHashCode() => (Base.Value << 16) | Quote.Value;

    public static bool operator ==(CanonicalPair left, CanonicalPair right) => left.Equals(right);

    public static bool operator !=(CanonicalPair left, CanonicalPair right) => !left.Equals(right);
}

/// <summary>
/// Immutable instrument snapshot. Hot path addresses specs by <see cref="InstrumentKey.Index"/> (array).
/// Refresh = build a new registry (keeping indexes stable via <see cref="InstrumentRegistryBuilder.From"/>)
/// and swap the reference atomically.
/// </summary>
public sealed class InstrumentRegistry
{
    private readonly InstrumentSpec[] _specs;
    private readonly Dictionary<(Venue, MarketType, string), InstrumentSpec> _bySymbol;
    private readonly Dictionary<CanonicalPair, InstrumentKey[]> _byPair;

    internal InstrumentRegistry(AssetRegistry assets, InstrumentSpec[] specs)
    {
        Assets = assets;
        _specs = specs;
        _bySymbol = new Dictionary<(Venue, MarketType, string), InstrumentSpec>();
        var pairs = new Dictionary<CanonicalPair, List<InstrumentKey>>();
        foreach (InstrumentSpec s in specs)
        {
            if (!_bySymbol.TryAdd((s.Key.Venue, s.Key.Market, s.VenueSymbol), s))
            {
                throw new ArgumentException($"duplicate instrument {s}");
            }

            var pair = new CanonicalPair(s.BaseAsset, s.QuoteAsset);
            if (!pairs.TryGetValue(pair, out List<InstrumentKey>? list))
            {
                list = new List<InstrumentKey>();
                pairs.Add(pair, list);
            }

            list.Add(s.Key);
        }

        _byPair = new Dictionary<CanonicalPair, InstrumentKey[]>(pairs.Count);
        foreach (KeyValuePair<CanonicalPair, List<InstrumentKey>> kv in pairs)
        {
            _byPair.Add(kv.Key, kv.Value.ToArray());
        }
    }

    public AssetRegistry Assets { get; }

    public int Count => _specs.Length;

    public InstrumentSpec this[InstrumentKey key]
    {
        get
        {
            if (key.Index >= _specs.Length)
            {
                throw new KeyNotFoundException($"instrument {key} not in registry");
            }

            InstrumentSpec s = _specs[key.Index];
            if (s.Key != key)
            {
                throw new KeyNotFoundException($"instrument {key} does not match registry entry {s.Key}");
            }

            return s;
        }
    }

    public InstrumentSpec ByIndex(int index) => _specs[index];

    public bool TryFind(Venue venue, MarketType market, string venueSymbol, out InstrumentSpec spec) =>
        _bySymbol.TryGetValue((venue, market, venueSymbol), out spec!);

    /// <summary>All listings of a canonical pair across venues and market types.</summary>
    public ReadOnlySpan<InstrumentKey> Listings(CanonicalPair pair) =>
        _byPair.TryGetValue(pair, out InstrumentKey[]? keys) ? keys : ReadOnlySpan<InstrumentKey>.Empty;
}

public sealed class InstrumentRegistryBuilder
{
    private readonly AssetRegistry _assets;
    private readonly List<InstrumentSpec> _specs = new();

    public InstrumentRegistryBuilder(AssetRegistry assets)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
    }

    /// <summary>Start from an existing snapshot so existing instruments keep their indexes (stable keys across refresh).</summary>
    public static InstrumentRegistryBuilder From(InstrumentRegistry existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var b = new InstrumentRegistryBuilder(existing.Assets);
        for (int i = 0; i < existing.Count; i++)
        {
            b._specs.Add(existing.ByIndex(i));
        }

        return b;
    }

    public InstrumentKey Add(
        Venue venue,
        MarketType market,
        string venueSymbol,
        string baseAsset,
        string quoteAsset,
        AccountKind account,
        decimal tickSize,
        decimal stepSize,
        decimal minQty = 0m,
        decimal minNotional = 0m,
        decimal baseMultiplier = 1m,
        int nominalFundingIntervalHours = 0,
        InstrumentStatus status = InstrumentStatus.Trading)
    {
        if (_specs.Count >= ushort.MaxValue)
        {
            throw new InvalidOperationException("instrument registry full");
        }

        var key = new InstrumentKey(venue, market, (ushort)_specs.Count);
        _specs.Add(new InstrumentSpec(
            key,
            venueSymbol,
            _assets[baseAsset],
            _assets[quoteAsset],
            account,
            tickSize,
            stepSize,
            minQty,
            minNotional,
            baseMultiplier,
            nominalFundingIntervalHours,
            status));
        return key;
    }

    /// <summary>Replace metadata of an existing instrument (same key/index), e.g. status → Halted or new tick size.</summary>
    public void Replace(
        InstrumentKey key,
        decimal tickSize,
        decimal stepSize,
        decimal minQty,
        decimal minNotional,
        InstrumentStatus status)
    {
        InstrumentSpec old = _specs[key.Index];
        if (old.Key != key)
        {
            throw new KeyNotFoundException($"instrument {key} not in builder");
        }

        _specs[key.Index] = new InstrumentSpec(
            key,
            old.VenueSymbol,
            old.BaseAsset,
            old.QuoteAsset,
            old.Account,
            tickSize,
            stepSize,
            minQty,
            minNotional,
            old.BaseMultiplier,
            old.NominalFundingIntervalHours,
            status);
    }

    public InstrumentRegistry Build() => new(_assets, _specs.ToArray());
}
