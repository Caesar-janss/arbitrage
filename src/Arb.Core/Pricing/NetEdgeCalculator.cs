namespace Arb.Core.Pricing;

/// <summary>Inputs in canonical units (base qty, quote per base unit).</summary>
public readonly struct EdgeInputs
{
    public EdgeInputs(
        double buyQty,
        double sellQty,
        double buyVwap,
        double sellVwap,
        ResolvedFee buyFee,
        ResolvedFee sellFee,
        double buySlipBps,
        double sellSlipBps,
        double rebalancePerUnitQuote,
        double residualMarkPx)
    {
        BuyQty = buyQty;
        SellQty = sellQty;
        BuyVwap = buyVwap;
        SellVwap = sellVwap;
        BuyFee = buyFee;
        SellFee = sellFee;
        BuySlipBps = buySlipBps;
        SellSlipBps = sellSlipBps;
        RebalancePerUnitQuote = rebalancePerUnitQuote;
        ResidualMarkPx = residualMarkPx;
    }

    public double BuyQty { get; }

    public double SellQty { get; }

    public double BuyVwap { get; }

    public double SellVwap { get; }

    public ResolvedFee BuyFee { get; }

    public ResolvedFee SellFee { get; }

    public double BuySlipBps { get; }

    public double SellSlipBps { get; }

    /// <summary>Amortized transfer cost per canonical unit moved (withdraw fee / expected units per rebalance cycle).</summary>
    public double RebalancePerUnitQuote { get; }

    /// <summary>Price used to value residual base (dust from lot rounding or base-asset fees).</summary>
    public double ResidualMarkPx { get; }
}

public readonly struct EdgeBreakdown
{
    public double BuyQty { get; init; }

    public double SellQty { get; init; }

    public double BuyNotional { get; init; }

    public double SellNotional { get; init; }

    /// <summary>SellQty × (SellVwap − BuyVwap): price edge on the matched quantity.</summary>
    public double GrossQuote { get; init; }

    public double BuyFeeQuote { get; init; }

    public double SellFeeQuote { get; init; }

    public double SlippageQuote { get; init; }

    public double RebalanceQuote { get; init; }

    /// <summary>Base left over after both legs (received − delivered). Positive = long dust.</summary>
    public double ResidualBase { get; init; }

    public double ResidualValueQuote { get; init; }

    public double NetQuote { get; init; }

    /// <summary>(SellVwap − BuyVwap) / BuyVwap × 1e4 — the DB column trd.opportunity.gross_edge_bps.</summary>
    public double GrossEdgeBps { get; init; }

    public double NetEdgeBps { get; init; }

    public double TotalFeeQuote => BuyFeeQuote + SellFeeQuote;

    /// <summary>
    /// Modelled cash PnL at the modelled VWAPs (fees + residual value, WITHOUT the slippage buffer and the amortized
    /// rebalance cost). This is what fills realize when the model is exact → the denominator of the capture ratio (R-16).
    /// NetQuote stays the (more conservative) entry gate. Persist SlippageQuote / RebalanceQuote separately.
    /// </summary>
    public double ExpectedCashQuote => NetQuote + SlippageQuote + RebalanceQuote;

    /// <summary>Placeholder for quotes rejected before pricing: every amount NaN (persisted as NULL, R-15).</summary>
    public static EdgeBreakdown NotComputed { get; } = new()
    {
        BuyQty = double.NaN,
        SellQty = double.NaN,
        BuyNotional = double.NaN,
        SellNotional = double.NaN,
        GrossQuote = double.NaN,
        BuyFeeQuote = double.NaN,
        SellFeeQuote = double.NaN,
        SlippageQuote = double.NaN,
        RebalanceQuote = double.NaN,
        ResidualBase = double.NaN,
        ResidualValueQuote = double.NaN,
        NetQuote = double.NaN,
        GrossEdgeBps = double.NaN,
        NetEdgeBps = double.NaN,
    };
}

/// <summary>
/// Net edge, unit-consistent and exact about WHERE each fee lands:
///   cash     = SellN − BuyN − fees paid in quote/discount-token
///   residual = BuyQty·(1 − f_buy·[fee in base]) − SellQty·(1 + f_sell·[fee in base])
///   net      = cash − slippage − rebalance + residual × mark
/// With equal qty and quote fees this reduces to the textbook
///   Q·(VWAPbid_B − VWAPask_A) − Fee_A − Fee_B − Slippage − Rebalance.
/// A base-asset fee is not double counted: it shows up once, as negative residual.
/// </summary>
public static class NetEdgeCalculator
{
    public static EdgeBreakdown Compute(in EdgeInputs x)
    {
        double buyN = x.BuyQty * x.BuyVwap;
        double sellN = x.SellQty * x.SellVwap;
        double feeBuy = buyN * x.BuyFee.Rate;
        double feeSell = sellN * x.SellFee.Rate;
        bool buyBase = x.BuyFee.Asset == FeeAssetKind.Base;
        bool sellBase = x.SellFee.Asset == FeeAssetKind.Base;

        double residual = (x.BuyQty * (buyBase ? 1 - x.BuyFee.Rate : 1)) - (x.SellQty * (sellBase ? 1 + x.SellFee.Rate : 1));
        double cash = sellN - buyN - (buyBase ? 0 : feeBuy) - (sellBase ? 0 : feeSell);
        double slip = (buyN * x.BuySlipBps * 1e-4) + (sellN * x.SellSlipBps * 1e-4);
        double rebalance = x.SellQty * x.RebalancePerUnitQuote;
        double residualValue = residual * x.ResidualMarkPx;
        double net = cash - slip - rebalance + residualValue;
        double matchedBuyNotional = x.SellQty * x.BuyVwap;

        return new EdgeBreakdown
        {
            BuyQty = x.BuyQty,
            SellQty = x.SellQty,
            BuyNotional = buyN,
            SellNotional = sellN,
            GrossQuote = x.SellQty * (x.SellVwap - x.BuyVwap),
            BuyFeeQuote = feeBuy,
            SellFeeQuote = feeSell,
            SlippageQuote = slip,
            RebalanceQuote = rebalance,
            ResidualBase = residual,
            ResidualValueQuote = residualValue,
            NetQuote = net,
            GrossEdgeBps = x.BuyVwap > 0 ? (x.SellVwap - x.BuyVwap) / x.BuyVwap * 1e4 : 0,
            NetEdgeBps = matchedBuyNotional > 0 ? net / matchedBuyNotional * 1e4 : 0,
        };
    }
}
