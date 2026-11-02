using Arb.Core.Primitives;

namespace Arb.Core.Instruments;

public enum InstrumentStatus : byte
{
    Trading = 1,
    Halted = 2,
    Delisting = 3,
    Delisted = 4,
}

/// <summary>
/// Immutable instrument metadata (from exchangeInfo). Changes → new registry snapshot, swapped atomically.
///
/// Unit conventions (identical to the PostgreSQL schema):
///   venue px / qty   : what the venue quotes (1000PEPEUSDT: 1 unit = 1000 PEPE, px per 1000 PEPE)
///   qty_base         = qty_venue × BaseMultiplier                 (canonical base units)
///   notional (quote) = px_venue × qty_venue                         (= ticks × lots × tick × step)
///   canonical px     = notional / qty_base = px_venue / BaseMultiplier
/// Without BaseMultiplier a 1000PEPE vs PEPE comparison is off by 1000× and shows a fake "arbitrage".
///
/// Wallet deposit/withdraw status is deliberately NOT here: it is per (venue, asset, network), changes
/// minutes-to-hours (not per exchangeInfo refresh) and is consumed by Risk.SanityGuard via WalletStatusBook.
/// </summary>
public sealed class InstrumentSpec
{
    internal InstrumentSpec(
        InstrumentKey key,
        string venueSymbol,
        AssetId baseAsset,
        AssetId quoteAsset,
        AccountKind account,
        decimal tickSize,
        decimal stepSize,
        decimal minQty,
        decimal minNotional,
        decimal baseMultiplier,
        int nominalFundingIntervalHours,
        InstrumentStatus status,
        int gridGeneration = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venueSymbol);
        if (gridGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gridGeneration), gridGeneration, "must be >= 0");
        }

        if (!key.IsValid)
        {
            throw new ArgumentException("invalid instrument key", nameof(key));
        }

        if (!baseAsset.IsValid || !quoteAsset.IsValid || baseAsset == quoteAsset)
        {
            throw new ArgumentException("base/quote assets must be valid and different");
        }

        if (baseMultiplier <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(baseMultiplier), baseMultiplier, "must be > 0");
        }

        if (minQty < 0m || minNotional < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(minQty), "minimums must be >= 0");
        }

        bool perp = key.Market == MarketType.PerpLinear;
        if (perp != (nominalFundingIntervalHours != 0))
        {
            throw new ArgumentException("perp instruments require a funding interval; spot must use 0", nameof(nominalFundingIntervalHours));
        }

        if (perp && nominalFundingIntervalHours is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(nominalFundingIntervalHours), nominalFundingIntervalHours, "1, 2, 4 or 8");
        }

        Key = key;
        VenueSymbol = venueSymbol;
        BaseAsset = baseAsset;
        QuoteAsset = quoteAsset;
        Account = account;
        Status = status;
        BaseMultiplier = baseMultiplier;
        BaseMultiplierD = (double)baseMultiplier;
        Tick = DecimalGrid.FromDecimal(tickSize);
        Step = DecimalGrid.FromDecimal(stepSize);
        CanonicalLot = DecimalGrid.FromDecimal(stepSize * baseMultiplier);
        MinQtyLots = minQty == 0m ? 0 : Step.ToUnits(minQty, Rounding.Up);
        MinNotional = minNotional;
        MinNotionalD = (double)minNotional;
        NominalFundingIntervalHours = nominalFundingIntervalHours;
        GridGeneration = gridGeneration;

        CanonPxPerTick = (double)(tickSize / baseMultiplier);
        CanonQtyPerLot = (double)(stepSize * baseMultiplier);
        QuotePerNotionalUnitExact = tickSize * stepSize;
        QuotePerNotionalUnit = (double)QuotePerNotionalUnitExact;
    }

    public InstrumentKey Key { get; }

    public string VenueSymbol { get; }

    public AssetId BaseAsset { get; }

    public AssetId QuoteAsset { get; }

    public AccountKind Account { get; }

    public InstrumentStatus Status { get; }

    public decimal BaseMultiplier { get; }

    public double BaseMultiplierD { get; }

    /// <summary>Venue price grid.</summary>
    public DecimalGrid Tick { get; }

    /// <summary>Venue quantity grid (lot step, venue units).</summary>
    public DecimalGrid Step { get; }

    /// <summary>One lot expressed in canonical base units (= step × multiplier).</summary>
    public DecimalGrid CanonicalLot { get; }

    public long MinQtyLots { get; }

    public decimal MinNotional { get; }

    public double MinNotionalD { get; }

    /// <summary>Configured/listed interval. The LIVE interval can change dynamically (Bybit 8/4/2/1h) and is passed to funding math separately.</summary>
    public int NominalFundingIntervalHours { get; }

    /// <summary>
    /// Incremented whenever tick or step changes under the same key (R-08). Books carry the generation of the grid
    /// their ticks/lots were parsed with; pricer and repair planner refuse a book from another generation.
    /// </summary>
    public int GridGeneration { get; }

    public double CanonPxPerTick { get; }

    public double CanonQtyPerLot { get; }

    public decimal QuotePerNotionalUnitExact { get; }

    public double QuotePerNotionalUnit { get; }

    public bool IsPerp => Key.Market == MarketType.PerpLinear;

    public bool IsTrading => Status == InstrumentStatus.Trading;

    public double CanonicalPrice(long ticks) => ticks * CanonPxPerTick;

    public double CanonicalQty(long lots) => lots * CanonQtyPerLot;

    public decimal CanonicalQtyExact(long lots) => CanonicalLot.ToDecimal(lots);

    public decimal VenuePrice(long ticks) => Tick.ToDecimal(ticks);

    public decimal VenueQty(long lots) => Step.ToDecimal(lots);

    public double NotionalQuote(Int128 notionalUnits) => (double)notionalUnits * QuotePerNotionalUnit;

    public decimal NotionalQuoteExact(Int128 notionalUnits) => (decimal)notionalUnits * QuotePerNotionalUnitExact;

    public long LotsFromCanonical(double canonicalQty, Rounding rounding) =>
        FixedPoint.ToUnits(canonicalQty, CanonQtyPerLot, rounding);

    public long TicksFromCanonicalPrice(double canonicalPx, Rounding rounding) =>
        FixedPoint.ToUnits(canonicalPx, CanonPxPerTick, rounding);

    /// <summary>Venue minimums (min qty, min notional). <paramref name="notionalQuote"/> in quote currency.</summary>
    public bool MeetsMinimums(long lots, double notionalQuote) =>
        lots > 0 && lots >= MinQtyLots && notionalQuote >= MinNotionalD * (1 - 1e-12);

    public bool MeetsMinimumsExact(long lots, Int128 notionalUnits) =>
        lots > 0 && lots >= MinQtyLots && NotionalQuoteExact(notionalUnits) >= MinNotional;

    public override string ToString() => $"{Key.Venue}:{VenueSymbol}({Key.Market})";
}
