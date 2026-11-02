using Arb.Core.Primitives;
using Arb.Core.Risk;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-11 (S3), invariant on the pre-existing API (P03): with the base route closed, the inventory that cannot be
/// replenished may be used at most up to NonReplenishableInventoryFraction (25 %) of what existed at route close.
/// v0.2 capped each trade at 25 % of what was LEFT → 22 trades consumed 99.8 %.
/// </summary>
public class R11_NonReplenishableCapInvariantTests
{
    internal const long Now = 1_800_000_000_000;

    [Fact]
    public void RepeatedTradesAtTheCap_NeverConsumeMoreThanTheFraction()
    {
        var m = TestMarkets.Create();
        WalletStatusBook w = BaseRouteClosed(m);
        SanityLimits lim = SanityLimits.From(new RiskLimits());
        const double atClose = 0.2;
        double inventory = atClose;
        for (int trade = 0; trade < 200; trade++)
        {
            SanityResult r = SanityGuard.Evaluate(Input(m, inventory), w, Now, lim);
            if (!r.Passed || r.QtyCapCanon < 1e-9)
            {
                break;
            }

            inventory -= r.QtyCapCanon; // every trade takes the full cap
        }

        double consumed = (atClose - inventory) / atClose;
        Assert.True(consumed <= 0.25 + 1e-12, $"consumed {consumed:P2} of the non-replenishable inventory (limit 25 %)");
    }

    internal static WalletStatusBook BaseRouteClosed(TestMarkets m)
    {
        var w = new WalletStatusBook(m.Assets);
        var erc = new NetworkId(1);
        w.Update(Venue.Bitget, m.Btc, new[] { new WalletNetworkStatus(erc, true, true, Now) });
        w.Update(Venue.Bybit, m.Btc, new[] { new WalletNetworkStatus(erc, false, true, Now) }); // deposit closed on the sell venue
        w.Update(Venue.Bitget, m.Usdt, new[] { new WalletNetworkStatus(erc, true, true, Now) });
        w.Update(Venue.Bybit, m.Usdt, new[] { new WalletNetworkStatus(erc, true, true, Now) });
        return w;
    }

    internal static SanityInput Input(TestMarkets m, double sellInventory) => new()
    {
        Strategy = StrategyKind.Spatial,
        Buy = m.BitgetBtcSpot,
        Sell = m.BybitBtcSpot,
        GrossEdgeBps = 20,
        SellVenueBaseInventory = sellInventory,
        BuyVenueQuoteInventory = 1e9,
        ReferencePx = 60_000,
    };
}
