using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-sector page verification: an array of <c>{CRC32C, generation}</c> pairs covering equal slices of a page, so damage
/// can be localised to a sector instead of condemning all 8192 bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem.</b> A page carries one CRC32C over all 8192 bytes, so a single flipped bit condemns every entity on it —
/// up to 475. The detector's resolution is 8192 bytes; the decision anyone actually wants to make is per-entity, roughly 24
/// bytes. Splitting the checksum into 16 independent 512-byte sectors takes provable salvage from <b>0 % to 40–91 %</b> of
/// entities depending on archetype shape, and it does so at <b>zero added bytes</b> (the space is already inside the page
/// and already covered) and <b>negative CPU cost</b> (independent chains break the <c>crc32</c> instruction's 3-cycle
/// latency dependency — see <see cref="Crc32CUtil"/>).
/// </para>
/// <para>
/// <b>Why the generation field is required.</b> A torn write leaves one region from the <i>previous</i> write. Those bytes
/// are not corrupt — they are a valid image of generation <i>G-1</i>, and their CRC validates. Without a currency stamp,
/// whole clusters can be silently stale with every checksum passing. The CRC proves <i>integrity</i>; only the generation
/// proves <i>currency</i>.
/// </para>
/// <para>
/// <b>The validity rule, and why the obvious one is unsafe.</b> Comparing each sector's generation to the page header's is
/// backwards-unsafe: the header lives in sector 0, so if sector 0 did not persist but sector 5 did, the header reads
/// <i>G-1</i> — and the rule then condemns the newest, correct sector while accepting every stale one, because the stale
/// sectors all agree with the stale header. The rule used here takes the <b>maximum</b> over the header and every sector
/// stamp, which cannot invert that way. It is sound because every page write rewrites every sector's stamp (writes are
/// always whole-page), so after a clean write all stamps agree and the maximum never over-condemns.
/// </para>
/// <para>
/// <b>Geometry is declared by the page, not inferred.</b> The footer shares the page metadata region with the
/// chunk-occupancy bitmap, whose size depends on the owning segment's stride — which the page writer does not know. So the
/// segment stamps <see cref="SectorCountOffset"/> once at page initialisation and the writer honours it. Pages whose
/// bitmap leaves no room (a stride-8 segment fills the whole region) declare <c>0</c> and keep the legacy whole-page
/// checksum. A reader therefore never has to consult a directory to know how to verify a page — which is exactly the
/// property a damage-tolerant reader needs.
/// </para>
/// </remarks>
internal static class PageSectorFooter
{
    /// <summary>
    /// Byte offset of the page's own logical index. Detects a <b>misdirected write</b> — a page landing at the wrong file
    /// offset, which is otherwise completely undetectable because its checksum is perfectly valid. <c>0</c> means unstamped.
    /// </summary>
    /// <remarks>
    /// This is the page's <b>logical</b> index, not the physical slot it happens to occupy: A/B protected pages alternate
    /// between two physical slots by design, so stamping the physical slot would make every legitimate flip look like a
    /// misdirect. The reader compares against the index it asked for.
    /// </remarks>
    public const int FilePageIndexOffset = 48;

    /// <summary>Byte offset of the declared sector count. Legal values are <c>0</c>, 2, 4, 8 and 16.</summary>
    public const int SectorCountOffset = 52;

    /// <summary>Byte offset of the declared sector size, as a base-2 logarithm of the byte count.</summary>
    public const int SectorLogSizeOffset = 53;

    /// <summary>The footer's high-water mark: it grows <i>down</i> from the end of the page header zone.</summary>
    public const int FooterEndOffset = PagedMMF.PageHeaderSize;

    /// <summary>Bytes each sector costs in the footer: a 4-byte CRC32C plus a 2-byte generation.</summary>
    public const int BytesPerSector = 6;

    /// <summary>The most sectors a page can carry, which is also the granularity that maximises salvage.</summary>
    public const int MaxSectorCount = 16;

    /// <summary>Start of the page metadata region, which the footer shares with the chunk-occupancy bitmap.</summary>
    public const int MetadataOffset = PagedMMF.PageBaseHeaderSize;

    /// <summary>Byte offset at which the footer for <paramref name="sectorCount"/> sectors begins.</summary>
    /// <param name="sectorCount">Number of sectors.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FooterBase(int sectorCount) => FooterEndOffset - (sectorCount * BytesPerSector);

