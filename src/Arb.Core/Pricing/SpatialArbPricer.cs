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
///  gross-up buy leg if its fee is charged in base → 4) exact integer VWAP walks WITH THE SAME HAIRCUT + marginal limit prices →
///  5) venue minimums → 6) exact net edge vs MinProfit.
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

        long sellLots = units * ctx.Grid.LotsPerUnitB;
        decimal sellQtyExact = sell.CanonicalQtyExact(sellLots);
        long buyLots = p.BuyFee.Asset == FeeAssetKind.Base
            ? LotNormalizer.GrossUpBuyLots(sellQtyExact, p.BuyFee.Rate, buy)
            : units * ctx.Grid.LotsPerUnitA;

        // Same liquidity assumption as Q* (R-03): walk only HaircutPpm of each displayed level. The marginal level of
        // this walk is the IOC limit, so the IOC can sweep FULL displayed size up to it and still fill the whole leg
        // when only the haircut fraction is really there; VWAP / edge are priced at that (conservative) depth.
        VwapFill buyFill = VwapWalker.WalkLots(asks, buyLots, p.HaircutPpm);
        VwapFill sellFill = VwapWalker.WalkLots(bids, sellLots, p.HaircutPpm);
        if (!buyFill.IsComplete || !sellFill.IsComplete)
        {
            return Unpriced(RejectReason.RejectThreshold, cross, askTob, bidTob, tobBps);
        }

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
            BuyNotionalExact = buyFill.NotionalQuoteExact(buy),
            SellQtyExact = sellQtyExact,
            OptimizerQtyCanon = cross.QtyCanon,
            Edge = edge,
        };
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
