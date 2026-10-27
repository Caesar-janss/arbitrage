using Arb.Core.Pricing;
using Arb.Core.Primitives;

namespace Arb.Core.Execution;

/// <summary>
/// Aggregate of one arbitrage decision: state machine + exact canonical exposure from fills.
/// Owned by the executor thread; pooled (call <see cref="Initialize"/> to reuse, no per-trade allocation).
/// For funding strategies "Buy" = long leg, "Sell" = short leg.
///
/// Exposure has two parts; a terminal state (COMPLETED / REPAIRED / CLOSED) needs both accounted for:
///   * net delta  = Σ base bought − Σ base sold − base-asset fees (all instruments of the trade). It must equal the
///     residual EXPLICITLY accepted as dust (<see cref="AcceptResidualAsDust"/>: below every venue's minimum, or within
///     RepairPolicy.DustCanon) — usually 0. The accepted amount is persisted as trd.trade_group.dust_accepted_base and
///     the DB's is_flat uses the same rule, so C# and SQL share ONE definition of flat (R-14);
///   * gross perp = the signed position on EVERY perp instrument the trade traded (entry legs and hedges). A spatial
///     residual hedged on a perp has delta 0 but two open positions (R-02): it is POSITION_OPEN, not REPAIRED.
/// </summary>
public sealed class TradeGroup
{
    /// <summary>Distinct perp instruments one trade may touch (two funding legs + hedges).</summary>
    public const int MaxPerpInstruments = 4;

    private readonly InstrumentKey[] _perpKeys = new InstrumentKey[MaxPerpInstruments];
    private readonly decimal[] _perpNet = new decimal[MaxPerpInstruments];
    private int _perpCount;
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

    /// <summary>Signed residual written off as untradeable dust (0 = none). Persist as trd.trade_group.dust_accepted_base.</summary>
    public decimal DustAcceptedCanon { get; private set; }

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
        DustAcceptedCanon = 0m;
        _perpCount = 0;
        Array.Clear(_perpNet);
    }

    /// <summary>Signed open position (canonical base, + long) on a perp instrument of this trade; 0 if never traded.</summary>
    public decimal PerpPositionCanon(InstrumentKey perp)
    {
        for (int i = 0; i < _perpCount; i++)
        {
            if (_perpKeys[i] == perp)
            {
                return _perpNet[i];
            }
        }

        return 0m;
    }

    /// <summary>True while any perp instrument of the trade (entry leg or hedge) has a non-zero position.</summary>
    public bool HasOpenPerpPosition
    {
        get
        {
            for (int i = 0; i < _perpCount; i++)
            {
                if (_perpNet[i] != 0m)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Apply one fill (any leg role: entry, repair, exit). Quantities canonical, notional in quote.
    /// <paramref name="instrument"/> must be one of the two legs, or a PERP (hedge); a spot fill on any other
    /// instrument is a routing bug and throws.
    /// </summary>
    public void ApplyFill(InstrumentKey instrument, Side side, decimal qtyCanon, decimal notionalQuote, decimal feeAmount, FeeAssetKind feeAsset, decimal feeQuoteValue)
    {
        if (qtyCanon <= 0m || notionalQuote <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(qtyCanon), "fill qty and notional must be > 0");
        }

        if (!instrument.IsValid)
        {
            throw new ArgumentException("fill needs a valid instrument", nameof(instrument));
        }

        bool isLeg = instrument == BuyInstrument || instrument == SellInstrument;
        if (!isLeg && instrument.Market != MarketType.PerpLinear)
        {
            throw new ArgumentException($"spot fill on {instrument} does not belong to trade {TradeId}", nameof(instrument));
        }

        if (instrument.Market == MarketType.PerpLinear)
        {
            AddPerp(instrument, side == Side.Buy ? qtyCanon : -qtyCanon);
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

    /// <summary>
    /// State transition with exposure rules on top of the transition table: no terminal flat state while a perp
    /// position is open; a SPATIAL trade enters POSITION_OPEN only as a perp-hedged residual.
    /// </summary>
    public TransitionError TransitionTo(TradeState to, long atMicros)
    {
        if (_state.State != 0 && TradeStateMachine.IsAllowed(_state.State, to))
        {
            if (to is TradeState.Completed or TradeState.Repaired or TradeState.Closed)
            {
                if (HasOpenPerpPosition)
                {
                    return TransitionError.OpenDerivativePosition;
                }

                if (NetBaseDelta != DustAcceptedCanon)
                {
                    return TransitionError.UnacceptedResidual;
                }
            }

            if (Strategy == StrategyKind.Spatial && to == TradeState.PositionOpen && !HasOpenPerpPosition)
            {
                return TransitionError.NotHedged;
            }
        }

        return TradeStateMachine.TryTransition(ref _state, to, Strategy, atMicros);
    }

    /// <summary>|net delta| ≤ dust (funding POSITION_OPEN is delta-neutral but not flat).</summary>
    public bool IsDeltaNeutral(decimal dustCanon) => Math.Abs(NetBaseDelta) <= dustCanon;

    /// <summary>
    /// No exposure left: no open perp AND |net delta − accepted dust| ≤ <paramref name="toleranceCanon"/>. Terminal states
    /// require tolerance 0, i.e. the residual is either zero or explicitly accepted (<see cref="AcceptResidualAsDust"/>).
    /// </summary>
    public bool IsFlat(decimal toleranceCanon) => !HasOpenPerpPosition && Math.Abs(NetBaseDelta - DustAcceptedCanon) <= toleranceCanon;

    /// <summary>
    /// Write the CURRENT net delta off as dust (call only when RepairPlanner says Flat or BelowVenueMinimum, and the value is
    /// within the dust budget). Returns the accepted amount. A perp position is never dust.
    /// </summary>
    public decimal AcceptResidualAsDust()
    {
        if (HasOpenPerpPosition)
        {
            throw new InvalidOperationException($"trade {TradeId}: an open perp position cannot be accepted as dust");
        }

        DustAcceptedCanon = NetBaseDelta;
        return DustAcceptedCanon;
    }

    private void AddPerp(InstrumentKey perp, decimal signedQty)
    {
        for (int i = 0; i < _perpCount; i++)
        {
            if (_perpKeys[i] == perp)
            {
                _perpNet[i] += signedQty;
                return;
            }
        }

        if (_perpCount == MaxPerpInstruments)
        {
            throw new InvalidOperationException($"trade {TradeId}: more than {MaxPerpInstruments} perp instruments");
        }

        _perpKeys[_perpCount] = perp;
        _perpNet[_perpCount] = signedQty;
        _perpCount++;
    }
}
