// unset

using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Abstract non-generic base class for B+Tree indexes. Provides the non-generic surface
/// used by ComponentTable, selectivity estimation, and diagnostic tools.
/// </summary>
/// <remarks>
/// Replaces the former <c>IBTree</c> interface. Since <see cref="BTree{TKey,TStore}"/> was the only implementation,
/// an abstract class is a better fit: it avoids interface dispatch overhead and provides a natural home
/// for shared non-generic operations like <see cref="GetMinKeyAsLong"/> / <see cref="GetMaxKeyAsLong"/>.
/// Implements <see cref="IBTreeIndex"/> to allow <see cref="IndexedFieldInfo"/> to hold indexes backed by any store type without being generic itself.
/// </remarks>
internal abstract class BTreeBase<TStore> : IBTreeIndex where TStore : struct, IPageStore
{
    public abstract ChunkBasedSegment<TStore> Segment { get; }
    public abstract bool AllowMultiple { get; }
    public abstract int EntryCount { get; }

    public abstract unsafe int Add(void* keyAddr, int value, ref ChunkAccessor<TStore>accessor);
    public abstract unsafe int Add(void* keyAddr, int value, ref ChunkAccessor<TStore>accessor, out int bufferRootId);
    public abstract unsafe bool Remove(void* keyAddr, out int value, ref ChunkAccessor<TStore>accessor);
    public abstract unsafe Result<int, BTreeLookupStatus> TryGet(void* keyAddr, ref ChunkAccessor<TStore>accessor);
    public abstract unsafe bool RemoveValue(void* keyAddr, int elementId, int value, ref ChunkAccessor<TStore>accessor);
    public abstract unsafe VariableSizedBufferAccessor<int, TStore> TryGetMultiple(void* keyAddr, ref ChunkAccessor<TStore>accessor);

    /// <summary>
    /// Compound move: atomically removes <paramref name="value"/> from <paramref name="oldKeyAddr"/>
    /// and inserts it under <paramref name="newKeyAddr"/>. For unique indexes (!AllowMultiple).
    /// </summary>
    /// <returns>True if the old key was found and moved; false if old key not found.</returns>
    public abstract unsafe bool Move(void* oldKeyAddr, void* newKeyAddr, int value, ref ChunkAccessor<TStore>accessor);

    /// <summary>
    /// Compound move for multi-value indexes (AllowMultiple): removes <paramref name="elementId"/>/<paramref name="value"/>
    /// from <paramref name="oldKeyAddr"/>'s buffer and appends <paramref name="value"/> under <paramref name="newKeyAddr"/>.
    /// Returns the new element ID and both HEAD buffer IDs for inline TAIL tracking.
    /// </summary>
    public abstract unsafe int MoveValue(void* oldKeyAddr, void* newKeyAddr, int elementId, int value, ref ChunkAccessor<TStore>accessor, 
        out int oldHeadBufferId, out int newHeadBufferId);

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Bulk value update — the key-erased surface the tick fence's IndexMassUpdate phase drives K heterogeneous trees through (#872 step 6, §5.5).
    //
    // Same erasure discipline as Add / Remove above: the phase holds a BTreeBase<TStore> and cannot name TKey, so keys cross the boundary as raw addresses
    // and batches as raw buffers of a stride the tree itself reports. Nothing here interprets the bytes — the concrete BTree<TKey, TStore> casts once and
    // everything downstream of that is typed.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Bytes per staged entry, which is <c>sizeof</c> of the tree's own entry struct including its alignment padding.</summary>
    /// <remarks>
    /// Reported by the tree rather than derived from the indexed field's size, because the entry is a struct: a 4-byte key packs to 8 bytes with its value
    /// while an 8-byte key pads to 16. A producer computing the stride itself would be right for <c>int</c> and silently wrong for <c>long</c>.
    /// </remarks>
    /// <param name="multi"><c>true</c> for the <c>AllowMultiple</c> entry shape, which also carries an element id and the value being replaced.</param>
    public abstract int BulkEntryStride(bool multi);

    /// <summary>Writes one unique-index staged entry into <paramref name="dest"/>, which must be exactly <see cref="BulkEntryStride"/> bytes.</summary>
    /// <remarks>
    /// The destination is a span, not a pointer: it addresses a managed staging array that grows, and a pointer taken before a grow would address the old
    /// one. The KEY stays a raw address because it comes from cluster memory the caller already holds.
    /// </remarks>
    public abstract unsafe void WriteBulkEntry(Span<byte> dest, void* keyAddr, int newValue);

