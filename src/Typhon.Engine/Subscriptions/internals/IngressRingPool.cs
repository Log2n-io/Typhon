using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// A ring handed out by <see cref="IngressRingPool"/>, together with the proof that the holder is the one who took it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a lease rather than the ring alone.</b> A pool that takes back a bare object cannot tell a legitimate return from a stale one: release R, let
/// another session acquire R, then release R a second time, and every check the pool can make from the object alone passes — the slot is occupied, the
/// reference matches, the free bit is clear. It would reset a ring another session is actively writing to and then publish it as free, which is the one
/// failure this pool exists to prevent. The <see cref="Generation"/> is what closes that: it is bumped on every acquire, so a stale lease names a generation
/// the slot has moved past and is rejected exactly.
/// </para>
/// <para>
/// It also keeps <see cref="IngressRing"/> a general SPSC primitive with no knowledge of any pool — the slot lives here, where it is already paired with the
/// thing that makes it meaningful.
/// </para>
/// </remarks>
internal readonly struct IngressRingLease
{
    internal IngressRingLease(IngressRing ring, int slot, uint generation)
    {
        Ring = ring;
        Slot = slot;
        Generation = generation;
    }

    /// <summary>The leased ring, or <see langword="null"/> for a lease that was never granted.</summary>
    public IngressRing Ring { get; }

    /// <summary>The pool slot backing <see cref="Ring"/>.</summary>
    internal int Slot { get; }

    /// <summary>The slot's generation at the moment it was leased. A release carrying any other generation is stale.</summary>
    internal uint Generation { get; }

    /// <summary>True when this lease names a real ring.</summary>
    public bool IsValid => Ring != null;
}

/// <summary>
/// Carves per-session <see cref="IngressRing"/> buffers out of native slabs, hands them out without waiting, and takes them back when a session closes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why slabs rather than one allocation per session.</b> Every ring must be <b>native</b> memory the engine frees deterministically: a pointer into a GC
/// array is the one thing this path may never hold (v1's <c>SendBuffer</c> used the pinned object heap, and the POH stops the GC <i>moving</i> an array, not
/// <i>freeing</i> it — the SWG x64 <c>0x80131506</c> crash). One slab backing many rings makes that ownership single and obvious: the slab is a resource
/// child, <c>base.Dispose</c> frees it, and no ring owns anything.
/// </para>
/// <para>
/// <b>Exhaustion refuses admission; it never waits and never throws.</b> This is where this pool and <c>StagingBufferPool</c> part company: that one blocks on
/// a <see cref="SemaphoreSlim"/> because a checkpoint can afford to wait for a buffer and its caller is an engine thread. Here the caller is admitting a client
/// connection, so the honest answer to "no room" is to refuse the session at connect time — cheap, visible and bounded — rather than queue a connection the
/// engine cannot serve. <see cref="SpilloverRingPool"/> is the shape followed: hand back nothing, and count it.
/// </para>
/// <para>
/// <b>Why a lock, and why it costs nothing that matters.</b> Acquire, release and dispose all run under one lock. They happen at <i>session lifecycle</i> —
/// a connection opening or closing — never per command and never on the tick path: a live session's producer and consumer touch only its own ring, which is
/// lock-free SPSC and knows nothing about this type. A first draft made claim and release lock-free over an <see cref="Interlocked"/> bitmap, and bought two
/// real defects for that cleverness: a stale release could reset a ring another session was writing to, and a claim racing <see cref="Dispose"/> could return
/// a ring whose slab had just been freed — a use-after-free on a native pointer, the exact class the design forbids. Correctness at session open is worth
/// more than lock-freedom there.
/// </para>
/// <para>
/// <b>The bitmap is still the free list</b> ([05-ingress-rings § 4.1] specifies <c>StagingBufferPool</c>'s slicing), now read under the lock rather than by
/// <see cref="Interlocked"/>, with a rotating start hint so a large pool does not rescan from word zero on every acquire.
/// </para>
/// </remarks>
internal sealed unsafe class IngressRingPool : ResourceNode, IMemoryResource
{
    /// <summary>Smallest slab taken from the allocator. A ring larger than this gets a slab of its own.</summary>
    private const int MinSlabBytes = 64 * 1024;

    /// <summary>Slabs are cache-line aligned, so every ring's cursors start on a line boundary rather than straddling one.</summary>
    private const int SlabAlignment = 64;

