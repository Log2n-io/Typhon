using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Typhon.Engine.Internals;

/// <summary>
/// What a chunk-based segment's allocator needs at load and the file does not otherwise record: how many chunks are allocated and which pages have
/// room. Captured at a clean close, so the next open can rebuild the allocator without reading a single data page (#1143).
/// </summary>
/// <remarks>
/// The page bitmaps stay the truth. This is a snapshot of state derived from them, valid only for the file exactly as that close left it, which is why
/// it is trusted only after a clean close and only with the nonce that close wrote (<see cref="ChunkSummaryFile"/>).
/// </remarks>
internal sealed class ChunkSegmentSummary
{
    internal ChunkSegmentSummary(int rootPageIndex, int pageCount, int allocatedCount, ulong[] pagesWithRoom)
    {
        RootPageIndex = rootPageIndex;
        PageCount = pageCount;
        AllocatedCount = allocatedCount;
        PagesWithRoom = pagesWithRoom;
    }

    /// <summary>The segment's root page: the key the open looks it up by.</summary>
    public int RootPageIndex { get; }

    /// <summary>The segment's length in pages at the close.</summary>
    public int PageCount { get; }

    /// <summary>Allocated chunks at the close.</summary>
    public int AllocatedCount { get; }

    /// <summary>One bit per segment page, set when the page was in the allocator's free list: it has at least one free chunk.</summary>
    public ulong[] PagesWithRoom { get; }

    /// <summary>Number of 64-bit words <see cref="PagesWithRoom"/> needs for <paramref name="pageCount"/> pages.</summary>
    internal static int WordCount(int pageCount) => (pageCount + 63) >> 6;

    /// <summary>Whether page <paramref name="pageIndex"/> has room.</summary>
    internal bool HasRoom(int pageIndex) => (PagesWithRoom[pageIndex >> 6] & (1UL << (pageIndex & 63))) != 0;
}

/// <summary>
/// What an archetype's cluster state needs at load and the file does not otherwise record: which clusters hold entities, and which one new entities fill
/// next. Captured at a clean close, so the next open does not read every cluster's occupancy word to find them (#1143).
/// </summary>
/// <remarks>
/// The occupancy words stay the truth: a cluster is active when its word is not zero. The close records the running engine's own list, which agrees with
/// the words once nothing is spawning, destroying or draining — the only time the close takes it.
/// </remarks>
internal sealed class ClusterListSummary
{
    internal ClusterListSummary(int rootPageIndex, int freeClusterHead, int[] activeClusterIds)
    {
        RootPageIndex = rootPageIndex;
        FreeClusterHead = freeClusterHead;
        ActiveClusterIds = activeClusterIds;
    }

    /// <summary>The cluster segment's root page: the key the open looks it up by.</summary>
    public int RootPageIndex { get; }

    /// <summary>The cluster new entities were filling at the close, or <c>-1</c>. Always one of <see cref="ActiveClusterIds"/>.</summary>
    public int FreeClusterHead { get; }

    /// <summary>The clusters holding at least one entity, ascending — the order a scan of the occupancy words would find them in.</summary>
    public int[] ActiveClusterIds { get; }
}

/// <summary>Everything a <see cref="ChunkSummaryFile"/> holds, keyed by segment root page.</summary>
internal sealed class ChunkSummaryContents
{
    internal ChunkSummaryContents(Dictionary<int, ChunkSegmentSummary> segments, Dictionary<int, ClusterListSummary> clusters)
    {
        Segments = segments;
        Clusters = clusters;
    }

    /// <summary>Allocator state, one entry per chunk-based segment.</summary>
    public Dictionary<int, ChunkSegmentSummary> Segments { get; }

    /// <summary>Active-cluster lists, one entry per archetype cluster segment.</summary>
    public Dictionary<int, ClusterListSummary> Clusters { get; }
}

/// <summary>How reading a chunk summary file ended.</summary>
internal enum ChunkSummaryReadResult
{
    /// <summary>The open did not consult a summary: the last close was not clean.</summary>
    NotConsulted,

    /// <summary>The file was read and every entry is usable.</summary>
    Loaded,

    /// <summary>No file: a database written before #1143, or a close that could not write one.</summary>
    Missing,

    /// <summary>The file belongs to another close than the one the data file records — a stale or copied-in file.</summary>
    NonceMismatch,

    /// <summary>The file is damaged or not a summary file: wrong magic or version, a checksum mismatch, or entries that do not fit.</summary>
    Invalid,

    /// <summary>The file could not be read.</summary>
    Unreadable,
}

