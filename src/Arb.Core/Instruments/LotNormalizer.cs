using Arb.Core.Primitives;

namespace Arb.Core.Instruments;

/// <summary>
/// Common tradable lot of two instruments on the same base asset, in canonical units:
/// LCM of both canonical lots. Example: Bybit 1000PEPEUSDT (step 100 × 1000 = 100,000 PEPE) vs
/// Bitget PEPEUSDT (step 1 PEPE) → common lot 100,000 PEPE = 1 Bybit lot = 100,000 Bitget lots.
/// Any quantity that is a multiple of the common lot is exactly representable on BOTH venues,
/// so both legs carry the identical canonical quantity (zero structural residual).
/// </summary>
public readonly struct PairLotGrid
{
    public PairLotGrid(InstrumentSpec a, InstrumentSpec b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.BaseAsset != b.BaseAsset)
        {
            throw new ArgumentException($"{a} and {b} have different base assets");
        }

        CommonLot = DecimalGrid.Lcm(a.CanonicalLot, b.CanonicalLot);
        LotsPerUnitA = CommonLot.UnitsPer(a.CanonicalLot);
        LotsPerUnitB = CommonLot.UnitsPer(b.CanonicalLot);
    }

    public DecimalGrid CommonLot { get; }

    public double CommonLotD => CommonLot.ValueD;

    public long LotsPerUnitA { get; }

    public long LotsPerUnitB { get; }

    /// <summary>Largest number of common lots not exceeding <paramref name="canonicalQty"/>.</summary>
    public long UnitsFloor(double canonicalQty) =>
        canonicalQty <= 0 ? 0 : FixedPoint.ToUnits(canonicalQty, CommonLot.ValueD, Rounding.Down);
}

public static class LotNormalizer
{
    /// <summary>
    /// Spot buy whose fee is charged in the RECEIVED base asset: to deliver <paramref name="deliverCanonical"/>
    /// on the sell venue we must buy deliver / (1 − fee), rounded UP to the buy venue's lot.
    /// Without this, inventory drifts by fee × qty on every trade. Result guarantees received ≥ deliver.
    /// </summary>
    public static long GrossUpBuyLots(decimal deliverCanonical, double baseFeeRate, InstrumentSpec buy)
    {
        ArgumentNullException.ThrowIfNull(buy);
        if (deliverCanonical <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(deliverCanonical), deliverCanonical, "must be > 0");
        }

        if (baseFeeRate < 0 || baseFeeRate >= 0.5)
        {
            throw new ArgumentOutOfRangeException(nameof(baseFeeRate), baseFeeRate, "0 <= fee < 0.5");
        }

        decimal keep = 1m - (decimal)baseFeeRate;
        decimal required = deliverCanonical / keep;
        long lots = buy.CanonicalLot.ToUnits(required, Rounding.Up);
        while (buy.CanonicalQtyExact(lots) * keep < deliverCanonical)
        {
            lots++;
        }

        return lots;
    }
}
