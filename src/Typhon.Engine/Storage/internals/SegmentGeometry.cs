using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// The arithmetic an offline reader needs to interpret a chunk-based segment, written into free bytes of every one of
/// its pages.
/// </summary>
/// <remarks>
/// <para>
/// Chunk stride is not derivable from the file. <c>ChunkBasedSegment</c> takes it as a constructor argument, and the
/// caller derives it from a CLR component type — so a reader with no schema assembly can find a segment, read its
/// directory and classify its pages, but cannot locate chunk <i>n</i> inside them. That single missing integer is what
/// blocks the whole cross-structure check family
/// (<c>claude/design/Durability/Integrity/09-closing-the-gap.md</c> §2): every CHN check reads engine-defined chunk
/// headers, never schema-shaped payload, and still cannot reach them.
/// </para>
/// <para>
/// The precedent is <c>LogicalSegmentHeader.Kind</c>, which exists so storage introspection can classify a page
/// <i>without re-deriving ownership from context</i>. Stride is the same class of fact about the same page, and its
/// absence is the reason the file cannot describe itself.
/// </para>
/// <para>
/// <b>Every page, not only the root.</b> A reader that only ever entered through the root would need it there alone,
/// but a repair tool meets torn roots — that is its job — and four bytes of already-free header space per page is not
/// a cost worth trading for losing the ability to interpret a segment whose root is gone.
/// </para>
/// <para>
/// <b>Where the bytes live.</b> The page header zone is <c>[0,64)</c>: <c>[0,16)</c> <see cref="PageBaseHeader"/>,
/// <c>[16,32)</c> <c>LogicalSegmentHeader</c>, <c>[32,36)</c> <c>ChunkBasedSegmentHeader</c>, <c>[40,48)</c> the CK-05
/// pair generation, <c>[48,54)</c> the <see cref="PageSectorFooter"/> geometry declaration. This claims
/// <c>[54,58)</c> and leaves <c>[58,64)</c> free. No page-layout growth, and no effect on chunk capacity — the
/// metadata region <c>[64,192)</c> where the occupancy bitmap and sector footers live is untouched.
/// </para>
/// </remarks>
internal static class SegmentGeometry
{
    /// <summary>
    /// Byte offset of the chunk stride. A <see cref="ushort"/> because a chunk cannot exceed one page's raw data area
    /// (<c>PageRawDataSize</c> = 8000), so the wider type would buy nothing and cost two of six remaining free bytes.
    /// </summary>
    public const int StrideOffset = 54;

    /// <summary>First byte after the geometry block. <c>[58,64)</c> remains free for a later increment.</summary>
    public const int EndOffset = 58;

    /// <summary>Largest stride expressible here. A chunk must fit within one page's raw data area, so this is never binding in practice.</summary>
    public const int MaxStride = ushort.MaxValue;

    /// <summary>Writes the stride into a page image. <c>0</c> means "not a chunk-based page", which is the default state.</summary>
    /// <param name="page">The whole page image.</param>
    /// <param name="stride">Chunk stride in bytes, or <c>0</c> for a segment that has no chunks.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteStride(Span<byte> page, int stride)
    {
        if (stride is < 0 or > MaxStride)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride,
                $"A chunk stride must fit in [0, {MaxStride}] to be recorded on the page; a chunk cannot exceed one page's raw data area.");
        }

        MemoryMarshal.Write(page.Slice(StrideOffset), (ushort)stride);
    }

    /// <summary>Reads the recorded stride from a page image; <c>0</c> when the page records none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadStride(ReadOnlySpan<byte> page) => MemoryMarshal.Read<ushort>(page.Slice(StrideOffset));
}
