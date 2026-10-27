using Arb.Core.Instruments;
using Arb.Core.Primitives;

namespace Arb.Core.Inventory;

public readonly struct ReservationHandle : IEquatable<ReservationHandle>
{
    internal ReservationHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    internal int Index { get; }

    internal int Generation { get; }

    public bool IsValid => Generation != 0;

    public bool Equals(ReservationHandle other) => Index == other.Index && Generation == other.Generation;

    public override bool Equals(object? obj) => obj is ReservationHandle other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Index, Generation);

    public static bool operator ==(ReservationHandle left, ReservationHandle right) => left.Equals(right);

    public static bool operator !=(ReservationHandle left, ReservationHandle right) => !left.Equals(right);
}

/// <summary>
/// Pre-trade inventory: in-memory, fed by the venue User Data Stream (never REST on the hot path).
///
/// Available = venueFree − reserved. Reservation lifecycle (conservative — can understate, never overstate):
///   TryReserve(…)                    decision taken, before sending
///   MarkTerminal(h, venueEventMs)    order reached a terminal state
///   OnBalance(slot, free, eventMs)   releases every terminal reservation on that slot whose terminal event happened
///                                    STRICTLY before the balance event. Order and wallet events come from different
///                                    venue services: a balance stamped in the same millisecond may predate the fill
///                                    (R-06), so "same ms" keeps the reservation.
///   FlagUnsettled(now, timeout)      no balance event for a terminal reservation within the timeout → flag the slot
///                                    for a REST reconcile. It does NOT release (that overstated Available, R-06); the
///                                    reconcile result fed to OnBalance (venue server time) settles it.
/// While an IOC is in flight the venue may already lock funds AND we still hold the reservation: double
/// counting in the SAFE direction. Two simultaneous opportunities can never spend the same balance.
///
/// SINGLE OWNER THREAD (the executor). Balance events from the user-stream thread are marshalled to it.
/// </summary>
public sealed class InventoryLedger
{
    private const int VenueSlots = 4;
    private const int AccountSlots = 4;

    private readonly int _assetCapacity;
    private readonly decimal[] _venueFree;
    private readonly decimal[] _reserved;
    private readonly long[] _lastBalanceEventMs;
    private readonly int[] _unsettled;
    private readonly Entry[] _entries;
    private readonly int[] _freeStack;
    private int _freeTop;
    private int _generation;