    /// <summary>Writes one <c>AllowMultiple</c> staged entry at <paramref name="dest"/>.</summary>
    /// <remarks>
    /// <paramref name="oldValue"/> is not redundant with <paramref name="elementId"/>: the id names the CHUNK holding the element and elements are addressed
    /// by value within it, so replacing one requires the value being replaced. Migration has it — the old ClusterLocation is exactly what is overwritten.
    /// </remarks>
    public abstract unsafe void WriteBulkMultiEntry(Span<byte> dest, void* keyAddr, int elementId, int oldValue, int newValue);

    /// <summary>Sorts a staged buffer ascending by key, in place. The partitioning descent requires it and asserts it in Debug.</summary>
    /// <param name="entries">The staged run, a whole number of entries at this tree's stride.</param>
    /// <param name="scratch">Ping-pong scratch for the radix sort, at least as long as <paramref name="entries"/>; unused by a tree whose key has no radix
    /// order (<c>String64</c>). Caller-owned — <c>IndexUpdateStaging.SortScratch</c> on the fence — so nothing here allocates per run.</param>
    /// <param name="radixCounts">Histogram scratch of <see cref="RadixSort.Buckets"/> ints, the caller's too.</param>
    /// <param name="multi"><c>true</c> for the <c>AllowMultiple</c> entry shape.</param>
    public abstract void SortBulkEntries(Span<byte> entries, Span<byte> scratch, Span<int> radixCounts, bool multi);

    /// <summary>
    /// Merges two key-sorted staged runs into <paramref name="dest"/>, which must hold exactly their combined length.
    /// </summary>
    /// <remarks>
    /// <b>One erased call per merge pass, not one per comparison.</b> The alternative — an erased "compare these two entries" primitive with the merge loop
    /// written in the caller — would put a virtual call on every one of the ~n log W comparisons. Doing the whole two-way merge behind one call lets the
    /// typed side run monomorphised, which is the same reason <c>ILeafApplier</c> is a struct type parameter rather than an interface.
    /// </remarks>
    public abstract void MergeBulkRuns(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dest, bool multi);

    /// <summary>
    /// Splits a sorted staged buffer into at most <paramref name="desiredParts"/> contiguous parts whose leaves are disjoint, so the parts can be applied
    /// concurrently with no latch. See <c>BTree.PartitionByLeafBoundaries</c>.
    /// </summary>
    public abstract int PartitionBulkEntries(ReadOnlySpan<byte> entries, bool multi, int desiredParts, Span<int> boundaries,
        ref ChunkAccessor<TStore> accessor);

    /// <summary>
    /// <b>Callable only inside the exclusive tick-fence window</b> (<c>EW-01</c>). Applies one contiguous run of staged entries in a single partitioning
    /// descent, visiting every internal node at most once for the whole run.
    /// </summary>
    public abstract int ApplyBulkEntries(ReadOnlySpan<byte> entries, bool multi, ref ChunkAccessor<TStore> accessor, out BulkUpdateStats stats);

    public abstract void CheckConsistency(ref ChunkAccessor<TStore>accessor);

    /// <summary>
    /// Advances a parked range cursor by one leaf, writing that leaf's in-range entries as (ordered key, value) pairs.
    /// See <see cref="BTree{TKey,TStore}.FillOrderedPage"/> for the contract, including the negative "grow the spans" return.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the streaming K-way merge does not have to be generic over the key type. The cursor state is non-generic, the output is
    /// non-generic, and the typed override does the key comparison and the ordered encoding on its side of the call. Without it, holding K live cursors would
    /// mean making <c>KWayMergeState</c>, <c>ArchetypeSortedStream</c> and everything they touch generic over <c>TKey</c> — for a merge whose entire job is
    /// to compare keys that have already been normalised to <see cref="long"/>.
    /// </remarks>
    internal abstract int FillOrderedPage(ref LeafPageCursorState state, Span<long> orderedKeys, Span<int> values, ref ChunkAccessor<TStore> accessor);