/// <summary>
/// The bundle file holding every chunk-based segment's <see cref="ChunkSegmentSummary"/> and every archetype's <see cref="ClusterListSummary"/>:
/// <c>{bundle}/chunk-summary</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate file.</b> It is written once, at a clean close, after the data file is final, and read once, at the next open, before any chunk
/// segment loads. Keeping it out of the data file keeps it out of every structure that must account for data-file pages — the occupancy map and its
/// crash re-derive, the offline scanner, storage introspection — and needs no second data flush at close. Losing it costs a scan, never data.
/// </para>
/// <para>
/// <b>Validity.</b> The close writes a fresh random nonce into the file and, in the same meta-page flip as the clean-shutdown flag, into the data file
/// (<c>DurabilityWatermarks</c>). An open trusts the file only after a clean close and only when the two nonces agree, so a file from an earlier close,
/// a crash in between, or a data file restored without its sidecar all fall back to the scan. CRC32C covers the header and the payload.
/// </para>
/// <para>
/// <b>Layout</b> (little-endian). Header, 32 bytes: magic <c>TCSM</c>, u16 version, u16 reserved, u64 nonce, i32 entry count, i32 payload length,
/// u32 payload CRC32C, u32 header CRC32C over the first 28 bytes. Payload: the segment entries (the header's count), each i32 root page, i32 page count,
/// i32 allocated count, i32 reserved, then <c>ceil(pageCount / 64)</c> u64 words of the pages-with-room bitmap — about one bit per data page, 800 KiB for
/// a 50 GiB database; then i32 cluster entry count, i32 reserved, and the cluster entries, each i32 root page, i32 free-cluster head, i32 active count,
/// i32 reserved, then the active cluster ids as i32, ascending — four bytes per cluster holding entities.
/// </para>
/// </remarks>
internal static class ChunkSummaryFile
{
    /// <summary>The file's name inside the bundle.</summary>
    internal const string FileName = "chunk-summary";

    private const uint Magic = 0x4D534354;   // "TCSM"
    private const ushort Version = 2;
    private const int HeaderSize = 32;
    private const int EntryHeaderSize = 16;
    private const int SectionHeaderSize = 8;

