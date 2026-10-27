using Arb.Core.Instruments;
using Arb.Core.Primitives;

namespace Arb.Core.Risk;

/// <summary>
/// Inventory that cannot be replenished while a transfer route is closed: snapshot at route close and cumulative use
/// since. SanityGuard allows at most NonReplenishableFraction × snapshot in TOTAL (R-11: v0.2 applied the fraction to
/// what was left on every trade → 22 trades used 99.8 %). default = unknown → fail closed.
/// </summary>
public readonly struct NonReplenishableBudget
{
    public NonReplenishableBudget(double inventoryAtRouteClose, double usedSinceRouteClose)
    {
        InventoryAtRouteClose = inventoryAtRouteClose;
        UsedSinceRouteClose = usedSinceRouteClose;
        IsKnown = double.IsFinite(inventoryAtRouteClose) && inventoryAtRouteClose >= 0
            && double.IsFinite(usedSinceRouteClose) && usedSinceRouteClose >= 0;
    }

    public double InventoryAtRouteClose { get; }

    public double UsedSinceRouteClose { get; }

    public bool IsKnown { get; }

    /// <summary>fraction × snapshot − used, never negative; 0 when unknown.</summary>
    public double Remaining(double fraction) =>
        IsKnown ? Math.Max(0, (fraction * InventoryAtRouteClose) - UsedSinceRouteClose) : 0;
}

/// <summary>
/// Tracks <see cref="NonReplenishableBudget"/> per (venue, asset) = the wallet that a closed route cannot refill.
/// Executor thread only; allocation-free after construction.
///   Observe(venue, asset, routeStatus, inventory) every evaluation: Open → budget cleared; first non-Open →
///   snapshot = inventory; while not Open the snapshot is kept.
///   RecordUse(venue, asset, qty) after a fill that consumed the wallet.
/// </summary>
public sealed class RouteBudgetTracker
{
    private const int VenueSlots = 4;
    private readonly int _assetCapacity;
    private readonly bool[] _active;
    private readonly double[] _atClose;
    private readonly double[] _used;

    public RouteBudgetTracker(AssetRegistry assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assetCapacity = assets.Capacity;
        _active = new bool[VenueSlots * _assetCapacity];
        _atClose = new double[VenueSlots * _assetCapacity];
        _used = new double[VenueSlots * _assetCapacity];
    }

    public void Observe(Venue venue, AssetId asset, RouteStatus status, double currentInventory)
    {
        int i = Slot(venue, asset);
        if (status == RouteStatus.Open)
        {
            _active[i] = false;
            _atClose[i] = 0;
            _used[i] = 0;
            return;
        }

        if (!_active[i])
        {
            _active[i] = true;
            _atClose[i] = double.IsFinite(currentInventory) ? Math.Max(0, currentInventory) : double.NaN;
            _used[i] = 0;
        }
    }

    public void RecordUse(Venue venue, AssetId asset, double qty)
    {
        int i = Slot(venue, asset);
        if (_active[i] && qty > 0)
        {
            _used[i] += qty;
        }
    }

    /// <summary>Budget of a closed route; unknown (fail-closed) when the route was never observed closed.</summary>
    public NonReplenishableBudget Budget(Venue venue, AssetId asset)
    {
        int i = Slot(venue, asset);
        return _active[i] ? new NonReplenishableBudget(_atClose[i], _used[i]) : default;
    }

    private int Slot(Venue venue, AssetId asset)
    {
        if (venue == Venue.Unknown || (uint)venue >= VenueSlots || !asset.IsValid || asset.Value >= _assetCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(asset), "unknown venue/asset");
        }

        return ((int)venue * _assetCapacity) + asset.Value;
    }
}
