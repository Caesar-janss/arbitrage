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
    /// <summary>
    /// Became the slot's balance: definitely newer than everything the slot holds (its as-of lower bound is after their
    /// upper bound), or the next event of the ordered stream in the same ms.
    /// </summary>
    Applied = 1,

    /// <summary>Could not be ordered, but has the same value: the value stays, the time bounds widen.</summary>
    Duplicate = 2,

    /// <summary>Definitely older than an observation the slot holds: ignored.</summary>
    Stale = 3,

    /// <summary>
    /// Could not be ordered against the slot's balance (as-of intervals overlap / same ms without a delivery order; or the
    /// ordered stream's venue time went backwards) and has another value. The LOWER value is kept and the slot is flagged
    /// for a reconcile (C-07, C-09). Resolved by the next observation that is definitely newer than all of them.
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
/// Ordering balance observations (C-07, C-09). Every observation has an as-of INTERVAL [notBefore, notAfter] of venue
/// time: the instant whose state it reports lies in it. A stream event or an exact-as-of snapshot is a point; a REST
/// snapshot is [request sent − cache lag, response time]. Two observations are ordered only if one interval ends before
/// the other starts, or both come from the slot's ordered stream; otherwise they are ambiguous.
///   * <see cref="OnBalance(int, decimal, long, long)"/>: the ONE ordered user stream of the slot, with a delivery
///     sequence that strictly increases per slot. Inside a millisecond, delivery order decides — but only while the slot
///     holds nothing else: any unordered observation (even an equal-valued one) ends same-ms ordering.
///   * <see cref="OnBalance(int, decimal, long)"/>: a point without a delivery order (exact-as-of snapshot, a second
///     connection).
///   * <see cref="OnBalanceSnapshot"/>: a snapshot known only within an interval.
///   * The slot's balance RISES only from an observation that is definitely newer than everything it holds. An ambiguous
///     observation is a Conflict → keep the lower value (one of them is the true latest, so the lower cannot overstate)
///     and flag <see cref="NeedsReconcile"/>; its interval joins the slot's, so the next applied observation must be
///     newer than all of them. Releases need the fill strictly before the applied observation's LOWER bound.
///   * A sequence that does not advance (replay, reconnect reset) is treated as unsequenced. The ordered stream's venue
///     time going backwards is a Conflict, not a silent drop (the dropped event could be a debit).
/// HOST CONTRACT (adapters, Tahap 2): bounds are venue times the observation is KNOWN to lie within, never local
/// receive time and never a fresh time on cached data. Corrected (C-09): stamping a snapshot with only a lower bound
/// through the point API is NOT safe — a delayed, older stream event stamped inside the real interval would overwrite
/// it. A snapshot without an exact venue as-of goes through <see cref="OnBalanceSnapshot"/> with both bounds.
/// "Never overstate" holds under this contract; the model checks in C07_* / C09_* test it with delayed streams and
/// interval snapshots.
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
    private readonly long[] _maxLo;     // largest as-of lower bound held: ending before it = definitely older
    private readonly long[] _maxHi;     // largest upper bound: an observation starting after it is definitely newer
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
        _maxLo = new long[slots];
        Array.Fill(_maxLo, long.MinValue);
        _maxHi = new long[slots];
        Array.Fill(_maxHi, long.MinValue);
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
    public BalanceUpdate OnBalance(int slot, decimal venueFree, long venueEventMs) => Apply(slot, venueFree, venueEventMs, venueEventMs, ordered: false);

    /// <summary>
    /// Venue-reported FREE balance from a snapshot whose as-of instant is only known to lie in
    /// [<paramref name="asOfNotBeforeMs"/>, <paramref name="asOfNotAfterMs"/>] (venue time): request sent minus the
    /// venue's cache lag, and the response time. It raises the balance only when it is definitely newer than everything
    /// the slot holds; it releases only fills strictly before <paramref name="asOfNotBeforeMs"/> (C-09).
    /// </summary>
    public BalanceUpdate OnBalanceSnapshot(int slot, decimal venueFree, long asOfNotBeforeMs, long asOfNotAfterMs)
    {
        if (asOfNotAfterMs < asOfNotBeforeMs)
        {
            throw new ArgumentOutOfRangeException(nameof(asOfNotAfterMs), asOfNotAfterMs, "as-of interval must satisfy notBefore ≤ notAfter");
        }

        return Apply(slot, venueFree, asOfNotBeforeMs, asOfNotAfterMs, ordered: false);
    }

    /// <summary>
    /// Venue-reported FREE balance from the slot's ordered user stream. <paramref name="streamSequence"/> is the
    /// delivery order (strictly increasing per slot, e.g. the adapter's receive counter); it orders events inside one
    /// venue millisecond. Exactly one source per slot may use this overload.
    /// </summary>
    public BalanceUpdate OnBalance(int slot, decimal venueFree, long venueEventMs, long streamSequence)
    {
        if (streamSequence <= _lastStreamSeq[slot])
        {
            return Apply(slot, venueFree, venueEventMs, venueEventMs, ordered: false); // no delivery order → venue time only
        }

        _lastStreamSeq[slot] = streamSequence;
        if (venueEventMs < _lastStreamMs[slot])
        {
            return Merge(slot, venueFree, venueEventMs, venueEventMs); // newer by delivery, older by venue time: cannot tell
        }

        _lastStreamMs[slot] = venueEventMs;
        return Apply(slot, venueFree, venueEventMs, venueEventMs, ordered: true);
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
        if (_maxLo[e.Slot] > venueEventMs)
        {
            // an observation the slot holds is as-of after the fill, so the newest delivered state reflects it; the slot's
            // value is the minimum of what it holds, never above that state
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

    private BalanceUpdate Apply(int slot, decimal venueFree, long lo, long hi, bool ordered)
    {
        if (hi < _maxLo[slot])
        {
            return BalanceUpdate.Stale; // ends before an observation the slot holds begins: definitely older
        }

        if (lo > _maxHi[slot])
        {
            // begins after everything the slot holds ends: definitely newer → it IS the balance
            _venueFree[slot] = venueFree;
            _maxLo[slot] = lo;
            _maxHi[slot] = hi;
            _orderedAtLast[slot] = ordered;
            _balanceConflict[slot] = false;
            for (int i = 0; i < _entries.Length; i++)
            {
                ref Entry e = ref _entries[i];
                if (e.Active && e.Terminal && e.Slot == slot && e.TerminalEventMs < lo)
                {
                    ReleaseAt(i);
                }
            }

            return BalanceUpdate.Applied;
        }

        if (ordered && _orderedAtLast[slot])
        {
            // the slot holds exactly one observation, the previous event of the ordered stream, in this same ms
            _venueFree[slot] = venueFree;
            return BalanceUpdate.Applied;
        }

        return Merge(slot, venueFree, lo, hi);
    }

    /// <summary>Ambiguous observation: keep the lower value, widen the slot's bounds, end same-ms ordering.</summary>
    private BalanceUpdate Merge(int slot, decimal venueFree, long lo, long hi)
    {
        _maxLo[slot] = Math.Max(_maxLo[slot], lo);
        _maxHi[slot] = Math.Max(_maxHi[slot], hi);
        _orderedAtLast[slot] = false; // even an equal value may be newer than the next same-ms stream event (C-09)
        if (venueFree == _venueFree[slot])
        {
            return BalanceUpdate.Duplicate;
        }

        if (venueFree < _venueFree[slot])
        {
            _venueFree[slot] = venueFree;
        }

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
