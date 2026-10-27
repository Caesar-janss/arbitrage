namespace Arb.Core.Primitives;

/// <summary>Trading venue. Numeric values are persisted/serialized: never renumber.</summary>
public enum Venue : byte
{
    Unknown = 0,
    Binance = 1,
    Bybit = 2,
    Bitget = 3,
}

public enum MarketType : byte
{
    Unknown = 0,
    Spot = 1,
    PerpLinear = 2,
}

public enum Side : byte
{
    Buy = 1,
    Sell = 2,
}

public enum TimeInForce : byte
{
    Ioc = 1,
    Fok = 2,
    Gtc = 3,
    PostOnly = 4,
}

public enum OrderType : byte
{
    Limit = 1,
    Market = 2,
}

/// <summary>Wallet/account an instrument settles into (Binance spot vs futures are separate; Bybit UTA is unified).</summary>
public enum AccountKind : byte
{
    Spot = 1,
    Futures = 2,
    Unified = 3,
}

public enum StrategyKind : byte
{
    Spatial = 1,
    FundingPerpPerp = 2,
    FundingSpotPerp = 3,
}

/// <summary>Grid snapping direction: Down = toward −∞, Up = toward +∞.</summary>
public enum Rounding : byte
{
    Down = 0,
    Up = 1,
}

public static class PrimitiveExtensions
{
    public static Side Opposite(this Side side) => side switch
    {
        Side.Buy => Side.Sell,
        Side.Sell => Side.Buy,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, null),
    };

    /// <summary>+1 for Buy (adds base exposure), −1 for Sell.</summary>
    public static int Sign(this Side side) => side == Side.Buy ? 1 : -1;

    public static bool IsFunding(this StrategyKind strategy) =>
        strategy is StrategyKind.FundingPerpPerp or StrategyKind.FundingSpotPerp;

    /// <summary>Code used by PostgreSQL schema (trd.*.strategy).</summary>
    public static string ToDbCode(this StrategyKind strategy) => strategy switch
    {
        StrategyKind.Spatial => "SPATIAL",
        StrategyKind.FundingPerpPerp => "FUNDING_PERP_PERP",
        StrategyKind.FundingSpotPerp => "FUNDING_SPOT_PERP",
        _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null),
    };

    /// <summary>Code used by PostgreSQL schema (ref.exchange.exchange_code).</summary>
    public static string ToDbCode(this Venue venue) => venue switch
    {
        Venue.Binance => "BINANCE",
        Venue.Bybit => "BYBIT",
        Venue.Bitget => "BITGET",
        _ => throw new ArgumentOutOfRangeException(nameof(venue), venue, null),
    };

    /// <summary>Code used by PostgreSQL schema (ref.instrument.market_type).</summary>
    public static string ToDbCode(this MarketType market) => market switch
    {
        MarketType.Spot => "SPOT",
        MarketType.PerpLinear => "PERP_LINEAR",
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
    };

    /// <summary>Code used by PostgreSQL schema (trd.order_leg.side, trd.fill.side).</summary>
    public static string ToDbCode(this Side side) => side switch
    {
        Side.Buy => "BUY",
        Side.Sell => "SELL",
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, null),
    };

    /// <summary>Code used by PostgreSQL schema (trd.order_leg.order_type).</summary>
    public static string ToDbCode(this OrderType type) => type switch
    {
        OrderType.Limit => "LIMIT",
        OrderType.Market => "MARKET",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Code used by PostgreSQL schema (trd.order_leg.time_in_force).</summary>
    public static string ToDbCode(this TimeInForce tif) => tif switch
    {
        TimeInForce.Ioc => "IOC",
        TimeInForce.Fok => "FOK",
        TimeInForce.Gtc => "GTC",
        TimeInForce.PostOnly => "POST_ONLY",
        _ => throw new ArgumentOutOfRangeException(nameof(tif), tif, null),
    };
}
