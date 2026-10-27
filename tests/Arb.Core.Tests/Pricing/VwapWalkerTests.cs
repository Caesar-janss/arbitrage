using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Pricing;

public class VwapWalkerTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    [Fact]
    public void WalksMultipleLevels_ExactVwap_MarginalLimit()
    {
        // Binance BTC asks: 0.006 @ 59995, 0.010 @ 60007.50 → buy 0.01 BTC
        PriceLevel[] asks = TestMarkets.Levels(_m.BinanceBtcSpot, (59995m, 0.006m), (60007.50m, 0.010m));
        VwapFill f = VwapWalker.WalkLots(asks, 1000);
        Assert.True(f.IsComplete);
        Assert.Equal(1000L, f.FilledLots);
        Assert.Equal(2, f.LevelsUsed);
        Assert.Equal(5999500L, f.BestPxTicks);
        Assert.Equal(6000750L, f.WorstPxTicks);
        Assert.Equal(600.00m, f.NotionalQuoteExact(_m.BinanceBtcSpot));
        Assert.Equal(60000.0, f.CanonicalVwap(_m.BinanceBtcSpot), 1e-9);
    }

    [Fact]
    public void PartialDepth_FillsWhatExists_AndReportsIncomplete()
    {
        PriceLevel[] bids = TestMarkets.Levels(_m.BybitBtcSpot, (60150m, 0.004m), (60100m, 0.002m));
        VwapFill f = VwapWalker.WalkLots(bids, 10_000); // want 0.01 BTC, only 0.006 visible
        Assert.False(f.IsComplete);
        Assert.Equal(6_000L, f.FilledLots);
        Assert.Equal(2, f.LevelsUsed);
        Assert.Equal(360.80m, f.NotionalQuoteExact(_m.BybitBtcSpot));
    }

    [Fact]
    public void TopOfBookIllusion_VwapWorseThanTob()
    {
        PriceLevel[] asks = TestMarkets.Levels(_m.BinanceBtcSpot, (60000m, 0.001m), (60030m, 0.004m), (60090m, 0.02m));
        VwapFill f = VwapWalker.WalkLots(asks, 1000); // 0.01 BTC
        double vwap = f.CanonicalVwap(_m.BinanceBtcSpot);
        // (0.001×60000 + 0.004×60030 + 0.005×60090) / 0.01 = 60057
        Assert.Equal(60057.0, vwap, 1e-9);
        Assert.Equal(3, f.LevelsUsed);
        Assert.True(vwap - 60000.0 > 50, "TOB understates the real cost by > 50 USDT/BTC");
    }

    [Fact]
    public void Haircut_TakesOnlyFractionOfDisplayedSize()
    {
        PriceLevel[] asks = TestMarkets.Levels(_m.BinanceBtcSpot, (60000m, 0.010m), (60010m, 0.010m));
        VwapFill f = VwapWalker.WalkLots(asks, 1000, haircutPpm: 500_000);
        Assert.Equal(1000L, f.FilledLots);
        Assert.Equal(2, f.LevelsUsed);
        Assert.Equal(60005.0, f.CanonicalVwap(_m.BinanceBtcSpot), 1e-9);
        Assert.Throws<ArgumentOutOfRangeException>(() => VwapWalker.WalkLots(asks, 1, 0));
    }

    [Fact]
    public void WithinLimit_StopsAtLimitPrice()
    {
        PriceLevel[] bids = TestMarkets.Levels(_m.BybitBtcSpot, (60100m, 0.002m), (60050m, 0.003m), (59900m, 1m));
        VwapFill f = VwapWalker.WalkLotsWithinLimit(bids, 10_000, 6005000, Side.Sell);
        Assert.Equal(5_000L, f.FilledLots);
        Assert.Equal(6005000L, f.WorstPxTicks);
    }

    [Fact]
    public void QuoteBudget_FloorsPartialLevel_NeverOverspends()
    {
        PriceLevel[] asks = TestMarkets.Levels(_m.BinanceBtcSpot, (60000m, 0.005m), (60100m, 0.010m));
        Int128 budget = VwapWalker.BudgetUnits(_m.BinanceBtcSpot, 500m);
        VwapFill f = VwapWalker.WalkQuote(asks, budget);
        decimal spent = f.NotionalQuoteExact(_m.BinanceBtcSpot);
        Assert.True(spent <= 500m);
        // 300 at 60000 (0.005), then floor(200/60100 / 1e-5) = 332 lots → 199.5320
        Assert.Equal(500L + 332L, f.FilledLots);
        Assert.Equal(499.532m, spent);
    }

    [Fact]
    public void EmptyBook_OrZeroTarget()
    {
        VwapFill empty = VwapWalker.WalkLots(ReadOnlySpan<PriceLevel>.Empty, 100);
        Assert.False(empty.IsComplete);
        Assert.Equal(0L, empty.FilledLots);
        Assert.True(double.IsNaN(empty.VwapTicks));
        Assert.False(VwapWalker.WalkLots(TestMarkets.Levels(_m.BinanceBtcSpot, (1m, 1m)), 0).IsComplete);
    }

    [Fact]
    public void ThousandPepe_CanonicalVwap()
    {
        // Bybit 1000PEPE asks: 1000 contracts @ 0.0120000, 2000 @ 0.0120100 → buy 15 lots (1,500,000 PEPE)
        PriceLevel[] asks = TestMarkets.Levels(_m.BybitPepePerp, (0.0120000m, 1000m), (0.0120100m, 2000m));
        VwapFill f = VwapWalker.WalkLots(asks, 15);
        Assert.Equal(18.005m, f.NotionalQuoteExact(_m.BybitPepePerp));
        Assert.Equal(18.005 / 1_500_000, f.CanonicalVwap(_m.BybitPepePerp), 1e-18);
    }
}
