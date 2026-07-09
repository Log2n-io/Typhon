using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.IO;
using K4os.Compression.LZ4;

namespace Typhon.Profiler;

/// <summary>
/// <see cref="ICacheChunkSink"/> implementation for live sessions: writes LZ4-compressed chunk bytes back-to-back to a
/// caller-provided <see cref="Stream"/> (typically a temp file). No headers, no section table, no trailer — chunks only.
/// The owning <c>AttachSessionRuntime</c> keeps the manifest in memory and serves chunks via offset+length lookups.
/// </summary>
/// <remarks>
/// <para>
/// This sink does NOT write a trailer (<see cref="SupportsTrailer"/> returns <c>false</c>). The builder calls
/// <see cref="ICacheChunkSink.WriteTrailer"/> only when this is true; live mode skips it. If a future feature wants to
/// snapshot a live session as a sealed cache file, it can build a <see cref="FileCacheSink"/> alongside this one and
/// stream-replay the chunks into it.
/// </para>
/// <para>
/// The sink is NOT thread-safe — the builder is the sole writer, on the AttachSessionRuntime's read loop thread.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class AppendOnlyChunkSink : ICacheChunkSink
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private byte[] _lz4Buffer;
    private bool _disposed;

    /// <summary>Wrap a writable stream as a chunk sink.</summary>
    /// <param name="stream">Destination for compressed chunk bytes. Must be writable.</param>
    /// <param name="ownsStream">When <c>true</c>, <see cref="Dispose"/> also disposes <paramref name="stream"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not writable.</exception>
    public AppendOnlyChunkSink(Stream stream, bool ownsStream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("Stream must be writable.", nameof(stream));
        }
        _stream = stream;
        _ownsStream = ownsStream;
        _lz4Buffer = new byte[LZ4Codec.MaximumOutputSize(TraceFileCacheConstants.ByteCap)];
    }

    /// <inheritdoc/>
    /// <remarks>Always <c>false</c> — live mode keeps trailer metadata in memory and never seals a trailer.</remarks>
    public bool SupportsTrailer => false;

    /// <summary>The current write position in the underlying stream — equals total compressed bytes appended so far.</summary>
    public long Position => _stream.Position;

    /// <inheritdoc/>
    /// <remarks>Flushes the stream after each append so a separate reader (the live chunk endpoint) can see the bytes immediately.</remarks>
    /// <exception cref="ArgumentException"><paramref name="uncompressedRecords"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">LZ4 compression produced a non-positive length.</exception>
    public (long CacheOffset, uint CompressedLength, uint UncompressedLength) AppendChunk(ReadOnlySpan<byte> uncompressedRecords)
    {
        if (uncompressedRecords.IsEmpty)
        {
            throw new ArgumentException("Cannot write an empty chunk.", nameof(uncompressedRecords));
        }

        var maxCompressed = LZ4Codec.MaximumOutputSize(uncompressedRecords.Length);
        if (_lz4Buffer.Length < maxCompressed)
        {
            _lz4Buffer = new byte[maxCompressed];
        }

        var compressedLength = LZ4Codec.Encode(uncompressedRecords, _lz4Buffer);
        if (compressedLength <= 0)
        {
            throw new InvalidOperationException(
                $"LZ4 encode failed for chunk of {uncompressedRecords.Length} B (compressed result {compressedLength}).");
        }

        var offset = _stream.Position;
        _stream.Write(_lz4Buffer.AsSpan(0, compressedLength));
        // Flush after every chunk so the live chunk-endpoint can read it via a separate FileStream. Without this,
        // user-space buffering on the writer hides the bytes from the reader and chunk fetches fail with EOF. Cost
        // is one syscall per ~1 chunk/sec — negligible.
        _stream.Flush();
        return (offset, (uint)compressedLength, (uint)uncompressedRecords.Length);
    }

    /// <inheritdoc/>
    /// <remarks>Not supported by the live sink — always throws <see cref="NotSupportedException"/>. Gate on <see cref="SupportsTrailer"/>.</remarks>
    /// <exception cref="NotSupportedException">Always — live mode keeps trailer metadata in memory.</exception>
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
        IReadOnlyDictionary<ushort, string> queueIdToName,
        IReadOnlyList<SystemArchetypeTouchSummary> systemArchetypeTouches) =>
        throw new NotSupportedException("AppendOnlyChunkSink does not support a trailer (live mode keeps metadata in memory).");

    /// <summary>Flushes the stream a final time and, if this sink owns it, disposes it. Idempotent; never throws.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _stream.Flush();
        }
        catch
        {
            // Disposal must not throw — stream may already be in a bad state.
        }
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