    /// <summary>
    /// The largest legal sector count whose footer fits above a bitmap of <paramref name="reservedMetadataBytes"/> bytes,
    /// or <c>0</c> when none does.
    /// </summary>
    /// <param name="reservedMetadataBytes">Bytes of the metadata region already claimed, counted from <see cref="MetadataOffset"/>.</param>
    public static int ChooseSectorCount(int reservedMetadataBytes)
    {
        var floor = MetadataOffset + reservedMetadataBytes;
        for (var n = MaxSectorCount; n >= 2; n >>= 1)
        {
            if (FooterBase(n) >= floor)
            {
                return n;
            }
        }

        return 0;
    }

    /// <summary>
    /// Declares a page's footer geometry. Called once when a segment initialises the page — the segment is the only layer
    /// that knows how much of the metadata region its chunk bitmap needs — and the write path reads the declaration back
    /// rather than trying to infer it.
    /// </summary>
    /// <param name="page">The full page image.</param>
    /// <param name="reservedMetadataBytes">Bytes of the metadata region the owning segment claims for its chunk bitmap.</param>
    public static void DeclareGeometry(Span<byte> page, int reservedMetadataBytes)
    {
        var n = ChooseSectorCount(reservedMetadataBytes);
        page[SectorCountOffset] = (byte)n;
        page[SectorLogSizeOffset] = n == 0 ? (byte)0 : (byte)System.Numerics.BitOperations.TrailingZeroCount(PagedMMF.PageSize / n);
    }

    /// <summary>Clears any footer declaration, returning the page to whole-page checksumming.</summary>
    /// <param name="page">The full page image.</param>
    public static void ClearGeometry(Span<byte> page)
    {
        page[SectorCountOffset] = 0;
        page[SectorLogSizeOffset] = 0;
    }

    /// <summary>Stamps the page's own logical index, so a write that lands at the wrong offset is detectable.</summary>
    /// <param name="page">The full page image.</param>
    /// <param name="filePageIndex">The page's logical file-page index.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StampFilePageIndex(Span<byte> page, int filePageIndex) => MemoryMarshal.Write(page[FilePageIndexOffset..], in filePageIndex);

    /// <summary>
    /// Reads the declared sector count, returning <c>0</c> for any value that is not a legal, self-consistent declaration.
    /// A reader must never trust a count it has not validated — the byte may itself be damage.
    /// </summary>
    /// <param name="page">The full page image.</param>
    public static int ReadSectorCount(ReadOnlySpan<byte> page)
    {
        int n = page[SectorCountOffset];
        if (n is not (2 or 4 or 8 or 16))
        {
            return 0;
        }

        int logSize = page[SectorLogSizeOffset];
        if (logSize is < 1 or > 30 || (1 << logSize) != PagedMMF.PageSize / n)
        {
            return 0;
        }

        return n;
    }

    /// <summary>Reads the page's stamped logical index. <c>0</c> means the page predates the stamp.</summary>
    /// <param name="page">The full page image.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadFilePageIndex(ReadOnlySpan<byte> page) => MemoryMarshal.Read<int>(page[FilePageIndexOffset..]);

    /// <summary>Reads sector <paramref name="sector"/>'s stored CRC.</summary>
    /// <param name="page">The full page image.</param>
    /// <param name="sectorCount">Validated sector count.</param>
    /// <param name="sector">Sector index.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadSectorCrc(ReadOnlySpan<byte> page, int sectorCount, int sector)
        => MemoryMarshal.Read<uint>(page[(FooterBase(sectorCount) + (sector * 4))..]);

    /// <summary>Reads sector <paramref name="sector"/>'s stored generation stamp.</summary>
    /// <param name="page">The full page image.</param>
    /// <param name="sectorCount">Validated sector count.</param>
    /// <param name="sector">Sector index.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadSectorGeneration(ReadOnlySpan<byte> page, int sectorCount, int sector)
        => MemoryMarshal.Read<ushort>(page[(FooterBase(sectorCount) + (sectorCount * 4) + (sector * 2))..]);

    /// <summary>
    /// Computes sector <paramref name="sector"/>'s CRC over the page image, excluding the two self-referencing regions that
    /// are written after it: the page checksum field and the footer array itself. Both live in sector 0.
    /// </summary>
    /// <param name="page">The full page image.</param>
    /// <param name="sectorCount">Validated sector count.</param>
    /// <param name="sector">Sector index.</param>
    public static uint ComputeSectorCrc(ReadOnlySpan<byte> page, int sectorCount, int sector)
    {
        var size = PagedMMF.PageSize / sectorCount;
        var slice = page.Slice(sector * size, size);
        if (sector != 0)
        {
            return Crc32CUtil.Compute(slice);
        }

        var footerBase = FooterBase(sectorCount);
        return Crc32CUtil.ComputeSkippingPair(slice, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize,
            footerBase, FooterEndOffset - footerBase);
    }

