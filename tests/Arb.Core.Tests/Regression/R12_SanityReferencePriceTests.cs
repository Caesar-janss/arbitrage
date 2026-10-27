using Arb.Core.Primitives;
using Arb.Core.Risk;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-12 (S3): with the QUOTE route closed, the quote-inventory cap needs ReferencePx to convert USDT into base. An
/// unset (0) or NaN ReferencePx silently skipped the cap → Execute with an unlimited cap (fail-open, P02).
/// </summary>
public class R12_SanityReferencePriceTests
{
    private const long Now = 1_800_000_000_000;
    private readonly TestMarkets _m = TestMarkets.Create();

    private WalletStatusBook QuoteRouteClosed()
    {
        var w = new WalletStatusBook(_m.Assets);
        var erc = new NetworkId(1);
        foreach (Venue v in new[] { Venue.Bitget, Venue.Bybit })
        {
            w.Update(v, _m.Btc, new[] { new WalletNetworkStatus(erc, true, true, Now) });
            w.Update(v, _m.Usdt, new[] { new WalletNetworkStatus(erc, true, true, Now) });
        }

        w.Update(Venue.Bybit, _m.Usdt, new[] { new WalletNetworkStatus(erc, true, false, Now) }); // USDT Bybit → Bitget closed
        return w;
    }

    private SanityInput Input(double refPx) => new()
    {
        Strategy = StrategyKind.Spatial,
        Buy = _m.BitgetBtcSpot,
        Sell = _m.BybitBtcSpot,
        GrossEdgeBps = 20,
        SellVenueBaseInventory = 0.2,
        BuyVenueQuoteInventory = 12_000,
        ReferencePx = refPx,
    };

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(-1.0)]
    [InlineData(double.PositiveInfinity)]
    public void QuoteRouteClosed_WithoutAUsableReferencePrice_FailsClosed(double refPx)
    {
        SanityResult r = SanityGuard.Evaluate(Input(refPx), QuoteRouteClosed(), Now, SanityLimits.From(new RiskLimits()));
        Assert.True(r.Decision != RejectReason.Execute, $"refPx={refPx}: {r.Decision} cap={r.QtyCapCanon}"); // v0.2: Execute, cap ∞
        Assert.Equal(0, r.QtyCapCanon);
    }

    [Fact]
    public void QuoteRouteClosed_WithReferencePrice_StillCaps()
    {
        SanityResult r = SanityGuard.Evaluate(Input(60_000), QuoteRouteClosed(), Now, SanityLimits.From(new RiskLimits()));
        Assert.Equal(RejectReason.Execute, r.Decision);
        Assert.Equal(12_000 * 0.25 / 60_000, r.QtyCapCanon, 1e-12);
    }
}
