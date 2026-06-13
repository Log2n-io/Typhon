using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

[Flags]
public enum PageBlockFlags : byte
{
    None                 = 0x00,
    IsFree               = 0x01,
    IsLogicalSegment     = 0x02,
    IsLogicalSegmentRoot = 0x04
}

public enum PageBlockType : byte
{
    None = 0,
    OccupancyMap,
}

[PublicAPI]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PageBaseHeader
{
    public static readonly int Offset;
    unsafe public static readonly int Size = sizeof(PageBaseHeader);

    /// <summary>
    /// Combination of one to many flags
    /// </summary>
    public PageBlockFlags Flags;          // NOTE: keep this field as the first byte of the header because we perform direct access on it sometimes
    /// <summary>
    /// Block Type
    /// </summary>
    public PageBlockType Type;
    /// <summary>
    /// Revision number specific to the Page Block Type, to support basic versioning.
    /// </summary>
    public short FormatRevision;
    /// <summary>
    /// The Change Revision is incremented every time the Page is written to disk.
    /// </summary>
    public int ChangeRevision;

    /// <summary>
    /// CRC32C checksum of the page contents, excluding this field itself.
    /// Zero means "never checksummed" (correct sentinel for pages that predate FPI support).
    /// Computed via <c>WalCrc.ComputeSkipping(pageSpan, PageChecksumOffset, PageChecksumSize)</c>.
    /// </summary>
    public uint PageChecksum;

    /// <summary>
    /// Seqlock-style modification counter for torn-page detection.
    /// Even values indicate the page is quiescent; odd values indicate an in-progress modification.
    /// Readers compare before/after to detect torn writes.
    /// </summary>
    public int ModificationCounter;

    /// <summary>Byte offset of <see cref="PageChecksum"/> within the page header.</summary>
    public const int PageChecksumOffset = 8;

    /// <summary>Size in bytes of <see cref="PageChecksum"/> (for CRC skip region).</summary>
    public const int PageChecksumSize = 4;

    /// <summary>
    /// Byte offset of the A/B slot-pairing generation counter (CK-05), in the reserved header region (offset 16, the
    /// first free 8-aligned slot after <see cref="ModificationCounter"/>). <c>0</c> = "not a pair slot" (every normal
    /// page). Protected pages (the meta pair; segment-directory twins in C2) stamp a monotonic <see cref="ulong"/> here;
    /// the higher valid generation among a pair's two slots is the current one. CRC-covered (it is outside the 8–11 skip
    /// region). Accessed by offset rather than a struct field so <c>sizeof(PageBaseHeader)</c> stays 16 and no dependent
    /// page layout (e.g. <c>LogicalSegmentHeader.Offset = PageBaseHeader.Size</c>) shifts.
    /// </summary>
    public const int PairGenerationOffset = 16;

    /// <summary>Reads the CK-05 pair generation (<see cref="PairGenerationOffset"/>) from a page image.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadPairGeneration(ReadOnlySpan<byte> page) => MemoryMarshal.Read<ulong>(page.Slice(PairGenerationOffset));

    /// <summary>Writes the CK-05 pair generation (<see cref="PairGenerationOffset"/>) into a page image.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WritePairGeneration(Span<byte> page, ulong generation) => MemoryMarshal.Write(page.Slice(PairGenerationOffset), in generation);
}