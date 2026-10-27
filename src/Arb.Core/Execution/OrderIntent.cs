using Arb.Core.Primitives;

namespace Arb.Core.Execution;

/// <summary>Venue-agnostic order to send (exact venue units). The gateway serializes it; nothing here allocates.</summary>
public readonly struct OrderIntent
{
    public OrderIntent(
        InstrumentKey instrument,
        Side side,
        OrderType type,
        TimeInForce timeInForce,
        long lots,
        long limitPxTicks,
        long clientOrderId,
        bool reduceOnly)
    {
        if (lots <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lots), lots, "must be > 0");
        }

        if (type == OrderType.Limit && limitPxTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limitPxTicks), limitPxTicks, "limit order needs a price");
        }

        Instrument = instrument;
        Side = side;
        Type = type;
        TimeInForce = timeInForce;
        Lots = lots;
        LimitPxTicks = limitPxTicks;
        ClientOrderId = clientOrderId;
        ReduceOnly = reduceOnly;
    }

    public InstrumentKey Instrument { get; }

    public Side Side { get; }

    public OrderType Type { get; }

    public TimeInForce TimeInForce { get; }

    public long Lots { get; }

    public long LimitPxTicks { get; }

    public long ClientOrderId { get; }

    public bool ReduceOnly { get; }

    public bool IsValid => Lots > 0;
}
