using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// A block of native memory holding one encoded frame, together with the capacity the pool handed out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Native by construction.</b> <see cref="Bytes"/> points into a slab taken through <see cref="IMemoryAllocator.AllocatePinned"/> — never into a managed
/// object — so a link can wrap it with <see cref="NativeFrameMemoryManager"/> and hand the resulting <see cref="System.ReadOnlyMemory{T}"/> to a socket with
/// no pinning anywhere on the path.
/// </para>
/// <para>
/// <b>It carries the capacity, not the length.</b> How many of those bytes a frame actually used is the session's business and lives in its send slot; the
/// pool only ever needs to know which size class the block came from, and <see cref="Capacity"/> is exactly that class's size.
/// </para>
/// </remarks>
internal readonly unsafe struct FrameBlock
{
    internal FrameBlock(byte* bytes, int capacity)
    {
        Bytes = bytes;
        Capacity = capacity;
    }

    /// <summary>The first byte of the frame. Native memory owned by the pool.</summary>
    public byte* Bytes { get; }

    /// <summary>The block's size class, in bytes. The largest frame it can hold.</summary>
    public int Capacity { get; }

    /// <summary>True when this names a real block. A default instance does not.</summary>
    public bool IsValid => Bytes != null;
}

/// <summary>
/// The 64 bytes in front of every frame block: the pool's own bookkeeping, never part of the frame.
/// </summary>
/// <remarks>
/// A cache line, so the payload behind it starts on a line boundary: a frame is memcpy'd in and then read by a socket, and both want an aligned start. The
/// fields are the same three <see cref="ReplicationBlockPool"/> keeps, for the same reasons — an intrusive free link so recycling needs no side table, and a
/// pool state that makes a double return detectable rather than a silently self-linked list.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = FramePool.BlockHeaderBytes)]
internal struct FrameBlockHeader
{
    /// <summary>Next block on this class's free list, or zero for the end.</summary>
    public nint NextFree;

    /// <summary>
    /// The size class's payload bytes. Validated against the returned <see cref="FrameBlock"/>, so a foreign block cannot enter the free list.
    /// </summary>
    public int Capacity;

    /// <summary>Which free list this block belongs to.</summary>
    public int ClassIndex;

    /// <summary>Rented or free, as the pool sees it.</summary>
    public int PoolState;
}

/// <summary>
/// Native memory for encoded frames, carved into six size classes and bounded by <see cref="SubscriptionsOptions.FramePoolBudgetBytes"/>. Exhaustion skips
/// and counts: the pool never allocates past its budget, never waits and never throws for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Six classes, because a frame's size is known only after it is encoded.</b> A session encodes into a per-worker scratch buffer and then copies the bytes
/// it actually produced into a pooled block of the right class ([02-execution § 5]). Reserving each session its maximum frame instead would cost
/// <c>2 × FrameBytes</c> per session — 512 KiB each at the default, half a gigabyte for a thousand sessions — for frames a few hundred bytes wide in the
/// common case. The classes are 512 B, 2 KiB, 8 KiB, 32 KiB, 128 KiB and 256 KiB: a 4× ladder, so the worst-case internal waste is 75 % of one block and
/// the realistic waste far less, against six free lists to keep rather than one.
/// </para>
/// <para>
/// <b>A request above the largest class is refused, never served.</b> 256 KiB is <see cref="SubscriptionsOptions.FrameBytes"/>'s own default and is exported
/// in the catalog so an SDK sizes its receive buffer from it. Quietly allocating a larger block would send a client more than the buffer it has already
/// sized, which is a protocol violation dressed as a convenience — so <see cref="TryRent"/> answers <see langword="false"/> and counts it separately from
/// budget exhaustion, because the two mean completely different things to an operator: one is a sizing problem, the other is a bug upstream.
/// </para>
/// <para>
/// <b>Exhaustion skips and counts.</b> When the budget binds, <see cref="TryRent"/> returns <see langword="false"/> and increments
/// <see cref="BudgetSkipCount"/>; the caller skips that session for the tick and the session converges on its next frame, because records are absolute
/// (SUB-03). It never evicts another session's frame — those bytes may be on a socket — and it never allocates "just this once", which is the failure mode
/// this budget exists to prevent: the watched set is driven by untrusted client behaviour, so an unbounded frame pool is a memory-exhaustion path the
/// application cannot close.
/// </para>
/// <para>
/// <b>Why one lock is enough, and where the traffic actually is.</b> Frames are produced by per-worker chunks, so rent and return are genuinely concurrent,
/// and this pool takes the same shape <see cref="IngressRingPool"/> settled on: one <see cref="Lock"/> over every free list and over growth. What makes that
/// cheap is <see cref="TryRentOrKeep"/>: a session's frame size is stable tick to tick, so after its first two frames the block already in the slot is
/// already the right class and is reused in place, touching no lock at all. Pool traffic is therefore a session-lifecycle event — open, close, a change of
/// size class — not a per-frame one, exactly as ring acquisition is. Nothing here spins waiting for a tick or for I/O.
/// </para>
/// <para>
/// <b>The budget is a ceiling, not a reservation.</b> Nothing is committed at construction; a class commits its first slab on its first rent and grows one
/// slab at a time while the budget allows. Committed bytes include the 64 B header in front of each block, because that is memory the pool really holds.
/// </para>
/// </remarks>
internal sealed unsafe class FramePool : ResourceNode, IMemoryResource, IMetricSource
{
    /// <summary>The pool's own bookkeeping in front of each block. One cache line, so payloads stay line-aligned.</summary>
    internal const int BlockHeaderBytes = 64;