    public InventoryLedger(AssetRegistry assets, int maxReservations = 1024)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (maxReservations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxReservations), maxReservations, "must be > 0");
        }

        _assetCapacity = assets.Capacity;
        int slots = VenueSlots * AccountSlots * _assetCapacity;
        _venueFree = new decimal[slots];
        _reserved = new decimal[slots];
        _lastBalanceEventMs = new long[slots];
        Array.Fill(_lastBalanceEventMs, long.MinValue);
        _unsettled = new int[slots];
        _entries = new Entry[maxReservations];
        _freeStack = new int[maxReservations];
        for (int i = 0; i < maxReservations; i++)
        {
            _freeStack[i] = maxReservations - 1 - i;
        }

        _freeTop = maxReservations;
    }

    public int ActiveReservations => _entries.Length - _freeTop;

    public int SlotOf(Venue venue, AccountKind account, AssetId asset)
    {
        if (venue == Venue.Unknown || (uint)venue >= VenueSlots || (uint)account >= AccountSlots || account == 0 || !asset.IsValid || asset.Value >= _assetCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(asset), "unknown venue/account/asset");
        }

        return ((((int)venue * AccountSlots) + (int)account) * _assetCapacity) + asset.Value;
    }

    public decimal VenueFree(int slot) => _venueFree[slot];

    public decimal Reserved(int slot) => _reserved[slot];

    public decimal Available(int slot) => _venueFree[slot] - _reserved[slot];

    /// <summary>True while a terminal reservation on this slot waited longer than the settle timeout (→ REST reconcile).</summary>
    public bool NeedsReconcile(int slot) => _unsettled[slot] > 0;

    /// <summary>Venue-reported FREE balance (excluding venue-side locked) with the venue event time.</summary>
    public void OnBalance(int slot, decimal venueFree, long venueEventMs)
    {
        if (venueEventMs < _lastBalanceEventMs[slot])
        {
            return; // out-of-order older snapshot: ignore
        }

        _venueFree[slot] = venueFree;
        _lastBalanceEventMs[slot] = venueEventMs;
        for (int i = 0; i < _entries.Length; i++)
        {
            ref Entry e = ref _entries[i];
            if (e.Active && e.Terminal && e.Slot == slot && e.TerminalEventMs < venueEventMs)
            {
                ReleaseAt(i);
            }
        }
    }

    public bool TryReserve(int slot, decimal amount, out ReservationHandle handle)
    {
        handle = default;
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "must be > 0");
        }

        if (_freeTop == 0 || Available(slot) < amount)
        {
            return false;
        }

        handle = Allocate(slot, amount);
        return true;
    }

    /// <summary>Atomic: both reservations or none (two legs of one arbitrage).</summary>
    public bool TryReservePair(int slotA, decimal amountA, int slotB, decimal amountB, out ReservationHandle a, out ReservationHandle b)
    {
        a = default;
        b = default;
        if (amountA <= 0m || amountB <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amountA), "amounts must be > 0");
        }

        if (_freeTop < 2)
        {
            return false;
        }

        bool ok = slotA == slotB
            ? Available(slotA) >= amountA + amountB
            : Available(slotA) >= amountA && Available(slotB) >= amountB;
        if (!ok)
        {
            return false;
        }

        a = Allocate(slotA, amountA);
        b = Allocate(slotB, amountB);
        return true;
    }

    /// <summary>Order terminal. Released immediately only if a balance event STRICTLY after this time was already seen.</summary>
    public bool MarkTerminal(ReservationHandle handle, long venueEventMs, long nowMonoMicros)
    {
        if (!TryGet(handle, out int index))
        {
            return false;
        }

        ref Entry e = ref _entries[index];
        e.Terminal = true;
        e.TerminalEventMs = venueEventMs;
        e.TerminalMonoMicros = nowMonoMicros;
        if (_lastBalanceEventMs[e.Slot] > venueEventMs)
        {
            ReleaseAt(index);
        }

        return true;
    }

    /// <summary>Release without waiting for a balance event (order never sent / rejected before reaching the venue).</summary>
    public bool Release(ReservationHandle handle)
    {
        if (!TryGet(handle, out int index))
        {
            return false;
        }

        ReleaseAt(index);
        return true;
    }

    /// <summary>
    /// Flag terminal reservations that no balance event settled within the timeout. Returns how many were NEWLY flagged
    /// (trigger one REST balance reconcile per affected slot; see <see cref="NeedsReconcile"/>). Never releases.
    /// </summary>
    public int FlagUnsettled(long nowMonoMicros, long settleTimeoutMicros)
    {
        int n = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            ref Entry e = ref _entries[i];
            if (e.Active && e.Terminal && !e.ReconcileFlagged && nowMonoMicros - e.TerminalMonoMicros >= settleTimeoutMicros)
            {
                e.ReconcileFlagged = true;
                _unsettled[e.Slot]++;
                n++;
            }
        }

        return n;
    }

    public bool IsActive(ReservationHandle handle) => TryGet(handle, out _);

    private ReservationHandle Allocate(int slot, decimal amount)
    {
        int index = _freeStack[--_freeTop];
        _generation = _generation == int.MaxValue ? 1 : _generation + 1;
        _entries[index] = new Entry
        {
            Slot = slot,
            Amount = amount,
            Generation = _generation,
            Active = true,
        };
        _reserved[slot] += amount;
        return new ReservationHandle(index, _generation);
    }

    private bool TryGet(ReservationHandle handle, out int index)
    {
        index = handle.Index;
        return handle.IsValid
            && (uint)index < (uint)_entries.Length
            && _entries[index].Active
            && _entries[index].Generation == handle.Generation;
    }

    private void ReleaseAt(int index)
    {
        ref Entry e = ref _entries[index];
        if (e.ReconcileFlagged)
        {
            _unsettled[e.Slot]--;
        }

        _reserved[e.Slot] -= e.Amount;
        e = default;
        _freeStack[_freeTop++] = index;
    }

    private struct Entry
    {
        public int Slot;
        public decimal Amount;
        public int Generation;
        public bool Active;
        public bool Terminal;
        public long TerminalEventMs;
        public long TerminalMonoMicros;
        public bool ReconcileFlagged;
    }
}
