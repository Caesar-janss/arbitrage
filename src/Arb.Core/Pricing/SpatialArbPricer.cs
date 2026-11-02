using Arb.Core.Instruments;
using Arb.Core.MarketData;
using Arb.Core.Primitives;

namespace Arb.Core.Pricing;

/// <summary>Static, per-direction context (build once per "buy on A / sell on B" pair; cold path).</summary>
public sealed class SpatialPairContext
{
    public SpatialPairContext(InstrumentSpec buy, InstrumentSpec sell)
    {
        ArgumentNullException.ThrowIfNull(buy);
        ArgumentNullException.ThrowIfNull(sell);
        if (buy.Key == sell.Key)
        {
            throw new ArgumentException("buy and sell instrument must differ");
        }

        if (buy.QuoteAsset != sell.QuoteAsset)
        {
            throw new ArgumentException("quote assets must match (no implicit FX)");
        }

        Buy = buy;
        Sell = sell;
        Grid = new PairLotGrid(buy, sell);
    }

    public InstrumentSpec Buy { get; }

    public InstrumentSpec Sell { get; }

    public PairLotGrid Grid { get; }
}

/// <summary>Dynamic inputs of one evaluation (fees resolved for current token balance, slippage from live σ, limits).</summary>
public readonly struct SpatialParams
{
    public ResolvedFee BuyFee { get; init; }

    public ResolvedFee SellFee { get; init; }

    public double BuySlipBps { get; init; }

    public double SellSlipBps { get; init; }

    public double RebalancePerUnitQuote { get; init; }

    public double MinMarginalEdgeBps { get; init; }

    public double MinProfitQuote { get; init; }

    public double MaxQtyCanon { get; init; }

    public double MaxBuyNotional { get; init; }

    public int HaircutPpm { get; init; }

    public int MaxLevelsPerSide { get; init; }
}

/// <summary>
/// Decision + everything needed to fire both IOC legs and to persist trd.opportunity.
/// Persistence contract (R-15): a price the pricer did not compute is NaN (never 0) and is written as NULL; when
/// <see cref="HasExecutionPricing"/> is false, qty / VWAP / worst px / levels / edge / cost columns are NULL. V007 makes
/// those columns nullable for every decision except EXECUTE.
/// </summary>
public readonly struct SpatialQuote
{
    public RejectReason Decision { get; init; }

    /// <summary>Both legs were walked and the edge computed (EXECUTE, or a reject after pricing).</summary>
    public bool HasExecutionPricing { get; init; }

    public CrossStop Stop { get; init; }

    public double BuyTobPx { get; init; }

    public double SellTobPx { get; init; }

    public double TobEdgeBps { get; init; }

    public long BuyLots { get; init; }

    public long SellLots { get; init; }

    /// <summary>IOC limit = marginal walked level (exact venue ticks).</summary>
    public long BuyLimitTicks { get; init; }

    public long SellLimitTicks { get; init; }

    public double BuyVwap { get; init; }

    public double SellVwap { get; init; }

    public double BuyWorstPx { get; init; }

    public double SellWorstPx { get; init; }

    public int BuyLevels { get; init; }

    public int SellLevels { get; init; }

    /// <summary>Exact quote needed on the buy venue (before fee).</summary>
    public decimal BuyNotionalExact { get; init; }

    /// <summary>Exact canonical base delivered on the sell venue.</summary>
    public decimal SellQtyExact { get; init; }

    public double OptimizerQtyCanon { get; init; }

    public EdgeBreakdown Edge { get; init; }

    public bool IsExecutable => Decision == RejectReason.Execute;
}

/// <summary>
/// Spatial opportunity pricing pipeline (pure, allocation-free):
///  1) usable books → 2) Q* merge-walk with haircut/caps/hurdle → 3) snap to common lot (floor),
///  gross-up buy leg if its fee is charged in base → 4) exact integer VWAP walks WITH THE SAME HAIRCUT, within
///  MaxLevelsPerSide, + marginal limit prices → 4b) final legs vs exact caps; if one breaks, the largest common size that
///  fits (C-02) → 5) venue minimums → 6) exact net edge vs MinProfit.
/// Invariant: IsExecutable ⇒ final buy notional ≤ MaxBuyNotional, both legs ≤ MaxQtyCanon, levels ≤ MaxLevelsPerSide,
/// both walks complete on haircut depth (exact decimal / integer comparisons).
/// Staleness, sanity (wallet status), inventory and halts are separate gates (Risk/*), applied by the engine.
/// </summary>
public static class SpatialArbPricer
{
    public static SpatialQuote Evaluate(SpatialPairContext ctx, BookView buyBook, BookView sellBook, in SpatialParams p)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(buyBook);
        ArgumentNullException.ThrowIfNull(sellBook);
        InstrumentSpec buy = ctx.Buy;
        InstrumentSpec sell = ctx.Sell;
        if (buyBook.Instrument != buy.Key || sellBook.Instrument != sell.Key)
        {
            throw new ArgumentException("book does not belong to the pair context");
        }

