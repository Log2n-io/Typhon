using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using K4os.Compression.LZ4;

namespace Typhon.Profiler;

/// <summary>
/// Opens and reads a `.typhon-trace-cache` sidecar file. Small sections (<see cref="TickIndex"/>, <see cref="TickSummaries"/>,
/// <see cref="ChunkManifest"/>, <see cref="GlobalMetrics"/>, <see cref="SpanNames"/>) are loaded eagerly on construction — they're capped
/// at tens of MB even for 500K-tick traces, and the server/client need random access to them anyway. The bulk FoldedChunkData section stays
/// on disk; callers read chunks on demand via <see cref="ReadChunkRaw"/> or <see cref="DecompressChunk"/>.
/// </summary>
/// <remarks>
/// Construction validates the magic, version, and section-table consistency. Fingerprint verification against the source is a separate concern
/// (see <see cref="VerifyFingerprint"/> and <see cref="ComputeSourceFingerprint"/>) — the reader does NOT open the source file itself.
/// </remarks>
public sealed class TraceFileCacheReader : IDisposable
{
    private readonly Stream _stream;
    /// <summary>
    /// <see cref="Microsoft.Win32.SafeHandles.SafeFileHandle"/> extracted once at construction when the backing stream is a <see cref="FileStream"/>.
    /// Lets <see cref="ReadChunkRaw"/> and <see cref="DecompressChunk"/> use <see cref="RandomAccess.Read"/> for thread-safe, stateless, offset-based reads —
    /// the shared-stream seek+read pattern they previously used was a classic race condition that corrupted compressed bytes when multiple chunk requests were
    /// in flight simultaneously (e.g., distant range-select triggering N parallel cache misses). Null when the stream isn't a <see cref="FileStream"/> — the
    /// lock-based fallback kicks in.
    /// </summary>
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _fileHandle;
    /// <summary>
    /// Serializes chunk reads when <see cref="_fileHandle"/> is null (non-<see cref="FileStream"/> backings — unit tests that pass a
    /// <see cref="MemoryStream"/>, for example). Unused in production since the FileStream path uses <see cref="RandomAccess"/>.
    /// </summary>
    private readonly object _readFallbackLock = new();
    private readonly Dictionary<CacheSectionId, SectionTableEntry> _sectionsByid = new();
    private readonly List<TickIndexEntry> _tickIndex = [];
    private readonly List<TickSummary> _tickSummaries = [];
    private readonly List<ChunkManifestEntry> _chunkManifest = [];
    // NOTE: the former `_chunkIndexByFromTick` dictionary was removed in chunker v8 when intra-tick splitting became supported — FromTick is
    // no longer unique across entries (a split tick produces multiple chunks with the same [FromTick, ToTick)). Chunk endpoints now take a
    // `chunkIdx` parameter and index directly into `_chunkManifest` instead, which is both simpler and strictly more expressive.
    private readonly List<SystemAggregateDuration> _systemAggregates = [];
    private readonly Dictionary<int, string> _spanNames = new();
    // ── v12 (#311) ──────────────────────────────────────────────────────
    private readonly List<SystemTickSummary> _systemTickSummaries = [];
    private readonly List<QueueTickSummary> _queueTickSummaries = [];
    private readonly List<PostTickSummary> _postTickSummaries = [];
    private readonly Dictionary<ushort, string> _queueIdToName = new();
    // ── v15 (#327) ──────────────────────────────────────────────────────
    private readonly List<SystemArchetypeTouchSummary> _systemArchetypeTouches = [];
    private byte[] _sourceMetadataBytes;
    private GlobalMetricsFixed _globalMetrics;
    private CacheHeader _header;
    private bool _disposed;

