using Arb.Core.Instruments;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-20 (S4) f: the v0.2 brute-force check of Q* used hurdle = 0, no caps and no haircut, so a bug in any of those
/// branches could not fail it. Independent reference here: Q* maximizes the hurdle-adjusted net
///   H(Q) = ∫₀^Q [m(q) − h·ask(q)] dq  over the haircut depth, s.t. Q ≤ qty cap, buy notional ≤ notional cap,
/// at most L levels per side. H is concave and piecewise linear → its max is at a breakpoint (level edge or cap).
/// </summary>
public class R20_OptimizerBruteForceTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    [Theory]
    [InlineData(11)]
    [InlineData(23)]
    [InlineData(37)]
    public void MatchesBruteForce_WithHurdleCapsHaircutAndLevelCap(int seed)
    {
        var rng = new Random(seed);
        InstrumentSpec buy = _m.BinanceBtcSpot;
        InstrumentSpec sell = _m.BybitBtcSpot;
        int crossed = 0, hurdleBound = 0, capBound = 0, haircutUsed = 0;
        for (int trial = 0; trial < 400; trial++)
        {
            PriceLevel[] asks = Side(rng, 6_000_000 + rng.Next(0, 2_000), +1, 1, 2_000);
            // crossed by up to ~50 bps so that fees (17.5 bps), slippage, hurdle and caps all bind in some trials
            PriceLevel[] bids = Side(rng, asks[0].PxTicks + rng.Next(-2_000, 30_000), -1, 1, 20_000);
            var costs = new CrossCosts(0.00075, 0.001, rng.NextDouble(), rng.NextDouble(), rng.NextDouble() * 5, rng.NextDouble() * 4);
            double qtyCap = rng.Next(3) == 0 ? double.PositiveInfinity : 0.001 + (rng.NextDouble() * 0.05);
            double notionalCap = rng.Next(3) == 0 ? double.PositiveInfinity : 50 + (rng.NextDouble() * 3_000);
            int haircut = rng.Next(2) == 0 ? VwapWalker.FullLiquidityPpm : rng.Next(100_000, 1_000_000);
            int levelCap = rng.Next(1, 16);
            var limits = new CrossLimits(qtyCap, notionalCap, haircut, levelCap);

            CrossResult r = CrossedBookOptimizer.Optimize(asks, buy, bids, sell, costs, limits);
            (double q, double net) = BruteForce(asks, buy, bids, sell, costs, limits);
            double tol = Math.Max(1e-9, Math.Abs(net) * 1e-9);
            Assert.True(Math.Abs(net - r.ExpectedNetQuote) <= tol, $"seed {seed} trial {trial}: net {r.ExpectedNetQuote} vs brute force {net}");
            Assert.True(Math.Abs(q - r.QtyCanon) <= Math.Max(1e-12, q * 1e-9), $"seed {seed} trial {trial}: Q {r.QtyCanon} vs brute force {q}");
            Assert.True(r.QtyCanon <= qtyCap * (1 + 1e-12) && r.BuyNotional <= notionalCap * (1 + 1e-12));
            crossed += r.QtyCanon > 0 ? 1 : 0;
            hurdleBound += r.Stop == CrossStop.MarginalEdge && costs.Hurdle > 0 ? 1 : 0;
            capBound += r.Stop is CrossStop.QtyCap or CrossStop.NotionalCap or CrossStop.LevelCap ? 1 : 0;
            haircutUsed += r.QtyCanon > 0 && haircut < VwapWalker.FullLiquidityPpm ? 1 : 0;
        }

        // the test must exercise every branch it claims to cover (R-20: the v0.2 version mostly priced no-cross books)
        Assert.True(crossed >= 100 && hurdleBound >= 10 && capBound >= 20 && haircutUsed >= 20, $"coverage crossed={crossed} hurdle={hurdleBound} caps={capBound} haircut={haircutUsed}");
    }

    private static PriceLevel[] Side(Random rng, long top, int direction, int minQty, int maxQty)
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

    private static (double Q, double Net) BruteForce(PriceLevel[] asks, InstrumentSpec buy, PriceLevel[] bids, InstrumentSpec sell, CrossCosts c, CrossLimits lim)
    {
        double hc = lim.HaircutPpm / (double)VwapWalker.FullLiquidityPpm;
        var a = asks.Take(lim.MaxLevelsPerSide).Select(l => (Px: buy.CanonicalPrice(l.PxTicks), Qty: buy.CanonicalQty(l.QtyLots) * hc)).ToArray();
        var b = bids.Take(lim.MaxLevelsPerSide).Select(l => (Px: sell.CanonicalPrice(l.PxTicks), Qty: sell.CanonicalQty(l.QtyLots) * hc)).ToArray();
        double maxQ = Math.Min(a.Sum(x => x.Qty), b.Sum(x => x.Qty));
        maxQ = Math.Min(maxQ, lim.MaxQtyCanon);
        maxQ = Math.Min(maxQ, QtyForBuyNotional(a, lim.MaxBuyNotional));

        var points = new SortedSet<double> { 0, maxQ };
        double cum = 0;
        foreach (var l in a) { cum += l.Qty; if (cum < maxQ) points.Add(cum); }
        cum = 0;
        foreach (var l in b) { cum += l.Qty; if (cum < maxQ) points.Add(cum); }

        double bestQ = 0, bestH = 0, bestNet = 0;
        foreach (double q in points)
        {
            double buyN = Consume(a, q), sellN = Consume(b, q);
            double net = (sellN * c.SellFactor) - (buyN * c.BuyFactor) - (q * c.PerUnitQuote);
            double h = net - (c.Hurdle * buyN);
            if (h > bestH + Math.Max(1e-12, Math.Abs(bestH) * 1e-12))
            {
                bestH = h;
                bestQ = q;
                bestNet = net;
            }
        }

        return (bestQ, bestNet);
    }

    private static double QtyForBuyNotional((double Px, double Qty)[] side, double cap)
    {
        if (double.IsPositiveInfinity(cap)) return double.PositiveInfinity;
        double q = 0, left = cap;
        foreach (var l in side)
        {
            if (l.Px * l.Qty >= left) return q + (left / l.Px);
            q += l.Qty;
            left -= l.Px * l.Qty;
        }

        return q;
    }

    private static double Consume((double Px, double Qty)[] side, double q)
    {
        double left = q, notional = 0;
        foreach (var l in side)
        {
            double take = Math.Min(left, l.Qty);
            notional += take * l.Px;
            left -= take;
            if (left <= 0) break;
        }

        return notional;
    }
}
