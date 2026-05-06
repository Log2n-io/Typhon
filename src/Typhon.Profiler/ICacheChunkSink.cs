using System;
using System.Collections.Generic;

namespace Typhon.Profiler;

/// <summary>
/// Abstraction over "where flushed chunks go" for <see cref="IncrementalCacheBuilder"/>. Two implementations:
/// <see cref="FileCacheSink"/> wraps <see cref="TraceFileCacheWriter"/> and produces a complete sidecar cache file
/// (replay path); <c>AppendOnlyChunkSink</c> (live path) writes only chunk bytes to a temp file and skips the trailer.
/// </summary>
/// <remarks>
/// <para>
/// The sink is fully responsible for laying out compressed chunk bytes — the builder hands raw uncompressed records and the
/// resulting (offset, length) come back so the builder can record the chunk in its in-memory manifest. For replay sinks,
/// trailer sections are also written via <see cref="WriteTrailer"/> at finalize time. For live sinks, those sections live in
/// memory only and the trailer call is a no-op (<see cref="SupportsTrailer"/> returns <c>false</c>).
/// </para>
/// </remarks>
public interface ICacheChunkSink : IDisposable
{
    /// <summary>
    /// LZ4-compress and append a chunk's records to the sink's underlying storage. Returns the byte offset and lengths needed
    /// to populate the matching <see cref="ChunkManifestEntry"/>.
    /// </summary>
    (long CacheOffset, uint CompressedLength, uint UncompressedLength) AppendChunk(ReadOnlySpan<byte> uncompressedRecords);

    /// <summary>True if this sink writes a trailer (TickSummaries / GlobalMetrics / ChunkManifest / SpanNameTable + cache header).</summary>
    bool SupportsTrailer { get; }

    /// <summary>
    /// Write trailer sections + finalize the cache header. Replay sinks (<see cref="FileCacheSink"/>) implement this; live sinks throw.
    /// Idempotent guard not required — builder calls this at most once on dispose.
    /// </summary>
    /// <param name="tickSummaries">
    /// Per-tick overview rows (one entry per tick processed). Written to the <see cref="CacheSectionId.TickSummaries"/> section.
    /// Order is the order in which the builder accumulated them — typically tick-number ascending; the cache reader does not
    /// re-sort.
    /// </param>
    /// <param name="globalMetrics">
    /// Session-wide aggregates (start/end µs, max/p95 tick durations, total events, total ticks, per-system invocation counts).
    /// Computed once at finalize and written to the <see cref="CacheSectionId.GlobalMetrics"/> section.
    /// </param>
    /// <param name="systemAggregates">
    /// Per-system invocation count + cumulative duration across the whole session. Companion to
    /// <paramref name="globalMetrics"/>; written into the <see cref="CacheSectionId.GlobalMetrics"/> section as a fixed-size table.
    /// </param>
    /// <param name="chunkManifest">
    /// Byte-offset index into the trailing <see cref="CacheSectionId.FoldedChunkData"/> blob — one entry per chunk previously
    /// produced by <see cref="AppendChunk"/>. Drives random-access tick-range fetches at read time.
    /// </param>
    /// <param name="spanNames">
    /// SpanId → display-name lookup table for trace events that carry a <c>SpanId</c> reference. Written to the
    /// <see cref="CacheSectionId.SpanNameTable"/> section. Empty dictionary is valid.
    /// </param>
    /// <param name="sourceMetadataBytes">
    /// Optional verbatim source metadata (header + system / archetype / component-type tables, in <c>TraceFileWriter</c> wire format). When
    /// non-empty, the sink emits a <see cref="CacheSectionId.SourceMetadata"/> section; the caller must set
    /// <see cref="CacheHeaderFlags.IsSelfContained"/> on <paramref name="headerTemplate"/> so loaders project metadata from these bytes
    /// instead of opening a sibling source file. Pass <see cref="ReadOnlySpan{T}.Empty"/> for source-derived caches.
    /// </param>
    /// <param name="headerTemplate">
    /// Cache-file header to finalize. The sink fills in section-table offsets / lengths and writes the result at the file head;
    /// caller-supplied fields (magic, version, chunker version, identifier, flags) are preserved verbatim.
    /// </param>
    /// <param name="systemTickSummaries">
    /// v12 (#311). Per-tick per-system rollup rows (duration, ready/start/end µs, entities processed, etc.) backing the Workbench
    /// Data API's <c>system/&lt;name&gt;</c> tracks. Written to the <see cref="CacheSectionId.SystemTickSummaries"/> section.
    /// Empty list is valid for v11-derived caches and produces an empty section that readers tolerate.
    /// </param>
    /// <param name="queueTickSummaries">
    /// v12 (#311). Per-tick per-event-queue rollup rows (peak depth, end-of-tick depth, overflow count, produced/consumed)
    /// backing the <c>queue/&lt;name&gt;</c> tracks. Written to the <see cref="CacheSectionId.QueueTickSummaries"/> section.
    /// Empty list is valid for v11-derived caches.
    /// </param>
    /// <param name="postTickSummaries">
    /// v12 (#311). Per-tick post-serial-block rollup rows (WAL flush, write-tick-fence, subscription output, etc.) backing the
    /// <c>posttick/&lt;phase&gt;</c> tracks. Written to the <see cref="CacheSectionId.PostTickSummaries"/> section. Empty list
    /// is valid for v11-derived caches.
    /// </param>
    /// <param name="queueIdToName">
    /// v12 (#311). QueueId → display-name lookup for the entries in <paramref name="queueTickSummaries"/>. Written to the
    /// <see cref="CacheSectionId.QueueNameTable"/> section. Empty dictionary is valid.
    /// </param>
    /// <exception cref="NotSupportedException">
    /// Thrown by live sinks (e.g. <c>AppendOnlyChunkSink</c>) — see <see cref="SupportsTrailer"/>. Callers should gate on that
    /// flag rather than catching the exception.
    /// </exception>
    void WriteTrailer(
        IReadOnlyList<TickSummary> tickSummaries,
        in GlobalMetricsFixed globalMetrics,
        IReadOnlyList<SystemAggregateDuration> systemAggregates,
        IReadOnlyList<ChunkManifestEntry> chunkManifest,
        IReadOnlyDictionary<int, string> spanNames,
        ReadOnlySpan<byte> sourceMetadataBytes,
        in CacheHeader headerTemplate,
        // v12 (#311) — Workbench Data API per-system / per-queue / post-tick rollups. May be empty for v11-derived caches.
        IReadOnlyList<SystemTickSummary> systemTickSummaries,
        IReadOnlyList<QueueTickSummary> queueTickSummaries,
        IReadOnlyList<PostTickSummary> postTickSummaries,
        IReadOnlyDictionary<ushort, string> queueIdToName);
}
