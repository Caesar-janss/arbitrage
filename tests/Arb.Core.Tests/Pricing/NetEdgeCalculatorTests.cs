using Arb.Core.Pricing;

namespace Arb.Core.Tests.Pricing;

public class NetEdgeCalculatorTests
{
    private static readonly ResolvedFee BinanceBnb = new(0.00075, FeeAssetKind.DiscountToken);
    private static readonly ResolvedFee BybitQuote = new(0.001, FeeAssetKind.Quote);

    [Fact]
    public void TextbookFormula_EqualQty_QuoteFees()
    {
        // NetProfit = Q·(VWAPbid_B − VWAPask_A) − Fee_A − Fee_B − Slippage − Rebalance
        var x = new EdgeInputs(0.01, 0.01, 60000, 60150, BinanceBnb, BybitQuote, 1.0, 1.0, 2.0, 60150);
        EdgeBreakdown e = NetEdgeCalculator.Compute(x);
        Assert.Equal(1.5, e.GrossQuote, 1e-9);
        Assert.Equal(0.45, e.BuyFeeQuote, 1e-9);
        Assert.Equal(0.6015, e.SellFeeQuote, 1e-9);
        Assert.Equal(0.12015, e.SlippageQuote, 1e-9);
        Assert.Equal(0.02, e.RebalanceQuote, 1e-12);
        Assert.Equal(0.30835, e.NetQuote, 1e-9);
        Assert.Equal(25.0, e.GrossEdgeBps, 1e-9);
        Assert.Equal(0.30835 / 600 * 1e4, e.NetEdgeBps, 1e-9);
        Assert.Equal(0, e.ResidualBase, 1e-15);
    }

    [Fact]
    public void MatchesDbOpportunity501()
    {
        // db/tests opportunity 501: fee_cost 1.0515, slip 0.08, rebalance 0.02 → expected_net 0.3485
        var x = new EdgeInputs(0.01, 0.01, 60000, 60150, BinanceBnb, BybitQuote, 0, 0, 0, 60150);
        EdgeBreakdown e = NetEdgeCalculator.Compute(x);
        Assert.Equal(1.0515, e.TotalFeeQuote, 1e-9);
        Assert.Equal(0.3485, e.NetQuote - 0.08 - 0.02, 1e-9);
    }

    [Fact]
    public void BaseFee_IsCountedOnce_AsResidual()
    {
        var baseFee = new ResolvedFee(0.001, FeeAssetKind.Base);
        var quoteFee = new ResolvedFee(0.001, FeeAssetKind.Quote);

        // no gross-up: buy 0.01, sell 0.01, fee in base → short 0.00001 BTC
        EdgeBreakdown noGross = NetEdgeCalculator.Compute(new EdgeInputs(0.01, 0.01, 60000, 60150, baseFee, quoteFee, 0, 0, 0, 60150));
        Assert.Equal(-0.00001, noGross.ResidualBase, 1e-15);
        Assert.Equal(1.5 - 0.6015 - (0.00001 * 60150), noGross.NetQuote, 1e-9);

        // gross-up: buy 0.010011 so that received ≥ 0.01 → small long residual, no double count
        EdgeBreakdown gross = NetEdgeCalculator.Compute(new EdgeInputs(0.010011, 0.01, 60000, 60150, baseFee, quoteFee, 0, 0, 0, 60150));
        Assert.Equal(0.000000989, gross.ResidualBase, 1e-15);
        double expected = 601.5 - 600.66 - 0.6015 + (0.000000989 * 60150);
        Assert.Equal(expected, gross.NetQuote, 1e-9);
        Assert.Equal(600.66 * 0.001, gross.BuyFeeQuote, 1e-9);
    }

    [Fact]
    public void ZeroQty_IsSafe()
    {
        EdgeBreakdown e = NetEdgeCalculator.Compute(new EdgeInputs(0, 0, 60000, 60150, BinanceBnb, BybitQuote, 0, 0, 0, 60150));
        Assert.Equal(0, e.NetQuote);
        Assert.Equal(0, e.NetEdgeBps);
    }
}
