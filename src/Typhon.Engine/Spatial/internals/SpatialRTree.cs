using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Maximum tree depth supported. 16 is generous — 100K entities in 2D-f32 (fanout 20) need depth 4-5.
/// </summary>
internal static class SpatialRTreeConstants
{
    internal const int MaxTreeDepth = 16;

    /// <summary>Inline capacity of the ray query's priority queue — the allocation-free fast path before it spills to pooled arrays.</summary>
    internal const int RayHeapInlineCapacity = 64;

    /// <summary>
    /// Hard ceiling on the ray priority queue after spilling (~192 KB of pooled backing at 12 B/entry).
    /// </summary>
    /// <remarks>
    /// Not derived from <see cref="MaxTreeDepth"/>: unlike a descent path, the ray frontier is not depth-bounded — a ray that grazes many subtrees at the same
    /// entry distance holds them all pending at once. This bound exists only so a degenerate or cyclic tree cannot grow the heap without limit; reaching it is
    /// recorded through <see cref="SpatialRTreeDiagnostics.RecordDfsStackOverflow"/> rather than silently dropping children (#589).
    /// </remarks>
    internal const int MaxRayHeapCapacity = 1 << 14;
}

/// <summary>Stack-allocated buffer for path recording during descent.</summary>
[InlineArray(SpatialRTreeConstants.MaxTreeDepth)]
internal struct PathChunkIdBuffer
{
    private int _element0;
}

/// <summary>Stack-allocated buffer for child indices along the descent path.</summary>
[InlineArray(SpatialRTreeConstants.MaxTreeDepth)]
internal struct PathChildIndexBuffer
{
    private int _element0;
}

/// <summary>Stack-allocated buffer for OLC versions along the descent path.</summary>
[InlineArray(SpatialRTreeConstants.MaxTreeDepth)]
internal struct PathVersionBuffer
{
    private int _element0;
}

