using Arb.Core.Instruments;
using Arb.Core.MarketData;

namespace Arb.Core.Pricing;

/// <summary>Why the Q* search stopped.</summary>
public enum CrossStop : byte
{
    NoCross = 0,
    MarginalEdge = 1,
    BuyDepth = 2,
    SellDepth = 3,
    QtyCap = 4,
    NotionalCap = 5,
    LevelCap = 6,
}

/// <summary>Per-unit cost factors, all proportional to price except <see cref="PerUnitQuote"/>.</summary>
public readonly struct CrossCosts
{
    public CrossCosts(double buyFeeRate, double sellFeeRate, double buySlipBps, double sellSlipBps, double perUnitQuote, double minMarginalEdgeBps)
    {
        BuyFactor = 1 + buyFeeRate + (buySlipBps * 1e-4);
        SellFactor = 1 - sellFeeRate - (sellSlipBps * 1e-4);
        PerUnitQuote = perUnitQuote;
        Hurdle = minMarginalEdgeBps * 1e-4;
    }

    /// <summary>Effective cost multiplier on the ask price (1 + fee + slippage).</summary>
    public double BuyFactor { get; }

    /// <summary>Effective proceeds multiplier on the bid price (1 − fee − slippage).</summary>
    public double SellFactor { get; }

    public double PerUnitQuote { get; }

    /// <summary>Required net edge of EACH marginal unit, as a fraction of its ask price (risk hurdle).</summary>
    public double Hurdle { get; }
}

public readonly struct CrossLimits
{
    public CrossLimits(double maxQtyCanon, double maxBuyNotional, int haircutPpm, int maxLevelsPerSide)
    {
        if (!(maxQtyCanon > 0) || !(maxBuyNotional > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(maxQtyCanon), "caps must be > 0 (use +Infinity for none)");
        }

        if (haircutPpm <= 0 || haircutPpm > VwapWalker.FullLiquidityPpm)
        {
            throw new ArgumentOutOfRangeException(nameof(haircutPpm), haircutPpm, "1..1_000_000");
        }

        if (maxLevelsPerSide <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLevelsPerSide), maxLevelsPerSide, "must be > 0");
        }

        MaxQtyCanon = maxQtyCanon;
        MaxBuyNotional = maxBuyNotional;
        HaircutPpm = haircutPpm;
        MaxLevelsPerSide = maxLevelsPerSide;
    }

    public double MaxQtyCanon { get; }

    public double MaxBuyNotional { get; }

    public int HaircutPpm { get; }

    public int MaxLevelsPerSide { get; }
}

public readonly struct CrossResult
{
    public double QtyCanon { get; init; }

    public double BuyNotional { get; init; }

    public double SellNotional { get; init; }

    /// <summary>Σ marginal net (after proportional fees, slippage, per-unit cost; before the hurdle).</summary>
    public double ExpectedNetQuote { get; init; }

    public int BuyLevels { get; init; }

    public int SellLevels { get; init; }

    public CrossStop Stop { get; init; }

    /// <summary>Net per unit of the last evaluated marginal unit (≤ hurdle when Stop = MarginalEdge).</summary>
    public double LastMarginalNetPerUnit { get; init; }
}

/// <summary>
/// Q* = argmax NetProfit(Q) for "buy on A's asks, sell on B's bids".
///
/// With proportional costs, the marginal net of the q-th unit is
///     m(q) = bid_B(q)·(1 − f_B − s_B) − ask_A(q)·(1 + f_A + s_A) − c_unit
/// and is non-increasing in q (asks rise, bids fall), so NetProfit(Q) = ∫ m(q) dq is concave and
/// piecewise linear. Its maximum is where m(q) crosses the hurdle → a single merge-walk of both sides,
/// O(levels_A + levels_B), no search over Q, no allocation. A fixed Q (e.g. "500 USDT") either leaves
/// edge on the table or eats levels with negative marginal edge.
///
/// Works in canonical units, so instruments with different BaseMultiplier/lot steps compare correctly.
/// Result is continuous; caller snaps it to the common lot grid (floor) and re-prices exactly.
/// </summary>
public static class CrossedBookOptimizer
{
    private const double RelEps = 1e-12;

