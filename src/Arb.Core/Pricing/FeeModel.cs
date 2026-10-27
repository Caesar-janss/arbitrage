using Arb.Core.Instruments;
using Arb.Core.Primitives;

namespace Arb.Core.Pricing;

/// <summary>Which asset a fee is charged in. Matters for delta: a Base fee reduces the coins you receive.</summary>
public enum FeeAssetKind : byte
{
    Quote = 1,
    Base = 2,
    DiscountToken = 3,
}

/// <summary>
/// Immutable fee tier of one instrument (VIP level, maker/taker, exchange-token discount such as BNB/BGB).
/// Reference type on purpose: a refresh swaps the reference atomically → readers never see a torn tier.
/// </summary>
public sealed class FeeTier
{
    public FeeTier(double makerBps, double takerBps, double discountPct = 0, AssetId discountToken = default, bool payWithDiscountToken = false)
    {
        // taker in [0, 100] bps; maker ≤ taker, rebates down to −10 bps.
        if (double.IsNaN(makerBps) || double.IsNaN(takerBps) || takerBps < 0 || takerBps > 100 || makerBps > takerBps || makerBps < -10)
        {
            throw new ArgumentOutOfRangeException(nameof(takerBps), "invalid maker/taker bps");
        }

        if (discountPct < 0 || discountPct >= 100)
        {
            throw new ArgumentOutOfRangeException(nameof(discountPct), discountPct, "0 <= discount < 100");
        }

        if (payWithDiscountToken && !discountToken.IsValid)
        {
            throw new ArgumentException("discount token required when paying with it", nameof(discountToken));
        }

        MakerRate = makerBps * 1e-4;
        TakerRate = takerBps * 1e-4;
        DiscountFactor = 1 - (discountPct / 100);
        DiscountToken = discountToken;
        PayWithDiscountToken = payWithDiscountToken;
    }

    public double MakerRate { get; }

    public double TakerRate { get; }

    public double DiscountFactor { get; }

    public AssetId DiscountToken { get; }

    public bool PayWithDiscountToken { get; }
}

public readonly struct ResolvedFee
{
    public ResolvedFee(double rate, FeeAssetKind asset)
    {
        Rate = rate;
        Asset = asset;
    }

    /// <summary>Fraction of notional (0.00075 = 7.5 bps). Negative = maker rebate.</summary>
    public double Rate { get; }

    public FeeAssetKind Asset { get; }

    public double RateBps => Rate * 1e4;
}

/// <summary>
/// Per-instrument fee resolution. Values come from the venue fee-rate API (Engine refresh); these are
/// EXPECTED fees for edge math — realized fees always come from fills (DB trd.fill is the source of truth).
/// </summary>
public sealed class FeeModel
{
    private readonly InstrumentRegistry _registry;
    private readonly FeeTier?[] _tiers;

    public FeeModel(InstrumentRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _tiers = new FeeTier?[registry.Count];
    }

    public void Set(InstrumentKey key, FeeTier tier)
    {
        ArgumentNullException.ThrowIfNull(tier);
        _ = _registry[key];
        Volatile.Write(ref _tiers[key.Index], tier);
    }

    public bool IsConfigured(InstrumentKey key) => key.Index < _tiers.Length && Volatile.Read(ref _tiers[key.Index]) is not null;

    public FeeTier Tier(InstrumentKey key) =>
        Volatile.Read(ref _tiers[key.Index]) ?? throw new InvalidOperationException($"fee tier not configured for {key}");

    /// <summary>
    /// Effective fee. Token discount only applies when enabled AND the token balance is available
    /// (otherwise venues silently fall back to the full rate in the received asset).
    /// Spot: fee in the RECEIVED asset (buy → base, sell → quote). Perp: settle asset (quote).
    /// </summary>
    public ResolvedFee Resolve(InstrumentKey key, Side side, bool isMaker, bool discountTokenAvailable)
    {
        FeeTier tier = Tier(key);
        double rate = isMaker ? tier.MakerRate : tier.TakerRate;
        if (tier.PayWithDiscountToken && discountTokenAvailable)
        {
            return new ResolvedFee(rate * tier.DiscountFactor, FeeAssetKind.DiscountToken);
        }

        if (key.Market == MarketType.Spot)
        {
            return new ResolvedFee(rate, side == Side.Buy ? FeeAssetKind.Base : FeeAssetKind.Quote);
        }

        return new ResolvedFee(rate, FeeAssetKind.Quote);
    }
}