/// <summary>
/// Stack-allocated traversal path for a single R-Tree mutation. Records (chunkId, childIndex, olcVersion) at each level during descent, enabling parent
/// access during split propagation and ancestor MBR refit.
/// </summary>
internal ref struct DescentPath
{
    public PathChunkIdBuffer ChunkIds;
    public PathChildIndexBuffer ChildIndices;
    public PathVersionBuffer Versions;
    public int Depth;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(int chunkId, int childIndex, int version)
    {
        ChunkIds[Depth] = chunkId;
        ChildIndices[Depth] = childIndex;
        Versions[Depth] = version;
        Depth++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear() => Depth = 0;
}

/// <summary>
/// Page-backed wide R-Tree for spatial indexing. Uses SOA node layout driven by <see cref="SpatialNodeDescriptor"/>. All four variants (2D/3D × f32/f64) are
/// served by a single implementation — descriptor fields are JIT-constant after readonly promotion.
/// </summary>
/// <remarks>
/// Coordinates flow through the tree as <c>double</c> arrays of length <c>CoordCount</c>, ordered as [min0, min1, ..., max0, max1, ...]
/// (e.g., [MinX, MinY, MaxX, MaxY] for 2D). The SOA read/write helpers in <see cref="SpatialNodeHelper"/> handle float↔double conversion at the storage boundary.
/// </remarks>
internal unsafe partial class SpatialRTree<TStore> where TStore : struct, IPageStore
{
    private readonly ChunkBasedSegment<TStore> _segment;
    private readonly SpatialNodeDescriptor _desc;
    private readonly SpatialVariant _variant;

    // Tree metadata (persisted in chunk 0)
    private int _rootChunkId;
    private int _nodeCount;
    private int _entityCount;
    private int _depth;

    /// <summary>Monotonic counter incremented on every Insert/Remove. Used by trigger system for static cache invalidation.</summary>
    private int _mutationVersion;

    /// <summary>Lock protecting SyncMetadata writes to chunk 0 against concurrent mutations.</summary>
    private readonly Lock _metadataLock = new();

    /// <summary>
    /// Whether this tree mirrors its four metadata values into chunk 0 on every mutation. On for a tree that owns its segment; OFF for the transient per-cell
    /// cluster trees of #872 step 9, which share one segment per archetype.
    /// </summary>
    /// <remarks>
    /// <para><b>Suppressing it on a shared segment is a correctness fix, not only a saving.</b> Chunk 0 is one chunk per SEGMENT, so every tree sharing a
    /// segment writes the same sixteen bytes — each one stamping its own root, node count, depth and variant over its neighbours'. The mirror is therefore
    /// already meaningless there, and the only thing that stops it mattering is that nothing reads it back: the authoritative values are per-INSTANCE fields,
    /// and <see cref="LoadMetadata"/> runs only when a tree is constructed with <c>load: true</c>, which a transient tree never is.</para>
    /// <para>What it saves is a contended cache line per archetype. The write is taken under <c>_metadataLock</c> on every insert, split and remove, and the
    /// lock is per-instance while the line is per-segment — so once the fence runs cells in parallel, every cell tree in an archetype is false-sharing one
    /// line while holding locks that do not exclude each other.</para>
    /// <para>It is incompatible with <c>load: true</c> by construction, and the constructor refuses the combination rather than silently reading whatever the
    /// last tree happened to leave in chunk 0.</para>
    /// </remarks>
    private readonly bool _mirrorMetadata;

    /// <summary>
    /// Payload-indexed back-pointer array (#872 step 9). Indexed by the payload id passed
    /// to <see cref="Insert(long,System.ReadOnlySpan{double},ref ChunkAccessor{TStore},ChangeSet,uint)"/>; each element is a packed
    /// <c>(leafChunkId, slotIndex)</c> made by <see cref="PackHandle"/>. Null for every tree that does not use handles.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists at all.</b> A handle returned by <c>Insert</c> is only valid until the leaf splits. <c>InsertWithSplit</c> scatters BOTH
    /// halves through the overlap-minimising permutation, so an entry that stays in its original leaf still moves slot — measured at
    /// <c>SharedSegmentRTreeHarnessTests.Claim1</c>: <b>43 of 60 handles wrong after splits, 20 of them pointing outside their leaf's live range</b>. A stale
    /// handle makes <c>C5</c>'s escape-bound update write one cluster's bound into another cluster's slot, which is <c>CA-01</c> violated in both directions
    /// and an <c>SQ-01</c> false negative with no exception anywhere near it.</para>
    /// <para><b>Why an array rather than the chunk-based back-pointer segment this replaced.</b> That path — the entity tree's, removed in #872 step 13 —
    /// opened a <see cref="ChunkAccessor{TStore}"/> and did a paged lookup and write per entry, keyed by the owning component's chunk id. The cluster payload
    /// IS a cluster chunk id and <c>ArchetypeClusterState.ClusterSpatialIndexSlot</c> is already an <c>int[]</c> indexed by exactly that, so the fix-up is
    /// one array store folded into a scatter loop that is already writing the entry's other fields.</para>
    /// <para><b>The array must be large enough BEFORE any mutation, and must not be resized while one is in flight.</b> It is indexed by payload id, so it
    /// has to cover the largest live one. A resize is not merely unsynchronised — it cannot be made safe by a writer-side lock alone:
    /// <see cref="ScatterLeafEntries"/> reads the field once and writes through that reference, so an <c>Array.Resize</c> concurrent with a split publishes
    /// a NEW array while the scatter fills the abandoned one. The lost write is a stale handle, which is the exact defect this array exists to remove.
    /// <c>ArchetypeClusterState.EnsureClusterSpatialIndexSlotCapacity</c> keeps that contract on the caller's behalf: growers serialise on the archetype's
    /// latch, and growth is REFUSED outright from inside a parallel Migrate slice, because serialising growers does nothing for a split that is already
    /// writing through the reference it loaded. The fence pre-sizes the array so the refusal is unreachable in a correct tick.
    /// <see cref="ThrowPayloadOutOfRange"/> makes a violation loud rather than silent.</para>
    /// </remarks>
    internal int[] PayloadBackPointers;

    /// <summary>
    /// Optional sink receiving every chunk id this tree frees. Set by owners that hold chunk ids OUTSIDE the tree and must scrub them.
    /// </summary>
    /// <remarks>
    /// <b>"Is it still allocated?" is not a usable test, which is why the sink exists.</b> <c>FreeChunk</c> clears one allocation bit and leaves the chunk's
    /// bytes intact — leaf flag, count, MBR and parent pointer all survive — so a stale id still looks like a valid leaf. Worse on a segment shared by every
    /// cell of an archetype: the very next <c>AllocateChunk</c> can hand the chunk to a DIFFERENT cell's tree, at which point the id is allocated, is a leaf,
    /// and belongs to somebody else. The only sound moment to drop such a record is the instant the chunk is freed, which is here.
    /// </remarks>
    internal List<int> FreedChunkSink;

    /// <summary>Pack a <c>(leafChunkId, slotIndex)</c> pair into one <see cref="int"/>.</summary>
    /// <remarks>
    /// <para>Packing into an <c>int</c> rather than widening to a <c>long</c> keeps <c>ClusterSpatialIndexSlot</c> the size it already is: one array per
    /// archetype sized by cluster count, so the width is paid per cluster forever. <c>-1</c> stays the "not in any tree" sentinel it already was, and is never
    /// a valid packed handle because leaf chunk 0 is the segment's reserved null chunk.</para>
    /// <para><b>Five bits, not four — and #872 step 13 is why that mattered.</b> Leaf capacities computed from
    /// <see cref="SpatialNodeDescriptor"/>'s arithmetic are <c>R2Df32</c> 17, <c>R3Df32</c> 13, <c>R2Df64</c> 10, <c>R3Df64</c> 11. Four bits holds 0-15, so
    /// <b>the widest variant no longer fits in four</b>: when step 13 dropped the leaf entry's 4-byte <c>ComponentChunkId</c> column, <c>R2Df32</c> went 15 to
    /// 17 and a four-bit handle would have started silently truncating the slot index — a handle naming the wrong entry, which is precisely the defect this
    /// whole mechanism exists to remove. The step changed the capacities and did not touch this file; nothing broke because the five-bit choice had already
    /// bought the headroom, and <see cref="AssertHandleCapacity"/> would have made it a startup failure rather than a silent wrap either way.</para>
    /// <para>Five bits costs one bit of chunk id, leaving 67 M chunks per segment, and holds 0-31 against a current worst case of 17. <b>Restate these four
    /// numbers whenever the leaf layout changes</b> — <c>SH-02</c> in <c>rules/spatial.md</c> carries them as an invariant for exactly this reason, and the
    /// previous version of this paragraph reasoned from the pre-step-13 set for as long as it took a reviewer to notice.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PackHandle(int leafChunkId, int slotIndex)
    {
        // Both fields are guarded because both can silently wrap. The slot is bounded by LeafCapacity, which AssertHandleCapacity pins at construction;
        // the chunk id is bounded only by how big the segment grew, and shifting it past bit 31 sets the sign bit, after which >>> returns garbage.
        if ((uint)slotIndex > HandleSlotMask || (uint)leafChunkId > MaxHandleChunkId)
        {
            ThrowHandleOutOfRange(leafChunkId, slotIndex);
        }
        return (leafChunkId << HandleSlotBits) | slotIndex;
    }

    /// <summary>Unpack a handle made by <see cref="PackHandle"/>. Passing <see cref="NullHandle"/> is a caller bug — use <see cref="IsNullHandle"/> first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (int leafChunkId, int slotIndex) UnpackHandle(int handle) => (handle >>> HandleSlotBits, handle & HandleSlotMask);

    /// <summary>
    /// The "this payload is in no tree" handle. Deliberately <c>-1</c>, matching the sentinel <c>ArchetypeClusterState.ClusterSpatialIndexSlot</c> already
    /// used before it held handles — a second spelling of "absent" is how one of the two gets forgotten.
    /// </summary>
    /// <remarks>
    /// It is not a decodable handle: <c>UnpackHandle(-1)</c> yields <c>(0x07FFFFFF, 31)</c>, which is a perfectly plausible-looking leaf and slot. Test with
    /// <see cref="IsNullHandle"/> before unpacking; there is no in-band way to tell the two apart afterwards.
    /// </remarks>
    internal const int NullHandle = -1;

    /// <summary>Whether a stored handle means "not in any tree".</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsNullHandle(int handle) => handle < 0;

    /// <summary>Fail loudly if this variant's leaf capacity cannot be addressed by <see cref="PackHandle"/>'s slot field.</summary>
    /// <remarks>
    /// Always compiled, and called from the constructor rather than left to a test: the descriptor arithmetic is derived from the stride at runtime, so a
    /// change that overflows the field would otherwise produce corrupted handles in Release with nothing to point at.
    /// </remarks>
    private void AssertHandleCapacity()
    {
        if (_desc.LeafCapacity > HandleSlotMask)
        {
            ThrowHandleCapacityExceeded(_variant, _desc.LeafCapacity);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowHandleCapacityExceeded(SpatialVariant variant, int leafCapacity) =>
        throw new InvalidOperationException(
            $"Spatial variant {variant} has a leaf capacity of {leafCapacity}, which does not fit PackHandle's {HandleSlotBits}-bit slot field "
            + $"(max {HandleSlotMask}). Widen HandleSlotBits — the alternative is a silently truncated slot index in every payload back-pointer.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowHandleOutOfRange(int leafChunkId, int slotIndex) =>
        throw new ArgumentOutOfRangeException(nameof(leafChunkId),
            $"Cannot pack handle (leaf {leafChunkId}, slot {slotIndex}): the slot field holds 0..{HandleSlotMask} and the chunk field 0..{MaxHandleChunkId}. "
            + "Packing anyway would produce a handle naming a different entry, which is silent.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPayloadOutOfRange(long payloadId, int length) =>
        throw new ArgumentOutOfRangeException(nameof(payloadId),
            $"Payload {payloadId} is outside PayloadBackPointers (length {length}). The array must cover every live payload id BEFORE the mutation that "
            + "relocates it; skipping the write would leave the payload's handle naming whatever entry now occupies its old slot.");

    private const int HandleSlotBits = 5;
    private const int HandleSlotMask = (1 << HandleSlotBits) - 1;

    /// <summary>Largest leaf chunk id <see cref="PackHandle"/> can hold without shifting into the sign bit.</summary>
    private const int MaxHandleChunkId = int.MaxValue >> HandleSlotBits;

    /// <summary>Record a payload's new position, or fail loudly if the array cannot hold it. See <see cref="PayloadBackPointers"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePayloadHandle(int[] payloadBackPointers, long payloadId, int handle)
    {
        if ((ulong)payloadId >= (ulong)payloadBackPointers.Length)
        {
            ThrowPayloadOutOfRange(payloadId, payloadBackPointers.Length);
        }
        payloadBackPointers[payloadId] = handle;
    }

    // Chunk 0 metadata layout
    private const int MetaRootOffset = 0;
    private const int MetaNodeCountOffset = 4;
    private const int MetaEntityCountOffset = 8;
    private const int MetaDepthOffset = 12;
    private const int MetaVariantOffset = 16;

    internal ChunkBasedSegment<TStore> Segment => _segment;
    internal SpatialNodeDescriptor Descriptor => _desc;
    internal SpatialVariant Variant => _variant;
    internal int RootChunkId => _rootChunkId;
    internal int NodeCount => _nodeCount;

    /// <summary>
    /// Free the one chunk an emptied tree still owns — its root — and leave the instance unusable.
    /// </summary>
    /// <remarks>
    /// Removal frees a leaf only when it has a parent to unlink it from (see <c>RemoveEmptyLeaf</c>), so a tree drained to zero entries collapses to a single
    /// empty root leaf that nothing releases. That is correct while the tree lives, and a leak the moment its owner drops it: on a TRANSIENT segment there is
    /// no GC watching the chunks, so the root outlives every reference to the structure that allocated it. Callers that retire a whole tree must call this.
    /// </remarks>
    internal void ReleaseRootChunk()
    {
        if (_rootChunkId < 0)
        {
            return;
        }

        _segment.FreeChunk(_rootChunkId);
        _rootChunkId = -1;
        _nodeCount = 0;
    }
    internal int EntityCount => _entityCount;
    internal int Depth => _depth;
    internal int MutationVersion => _mutationVersion;

    /// <summary>
    /// Create a new R-Tree or load an existing one from the segment.
    /// </summary>
    /// <param name="segment">Pre-allocated CBS with stride matching the descriptor's Stride</param>
    /// <param name="variant">Spatial variant (determines descriptor and node layout)</param>
    /// <param name="load">True to load existing tree from segment, false to create new</param>
    /// <param name="changeSet">ChangeSet for WAL participation (null for non-WAL)</param>
    /// <param name="mirrorMetadata">
    /// False to stop mirroring metadata into chunk 0 — required for trees SHARING a segment, where the mirror is contended and meaningless. See
    /// <see cref="_mirrorMetadata"/>.
    /// </param>
    internal SpatialRTree(ChunkBasedSegment<TStore> segment, SpatialVariant variant, bool load = false, ChangeSet changeSet = null,
        bool mirrorMetadata = true)
    {
        if (load && !mirrorMetadata)
        {
            ThrowHelper.ThrowInvalidOp("SpatialRTree cannot load from a segment whose metadata mirror it does not maintain — chunk 0 would hold another "
                + "tree's values, or none. Load implies mirrorMetadata: true.");
        }

        _segment = segment;
        _variant = variant;
        _mirrorMetadata = mirrorMetadata;
        _desc = SpatialNodeDescriptor.ForVariant(variant);
        AssertHandleCapacity();

        var guard = EpochGuard.Enter(_segment.Store.EpochManager);
        try
        {
            if (!load)
            {
                // Reserve chunk 0 for metadata BEFORE creating our accessor (ReserveChunk with clearContent creates its own internal accessor)
                if (!_segment.IsChunkAllocated(0))
                {
                    _segment.ReserveChunk(0, true, changeSet);
                }
            }

            var accessor = _segment.CreateChunkAccessor(changeSet);
            try
            {
                if (!load)
                {
                    _rootChunkId = AllocNode(true, 0, ref accessor, changeSet);
                    _nodeCount = 1;
                    _entityCount = 0;
                    _depth = 1;
                    SyncMetadata(ref accessor);
                }
                else
                {
                    LoadMetadata(ref accessor);
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            guard.Dispose();
        }
    }

    /// <summary>Allocate a new node, initialize its header fields.</summary>
    /// <remarks>
    /// Allocates WITHOUT clearContent to avoid creating a nested ChunkAccessor inside AllocateChunk (the caller already has an active accessor).
    /// We zero and initialize the header manually.
    /// </remarks>
    private int AllocNode(bool isLeaf, int parentChunkId, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet = null)
    {
        int chunkId = _segment.AllocateChunk(false, changeSet);
        byte* nodeBase = accessor.GetChunkAddress(chunkId, true);

        // Zero the entire chunk (stride bytes)
        new Span<byte>(nodeBase, _desc.Stride).Clear();

        // Initialize header
        // OlcVersion must start at version >= 1 (not 0) because ReadVersion() returns 0 as "locked/obsolete"
        // Set to 0b100 = 4 (version=1, lock=0, obsolete=0)
        *(int*)nodeBase = 4;
        SpatialNodeHelper.SetCount(nodeBase, 0);
        SpatialNodeHelper.SetIsLeaf(nodeBase, isLeaf);
        SpatialNodeHelper.SetParentChunkId(nodeBase, parentChunkId);
        return chunkId;
    }

    /// <summary>Write tree metadata to chunk 0. Lock-protected against concurrent mutations.</summary>
    private void SyncMetadata(ref ChunkAccessor<TStore> accessor)
    {
        if (!_mirrorMetadata)
        {
            return;
        }

        lock (_metadataLock)
        {
            byte* meta = accessor.GetChunkAddress(0, true);
            *(int*)(meta + MetaRootOffset) = _rootChunkId;
            *(int*)(meta + MetaNodeCountOffset) = _nodeCount;
            *(int*)(meta + MetaEntityCountOffset) = _entityCount;
            *(int*)(meta + MetaDepthOffset) = _depth;
            *(meta + MetaVariantOffset) = (byte)_variant;
        }
    }

    /// <summary>Load tree metadata from chunk 0.</summary>
    private void LoadMetadata(ref ChunkAccessor<TStore> accessor)
    {
        byte* meta = accessor.GetChunkAddress(0);
        _rootChunkId = *(int*)(meta + MetaRootOffset);
        _nodeCount = *(int*)(meta + MetaNodeCountOffset);
        _entityCount = *(int*)(meta + MetaEntityCountOffset);
        _depth = *(int*)(meta + MetaDepthOffset);
    }

    // ── OLC helpers ─────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static OlcLatch GetLatch(byte* nodeBase) => new(ref SpatialNodeHelper.OlcVersionRef(nodeBase));

    /// <summary>Spin to acquire the write lock on a node.</summary>
    /// <remarks>
    /// <see cref="PureSpin"/> rather than <see cref="SpinWait"/>: the holder is a handful of node writes from releasing, so handing the core to the scheduler
    /// to wait that out is never the right trade. The B+Tree keeps <see cref="SpinWait"/> at two sites for a reason that cannot arise here — a latch holder
    /// there may be inside a page fault, because its nodes live in a mapped file and <c>PreDirtyForWrite</c> can pull a page in while a latch is held. Every
    /// cell tree is built over a <see cref="TransientStore"/>: heap-backed, pinned, no file I/O and no cache eviction, so no holder of this latch can block on
    /// anything. The waiting thread has nothing to gain by sleeping and a full quantum to lose.
    /// <para>
    /// <b>That argument is about <c>TStore</c>, and this method is not.</b> It sits on the generic type, so it applies to every instantiation — and the
    /// reasoning above holds only for the transient one. <c>SpatialRTree&lt;PersistentStore&gt;</c> has no use anywhere in <c>src/</c> today (benchmarks
    /// only), which is what makes the unqualified choice safe rather than merely convenient. Give this a store-conditional wait before putting a persistent
    /// instantiation on a production path: there a holder CAN be inside a page fault while the latch is held, and spinning through one burns a core for the
    /// length of an I/O.
    /// </para>
    /// <para>
    /// The trade this accepts is visibility: a <see cref="PureSpin"/> wait emits no yield event, so a wait that runs long is invisible to that channel by
    /// construction. Count it at the call site if that ever matters here.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SpinWriteLock(byte* nodeBase, out OlcLatch latch)
    {
        latch = GetLatch(nodeBase);
        PureSpin spin = default;
        while (!latch.TryWriteLock())
        {
            spin.Once();
        }
    }

    // ── Category mask helpers ────────────────────────────────────────────────

    /// <summary>
    /// Recompute an internal node's UnionCategoryMask as the bitwise OR of all children's UnionCategoryMasks.
    /// Must be called after RefitInternalMBR whenever category masks may have changed.
    /// </summary>
    private void RefitInternalUnionMask(byte* nodeBase, ref ChunkAccessor<TStore> accessor)
    {
        int count = SpatialNodeHelper.GetCount(nodeBase);
        uint unionMask = 0;
        for (int i = 0; i < count; i++)
        {
            int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
            byte* childBase = accessor.GetChunkAddress(childId);
            unionMask |= SpatialNodeHelper.ReadUnionCategoryMask(childBase, _desc);
        }
        SpatialNodeHelper.WriteUnionCategoryMask(nodeBase, unionMask, _desc);
    }

    /// <summary>
    /// Update the category mask of a leaf entry in-place and refit union masks up the ancestor chain.
    /// Called via back-pointer for runtime category changes (e.g., entity dies → clear Alive bit).
    /// </summary>
    internal void SetEntryCategoryMask(int leafChunkId, int slotIndex, uint mask, ref ChunkAccessor<TStore> accessor)
    {
        byte* leafBase = accessor.GetChunkAddress(leafChunkId, true);
        SpinWriteLock(leafBase, out var latch);
        SpatialNodeHelper.WriteLeafCategoryMask(leafBase, slotIndex, mask, _desc);
        SpatialNodeHelper.RefitLeafMBR(leafBase, _desc); // recomputes leaf union mask
        latch.WriteUnlock();
        RefitAncestorsBottomUp(leafChunkId, ref accessor);
    }

    /// <summary>
    /// Recompute one leaf's MBR from its entries and refit its ancestors — the make-good for the in-place update path, which deliberately skips both
    /// (<c>ST-07</c>, #872 step 9).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SetEntryCategoryMask"/> because that method exists to CHANGE something and refits as a consequence; this one changes nothing
    /// and exists only to restore <c>ST-01</c>'s equality. Calling it on a leaf that is already tight is correct and cheap, which is what lets the caller
    /// record touched leaves without deduplicating them.
    /// </remarks>
    internal void RefitLeafAndAncestors(int leafChunkId, ref ChunkAccessor<TStore> accessor)
    {
        byte* leafBase = accessor.GetChunkAddress(leafChunkId, true);
        if (!SpatialNodeHelper.IsLeaf(leafBase))
        {
            // The leaf was consumed by a split or removal since it was recorded. Its replacement was refit by that operation, so there is nothing owed here.
            return;
        }

        // Being a leaf is NOT proof of being OUR leaf. Since #872 step 9 many trees share one ChunkBasedSegment, so a chunk this tree freed can have been
        // reallocated as another cell tree's leaf — and it passes the IsLeaf test above wearing that tree's data. Refitting it would overwrite a stranger's
        // MBR, and RefitAncestorsBottomUp would then climb ITS parent chain, rewriting internal MBRs and union masks all the way to that tree's root. Nothing
        // would throw and both trees would stay structurally valid, so TreeValidator would pass over the damage; the only symptom is SQ-01 false negatives in
        // a cell this caller never touched. RemoveChecked already refuses on identity for the same reason (ST-05) and this path had no equivalent.
        if (!OwnsNode(leafChunkId, ref accessor))
        {
            return;
        }

        SpinWriteLock(leafBase, out var latch);
        try
        {
            SpatialNodeHelper.RefitLeafMBR(leafBase, _desc);
        }
        finally
        {
            latch.WriteUnlock();
        }

        RefitAncestorsBottomUp(leafChunkId, ref accessor);
    }

    /// <summary>
    /// Whether <paramref name="chunkId"/> belongs to THIS tree, decided by walking the parent chain and checking it arrives at <see cref="_rootChunkId"/>.
    /// </summary>
    /// <remarks>
    /// <para>The chain terminator cannot be used for this on its own: every root in a shared segment has <c>ParentChunkId == 0</c>, so "the walk ended" is
    /// true for a foreign tree exactly as it is for ours. Arriving at OUR root is the part that discriminates.</para>
    /// <para>Read-only and unlatched, which is sound where it is called from: the loose-leaf refit runs in the fence's per-archetype tail, inside the
    /// exclusive window <c>ST-07</c> requires and <c>PC-01</c> guarantees. It is not a general concurrent-safe ownership test and must not be used as one.</para>
    /// <para>The depth cap is a cycle guard, not a correctness bound. A corrupted parent pointer that formed a loop would otherwise spin here forever; a tree
    /// deeper than the cap is answered "not ours", which fails in the direction that skips work rather than the one that writes to a stranger.</para>
    /// </remarks>
    private bool OwnsNode(int chunkId, ref ChunkAccessor<TStore> accessor)
    {
        const int MaxChainWalk = 64;

        int current = chunkId;
        for (int step = 0; step < MaxChainWalk; step++)
        {
            if (current == _rootChunkId)
            {
                return true;
            }

            int parent = SpatialNodeHelper.GetParentChunkId(accessor.GetChunkAddress(current));
            if (parent == 0 || parent == current)
            {
                return false;
            }

            current = parent;
        }

        return false;
    }

    // ── Ancestor refit (bottom-up via ParentChunkId chain) ──────────────────

    /// <summary>
    /// Walk up from a node to the root via ParentChunkId, refitting each ancestor's MBR and UnionCategoryMask.
    /// Used after remove and other mutations that don't have a recorded descent path.
    /// </summary>
    private void RefitAncestorsBottomUp(int startChunkId, ref ChunkAccessor<TStore> accessor)
    {
        int currentChunkId = startChunkId;
        while (true)
        {
            byte* currentBase = accessor.GetChunkAddress(currentChunkId);

            // OLC-validate the child read to avoid chasing a stale parent pointer after concurrent split
            var childLatch = GetLatch(currentBase);
            int childVersion = childLatch.ReadVersion();
            int parentChunkId = SpatialNodeHelper.GetParentChunkId(currentBase);
            if (!childLatch.ValidateVersion(childVersion))
            {
                // Child was concurrently modified (split changed parent pointer) — re-read
                continue;
            }

            if (parentChunkId == 0)
            {
                break;
            }

            byte* parentBase = accessor.GetChunkAddress(parentChunkId, true);
            SpinWriteLock(parentBase, out var parentLatch);

            // Refit the parent's internal entry for this child
            int parentCount = SpatialNodeHelper.GetCount(parentBase);
            for (int i = 0; i < parentCount; i++)
            {
                if (SpatialNodeHelper.ReadInternalChildId(parentBase, i, _desc) == currentChunkId)
                {
                    // Update this child's MBR in the parent
                    for (int c = 0; c < _desc.CoordCount; c++)
                    {
                        SpatialNodeHelper.WriteInternalCoord(parentBase, i, c, SpatialNodeHelper.ReadNodeMBRCoord(currentBase, c, _desc), _desc);
                    }
                    break;
                }
            }

            SpatialNodeHelper.RefitInternalMBR(parentBase, _desc);
            RefitInternalUnionMask(parentBase, ref accessor);
            parentLatch.WriteUnlock();

            currentChunkId = parentChunkId;
        }
    }

    /// <summary>
    /// Walk the recorded descent path upward, refitting each ancestor's internal entry
    /// for the child that was modified, then recomputing the ancestor's own NodeMBR and UnionCategoryMask.
    /// </summary>
    private void RefitAncestors(ref DescentPath path, ref ChunkAccessor<TStore> accessor)
    {
        for (int level = path.Depth - 1; level >= 0; level--)
        {
            int parentChunkId = path.ChunkIds[level];
            int childIdx = path.ChildIndices[level];

            byte* parentBase = accessor.GetChunkAddress(parentChunkId, true);
            SpinWriteLock(parentBase, out var parentLatch);

            // Read child's current NodeMBR and update the parent's entry for that child
            int childChunkId = SpatialNodeHelper.ReadInternalChildId(parentBase, childIdx, _desc);
            byte* childBase = accessor.GetChunkAddress(childChunkId);
            for (int c = 0; c < _desc.CoordCount; c++)
            {
                SpatialNodeHelper.WriteInternalCoord(parentBase, childIdx, c, SpatialNodeHelper.ReadNodeMBRCoord(childBase, c, _desc), _desc);
            }

            SpatialNodeHelper.RefitInternalMBR(parentBase, _desc);
            RefitInternalUnionMask(parentBase, ref accessor);
            parentLatch.WriteUnlock();
        }
    }

    /// <summary>
    /// Read the fat AABB coordinates of a leaf entry at a known position. Used by SpatialMaintainer for containment check.
    /// </summary>
    internal void ReadLeafCoords(int leafChunkId, int slotIndex, Span<double> coords, ref ChunkAccessor<TStore> accessor)
    {
        byte* leafBase = accessor.GetChunkAddress(leafChunkId);
        SpatialNodeHelper.ReadLeafEntryCoords(leafBase, slotIndex, coords, _desc);
    }
}
