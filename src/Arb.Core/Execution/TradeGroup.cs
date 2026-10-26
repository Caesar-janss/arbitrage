using Arb.Core.Pricing;
using Arb.Core.Primitives;

namespace Arb.Core.Execution;

/// <summary>
/// Aggregate of one arbitrage decision: state machine + exact canonical exposure from fills.
/// Owned by the executor thread; pooled (call <see cref="Initialize"/> to reuse, no per-trade allocation).
/// For funding strategies "Buy" = long leg, "Sell" = short leg.
/// </summary>
public sealed class TradeGroup
{
    private TradeStateRecord _state;

    public long TradeId { get; private set; }

    public StrategyKind Strategy { get; private set; }

    public InstrumentKey BuyInstrument { get; private set; }

    public InstrumentKey SellInstrument { get; private set; }

    public decimal TargetQtyCanon { get; private set; }

    public TradeStateRecord State => _state;

    public decimal BoughtCanon { get; private set; }

    public decimal SoldCanon { get; private set; }

    /// <summary>Fees charged in the base asset (reduce coin holdings).</summary>
    public decimal BaseFeeCanon { get; private set; }

    public decimal BuyNotional { get; private set; }

    public decimal SellNotional { get; private set; }

    /// <summary>Fees charged in quote or discount token, valued in quote.</summary>
    public decimal NonBaseFeesQuote { get; private set; }

    /// <summary>Signed canonical base exposure: + long, − short. Zero = flat (delta-neutral).</summary>
    public decimal NetBaseDelta => BoughtCanon - SoldCanon - BaseFeeCanon;

    public decimal CashFlowQuote => SellNotional - BuyNotional - NonBaseFeesQuote;

    public void Initialize(long tradeId, StrategyKind strategy, InstrumentKey buy, InstrumentKey sell, decimal targetQtyCanon, long createdAtMicros)
    {
        if (tradeId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tradeId), tradeId, "must be > 0");
        }

        if (buy == sell)
        {
            throw new ArgumentException("legs must use different instruments");
        }

        if (targetQtyCanon <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(targetQtyCanon), targetQtyCanon, "must be > 0");
        }

        TradeId = tradeId;
        Strategy = strategy;
        BuyInstrument = buy;
        SellInstrument = sell;
        TargetQtyCanon = targetQtyCanon;
        _state = TradeStateRecord.Create(createdAtMicros);
        BoughtCanon = 0m;
        SoldCanon = 0m;
        BaseFeeCanon = 0m;
        BuyNotional = 0m;
        SellNotional = 0m;
        NonBaseFeesQuote = 0m;
    }

    /// <summary>Apply one fill (any leg role: entry, repair, exit). Quantities canonical, notional in quote.</summary>
    public void ApplyFill(Side side, decimal qtyCanon, decimal notionalQuote, decimal feeAmount, FeeAssetKind feeAsset, decimal feeQuoteValue)
    {
        if (qtyCanon <= 0m || notionalQuote <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(qtyCanon), "fill qty and notional must be > 0");
        }

        if (side == Side.Buy)
        {
            BoughtCanon += qtyCanon;
            BuyNotional += notionalQuote;
        }
        else
        {
            SoldCanon += qtyCanon;
            SellNotional += notionalQuote;
        }

        if (feeAsset == FeeAssetKind.Base)
        {
            BaseFeeCanon += feeAmount;
        }
        else
        {
            NonBaseFeesQuote += feeQuoteValue;
        }
    }

    public TransitionError TransitionTo(TradeState to, long atMicros) =>
        TradeStateMachine.TryTransition(ref _state, to, Strategy, atMicros);

    public bool IsFlat(decimal dustCanon) => Math.Abs(NetBaseDelta) <= dustCanon;
}
