using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// Fixed-size native block pool backing replication state — one per replicated archetype. Blocks are carved from slabs taken through
/// <see cref="IMemoryAllocator.AllocatePinned"/> and recycled through an intrusive free list threaded via <see cref="ReplicationBlockHeader.NextFree"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One size class, no fragmentation.</b> An archetype's slot count and declared groups are fixed at <c>Start</c>, so every block in a pool is identical
/// and a single free list is enough. Taking one <c>AllocatePinned</c> per cluster instead would be thousands of allocator round-trips per tick at scale,
/// which is why slabs are carved rather than blocks allocated.
/// </para>
/// <para>
/// <b>The budget is a ceiling, not a reservation.</b> Nothing is committed at construction; the first rent takes one slab, and the pool grows one slab at a
/// time while the budget allows. A pool whose budget cannot cover a single slab is legal and simply never rents — the subsystem is then disabled rather
/// than broken, which is the right outcome for an operator who sized it to zero.
/// </para>
/// <para>
/// <b>Exhaustion never throws and never overshoots.</b> <see cref="TryRent"/> returns <see langword="false"/> when the budget binds; it is the caller's job
/// to evict idle blocks and then degrade sessions, in that order. Allocating past the budget on the tick path, or throwing there, are both forbidden —
/// hence the <see cref="ExhaustionPolicy.Evict"/> classification on this node. A <i>programming</i> error is different: returning a block twice, or using a
/// disposed pool, throws, because those cannot be recovered from and silently tolerating them corrupts the free list.
/// </para>
/// <para>
/// <b>Thread safety: none, by contract.</b> The pool is mutated only at the replication track's prologue and its single-threaded "blocks" step, each
/// followed by a worker dispatch that publishes the writes. There is deliberately no lock and no CAS here; adding a concurrent caller would be a design
/// change, not a tuning change.
/// </para>
/// </remarks>
internal sealed unsafe class ReplicationBlockPool : ResourceNode, IMemoryResource
{
    /// <summary>Smallest slab taken from the allocator. A block larger than this gets a slab of its own.</summary>
    private const int MinSlabBytes = 64 * 1024;

    /// <summary>Slabs are cache-line aligned, which together with <see cref="ReplicationBlockLayout.BlockStride"/> keeps every block's hot region aligned.</summary>
    private const int SlabAlignment = 64;

    /// <summary>Sentinel for "no block". A carved block never sits at address zero.</summary>
    private const nint FreeListEnd = 0;

    /// <summary>A rented block that no directory has claimed yet. <see cref="ReplicationDirectory"/> stamps a real chunk id (≥ 0) when it registers one.</summary>
    internal const int UnassignedChunkId = -1;

    /// <summary>The pool considers this block handed out.</summary>
    private const byte PoolStateRented = 0;

    /// <summary>
    /// The pool considers this block to be on the free list. This lives in its own header field rather than in <c>ChunkId</c>, because the directory owns
    /// <c>ChunkId</c> and resets it on removal — which would silently disarm the double-return check below.
    /// </summary>
    private const byte PoolStateFree = 1;

    private readonly IMemoryAllocator _allocator;
    private readonly List<PinnedMemoryBlock> _slabs = [];
    private readonly int _blockStride;
    private readonly int _blocksPerSlab;
    private readonly int _slabBytes;
    private readonly long _budgetBytes;

    private nint _freeHead = FreeListEnd;
    private long _committedBytes;
    private int _blockCount;
    private int _freeBlockCount;
    private bool _disposed;
    private ReplicationThreadAffinity _affinity;