    /// <summary>Writes <paramref name="segments"/> and <paramref name="clusters"/> under <paramref name="nonce"/>, replacing any previous file.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="nonce">The close's nonce; never 0, which is what a close that wrote no file records.</param>
    /// <param name="segments">One summary per chunk-based segment.</param>
    /// <param name="clusters">One active-cluster list per archetype cluster segment.</param>
    /// <param name="flushToDisk">Whether to fsync. False only in test mode, like the data file's own flush.</param>
    internal static void Write(string path, ulong nonce, IReadOnlyList<ChunkSegmentSummary> segments, IReadOnlyList<ClusterListSummary> clusters,
        bool flushToDisk)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(clusters);
        if (nonce == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nonce), "0 is the nonce of a close that wrote no summary.");
        }

        long payloadLength = SectionHeaderSize;
        for (var i = 0; i < segments.Count; i++)
        {
            payloadLength += EntryHeaderSize + ((long)ChunkSegmentSummary.WordCount(segments[i].PageCount) * sizeof(ulong));
        }

        for (var i = 0; i < clusters.Count; i++)
        {
            payloadLength += EntryHeaderSize + ((long)clusters[i].ActiveClusterIds.Length * sizeof(int));
        }

        if (HeaderSize + payloadLength > int.MaxValue)
        {
            throw new InvalidOperationException($"The chunk summary would be {payloadLength:N0} bytes, past the format's 2 GiB limit.");
        }

        var buffer = new byte[HeaderSize + payloadLength];
        var payload = buffer.AsSpan(HeaderSize);
        var at = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            BinaryPrimitives.WriteInt32LittleEndian(payload[at..], s.RootPageIndex);
            BinaryPrimitives.WriteInt32LittleEndian(payload[(at + 4)..], s.PageCount);
            BinaryPrimitives.WriteInt32LittleEndian(payload[(at + 8)..], s.AllocatedCount);
            at += EntryHeaderSize;
            var words = ChunkSegmentSummary.WordCount(s.PageCount);
            for (var w = 0; w < words; w++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(payload[at..], s.PagesWithRoom[w]);
                at += sizeof(ulong);
            }
        }

        BinaryPrimitives.WriteInt32LittleEndian(payload[at..], clusters.Count);
        at += SectionHeaderSize;
        for (var i = 0; i < clusters.Count; i++)
        {
            var c = clusters[i];
            var ids = c.ActiveClusterIds;
            BinaryPrimitives.WriteInt32LittleEndian(payload[at..], c.RootPageIndex);
            BinaryPrimitives.WriteInt32LittleEndian(payload[(at + 4)..], c.FreeClusterHead);
            BinaryPrimitives.WriteInt32LittleEndian(payload[(at + 8)..], ids.Length);
            at += EntryHeaderSize;
            for (var k = 0; k < ids.Length; k++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(payload[at..], ids[k]);
                at += sizeof(int);
            }
        }

        var header = buffer.AsSpan(0, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], nonce);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], segments.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], (int)payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], Crc32CUtil.Compute(payload));
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], Crc32CUtil.Compute(header[..28]));

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(buffer);
        stream.Flush(flushToDisk);
    }

    /// <summary>
    /// Reads the file at <paramref name="path"/> and returns its entries by root page when it was written under <paramref name="expectedNonce"/> and is
    /// intact; otherwise <c>null</c>, with the reason in <paramref name="result"/>.
    /// </summary>
    internal static ChunkSummaryContents TryRead(string path, ulong expectedNonce, out ChunkSummaryReadResult result)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                result = ChunkSummaryReadResult.Missing;
                return null;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            result = ChunkSummaryReadResult.Unreadable;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            result = ChunkSummaryReadResult.Unreadable;
            return null;
        }

        return Parse(bytes, expectedNonce, out result);
    }

    /// <summary>The validation and parsing half of <see cref="TryRead"/>, over bytes already in memory.</summary>
    internal static ChunkSummaryContents Parse(ReadOnlySpan<byte> bytes, ulong expectedNonce, out ChunkSummaryReadResult result)
    {
        result = ChunkSummaryReadResult.Invalid;
        if (bytes.Length < HeaderSize)
        {
            return null;
        }

        var header = bytes[..HeaderSize];
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != Version
            || BinaryPrimitives.ReadUInt32LittleEndian(header[28..]) != Crc32CUtil.Compute(header[..28]))
        {
            return null;
        }

        // The nonce is checked before the payload: a stale file is the common case, and it is not damage.
        if (expectedNonce == 0 || BinaryPrimitives.ReadUInt64LittleEndian(header[8..]) != expectedNonce)
        {
            result = ChunkSummaryReadResult.NonceMismatch;
            return null;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
        if (count < 0 || payloadLength != bytes.Length - HeaderSize)
        {
            return null;
        }

        var payload = bytes[HeaderSize..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) != Crc32CUtil.Compute(payload))
        {
            return null;
        }

        var entries = new Dictionary<int, ChunkSegmentSummary>(count);
        var at = 0;
        for (var i = 0; i < count; i++)
        {
            if (payload.Length - at < EntryHeaderSize)
            {
                return null;
            }

            var root = BinaryPrimitives.ReadInt32LittleEndian(payload[at..]);
            var pageCount = BinaryPrimitives.ReadInt32LittleEndian(payload[(at + 4)..]);
            var allocated = BinaryPrimitives.ReadInt32LittleEndian(payload[(at + 8)..]);
            at += EntryHeaderSize;

            var words = pageCount > 0 ? ChunkSegmentSummary.WordCount(pageCount) : -1;
            if (root <= 0 || words < 0 || allocated < 0 || (long)words * sizeof(ulong) > payload.Length - at)
            {
                return null;
            }

            var bits = new ulong[words];
            for (var w = 0; w < words; w++)
            {
                bits[w] = BinaryPrimitives.ReadUInt64LittleEndian(payload[at..]);
                at += sizeof(ulong);
            }

            if (!entries.TryAdd(root, new ChunkSegmentSummary(root, pageCount, allocated, bits)))
            {
                return null;
            }
        }

        var clusters = ParseClusterLists(payload, ref at);
        if (clusters == null || at != payload.Length)
        {
            return null;
        }

        result = ChunkSummaryReadResult.Loaded;
        return new ChunkSummaryContents(entries, clusters);
    }

    /// <summary>
    /// Parses the cluster section starting at <paramref name="at"/>, or returns <c>null</c> when an entry is not one a close can write: ids not strictly
    /// ascending or below 1 (chunk 0 is never a cluster), a free-cluster head that is not one of the entry's ids, or a root seen twice.
    /// </summary>
    private static Dictionary<int, ClusterListSummary> ParseClusterLists(ReadOnlySpan<byte> payload, ref int at)
    {
        if (payload.Length - at < SectionHeaderSize)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload[at..]);
        at += SectionHeaderSize;
        if (count < 0)
        {
            return null;
        }

        var clusters = new Dictionary<int, ClusterListSummary>(count);
        for (var i = 0; i < count; i++)
        {
            if (payload.Length - at < EntryHeaderSize)
            {
                return null;
            }

            var root = BinaryPrimitives.ReadInt32LittleEndian(payload[at..]);
            var freeHead = BinaryPrimitives.ReadInt32LittleEndian(payload[(at + 4)..]);
            var activeCount = BinaryPrimitives.ReadInt32LittleEndian(payload[(at + 8)..]);
            at += EntryHeaderSize;
            if (root <= 0 || activeCount < 0 || (long)activeCount * sizeof(int) > payload.Length - at)
            {
                return null;
            }

            var ids = new int[activeCount];
            var previous = 0;
            for (var k = 0; k < activeCount; k++)
            {
                var id = BinaryPrimitives.ReadInt32LittleEndian(payload[at..]);
                at += sizeof(int);
                if (id <= previous)
                {
                    return null;
                }

                ids[k] = id;
                previous = id;
            }

            if ((freeHead != -1 && Array.BinarySearch(ids, freeHead) < 0) || !clusters.TryAdd(root, new ClusterListSummary(root, freeHead, ids)))
            {
                return null;
            }
        }

        return clusters;
    }
}
