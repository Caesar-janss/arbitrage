using Arb.Core.Instruments;
using Arb.Core.MarketData;
using Arb.Core.Primitives;

namespace Arb.Benchmarks;

/// <summary>Realistic depth: 200 levels per side, BTCUSDT on Binance (buy) and Bybit (sell), ~25 bps crossed at the top.</summary>
internal sealed class BenchFixtures
{
    public BenchFixtures()
    {
        Assets = AssetRegistry.Create("BTC", "USDT", "BNB");
        var b = new InstrumentRegistryBuilder(Assets);
        InstrumentKey binance = b.Add(Venue.Binance, MarketType.Spot, "BTCUSDT", "BTC", "USDT", AccountKind.Spot, 0.01m, 0.00001m, minNotional: 5m);
        InstrumentKey bybit = b.Add(Venue.Bybit, MarketType.Spot, "BTCUSDT", "BTC", "USDT", AccountKind.Unified, 0.01m, 0.000001m, minNotional: 5m);
        InstrumentKey bitget = b.Add(Venue.Bitget, MarketType.Spot, "BTCUSDT", "BTC", "USDT", AccountKind.Spot, 0.01m, 0.000001m, minNotional: 1m);
        Registry = b.Build();
        Buy = Registry[binance];
        Sell = Registry[bybit];
        Third = Registry[bitget];

        BuyBids = new PriceLevel[Depth];
        BuyAsks = new PriceLevel[Depth];
        SellBids = new PriceLevel[Depth];
        SellAsks = new PriceLevel[Depth];
        for (int i = 0; i < Depth; i++)
        {
            BuyBids[i] = new PriceLevel(5_999_990 - (i * 5), 200 + (i * 7));
            BuyAsks[i] = new PriceLevel(6_000_000 + (i * 5), 150 + (i * 9));
            SellBids[i] = new PriceLevel(6_015_000 - (i * 6), 1_800 + (i * 50));
            SellAsks[i] = new PriceLevel(6_015_010 + (i * 6), 2_000 + (i * 40));
        }

        BuyView = Load(Buy.Key, BuyBids, BuyAsks);
        SellView = Load(Sell.Key, SellBids, SellAsks);
        ThirdView = Load(Third.Key, SellBids, SellAsks);
    }

    public const int Depth = 200;

    public AssetRegistry Assets { get; }

    public InstrumentRegistry Registry { get; }

    public InstrumentSpec Buy { get; }

    public InstrumentSpec Sell { get; }

    public InstrumentSpec Third { get; }

    public PriceLevel[] BuyBids { get; }

    public PriceLevel[] BuyAsks { get; }

    public PriceLevel[] SellBids { get; }

    public PriceLevel[] SellAsks { get; }

    public BookView BuyView { get; }

    public BookView SellView { get; }

    public BookView ThirdView { get; }

    private static BookView Load(InstrumentKey key, PriceLevel[] bids, PriceLevel[] asks)
    {
        var v = new BookView(Depth);
        v.Load(key, bids, asks, new BookStamp(1, 0, 0, 0));
        return v;
    }
}