    /// <summary>Creates a pool for one archetype's blocks. Nothing is allocated here.</summary>
    /// <param name="id">Stable resource id, unique among <paramref name="parent"/>'s children.</param>
    /// <param name="parent">Resource-graph parent; the pool registers under it and its slabs register under the pool.</param>
    /// <param name="allocator">Engine allocator, supplied by DI exactly as <c>TransientStore</c> takes it.</param>
    /// <param name="layout">The archetype's block layout; fixes the stride for the pool's lifetime.</param>
    /// <param name="options">Operator configuration; <see cref="SubscriptionsOptions.StatePoolBudgetBytes"/> is the ceiling this pool honours.</param>
    public ReplicationBlockPool(string id, IResource parent, IMemoryAllocator allocator, ReplicationBlockLayout layout, SubscriptionsOptions options)
        : base(id, ResourceType.Allocator, parent, ExhaustionPolicy.Evict)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(options);

        // A negative budget would silently disable the pool — every rent failing for a reason no counter explains. Zero is legal and means exactly that,
        // deliberately; negative is a mistake.
        if (options.StatePoolBudgetBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.StatePoolBudgetBytes,
                $"{nameof(SubscriptionsOptions.StatePoolBudgetBytes)} cannot be negative; 0 disables the pool");
        }

        _allocator = allocator;
        Layout = layout;
        _blockStride = layout.BlockStride;

        // Round UP: the slab floor is a minimum, so rounding down would hand out slabs below it. At N = 21 the stride is 2 112 B, and rounding down gave
        // 31 blocks = 65 472 B — just under the 64 KiB the design specifies.
        _blocksPerSlab = Math.Max(1, (MinSlabBytes + _blockStride - 1) / _blockStride);
        _slabBytes = _blocksPerSlab * _blockStride;
        _budgetBytes = options.StatePoolBudgetBytes;
    }

    /// <summary>The block layout this pool carves to.</summary>
    public ReplicationBlockLayout Layout { get; }

    /// <summary>Ceiling on committed bytes.</summary>
    public long BudgetBytes => _budgetBytes;

    /// <summary>Bytes currently committed in slabs. Never exceeds <see cref="BudgetBytes"/>; returns to zero on <see cref="Dispose"/>.</summary>
    public long CommittedBytes => _committedBytes;

    /// <summary>Distance between consecutive blocks in a slab.</summary>
    public int BlockStride => _blockStride;

    /// <summary>Blocks carved so far, rented and free together.</summary>
    public int BlockCount => _blockCount;

    /// <summary>Blocks currently on the free list.</summary>
    public int FreeBlockCount => _freeBlockCount;

    /// <summary>
    /// Fraction of the budget committed. Crossing 0.8 is the resource graph's default health threshold; surfacing that as a warning needs a logger this type
    /// does not yet have, and lands with the DI wiring — see the note on <see cref="SubscriptionsOptions.StatePoolBudgetBytes"/>.
    /// </summary>
    public double Utilization => _budgetBytes <= 0 ? 0d : (double)_committedBytes / _budgetBytes;

    /// <summary>Live block count, surfaced to the Workbench resource tree.</summary>
    public override int? Count => _blockCount;

    /// <inheritdoc />
    /// <remarks>Bookkeeping only. The slabs are children of this node and are accounted separately, per the interface contract.</remarks>
    public int EstimatedMemorySize => 64 + (_slabs.Count * IntPtr.Size);

    /// <summary>
    /// Takes a block from the free list, growing by one slab when it is empty. The returned block's header is zeroed; its entries are not — they are
    /// initialized on first use through the per-entry <c>EntityId</c> mismatch path.
    /// </summary>
    /// <param name="block">The rented block, or <see langword="null"/> when the budget binds or the pool is disposed.</param>
    /// <returns><see langword="false"/> when no block could be rented without exceeding the budget. Never throws on this path.</returns>
    public bool TryRent(out ReplicationBlockHeader* block)
    {
        _affinity.Enter(nameof(ReplicationBlockPool), nameof(TryRent));
        try
        {
            if (_disposed || (_freeHead == FreeListEnd && !TryGrow()))
            {
                block = null;
                return false;
            }

            var header = (ReplicationBlockHeader*)_freeHead;
            _freeHead = header->NextFree;
            _freeBlockCount--;

            header->WatchedMask = 0;
            header->LastWatchedTick = 0;

            // The change mask is stamped with the tick it describes and a reader ignores it when the stamps disagree, so a recycled block's stale pair is
            // already inert. Zeroed anyway because this method initialises the header field by field rather than clearing it, and "inert unless the tick
            // counter wraps" is a worse guarantee than "empty", for two stores on a path that runs once per block.
            header->ChangedSlots = 0;
            header->ChangedTick = 0;
            header->ChunkId = UnassignedChunkId;
            header->NextFree = FreeListEnd;
            header->PoolState = PoolStateRented;

            block = header;
            return true;
        }
        finally
        {
            _affinity.Exit();
        }
    }

    /// <summary>Returns a block to the free list. The block must have come from this pool and must no longer be reachable from any directory.</summary>
    /// <exception cref="ArgumentException">The block is already on the free list — returning twice would make its link point at itself.</exception>
    public void Return(ReplicationBlockHeader* block)
    {
        _affinity.Enter(nameof(ReplicationBlockPool), nameof(Return));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            if (block->PoolState == PoolStateFree)
            {
                throw new ArgumentException("Block is already on the free list; returning it twice would corrupt the list.", nameof(block));
            }

            block->WatchedMask = 0;
            block->ChunkId = UnassignedChunkId;
            block->PoolState = PoolStateFree;
            block->NextFree = _freeHead;
            _freeHead = (nint)block;
            _freeBlockCount++;
        }
        finally
        {
            _affinity.Exit();
        }
    }

    /// <summary>
    /// Commits one slab and threads its blocks onto the free list, or reports that the budget forbids it.
    /// </summary>
    /// <remarks>
    /// The budget is checked against the whole slab before the allocator is called, so a pool never commits bytes it then has to give back — the check is
    /// what makes "never allocate beyond the budget" a property of this method rather than of its callers.
    /// </remarks>
    private bool TryGrow()
    {
        if (_committedBytes + _slabBytes > _budgetBytes)
        {
            return false;
        }

        // zeroed: false — only the header is cleared, and that happens on rent. Zeroing whole slabs would touch every entry byte for no benefit.
        var slab = _allocator.AllocatePinned($"Slab-{_slabs.Count}", this, _slabBytes, false, SlabAlignment);
        _slabs.Add(slab);
        _committedBytes += _slabBytes;

        // Threaded in REVERSE so the LIFO free list hands blocks out in ascending address order: a run of freshly rented blocks then walks forward, with
        // the prefetcher rather than against it.
        var data = slab.DataAsPointer;
        for (var i = _blocksPerSlab - 1; i >= 0; i--)
        {
            var header = (ReplicationBlockHeader*)(data + (i * _blockStride));
            header->ChunkId = UnassignedChunkId;
            header->PoolState = PoolStateFree;
            header->NextFree = _freeHead;
            _freeHead = (nint)header;
        }

        _blockCount += _blocksPerSlab;
        _freeBlockCount += _blocksPerSlab;
        return true;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Drop the free head before the slabs go, so no stale pointer outlives the memory it names. Every counter resets with it: leaving _committedBytes
        // behind would let a post-dispose rent pass the budget check and register a fresh slab under a node whose children have already been cleared,
        // leaking it outright.
        _freeHead = FreeListEnd;
        _freeBlockCount = 0;
        _blockCount = 0;
        _committedBytes = 0;
        _slabs.Clear();

        // ResourceNode.Dispose frees the slabs, which are children of this node.
        base.Dispose(disposing);

        // Last, so the tree stays walkable while the children dispose. Without this the disposed pool keeps its id in the parent's child map, and because
        // RegisterChild's false return is ignored, a pool recreated under the same id would be silently absent from the resource tree.
        Parent?.RemoveChild(this);
    }
}