        if (!buyBook.IsUsable || !sellBook.IsUsable)
        {
            return Unpriced(RejectReason.RejectStale, default, double.NaN, double.NaN, double.NaN);
        }

        if (buyBook.GridGeneration != buy.GridGeneration || sellBook.GridGeneration != sell.GridGeneration)
        {
            // ticks/lots parsed with another grid (R-08): even the top of book is not a price
            return Unpriced(RejectReason.RejectStale, default, double.NaN, double.NaN, double.NaN);
        }

        ReadOnlySpan<PriceLevel> asks = buyBook.Asks;
        ReadOnlySpan<PriceLevel> bids = sellBook.Bids;
        double askTob = buy.CanonicalPrice(asks[0].PxTicks);
        double bidTob = sell.CanonicalPrice(bids[0].PxTicks);
        double tobBps = (bidTob - askTob) / askTob * 1e4;

        var costs = new CrossCosts(p.BuyFee.Rate, p.SellFee.Rate, p.BuySlipBps, p.SellSlipBps, p.RebalancePerUnitQuote, p.MinMarginalEdgeBps);
        var limits = new CrossLimits(p.MaxQtyCanon, p.MaxBuyNotional, p.HaircutPpm, p.MaxLevelsPerSide);
        CrossResult cross = CrossedBookOptimizer.Optimize(asks, buy, bids, sell, costs, limits);
        if (cross.QtyCanon <= 0)
        {
            return Unpriced(RejectReason.RejectThreshold, cross, askTob, bidTob, tobBps);
        }

        long units = ctx.Grid.UnitsFloor(cross.QtyCanon);
        if (units <= 0)
        {
            return Unpriced(RejectReason.RejectMinNotional, cross, askTob, bidTob, tobBps);
        }

        // Final legs are checked against EVERY cap again (C-02): the base-fee gross-up of the buy leg and lot rounding
        // happen after Q*, and can push the order past MaxQtyCanon / MaxBuyNotional / MaxLevelsPerSide / haircut depth.
        // Only the first MaxLevelsPerSide levels are walked, with the same haircut as Q* (R-03): the marginal level of
        // this walk is the IOC limit, so the IOC can sweep FULL displayed size up to it and still fill the whole leg when
        // only the haircut fraction is really there; VWAP / edge are priced at that (conservative) depth.
        var caps = new FinalCaps(p);
        ReadOnlySpan<PriceLevel> walkAsks = asks[..Math.Min(asks.Length, p.MaxLevelsPerSide)];
        ReadOnlySpan<PriceLevel> walkBids = bids[..Math.Min(bids.Length, p.MaxLevelsPerSide)];
        if (!TrySize(ctx, walkAsks, walkBids, p, caps, units, out Legs legs))
        {
            // Every cap is monotone in the common size (gross-up, lots, notional, levels, depth all grow with it):
            // binary search for the largest size that fits; 0 → nothing fits → reject.
            long lo = 0;
            long hi = units;
            Legs best = default;
            while (hi - lo > 1)
            {
                long mid = lo + ((hi - lo) / 2);
                if (TrySize(ctx, walkAsks, walkBids, p, caps, mid, out Legs candidate))
                {
                    lo = mid;
                    best = candidate;
                }
                else
                {
                    hi = mid;
                }
            }

            if (lo == 0)
            {
                return Unpriced(RejectReason.RejectThreshold, cross, askTob, bidTob, tobBps);
            }

            legs = best;
        }

        long buyLots = legs.BuyLots;
        long sellLots = legs.SellLots;
        decimal sellQtyExact = legs.SellQtyExact;
        VwapFill buyFill = legs.BuyFill;
        VwapFill sellFill = legs.SellFill;
        if (!buy.MeetsMinimumsExact(buyLots, buyFill.NotionalUnits) || !sell.MeetsMinimumsExact(sellLots, sellFill.NotionalUnits))
        {
            return Unpriced(RejectReason.RejectMinNotional, cross, askTob, bidTob, tobBps);
        }

        double buyVwap = buyFill.CanonicalVwap(buy);
        double sellVwap = sellFill.CanonicalVwap(sell);
        double buyQty = buy.CanonicalQty(buyLots);
        double sellQty = sell.CanonicalQty(sellLots);

        // Residual (gross-up / base-fee dust) is valued WHERE it lands (R-19): long dust stays on the buy venue → its
        // bid; short dust (base fee on the sell leg) must be bought back on the sell venue → its ask.
        double residual = (buyQty * (p.BuyFee.Asset == FeeAssetKind.Base ? 1 - p.BuyFee.Rate : 1))
            - (sellQty * (p.SellFee.Asset == FeeAssetKind.Base ? 1 + p.SellFee.Rate : 1));
        double residualMark = residual >= 0
            ? buy.CanonicalPrice(buyBook.Bids[0].PxTicks)
            : sell.CanonicalPrice(sellBook.Asks[0].PxTicks);
        EdgeBreakdown edge = NetEdgeCalculator.Compute(new EdgeInputs(
            buyQty,
            sellQty,
            buyVwap,
            sellVwap,
            p.BuyFee,
            p.SellFee,
            p.BuySlipBps,
            p.SellSlipBps,
            p.RebalancePerUnitQuote,
            residualMark));

