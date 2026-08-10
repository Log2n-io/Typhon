using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Locates chunk <i>n</i> inside a chunk-based segment from raw bytes, using nothing but the stride the page records.
/// </summary>
/// <remarks>
/// <para>
/// This is the payoff of format revision 7 and the gate on the entire cross-structure check family
/// (<c>claude/design/Durability/Integrity/09-closing-the-gap.md</c> §2): every <c>CHN</c>, <c>MAP</c> and <c>ALO</c> check
/// reads <b>engine-defined</b> chunk headers, never schema-shaped payload — and still could not reach them, because
/// stride arrived as a constructor argument derived from a CLR type and was nowhere on disk.
/// </para>
/// <para>
/// <b>Every other quantity here is arithmetic, not information.</b> Alignment padding, chunks per page, the size of the
/// occupancy bitmap, the offset chunk 0 starts at — each is a pure function of the stride and of engine constants that do
/// not vary per segment. That is the finding that made "persist geometry" a four-byte change rather than a schema
/// project, and <c>ChunkGeometryAgreementTests</c> holds it to account: the numbers below are compared against the
/// engine's own for every stride a component can have. A re-derivation that drifts from the original is worse than no
/// re-derivation, because it reads plausible chunk headers out of the wrong offsets.
/// </para>
/// <para>
/// <b>The root page holds no chunks.</b> Since the v4 directory-only root the root's whole raw-data area is the segment's
/// page directory, so <c>ChunkCountRootPage</c> is zero for <i>every</i> stride and chunk 0 lives on the segment's second
/// page. The engine's <c>GetChunkLocation</c> keeps a general branch for it; this mirrors the branch rather than
/// hard-coding the zero, so the two agree even if that ever changes.
/// </para>
/// </remarks>
internal readonly struct ChunkGeometry
{
    /// <summary>Offset of the metadata region, where the chunk-occupancy bitmap starts.</summary>
    internal const int MetadataOffset = 64;

    /// <summary>Cache-line ceiling on chunk-start alignment. Mirrors <c>PagedMMF.ChunkStartAlignment</c>.</summary>
    private const int ChunkStartAlignment = 64;

    /// <summary>
    /// Bytes of the root page reserved for the segment's own page directory. Mirrors
    /// <c>LogicalSegment.RootHeaderIndexSectionLength</c>, which is the whole raw-data area since v4.
    /// </summary>
    private const int RootDirectoryBytes = IntegrityConstants.PageRawDataSize;

    private ChunkGeometry(int stride)
    {
        Stride = stride;

        var align = Math.Min(stride, ChunkStartAlignment);
        var needsAlignment = IntegrityConstants.PageHeaderSize % align != 0;
        var otherPadding = needsAlignment ? align - (IntegrityConstants.PageHeaderSize % align) : 0;
        var rootPadding = needsAlignment ? (align - ((IntegrityConstants.PageHeaderSize + RootDirectoryBytes) % align)) % align : 0;

        ChunkCountRootPage = (IntegrityConstants.PageRawDataSize - RootDirectoryBytes - rootPadding) / stride;
        ChunkCountPerPage = (IntegrityConstants.PageRawDataSize - otherPadding) / stride;
        RootDataOffset = IntegrityConstants.PageHeaderSize + RootDirectoryBytes + rootPadding;
        OtherDataOffset = IntegrityConstants.PageHeaderSize + otherPadding;
        BitmapLongsRoot = (ChunkCountRootPage + 63) >> 6;
        BitmapLongsOther = (ChunkCountPerPage + 63) >> 6;
    }

    /// <summary>Chunk stride in bytes.</summary>
    public int Stride { get; }

    /// <summary>Chunks the root page holds. Zero since v4; kept as a computed value rather than an assumption.</summary>
    public int ChunkCountRootPage { get; }

    /// <summary>Chunks every non-root page of the segment holds.</summary>
    public int ChunkCountPerPage { get; }

    /// <summary>Byte offset within the root page where its chunk 0 would begin.</summary>
    public int RootDataOffset { get; }

    /// <summary>Byte offset within a non-root page where its chunk 0 begins.</summary>
    public int OtherDataOffset { get; }

    /// <summary>Occupancy-bitmap words on the root page.</summary>
    public int BitmapLongsRoot { get; }

    /// <summary>Occupancy-bitmap words on a non-root page.</summary>
    public int BitmapLongsOther { get; }

    /// <summary>Whether this geometry can address anything at all.</summary>
    public bool IsUsable => Stride > 0 && ChunkCountPerPage > 0;

    /// <summary>
    /// Builds the geometry a page declares, or an unusable one when the page records no stride.
    /// </summary>
    /// <remarks>
    /// A recorded stride of <c>0</c> means "this page holds no chunks" and is the state of every non-chunk segment. It is
    /// deliberately NOT conflated with "stride unknown": a walker that treated the two alike would start doing chunk
    /// arithmetic on a page that has none. Callers gate on <see cref="IsUsable"/>.
    /// </remarks>
    /// <param name="page">A whole page image.</param>
    public static ChunkGeometry FromPage(ReadOnlySpan<byte> page)
    {
        var stride = SegmentGeometry.ReadStride(page);
        return stride >= sizeof(long) ? new ChunkGeometry(stride) : default;
    }

    /// <summary>Builds the geometry for a known stride. For tests and for callers that already resolved it.</summary>
    /// <param name="stride">Chunk stride in bytes.</param>
    public static ChunkGeometry ForStride(int stride) => stride >= sizeof(long) ? new ChunkGeometry(stride) : default;

    /// <summary>
    /// Maps a chunk id to its position within the segment. Mirrors <c>ChunkBasedSegment.GetChunkLocation</c>.
    /// </summary>
    /// <param name="chunkId">The chunk id.</param>
    /// <param name="segmentPageOrdinal">Receives the index into the segment's own page list, not a file page.</param>
    /// <param name="chunkInPage">Receives the chunk's position within that page.</param>
    /// <returns><c>false</c> for a negative id or an unusable geometry, in which case nothing may be dereferenced.</returns>
    public bool TryLocate(int chunkId, out int segmentPageOrdinal, out int chunkInPage)
    {
        segmentPageOrdinal = -1;
        chunkInPage = -1;
        if (!IsUsable || chunkId < 0)
        {
            return false;
        }

        if (chunkId < ChunkCountRootPage)
        {
            segmentPageOrdinal = 0;
            chunkInPage = chunkId;
            return true;
        }

        var adjusted = chunkId - ChunkCountRootPage;
        segmentPageOrdinal = (adjusted / ChunkCountPerPage) + 1;
        chunkInPage = adjusted % ChunkCountPerPage;
        return true;
    }

    /// <summary>Byte offset of a chunk within its page.</summary>
    /// <param name="segmentPageOrdinal">Which page of the segment, as returned by <see cref="TryLocate"/>.</param>
    /// <param name="chunkInPage">The chunk's position within that page.</param>
    public int OffsetInPage(int segmentPageOrdinal, int chunkInPage)
        => (segmentPageOrdinal == 0 ? RootDataOffset : OtherDataOffset) + (chunkInPage * Stride);

    /// <summary>Chunks the segment can hold across <paramref name="pageCount"/> pages.</summary>
    /// <param name="pageCount">Number of pages in the segment, root included.</param>
    public int Capacity(int pageCount)
        => pageCount <= 0 ? 0 : ChunkCountRootPage + ((pageCount - 1) * ChunkCountPerPage);

    /// <summary>
    /// Whether the page's own occupancy bitmap marks <paramref name="chunkInPage"/> allocated.
    /// </summary>
    /// <remarks>
    /// The bitmap is the authority on what is allocated — the same discipline the engine applies, and the reason a chunk
    /// walk never has to consult a free list that may itself be damaged.
    /// </remarks>
    /// <param name="page">The page image.</param>
    /// <param name="isRootPage">Whether this is the segment's root page.</param>
    /// <param name="chunkInPage">The chunk's position within the page.</param>
    public bool IsChunkAllocated(ReadOnlySpan<byte> page, bool isRootPage, int chunkInPage)
    {
        var words = isRootPage ? BitmapLongsRoot : BitmapLongsOther;
        var wordIndex = chunkInPage >> 6;
        if (chunkInPage < 0 || wordIndex >= words)
        {
            return false;
        }

        var bitmap = MemoryMarshal.Cast<byte, long>(page.Slice(MetadataOffset, words * sizeof(long)));
        return (bitmap[wordIndex] & (1L << (chunkInPage & 63))) != 0;
    }
}
