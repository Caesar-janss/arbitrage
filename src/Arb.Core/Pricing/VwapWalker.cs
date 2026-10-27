using Arb.Core.Instruments;
using Arb.Core.MarketData;
using Arb.Core.Primitives;

namespace Arb.Core.Pricing;

/// <summary>Result of walking one book side. Exact integer domain (ticks, lots, Int128 notional units).</summary>
public readonly struct VwapFill
{
    public VwapFill(long requestedLots, long filledLots, Int128 notionalUnits, long bestPxTicks, long worstPxTicks, int levelsUsed)
    {
        RequestedLots = requestedLots;
        FilledLots = filledLots;
        NotionalUnits = notionalUnits;
        BestPxTicks = bestPxTicks;
        WorstPxTicks = worstPxTicks;
        LevelsUsed = levelsUsed;
    }

    public long RequestedLots { get; }

    public long FilledLots { get; }

    /// <summary>Σ ticks × lots. Quote notional = NotionalUnits × tick × step.</summary>
    public Int128 NotionalUnits { get; }

    public long BestPxTicks { get; }

    /// <summary>Marginal (last) level price — this is the IOC limit price that sweeps exactly the walked levels.</summary>
    public long WorstPxTicks { get; }

    public int LevelsUsed { get; }

    public bool IsComplete => RequestedLots > 0 && FilledLots >= RequestedLots;

    /// <summary>VWAP in (fractional) ticks; NaN when nothing filled.</summary>
    public double VwapTicks => FilledLots == 0 ? double.NaN : (double)NotionalUnits / FilledLots;

    /// <summary>VWAP in canonical quote-per-base-unit (handles BaseMultiplier).</summary>
    public double CanonicalVwap(InstrumentSpec spec) => VwapTicks * spec.CanonPxPerTick;

    public decimal NotionalQuoteExact(InstrumentSpec spec) => spec.NotionalQuoteExact(NotionalUnits);
}

/// <summary>
/// Order-book walking (L2 depth consumption). Never decide on top-of-book: the price you actually pay for
/// quantity Q is the VWAP over the levels needed to fill Q, and the IOC limit must be the marginal level.
/// All methods are allocation-free and operate on best-first spans.
/// </summary>
public static class VwapWalker
{
    public const int FullLiquidityPpm = 1_000_000;

    /// <summary>Walk until <paramref name="targetLots"/> are filled or depth runs out.</summary>
    /// <param name="haircutPpm">Fraction (ppm) of displayed size assumed takeable (competition for liquidity).</param>
    public static VwapFill WalkLots(ReadOnlySpan<PriceLevel> levelsBestFirst, long targetLots, int haircutPpm = FullLiquidityPpm)
    {
        ValidateHaircut(haircutPpm);
        if (targetLots <= 0)
        {
            return new VwapFill(targetLots, 0, 0, 0, 0, 0);
        }

        long remaining = targetLots;
        Int128 notional = 0;
        long best = 0;
        long worst = 0;
        int used = 0;
        for (int i = 0; i < levelsBestFirst.Length && remaining > 0; i++)
        {
            long avail = Available(levelsBestFirst[i].QtyLots, haircutPpm);
            if (avail <= 0)
            {
                continue;
            }

            long take = Math.Min(avail, remaining);
            long px = levelsBestFirst[i].PxTicks;
            notional += (Int128)take * px;
            remaining -= take;
            if (used == 0)
            {
                best = px;
            }

            worst = px;
            used++;
        }

        return new VwapFill(targetLots, targetLots - remaining, notional, best, worst, used);
    }

    /// <summary>Walk but never cross <paramref name="limitPxTicks"/> (Buy taker: asks ≤ limit; Sell taker: bids ≥ limit).</summary>
    public static VwapFill WalkLotsWithinLimit(
        ReadOnlySpan<PriceLevel> levelsBestFirst,
        long targetLots,
        long limitPxTicks,
        Side takerSide,
        int haircutPpm = FullLiquidityPpm)
    {
        int n = 0;
        while (n < levelsBestFirst.Length && WithinLimit(levelsBestFirst[n].PxTicks, limitPxTicks, takerSide))
        {
            n++;
        }

        return WalkLots(levelsBestFirst[..n], targetLots, haircutPpm);
    }

    /// <summary>Spend a quote budget (in notional units = quote / (tick×step)). Partial last level is floored to whole lots.</summary>
    public static VwapFill WalkQuote(ReadOnlySpan<PriceLevel> levelsBestFirst, Int128 budgetUnits, int haircutPpm = FullLiquidityPpm)
    {
        ValidateHaircut(haircutPpm);
        long filled = 0;
        Int128 notional = 0;
        long best = 0;
        long worst = 0;
        int used = 0;
        Int128 left = budgetUnits;
        for (int i = 0; i < levelsBestFirst.Length && left > 0; i++)
        {
            long avail = Available(levelsBestFirst[i].QtyLots, haircutPpm);
            long px = levelsBestFirst[i].PxTicks;
            if (avail <= 0)
            {
                continue;
            }

            Int128 cost = (Int128)avail * px;
            long take = cost <= left ? avail : (long)(left / px);
            if (take <= 0)
            {
                break;
            }

            Int128 spent = (Int128)take * px;
            notional += spent;
            left -= spent;
            filled += take;
            if (used == 0)
            {
                best = px;
            }

            worst = px;
            used++;
        }

        return new VwapFill(filled, filled, notional, best, worst, used);
    }

    /// <summary>Convert a quote budget to notional units for <see cref="WalkQuote"/> (floored, never overspends).</summary>
    public static Int128 BudgetUnits(InstrumentSpec spec, decimal quoteBudget)
    {
        ArgumentNullException.ThrowIfNull(spec);
        decimal u = decimal.Floor(quoteBudget / spec.QuotePerNotionalUnitExact);
        return u <= 0 ? 0 : (Int128)u;
    }

    private static bool WithinLimit(long px, long limit, Side takerSide) =>
        takerSide == Side.Buy ? px <= limit : px >= limit;

    private const long MaxQtyForLongHaircut = long.MaxValue / FullLiquidityPpm; // ≈ 9.2e12 lots

    // Since R-03 the exact walks run WITH the haircut on every evaluation: keep it on 64-bit arithmetic (Int128
    // division is several times slower); Int128 only for absurd level sizes. Identical result (floor).
    private static long Available(long qty, int haircutPpm) =>
        haircutPpm == FullLiquidityPpm ? qty
        : qty <= MaxQtyForLongHaircut ? qty * haircutPpm / FullLiquidityPpm
        : (long)((Int128)qty * haircutPpm / FullLiquidityPpm);

    private static void ValidateHaircut(int haircutPpm)
    {
        if (haircutPpm <= 0 || haircutPpm > FullLiquidityPpm)
        {
            throw new ArgumentOutOfRangeException(nameof(haircutPpm), haircutPpm, "1..1_000_000");
        }
    }
}