    /// <summary>Smallest slab taken from the allocator. A block larger than this gets a slab of its own.</summary>
    private const int MinSlabBytes = 64 * 1024;

    /// <summary>Slabs are cache-line aligned, which together with the class sizes keeps every header and every payload aligned.</summary>
    private const int SlabAlignment = 64;

    /// <summary>Sentinel for "no block". A carved block never sits at address zero.</summary>
    private const nint FreeListEnd = 0;

    /// <summary>The pool considers this block handed out.</summary>
    private const int PoolStateRented = 0;

    /// <summary>The pool considers this block to be on a free list. Its own field, so a double return is caught rather than self-linking the list.</summary>
    private const int PoolStateFree = 1;

    /// <summary>
    /// The size classes, ascending. Every one is a multiple of <see cref="SlabAlignment"/>, so a stride of header + class keeps both aligned.
    /// </summary>
    private static readonly int[] SizeClassBytes = [512, 2 * 1024, 8 * 1024, 32 * 1024, 128 * 1024, 256 * 1024];

    private readonly IMemoryAllocator _allocator;
    private readonly List<PinnedMemoryBlock> _slabs = [];
    private readonly Lock _lock = new();
    private readonly long _budgetBytes;

    private readonly nint[] _freeHeads = new nint[SizeClassBytes.Length];
    private readonly int[] _blockStride = new int[SizeClassBytes.Length];
    private readonly int[] _blocksPerSlab = new int[SizeClassBytes.Length];
    private readonly int[] _slabBytes = new int[SizeClassBytes.Length];
    private readonly int[] _blockCounts = new int[SizeClassBytes.Length];
    private readonly int[] _freeCounts = new int[SizeClassBytes.Length];

    private long _committedBytes;
    private long _peakCommittedBytes;
    private long _rentTotal;
    private long _budgetSkips;
    private long _oversizeRefusals;
    private int _blockCount;
    private int _rentedCount;
    private bool _disposed;

    /// <summary>Creates a pool. Nothing is allocated here — the first <see cref="TryRent"/> of a class commits that class's first slab.</summary>
    /// <param name="id">Stable resource id, unique among <paramref name="parent"/>'s children.</param>
    /// <param name="parent">
    /// Resource-graph parent; the pool registers under it and its slabs register under the pool. Production passes the Runtime node.
    /// </param>
    /// <param name="allocator">Engine allocator, supplied by DI exactly as the other replication pools take it.</param>
    /// <param name="options">Operator configuration; <see cref="SubscriptionsOptions.FramePoolBudgetBytes"/> is the ceiling this pool honours.</param>
    public FramePool(string id, IResource parent, IMemoryAllocator allocator, SubscriptionsOptions options)
        // Degrade, not Evict: the answer to a bound budget is to serve a session less this tick, not to take back a block whose bytes may be on a socket.
        // The fallback exists and is correct — a skipped frame costs nothing permanent, because the next frame carries everything that changed.
        : base(id, ResourceType.Allocator, parent, ExhaustionPolicy.Degrade)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(options);

