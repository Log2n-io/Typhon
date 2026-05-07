using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using K4os.Compression.LZ4;

namespace Typhon.Profiler;

/// <summary>
/// Writes a `.typhon-trace-cache` sidecar file. Owns the underlying stream; not thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// Write protocol: construct the writer (reserves header + section-table placeholder at stream offsets 0..271), then call
/// <see cref="BeginSection"/> / <c>Write*</c> to emit each section in turn, then call <see cref="Finalize"/> with the final header (fingerprint
/// filled in by the builder). The writer rewinds once at finalize to patch the header + section table; the stream must support seek.
/// </para>
/// <para>
/// Sections can be emitted in any order. The writer records each section's byte range in <see cref="_sections"/> as it goes and stitches the
/// final <see cref="CacheSectionId"/> → offset/length table at finalize time.
/// </para>
/// </remarks>
public sealed class TraceFileCacheWriter : IDisposable
{
    /// <summary>
    /// Number of distinct section slots reserved in the table. Bumping this requires re-reserving more placeholder space —
    /// the section table sits at a fixed offset (128) and the subsequent <see cref="SectionsStartOffset"/> derives from this
    /// constant, so changing it shifts where the very first section is written.
    /// v12 (#311) bumped this from 8 → 16 to accommodate the four new sections (SystemTickSummaries, QueueTickSummaries,
    /// PostTickSummaries, QueueNameTable) plus headroom for future v13/v14 extensions without another invalidation pass.
    /// </summary>
    public const int MaxSections = 16;

    /// <summary>
    /// Fixed offset where section writes begin. Header (128) + section table (<see cref="MaxSections"/> × 24 = 192) = 320.
    /// Kept 8-aligned so subsequent struct writes are naturally aligned.
    /// </summary>
    public const int SectionsStartOffset = 128 + MaxSections * 24;

    private readonly Stream _stream;
    private readonly Dictionary<CacheSectionId, SectionTableEntry> _sections = new();
    private byte[] _lz4Buffer;
    private CacheSectionId _currentSection;
    private long _currentSectionStart;
    private bool _disposed;
    private bool _finalized;

    public TraceFileCacheWriter(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!_stream.CanSeek)
        {
            throw new ArgumentException("TraceFileCacheWriter requires a seekable stream for header patching.", nameof(stream));
        }

        // Pre-allocate an LZ4 output buffer sized for the worst-case chunk (ByteCap). LZ4 expansion is tiny for incompressible data.
        _lz4Buffer = new byte[LZ4Codec.MaximumOutputSize(TraceFileCacheConstants.ByteCap)];