    /// <summary>
    /// Opens a cache reader over <paramref name="stream"/>, validating the magic, cache version, and chunker version, then eagerly loading the
    /// small sections. The stream must be seekable; the reader takes ownership and disposes it in <see cref="Dispose"/>.
    /// </summary>
    /// <param name="stream">Seekable stream positioned at the start of a <c>.typhon-trace-cache</c> file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not seekable.</exception>
    /// <exception cref="System.IO.InvalidDataException">The magic, cache version, or chunker version does not match this reader.</exception>
    public TraceFileCacheReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!_stream.CanSeek)
        {
            throw new ArgumentException("TraceFileCacheReader requires a seekable stream.", nameof(stream));
        }
        // Snapshot the SafeFileHandle once so the hot path in ReadChunkRaw/DecompressChunk doesn't pay a virtual-dispatch + type-check on
        // every chunk request. Non-FileStream callers (unit tests) see a null handle and fall through to the lock-based path.
        _fileHandle = (_stream as FileStream)?.SafeFileHandle;

        ReadHeader();
        ReadSectionTable();
        LoadSmallSections();
    }

    /// <summary>The cache file's header. Contains version, fingerprint, and section-table location.</summary>
    public ref readonly CacheHeader Header => ref _header;

    /// <summary>
    /// Returns <see cref="CacheHeader.SourceFingerprint"/> as a 64-char uppercase hex string. Useful for IDs that need to cross a process boundary (e.g.,
    /// /api/trace/open responses: the client uses this string as an invalidation key for its OPFS chunk cache — source file changes produce a different
    /// fingerprint, old cached chunks become unreachable).
    /// </summary>
    public string GetSourceFingerprintHex()
    {
        Span<byte> fp = stackalloc byte[32];
        CopySourceFingerprint(fp);
        return Convert.ToHexString(fp);
    }

    /// <summary>
    /// Copies the 32-byte source fingerprint into <paramref name="destination"/>. For source-derived caches the bytes are a SHA-256
    /// hash of the source file; for self-contained caches (<see cref="IsSelfContained"/>) they are an arbitrary session-derived
    /// identifier and must not be treated as a hash.
    /// </summary>
    public unsafe void CopySourceFingerprint(Span<byte> destination)
    {
        if (destination.Length < 32)
        {
            throw new ArgumentException("Destination must be at least 32 bytes.", nameof(destination));
        }
        fixed (byte* src = _header.SourceFingerprint)
        {
            new ReadOnlySpan<byte>(src, 32).CopyTo(destination);
        }
    }

    /// <summary>Per-tick source-file index, loaded eagerly at construction.</summary>
    public IReadOnlyList<TickIndexEntry> TickIndex => _tickIndex;
    /// <summary>Per-tick overview rollups, loaded eagerly at construction. Drives the viewer's timeline.</summary>
    public IReadOnlyList<TickSummary> TickSummaries => _tickSummaries;
    /// <summary>Chunk manifest addressing folded chunk payloads in the cache file, loaded eagerly at construction.</summary>
    public IReadOnlyList<ChunkManifestEntry> ChunkManifest => _chunkManifest;
    /// <summary>Trace-wide aggregate metrics (the fixed header of the GlobalMetrics section), loaded eagerly at construction.</summary>
    public ref readonly GlobalMetricsFixed GlobalMetrics => ref _globalMetrics;
    /// <summary>Per-system trace-wide duration aggregates, loaded eagerly at construction. Empty when the section is absent.</summary>
    public IReadOnlyList<SystemAggregateDuration> SystemAggregates => _systemAggregates;
    /// <summary>Interned span-name id → name map. Empty when the source carried no span-name table.</summary>
    public IReadOnlyDictionary<int, string> SpanNames => _spanNames;

    /// <summary>v12 per-(tick, system) rollup rows. Empty for v11-or-older caches.</summary>
    public IReadOnlyList<SystemTickSummary> SystemTickSummaries => _systemTickSummaries;

    /// <summary>v12 per-(tick, queue) rollup rows. Empty for v11-or-older caches.</summary>
    public IReadOnlyList<QueueTickSummary> QueueTickSummaries => _queueTickSummaries;

    /// <summary>v12 per-tick post-tick markers. Empty for v11-or-older caches.</summary>
    public IReadOnlyList<PostTickSummary> PostTickSummaries => _postTickSummaries;

    /// <summary>v12 queue-id → display-name map. Empty for v11-or-older caches.</summary>
    public IReadOnlyDictionary<ushort, string> QueueIdToName => _queueIdToName;

    /// <summary>v15 per-(tick, system, archetype) entity-touch rows. Empty for v14-or-older caches.</summary>
    public IReadOnlyList<SystemArchetypeTouchSummary> SystemArchetypeTouches => _systemArchetypeTouches;

    /// <summary>
    /// True when <see cref="CacheHeaderFlags.IsSelfContained"/> is set in the header. A self-contained cache carries the source metadata tables
    /// (header / systems / archetypes / component types) inside its <see cref="CacheSectionId.SourceMetadata"/> section, so the loader does not
    /// need to read a sibling <c>.typhon-trace</c> file.
    /// </summary>
    public bool IsSelfContained => (_header.Flags & CacheHeaderFlags.IsSelfContained) != 0;

    /// <summary>
    /// Verbatim bytes of the embedded source metadata: <see cref="TraceFileHeader"/> + system definitions table + archetypes table + component
    /// types table, in the same wire format the engine produces. Empty span when the cache has no <see cref="CacheSectionId.SourceMetadata"/>
    /// section (i.e., not self-contained). Callers project header / tables by feeding these bytes through a <c>TraceFileReader</c> over a
    /// <see cref="System.IO.MemoryStream"/>.
    /// </summary>
    public ReadOnlySpan<byte> SourceMetadataBytes => _sourceMetadataBytes ?? [];

    /// <summary>
    /// Read a chunk's compressed bytes into <paramref name="compressedDestination"/>. The destination must be at least
    /// <paramref name="entry"/>.CacheByteLength bytes long. No decompression happens here.
    /// </summary>
    public void ReadChunkRaw(in ChunkManifestEntry entry, Span<byte> compressedDestination)
    {
        ThrowIfDisposed();
        if (compressedDestination.Length < entry.CacheByteLength)
        {
            throw new ArgumentException($"Destination too small: need {entry.CacheByteLength}, got {compressedDestination.Length}.", nameof(compressedDestination));
        }
        // Thread-safety: the minimal API endpoints serve concurrent /chunk-binary requests out of a SHARED reader instance (one per trace
        // file). The earlier seek+read pattern would interleave — thread A seeks to offset_A, thread B seeks to offset_B before A's
        // ReadExactly, then A reads from offset_B (B's bytes into A's buffer). Manifested on the client as seemingly random LZ4 decode
        // errors ("offset=0 is reserved", "match points before output start") when the user triggered many simultaneous chunk loads —
        // e.g., a distant range-select that cache-missed N chunks at once. RandomAccess.Read is kernel-level stateless (pread syscall)
        // and natively concurrent-safe; no user-space lock needed.
        var dst = compressedDestination[..(int)entry.CacheByteLength];
        if (_fileHandle is not null)
        {
            ReadAtExact(_fileHandle, dst, entry.CacheByteOffset);
        }
        else
        {
            lock (_readFallbackLock)
            {
                _stream.Position = entry.CacheByteOffset;
                _stream.ReadExactly(dst);
            }
        }
    }

    /// <summary>
    /// Loop around <see cref="RandomAccess.Read"/> to guarantee the whole buffer is filled. A single call can return fewer bytes than
    /// requested on some stream-like backings; mirroring <see cref="Stream.ReadExactly"/> here means callers always get a complete
    /// compressed payload or an exception (never a silent short read that later fails LZ4 decode with a misleading error).
    /// </summary>
    private static void ReadAtExact(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Span<byte> destination, long offset)
    {
        while (destination.Length > 0)
        {
            var n = RandomAccess.Read(handle, destination, offset);
            if (n == 0) throw new EndOfStreamException("Unexpected end of cache file during chunk read.");
            destination = destination[n..];
            offset += n;
        }
    }

    /// <summary>
    /// Read and LZ4-decompress a chunk into <paramref name="uncompressedDestination"/>. <paramref name="compressedScratch"/> is a caller-supplied
    /// scratch buffer for the compressed read (callers pool these across many chunks). Both buffers must be sized ≥ the entry's respective
    /// lengths.
    /// </summary>
    public void DecompressChunk(in ChunkManifestEntry entry, Span<byte> uncompressedDestination, Span<byte> compressedScratch)
    {
        ThrowIfDisposed();
        if (uncompressedDestination.Length < entry.UncompressedBytes)
        {
            throw new ArgumentException($"Uncompressed destination too small: need {entry.UncompressedBytes}, got {uncompressedDestination.Length}.", nameof(uncompressedDestination));
        }
        if (compressedScratch.Length < entry.CacheByteLength)
        {
            throw new ArgumentException($"Compressed scratch too small: need {entry.CacheByteLength}, got {compressedScratch.Length}.", nameof(compressedScratch));
        }

        // Same thread-safety concern as ReadChunkRaw — see the detailed comment there. RandomAccess.Read is stateless, pread-based, and concurrency-safe; fall
        // back to the lock path only when the backing stream isn't a FileStream.
        var compressed = compressedScratch[..(int)entry.CacheByteLength];
        if (_fileHandle is not null)
        {
            ReadAtExact(_fileHandle, compressed, entry.CacheByteOffset);
        }
        else
        {
            lock (_readFallbackLock)
            {
                _stream.Position = entry.CacheByteOffset;
                _stream.ReadExactly(compressed);
            }
        }

        var decoded = LZ4Codec.Decode(compressed, uncompressedDestination[..(int)entry.UncompressedBytes]);
        if (decoded != (int)entry.UncompressedBytes)
        {
            throw new InvalidDataException($"LZ4 decode size mismatch for chunk [{entry.FromTick}, {entry.ToTick}): expected {entry.UncompressedBytes}, got {decoded}.");
        }
    }

    /// <summary>
    /// Compute the 32-byte source-file fingerprint: SHA-256 of source mtime-ticks + length + first 4 KB + last 4 KB. Cheap (~1 ms) and
    /// collision-resistant enough to detect any meaningful mutation.
    /// </summary>
    public static void ComputeSourceFingerprint(string sourcePath, Span<byte> destination32)
    {
        if (destination32.Length < 32)
        {
            throw new ArgumentException("Destination must be at least 32 bytes.", nameof(destination32));
        }
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Source file not found.", sourcePath);
        }

        using var sha = SHA256.Create();
        Span<byte> meta = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(meta[..8], info.LastWriteTimeUtc.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(meta.Slice(8, 8), info.Length);
        sha.TransformBlock(meta.ToArray(), 0, meta.Length, null, 0);

        using var fs = File.OpenRead(sourcePath);
        var edgeBuf = new byte[TraceFileCacheConstants.FingerprintEdgeBytes];
        var prefixLen = (int)Math.Min(edgeBuf.Length, fs.Length);
        fs.ReadExactly(edgeBuf.AsSpan(0, prefixLen));
        sha.TransformBlock(edgeBuf, 0, prefixLen, null, 0);

        if (fs.Length > edgeBuf.Length * 2)
        {
            fs.Position = fs.Length - edgeBuf.Length;
            fs.ReadExactly(edgeBuf.AsSpan(0, edgeBuf.Length));
            sha.TransformBlock(edgeBuf, 0, edgeBuf.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        sha.Hash!.CopyTo(destination32);
    }

    /// <summary>
    /// Verify the cache's header fingerprint against a freshly-computed fingerprint for the source file. Returns true if they match (cache is
    /// still valid for this source).
    /// </summary>
    public unsafe bool VerifyFingerprint(ReadOnlySpan<byte> expectedFingerprint32)
    {
        if (expectedFingerprint32.Length < 32)
        {
            throw new ArgumentException("Fingerprint must be 32 bytes.", nameof(expectedFingerprint32));
        }
        fixed (byte* fpPtr = _header.SourceFingerprint)
        {
            var headerFingerprint = new ReadOnlySpan<byte>(fpPtr, 32);
            return headerFingerprint.SequenceEqual(expectedFingerprint32[..32]);
        }
    }

    /// <summary>Disposes the reader and closes the underlying stream. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stream.Dispose();
    }

    // ────────────────────────────────────────────────────────────────────────────

    private void ReadHeader()
    {
        _stream.Position = 0;
        Span<byte> headerBytes = stackalloc byte[Unsafe.SizeOf<CacheHeader>()];
        _stream.ReadExactly(headerBytes);
        _header = MemoryMarshal.Read<CacheHeader>(headerBytes);

        if (_header.Magic != CacheHeader.MagicValue)
        {
            throw new InvalidDataException($"Invalid cache file magic: 0x{_header.Magic:X8} (expected 0x{CacheHeader.MagicValue:X8}).");
        }
        if (_header.Version != CacheHeader.CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported cache version: {_header.Version} (reader supports {CacheHeader.CurrentVersion}).");
        }
        if (_header.ChunkerVersion != TraceFileCacheConstants.CurrentChunkerVersion)
        {
            throw new InvalidDataException(
                $"Cache chunker version {_header.ChunkerVersion} does not match reader's {TraceFileCacheConstants.CurrentChunkerVersion}. " +
                "Cache must be rebuilt.");
        }
    }

    private void ReadSectionTable()
    {
        _stream.Position = _header.SectionTableOffset;
        var entrySize = Unsafe.SizeOf<SectionTableEntry>();
        var totalLength = (int)_header.SectionTableLength;
        if (totalLength % entrySize != 0)
        {
            throw new InvalidDataException($"Section table length {totalLength} is not a multiple of entry size {entrySize}.");
        }
        var entryCount = totalLength / entrySize;

        var buffer = new byte[totalLength];
        _stream.ReadExactly(buffer);
        var entries = MemoryMarshal.Cast<byte, SectionTableEntry>(buffer);
        for (var i = 0; i < entryCount; i++)
        {
            var entry = entries[i];
            if (entry.SectionId == (ushort)CacheSectionId.Invalid)
            {
                continue;
            }
            _sectionsByid[(CacheSectionId)entry.SectionId] = entry;
        }
    }

    private void LoadSmallSections()
    {
        if (_sectionsByid.TryGetValue(CacheSectionId.TickIndex, out var tickIndexSec))
        {
            LoadStructArray(tickIndexSec, _tickIndex);
        }
        if (_sectionsByid.TryGetValue(CacheSectionId.TickSummaries, out var tickSummariesSec))
        {
            LoadStructArray(tickSummariesSec, _tickSummaries);
        }
        if (_sectionsByid.TryGetValue(CacheSectionId.ChunkManifest, out var manifestSec))
        {
            LoadStructArray(manifestSec, _chunkManifest);
        }
        if (_sectionsByid.TryGetValue(CacheSectionId.GlobalMetrics, out var metricsSec))
        {
            LoadGlobalMetrics(metricsSec);
        }
        if (_sectionsByid.TryGetValue(CacheSectionId.SpanNameTable, out var spanSec))
        {
            LoadSpanNameTable(spanSec);
        }
        if (_sectionsByid.TryGetValue(CacheSectionId.SourceMetadata, out var sourceMetaSec))
        {
            LoadSourceMetadata(sourceMetaSec);
        }

        // v12 sections (#311). All optional — older caches simply omit them and the lists stay empty.
        if (_sectionsByid.TryGetValue(CacheSectionId.SystemTickSummaries, out var sysSec))
        {
            LoadStructArray(sysSec, _systemTickSummaries);
        }
        if (_sectionsByid.TryGetValue(CacheSectionId.QueueTickSummaries, out var qSec))
        {
            LoadStructArray(qSec, _queueTickSummaries);
        }
        if (_sectionsByid.TryGetValue(CacheSectionId.PostTickSummaries, out var ptSec))
        {
            LoadStructArray(ptSec, _postTickSummaries);
        }
        if (_sectionsByid.TryGetValue(CacheSectionId.QueueNameTable, out var qnameSec))
        {
            LoadQueueNameTable(qnameSec);
        }
        // v15 section (#327). Optional — older v14 caches lack it; readers tolerate the absence.
        if (_sectionsByid.TryGetValue(CacheSectionId.SystemArchetypeTouches, out var satSec))
        {
            LoadStructArray(satSec, _systemArchetypeTouches);
        }
    }

    private void LoadQueueNameTable(SectionTableEntry section)
    {
        _stream.Position = section.Offset;
        Span<byte> u16Buf = stackalloc byte[2];
        Span<byte> lenBuf = stackalloc byte[1];
        _stream.ReadExactly(u16Buf);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(u16Buf);
        for (var i = 0; i < count; i++)
        {
            _stream.ReadExactly(u16Buf);
            var queueId = BinaryPrimitives.ReadUInt16LittleEndian(u16Buf);
            _stream.ReadExactly(lenBuf);
            var nameLen = lenBuf[0];
            var nameBytes = new byte[nameLen];
            _stream.ReadExactly(nameBytes);
            _queueIdToName[queueId] = Encoding.UTF8.GetString(nameBytes);
        }
    }

    private void LoadSourceMetadata(SectionTableEntry section)
    {
        _stream.Position = section.Offset;
        _sourceMetadataBytes = new byte[section.Length];
        _stream.ReadExactly(_sourceMetadataBytes);
    }

    private void LoadStructArray<T>(SectionTableEntry section, List<T> destination) where T : unmanaged
    {
        _stream.Position = section.Offset;
        var entrySize = Unsafe.SizeOf<T>();
        if (section.Length % entrySize != 0)
        {
            throw new InvalidDataException($"Section {(CacheSectionId)section.SectionId} length {section.Length} not a multiple of entry size {entrySize}.");
        }
        var count = (int)(section.Length / entrySize);
        var buffer = new byte[section.Length];
        _stream.ReadExactly(buffer);
        var typed = MemoryMarshal.Cast<byte, T>(buffer);
        destination.Capacity = count;
        for (var i = 0; i < count; i++)
        {
            destination.Add(typed[i]);
        }
    }

    private void LoadGlobalMetrics(SectionTableEntry section)
    {
        _stream.Position = section.Offset;
        var fixedSize = Unsafe.SizeOf<GlobalMetricsFixed>();
        Span<byte> fixedBuf = stackalloc byte[fixedSize];
        _stream.ReadExactly(fixedBuf);
        _globalMetrics = MemoryMarshal.Read<GlobalMetricsFixed>(fixedBuf);

        var aggCount = (int)_globalMetrics.SystemAggregateCount;
        if (aggCount > 0)
        {
            var aggSize = Unsafe.SizeOf<SystemAggregateDuration>();
            var buffer = new byte[aggCount * aggSize];
            _stream.ReadExactly(buffer);
            var typed = MemoryMarshal.Cast<byte, SystemAggregateDuration>(buffer);
            _systemAggregates.Capacity = aggCount;
            for (var i = 0; i < aggCount; i++)
            {
                _systemAggregates.Add(typed[i]);
            }
        }
    }

    private void LoadSpanNameTable(SectionTableEntry section)
    {
        _stream.Position = section.Offset;
        Span<byte> u16Buf = stackalloc byte[2];
        _stream.ReadExactly(u16Buf);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(u16Buf);
        for (var i = 0; i < count; i++)
        {
            _stream.ReadExactly(u16Buf);
            var id = BinaryPrimitives.ReadUInt16LittleEndian(u16Buf);
            var len = _stream.ReadByte();
            if (len < 0)
            {
                throw new InvalidDataException("Unexpected EOF in span name table.");
            }
            var nameBuf = new byte[len];
            _stream.ReadExactly(nameBuf);
            _spanNames[id] = Encoding.UTF8.GetString(nameBuf);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TraceFileCacheReader));
        }
    }
}
