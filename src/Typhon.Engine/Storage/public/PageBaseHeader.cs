using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>Bit flags stamped in the first byte of a page's <see cref="PageBaseHeader"/>, describing the page's role in a logical segment.</summary>
[Flags]
public enum PageBlockFlags : byte
{
    /// <summary>No flags set.</summary>
    None                 = 0x00,
    /// <summary>The page is free — not allocated to any segment.</summary>
    IsFree               = 0x01,
    /// <summary>The page belongs to a logical segment.</summary>
    IsLogicalSegment     = 0x02,
    /// <summary>The page is the root page of its logical segment.</summary>
    IsLogicalSegmentRoot = 0x04
}

/// <summary>Coarse structural category of a page, stored in <see cref="PageBaseHeader.Type"/>.</summary>
public enum PageBlockType : byte
{
    /// <summary>No specific block type.</summary>
    None = 0,
    /// <summary>The page holds part of the occupancy bitmap.</summary>
    OccupancyMap,
}

/// <summary>
/// The fixed 16-byte header at the start of every storage page: role flags, block type, format/change revisions, a CRC32C checksum, and a seqlock-style
/// modification counter for torn-page detection. Laid out sequentially with 4-byte packing and kept at exactly 16 bytes so page-layout offsets stay stable.
/// </summary>
[PublicAPI]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PageBaseHeader
{
    /// <summary>Byte offset of this header within a page — always <c>0</c> (the header sits at the page start).</summary>
    public static readonly int Offset;
    /// <summary>Size of the header in bytes.</summary>
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
    /// Zero is the sentinel for a page that has not yet been checksummed.
    /// Computed via <c>Crc32CUtil.ComputeSkipping(pageSpan, PageChecksumOffset, PageChecksumSize)</c>.
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
    /// Byte offset of the A/B slot-pairing generation counter (CK-05). <c>0</c> = "not a pair slot" (every normal page).
    /// Protected pages (the meta pair; segment-directory twins in C2) stamp a monotonic <see cref="ulong"/> here; the
    /// higher valid generation among a pair's two slots is the current one. CRC-covered (it is outside the 8–11 skip region).
    /// <para>
    /// The offset is <b>40</b> — the first 8-aligned slot free on <i>every</i> page type, which CK-05 requires (the same
    /// offset is read/written uniformly regardless of what header the page otherwise carries). The page header zone packs:
    /// <c>[0,16)</c> <see cref="PageBaseHeader"/>; <c>[16,32)</c> <c>LogicalSegmentHeader</c> on a directory page (so offsets
    /// 16–31 are NOT free there — they are the directory's map/raw chain pointers + kind + twin index); <c>[32,36)</c>
    /// <c>ChunkBasedSegmentHeader</c> on a chunk-segment directory page. The intersection of the free regions — meta
    /// <c>[16,64)</c>, plain-dir <c>[32,64)</c>, chunk-dir <c>[36,64)</c> — first hits an 8-aligned slot at 40. Accessed by
    /// offset rather than a struct field so <c>sizeof(PageBaseHeader)</c> stays 16 and no dependent layout shifts.
    /// </para>
    /// </summary>
    public const int PairGenerationOffset = 40;

    /// <summary>Reads the CK-05 pair generation (<see cref="PairGenerationOffset"/>) from a page image.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadPairGeneration(ReadOnlySpan<byte> page) => MemoryMarshal.Read<ulong>(page.Slice(PairGenerationOffset));

    /// <summary>Writes the CK-05 pair generation (<see cref="PairGenerationOffset"/>) into a page image.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WritePairGeneration(Span<byte> page, ulong generation) => MemoryMarshal.Write(page.Slice(PairGenerationOffset), in generation);
}