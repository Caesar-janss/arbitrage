using Arb.Core.Instruments;
using Arb.Core.Primitives;

namespace Arb.Core.Risk;

/// <summary>
/// Canonical chain/network id (adapter maps venue names: Binance "ETH" / Bybit "ETH" / Bitget "ERC20" → same id).
/// 0 = UNMAPPED (adapter could not map the venue's name). Unmapped networks never match each other and never form a
/// route (R-09: two unmapped networks used to match as "network 0").
/// </summary>
public readonly struct NetworkId : IEquatable<NetworkId>
{
    public NetworkId(ushort value)
    {
        Value = value;
    }

    public ushort Value { get; }

    public bool IsValid => Value != 0;

    public bool Equals(NetworkId other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is NetworkId other && Equals(other);

    public override int GetHashCode() => Value;

    public static bool operator ==(NetworkId left, NetworkId right) => left.Equals(right);

    public static bool operator !=(NetworkId left, NetworkId right) => !left.Equals(right);
}

/// <summary>Deposit/withdraw flags of one (venue, asset, network) — from the venue's coin/network config API.</summary>
public readonly struct WalletNetworkStatus
{
    public WalletNetworkStatus(NetworkId network, bool isDepositEnabled, bool isWithdrawEnabled, long updatedUnixMs)
    {
        Network = network;
        IsDepositEnabled = isDepositEnabled;
        IsWithdrawEnabled = isWithdrawEnabled;
        UpdatedUnixMs = updatedUnixMs;
    }

    public NetworkId Network { get; }

    public bool IsDepositEnabled { get; }

    public bool IsWithdrawEnabled { get; }

    public long UpdatedUnixMs { get; }
}

public enum RouteStatus : byte
{
    Open = 0,
    Closed = 1,
    /// <summary>Missing or older than the max age → treated as Closed by guards (fail-safe).</summary>
    Unknown = 2,
}

public readonly struct TransferRoute
{
    public TransferRoute(RouteStatus status, NetworkId network)
    {
        Status = status;
        Network = network;
    }

    public RouteStatus Status { get; }

    /// <summary>A network open on both ends (valid only when <see cref="Status"/> is Open).</summary>
    public NetworkId Network { get; }

    public bool IsOpen => Status == RouteStatus.Open;
}

/// <summary>
/// Wallet status per (venue, asset). Writer: metadata poller (cold, every few minutes); readers: hot path.
/// Each slot holds an immutable array swapped atomically → no locks, no torn reads.
///
/// A transfer route from venue A to venue B exists only if SOME network has withdraw open on A AND deposit
/// open on B. "Withdraw open on A" and "deposit open on B" on different networks is NOT a route.
/// </summary>
public sealed class WalletStatusBook
{
    private const int VenueSlots = 4;
    private readonly int _assetCapacity;
    private readonly WalletNetworkStatus[]?[] _slots;

    public WalletStatusBook(AssetRegistry assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assetCapacity = assets.Capacity;
        _slots = new WalletNetworkStatus[]?[VenueSlots * _assetCapacity];
    }

    /// <summary>Replace the networks of (venue, asset). Returns how many were UNMAPPED (ignored by every check → alert).</summary>
    public int Update(Venue venue, AssetId asset, ReadOnlySpan<WalletNetworkStatus> networks)
    {
        int slot = Slot(venue, asset);
        int unmapped = 0;
        for (int i = 0; i < networks.Length; i++)
        {
            if (!networks[i].Network.IsValid)
            {
                unmapped++;
            }
        }

        Volatile.Write(ref _slots[slot], networks.ToArray());
        return unmapped;
    }

    public bool IsDepositEnabled(Venue venue, AssetId asset, long nowUnixMs, long maxAgeMs) =>
        Any(venue, asset, nowUnixMs, maxAgeMs, deposit: true);

    public bool IsWithdrawEnabled(Venue venue, AssetId asset, long nowUnixMs, long maxAgeMs) =>
        Any(venue, asset, nowUnixMs, maxAgeMs, deposit: false);

    public TransferRoute FindRoute(AssetId asset, Venue from, Venue to, long nowUnixMs, long maxAgeMs)
    {
        WalletNetworkStatus[]? src = Volatile.Read(ref _slots[Slot(from, asset)]);
        WalletNetworkStatus[]? dst = Volatile.Read(ref _slots[Slot(to, asset)]);
        if (src is null || dst is null)
        {
            return new TransferRoute(RouteStatus.Unknown, default);
        }

        bool anyStale = false;
        bool anyUnmapped = false;
        for (int i = 0; i < src.Length; i++)
        {
            ref readonly WalletNetworkStatus s = ref src[i];
            if (!s.Network.IsValid)
            {
                anyUnmapped = true;
                continue;
            }

            if (nowUnixMs - s.UpdatedUnixMs > maxAgeMs)
            {
                anyStale = true;
                continue;
            }

            if (!s.IsWithdrawEnabled)
            {
                continue;
            }

            for (int j = 0; j < dst.Length; j++)
            {
                ref readonly WalletNetworkStatus d = ref dst[j];
                if (d.Network != s.Network)
                {
                    continue;
                }

                if (nowUnixMs - d.UpdatedUnixMs > maxAgeMs)
                {
                    anyStale = true;
                    continue;
                }

                if (d.IsDepositEnabled)
                {
                    return new TransferRoute(RouteStatus.Open, s.Network);
                }
            }
        }

        for (int j = 0; j < dst.Length && !anyUnmapped; j++)
        {
            anyUnmapped = !dst[j].Network.IsValid;
        }

        // an unmapped network MIGHT be a real route we cannot see: Unknown (= closed for guards), never Open
        return new TransferRoute(anyStale || anyUnmapped ? RouteStatus.Unknown : RouteStatus.Closed, default);
    }

    private bool Any(Venue venue, AssetId asset, long nowUnixMs, long maxAgeMs, bool deposit)
    {
        WalletNetworkStatus[]? nets = Volatile.Read(ref _slots[Slot(venue, asset)]);
        if (nets is null)
        {
            return false;
        }

        for (int i = 0; i < nets.Length; i++)
        {
            ref readonly WalletNetworkStatus n = ref nets[i];
            if (n.Network.IsValid && nowUnixMs - n.UpdatedUnixMs <= maxAgeMs && (deposit ? n.IsDepositEnabled : n.IsWithdrawEnabled))
            {
                return true;
            }
        }

        return false;
    }

    private int Slot(Venue venue, AssetId asset)
    {
        if ((uint)venue >= VenueSlots || venue == Venue.Unknown || !asset.IsValid || asset.Value >= _assetCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(asset), "unknown venue/asset");
        }

        return ((int)venue * _assetCapacity) + asset.Value;
    }
}