        return new SpatialQuote
        {
            Decision = edge.NetQuote >= p.MinProfitQuote ? RejectReason.Execute : RejectReason.RejectThreshold,
            HasExecutionPricing = true,
            Stop = cross.Stop,
            BuyTobPx = askTob,
            SellTobPx = bidTob,
            TobEdgeBps = tobBps,
            BuyLots = buyLots,
            SellLots = sellLots,
            BuyLimitTicks = buyFill.WorstPxTicks,
            SellLimitTicks = sellFill.WorstPxTicks,
            BuyVwap = buyVwap,
            SellVwap = sellVwap,
            BuyWorstPx = buy.CanonicalPrice(buyFill.WorstPxTicks),
            SellWorstPx = sell.CanonicalPrice(sellFill.WorstPxTicks),
            BuyLevels = buyFill.LevelsUsed,
            SellLevels = sellFill.LevelsUsed,
            BuyNotionalExact = legs.BuyNotionalExact,
            SellQtyExact = sellQtyExact,
            OptimizerQtyCanon = cross.QtyCanon,
            Edge = edge,
        };
    }

    /// <summary>Size both legs for <paramref name="units"/> common lots and check them against the exact caps.</summary>
    private static bool TrySize(
        SpatialPairContext ctx,
        ReadOnlySpan<PriceLevel> asks,
        ReadOnlySpan<PriceLevel> bids,
        in SpatialParams p,
        in FinalCaps caps,
        long units,
        out Legs legs)
    {
        InstrumentSpec buy = ctx.Buy;
        InstrumentSpec sell = ctx.Sell;
        legs = default;
        legs.SellLots = units * ctx.Grid.LotsPerUnitB;
        legs.SellQtyExact = sell.CanonicalQtyExact(legs.SellLots);
        legs.BuyLots = p.BuyFee.Asset == FeeAssetKind.Base
            ? LotNormalizer.GrossUpBuyLots(legs.SellQtyExact, p.BuyFee.Rate, buy)
            : units * ctx.Grid.LotsPerUnitA;
        if (caps.HasQty && (buy.CanonicalQtyExact(legs.BuyLots) > caps.Qty || legs.SellQtyExact > caps.Qty))
        {
            return false;
        }

        legs.BuyFill = VwapWalker.WalkLots(asks, legs.BuyLots, p.HaircutPpm);
        legs.SellFill = VwapWalker.WalkLots(bids, legs.SellLots, p.HaircutPpm);
        if (!legs.BuyFill.IsComplete || !legs.SellFill.IsComplete)
        {
            return false;
        }

        legs.BuyNotionalExact = legs.BuyFill.NotionalQuoteExact(buy);
        return !caps.HasNotional || legs.BuyNotionalExact <= caps.Notional;
    }

    /// <summary>
    /// Caps as exact decimals. A configured double converts to its shortest decimal form (600 → 600, 0.01 → 0.01), so an
    /// order is compared with the number the operator wrote. +∞ (or beyond decimal range) = no cap.
    /// </summary>
    private readonly struct FinalCaps
    {
        private const double NoCapAbove = 1e27;

        public FinalCaps(in SpatialParams p)
        {
            HasQty = p.MaxQtyCanon < NoCapAbove;
            Qty = HasQty ? (decimal)p.MaxQtyCanon : 0m;
            HasNotional = p.MaxBuyNotional < NoCapAbove;
            Notional = HasNotional ? (decimal)p.MaxBuyNotional : 0m;
        }

        public bool HasQty { get; }

        public decimal Qty { get; }

        public bool HasNotional { get; }

        public decimal Notional { get; }
    }

    private struct Legs
    {
        public long BuyLots;
        public long SellLots;
        public decimal SellQtyExact;
        public decimal BuyNotionalExact;
        public VwapFill BuyFill;
        public VwapFill SellFill;
    }

    /// <summary>Reject before both legs were walked: execution columns NaN / 0 levels (→ NULL), TOB when known.</summary>
    private static SpatialQuote Unpriced(RejectReason reason, in CrossResult cross, double askTob, double bidTob, double tobBps) => new()
    {
        Decision = reason,
        HasExecutionPricing = false,
        Stop = cross.Stop,
        BuyTobPx = askTob,
        SellTobPx = bidTob,
        TobEdgeBps = tobBps,
        BuyVwap = double.NaN,
        SellVwap = double.NaN,
        BuyWorstPx = double.NaN,
        SellWorstPx = double.NaN,
        OptimizerQtyCanon = cross.QtyCanon,
        Edge = EdgeBreakdown.NotComputed,
    };
}
