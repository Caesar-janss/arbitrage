using Arb.Core.Instruments;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;

namespace Arb.Core.Execution;

public enum RepairMethod : byte
{
    None = 0,
    CompleteSellLeg = 1,
    CompleteBuyLeg = 2,
    Unwind = 3,
    PerpHedge = 4,
}

public enum RepairVerdict : byte
{
    /// <summary>|residual| ≤ dust: nothing to do.</summary>
    Flat = 0,
    Planned = 1,
    /// <summary>Residual exists but is below every venue's lot / min-notional: book as dust, alert if material.</summary>
    BelowVenueMinimum = 2,
    /// <summary>No venue available (halted / stale / empty) → STUCK + HALT_GLOBAL.</summary>
    NoViableVenue = 3,
}

/// <summary>
/// One way to flatten a residual. The engine builds the candidate list for the residual's sign:
///   long residual  (bought > sold): CompleteSellLeg on B, Unwind (sell) on A, PerpHedge (short) on a perp.
///   short residual (sold > bought): CompleteBuyLeg on A, Unwind (buy back) on B, PerpHedge (long) on a perp.
/// </summary>
public readonly struct RepairCandidate
{
    public RepairMethod Method { get; init; }

    public InstrumentSpec Spec { get; init; }

    public BookView Book { get; init; }

    /// <summary>Taker fee rate (fraction) on this venue.</summary>
    public double FeeRate { get; init; }

    /// <summary>
    /// Where the fee lands (FeeModel.Resolve). Base on a BUY = taken from the received coins → the order is grossed up
    /// like the entry leg (R-13); Base on a SELL = extra coins leave the wallet → the order is sized down. Default = quote.
    /// </summary>
    public FeeAssetKind FeeAsset { get; init; }

    /// <summary>False when the venue is halted (e.g. HTTP 429), the book is stale, or inventory is missing.</summary>
    public bool Available { get; init; }

    public bool ReduceOnly { get; init; }

    /// <summary>Additional expected cost in bps (PerpHedge: later unwind of the hedge = extra fees + spread).</summary>
    public double ExtraCostBps { get; init; }
}

public readonly struct RepairPolicy
{
    /// <summary>Above this expected loss the plan is flagged (counts toward the loss breaker).</summary>
    public double MaxStopLossBps { get; init; }

    /// <summary>Above this the engine escalates to HALT_GLOBAL after flattening.</summary>
    public double SevereLossBps { get; init; }

    /// <summary>Marketable-limit tolerance per escalation step, e.g. [10, 30, 80] bps.</summary>
    public ReadOnlyMemory<double> LadderBps { get; init; }

    /// <summary>|residual| at or below this (canonical base) counts as flat.</summary>
    public double DustCanon { get; init; }
}

public readonly struct RepairPlan
{
    public RepairVerdict Verdict { get; init; }

    public RepairMethod Method { get; init; }

    public OrderIntent Order { get; init; }

    public int CandidateIndex { get; init; }

    /// <summary>Signed residual before repair (canonical base).</summary>
    public double ResidualCanon { get; init; }

    public double PlannedQtyCanon { get; init; }

    /// <summary>Residual left after this order because it is below one lot of the chosen venue.</summary>
    public double UntradeableCanon { get; init; }

    /// <summary>Flat / BelowVenueMinimum: |residual| × reference price — what accepting it as dust writes off (alert above budget).</summary>
    public double DustQuote { get; init; }

    public double ExpectedVwap { get; init; }

    /// <summary>Expected loss vs. reference price (+ = loss), incl. fee and ExtraCostBps.</summary>
    public double ExpectedCostBps { get; init; }

    public double ExpectedFillRatio { get; init; }

    public double ToleranceBps { get; init; }

    public bool ExceedsStopLoss { get; init; }

    public bool ExceedsSevere { get; init; }
}

