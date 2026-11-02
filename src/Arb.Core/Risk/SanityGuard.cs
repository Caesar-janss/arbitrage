using Arb.Core.Instruments;
using Arb.Core.Primitives;

namespace Arb.Core.Risk;

[Flags]
public enum SanityFlags : ushort
{
    None = 0,
    InstrumentNotTrading = 1 << 0,
    EdgeTooLarge = 1 << 1,
    BaseRouteClosed = 1 << 2,
    BaseRouteUnknown = 1 << 3,
    QuoteRouteClosed = 1 << 4,
    QuoteRouteUnknown = 1 << 5,
    InventoryCapApplied = 1 << 6,
    SuspectedWalletTrap = 1 << 7,
    MarketTypeMismatch = 1 << 8,

    /// <summary>A cap needs ReferencePx (quote → base) but it is missing / not finite / ≤ 0 (R-12: used to skip the cap).</summary>
    ReferencePriceInvalid = 1 << 9,

    /// <summary>Route closed but no <see cref="NonReplenishableBudget"/> was supplied for the wallet it drains (R-11).</summary>
    BudgetUnknown = 1 << 10,
}

public readonly struct SanityInput
{
    public StrategyKind Strategy { get; init; }

    public InstrumentSpec Buy { get; init; }

    public InstrumentSpec Sell { get; init; }

    /// <summary>VWAP-based gross edge (bps) of the candidate.</summary>
    public double GrossEdgeBps { get; init; }

    /// <summary>Base inventory available on the SELL venue (canonical) — what a spatial sell consumes.</summary>
    public double SellVenueBaseInventory { get; init; }

    /// <summary>Quote inventory available on the BUY venue — what a spatial buy consumes.</summary>
    public double BuyVenueQuoteInventory { get; init; }

    /// <summary>Reference price to convert the quote cap into base units.</summary>
    public double ReferencePx { get; init; }

    /// <summary>Base route closed: budget of the SELL venue's base wallet since the route closed (<see cref="RouteBudgetTracker"/>).</summary>
    public NonReplenishableBudget SellVenueBaseBudget { get; init; }

    /// <summary>Quote route closed: budget (in quote) of the BUY venue's quote wallet since the route closed.</summary>
    public NonReplenishableBudget BuyVenueQuoteBudget { get; init; }
}

public readonly struct SanityLimits
{
    public SanityLimits(double maxGrossEdgeBps, double walletClosedMaxEdgeBps, double nonReplenishableFraction, long maxWalletStatusAgeMs)
    {
        MaxGrossEdgeBps = maxGrossEdgeBps;
        WalletClosedMaxEdgeBps = walletClosedMaxEdgeBps;
        NonReplenishableFraction = nonReplenishableFraction;
        MaxWalletStatusAgeMs = maxWalletStatusAgeMs;
    }

    public double MaxGrossEdgeBps { get; }

    public double WalletClosedMaxEdgeBps { get; }

    public double NonReplenishableFraction { get; }

    public long MaxWalletStatusAgeMs { get; }

    public static SanityLimits From(RiskLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return new SanityLimits(limits.SanityMaxGrossEdgeBps, limits.WalletClosedMaxEdgeBps, limits.NonReplenishableInventoryFraction, limits.MaxWalletStatusAgeMs);
    }
}

public readonly struct SanityResult
{
    public RejectReason Decision { get; init; }

    public SanityFlags Flags { get; init; }

    /// <summary>Maximum canonical qty allowed (+∞ when uncapped). The engine applies min(Q, cap).</summary>
    public double QtyCapCanon { get; init; }

    public TransferRoute BaseRoute { get; init; }

    public TransferRoute QuoteRoute { get; init; }

    public bool Passed => Decision == RejectReason.Execute;
}

