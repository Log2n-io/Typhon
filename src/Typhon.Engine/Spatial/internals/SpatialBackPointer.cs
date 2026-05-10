using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Back-pointer from an entity to its position in the spatial R-Tree leaf node.
/// Stored in a CBS&lt;PersistentStore&gt; with stride=8, keyed by the entity's component chunkId.
/// Enables O(1) leaf lookup for fat AABB containment check and remove operations.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SpatialBackPointer
{
    public int LeafChunkId;
    public short SlotIndex;
    /// <summary>Identifies which R-Tree this entity belongs to. Value equals <c>(byte)SpatialMode</c>.</summary>
    public byte TreeSelector;
    public byte Reserved;
}

/// <summary>
/// Static helpers for reading/writing <see cref="SpatialBackPointer"/> entries in a CBS segment.
/// The CBS is keyed by the entity's component chunkId (same addressing as component data).
/// </summary>
internal static unsafe class SpatialBackPointerHelper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static SpatialBackPointer Read<TStore>(ref ChunkAccessor<TStore> accessor, int componentChunkId) where TStore : struct, IPageStore
    {
        byte* ptr = accessor.GetChunkAddress(componentChunkId);
        return *(SpatialBackPointer*)ptr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Write<TStore>(ref ChunkAccessor<TStore> accessor, int componentChunkId, int leafChunkId, short slotIndex, byte treeSelector)
        where TStore : struct, IPageStore
    {
        byte* ptr = accessor.GetChunkAddress(componentChunkId, true);
        var bp = (SpatialBackPointer*)ptr;
        bp->LeafChunkId = leafChunkId;
        bp->SlotIndex = slotIndex;
        bp->TreeSelector = treeSelector;
        bp->Reserved = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Clear<TStore>(ref ChunkAccessor<TStore> accessor, int componentChunkId) where TStore : struct, IPageStore
    {
        byte* ptr = accessor.GetChunkAddress(componentChunkId, true);
        *(long*)ptr = 0;
    }
}