    // Deliberately NOT here: the OLC diagnostic counters (OptimisticRestarts, PessimisticFallbacks, SplitCount,
    // MergeCount, MoveRightCount, WriteLockFailures, ContentionSplitCount). Five of them were abstract on this base and
    // nothing ever read them through a BTreeBase reference — every caller holds the concrete BTree<TKey, TStore>, and
    // four of their immediate neighbours were already plain members, so the split between "promoted to the polymorphic
    // surface" and "not" tracked nothing. Put a counter here only when a caller genuinely has nothing but a BTreeBase.
    //
    // Removed outright rather than demoted: Count (a second name for EntryCount over the same field) and
    // LeafFullFromOlc (incremented on the insert path, read by nobody, and not even reset by ResetDiagnostics — the
    // signal it carried is emitted live as Data:Index:BTree:RebalanceFallback reason 0).

    /// <summary>
    /// Returns the minimum key encoded as a <see cref="long"/> using the same encoding as
    /// <see cref="QueryResolverHelper.EncodeThreshold"/>. Returns 0 for empty trees.
    /// </summary>
    public abstract long GetMinKeyAsLong();

    /// <summary>
    /// Returns the maximum key encoded as a <see cref="long"/> using the same encoding as
    /// <see cref="QueryResolverHelper.EncodeThreshold"/>. Returns 0 for empty trees.
    /// </summary>
    public abstract long GetMaxKeyAsLong();

    /// <summary>Number of preallocated directory chunks (0-3) every shared index segment reserves for its chunk-0 BTree directory. How many trees that holds
    /// depends on the stride — see <see cref="MaxDirectoryEntriesFor"/>. Node chunks live at chunkId &gt;= this.</summary>
    internal const int DirectoryChunkCount = 4;

    /// <summary>
    /// Hard cap on B+Trees per segment: how many <see cref="BTreeDirectoryEntry"/> fit in the <see cref="DirectoryChunkCount"/> reserved directory chunks at
    /// <paramref name="stride"/>. Chunk 0 loses <see cref="BTreeDirectoryHeader"/> to its header; chunks 1..n-1 are pure entry storage. 84 at the 256-byte
    /// node stride. Entry <c>MaxDirectoryEntriesFor(stride)</c> would land in the first NODE chunk, so this must stay exactly what those chunks hold — never
    /// a looser round number (#657, which replaced a hardcoded 20).
    /// </summary>
    internal static int MaxDirectoryEntriesFor(int stride)
        => (stride - BTreeDirectoryHeader.Size) / BTreeDirectoryEntry.Size + (DirectoryChunkCount - 1) * (stride / BTreeDirectoryEntry.Size);

    /// <summary>
    /// Torn-safe reset of a shared index segment to empty — used by crash recovery before fresh index trees are (re)built (RB-01). Frees every node chunk
    /// (chunkId &gt;= <see cref="DirectoryChunkCount"/>) via the allocation bitmap ONLY — it never reads chunk content, so a torn on-disk index node page is
    /// reclaimed without being parsed (the precondition for retiring FPI on index pages) — then zeroes the chunk-0 directory header so a subsequent fresh
    /// <c>RegisterInDirectory</c> re-registers every tree from an empty directory. The four directory chunks (0-3) stay allocated and are reused.
    /// </summary>
    internal static unsafe void ClearSharedSegment(ChunkBasedSegment<TStore> segment, ChangeSet changeSet)
    {
        if (segment == null)
        {
            return;
        }

        using var guard = EpochGuard.Enter(segment.Store.EpochManager);

        // Free node chunks by bitmap only — torn-safe: a torn node page is reclaimed, never parsed (FreeChunk touches only the page's occupancy metadata).
        var capacity = segment.ChunkCapacity;
        for (var chunkId = DirectoryChunkCount; chunkId < capacity; chunkId++)
        {
            if (segment.IsChunkAllocated(chunkId))
            {
                segment.FreeChunk(chunkId);
            }
        }

        // A segment that has never hosted a tree has no reserved directory chunk yet — the first BTree ctor reserves 0-3. Nothing to clear.
        if (!segment.IsChunkAllocated(0))
        {
            return;
        }

        // Zero the directory header (chunk 0) so the directory reads as empty; fresh trees re-register from slot 0, overwriting the stale entries.
        var accessor = segment.CreateChunkAccessor(changeSet);
        try
        {
            var addr = accessor.GetChunkAddress(0, true);
            ref var header = ref Unsafe.AsRef<BTreeDirectoryHeader>(addr);
            header.EntryCount = 0;
        }
        finally
        {
            accessor.Dispose();
        }
    }
}
