// unset

namespace Typhon.Engine.Internals;

/// <summary>
/// Abstract non-generic base class for B+Tree indexes. Provides the non-generic surface
/// used by ComponentTable, selectivity estimation, and diagnostic tools.
/// </summary>
/// <remarks>
/// Replaces the former <c>IBTree</c> interface. Since <see cref="BTree{TKey}"/> was the only implementation,
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
}