/// <summary>
/// "Is this edge real?" gate.
///   * instrument must be Trading;
///   * gross edge above MaxGrossEdgeBps → reject (bad mapping e.g. 1000PEPE vs PEPE, delisting, broken feed);
///   * SPOT-SPOT spatial: coins bought on A must be movable A→B (withdraw A ∧ deposit B on a common network),
///     USDT B→A likewise. A closed route is the classic trap: spreads open BECAUSE nobody can move coins.
///       - route closed/unknown AND edge &gt; WalletClosedMaxEdgeBps → reject (SuspectedWalletTrap)
///       - route closed/unknown AND normal edge → allowed only on pre-positioned inventory: CUMULATIVELY at most a
///         fraction of what the wallet held when the route closed (<see cref="NonReplenishableBudget"/>), because it cannot
///         be replenished until the wallet reopens; no budget supplied → RejectInventory (fail closed)
///   * any leg perp (perp-perp, spot-perp): no coin transfer needed → coin wallet status ignored.
/// </summary>
public static class SanityGuard
{
    public static SanityResult Evaluate(in SanityInput x, WalletStatusBook wallets, long nowUnixMs, in SanityLimits limits)
    {
        ArgumentNullException.ThrowIfNull(wallets);
        if (x.Buy is null || x.Sell is null)
        {
            throw new ArgumentException("instruments required", nameof(x));
        }

        SanityFlags flags = SanityFlags.None;
        if (!x.Buy.IsTrading || !x.Sell.IsTrading)
        {
            return Reject(flags | SanityFlags.InstrumentNotTrading);
        }

        if (!(x.GrossEdgeBps <= limits.MaxGrossEdgeBps))
        {
            return Reject(flags | SanityFlags.EdgeTooLarge);
        }

        if (x.Strategy == StrategyKind.Spatial && x.Buy.Key.Market != x.Sell.Key.Market)
        {
            return Reject(flags | SanityFlags.MarketTypeMismatch);
        }

        bool spotSpot = x.Strategy == StrategyKind.Spatial && x.Buy.Key.Market == MarketType.Spot && x.Sell.Key.Market == MarketType.Spot;
        if (!spotSpot)
        {
            return new SanityResult { Decision = RejectReason.Execute, Flags = flags, QtyCapCanon = double.PositiveInfinity };
        }

        TransferRoute baseRoute = wallets.FindRoute(x.Buy.BaseAsset, x.Buy.Key.Venue, x.Sell.Key.Venue, nowUnixMs, limits.MaxWalletStatusAgeMs);
        TransferRoute quoteRoute = wallets.FindRoute(x.Buy.QuoteAsset, x.Sell.Key.Venue, x.Buy.Key.Venue, nowUnixMs, limits.MaxWalletStatusAgeMs);
        flags |= RouteFlag(baseRoute.Status, SanityFlags.BaseRouteClosed, SanityFlags.BaseRouteUnknown);
        flags |= RouteFlag(quoteRoute.Status, SanityFlags.QuoteRouteClosed, SanityFlags.QuoteRouteUnknown);

        double cap = double.PositiveInfinity;
        bool anyClosed = !baseRoute.IsOpen || !quoteRoute.IsOpen;
        if (anyClosed && x.GrossEdgeBps > limits.WalletClosedMaxEdgeBps)
        {
            return new SanityResult
            {
                Decision = RejectReason.RejectSanity,
                Flags = flags | SanityFlags.SuspectedWalletTrap,
                QtyCapCanon = 0,
                BaseRoute = baseRoute,
                QuoteRoute = quoteRoute,
            };
        }

        if (!baseRoute.IsOpen)
        {
            if (!x.SellVenueBaseBudget.IsKnown)
            {
                return BudgetUnknown(flags, baseRoute, quoteRoute);
            }

            cap = Math.Min(cap, Math.Min(Math.Max(0, x.SellVenueBaseInventory), x.SellVenueBaseBudget.Remaining(limits.NonReplenishableFraction)));
        }

        if (!quoteRoute.IsOpen)
        {
            if (!(x.ReferencePx > 0) || !double.IsFinite(x.ReferencePx))
            {
                return new SanityResult
                {
                    Decision = RejectReason.RejectSanity,
                    Flags = flags | SanityFlags.ReferencePriceInvalid,
                    QtyCapCanon = 0,
                    BaseRoute = baseRoute,
                    QuoteRoute = quoteRoute,
                };
            }

            if (!x.BuyVenueQuoteBudget.IsKnown)
            {
                return BudgetUnknown(flags, baseRoute, quoteRoute);
            }

            double quoteCap = Math.Min(Math.Max(0, x.BuyVenueQuoteInventory), x.BuyVenueQuoteBudget.Remaining(limits.NonReplenishableFraction));
            cap = Math.Min(cap, quoteCap / x.ReferencePx);
        }

        if (!double.IsPositiveInfinity(cap))
        {
            flags |= SanityFlags.InventoryCapApplied;
        }

        return new SanityResult
        {
            Decision = cap > 0 ? RejectReason.Execute : RejectReason.RejectInventory,
            Flags = flags,
            QtyCapCanon = cap,
            BaseRoute = baseRoute,
            QuoteRoute = quoteRoute,
        };
    }

    private static SanityFlags RouteFlag(RouteStatus status, SanityFlags closed, SanityFlags unknown) => status switch
    {
        RouteStatus.Open => SanityFlags.None,
        RouteStatus.Closed => closed,
        _ => unknown,
    };

    private static SanityResult BudgetUnknown(SanityFlags flags, in TransferRoute baseRoute, in TransferRoute quoteRoute) => new()
    {
        Decision = RejectReason.RejectInventory,
        Flags = flags | SanityFlags.BudgetUnknown,
        QtyCapCanon = 0,
        BaseRoute = baseRoute,
        QuoteRoute = quoteRoute,
    };

    private static SanityResult Reject(SanityFlags flags) => new()
    {
        Decision = RejectReason.RejectSanity,
        Flags = flags,
        QtyCapCanon = 0,
    };
}
