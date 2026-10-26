using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Pricing;

public class FeeAndSlippageTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    [Fact]
    public void Spot_FeeLandsInReceivedAsset_TokenDiscountOnlyWithBalance()
    {
        var fees = new FeeModel(_m.Registry);
        fees.Set(_m.BinanceBtcSpot.Key, new FeeTier(10, 10, discountPct: 25, discountToken: _m.Bnb, payWithDiscountToken: true));

        ResolvedFee withBnb = fees.Resolve(_m.BinanceBtcSpot.Key, Side.Buy, isMaker: false, discountTokenAvailable: true);
        Assert.Equal(7.5, withBnb.RateBps, 1e-9);
        Assert.Equal(FeeAssetKind.DiscountToken, withBnb.Asset);

        ResolvedFee noBnbBuy = fees.Resolve(_m.BinanceBtcSpot.Key, Side.Buy, false, discountTokenAvailable: false);
        Assert.Equal(10.0, noBnbBuy.RateBps, 1e-9);
        Assert.Equal(FeeAssetKind.Base, noBnbBuy.Asset);

        ResolvedFee noBnbSell = fees.Resolve(_m.BinanceBtcSpot.Key, Side.Sell, false, false);
        Assert.Equal(FeeAssetKind.Quote, noBnbSell.Asset);
    }

    [Fact]
    public void Perp_FeeInSettleAsset_MakerRebateAllowed()
    {
        var fees = new FeeModel(_m.Registry);
        fees.Set(_m.BinancePepePerp.Key, new FeeTier(2, 5, discountPct: 10, discountToken: _m.Bnb, payWithDiscountToken: true));
        fees.Set(_m.BybitPepePerp.Key, new FeeTier(-0.5, 5.5));

        Assert.Equal(1.8, fees.Resolve(_m.BinancePepePerp.Key, Side.Sell, true, true).RateBps, 1e-9);
        Assert.Equal(5.0, fees.Resolve(_m.BinancePepePerp.Key, Side.Sell, false, false).RateBps, 1e-9);
        ResolvedFee rebate = fees.Resolve(_m.BybitPepePerp.Key, Side.Buy, true, false);
        Assert.Equal(-0.5, rebate.RateBps, 1e-9);
        Assert.Equal(FeeAssetKind.Quote, rebate.Asset);
    }

    [Fact]
    public void Unconfigured_Throws_InvalidTierRejected()
    {
        var fees = new FeeModel(_m.Registry);
        Assert.False(fees.IsConfigured(_m.BybitBtcSpot.Key));
        Assert.Throws<InvalidOperationException>(() => fees.Resolve(_m.BybitBtcSpot.Key, Side.Buy, false, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FeeTier(12, 10));
        Assert.Throws<ArgumentException>(() => new FeeTier(1, 2, 10, default, payWithDiscountToken: true));
    }

    [Fact]
    public void EwmaVolatility_IgnoresSubSampleNoise_AndColdStartUsesFallback()
    {
        var vol = new EwmaVolatility(halfLifeMs: 1_000, minSampleMs: 10);
        vol.Update(100, 0);
        vol.Update(101, 1_000); // 1 ms later → ignored (bid/ask bounce)
        Assert.Equal(0, vol.Samples);
        vol.Update(101, 20_000);
        Assert.Equal(1, vol.Samples);
        Assert.True(vol.VarPerMs > 0);

        var model = new SlippageModel(0.5, 2, 20, coldStartBps: 8, minWarmSamples: 50);
        Assert.Equal(8.0, model.LegBps(vol, 10), 1e-12);
    }

    [Fact]
    public void Slippage_IsCapped()
    {
        var vol = new EwmaVolatility(1_000, 1, initialVarPerMs: 1e-4);
        var model = new SlippageModel(0.5, 2, 20, 8, 0);
        Assert.Equal(20.0, model.LegBps(vol, 1_000), 1e-12);
    }
}