        // Reserve header + section-table space by writing placeholder zeros. This positions the stream at SectionsStartOffset so the first
        // section write lands in a known place.
        _stream.SetLength(SectionsStartOffset);
        _stream.Position = SectionsStartOffset;
    }

    /// <summary>Current byte offset in the cache file. Useful for capturing chunk offsets before appending their compressed payload.</summary>
    public long CurrentOffset => _stream.Position;

    /// <summary>
    /// Start a new section. If a section was already in progress, it is closed and its [offset, length) is recorded in the section table.
    /// </summary>
    public void BeginSection(CacheSectionId id)
    {
        ThrowIfDisposed();
        if (id == CacheSectionId.Invalid)
        {
            throw new ArgumentException("Invalid section ID.", nameof(id));
        }
        if (_sections.ContainsKey(id))
        {
            throw new InvalidOperationException($"Section {id} has already been written.");
        }

        CloseCurrentSection();
        _currentSection = id;
        _currentSectionStart = _stream.Position;
    }

    /// <summary>Raw bytes appended to the current section.</summary>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        ThrowIfNoSection();
        _stream.Write(bytes);
    }

    /// <summary>Write a single blittable struct to the current section.</summary>
    public void WriteStruct<T>(in T value) where T : unmanaged
    {
        ThrowIfNoSection();
        var span = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in Unsafe.AsRef(in value), 1));
        _stream.Write(span);
    }

    /// <summary>Write an array of blittable structs to the current section, packed contiguously.</summary>
    public void WriteArray<T>(ReadOnlySpan<T> values) where T : unmanaged
    {
        ThrowIfNoSection();
        if (values.IsEmpty)
        {
            return;
        }
        _stream.Write(MemoryMarshal.AsBytes(values));
    }

    /// <summary>
    /// LZ4-compress <paramref name="uncompressedRecords"/> and append the compressed bytes to the current section (which must be
    /// <see cref="CacheSectionId.FoldedChunkData"/>). Returns the byte offset and lengths needed to populate a matching
    /// <see cref="ChunkManifestEntry"/>.
    /// </summary>
    public (long CacheOffset, uint CompressedLength, uint UncompressedLength) AppendLz4Chunk(ReadOnlySpan<byte> uncompressedRecords)
    {
        ThrowIfNoSection();
        if (_currentSection != CacheSectionId.FoldedChunkData)
        {
            throw new InvalidOperationException($"AppendLz4Chunk only valid within the FoldedChunkData section; current is {_currentSection}.");
        }
        if (uncompressedRecords.IsEmpty)
        {
            throw new ArgumentException("Cannot write an empty chunk.", nameof(uncompressedRecords));
        }

        // Grow the LZ4 scratch buffer if the caller's chunk happens to exceed the nominal ByteCap (defensive — builder is supposed to respect
        // the cap, but don't crash if it doesn't).
        var maxCompressed = LZ4Codec.MaximumOutputSize(uncompressedRecords.Length);
        if (_lz4Buffer.Length < maxCompressed)
        {
            _lz4Buffer = new byte[maxCompressed];
        }

        var compressedLength = LZ4Codec.Encode(uncompressedRecords, _lz4Buffer);
        if (compressedLength <= 0)
        {
            throw new InvalidOperationException($"LZ4 encode failed for chunk of {uncompressedRecords.Length} B (compressed result {compressedLength}).");
        }

        var cacheOffset = _stream.Position;
        _stream.Write(_lz4Buffer.AsSpan(0, compressedLength));
        return (cacheOffset, (uint)compressedLength, (uint)uncompressedRecords.Length);
    }

    /// <summary>
    /// Write the source file's span-name table (count u16 + entries of (u16 id, shortString name)). Null or empty input emits a zero count.
    /// </summary>
    public void WriteSpanNameTable(IReadOnlyDictionary<int, string> spanNames)
    {
        ThrowIfNoSection();
        if (_currentSection != CacheSectionId.SpanNameTable)
        {
            throw new InvalidOperationException($"WriteSpanNameTable only valid within the SpanNameTable section; current is {_currentSection}.");
        }

        var count = spanNames?.Count ?? 0;
        if (count > ushort.MaxValue)
        {
            throw new InvalidOperationException($"SpanNameTable has {count} entries, exceeds u16 limit.");
        }

        // Single stackalloc reused for the count prefix and every per-entry u16 ID — avoids stackalloc-in-loop which accumulates in the method
        // frame and could overflow with large span-name tables.
        Span<byte> u16Buf = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(u16Buf, (ushort)count);
        _stream.Write(u16Buf);

        if (count == 0)
        {
            return;
        }

        foreach (var (id, name) in spanNames!)
        {
            if (id < 0 || id > ushort.MaxValue)
            {
                throw new InvalidOperationException($"Span name ID {id} is out of range for u16.");
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(u16Buf, (ushort)id);
            _stream.Write(u16Buf);
            WriteShortString(name);
        }
    }

    /// <summary>
    /// Write the v12 queue-name intern table — count u16 + entries of (u16 queueId, shortString name). Null or empty input emits a zero count and returns.
    /// Used by <see cref="CacheSectionId.QueueNameTable"/>.
    /// </summary>
    public void WriteQueueNameTable(IReadOnlyDictionary<ushort, string> queueIdToName)
    {
        ThrowIfNoSection();
        if (_currentSection != CacheSectionId.QueueNameTable)
        {
            throw new InvalidOperationException($"WriteQueueNameTable only valid within the QueueNameTable section; current is {_currentSection}.");
        }

        var count = queueIdToName?.Count ?? 0;
        if (count > ushort.MaxValue)
        {
            throw new InvalidOperationException($"QueueNameTable has {count} entries, exceeds u16 limit.");
        }

        Span<byte> u16Buf = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(u16Buf, (ushort)count);
        _stream.Write(u16Buf);

        if (count == 0)
        {
            return;
        }

        foreach (var (id, name) in queueIdToName!)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(u16Buf, id);
            _stream.Write(u16Buf);
            WriteShortString(name ?? string.Empty);
        }
    }

    /// <summary>
    /// Finalize the cache file: closes any in-progress section, writes the section table at offset 128, writes the final header (including
    /// the caller's pre-computed fingerprint) at offset 0. The stream is positioned at EOF on return.
    /// </summary>
    public void Finalize(in CacheHeader header)
    {
        ThrowIfDisposed();
        if (_finalized)
        {
            throw new InvalidOperationException("Writer has already been finalized.");
        }

        CloseCurrentSection();

        var eofPosition = _stream.Position;

        // Write the section table at its reserved position (immediately after the header).
        _stream.Position = 128;
        var entryCount = 0;
        foreach (var entry in _sections.Values)
        {
            WriteSectionEntryAt(_stream, entry);
            entryCount++;
            if (entryCount > MaxSections)
            {
                throw new InvalidOperationException($"Too many sections ({entryCount}) — exceeds MaxSections={MaxSections}.");
            }
        }

        // Pad out the reserved region with zero-filled entries so readers scanning the fixed table size read valid (Invalid-id) entries.
        while (entryCount < MaxSections)
        {
            WriteSectionEntryAt(_stream, default);
            entryCount++;
        }

        // Finalize the header. Overwrite the caller's SectionTableOffset/Length with the actual values we reserved + emitted.
        var finalHeader = header;
        finalHeader.Magic = CacheHeader.MagicValue;
        finalHeader.Version = CacheHeader.CurrentVersion;
        finalHeader.SectionTableOffset = 128;
        finalHeader.SectionTableLength = MaxSections * Marshal.SizeOf<SectionTableEntry>();

        _stream.Position = 0;
        var headerSpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in finalHeader, 1));
        _stream.Write(headerSpan);

        // Restore the stream position to EOF so the file's length is correct and callers see the expected seekable size.
        _stream.Position = eofPosition;
        _stream.Flush();
        _finalized = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (!_finalized)
        {
            // Leave the stream to the caller; they may want to treat an unfinalized file as "building in progress" and keep it.
        }
        _stream.Dispose();
    }

    private void CloseCurrentSection()
    {
        if (_currentSection == CacheSectionId.Invalid)
        {
            return;
        }

        var length = _stream.Position - _currentSectionStart;
        _sections[_currentSection] = new SectionTableEntry
        {
            SectionId = (ushort)_currentSection,
            Flags = 0,
            Padding = 0,
            Offset = _currentSectionStart,
            Length = length,
        };
        _currentSection = CacheSectionId.Invalid;
    }

    private static void WriteSectionEntryAt(Stream stream, SectionTableEntry entry)
    {
        var span = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in entry, 1));
        stream.Write(span);
    }

    private void WriteShortString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > byte.MaxValue)
        {
            throw new InvalidOperationException($"Span name length {bytes.Length} exceeds u8 short-string limit.");
        }
        _stream.WriteByte((byte)bytes.Length);
        _stream.Write(bytes);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TraceFileCacheWriter));
        }
    }

    private void ThrowIfNoSection()
    {
        ThrowIfDisposed();
        if (_currentSection == CacheSectionId.Invalid)
        {
            throw new InvalidOperationException("No section in progress. Call BeginSection before writing.");
        }
    }
}
