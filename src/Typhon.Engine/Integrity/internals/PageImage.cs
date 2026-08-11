using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Byte-level accessors over a raw 8 KiB page image, for a reader that must never trust what it is decoding.
/// </summary>
/// <remarks>
/// Every accessor is a bounded read over a <see cref="ReadOnlySpan{T}"/> — no pointers, no <c>StructAt</c> reinterpretation
/// of a possibly-garbage header, no dereferencing of a value that has not been range-checked by the caller. This is the
/// traversal-safety requirement <c>MAP-04</c> makes explicit: a checker that crashes on the databases it was built to
/// diagnose is worse than useless.
/// </remarks>
internal static class PageImage
{
    /// <summary>Byte offset of the page-block flags byte.</summary>
    public const int FlagsOffset = 0;

    /// <summary>Byte offset of the page-block type byte.</summary>
    public const int TypeOffset = 1;

    /// <summary>Byte offset of the type-scoped format revision.</summary>
    public const int FormatRevisionOffset = 2;

    /// <summary>Byte offset of the change revision, incremented on every write to disk.</summary>
    public const int ChangeRevisionOffset = 4;

    /// <summary>Byte offset of the seqlock-style modification counter.</summary>
    public const int ModificationCounterOffset = 12;

    /// <summary>Byte offset of the segment-header zone present on directory pages.</summary>
    public const int LogicalSegmentHeaderOffset = 16;

    /// <summary>Byte offset within the segment header of the next map-extension page index.</summary>
    public const int NextMapPageOffset = LogicalSegmentHeaderOffset + 0;

    /// <summary>Byte offset within the segment header of the next raw-data page index.</summary>
    public const int NextRawDataPageOffset = LogicalSegmentHeaderOffset + 4;

    /// <summary>Byte offset within the segment header of the segment kind.</summary>
    public const int SegmentKindOffset = LogicalSegmentHeaderOffset + 8;

    /// <summary>Byte offset within the segment header of the A/B twin page index.</summary>
    public const int TwinPageOffset = LogicalSegmentHeaderOffset + 12;

    /// <summary>Byte offset of the start of the page metadata region.</summary>
    public const int MetadataOffset = 64;

    /// <summary>Byte offset of the start of the page raw-data region.</summary>
    public const int RawDataOffset = IntegrityConstants.PageHeaderSize;

    /// <summary>Reads the page-block flags.</summary>
    /// <param name="page">A full page image.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PageBlockFlags Flags(ReadOnlySpan<byte> page) => (PageBlockFlags)page[FlagsOffset];

    /// <summary>Reads the page-block type.</summary>
    /// <param name="page">A full page image.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PageBlockType Type(ReadOnlySpan<byte> page) => (PageBlockType)page[TypeOffset];

    /// <summary>Reads the type-scoped format revision.</summary>
    /// <param name="page">A full page image.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short FormatRevision(ReadOnlySpan<byte> page) => MemoryMarshal.Read<short>(page[FormatRevisionOffset..]);

    /// <summary>Reads the change revision — incremented on every write of this page to disk.</summary>
    /// <param name="page">A full page image.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ChangeRevision(ReadOnlySpan<byte> page) => MemoryMarshal.Read<int>(page[ChangeRevisionOffset..]);

    /// <summary>Reads the stored page checksum.</summary>
    /// <param name="page">A full page image.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint StoredChecksum(ReadOnlySpan<byte> page) => MemoryMarshal.Read<uint>(page[PageBaseHeader.PageChecksumOffset..]);

    /// <summary>Reads the seqlock modification counter. Odd on disk is evidence of a write that bypassed the seqlock.</summary>
    /// <param name="page">A full page image.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ModificationCounter(ReadOnlySpan<byte> page) => MemoryMarshal.Read<int>(page[ModificationCounterOffset..]);

    /// <summary>Reads the A/B pair generation stamped on a protected page. <c>0</c> means "not a pair slot".</summary>
    /// <param name="page">A full page image.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong PairGeneration(ReadOnlySpan<byte> page) => MemoryMarshal.Read<ulong>(page[PageBaseHeader.PairGenerationOffset..]);

    /// <summary>Reads the next map-extension page index from a directory page's segment header.</summary>
    /// <param name="page">A full page image.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextMapPage(ReadOnlySpan<byte> page) => MemoryMarshal.Read<int>(page[NextMapPageOffset..]);

    /// <summary>Reads the next raw-data page index from a page's segment header (the forward chain).</summary>
    /// <param name="page">A full page image.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NextRawDataPage(ReadOnlySpan<byte> page) => MemoryMarshal.Read<int>(page[NextRawDataPageOffset..]);

    /// <summary>Reads the segment kind from a root page's segment header.</summary>
    /// <param name="page">A full page image.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StorageSegmentKind SegmentKind(ReadOnlySpan<byte> page) => (StorageSegmentKind)page[SegmentKindOffset];

    /// <summary>Reads the A/B twin page index from a directory page's segment header. <c>0</c> means "no twin".</summary>
    /// <param name="page">A full page image.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TwinPage(ReadOnlySpan<byte> page) => MemoryMarshal.Read<int>(page[TwinPageOffset..]);

    /// <summary>The page's raw-data region.</summary>
    /// <param name="page">A full page image.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> RawData(ReadOnlySpan<byte> page) => page[RawDataOffset..];

    /// <summary>The page's metadata region — the chunk-occupancy bitmap and the per-sector verification footer.</summary>
    /// <param name="page">A full page image.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> Metadata(ReadOnlySpan<byte> page) => page.Slice(MetadataOffset, IntegrityConstants.PageHeaderSize - MetadataOffset);

    /// <summary>Whether the flags byte holds only defined bits.</summary>
    /// <param name="flags">The flags value to validate.</param>
    public static bool AreFlagsWellFormed(PageBlockFlags flags)
    {
        const PageBlockFlags all = PageBlockFlags.IsFree | PageBlockFlags.IsLogicalSegment | PageBlockFlags.IsLogicalSegmentRoot;
        if ((flags & ~all) != 0)
        {
            return false;
        }

        // A root implies membership; free and allocated are mutually exclusive.
        if ((flags & PageBlockFlags.IsLogicalSegmentRoot) != 0 && (flags & PageBlockFlags.IsLogicalSegment) == 0)
        {
            return false;
        }

        return (flags & PageBlockFlags.IsFree) == 0 || (flags & PageBlockFlags.IsLogicalSegment) == 0;
    }

    /// <summary>Whether the type byte is a defined <see cref="PageBlockType"/>.</summary>
    /// <param name="type">The type value to validate.</param>
    public static bool IsTypeDefined(PageBlockType type) => type is PageBlockType.None or PageBlockType.OccupancyMap;

    /// <summary>Whether the kind byte is a defined <see cref="StorageSegmentKind"/>.</summary>
    /// <param name="kind">The kind value to validate.</param>
    public static bool IsKindDefined(StorageSegmentKind kind) => kind <= StorageSegmentKind.System;

    /// <summary>
    /// Verifies a page exactly the way the engine's load path does, honouring whichever checksum form the page declared —
    /// per-sector footer or single whole-page CRC.
    /// </summary>
    /// <param name="page">A full page image.</param>
    /// <param name="computed">Receives the computed whole-page value when the page uses that form; <c>0</c> otherwise.</param>
    public static bool VerifyWholePageChecksum(ReadOnlySpan<byte> page, out uint computed) => PagedMMF.VerifyPageImage(page, out computed);
}
