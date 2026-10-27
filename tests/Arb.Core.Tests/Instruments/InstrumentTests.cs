using Arb.Core.Instruments;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Instruments;

public class InstrumentTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    [Fact]
    public void ThousandPepe_ConvertsVenueUnitsToCanonical()
    {
        InstrumentSpec bybit = _m.BybitPepePerp;

        // 0.0120000 per 1000 PEPE → 0.000012 per PEPE
        Assert.True(bybit.Tick.TryToUnitsExact(0.0120000m, out long ticks));
        Assert.Equal(120000L, ticks);
        Assert.Equal(0.000012, bybit.CanonicalPrice(ticks), 1e-18);

        // 1 lot = 100 contracts × 1000 = 100,000 PEPE
        Assert.Equal(100_000m, bybit.CanonicalLot.Value);
        Assert.Equal(1_500_000m, bybit.CanonicalQtyExact(15));

        // notional is multiplier-free: px_venue × qty_venue
        Int128 units = (Int128)ticks * 15;
        Assert.Equal(18.0m, bybit.NotionalQuoteExact(units));
        Assert.Equal(18.0, bybit.NotionalQuote(units), 1e-12);
    }

    [Fact]
    public void Registry_GroupsListingsByCanonicalPair_AcrossMultipliers()
    {
        ReadOnlySpan<InstrumentKey> pepe = _m.Registry.Listings(new CanonicalPair(_m.Pepe, _m.Usdt));
        Assert.Equal(3, pepe.Length);
        ReadOnlySpan<InstrumentKey> btc = _m.Registry.Listings(new CanonicalPair(_m.Btc, _m.Usdt));
        Assert.Equal(4, btc.Length);
        Assert.True(_m.Registry.TryFind(Venue.Bybit, MarketType.PerpLinear, "1000PEPEUSDT", out InstrumentSpec found));
        Assert.Equal(_m.BybitPepePerp.Key, found.Key);
    }

    [Fact]
    public void Registry_RejectsStaleOrForeignKeys()
    {
        var foreign = new InstrumentKey(Venue.Bitget, MarketType.Spot, 0); // index 0 belongs to Binance
        Assert.Throws<KeyNotFoundException>(() => _m.Registry[foreign]);
        Assert.Throws<KeyNotFoundException>(() => _m.Registry[new InstrumentKey(Venue.Binance, MarketType.Spot, 999)]);
    }

    [Fact]
    public void Builder_From_KeepsIndexesStable_OnRefresh()
    {
        InstrumentRegistryBuilder b = InstrumentRegistryBuilder.From(_m.Registry);
        b.Replace(_m.BybitBtcSpot.Key, 0.01m, 0.000001m, 0m, 5m, InstrumentStatus.Halted);
        InstrumentRegistry refreshed = b.Build();
        Assert.Equal(_m.Registry.Count, refreshed.Count);
        Assert.Equal(InstrumentStatus.Halted, refreshed[_m.BybitBtcSpot.Key].Status);
        Assert.Equal(_m.BinanceBtcSpot.VenueSymbol, refreshed[_m.BinanceBtcSpot.Key].VenueSymbol);
    }

    [Fact]
    public void Spec_Validation()
    {
        var assets = AssetRegistry.Create("BTC", "USDT");
        var b = new InstrumentRegistryBuilder(assets);
        Assert.Throws<ArgumentException>(() =>
            b.Add(Venue.Bybit, MarketType.PerpLinear, "BTCUSDT", "BTC", "USDT", AccountKind.Unified, 0.1m, 0.001m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            b.Add(Venue.Bybit, MarketType.PerpLinear, "BTCUSDT", "BTC", "USDT", AccountKind.Unified, 0.1m, 0.001m, nominalFundingIntervalHours: 3));
        Assert.Throws<ArgumentException>(() =>
            b.Add(Venue.Bybit, MarketType.Spot, "BTCBTC", "BTC", "BTC", AccountKind.Unified, 0.1m, 0.001m));
        Assert.Throws<KeyNotFoundException>(() =>
            b.Add(Venue.Bybit, MarketType.Spot, "ETHUSDT", "ETH", "USDT", AccountKind.Unified, 0.1m, 0.001m));
        Assert.Throws<ArgumentException>(() => AssetRegistry.Create("btc"));
        Assert.Throws<ArgumentException>(() => AssetRegistry.Create("BTC", "BTC"));
    }

    [Fact]
    public void MinQty_IsRoundedUpToLots_AndMinimumsChecked()
    {
        var assets = AssetRegistry.Create("BTC", "USDT");
        var b = new InstrumentRegistryBuilder(assets);
        InstrumentKey k = b.Add(Venue.Binance, MarketType.Spot, "BTCUSDT", "BTC", "USDT", AccountKind.Spot, 0.01m, 0.00001m, minQty: 0.000015m, minNotional: 5m);
        InstrumentSpec s = b.Build()[k];
        Assert.Equal(2L, s.MinQtyLots);
        Assert.False(s.MeetsMinimums(1, 100));
        Assert.False(s.MeetsMinimums(10, 4.99));
        Assert.True(s.MeetsMinimums(10, 5.0));
    }

    [Fact]
    public void PairLotGrid_CommonLotRepresentableOnBothVenues()
    {
        var btc = new PairLotGrid(_m.BinanceBtcSpot, _m.BybitBtcSpot);
        Assert.Equal(0.00001m, btc.CommonLot.Value);
        Assert.Equal(1L, btc.LotsPerUnitA);
        Assert.Equal(10L, btc.LotsPerUnitB);

        // 1000PEPE perp (step 100 → 100,000 PEPE) vs PEPE spot (step 1)
        var pepe = new PairLotGrid(_m.BybitPepePerp, _m.BitgetPepeSpot);
        Assert.Equal(100_000m, pepe.CommonLot.Value);
        Assert.Equal(1L, pepe.LotsPerUnitA);
        Assert.Equal(100_000L, pepe.LotsPerUnitB);
        Assert.Equal(15L, pepe.UnitsFloor(1_599_999));
        Assert.Equal(0L, pepe.UnitsFloor(99_999));

        var perps = new PairLotGrid(_m.BinancePepePerp, _m.BybitPepePerp);
        Assert.Equal(100L, perps.LotsPerUnitA);
        Assert.Equal(1L, perps.LotsPerUnitB);

        Assert.Throws<ArgumentException>(() => new PairLotGrid(_m.BinanceBtcSpot, _m.BitgetPepeSpot));
    }

    [Fact]
    public void GrossUpForBaseFee_DeliversAtLeastSellQty()
    {
        // Bitget spot buy, fee 10 bps in BTC; deliver 0.01 BTC → buy 0.010011 (10011 lots of 1e-6)
        long lots = LotNormalizer.GrossUpBuyLots(0.01m, 0.001, _m.BitgetBtcSpot);
        Assert.Equal(10011L, lots);
        decimal received = _m.BitgetBtcSpot.CanonicalQtyExact(lots) * 0.999m;
        Assert.True(received >= 0.01m);
        Assert.Equal(0.000000989m, received - 0.01m);

        Assert.Equal(1000L, LotNormalizer.GrossUpBuyLots(0.01m, 0.0, _m.BinanceBtcSpot));
        Assert.Throws<ArgumentOutOfRangeException>(() => LotNormalizer.GrossUpBuyLots(0m, 0.001, _m.BitgetBtcSpot));
    }
}
