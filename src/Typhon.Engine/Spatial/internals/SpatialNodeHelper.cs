using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Low-level SOA read/write helpers for spatial R-Tree nodes. All methods operate on raw <c>byte*</c> node base addresses plus a <see cref="SpatialNodeDescriptor"/>.
/// Coordinates are passed as <c>double</c> internally — the CoordSize branch (float vs double at the SOA boundary) is eliminated by the JIT since descriptor
/// fields are readonly.
/// </summary>
internal static unsafe class SpatialNodeHelper
{
    // ── Header access (fixed offsets for all variants) ──────────────────────

    /// <summary>Returns a ref to the OlcVersion int at offset 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref int OlcVersionRef(byte* nodeBase) => ref Unsafe.AsRef<int>(nodeBase);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetCount(byte* nodeBase) => *(int*)(nodeBase + 4) & 0xFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetCount(byte* nodeBase, int count)
    {
        ref int control = ref *(int*)(nodeBase + 4);
        control = (control & ~0xFF) | (count & 0xFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLeaf(byte* nodeBase) => (*(int*)(nodeBase + 4) & 0x100) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetIsLeaf(byte* nodeBase, bool isLeaf)
    {
        ref int control = ref *(int*)(nodeBase + 4);
        control = isLeaf ? (control | 0x100) : (control & ~0x100);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetParentChunkId(byte* nodeBase) => *(int*)(nodeBase + 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetParentChunkId(byte* nodeBase, int parentChunkId) => *(int*)(nodeBase + 8) = parentChunkId;

    // ── NodeMBR access (offset 12, variable size: CoordCount * CoordSize) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ReadNodeMBRCoord(byte* nodeBase, int coordIndex, in SpatialNodeDescriptor desc)
    {
        byte* addr = nodeBase + 12 + coordIndex * desc.CoordSize;
        return desc.CoordSize == 4 ? *(float*)addr : *(double*)addr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNodeMBRCoord(byte* nodeBase, int coordIndex, double value, in SpatialNodeDescriptor desc)
    {
        byte* addr = nodeBase + 12 + coordIndex * desc.CoordSize;
        if (desc.CoordSize == 4)
        {
            *(float*)addr = (float)value;
        }
        else
        {
            *(double*)addr = value;
        }
    }

    // ── UnionCategoryMask access (header field, after NodeMBR) ─────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUnionCategoryMask(byte* nodeBase, in SpatialNodeDescriptor desc) => *(uint*)(nodeBase + desc.UnionCategoryMaskOffset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUnionCategoryMask(byte* nodeBase, uint mask, in SpatialNodeDescriptor desc) => *(uint*)(nodeBase + desc.UnionCategoryMaskOffset) = mask;

    // ── Leaf SOA access ─────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ReadLeafCoord(byte* nodeBase, int index, int coordIndex, in SpatialNodeDescriptor desc)
    {
        byte* addr = nodeBase + desc.LeafCoordOffsets + coordIndex * desc.LeafCoordStride + index * desc.CoordSize;
        return desc.CoordSize == 4 ? *(float*)addr : *(double*)addr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLeafCoord(byte* nodeBase, int index, int coordIndex, double value, in SpatialNodeDescriptor desc)
    {
        byte* addr = nodeBase + desc.LeafCoordOffsets + coordIndex * desc.LeafCoordStride + index * desc.CoordSize;
        if (desc.CoordSize == 4)
        {
            *(float*)addr = (float)value;
        }
        else
        {
            *(double*)addr = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadLeafEntityId(byte* nodeBase, int index, in SpatialNodeDescriptor desc) => *(long*)(nodeBase + desc.LeafIdOffset + index * desc.LeafIdSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLeafEntityId(byte* nodeBase, int index, long entityId, in SpatialNodeDescriptor desc) =>
        *(long*)(nodeBase + desc.LeafIdOffset + index * desc.LeafIdSize) = entityId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadLeafCompChunkId(byte* nodeBase, int index, in SpatialNodeDescriptor desc) =>
        *(int*)(nodeBase + desc.LeafCompChunkIdOffset + index * desc.LeafCompChunkIdSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLeafCompChunkId(byte* nodeBase, int index, int compChunkId, in SpatialNodeDescriptor desc) =>
        *(int*)(nodeBase + desc.LeafCompChunkIdOffset + index * desc.LeafCompChunkIdSize) = compChunkId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadLeafCategoryMask(byte* nodeBase, int index, in SpatialNodeDescriptor desc) =>
        *(uint*)(nodeBase + desc.LeafCategoryMaskOffset + index * desc.LeafCategoryMaskSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLeafCategoryMask(byte* nodeBase, int index, uint mask, in SpatialNodeDescriptor desc) =>
        *(uint*)(nodeBase + desc.LeafCategoryMaskOffset + index * desc.LeafCategoryMaskSize) = mask;

    // ── Internal SOA access ─────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ReadInternalCoord(byte* nodeBase, int index, int coordIndex, in SpatialNodeDescriptor desc)
    {
        byte* addr = nodeBase + desc.HeaderSize + coordIndex * desc.InternalCoordStride + index * desc.CoordSize;
        return desc.CoordSize == 4 ? *(float*)addr : *(double*)addr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInternalCoord(byte* nodeBase, int index, int coordIndex, double value, in SpatialNodeDescriptor desc)
    {
        byte* addr = nodeBase + desc.HeaderSize + coordIndex * desc.InternalCoordStride + index * desc.CoordSize;
        if (desc.CoordSize == 4)
        {
            *(float*)addr = (float)value;
        }
        else
        {
            *(double*)addr = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadInternalChildId(byte* nodeBase, int index, in SpatialNodeDescriptor desc) =>
        *(int*)(nodeBase + desc.InternalIdOffset + index * desc.InternalIdSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInternalChildId(byte* nodeBase, int index, int childId, in SpatialNodeDescriptor desc) =>
        *(int*)(nodeBase + desc.InternalIdOffset + index * desc.InternalIdSize) = childId;

    // ── Bulk operations ─────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadLeafEntryCoords(byte* nodeBase, int index, Span<double> coords, in SpatialNodeDescriptor desc)
    {
        for (int c = 0; c < desc.CoordCount; c++)
        {
            coords[c] = ReadLeafCoord(nodeBase, index, c, desc);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadInternalEntryCoords(byte* nodeBase, int index, Span<double> coords, in SpatialNodeDescriptor desc)
    {
        for (int c = 0; c < desc.CoordCount; c++)
        {
            coords[c] = ReadInternalCoord(nodeBase, index, c, desc);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLeafEntryCoords(byte* nodeBase, int index, ReadOnlySpan<double> coords, in SpatialNodeDescriptor desc)
    {
        for (int c = 0; c < desc.CoordCount; c++)
        {
            WriteLeafCoord(nodeBase, index, c, coords[c], desc);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInternalEntryCoords(byte* nodeBase, int index, ReadOnlySpan<double> coords, in SpatialNodeDescriptor desc)
    {
        for (int c = 0; c < desc.CoordCount; c++)
        {
            WriteInternalCoord(nodeBase, index, c, coords[c], desc);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyLeafEntry(byte* nodeBase, int srcIdx, int dstIdx, in SpatialNodeDescriptor desc)
    {
        for (int c = 0; c < desc.CoordCount; c++)
        {
            WriteLeafCoord(nodeBase, dstIdx, c, ReadLeafCoord(nodeBase, srcIdx, c, desc), desc);
        }
        WriteLeafEntityId(nodeBase, dstIdx, ReadLeafEntityId(nodeBase, srcIdx, desc), desc);
        WriteLeafCompChunkId(nodeBase, dstIdx, ReadLeafCompChunkId(nodeBase, srcIdx, desc), desc);
        WriteLeafCategoryMask(nodeBase, dstIdx, ReadLeafCategoryMask(nodeBase, srcIdx, desc), desc);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyInternalEntry(byte* nodeBase, int srcIdx, int dstIdx, in SpatialNodeDescriptor desc)
    {
        for (int c = 0; c < desc.CoordCount; c++)
        {
            WriteInternalCoord(nodeBase, dstIdx, c, ReadInternalCoord(nodeBase, srcIdx, c, desc), desc);
        }
        WriteInternalChildId(nodeBase, dstIdx, ReadInternalChildId(nodeBase, srcIdx, desc), desc);
    }

    // ── MBR refit ───────────────────────────────────────────────────────────

    /// <summary>
    /// Recompute NodeMBR as the exact union of all leaf entries' coordinates, and recompute UnionCategoryMask as the bitwise OR of all leaf entries' category masks.
    /// First half of CoordCount are min coords, second half are max coords.
    /// </summary>
    public static void RefitLeafMBR(byte* nodeBase, in SpatialNodeDescriptor desc)
    {
        int count = GetCount(nodeBase);
        if (count == 0)
        {
            for (int c = 0; c < desc.CoordCount; c++)
            {
                WriteNodeMBRCoord(nodeBase, c, 0.0, desc);
            }
            WriteUnionCategoryMask(nodeBase, 0, desc);
            return;
        }

        int halfCoord = desc.CoordCount / 2;
        Span<double> mbr = stackalloc double[desc.CoordCount];
        ReadLeafEntryCoords(nodeBase, 0, mbr, desc);
        uint unionMask = ReadLeafCategoryMask(nodeBase, 0, desc);

        for (int i = 1; i < count; i++)
        {
            for (int c = 0; c < halfCoord; c++)
            {
                double v = ReadLeafCoord(nodeBase, i, c, desc);
                if (v < mbr[c])
                {
                    mbr[c] = v;
                }
            }
            for (int c = halfCoord; c < desc.CoordCount; c++)
            {
                double v = ReadLeafCoord(nodeBase, i, c, desc);
                if (v > mbr[c])
                {
                    mbr[c] = v;
                }
            }
            unionMask |= ReadLeafCategoryMask(nodeBase, i, desc);
        }

        for (int c = 0; c < desc.CoordCount; c++)
        {
            WriteNodeMBRCoord(nodeBase, c, mbr[c], desc);
        }
        WriteUnionCategoryMask(nodeBase, unionMask, desc);
    }

    /// <summary>
    /// Incrementally expand the NodeMBR and UnionCategoryMask to include a single new leaf entry.
    /// O(CoordCount) instead of O(N × CoordCount) — used after appending one entry to a non-empty leaf.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExpandLeafMBR(byte* nodeBase, int entryIndex, uint categoryMask, in SpatialNodeDescriptor desc)
    {
        int halfCoord = desc.CoordCount / 2;
        for (int c = 0; c < halfCoord; c++)
        {
            double v = ReadLeafCoord(nodeBase, entryIndex, c, desc);
            if (v < ReadNodeMBRCoord(nodeBase, c, desc))
            {
                WriteNodeMBRCoord(nodeBase, c, v, desc);
            }
        }
        for (int c = halfCoord; c < desc.CoordCount; c++)
        {
            double v = ReadLeafCoord(nodeBase, entryIndex, c, desc);
            if (v > ReadNodeMBRCoord(nodeBase, c, desc))
            {
                WriteNodeMBRCoord(nodeBase, c, v, desc);
            }
        }
        WriteUnionCategoryMask(nodeBase, ReadUnionCategoryMask(nodeBase, desc) | categoryMask, desc);
    }

    /// <summary>
    /// Recompute NodeMBR as the exact union of all internal entries' coordinates.
    /// </summary>
    public static void RefitInternalMBR(byte* nodeBase, in SpatialNodeDescriptor desc)
    {
        int count = GetCount(nodeBase);
        if (count == 0)
        {
            for (int c = 0; c < desc.CoordCount; c++)
            {
                WriteNodeMBRCoord(nodeBase, c, 0.0, desc);
            }
            return;
        }

        int halfCoord = desc.CoordCount / 2;
        Span<double> mbr = stackalloc double[desc.CoordCount];
        ReadInternalEntryCoords(nodeBase, 0, mbr, desc);

        for (int i = 1; i < count; i++)
        {
            for (int c = 0; c < halfCoord; c++)
            {
                double v = ReadInternalCoord(nodeBase, i, c, desc);
                if (v < mbr[c])
                {
                    mbr[c] = v;
                }
            }
            for (int c = halfCoord; c < desc.CoordCount; c++)
            {
                double v = ReadInternalCoord(nodeBase, i, c, desc);
                if (v > mbr[c])
                {
                    mbr[c] = v;
                }
            }
        }

        for (int c = 0; c < desc.CoordCount; c++)
        {
            WriteNodeMBRCoord(nodeBase, c, mbr[c], desc);
        }
    }
}
