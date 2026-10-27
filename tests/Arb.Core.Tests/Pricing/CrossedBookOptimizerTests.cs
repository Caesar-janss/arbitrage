using Arb.Core.Instruments;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Pricing;

public class CrossedBookOptimizerTests
{
    private const int NoHaircut = VwapWalker.FullLiquidityPpm;
    private readonly TestMarkets _m = TestMarkets.Create();

    private static CrossLimits Unbounded(int haircut = NoHaircut) => new(double.PositiveInfinity, double.PositiveInfinity, haircut, 100);

    [Fact]
    public void Btc_StopsWhereMarginalEdgeCrossesZero()
    {
        // Same books as db/tests opportunity 501 (Binance buy, Bybit sell)
        PriceLevel[] asks = TestMarkets.Levels(_m.BinanceBtcSpot, (59995m, 0.006m), (60007.50m, 0.010m));
        PriceLevel[] bids = TestMarkets.Levels(_m.BybitBtcSpot, (60150m, 0.010m), (60100m, 0.020m));
        var costs = new CrossCosts(0.00075, 0.001, 0, 0, 2.0, 0);
        CrossResult r = CrossedBookOptimizer.Optimize(asks, _m.BinanceBtcSpot, bids, _m.BybitBtcSpot, costs, Unbounded());

        Assert.Equal(CrossStop.MarginalEdge, r.Stop);
        Assert.Equal(0.01, r.QtyCanon, 1e-12);
        Assert.Equal(0.4285, r.ExpectedNetQuote, 1e-9);
        Assert.Equal(2, r.BuyLevels);
        Assert.Equal(1, r.SellLevels);
        Assert.Equal(-14.605625, r.LastMarginalNetPerUnit, 1e-6);
    }

    [Fact]
    public void ThousandPepePerp_vs_PepeSpot_MultiplierNormalized()
    {
        PriceLevel[] asks = TestMarkets.Levels(_m.BybitPepePerp, (0.0120000m, 1000m), (0.0120100m, 2000m));
        PriceLevel[] bids = TestMarkets.Levels(_m.BitgetPepeSpot, (0.00001215m, 1_500_000m), (0.00001200m, 3_000_000m));
        var costs = new CrossCosts(0.00055, 0.0008, 0, 0, 0, 0);
        CrossResult r = CrossedBookOptimizer.Optimize(asks, _m.BybitPepePerp, bids, _m.BitgetPepeSpot, costs, Unbounded());

        Assert.Equal(CrossStop.MarginalEdge, r.Stop);
        Assert.Equal(1_500_000, r.QtyCanon, 1e-6);
        Assert.Equal(0.19551725, r.ExpectedNetQuote, 1e-12);
        Assert.Equal(18.005, r.BuyNotional, 1e-12);
        Assert.Equal(18.225, r.SellNotional, 1e-12);
    }

    [Fact]
    public void NoCross_WhenFeesEatTheSpread()
    {
        PriceLevel[] asks = TestMarkets.Levels(_m.BinanceBtcSpot, (60000m, 1m));
        PriceLevel[] bids = TestMarkets.Levels(_m.BybitBtcSpot, (60010m, 1m)); // 1.67 bps gross < 17.5 bps fees
        CrossResult r = CrossedBookOptimizer.Optimize(asks, _m.BinanceBtcSpot, bids, _m.BybitBtcSpot, new CrossCosts(0.00075, 0.001, 0, 0, 0, 0), Unbounded());
        Assert.Equal(CrossStop.NoCross, r.Stop);
        Assert.Equal(0, r.QtyCanon);
    }

    [Fact]
    public void Hurdle_ExcludesThinMarginalUnits()
    {
        PriceLevel[] asks = TestMarkets.Levels(_m.BinanceBtcSpot, (59995m, 0.006m), (60007.50m, 0.010m));
        PriceLevel[] bids = TestMarkets.Levels(_m.BybitBtcSpot, (60150m, 0.010m), (60100m, 0.020m));
        // second chunk earns 35.34/60052.5 ≈ 5.9 bps; a 7 bps hurdle keeps only the first 0.006
        var costs = new CrossCosts(0.00075, 0.001, 0, 0, 2.0, 7.0);
        CrossResult r = CrossedBookOptimizer.Optimize(asks, _m.BinanceBtcSpot, bids, _m.BybitBtcSpot, costs, Unbounded());
        Assert.Equal(0.006, r.QtyCanon, 1e-12);
        Assert.Equal(0.006 * 47.85375, r.ExpectedNetQuote, 1e-9);
    }