    private readonly IMemoryAllocator _allocator;
    private readonly List<PinnedMemoryBlock> _slabs = [];
    private readonly Lock _lock = new();
    private readonly int _ringBytes;
    private readonly int _ringsPerSlab;
    private readonly int _slabBytes;
    private readonly long _budgetBytes;

    private IngressRing[] _rings = [];
    private uint[] _generation = [];
    private ulong[] _freeMap = [];
    private int _searchHint;
    private long _committedBytes;
    private int _carvedCount;
    private int _freeSlotCount;
    private long _acquired;
    private long _exhausted;
    private long _inUse;
    private bool _disposed;

    /// <summary>Creates a pool. Nothing is allocated here — the first <see cref="TryAcquire"/> commits the first slab.</summary>
    /// <param name="id">Stable resource id, unique among <paramref name="parent"/>'s children.</param>
    /// <param name="parent">Resource-graph parent; the pool registers under it and its slabs register under the pool.</param>
    /// <param name="allocator">Engine allocator.</param>
    /// <param name="options">Operator configuration; supplies the ring size and the budget ceiling.</param>
    public IngressRingPool(string id, IResource parent, IMemoryAllocator allocator, SubscriptionsOptions options)
        // FailFast is the closest of the five: its own doc names "the caller can handle failure gracefully", "queueing would make the problem worse" and
        // "edge capacity (client-facing)" — exactly admission. The one word that does not fit is "throw": this pool returns false and counts instead, because
        // the caller is a connection handshake, not an engine thread.
        : base(id, ResourceType.Allocator, parent, ExhaustionPolicy.FailFast)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(options);

        // The ring's own constructor enforces this too, but a misconfigured option must fail at Start with the option's name on it, not later on the first
        // connection with the ring's.
        if (options.IngressRingBytes < 64 || (options.IngressRingBytes & (options.IngressRingBytes - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.IngressRingBytes,
                $"{nameof(SubscriptionsOptions.IngressRingBytes)} must be a power of two and at least 64");
        }

        // Negative would disable the pool while every refusal looked like ordinary exhaustion. Zero is legal and means exactly "accept no sessions".
        if (options.IngressPoolBudgetBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.IngressPoolBudgetBytes,
                $"{nameof(SubscriptionsOptions.IngressPoolBudgetBytes)} cannot be negative; 0 admits no sessions");
        }

        _allocator = allocator;
        _ringBytes = options.IngressRingBytes;
        _budgetBytes = options.IngressPoolBudgetBytes;

        // Exact, not rounded: both values are powers of two and the ring is validated above, so the division has no remainder to lose. The Max(1, ...) is for
        // a ring LARGER than the slab floor, which then gets a slab of its own.
        _ringsPerSlab = Math.Max(1, MinSlabBytes / _ringBytes);
        _slabBytes = _ringsPerSlab * _ringBytes;