        // Negative would disable the pool while every refusal looked like ordinary exhaustion. Zero is legal and means exactly "produce no frames", which is
        // the right outcome for an operator who sized it to zero: sessions are skipped and counted, and nothing is broken.
        if (options.FramePoolBudgetBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FramePoolBudgetBytes,
                $"{nameof(SubscriptionsOptions.FramePoolBudgetBytes)} cannot be negative; 0 produces no frames");
        }

        _allocator = allocator;
        _budgetBytes = options.FramePoolBudgetBytes;

        for (var i = 0; i < SizeClassBytes.Length; i++)
        {
            _freeHeads[i] = FreeListEnd;
            _blockStride[i] = BlockHeaderBytes + SizeClassBytes[i];

            // Round UP: the slab floor is a minimum, so rounding down would hand out slabs below it. A class wider than the floor gets one block per slab.
            _blocksPerSlab[i] = Math.Max(1, (MinSlabBytes + _blockStride[i] - 1) / _blockStride[i]);
            _slabBytes[i] = _blocksPerSlab[i] * _blockStride[i];
        }
    }

    /// <summary>How many size classes there are.</summary>
    public static int SizeClassCount => SizeClassBytes.Length;

    /// <summary>The payload size of one class, in bytes.</summary>
    /// <param name="classIndex">A class index, as <see cref="ClassIndexFor"/> returns.</param>
    public static int ClassBytes(int classIndex) => SizeClassBytes[classIndex];

    /// <summary>The largest frame this pool will ever hand out.</summary>
    public static int LargestClassBytes => SizeClassBytes[^1];

    /// <summary>Ceiling on committed bytes.</summary>
    public long BudgetBytes => _budgetBytes;

    /// <summary>
    /// Bytes currently committed in slabs, headers included. Never exceeds <see cref="BudgetBytes"/>; returns to zero on <see cref="Dispose"/>.
    /// </summary>
    public long CommittedBytes => Volatile.Read(ref _committedBytes);

    /// <summary>Blocks carved so far across every class, rented and free together.</summary>
    public int BlockCount => Volatile.Read(ref _blockCount);

    /// <summary>Blocks currently held by a session.</summary>
    public int RentedCount => Volatile.Read(ref _rentedCount);

    /// <summary>Successful rents over the pool's life. Monotonic.</summary>
    public long RentCount => Interlocked.Read(ref _rentTotal);

    /// <summary>
    /// Rents refused because the budget bound. Monotonic; each one is a session skipped for that tick.
    /// </summary>
    public long BudgetSkipCount => Interlocked.Read(ref _budgetSkips);

    /// <summary>
    /// Rents refused because the request was larger than <see cref="LargestClassBytes"/>, or not positive. Monotonic and counted apart from
    /// <see cref="BudgetSkipCount"/>: a bound budget is a sizing decision, an over-sized frame is a defect upstream.
    /// </summary>
    public long OversizeRefusalCount => Interlocked.Read(ref _oversizeRefusals);

    /// <summary>Fraction of the budget committed. Crossing 0.8 is the resource graph's default health threshold.</summary>
    public double Utilization => _budgetBytes <= 0 ? 0d : (double)Volatile.Read(ref _committedBytes) / _budgetBytes;

    /// <inheritdoc />
    public override int? Count => BlockCount;

    /// <inheritdoc />
    /// <remarks>Bookkeeping only. The slabs are children of this node and are accounted separately, per the interface contract.</remarks>
    public int EstimatedMemorySize
    {
        get
        {
            lock (_lock)
            {
                return 64 + (SizeClassBytes.Length * 6 * sizeof(int)) + (_slabs.Count * IntPtr.Size);
            }
        }
    }

    /// <summary>
    /// The class that would serve <paramref name="byteCount"/>, or <c>-1</c> when no class can.
    /// </summary>
    /// <param name="byteCount">How many bytes the frame needs.</param>
    /// <returns>
    /// The smallest class index whose size covers the request; <c>-1</c> for a non-positive request or one above <see cref="LargestClassBytes"/>.
    /// </returns>
    /// <remarks>
    /// Six compares. It runs once per frame that is not already sitting in the right class, which after a session's first two frames is rare.
    /// </remarks>
    public static int ClassIndexFor(int byteCount)
    {
        if (byteCount <= 0)
        {
            return -1;
        }

        for (var i = 0; i < SizeClassBytes.Length; i++)
        {
            if (byteCount <= SizeClassBytes[i])
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Takes a block big enough for <paramref name="byteCount"/> bytes, committing a slab if that class has none free and the budget allows.
    /// </summary>
    /// <param name="byteCount">The frame's encoded length. Rounded up to a size class.</param>
    /// <param name="block">The rented block, or an invalid one when the request is refused.</param>
    /// <returns>
    /// <see langword="false"/> when the budget binds, the request is above the largest class, or the pool is disposed. Never allocates past the budget, never
    /// waits on a tick and never throws for any of them — the caller skips the session.
    /// </returns>
    public bool TryRent(int byteCount, out FrameBlock block)
    {
        var classIndex = ClassIndexFor(byteCount);
        if (classIndex < 0)
        {
            // Outside the lock: an over-sized request is a defect, not contention, and it must not queue behind the pool's real traffic.
            Interlocked.Increment(ref _oversizeRefusals);
            block = default;
            return false;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                block = default;
                return false;
            }

            if (_freeHeads[classIndex] == FreeListEnd && !TryGrow(classIndex))
            {
                _budgetSkips++;
                block = default;
                return false;
            }

            var header = (FrameBlockHeader*)_freeHeads[classIndex];
            _freeHeads[classIndex] = header->NextFree;
            _freeCounts[classIndex]--;

            header->NextFree = FreeListEnd;
            header->PoolState = PoolStateRented;

            _rentTotal++;
            _rentedCount++;

            block = new FrameBlock((byte*)header + BlockHeaderBytes, header->Capacity);
            return true;
        }
    }

    /// <summary>
    /// Keeps <paramref name="block"/> when it is already the right size class for <paramref name="byteCount"/>, and rents a replacement otherwise.
    /// </summary>
    /// <param name="byteCount">The frame's encoded length.</param>
    /// <param name="block">On entry, the block already in the session's slot (may be invalid). On success, the block to encode into.</param>
    /// <param name="previous">
    /// The block that must be returned to the pool, or an invalid one when <paramref name="block"/> was kept or nothing was replaced.
    /// </param>
    /// <returns><see langword="false"/> when a replacement was needed and could not be rented. <paramref name="block"/> is then left untouched.</returns>
    /// <remarks>
    /// <para>
    /// This is the method that keeps the lock off the per-frame path, and it is worth stating as a property rather than as an optimisation: a session's frame
    /// size is stable from tick to tick, so the block handed back by <c>SessionSendState.TryBeginFrame</c> is nearly always the class the next frame needs.
    /// Kept blocks are deliberately invisible to every counter here — the pool was not involved.
    /// </para>
    /// <para>
    /// The old block is handed back rather than returned here, because returning it before the replacement is secured would let another session take it and
    /// leave this one with nothing at all — a skip caused by the pool's own bookkeeping rather than by the budget.
    /// </para>
    /// </remarks>
    public bool TryRentOrKeep(int byteCount, ref FrameBlock block, out FrameBlock previous)
    {
        var classIndex = ClassIndexFor(byteCount);
        if (classIndex >= 0 && block.IsValid && block.Capacity == SizeClassBytes[classIndex])
        {
            previous = default;
            return true;
        }

        if (!TryRent(byteCount, out var rented))
        {
            previous = default;
            return false;
        }

        previous = block;
        block = rented;
        return true;
    }

    /// <summary>Returns a block to its class's free list.</summary>
    /// <param name="block">A block this pool handed out and that nothing is still reading.</param>
    /// <exception cref="ArgumentException">
    /// The block is invalid, is already on a free list, or does not describe a block of this pool. Each of those would put one buffer in two places, which no
    /// later check could untangle.
    /// </exception>
    public void Return(in FrameBlock block)
    {
        if (!block.IsValid)
        {
            throw new ArgumentException("Block names no memory.", nameof(block));
        }

        var header = (FrameBlockHeader*)(block.Bytes - BlockHeaderBytes);

        lock (_lock)
        {
            // Disposal is a legitimate teardown order: the pool can go before every session has closed, and a return arriving afterwards has nothing left to
            // give back. Throwing here would turn an ordering detail into a shutdown failure.
            if (_disposed)
            {
                return;
            }

            if (header->Capacity != block.Capacity || (uint)header->ClassIndex >= (uint)SizeClassBytes.Length
                || SizeClassBytes[header->ClassIndex] != block.Capacity)
            {
                throw new ArgumentException("Block was not handed out by this pool; its header does not describe the block being returned.", nameof(block));
            }

            if (header->PoolState == PoolStateFree)
            {
                throw new ArgumentException("Block is already on the free list; returning it twice would corrupt the list.", nameof(block));
            }

            var classIndex = header->ClassIndex;
            header->PoolState = PoolStateFree;
            header->NextFree = _freeHeads[classIndex];
            _freeHeads[classIndex] = (nint)header;
            _freeCounts[classIndex]++;
            _rentedCount--;
        }
    }

    /// <summary>Blocks carved for one class, rented and free together.</summary>
    /// <param name="classIndex">A class index.</param>
    public int BlocksInClass(int classIndex)
    {
        lock (_lock)
        {
            return _blockCounts[classIndex];
        }
    }

    /// <summary>Blocks of one class currently on its free list.</summary>
    /// <param name="classIndex">A class index.</param>
    public int FreeInClass(int classIndex)
    {
        lock (_lock)
        {
            return _freeCounts[classIndex];
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Read without the lock, per the interface contract: these are diagnostics sampled every few seconds, and a value one rent stale is worth far more than
    /// a snapshot thread that can contend with a tick.
    /// </remarks>
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteMemory(Volatile.Read(ref _committedBytes), Volatile.Read(ref _peakCommittedBytes));
        writer.WriteCapacity(Volatile.Read(ref _rentedCount), Volatile.Read(ref _blockCount));
        writer.WriteThroughput("FramesRented", Interlocked.Read(ref _rentTotal));
        writer.WriteThroughput("BudgetSkips", Interlocked.Read(ref _budgetSkips));
        writer.WriteThroughput("OversizeRefusals", Interlocked.Read(ref _oversizeRefusals));
    }

    /// <inheritdoc />
    public void ResetPeaks() => Volatile.Write(ref _peakCommittedBytes, Volatile.Read(ref _committedBytes));

    /// <summary>
    /// Commits one slab for a class and threads its blocks onto that class's free list, or reports that the budget forbids it.
    /// </summary>
    /// <remarks>
    /// The budget is checked against the whole slab before the allocator is called, so the pool never commits bytes it then has to give back — the check is
    /// what makes "never allocate beyond the budget" a property of this method rather than of its callers. Caller holds <see cref="_lock"/>.
    /// </remarks>
    private bool TryGrow(int classIndex)
    {
        var slabBytes = _slabBytes[classIndex];
        if (_committedBytes + slabBytes > _budgetBytes)
        {
            return false;
        }

        // zeroed: false — the header is written below and the payload is overwritten by the frame that rents it. Zeroing whole slabs would touch every frame
        // byte for no benefit, and a frame is only ever read up to the length its session published.
        var slab = _allocator.AllocatePinned($"Slab-{classIndex}-{_slabs.Count}", this, slabBytes, false, SlabAlignment);
        _slabs.Add(slab);

        Volatile.Write(ref _committedBytes, _committedBytes + slabBytes);
        if (_committedBytes > _peakCommittedBytes)
        {
            Volatile.Write(ref _peakCommittedBytes, _committedBytes);
        }

        // Threaded in REVERSE so the LIFO free list hands blocks out in ascending address order: a run of freshly rented blocks then walks forward, with the
        // prefetcher rather than against it.
        var data = slab.DataAsPointer;
        var stride = _blockStride[classIndex];
        var capacity = SizeClassBytes[classIndex];
        for (var i = _blocksPerSlab[classIndex] - 1; i >= 0; i--)
        {
            var header = (FrameBlockHeader*)(data + (i * stride));
            header->Capacity = capacity;
            header->ClassIndex = classIndex;
            header->PoolState = PoolStateFree;
            header->NextFree = _freeHeads[classIndex];
            _freeHeads[classIndex] = (nint)header;
        }

        _blockCounts[classIndex] += _blocksPerSlab[classIndex];
        _freeCounts[classIndex] += _blocksPerSlab[classIndex];
        Volatile.Write(ref _blockCount, _blockCount + _blocksPerSlab[classIndex]);
        return true;
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

            // Drop every free head before the slabs go, so no stale pointer outlives the memory it names. Every counter resets with it: leaving
            // _committedBytes behind would let a post-dispose rent pass the budget check and register a fresh slab under a node whose children have already
            // been cleared, leaking it outright.
            Array.Clear(_freeHeads);
            Array.Clear(_blockCounts);
            Array.Clear(_freeCounts);
            Volatile.Write(ref _committedBytes, 0);
            Volatile.Write(ref _blockCount, 0);
            Volatile.Write(ref _rentedCount, 0);
            _slabs.Clear();
        }

        // ResourceNode.Dispose frees the slabs, which are children of this node.
        base.Dispose(disposing);

        // Last, so the tree stays walkable while the children dispose — and so a pool recreated under the same id is not silently absent from the tree.
        Parent?.RemoveChild(this);
    }
}
