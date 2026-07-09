// unset

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
    public abstract unsafe bool RemoveValue(void* keyAddr, int elementId, int value, ref ChunkAccessor<TStore>accessor, bool preserveEmptyBuffer = false);
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
    public abstract unsafe int MoveValue(void* oldKeyAddr, void* newKeyAddr, int elementId, int value, ref ChunkAccessor<TStore>accessor, out int oldHeadBufferId,
        out int newHeadBufferId, bool preserveEmptyBuffer = false);

    public abstract void CheckConsistency(ref ChunkAccessor<TStore>accessor);

    // Diagnostic counters
    public abstract long Count { get; }
    public abstract long OptimisticRestarts { get; }
    public abstract long PessimisticFallbacks { get; }
    public abstract long LeafFullFromOlc { get; }
    public abstract long SplitCount { get; }

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

    /// <summary>Number of preallocated directory chunks (0-3) every shared index segment reserves for its chunk-0 BTree directory. Up to 20 index slots for
    /// 64-byte chunks. Node chunks live at chunkId &gt;= this.</summary>
    internal const int DirectoryChunkCount = 4;

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