    public static CrossResult Optimize(
        ReadOnlySpan<PriceLevel> asksBestFirst,
        InstrumentSpec buy,
        ReadOnlySpan<PriceLevel> bidsBestFirst,
        InstrumentSpec sell,
        in CrossCosts costs,
        in CrossLimits limits)
    {
        ArgumentNullException.ThrowIfNull(buy);
        ArgumentNullException.ThrowIfNull(sell);
        if (asksBestFirst.IsEmpty || bidsBestFirst.IsEmpty)
        {
            return new CrossResult { Stop = CrossStop.NoCross };
        }

        double haircut = limits.HaircutPpm / (double)VwapWalker.FullLiquidityPpm;
        int lastA = Math.Min(asksBestFirst.Length, limits.MaxLevelsPerSide);
        int lastB = Math.Min(bidsBestFirst.Length, limits.MaxLevelsPerSide);

        int i = 0;
        int j = 0;
        double levelA = buy.CanonicalQty(asksBestFirst[0].QtyLots) * haircut;
        double levelB = sell.CanonicalQty(bidsBestFirst[0].QtyLots) * haircut;
        double remA = levelA;
        double remB = levelB;
        double ask = buy.CanonicalPrice(asksBestFirst[0].PxTicks);
        double bid = sell.CanonicalPrice(bidsBestFirst[0].PxTicks);

        double q = 0;
        double buyN = 0;
        double sellN = 0;
        double net = 0;
        int usedA = -1;
        int usedB = -1;
        double lastMarginal;
        CrossStop stop;

        while (true)
        {
            double unitNet = (bid * costs.SellFactor) - (ask * costs.BuyFactor) - costs.PerUnitQuote;
            lastMarginal = unitNet;
            if (unitNet <= ask * costs.Hurdle)
            {
                stop = q > 0 ? CrossStop.MarginalEdge : CrossStop.NoCross;
                break;
            }

            double take = Math.Min(remA, remB);
            CrossStop capStop = CrossStop.NoCross;
            double capQ = limits.MaxQtyCanon - q;
            if (capQ <= take)
            {
                take = capQ;
                capStop = CrossStop.QtyCap;
            }

            double capN = (limits.MaxBuyNotional - buyN) / ask;
            if (capN <= take)
            {
                take = capN;
                capStop = CrossStop.NotionalCap;
            }

            if (take > 0)
            {
                q += take;
                buyN += take * ask;
                sellN += take * bid;
                net += take * unitNet;
                remA -= take;
                remB -= take;
                usedA = i;
                usedB = j;
            }

            if (capStop != CrossStop.NoCross)
            {
                stop = capStop;
                break;
            }

            if (remA <= levelA * RelEps)
            {
                i++;
                if (i >= lastA)
                {
                    stop = i >= asksBestFirst.Length ? CrossStop.BuyDepth : CrossStop.LevelCap;
                    break;
                }

                levelA = buy.CanonicalQty(asksBestFirst[i].QtyLots) * haircut;
                remA = levelA;
                ask = buy.CanonicalPrice(asksBestFirst[i].PxTicks);
            }

            if (remB <= levelB * RelEps)
            {
                j++;
                if (j >= lastB)
                {
                    stop = j >= bidsBestFirst.Length ? CrossStop.SellDepth : CrossStop.LevelCap;
                    break;
                }

                levelB = sell.CanonicalQty(bidsBestFirst[j].QtyLots) * haircut;
                remB = levelB;
                bid = sell.CanonicalPrice(bidsBestFirst[j].PxTicks);
            }
        }

        return new CrossResult
        {
            QtyCanon = q,
            BuyNotional = buyN,
            SellNotional = sellN,
            ExpectedNetQuote = net,
            BuyLevels = usedA + 1,
            SellLevels = usedB + 1,
            Stop = stop,
            LastMarginalNetPerUnit = lastMarginal,
        };
    }
}
