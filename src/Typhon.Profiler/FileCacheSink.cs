using System;
using System.Collections.Generic;
using System.IO;

namespace Typhon.Profiler;

/// <summary>
/// <see cref="ICacheChunkSink"/> implementation backed by a <see cref="TraceFileCacheWriter"/>. Produces a complete
/// <c>.typhon-trace-cache</c> sidecar file when <see cref="WriteTrailer"/> is called.
/// </summary>
/// <remarks>
/// The sink owns the underlying <see cref="TraceFileCacheWriter"/> (and therefore the file stream). Disposing it disposes the writer.
/// </remarks>
public sealed class FileCacheSink : ICacheChunkSink
{
    private readonly TraceFileCacheWriter _writer;
    private bool _foldedSectionOpen;
    private bool _trailerWritten;
    private bool _disposed;

    public FileCacheSink(TraceFileCacheWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    /// <summary>Open a sink at the given path, creating/overwriting the file. The sink owns the stream.</summary>
    public static FileCacheSink Create(string cachePath)
    {
        ArgumentNullException.ThrowIfNull(cachePath);
        var stream = File.Create(cachePath);
        return new FileCacheSink(new TraceFileCacheWriter(stream));
    }

    public bool SupportsTrailer => true;

    public (long CacheOffset, uint CompressedLength, uint UncompressedLength) AppendChunk(ReadOnlySpan<byte> uncompressedRecords)
    {
        if (!_foldedSectionOpen)
        {
            _writer.BeginSection(CacheSectionId.FoldedChunkData);
            _foldedSectionOpen = true;
        }
        return _writer.AppendLz4Chunk(uncompressedRecords);
    }

    public void WriteTrailer(
        IReadOnlyList<TickSummary> tickSummaries,
        in GlobalMetricsFixed globalMetrics,
        IReadOnlyList<SystemAggregateDuration> systemAggregates,
        IReadOnlyList<ChunkManifestEntry> chunkManifest,
        IReadOnlyDictionary<int, string> spanNames,
        ReadOnlySpan<byte> sourceMetadataBytes,
        in CacheHeader headerTemplate,
        IReadOnlyList<SystemTickSummary> systemTickSummaries,
        IReadOnlyList<QueueTickSummary> queueTickSummaries,
        IReadOnlyList<PostTickSummary> postTickSummaries,
        IReadOnlyDictionary<ushort, string> queueIdToName)
    {
        if (_trailerWritten)
        {
            throw new InvalidOperationException("Trailer has already been written.");
        }

        // If the FoldedChunkData section was never opened (zero chunks), open + close it now to maintain layout invariants.
        if (!_foldedSectionOpen)
        {
            _writer.BeginSection(CacheSectionId.FoldedChunkData);
            _foldedSectionOpen = true;
        }

        _writer.BeginSection(CacheSectionId.TickSummaries);
        var summaryArr = ToArray(tickSummaries);
        _writer.WriteArray(summaryArr);

        _writer.BeginSection(CacheSectionId.GlobalMetrics);
        _writer.WriteStruct(globalMetrics);
        var aggArr = ToArray(systemAggregates);
        _writer.WriteArray(aggArr);

        _writer.BeginSection(CacheSectionId.ChunkManifest);
        var manifestArr = ToArray(chunkManifest);
        _writer.WriteArray(manifestArr);

        _writer.BeginSection(CacheSectionId.SpanNameTable);
        _writer.WriteSpanNameTable(spanNames);

        if (!sourceMetadataBytes.IsEmpty)
        {
            _writer.BeginSection(CacheSectionId.SourceMetadata);
            _writer.Write(sourceMetadataBytes);
        }

        // v12 sections (#311) — always emit, even if empty, so v12 readers can rely on their presence.
        _writer.BeginSection(CacheSectionId.SystemTickSummaries);
        _writer.WriteArray(ToArray(systemTickSummaries));

        _writer.BeginSection(CacheSectionId.QueueTickSummaries);
        _writer.WriteArray(ToArray(queueTickSummaries));

        _writer.BeginSection(CacheSectionId.PostTickSummaries);
        _writer.WriteArray(ToArray(postTickSummaries));

        _writer.BeginSection(CacheSectionId.QueueNameTable);
        _writer.WriteQueueNameTable(queueIdToName);

        _writer.Finalize(headerTemplate);
        _trailerWritten = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _writer.Dispose();
    }

    private static T[] ToArray<T>(IReadOnlyList<T> list)
    {
        if (list is T[] arr)
        {
            return arr;
        }
        var result = new T[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            result[i] = list[i];
        }
        return result;
    }
}