    /// <summary>
    /// Stamps every sector's CRC and generation, then sets <see cref="PageBaseHeader.PageChecksum"/> to a CRC over the
    /// footer array — so the array that protects the sectors is itself protected, and a single pass over the page produces
    /// both the fine-grained and the whole-page verdicts.
    /// </summary>
    /// <param name="page">The full page image, with its <c>ChangeRevision</c> already advanced for this write.</param>
    /// <param name="sectorCount">Validated sector count; must be non-zero.</param>
    public static void Stamp(Span<byte> page, int sectorCount)
    {
        var generation = (ushort)(MemoryMarshal.Read<int>(page[PageImageChangeRevisionOffset..]) & 0xFFFF);
        var footerBase = FooterBase(sectorCount);

        for (var s = 0; s < sectorCount; s++)
        {
            var crc = ComputeSectorCrc(page, sectorCount, s);
            MemoryMarshal.Write(page[(footerBase + (s * 4))..], in crc);
            MemoryMarshal.Write(page[(footerBase + (sectorCount * 4) + (s * 2))..], in generation);
        }

        var footerCrc = Crc32CUtil.Compute(page[footerBase..FooterEndOffset]);
        MemoryMarshal.Write(page[PageBaseHeader.PageChecksumOffset..], in footerCrc);
    }

    /// <summary>
    /// Verifies a stamped page. Fills <paramref name="sectorOk"/> with each sector's verdict and reports whether the footer
    /// array itself verified.
    /// </summary>
    /// <param name="page">The full page image.</param>
    /// <param name="sectorCount">Validated sector count; must be non-zero.</param>
    /// <param name="sectorOk">Receives one verdict per sector; must be at least <paramref name="sectorCount"/> long.</param>
    /// <param name="failedSectors">Receives the number of sectors that did not verify.</param>
    /// <returns><c>true</c> when the footer array verified — i.e. when the per-sector verdicts can be trusted at all.</returns>
    public static bool Verify(ReadOnlySpan<byte> page, int sectorCount, Span<bool> sectorOk, out int failedSectors)
    {
        failedSectors = 0;
        var footerBase = FooterBase(sectorCount);
        var storedFooterCrc = MemoryMarshal.Read<uint>(page[PageBaseHeader.PageChecksumOffset..]);
        var footerIntact = Crc32CUtil.Compute(page[footerBase..FooterEndOffset]) == storedFooterCrc;

        // The currency floor: the newest generation any part of this page claims. Taking the maximum (rather than trusting
        // the header, which lives in sector 0) is what stops a torn sector 0 from condemning the sectors that DID persist.
        var pageGeneration = (ushort)(MemoryMarshal.Read<int>(page[PageImageChangeRevisionOffset..]) & 0xFFFF);
        if (footerIntact)
        {
            for (var s = 0; s < sectorCount; s++)
            {
                var gen = ReadSectorGeneration(page, sectorCount, s);
                if (GenerationIsNewer(gen, pageGeneration))
                {
                    pageGeneration = gen;
                }
            }
        }

        for (var s = 0; s < sectorCount; s++)
        {
            var ok = footerIntact
                && ComputeSectorCrc(page, sectorCount, s) == ReadSectorCrc(page, sectorCount, s)
                && ReadSectorGeneration(page, sectorCount, s) == pageGeneration;
            sectorOk[s] = ok;
            if (!ok)
            {
                failedSectors++;
            }
        }

        return footerIntact;
    }

    /// <summary>
    /// Wrapping-aware "is <paramref name="candidate"/> newer than <paramref name="current"/>" over a 16-bit counter. Half
    /// the range is treated as the future, which is correct because a page's sector stamps can never be more than a handful
    /// of generations apart.
    /// </summary>
    /// <param name="candidate">The generation being considered.</param>
    /// <param name="current">The generation to compare against.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GenerationIsNewer(ushort candidate, ushort current) => (ushort)(candidate - current) is > 0 and < 0x8000;

    /// <summary>Byte offset of the change revision within the page base header, mirrored here to avoid a layout lookup.</summary>
    private const int PageImageChangeRevisionOffset = 4;
}