    [Fact]
    public void Caps_AreHonoured()
    {
        PriceLevel[] asks = TestMarkets.Levels(_m.BinanceBtcSpot, (59995m, 0.006m), (60007.50m, 0.010m));
        PriceLevel[] bids = TestMarkets.Levels(_m.BybitBtcSpot, (60150m, 0.010m), (60100m, 0.020m));
        var costs = new CrossCosts(0.00075, 0.001, 0, 0, 2.0, 0);

        CrossResult q = CrossedBookOptimizer.Optimize(asks, _m.BinanceBtcSpot, bids, _m.BybitBtcSpot, costs, new CrossLimits(0.004, double.PositiveInfinity, NoHaircut, 100));
        Assert.Equal(CrossStop.QtyCap, q.Stop);
        Assert.Equal(0.004, q.QtyCanon, 1e-12);

        CrossResult n = CrossedBookOptimizer.Optimize(asks, _m.BinanceBtcSpot, bids, _m.BybitBtcSpot, costs, new CrossLimits(double.PositiveInfinity, 300, NoHaircut, 100));
        Assert.Equal(CrossStop.NotionalCap, n.Stop);
        Assert.Equal(300, n.BuyNotional, 1e-9);

        CrossResult lv = CrossedBookOptimizer.Optimize(asks, _m.BinanceBtcSpot, bids, _m.BybitBtcSpot, costs, new CrossLimits(double.PositiveInfinity, double.PositiveInfinity, NoHaircut, 1));
        Assert.Equal(CrossStop.LevelCap, lv.Stop);
        Assert.Equal(0.006, lv.QtyCanon, 1e-12);

        CrossResult h = CrossedBookOptimizer.Optimize(asks, _m.BinanceBtcSpot, bids, _m.BybitBtcSpot, costs, Unbounded(500_000));
        Assert.Equal(0.005, h.QtyCanon, 1e-12); // bid level 0.010 × 50% = 0.005
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    public void MatchesBruteForceArgmax_OnRandomBooks(int seed)
    {
        var rng = new Random(seed);
        InstrumentSpec buy = _m.BinanceBtcSpot;
        InstrumentSpec sell = _m.BybitBtcSpot;
        for (int trial = 0; trial < 300; trial++)
        {
            PriceLevel[] asks = RandomSide(rng, 6_000_000 + rng.Next(0, 2_000), +1, 1, 2_000);
            PriceLevel[] bids = RandomSide(rng, asks[0].PxTicks + rng.Next(-3_000, 3_000), -1, 1, 20_000);
            var costs = new CrossCosts(0.00075, 0.001, rng.NextDouble(), rng.NextDouble(), rng.NextDouble() * 5, 0);

            CrossResult r = CrossedBookOptimizer.Optimize(asks, buy, bids, sell, costs, Unbounded());
            double best = BruteForceMaxNet(asks, buy, bids, sell, costs);
            Assert.Equal(best, r.ExpectedNetQuote, Math.Max(1e-9, Math.Abs(best) * 1e-9));
            Assert.True(r.ExpectedNetQuote >= 0);
        }
    }

    private static PriceLevel[] RandomSide(Random rng, long top, int direction, int minQty, int maxQty)
    {
        var levels = new PriceLevel[rng.Next(1, 15)];
        long px = top;
        for (int i = 0; i < levels.Length; i++)
        {
            levels[i] = new PriceLevel(px, rng.Next(minQty, maxQty));
            px += direction * rng.Next(1, 500);
        }

        return levels;
    }

    /// <summary>Independent reference: evaluate NetProfit(Q) at every breakpoint (piecewise-linear ⇒ max is at one).</summary>
    private static double BruteForceMaxNet(PriceLevel[] asks, InstrumentSpec buy, PriceLevel[] bids, InstrumentSpec sell, CrossCosts c)
    {
        var points = new List<double> { 0 };
        double cum = 0;
        foreach (PriceLevel l in asks)
        {
            cum += buy.CanonicalQty(l.QtyLots);
            points.Add(cum);
        }

        double maxQ = cum;
        cum = 0;
        foreach (PriceLevel l in bids)
        {
            cum += sell.CanonicalQty(l.QtyLots);
            points.Add(cum);
        }

        maxQ = Math.Min(maxQ, cum);
        double best = 0;
        foreach (double q in points)
        {
            if (q > maxQ)
            {
                continue;
            }

            double net = (Consume(bids, sell, q) * c.SellFactor) - (Consume(asks, buy, q) * c.BuyFactor) - (q * c.PerUnitQuote);
            best = Math.Max(best, net);
        }

        return best;
    }

    private static double Consume(PriceLevel[] side, InstrumentSpec spec, double q)
    {
        double left = q;
        double notional = 0;
        foreach (PriceLevel l in side)
        {
            double take = Math.Min(left, spec.CanonicalQty(l.QtyLots));
            notional += take * spec.CanonicalPrice(l.PxTicks);
            left -= take;
            if (left <= 0)
            {
                break;
            }
        }

        return notional;
    }
}
