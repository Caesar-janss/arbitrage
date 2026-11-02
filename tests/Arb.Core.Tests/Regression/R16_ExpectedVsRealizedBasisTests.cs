using Arb.Core.Execution;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-16 (S3): expected and realized PnL were on different bases. exp_net subtracts a slippage BUFFER and amortized
/// REBALANCE cost that realized (fills + fees) never contains → capture ratio 1.29 for a perfectly modelled trade (DB
/// trade 1001). The comparable expectation is the modelled cash at the modelled VWAPs: ExpectedCashQuote. TradeGroup
/// also lacked funding, so the in-memory PnL of funding trades differed from the DB.
/// </summary>
public class R16_ExpectedVsRealizedBasisTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    [Fact]
    public void PerfectExecution_RealizedCash_EqualsExpectedCash()
    {
        var ctx = new SpatialPairContext(_m.BinanceBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (59995m, 0.006m), (60007.50m, 0.010m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60150m, 0.010m), (60100m, 0.020m) }, new[] { (60160m, 1m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, new SpatialParams
        {
            BuyFee = new(0.00075, FeeAssetKind.DiscountToken),
            SellFee = new(0.001, FeeAssetKind.Quote),
            BuySlipBps = 0.8,
            SellSlipBps = 0.5,
            RebalancePerUnitQuote = 2.0,
            MinProfitQuote = 0.05,
            MaxQtyCanon = double.PositiveInfinity,
            MaxBuyNotional = double.PositiveInfinity,
            HaircutPpm = VwapWalker.FullLiquidityPpm,
            MaxLevelsPerSide = 20,
        });
        Assert.Equal(RejectReason.Execute, q.Decision);

        // fills exactly at the modelled VWAPs and fees (= db/tests trade 1001)
        var g = new TradeGroup();
        g.Initialize(1001, StrategyKind.Spatial, _m.BinanceBtcSpot.Key, _m.BybitBtcSpot.Key, 0.01m, 0);
        g.ApplyFill(_m.BinanceBtcSpot.Key, Side.Buy, 0.01m, q.BuyNotionalExact, 0.00075m, FeeAssetKind.DiscountToken, (decimal)q.Edge.BuyFeeQuote);
        g.ApplyFill(_m.BybitBtcSpot.Key, Side.Sell, 0.01m, 601.5m, 0.6015m, FeeAssetKind.Quote, 0.6015m);

        Assert.Equal(q.Edge.ExpectedCashQuote, (double)g.CashFlowQuote, 1e-9);       // same basis → capture 1.0
        Assert.Equal(q.Edge.NetQuote + q.Edge.SlippageQuote + q.Edge.RebalanceQuote, q.Edge.ExpectedCashQuote, 1e-12);
        Assert.True(q.Edge.NetQuote < q.Edge.ExpectedCashQuote);                       // exp_net is the conservative gate
    }

    [Fact]
    public void FundingPayments_AreInTheTradeGroupCashFlow_LikeTheDbLedger()
    {
        // db/tests trade 1006: 1000PEPE perp-perp, 16 h, 6 funding payments (+0.14412), 4 taker fees
        var g = new TradeGroup();
        g.Initialize(1006, StrategyKind.FundingPerpPerp, _m.BybitPepePerp.Key, _m.BinancePepePerp.Key, 100_000_000m, 0);
        g.ApplyFill(_m.BybitPepePerp.Key, Side.Buy, 100_000_000m, 1200m, 0.66m, FeeAssetKind.Quote, 0.66m);
        g.ApplyFill(_m.BinancePepePerp.Key, Side.Sell, 100_000_000m, 1200.6m, 0.00090045m, FeeAssetKind.DiscountToken, 0.54027m);
        foreach (decimal f in new[] { -0.024m, 0.12006m, -0.024m, -0.024m, 0.12006m, -0.024m })
        {
            g.ApplyFunding(f);
        }

        g.ApplyFill(_m.BybitPepePerp.Key, Side.Sell, 100_000_000m, 1195m, 0.65725m, FeeAssetKind.Quote, 0.65725m);
        g.ApplyFill(_m.BinancePepePerp.Key, Side.Buy, 100_000_000m, 1195.3m, 0.0008965m, FeeAssetKind.DiscountToken, 0.537885m);
        Assert.Equal(0.14412m, g.FundingQuote);
        Assert.Equal(-1.951285m, g.CashFlowQuote); // == rpt.v_trade_ledger realized for 1006
    }
}
