using Arb.Core.Primitives;
using Arb.Core.Risk;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// R-09 (S2): an adapter that cannot map a venue network name leaves NetworkId at its default (0). In v0.2 two such
/// networks on different venues (e.g. Bitget "TRC20" and Bybit "ERC20") MATCHED each other → route Open → the
/// wallet-trap filter allowed 300 bps spreads instead of 40 (P04). An unmapped network must never form a route.
/// </summary>
public class R09_UnmappedNetworkTests
{
    private const long Now = 1_800_000_000_000;
    private readonly TestMarkets _m = TestMarkets.Create();

    private WalletStatusBook TwoUnmapped()
    {
        var w = new WalletStatusBook(_m.Assets);
        w.Update(Venue.Bitget, _m.Btc, new[] { new WalletNetworkStatus(default, false, true, Now) }); // withdraw on, network unmapped
        w.Update(Venue.Bybit, _m.Btc, new[] { new WalletNetworkStatus(default, true, false, Now) });  // deposit on, network unmapped
        return w;
    }

    [Fact]
    public void TwoUnmappedNetworks_DoNotFormARoute()
    {
        TransferRoute r = TwoUnmapped().FindRoute(_m.Btc, Venue.Bitget, Venue.Bybit, Now, 60_000);
        Assert.False(r.IsOpen); // v0.2: Open via network 0
        Assert.Equal(RouteStatus.Unknown, r.Status);
    }

    [Fact]
    public void UnmappedNetwork_DoesNotCountAsDepositOrWithdrawEnabled()
    {
        WalletStatusBook w = TwoUnmapped();
        Assert.False(w.IsWithdrawEnabled(Venue.Bitget, _m.Btc, Now, 60_000));
        Assert.False(w.IsDepositEnabled(Venue.Bybit, _m.Btc, Now, 60_000));
    }

    [Fact]
    public void UnmappedNetworks_KeepTheWalletTrapFilterOn()
    {
        WalletStatusBook w = TwoUnmapped();
        var usdt = new NetworkId(1);
        w.Update(Venue.Bitget, _m.Usdt, new[] { new WalletNetworkStatus(usdt, true, true, Now) });
        w.Update(Venue.Bybit, _m.Usdt, new[] { new WalletNetworkStatus(usdt, true, true, Now) });
        var x = new SanityInput { Strategy = StrategyKind.Spatial, Buy = _m.BitgetBtcSpot, Sell = _m.BybitBtcSpot, GrossEdgeBps = 120, SellVenueBaseInventory = 0.2, BuyVenueQuoteInventory = 12_000, ReferencePx = 60_000 };
        SanityResult r = SanityGuard.Evaluate(x, w, Now, SanityLimits.From(new RiskLimits()));
        Assert.Equal(RejectReason.RejectSanity, r.Decision); // v0.2: Execute (route "open")
        Assert.True((r.Flags & SanityFlags.SuspectedWalletTrap) != 0);
    }

    [Fact]
    public void MappedNetworks_StillWork_AndUnmappedOnesAreCounted()
    {
        var w = new WalletStatusBook(_m.Assets);
        var erc = new NetworkId(1);
        int ignored = w.Update(Venue.Bitget, _m.Btc, new[] { new WalletNetworkStatus(default, true, true, Now), new WalletNetworkStatus(erc, true, true, Now) });
        w.Update(Venue.Bybit, _m.Btc, new[] { new WalletNetworkStatus(erc, true, true, Now) });
        Assert.Equal(1, ignored);
        Assert.False(default(NetworkId).IsValid);
        Assert.True(erc.IsValid);
        TransferRoute r = w.FindRoute(_m.Btc, Venue.Bitget, Venue.Bybit, Now, 60_000);
        Assert.True(r.IsOpen);
        Assert.Equal(erc, r.Network);
    }
}
