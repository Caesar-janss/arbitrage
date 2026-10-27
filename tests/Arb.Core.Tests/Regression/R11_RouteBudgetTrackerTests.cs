using Arb.Core.Primitives;
using Arb.Core.Risk;
using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>R-11 (S3), mechanism: the budget is a snapshot at route close minus cumulative use, reset on reopen.</summary>
public class R11_RouteBudgetTrackerTests
{
    private readonly TestMarkets _m = TestMarkets.Create();

    [Fact]
    public void WithTracker_TradesConsumeExactlyTheFraction_ThenStop()
    {
        WalletStatusBook w = R11_NonReplenishableCapInvariantTests.BaseRouteClosed(_m);
        SanityLimits lim = SanityLimits.From(new RiskLimits());
        var tracker = new RouteBudgetTracker(_m.Assets);
        double inventory = 0.2;
        int trades = 0;
        while (trades < 100)
        {
            TransferRoute route = w.FindRoute(_m.Btc, Venue.Bitget, Venue.Bybit, R11_NonReplenishableCapInvariantTests.Now, lim.MaxWalletStatusAgeMs);
            tracker.Observe(Venue.Bybit, _m.Btc, route.Status, inventory);
            SanityInput x = R11_NonReplenishableCapInvariantTests.Input(_m, inventory) with { SellVenueBaseBudget = tracker.Budget(Venue.Bybit, _m.Btc) };
            SanityResult r = SanityGuard.Evaluate(x, w, R11_NonReplenishableCapInvariantTests.Now, lim);
            if (!r.Passed)
            {
                break;
            }

            double q = Math.Min(r.QtyCapCanon, 0.012); // trades smaller than the cap
            inventory -= q;
            tracker.RecordUse(Venue.Bybit, _m.Btc, q);
            trades++;
        }

        Assert.Equal(0.05, 0.2 - inventory, 1e-12); // exactly 25 % of the inventory at route close
        Assert.True(trades is > 1 and < 100);
    }

    [Fact]
    public void Reopen_ResetsTheBudget_AndUnknownBudgetFailsClosed()
    {
        var tracker = new RouteBudgetTracker(_m.Assets);
        tracker.Observe(Venue.Bybit, _m.Btc, RouteStatus.Closed, 0.2);
        tracker.RecordUse(Venue.Bybit, _m.Btc, 0.03);
        tracker.Observe(Venue.Bybit, _m.Btc, RouteStatus.Closed, 0.17); // still closed: snapshot kept
        NonReplenishableBudget b = tracker.Budget(Venue.Bybit, _m.Btc);
        Assert.Equal(0.2, b.InventoryAtRouteClose);
        Assert.Equal(0.03, b.UsedSinceRouteClose, 1e-15);

        tracker.Observe(Venue.Bybit, _m.Btc, RouteStatus.Open, 0.5);
        Assert.False(tracker.Budget(Venue.Bybit, _m.Btc).IsKnown);
        tracker.Observe(Venue.Bybit, _m.Btc, RouteStatus.Unknown, 0.5); // closes again: new snapshot
        Assert.Equal(0.5, tracker.Budget(Venue.Bybit, _m.Btc).InventoryAtRouteClose);

        WalletStatusBook w = R11_NonReplenishableCapInvariantTests.BaseRouteClosed(_m);
        SanityResult r = SanityGuard.Evaluate(R11_NonReplenishableCapInvariantTests.Input(_m, 0.2), w, R11_NonReplenishableCapInvariantTests.Now, SanityLimits.From(new RiskLimits()));
        Assert.Equal(RejectReason.RejectInventory, r.Decision); // no budget supplied → fail closed
        Assert.True((r.Flags & SanityFlags.BudgetUnknown) != 0);
    }
}