/// <summary>
/// Emergency unwind / auto-hedge planner (pure). Chooses the candidate with the lowest expected cost
/// (book-walked VWAP after fees vs. the price the residual was acquired at), penalizing venues that cannot
/// absorb the full residual. Orders are MARKETABLE-LIMIT IOC (limit = marginal level ± ladder tolerance),
/// never naked market orders: on a thin book a market order has unbounded slippage.
///
/// MaxStopLossBps does NOT block flattening: leaving the residual open is an unhedged directional bet,
/// which is the larger risk. It flags the plan so the circuit breaker and alerting react.
/// </summary>
public static class RepairPlanner
{
    public static RepairPlan Plan(
        decimal netBaseDelta,
        double referencePx,
        ReadOnlySpan<RepairCandidate> candidates,
        in RepairPolicy policy,
        int ladderStep,
        long clientOrderId)
    {
        if (!(referencePx > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(referencePx), referencePx, "must be > 0");
        }

        ReadOnlySpan<double> ladder = policy.LadderBps.Span;
        if (ladder.IsEmpty)
        {
            throw new ArgumentException("repair ladder must not be empty", nameof(policy));
        }

        double residual = (double)netBaseDelta;
        double absRes = Math.Abs(residual);
        decimal absResExact = Math.Abs(netBaseDelta);
        if (absRes <= policy.DustCanon)
        {
            return new RepairPlan { Verdict = RepairVerdict.Flat, ResidualCanon = residual, CandidateIndex = -1, DustQuote = absRes * referencePx };
        }

        Side side = residual > 0 ? Side.Sell : Side.Buy;
        double tolBps = ladder[Math.Clamp(ladderStep, 0, ladder.Length - 1)];

        int best = -1;
        double bestScore = double.PositiveInfinity;
        double bestCost = 0;
        double bestVwap = 0;
        double bestFillRatio = 0;
        long bestLots = 0;
        long bestWorstTicks = 0;
        bool anyBelowMinimum = false;

        for (int k = 0; k < candidates.Length; k++)
        {
            ref readonly RepairCandidate c = ref candidates[k];
            if (!c.Available || c.Spec is null || c.Book is null || !c.Book.IsUsable || c.Book.Instrument != c.Spec.Key
                || c.Book.GridGeneration != c.Spec.GridGeneration)
            {
                continue;
            }

            bool baseFee = c.FeeAsset == FeeAssetKind.Base && c.FeeRate > 0;
            long lots = !baseFee
                ? c.Spec.LotsFromCanonical(absRes, Rounding.Down)
                : side == Side.Buy
                    ? LotNormalizer.GrossUpBuyLots(absResExact, c.FeeRate, c.Spec)      // receive ≥ residual
                    : c.Spec.LotsFromCanonical(absRes / (1 + c.FeeRate), Rounding.Down); // sold + fee ≤ residual
            if (lots <= 0)
            {
                anyBelowMinimum = true;
                continue;
            }

            VwapFill fill = VwapWalker.WalkLots(c.Book.LiquidityFor(side), lots);
            if (fill.FilledLots == 0)
            {
                continue;
            }

            double vwap = fill.CanonicalVwap(c.Spec);
            if (!c.Spec.MeetsMinimums(lots, c.Spec.CanonicalQty(lots) * vwap))
            {
                anyBelowMinimum = true;
                continue;
            }

            // cost per unit of residual actually flattened: a base fee changes the quantity, not the cash
            double perUnit = side == Side.Sell
                ? (baseFee ? vwap / (1 + c.FeeRate) : vwap * (1 - c.FeeRate))
                : (baseFee ? vwap / (1 - c.FeeRate) : vwap * (1 + c.FeeRate));
            double cost = (side == Side.Sell ? referencePx - perUnit : perUnit - referencePx) / referencePx * 1e4;
            cost += c.ExtraCostBps;
            double fillRatio = (double)fill.FilledLots / lots;

            // Unfilled remainder is assumed to cost twice the severe threshold → full-depth venues win.
            double score = cost + ((1 - fillRatio) * 2 * Math.Max(policy.SevereLossBps, 1));
            if (score < bestScore)
            {
                best = k;
                bestScore = score;
                bestCost = cost;
                bestVwap = vwap;
                bestFillRatio = fillRatio;
                bestLots = lots;
                bestWorstTicks = fill.WorstPxTicks;
            }
        }

        if (best < 0)
        {
            return new RepairPlan
            {
                Verdict = anyBelowMinimum ? RepairVerdict.BelowVenueMinimum : RepairVerdict.NoViableVenue,
                ResidualCanon = residual,
                UntradeableCanon = absRes,
                CandidateIndex = -1,
                DustQuote = anyBelowMinimum ? absRes * referencePx : 0,
            };
        }

        ref readonly RepairCandidate chosen = ref candidates[best];

        // Exact decimal: limit = marginal level × (1 ∓ tolerance), rounded AWAY from the book's favour
        // (sell → down, buy → up) so the IOC stays marketable. double here can miss by 1 tick at ~1e6 ticks.
        decimal tol = (decimal)tolBps / 10_000m;
        decimal raw = bestWorstTicks * (side == Side.Sell ? 1m - tol : 1m + tol);
        long limitTicks = (long)(side == Side.Sell ? decimal.Floor(raw) : decimal.Ceiling(raw));
        if (limitTicks < 1)
        {
            limitTicks = 1;
        }

        double planned = chosen.Spec.CanonicalQty(bestLots);
        if (chosen.FeeAsset == FeeAssetKind.Base && chosen.FeeRate > 0)
        {
            planned = side == Side.Buy ? planned * (1 - chosen.FeeRate) : planned * (1 + chosen.FeeRate); // residual flattened
        }

        return new RepairPlan
        {
            Verdict = RepairVerdict.Planned,
            Method = chosen.Method,
            Order = new OrderIntent(chosen.Spec.Key, side, OrderType.Limit, TimeInForce.Ioc, bestLots, limitTicks, clientOrderId, chosen.ReduceOnly),
            CandidateIndex = best,
            ResidualCanon = residual,
            PlannedQtyCanon = planned,
            UntradeableCanon = Math.Max(0, absRes - planned),
            ExpectedVwap = bestVwap,
            ExpectedCostBps = bestCost,
            ExpectedFillRatio = bestFillRatio,
            ToleranceBps = tolBps,
            ExceedsStopLoss = bestCost > policy.MaxStopLossBps,
            ExceedsSevere = bestCost > policy.SevereLossBps,
        };
    }
}
