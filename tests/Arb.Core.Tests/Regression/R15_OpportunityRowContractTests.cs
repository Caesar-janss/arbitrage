using System.Text.RegularExpressions;
using Arb.Core.MarketData;
using Arb.Core.Pricing;
using Arb.Core.Primitives;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-15 (S3), contract: every SpatialQuote outcome maps to a row trd.opportunity accepts. HasExecutionPricing = the
/// execution columns (qty, VWAP, worst px, levels, edge, costs) are real; otherwise the writer stores NULL. V007 makes
/// those columns nullable and requires them only for EXECUTE (checked here against the migration text).
/// </summary>
public class R15_OpportunityRowContractTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    [Fact]
    public void ExecuteQuote_HasExecutionPricing_AndPositiveColumns()
    {
        var ctx = new SpatialPairContext(_m.BinanceBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 1m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60200m, 1m) }, new[] { (60210m, 1m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, new SpatialParams
        {
            BuyFee = new(0.00075, FeeAssetKind.DiscountToken),
            SellFee = new(0.001, FeeAssetKind.Quote),
            MinProfitQuote = 0.05,
            MaxQtyCanon = 0.01,
            MaxBuyNotional = double.PositiveInfinity,
            HaircutPpm = VwapWalker.FullLiquidityPpm,
            MaxLevelsPerSide = 20,
        });
        Assert.Equal(RejectReason.Execute, q.Decision);
        Assert.True(q.HasExecutionPricing);
        Assert.True(q.BuyVwap > 0 && q.SellVwap > 0 && q.BuyWorstPx > 0 && q.SellWorstPx > 0 && q.BuyLevels > 0 && q.SellLevels > 0);
    }

    [Fact]
    public void RejectWithoutWalks_HasNoExecutionPricing()
    {
        var ctx = new SpatialPairContext(_m.BinanceBtcSpot, _m.BybitBtcSpot);
        BookView buy = TestMarkets.View(_m.BinanceBtcSpot, new[] { (59990m, 1m) }, new[] { (60000m, 1m) });
        BookView sell = TestMarkets.View(_m.BybitBtcSpot, new[] { (60010m, 1m) }, new[] { (60020m, 1m) });
        SpatialQuote q = SpatialArbPricer.Evaluate(ctx, buy, sell, new SpatialParams
        {
            BuyFee = new(0.00075, FeeAssetKind.DiscountToken),
            SellFee = new(0.001, FeeAssetKind.Quote),
            MaxQtyCanon = double.PositiveInfinity,
            MaxBuyNotional = double.PositiveInfinity,
            HaircutPpm = VwapWalker.FullLiquidityPpm,
            MaxLevelsPerSide = 20,
        });
        Assert.False(q.HasExecutionPricing);
        Assert.Equal(0, q.BuyLevels); // writer: levels 0 → NULL
    }

    [Fact]
    public void Migration_RequiresExecutionColumnsOnlyForExecute()
    {
        string v7 = TestSupport.RepoFiles.ReadAll("db/migrations/V007__review_round1_fixes.sql");
        foreach (string col in new[] { "qty_base", "buy_vwap_px", "sell_vwap_px", "buy_worst_px", "sell_worst_px", "buy_levels", "sell_levels", "gross_edge_bps", "fee_cost_quote", "slippage_buffer_quote", "rebalance_cost_quote", "expected_net_quote" })
        {
            Assert.True(Regex.IsMatch(v7, $@"ALTER COLUMN {col}\s+DROP NOT NULL"), $"{col} still NOT NULL for rejects");
            Assert.True(Regex.IsMatch(v7, $@"decision <> 'EXECUTE' OR \([^;]*\b{col} IS NOT NULL", RegexOptions.Singleline), $"{col} not required for EXECUTE");
        }
    }
}
