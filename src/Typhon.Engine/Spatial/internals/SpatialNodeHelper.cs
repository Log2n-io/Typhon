using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Low-level SOA read/write helpers for spatial R-Tree nodes. All methods operate on a lifetime-checked <c>ref byte</c> node base reference plus a
/// <see cref="SpatialNodeDescriptor"/>. Coordinates are passed as <c>double</c> internally — the CoordSize branch (float vs double at the SOA boundary) is
/// eliminated by the JIT since descriptor fields are readonly. <c>Unsafe.Add</c>/<c>Unsafe.As</c> emit identical machine code to the former pointer arithmetic.
/// </summary>
internal static class SpatialNodeHelper
{
    // ── Header access (fixed offsets for all variants) ──────────────────────

    /// <summary>Returns a ref to the OlcVersion int at offset 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref int OlcVersionRef(ref byte nodeBase) => ref Unsafe.As<byte, int>(ref nodeBase);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetCount(ref byte nodeBase) => Unsafe.As<byte, int>(ref Unsafe.Add(ref nodeBase, 4)) & 0xFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetCount(ref byte nodeBase, int count)
    {
        ref int control = ref Unsafe.As<byte, int>(ref Unsafe.Add(ref nodeBase, 4));
        control = (control & ~0xFF) | (count & 0xFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLeaf(ref byte nodeBase) => (Unsafe.As<byte, int>(ref Unsafe.Add(ref nodeBase, 4)) & 0x100) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetIsLeaf(ref byte nodeBase, bool isLeaf)
    {
        ref int control = ref Unsafe.As<byte, int>(ref Unsafe.Add(ref nodeBase, 4));
        control = isLeaf ? (control | 0x100) : (control & ~0x100);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetParentChunkId(ref byte nodeBase) => Unsafe.As<byte, int>(ref Unsafe.Add(ref nodeBase, 8));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetParentChunkId(ref byte nodeBase, int parentChunkId) => Unsafe.As<byte, int>(ref Unsafe.Add(ref nodeBase, 8)) = parentChunkId;

    // ── NodeMBR access (offset 12, variable size: CoordCount * CoordSize) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ReadNodeMBRCoord(ref byte nodeBase, int coordIndex, in SpatialNodeDescriptor desc)
    {
        ref byte addr = ref Unsafe.Add(ref nodeBase, 12 + coordIndex * desc.CoordSize);
        return desc.CoordSize == 4 ? Unsafe.As<byte, float>(ref addr) : Unsafe.As<byte, double>(ref addr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNodeMBRCoord(ref byte nodeBase, int coordIndex, double value, in SpatialNodeDescriptor desc)
    {
        ref byte addr = ref Unsafe.Add(ref nodeBase, 12 + coordIndex * desc.CoordSize);
        if (desc.CoordSize == 4)
        {
            Unsafe.As<byte, float>(ref addr) = (float)value;
        }
        else
        {
            Unsafe.As<byte, double>(ref addr) = value;
        }
    }

    // ── UnionCategoryMask access (header field, after NodeMBR) ─────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUnionCategoryMask(ref byte nodeBase, in SpatialNodeDescriptor desc) =>
        Unsafe.As<byte, uint>(ref Unsafe.Add(ref nodeBase, desc.UnionCategoryMaskOffset));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUnionCategoryMask(ref byte nodeBase, uint mask, in SpatialNodeDescriptor desc) =>
        Unsafe.As<byte, uint>(ref Unsafe.Add(ref nodeBase, desc.UnionCategoryMaskOffset)) = mask;

    // ── Leaf SOA access ─────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ReadLeafCoord(ref byte nodeBase, int index, int coordIndex, in SpatialNodeDescriptor desc)
    {
        ref byte addr = ref Unsafe.Add(ref nodeBase, desc.LeafCoordOffsets + coordIndex * desc.LeafCoordStride + index * desc.CoordSize);
        return desc.CoordSize == 4 ? Unsafe.As<byte, float>(ref addr) : Unsafe.As<byte, double>(ref addr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLeafCoord(ref byte nodeBase, int index, int coordIndex, double value, in SpatialNodeDescriptor desc)
    {
        ref byte addr = ref Unsafe.Add(ref nodeBase, desc.LeafCoordOffsets + coordIndex * desc.LeafCoordStride + index * desc.CoordSize);
        if (desc.CoordSize == 4)
        {
            Unsafe.As<byte, float>(ref addr) = (float)value;
        }
        else
        {
            Unsafe.As<byte, double>(ref addr) = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadLeafEntityId(ref byte nodeBase, int index, in SpatialNodeDescriptor desc) =>
        Unsafe.As<byte, long>(ref Unsafe.Add(ref nodeBase, desc.LeafIdOffset + index * desc.LeafIdSize));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLeafEntityId(ref byte nodeBase, int index, long entityId, in SpatialNodeDescriptor desc) =>
        Unsafe.As<byte, long>(ref Unsafe.Add(ref nodeBase, desc.LeafIdOffset + index * desc.LeafIdSize)) = entityId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadLeafCompChunkId(ref byte nodeBase, int index, in SpatialNodeDescriptor desc) =>
        Unsafe.As<byte, int>(ref Unsafe.Add(ref nodeBase, desc.LeafCompChunkIdOffset + index * desc.LeafCompChunkIdSize));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLeafCompChunkId(ref byte nodeBase, int index, int compChunkId, in SpatialNodeDescriptor desc) =>
        Unsafe.As<byte, int>(ref Unsafe.Add(ref nodeBase, desc.LeafCompChunkIdOffset + index * desc.LeafCompChunkIdSize)) = compChunkId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadLeafCategoryMask(ref byte nodeBase, int index, in SpatialNodeDescriptor desc) =>
        Unsafe.As<byte, uint>(ref Unsafe.Add(ref nodeBase, desc.LeafCategoryMaskOffset + index * desc.LeafCategoryMaskSize));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLeafCategoryMask(ref byte nodeBase, int index, uint mask, in SpatialNodeDescriptor desc) =>
        Unsafe.As<byte, uint>(ref Unsafe.Add(ref nodeBase, desc.LeafCategoryMaskOffset + index * desc.LeafCategoryMaskSize)) = mask;

    // ── Internal SOA access ─────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ReadInternalCoord(ref byte nodeBase, int index, int coordIndex, in SpatialNodeDescriptor desc)
    {
        ref byte addr = ref Unsafe.Add(ref nodeBase, desc.HeaderSize + coordIndex * desc.InternalCoordStride + index * desc.CoordSize);
        return desc.CoordSize == 4 ? Unsafe.As<byte, float>(ref addr) : Unsafe.As<byte, double>(ref addr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInternalCoord(ref byte nodeBase, int index, int coordIndex, double value, in SpatialNodeDescriptor desc)
    {
        ref byte addr = ref Unsafe.Add(ref nodeBase, desc.HeaderSize + coordIndex * desc.InternalCoordStride + index * desc.CoordSize);
        if (desc.CoordSize == 4)
        {
            Unsafe.As<byte, float>(ref addr) = (float)value;
        }
        else
        {
            Unsafe.As<byte, double>(ref addr) = value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadInternalChildId(ref byte nodeBase, int index, in SpatialNodeDescriptor desc) =>
        Unsafe.As<byte, int>(ref Unsafe.Add(ref nodeBase, desc.InternalIdOffset + index * desc.InternalIdSize));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInternalChildId(ref byte nodeBase, int index, int childId, in SpatialNodeDescriptor desc) =>
        Unsafe.As<byte, int>(ref Unsafe.Add(ref nodeBase, desc.InternalIdOffset + index * desc.InternalIdSize)) = childId;

    // ── Bulk operations ─────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadLeafEntryCoords(ref byte nodeBase, int index, Span<double> coords, in SpatialNodeDescriptor desc)
    {
        for (int c = 0; c < desc.CoordCount; c++)
        {
            coords[c] = ReadLeafCoord(ref nodeBase, index, c, desc);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadInternalEntryCoords(ref byte nodeBase, int index, Span<double> coords, in SpatialNodeDescriptor desc)
    {
        for (int c = 0; c < desc.CoordCount; c++)
        {
            coords[c] = ReadInternalCoord(ref nodeBase, index, c, desc);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLeafEntryCoords(ref byte nodeBase, int index, ReadOnlySpan<double> coords, in SpatialNodeDescriptor desc)
    {
        for (int c = 0; c < desc.CoordCount; c++)
        {
            WriteLeafCoord(ref nodeBase, index, c, coords[c], desc);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInternalEntryCoords(ref byte nodeBase, int index, ReadOnlySpan<double> coords, in SpatialNodeDescriptor desc)
    {
        for (int c = 0; c < desc.CoordCount; c++)
        {
            WriteInternalCoord(ref nodeBase, index, c, coords[c], desc);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyLeafEntry(ref byte nodeBase, int srcIdx, int dstIdx, in SpatialNodeDescriptor desc)
    {
        for (int c = 0; c < desc.CoordCount; c++)
        {
            WriteLeafCoord(ref nodeBase, dstIdx, c, ReadLeafCoord(ref nodeBase, srcIdx, c, desc), desc);
        }
        WriteLeafEntityId(ref nodeBase, dstIdx, ReadLeafEntityId(ref nodeBase, srcIdx, desc), desc);
        WriteLeafCompChunkId(ref nodeBase, dstIdx, ReadLeafCompChunkId(ref nodeBase, srcIdx, desc), desc);
        WriteLeafCategoryMask(ref nodeBase, dstIdx, ReadLeafCategoryMask(ref nodeBase, srcIdx, desc), desc);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyInternalEntry(ref byte nodeBase, int srcIdx, int dstIdx, in SpatialNodeDescriptor desc)
    {
        for (int c = 0; c < desc.CoordCount; c++)
        {
            WriteInternalCoord(ref nodeBase, dstIdx, c, ReadInternalCoord(ref nodeBase, srcIdx, c, desc), desc);
        }
        WriteInternalChildId(ref nodeBase, dstIdx, ReadInternalChildId(ref nodeBase, srcIdx, desc), desc);
    }

    // ── MBR refit ───────────────────────────────────────────────────────────

    /// <summary>
    /// Recompute NodeMBR as the exact union of all leaf entries' coordinates, and recompute UnionCategoryMask as the bitwise OR of all leaf entries' category masks.
    /// First half of CoordCount are min coords, second half are max coords.
    /// </summary>
    public static void RefitLeafMBR(ref byte nodeBase, in SpatialNodeDescriptor desc)
    {
        int count = GetCount(ref nodeBase);
        if (count == 0)
        {
            for (int c = 0; c < desc.CoordCount; c++)
            {
                WriteNodeMBRCoord(ref nodeBase, c, 0.0, desc);
            }
            WriteUnionCategoryMask(ref nodeBase, 0, desc);
            return;
        }

        int halfCoord = desc.CoordCount / 2;
        Span<double> mbr = stackalloc double[desc.CoordCount];
        ReadLeafEntryCoords(ref nodeBase, 0, mbr, desc);
        uint unionMask = ReadLeafCategoryMask(ref nodeBase, 0, desc);

        for (int i = 1; i < count; i++)
        {
            for (int c = 0; c < halfCoord; c++)
            {
                double v = ReadLeafCoord(ref nodeBase, i, c, desc);
                if (v < mbr[c])
                {
                    mbr[c] = v;
                }
            }
            for (int c = halfCoord; c < desc.CoordCount; c++)
            {
                double v = ReadLeafCoord(ref nodeBase, i, c, desc);
                if (v > mbr[c])
                {
                    mbr[c] = v;
                }
            }
            unionMask |= ReadLeafCategoryMask(ref nodeBase, i, desc);
        }

        for (int c = 0; c < desc.CoordCount; c++)
        {
            WriteNodeMBRCoord(ref nodeBase, c, mbr[c], desc);
        }
        WriteUnionCategoryMask(ref nodeBase, unionMask, desc);
    }

    /// <summary>
    /// Incrementally expand the NodeMBR and UnionCategoryMask to include a single new leaf entry.
    /// O(CoordCount) instead of O(N × CoordCount) — used after appending one entry to a non-empty leaf.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExpandLeafMBR(ref byte nodeBase, int entryIndex, uint categoryMask, in SpatialNodeDescriptor desc)
    {
        int halfCoord = desc.CoordCount / 2;
        for (int c = 0; c < halfCoord; c++)
        {
            double v = ReadLeafCoord(ref nodeBase, entryIndex, c, desc);
            if (v < ReadNodeMBRCoord(ref nodeBase, c, desc))
            {
                WriteNodeMBRCoord(ref nodeBase, c, v, desc);
            }
        }
        for (int c = halfCoord; c < desc.CoordCount; c++)
        {
            double v = ReadLeafCoord(ref nodeBase, entryIndex, c, desc);
            if (v > ReadNodeMBRCoord(ref nodeBase, c, desc))
            {
                WriteNodeMBRCoord(ref nodeBase, c, v, desc);
            }
        }
        WriteUnionCategoryMask(ref nodeBase, ReadUnionCategoryMask(ref nodeBase, desc) | categoryMask, desc);
    }

    /// <summary>
    /// Recompute NodeMBR as the exact union of all internal entries' coordinates.
    /// </summary>
    public static void RefitInternalMBR(ref byte nodeBase, in SpatialNodeDescriptor desc)
    {
        int count = GetCount(ref nodeBase);
        if (count == 0)
        {
            for (int c = 0; c < desc.CoordCount; c++)
            {
                WriteNodeMBRCoord(ref nodeBase, c, 0.0, desc);
            }
            return;
        }

        int halfCoord = desc.CoordCount / 2;
        Span<double> mbr = stackalloc double[desc.CoordCount];
        ReadInternalEntryCoords(ref nodeBase, 0, mbr, desc);

        for (int i = 1; i < count; i++)
        {
            for (int c = 0; c < halfCoord; c++)
            {
                double v = ReadInternalCoord(ref nodeBase, i, c, desc);
                if (v < mbr[c])
                {
                    mbr[c] = v;
                }
            }
            for (int c = halfCoord; c < desc.CoordCount; c++)
            {
                double v = ReadInternalCoord(ref nodeBase, i, c, desc);
                if (v > mbr[c])
                {
                    mbr[c] = v;
                }
            }
        }

        for (int c = 0; c < desc.CoordCount; c++)
        {
            WriteNodeMBRCoord(ref nodeBase, c, mbr[c], desc);
        }
    }
}
