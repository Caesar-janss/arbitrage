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

/// <summary>Outcome of one balance observation (see <see cref="InventoryLedger.OnBalance(int, decimal, long)"/>).</summary>
public enum BalanceUpdate : byte
{
    /// <summary>Became the slot's balance (strictly later, or the next event of the ordered stream in the same ms).</summary>
    Applied = 1,

    /// <summary>Same millisecond, same value: nothing changes.</summary>
    Duplicate = 2,

    /// <summary>Older than what the slot already reflects: ignored.</summary>
    Stale = 3,

    /// <summary>
    /// Could not be ordered against the slot's balance (same millisecond, different value, at least one side without a
    /// delivery sequence; or the ordered stream's venue time went backwards). The LOWER value is kept and the slot is
    /// flagged for a reconcile (C-07). Resolved by the next strictly later observation.
    /// </summary>
    Conflict = 4,
}

/// <summary>
/// Pre-trade inventory: in-memory, fed by the venue User Data Stream (never REST on the hot path).
///
/// Available = venueFree − reserved. Reservation lifecycle (conservative — can understate, never overstate):
///   TryReserve(…)                    decision taken, before sending
///   MarkTerminal(h, venueEventMs)    order reached a terminal state
///   OnBalance(slot, free, eventMs…)  releases every terminal reservation on that slot whose terminal event happened
///                                    STRICTLY before the balance event. Order and wallet events come from different
///                                    venue services: a balance stamped in the same millisecond may predate the fill
///                                    (R-06), so "same ms" keeps the reservation.
///   FlagUnsettled(now, timeout)      no balance event for a terminal reservation within the timeout → flag the slot
///                                    for a REST reconcile. It does NOT release (that overstated Available, R-06); the
///                                    reconcile result fed to OnBalance (venue server time) settles it.
/// While an IOC is in flight the venue may already lock funds AND we still hold the reservation: double
/// counting in the SAFE direction. Two simultaneous opportunities can never spend the same balance.
///
/// Ordering balance observations (C-07). A millisecond stamp does not order two observations of the same millisecond,
/// and a late REST snapshot can describe the slot before a change booked in that millisecond (fee, transfer, fill).
///   * <see cref="OnBalance(int, decimal, long, long)"/>: the ONE ordered user stream of the slot, with a delivery
///     sequence that strictly increases per slot. Inside a millisecond, delivery order decides.
///   * <see cref="OnBalance(int, decimal, long)"/>: anything without a delivery order (REST reconcile, a second
///     connection). Ordered only by venue time; a same-ms observation with another value is a Conflict → keep the lower
///     value (one of the two is the true latest, so the lower cannot overstate) and flag <see cref="NeedsReconcile"/>.
///   * A sequence that does not advance (replay, reconnect reset) is treated as unsequenced. The ordered stream's venue
///     time going backwards is a Conflict, not a silent drop (the dropped event could be a debit).
/// HOST CONTRACT (adapters, Tahap 2): the stamp of an unsequenced snapshot is the venue time the snapshot is known to
/// reflect (as-of), never the local receive time and never a fresh time on cached data. When the venue gives no as-of
/// time, stamp a LOWER bound (venue time at request send minus the measured cache lag): an earlier stamp only releases
/// fewer reservations and loses ties, so it errs on the safe side. "Never overstate" holds under this contract; the
/// model check in C07_* tests it.
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
    private readonly long[] _lastStreamSeq;
    private readonly long[] _lastStreamMs;
    private readonly bool[] _orderedAtLast;
    private readonly bool[] _balanceConflict;
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
        _lastStreamSeq = new long[slots];
        Array.Fill(_lastStreamSeq, long.MinValue);
        _lastStreamMs = new long[slots];
        Array.Fill(_lastStreamMs, long.MinValue);
        _orderedAtLast = new bool[slots];
        _balanceConflict = new bool[slots];
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

    /// <summary>
    /// True while a terminal reservation on this slot waited longer than the settle timeout, or while two balance
    /// observations of the slot could not be ordered (C-07) → REST reconcile.
    /// </summary>
    public bool NeedsReconcile(int slot) => _unsettled[slot] > 0 || _balanceConflict[slot];

    /// <summary>True while the slot holds the lower of two observations that could not be ordered (C-07).</summary>
    public bool HasBalanceConflict(int slot) => _balanceConflict[slot];

    /// <summary>
    /// Venue-reported FREE balance (excluding venue-side locked) from a source WITHOUT a delivery order (REST reconcile,
    /// a second connection), stamped with the venue time it is known to reflect (see the host contract above).
    /// </summary>
    public BalanceUpdate OnBalance(int slot, decimal venueFree, long venueEventMs) => Apply(slot, venueFree, venueEventMs, ordered: false);

    /// <summary>
    /// Venue-reported FREE balance from the slot's ordered user stream. <paramref name="streamSequence"/> is the
    /// delivery order (strictly increasing per slot, e.g. the adapter's receive counter); it orders events inside one
    /// venue millisecond. Exactly one source per slot may use this overload.
    /// </summary>
    public BalanceUpdate OnBalance(int slot, decimal venueFree, long venueEventMs, long streamSequence)
    {
        if (streamSequence <= _lastStreamSeq[slot])
        {
            return Apply(slot, venueFree, venueEventMs, ordered: false); // no delivery order → venue time only
        }

        _lastStreamSeq[slot] = streamSequence;
        if (venueEventMs < _lastStreamMs[slot])
        {
            return Conflict(slot, venueFree); // newer by delivery, older by venue time: cannot tell → lower value
        }

        _lastStreamMs[slot] = venueEventMs;
        return Apply(slot, venueFree, venueEventMs, ordered: true);
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

    private BalanceUpdate Apply(int slot, decimal venueFree, long venueEventMs, bool ordered)
    {
        long last = _lastBalanceEventMs[slot];
        if (venueEventMs < last)
        {
            return BalanceUpdate.Stale; // the slot already reflects a later venue time
        }

        if (venueEventMs == last)
        {
            if (ordered && _orderedAtLast[slot])
            {
                _venueFree[slot] = venueFree; // next event of the ordered stream inside the same ms
                return BalanceUpdate.Applied;
            }

            return venueFree == _venueFree[slot] ? BalanceUpdate.Duplicate : Conflict(slot, venueFree);
        }

        _venueFree[slot] = venueFree;
        _lastBalanceEventMs[slot] = venueEventMs;
        _orderedAtLast[slot] = ordered;
        _balanceConflict[slot] = false;
        for (int i = 0; i < _entries.Length; i++)
        {
            ref Entry e = ref _entries[i];
            if (e.Active && e.Terminal && e.Slot == slot && e.TerminalEventMs < venueEventMs)
            {
                ReleaseAt(i);
            }
        }

        return BalanceUpdate.Applied;
    }

    private BalanceUpdate Conflict(int slot, decimal venueFree)
    {
        if (venueFree < _venueFree[slot])
        {
            _venueFree[slot] = venueFree;
        }

        _orderedAtLast[slot] = false; // a later same-ms stream event can no longer be ordered against this value
        _balanceConflict[slot] = true;
        return BalanceUpdate.Conflict;
    }

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
