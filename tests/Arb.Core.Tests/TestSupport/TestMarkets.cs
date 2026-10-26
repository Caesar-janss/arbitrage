using Arb.Core.Instruments;
using Arb.Core.MarketData;
using Arb.Core.Primitives;

namespace Arb.Core.Tests.TestSupport;

/// <summary>
/// Shared market fixture, mirrors db/tests/test_schema.sql reference data:
///   BTCUSDT spot on Binance (step 1e-5), Bybit (1e-6), Bitget (1e-6);
///   1000PEPEUSDT perp on Binance (step 1, ×1000, 8h) and Bybit (step 100, ×1000, 4h);
///   PEPEUSDT spot on Bitget (step 1); BTCUSDT perp on Bybit (tick 0.1, step 0.001).
/// </summary>
internal sealed class TestMarkets
{
    private TestMarkets(AssetRegistry assets, InstrumentRegistry registry, InstrumentKey[] keys)
    {
        Assets = assets;
        Registry = registry;
        Btc = assets["BTC"];
        Usdt = assets["USDT"];
        Pepe = assets["PEPE"];
        Bnb = assets["BNB"];
        Bgb = assets["BGB"];
        BinanceBtcSpot = registry[keys[0]];
        BybitBtcSpot = registry[keys[1]];
        BitgetBtcSpot = registry[keys[2]];
        BinancePepePerp = registry[keys[3]];
        BybitPepePerp = registry[keys[4]];
        BitgetPepeSpot = registry[keys[5]];
        BybitBtcPerp = registry[keys[6]];
    }

    public AssetRegistry Assets { get; }

    public InstrumentRegistry Registry { get; }

    public AssetId Btc { get; }

    public AssetId Usdt { get; }

    public AssetId Pepe { get; }

    public AssetId Bnb { get; }

    public AssetId Bgb { get; }

    public InstrumentSpec BinanceBtcSpot { get; }

    public InstrumentSpec BybitBtcSpot { get; }

    public InstrumentSpec BitgetBtcSpot { get; }

    public InstrumentSpec BinancePepePerp { get; }

    public InstrumentSpec BybitPepePerp { get; }

    public InstrumentSpec BitgetPepeSpot { get; }

    public InstrumentSpec BybitBtcPerp { get; }

    public static TestMarkets Create(decimal bybitPepeMultiplier = 1000m)
    {
        var assets = AssetRegistry.Create("BTC", "USDT", "PEPE", "BNB", "BGB");
        var b = new InstrumentRegistryBuilder(assets);
        var keys = new[]
        {
            b.Add(Venue.Binance, MarketType.Spot, "BTCUSDT", "BTC", "USDT", AccountKind.Spot, 0.01m, 0.00001m, minNotional: 5m),
            b.Add(Venue.Bybit, MarketType.Spot, "BTCUSDT", "BTC", "USDT", AccountKind.Unified, 0.01m, 0.000001m, minNotional: 5m),
            b.Add(Venue.Bitget, MarketType.Spot, "BTCUSDT", "BTC", "USDT", AccountKind.Spot, 0.01m, 0.000001m, minNotional: 1m),
            b.Add(Venue.Binance, MarketType.PerpLinear, "1000PEPEUSDT", "PEPE", "USDT", AccountKind.Futures, 0.0000001m, 1m, minNotional: 5m, baseMultiplier: 1000m, nominalFundingIntervalHours: 8),
            b.Add(Venue.Bybit, MarketType.PerpLinear, "1000PEPEUSDT", "PEPE", "USDT", AccountKind.Unified, 0.0000001m, 100m, minNotional: 5m, baseMultiplier: bybitPepeMultiplier, nominalFundingIntervalHours: 4),
            b.Add(Venue.Bitget, MarketType.Spot, "PEPEUSDT", "PEPE", "USDT", AccountKind.Spot, 0.00000001m, 1m, minNotional: 1m),
            b.Add(Venue.Bybit, MarketType.PerpLinear, "BTCUSDT", "BTC", "USDT", AccountKind.Unified, 0.1m, 0.001m, minNotional: 5m, nominalFundingIntervalHours: 8),
        };
        return new TestMarkets(assets, b.Build(), keys);
    }

    /// <summary>Exact level from venue price/qty (throws if off-grid — tests must use valid prices).</summary>
    public static PriceLevel Level(InstrumentSpec spec, decimal px, decimal qtyVenue)
    {
        if (!spec.Tick.TryToUnitsExact(px, out long ticks))
        {
            throw new ArgumentException($"{px} is off the tick grid of {spec}");
        }

        if (!spec.Step.TryToUnitsExact(qtyVenue, out long lots))
        {
            throw new ArgumentException($"{qtyVenue} is off the lot grid of {spec}");
        }

        return new PriceLevel(ticks, lots);
    }

    public static PriceLevel[] Levels(InstrumentSpec spec, params (decimal Px, decimal Qty)[] levels)
    {
        var r = new PriceLevel[levels.Length];
        for (int i = 0; i < levels.Length; i++)
        {
            r[i] = Level(spec, levels[i].Px, levels[i].Qty);
        }

        return r;
    }

    public static BookView View(
        InstrumentSpec spec,
        (decimal Px, decimal Qty)[] bids,
        (decimal Px, decimal Qty)[] asks,
        int capacity = 64,
        BookHealth health = BookHealth.Synced)
    {
        var view = new BookView(capacity);
        view.Load(spec.Key, Levels(spec, bids), Levels(spec, asks), new BookStamp(1, 1_000_000, 1_000_000, 1_000_000), health);
        return view;
    }
}
