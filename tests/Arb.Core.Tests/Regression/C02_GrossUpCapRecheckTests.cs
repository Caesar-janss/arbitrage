using Arb.Core.Instruments;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// C-02 (S2, ChatGPT; pre-existing API). Q* respects MaxQtyCanon / MaxBuyNotional / MaxLevelsPerSide / haircut depth,
/// but the base-fee gross-up of the buy leg (and lot rounding) happens AFTER the optimizer, and the final order was never
/// checked against those caps again. Reviewer case: Bitget → Bybit BTC, lot 1e-6, cap 600 USDT, buy fee 0.1 % in base →
/// buy 0.010011 BTC for 600.66 USDT, decision Execute.
/// Contract after the fix: every Execute satisfies all caps on the FINAL legs; when the first sizing breaks one, the
/// pricer takes the largest number of common lots that fits, or rejects.
/// </summary>
public class C02_GrossUpCapRecheckTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    private static SpatialParams Params(double maxQty, double maxNotional, int haircutPpm = VwapWalker.FullLiquidityPpm, int maxLevels = 20) => new()
    {
        BuyFee = new ResolvedFee(0.001, FeeAssetKind.Base),
        SellFee = new ResolvedFee(0, FeeAssetKind.Quote),
        BuySlipBps = 0,
        SellSlipBps = 0,
        RebalancePerUnitQuote = 0,
        MinMarginalEdgeBps = 0,
        MinProfitQuote = 0.01,
        MaxQtyCanon = maxQty,
        MaxBuyNotional = maxNotional,
        HaircutPpm = haircutPpm,
        MaxLevelsPerSide = maxLevels,
    };

    private SpatialQuote ReviewerCase(SpatialParams p)
    {
        var ctx = new SpatialPairContext(_m.BitgetBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BitgetBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 1m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60150m, 1m) }, new[] { (60160m, 1m) });
        return SpatialArbPricer.Evaluate(ctx, buy, sell, p);
    }

    [Fact]
    public void ReviewerCase_FinalBuyNotional_StaysWithinTheCap()
    {
        SpatialQuote q = ReviewerCase(Params(0.01, 600));
        Assert.True(q.IsExecutable);
        Assert.True(q.BuyNotionalExact <= 600m, $"buy notional {q.BuyNotionalExact} > 600");   // v0.3: 600.66
        Assert.True(_m.BitgetBtcSpot.CanonicalQtyExact(q.BuyLots) <= 0.01m);                     // v0.3: 0.010011
        // largest common size that fits: deliver 0.00999, buy 0.01 (0.01 × 0.999 = 0.00999) for exactly 600
        Assert.Equal(10_000L, q.BuyLots);
        Assert.Equal(0.00999m, q.SellQtyExact);
        Assert.Equal(600m, q.BuyNotionalExact);
    }

    [Fact]
    public void NotionalCapAlone_AppliesToTheGrossedUpBuyLeg()
    {
        SpatialQuote q = ReviewerCase(Params(double.PositiveInfinity, 600));
        Assert.True(q.IsExecutable);
        Assert.True(q.BuyNotionalExact <= 600m, $"buy notional {q.BuyNotionalExact} > 600");   // v0.3: 600.66
        Assert.Equal(600m, q.BuyNotionalExact);
    }

    [Fact]
    public void QtyCapAlone_AppliesToTheGrossedUpBuyLeg()
    {
        SpatialQuote q = ReviewerCase(Params(0.01, double.PositiveInfinity));
        Assert.True(q.IsExecutable);
        Assert.True(_m.BitgetBtcSpot.CanonicalQtyExact(q.BuyLots) <= 0.01m);                     // v0.3: 0.010011
        Assert.True(q.SellQtyExact <= 0.01m);
    }

    [Fact]
    public void GrossUp_DoesNotWalkPastTheLevelCap()
    {
        // L1 holds exactly what Q* takes (0.01); the grossed-up 0.010011 would need L2, beyond MaxLevelsPerSide = 1.
        var ctx = new SpatialPairContext(_m.BitgetBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BitgetBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 0.01m), (60001m, 1m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60150m, 1m) }, new[] { (60160m, 1m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, Params(double.PositiveInfinity, double.PositiveInfinity, maxLevels: 1));
        Assert.True(q.IsExecutable);
        Assert.Equal(1, q.BuyLevels);                                                            // v0.3: 2
        Assert.True(q.BuyLots <= 10_000L);
    }

    [Fact]
    public void GrossUp_BeyondHaircutDepth_ShrinksInsteadOfRejecting()
    {
        // 50 % haircut of a 0.02 level = 0.01 usable; the grossed-up leg needs 0.010011 → v0.3 walk incomplete → reject.
        var ctx = new SpatialPairContext(_m.BitgetBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BitgetBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 0.02m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60150m, 1m) }, new[] { (60160m, 1m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, Params(double.PositiveInfinity, double.PositiveInfinity, haircutPpm: 500_000));
        Assert.Equal(RejectReason.Execute, q.Decision);                                          // v0.3: RejectThreshold
        Assert.True(q.BuyLots <= 10_000L);
    }

    /// <summary>
    /// Property over random crossed books, fee in base on the buy leg, random finite caps: every Execute satisfies every
    /// cap on its final legs, and its size is the largest common size that does (oracle: linear scan downward from the
    /// optimizer's floor, independent of the implementation's search).
    /// </summary>
    [Fact]
    public void RandomBooks_EveryExecuteRespectsTheCaps_AndIsMaximal()
    {
        InstrumentSpec a = _m.BitgetBtcSpot;
        InstrumentSpec b = _m.BybitBtcSpot;
        var ctx = new SpatialPairContext(a, b);
        var rng = new Random(20261102);
        int executes = 0;
        int capBindingAfterGrossUp = 0;
        int adversarial = 0;
        for (int iter = 0; iter < 4_000; iter++)
        {
            int nA = rng.Next(1, 5);
            int nB = rng.Next(1, 5);
            var asks = new (decimal, decimal)[nA];
            var bids = new (decimal, decimal)[nB];
            decimal px = 60_000m;
            for (int i = 0; i < nA; i++)
            {
                px += rng.Next(0, 4);
                asks[i] = (px, rng.Next(1, 3_000) / 100_000m);
            }

            px = 60_000m + rng.Next(0, 120);
            for (int i = 0; i < nB; i++)
            {
                bids[i] = (px, rng.Next(1, 3_000) / 100_000m);
                px -= rng.Next(0, 4);
            }

            BookView buy = TestMarkets.View(a, new[] { (59_990m, 1m) }, asks);
            BookView sell = TestMarkets.View(b, bids, new[] { (60_200m, 1m) });
            double maxQty = rng.Next(2) == 0 ? double.PositiveInfinity : rng.Next(1, 4_000) / 100_000.0;
            double maxNotional = rng.Next(2) == 0 ? double.PositiveInfinity : rng.Next(50, 2_000);
            int haircut = rng.Next(3) == 0 ? VwapWalker.FullLiquidityPpm : rng.Next(300_000, 1_000_000);
            int maxLevels = rng.Next(1, 5);
            SpatialParams p = Params(maxQty, maxNotional, haircut, maxLevels) with { MinProfitQuote = 0 };

            SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, p);
            if (!q.IsExecutable)
            {
                continue;
            }

            executes++;
            string where = $"iter {iter} maxQty {maxQty} maxNotional {maxNotional} haircut {haircut} levels {maxLevels}";
            Assert.True(Fits(ctx, buy, sell, p, q.SellLots / ctx.Grid.LotsPerUnitB, out _), $"{where}: Execute breaks a cap (buy {q.BuyLots} lots, notional {q.BuyNotionalExact}, levels {q.BuyLevels}/{q.SellLevels})");

            long first = ctx.Grid.UnitsFloor(q.OptimizerQtyCanon);
            long oracle = first;
            while (oracle > 0 && !Fits(ctx, buy, sell, p, oracle, out _))
            {
                oracle--;
            }

            if (oracle < first)
            {
                capBindingAfterGrossUp++;
            }

            Assert.Equal(oracle * ctx.Grid.LotsPerUnitB, q.SellLots);

            // Adversarial cap 5e-15 below a notional the pricer can reach: inside the fast path's 1e-12 band, so only the
            // exact decimal compare can tell that this size no longer fits.
            SpatialParams tight = p with { MaxBuyNotional = (double)q.BuyNotionalExact * (1 - 5e-15) };
            SpatialQuote t = SpatialArbPricer.Evaluate(ctx, buy, sell, tight);
            if (t.IsExecutable)
            {
                adversarial++;
                Assert.True(Fits(ctx, buy, sell, tight, t.SellLots / ctx.Grid.LotsPerUnitB, out _), $"{where}: cap {tight.MaxBuyNotional:R} broken by {t.BuyNotionalExact}");
            }
        }

        Assert.True(adversarial >= 1_000, $"coverage: only {adversarial} adversarial-cap executes");
        Assert.True(executes >= 1_000, $"coverage: only {executes} executes");
        Assert.True(capBindingAfterGrossUp >= 100, $"coverage: a cap bound after gross-up in only {capBindingAfterGrossUp} cases");
    }

    /// <summary>Independent restatement of the caps on final legs (exact decimal, levels within MaxLevelsPerSide, haircut depth).</summary>
    private static bool Fits(SpatialPairContext ctx, BookView buyBook, BookView sellBook, in SpatialParams p, long units, out decimal buyNotional)
    {
        buyNotional = 0m;
        long sellLots = units * ctx.Grid.LotsPerUnitB;
        decimal deliver = ctx.Sell.CanonicalQtyExact(sellLots);
        long buyLots = LotNormalizer.GrossUpBuyLots(deliver, p.BuyFee.Rate, ctx.Buy);
        ReadOnlySpan<PriceLevel> asks = buyBook.Asks;
        ReadOnlySpan<PriceLevel> bids = sellBook.Bids;
        VwapFill bf = VwapWalker.WalkLots(asks[..Math.Min(asks.Length, p.MaxLevelsPerSide)], buyLots, p.HaircutPpm);
        VwapFill sf = VwapWalker.WalkLots(bids[..Math.Min(bids.Length, p.MaxLevelsPerSide)], sellLots, p.HaircutPpm);
        if (!bf.IsComplete || !sf.IsComplete)
        {
            return false;
        }

        buyNotional = bf.NotionalQuoteExact(ctx.Buy);
        if (!double.IsPositiveInfinity(p.MaxQtyCanon) && (ctx.Buy.CanonicalQtyExact(buyLots) > (decimal)p.MaxQtyCanon || deliver > (decimal)p.MaxQtyCanon))
        {
            return false;
        }

        return double.IsPositiveInfinity(p.MaxBuyNotional) || buyNotional <= (decimal)p.MaxBuyNotional;
    }
}