        // Slab-granular, because growth is: a budget that cannot cover a whole further slab yields no further rings, however many ring-sized holes remain in
        // it. Computing this as budget/ringBytes overstates the cap for any budget that is not a slab multiple — 100 000 B at 4 KiB rings would claim 24 and
        // deliver 16 — and that number is the documented concurrent-session cap, so it has to be the true one.
        MaxRingCount = (int)Math.Min(int.MaxValue, _budgetBytes / _slabBytes * _ringsPerSlab);
    }

    /// <summary>
    /// Rings this pool can ever carve, and so the hard cap on concurrent sessions. Slab-granular: a partial slab at the end of the budget is unusable.
    /// </summary>
    public int MaxRingCount { get; }

    /// <summary>Capacity of each ring handed out, in bytes.</summary>
    public int RingBytes => _ringBytes;

    /// <summary>Ceiling on committed bytes.</summary>
    public long BudgetBytes => _budgetBytes;

    /// <summary>Bytes currently committed in slabs. Never exceeds <see cref="BudgetBytes"/>; returns to zero on <see cref="Dispose"/>.</summary>
    public long CommittedBytes => Volatile.Read(ref _committedBytes);

    /// <summary>Rings carved so far, held and free together.</summary>
    public int CarvedCount => Volatile.Read(ref _carvedCount);

    /// <summary>Carved rings currently available.</summary>
    public int FreeCount => Volatile.Read(ref _freeSlotCount);

    /// <summary>Rings currently held by a session.</summary>
    public long InUseCount => Interlocked.Read(ref _inUse);

    /// <summary>Successful acquires over the pool's life. Monotonic.</summary>
    public long AcquiredCount => Interlocked.Read(ref _acquired);

    /// <summary>Acquires refused because the budget bound. Monotonic; each one is a session that was not admitted.</summary>
    public long ExhaustedCount => Interlocked.Read(ref _exhausted);

    /// <summary>Fraction of the budget committed.</summary>
    public double Utilization => _budgetBytes <= 0 ? 0d : (double)Volatile.Read(ref _committedBytes) / _budgetBytes;

    /// <inheritdoc />
    public override int? Count => CarvedCount;

    /// <inheritdoc />
    /// <remarks>The slabs are children of this node and are accounted separately; this covers only the pool's own tables.</remarks>
    public int EstimatedMemorySize
    {
        get
        {
            lock (_lock)
            {
                return 64 + (_rings.Length * IntPtr.Size) + (_freeMap.Length * sizeof(ulong)) + (_generation.Length * sizeof(uint))
                    + (_slabs.Count * IntPtr.Size);
            }
        }
    }

    /// <summary>
    /// Takes a ring for a session, committing a slab if none is free and the budget allows.
    /// </summary>
    /// <param name="lease">The granted lease, or an invalid one when the budget binds or the pool is disposed.</param>
    /// <returns>
    /// <see langword="false"/> when no ring could be handed out. Never waits on another session and never throws for exhaustion — the caller refuses
    /// admission.
    /// </returns>
    public bool TryAcquire(out IngressRingLease lease)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                lease = default;
                return false;
            }

            var slot = TryClaimFreeSlot();
            if (slot < 0)
            {
                if (!TryGrow())
                {
                    _exhausted++;
                    lease = default;
                    return false;
                }

                slot = TryClaimFreeSlot();
                if (slot < 0)
                {
                    // TryGrow reported success, so a slot exists; reaching here would mean the bitmap and the carve disagree.
                    throw new InvalidOperationException("IngressRingPool grew but produced no free slot; the free map and the carve are out of step.");
                }
            }

            // Bumped on every grant, so a lease from a previous tenancy of this slot is distinguishable from the live one.
            var generation = ++_generation[slot];
            _acquired++;
            _inUse++;
            lease = new IngressRingLease(_rings[slot], slot, generation);
            return true;
        }
    }

    /// <summary>Returns a ring to the pool, resetting it so the next session inherits nothing from the last.</summary>
    /// <param name="lease">The lease granted by <see cref="TryAcquire"/>.</param>
    /// <exception cref="ArgumentException">
    /// The lease is invalid, names no slot of this pool, or is stale — a second release of a ring another session has since taken. Tolerating any of those
    /// would hand one buffer to two sessions.
    /// </exception>
    public void Release(in IngressRingLease lease)
    {
        if (!lease.IsValid)
        {
            throw new ArgumentException("Lease names no ring.", nameof(lease));
        }

        lock (_lock)
        {
            // Disposal is a legitimate teardown order: the pool can go before its sessions are all closed, and a release arriving afterwards has nothing left
            // to return. Throwing here would turn an ordering detail into a shutdown failure.
            if (_disposed)
            {
                return;
            }

            if (lease.Slot < 0 || lease.Slot >= _carvedCount || !ReferenceEquals(_rings[lease.Slot], lease.Ring))
            {
                throw new ArgumentException("Lease was not granted by this pool.", nameof(lease));
            }

            // The generation is what makes a double release detectable at all. Without it, releasing a ring that a later session has already taken looks
            // exactly like a legitimate return: the slot is occupied, the reference matches, and the free bit is clear.
            if (_generation[lease.Slot] != lease.Generation)
            {
                throw new ArgumentException(
                    "Lease is stale; the ring has since been granted to another session. Releasing it would hand one buffer to two sessions.", nameof(lease));
            }

            var wordIndex = lease.Slot >> 6;
            var mask = 1UL << (lease.Slot & 0x3F);
            if ((_freeMap[wordIndex] & mask) != 0)
            {
                throw new ArgumentException("Ring is already free; returning it twice would hand one buffer to two sessions.", nameof(lease));
            }

            // Reset before publishing the slot as free, so no acquirer can see a ring still carrying the last session's bytes.
            lease.Ring.Reset();

            // Bumped again on release, so the lease just consumed cannot be replayed even before the slot is re-granted.
            _generation[lease.Slot]++;
            _freeMap[wordIndex] |= mask;
            _freeSlotCount++;
            _inUse--;
        }
    }

    /// <summary>Finds and claims the first free slot, or returns <c>-1</c>. Caller holds <see cref="_lock"/>.</summary>
    /// <remarks>
    /// The scan starts where the last one stopped. At the default budget the map is 256 words, and restarting at word zero每 acquire would make admission
    /// O(words) once the low slots are taken.
    /// </remarks>
    private int TryClaimFreeSlot()
    {
        if (_freeSlotCount == 0)
        {
            return -1;
        }

        var wordCount = _freeMap.Length;
        for (var i = 0; i < wordCount; i++)
        {
            var wordIndex = (_searchHint + i) % wordCount;
            var word = _freeMap[wordIndex];
            if (word == 0)
            {
                continue;
            }

            var bitIndex = BitOperations.TrailingZeroCount(word);
            _freeMap[wordIndex] = word & ~(1UL << bitIndex);
            _freeSlotCount--;
            _searchHint = wordIndex;
            return (wordIndex << 6) + bitIndex;
        }

        return -1;
    }

    /// <summary>Commits one slab, constructs its rings and publishes them. Caller holds <see cref="_lock"/>.</summary>
    private bool TryGrow()
    {
        // The whole slab is checked against the budget before the allocator is called, so the pool never commits bytes it then has to give back.
        if (_committedBytes + _slabBytes > _budgetBytes || _carvedCount >= MaxRingCount)
        {
            return false;
        }

        var carveCount = Math.Min(_ringsPerSlab, MaxRingCount - _carvedCount);
        if (carveCount <= 0)
        {
            return false;
        }

        // Tables grow with the slabs rather than being sized to the budget up front. Pre-sizing would allocate one reference plus a generation per ring the
        // budget COULD ever hold, before a single session connects — 128 KiB straight to the LOH at the default, and gigabytes for an outsized budget that
        // commits nothing. Growing them here is safe because every reader and writer of these tables holds the lock.
        EnsureTableCapacity(_carvedCount + carveCount);

        // zeroed: false — a ring's contents are meaningless until its producer frames a record, and Reset clears the cursors that decide what is readable.
        var slab = _allocator.AllocatePinned($"Slab-{_slabs.Count}", this, _slabBytes, false, SlabAlignment);
        _slabs.Add(slab);
        _committedBytes += _slabBytes;

        var data = slab.DataAsPointer;
        var firstSlot = _carvedCount;
        for (var i = 0; i < carveCount; i++)
        {
            var slot = firstSlot + i;
            _rings[slot] = new IngressRing(data + ((long)i * _ringBytes), _ringBytes);
            _freeMap[slot >> 6] |= 1UL << (slot & 0x3F);
        }

        _carvedCount += carveCount;
        _freeSlotCount += carveCount;
        return true;
    }

    /// <summary>Grows the ring, generation and free-map tables to hold at least <paramref name="required"/> slots. Caller holds <see cref="_lock"/>.</summary>
    private void EnsureTableCapacity(int required)
    {
        if (_rings.Length >= required)
        {
            return;
        }

        var capacity = Math.Max(_ringsPerSlab, _rings.Length == 0 ? _ringsPerSlab : _rings.Length * 2);
        while (capacity < required)
        {
            capacity *= 2;
        }

        capacity = Math.Min(capacity, MaxRingCount);
        Array.Resize(ref _rings, capacity);
        Array.Resize(ref _generation, capacity);
        Array.Resize(ref _freeMap, (capacity + 63) >> 6);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Drop every slot before the slabs go: nothing may hand out, or reclaim into, memory that is about to be freed. A session still holding a ring is
            // a lifecycle bug, but it must not become a use-after-free here — Release above returns quietly once _disposed is set.
            Array.Clear(_freeMap);
            Array.Clear(_rings);
            Array.Clear(_generation);
            _freeSlotCount = 0;
            _carvedCount = 0;
            _committedBytes = 0;
            _searchHint = 0;
            _slabs.Clear();
        }

        // ResourceNode.Dispose frees the slabs, which are children of this node.
        base.Dispose(disposing);

        // Last, so the tree stays walkable while the children dispose — and so a pool recreated under the same id is not silently absent from the tree.
        Parent?.RemoveChild(this);
    }
}
