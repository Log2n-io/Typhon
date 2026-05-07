using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Typhon.Profiler;

/// <summary>
/// Reads a <c>.typhon-trace</c> binary trace file (format v3 — variable-size typed records). Provides sequential block-by-block access,
/// yielding a raw byte span that the caller walks as a sequence of size-prefixed records.
/// </summary>
/// <remarks>
/// <para>
/// Typical use pattern:
/// <code>
/// using var reader = new TraceFileReader(stream);
/// reader.ReadHeader();
/// reader.ReadSystemDefinitions();
/// reader.ReadArchetypes();
/// reader.ReadComponentTypes();
/// while (reader.ReadNextBlock(out var records))
/// {
///     var pos = 0;
///     while (pos &lt; records.Length)
///     {
///         var size = BinaryPrimitives.ReadUInt16LittleEndian(records.Span[pos..]);
///         var kind = (TraceEventKind)records.Span[pos + 2];
///         // dispatch to typed codec based on kind
///         pos += size;
///     }
/// }
/// reader.ReadSpanNames();  // optional trailing table
/// </code>
/// </para>
/// </remarks>
public sealed class TraceFileReader : IDisposable
{
    private readonly Stream _stream;
    private readonly BinaryReader _binaryReader;
    private byte[] _compressedBuffer;
    /// <summary>
    /// Pooled buffer handed out via <see cref="ReadNextBlock"/>. The block is LZ4-decoded directly into this buffer — no staging
    /// copy. The returned <see cref="ReadOnlyMemory{Byte}"/> is valid only until the next call to <see cref="ReadNextBlock"/> or
    /// <see cref="Dispose"/>, at which point this buffer is returned to <see cref="ArrayPool{T}.Shared"/> and a new one is rented.
    /// Null when no block has been read yet.
    /// </summary>
    private byte[] _rentedBlock;
    private bool _disposed;

    /// <summary>File header, available after <see cref="ReadHeader"/>.</summary>
    public TraceFileHeader Header { get; private set; }

    /// <summary>System definitions, available after <see cref="ReadSystemDefinitions"/>.</summary>
    public IReadOnlyList<SystemDefinitionRecord> Systems => _systems;
    private readonly List<SystemDefinitionRecord> _systems = [];

    /// <summary>Archetype table, available after <see cref="ReadArchetypes"/>.</summary>
    public IReadOnlyList<ArchetypeRecord> Archetypes => _archetypes;
    private readonly List<ArchetypeRecord> _archetypes = [];

    /// <summary>Component type table, available after <see cref="ReadComponentTypes"/>.</summary>
    public IReadOnlyList<ComponentTypeRecord> ComponentTypes => _componentTypes;
    private readonly List<ComponentTypeRecord> _componentTypes = [];

    /// <summary>Span name intern table, available after <see cref="ReadSpanNames"/>.</summary>
    public IReadOnlyDictionary<int, string> SpanNames => _spanNames;
    private readonly Dictionary<int, string> _spanNames = new();

    public TraceFileReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _binaryReader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        _compressedBuffer = new byte[64 * 1024];
    }

    /// <summary>
    /// Oldest format version this reader still accepts. v4 traces have no source-location manifest
    /// (their on-disk header is 20 bytes shorter); we synthesize zeroed offset fields and downstream
    /// code interprets that as "no manifest". Bump this when removing back-compat for an older v.
    /// </summary>
    public const ushort MinSupportedVersion = 4;

    /// <summary>Reads and validates the file header. Must be called first.</summary>
    /// <exception cref="InvalidDataException">If magic or version is wrong.</exception>
    public TraceFileHeader ReadHeader()
    {
        // v4 layout ends at SamplingSessionStartQpc (51 bytes); v5+ appends FileTableOffset + SourceLocationManifestOffset + Reserved0/1 (+20 bytes).
        // v4 files transparently use the shorter read; their absent fields default to 0 ("no manifest").
        // v6 keeps the same struct shape as v5 — only data sections after the header changed (RFC 07 fields + Phases table).
        const int V4HeaderSize = 51;
        var fullSize = Unsafe.SizeOf<TraceFileHeader>();

        Span<byte> headerBytes = stackalloc byte[fullSize];
        _stream.ReadExactly(headerBytes[..V4HeaderSize]);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes[4..6]);

        if (version < MinSupportedVersion || version > TraceFileHeader.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported trace file version: {version}. This build reads versions "
                + $"{MinSupportedVersion}..{TraceFileHeader.CurrentVersion}.");
        }

        if (version >= 5)
        {
            // v5+ has the full 71-byte header (FileTable + SourceLocationManifest offsets included).
            _stream.ReadExactly(headerBytes[V4HeaderSize..fullSize]);
        }
        else
        {
            // v4 — clear the bytes we didn't read so the marshalled struct sees zeroed offset fields.
            headerBytes[V4HeaderSize..fullSize].Clear();
        }

        Header = MemoryMarshal.Read<TraceFileHeader>(headerBytes);

        if (Header.Magic != TraceFileHeader.MagicValue)
        {
            throw new InvalidDataException(
                $"Invalid trace file magic: 0x{Header.Magic:X8} (expected 0x{TraceFileHeader.MagicValue:X8})");
        }
        return Header;
    }

    /// <summary>Reads the system definition table. Call after <see cref="ReadHeader"/>.</summary>
    /// <remarks>
    /// Format v6+ appends RFC 07 access declarations per system. v5 traces lack the trailing fields;
    /// the reader returns empty defaults for them.
    /// </remarks>
    public IReadOnlyList<SystemDefinitionRecord> ReadSystemDefinitions()
    {
        _systems.Clear();
        var hasAccessDecls = Header.Version >= 6;
        var count = _binaryReader.ReadUInt16();

        for (var i = 0; i < count; i++)
        {
            var index = _binaryReader.ReadUInt16();
            var name = ReadShortString();
            var type = _binaryReader.ReadByte();
            var priority = _binaryReader.ReadByte();
            var isParallel = _binaryReader.ReadBoolean();
            var tierFilter = _binaryReader.ReadByte();

            var predCount = _binaryReader.ReadByte();
            var predecessors = new ushort[predCount];
            for (var p = 0; p < predCount; p++)
            {
                predecessors[p] = _binaryReader.ReadUInt16();
            }

            var succCount = _binaryReader.ReadByte();
            var successors = new ushort[succCount];
            for (var s = 0; s < succCount; s++)
            {
                successors[s] = _binaryReader.ReadUInt16();
            }

            var phaseName = string.Empty;
            var isExclusivePhase = false;
            string[] reads = [], readsFresh = [], readsSnapshot = [], additionalReads = [], writes = [], sideWrites = [];
            string[] writesEvents = [], readsEvents = [], writesResources = [], readsResources = [];
            string[] explicitAfter = [], explicitBefore = [];
            if (hasAccessDecls)
            {
                phaseName = ReadShortString();
                isExclusivePhase = _binaryReader.ReadBoolean();
                reads = ReadStringArray();
                readsFresh = ReadStringArray();
                readsSnapshot = ReadStringArray();
                additionalReads = ReadStringArray();
                writes = ReadStringArray();
                sideWrites = ReadStringArray();
                writesEvents = ReadStringArray();
                readsEvents = ReadStringArray();
                writesResources = ReadStringArray();
                readsResources = ReadStringArray();
                explicitAfter = ReadStringArray();
                explicitBefore = ReadStringArray();
            }

            _systems.Add(new SystemDefinitionRecord
            {
                Index = index,
                Name = name,
                Type = type,
                Priority = priority,
                IsParallel = isParallel,
                TierFilter = tierFilter,
                Predecessors = predecessors,
                Successors = successors,
                PhaseName = phaseName,
                IsExclusivePhase = isExclusivePhase,
                Reads = reads,
                ReadsFresh = readsFresh,
                ReadsSnapshot = readsSnapshot,
                AdditionalReads = additionalReads,
                Writes = writes,
                SideWrites = sideWrites,
                WritesEvents = writesEvents,
                ReadsEvents = readsEvents,
                WritesResources = writesResources,
                ReadsResources = readsResources,
                ExplicitAfter = explicitAfter,
                ExplicitBefore = explicitBefore,
            });
        }
        return _systems;
    }

    /// <summary>
    /// Phase order list (RFC 07 §Q3 — <c>RuntimeOptions.Phases</c>), available after <see cref="ReadPhases"/>.
    /// Empty for v5 traces (the section is absent and not read).
    /// </summary>
    public IReadOnlyList<string> Phases => _phases;
    private readonly List<string> _phases = [];

    /// <summary>
    /// Reads the phases table (v6+). Call after <see cref="ReadComponentTypes"/>. For v5 traces this is a
    /// no-op — the section does not exist and no bytes are consumed.
    /// </summary>
    public IReadOnlyList<string> ReadPhases()
    {
        _phases.Clear();
        if (Header.Version < 6)
        {
            return _phases;
        }

        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            _phases.Add(ReadShortString());
        }
        return _phases;
    }

    /// <summary>Reads the archetype table. Call after <see cref="ReadSystemDefinitions"/>.</summary>
    public IReadOnlyList<ArchetypeRecord> ReadArchetypes()
    {
        _archetypes.Clear();
        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            var archetypeId = _binaryReader.ReadUInt16();
            var name = ReadShortString();
            _archetypes.Add(new ArchetypeRecord { ArchetypeId = archetypeId, Name = name });
        }
        return _archetypes;
    }

    /// <summary>Reads the component type table. Call after <see cref="ReadArchetypes"/>.</summary>
    public IReadOnlyList<ComponentTypeRecord> ReadComponentTypes()
    {
        _componentTypes.Clear();
        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            var id = _binaryReader.ReadInt32();
            var name = ReadShortString();
            _componentTypes.Add(new ComponentTypeRecord { ComponentTypeId = id, Name = name });
        }
        return _componentTypes;
    }

    /// <summary>
    /// Reads the next compressed block of raw records. Returns the decoded byte payload in <paramref name="records"/>; caller walks it as a
    /// sequence of u16-size-prefixed records.
    /// </summary>
    /// <param name="records">
    /// Receives the decoded block bytes. <see cref="ReadOnlyMemory{Byte}.Empty"/> on end-of-stream. The returned memory is rented from
    /// <see cref="ArrayPool{T}.Shared"/> and is valid only until the next call to <see cref="ReadNextBlock"/> or <see cref="Dispose"/>. Do not
    /// stash the slice across iterations — the underlying buffer is returned to the pool on the next call and may be handed to another caller.
    /// </param>
    /// <param name="recordCount">Number of records the block contains (from the block header).</param>
    /// <returns><c>true</c> if a block was read, <c>false</c> if end of stream.</returns>
    public bool ReadNextBlock(out ReadOnlyMemory<byte> records, out int recordCount)
    {
        records = default;
        recordCount = 0;

        Span<byte> blockHeader = stackalloc byte[TraceBlockEncoder.BlockHeaderSize];
        var bytesRead = _stream.Read(blockHeader);
        if (bytesRead == 0)
        {
            return false;
        }

        if (bytesRead < 4)
        {
            return false;
        }

        var firstWord = BinaryPrimitives.ReadUInt32LittleEndian(blockHeader);
        if (firstWord == TraceFileWriter.SpanNameTableMagic)
        {
            // Span name table marker — seek back, read it, then recurse.
            _stream.Position -= bytesRead;
            ReadSpanNames();
            return ReadNextBlock(out records, out recordCount);
        }
        if (firstWord == TraceFileWriter.FileTableMagic ||
            firstWord == TraceFileWriter.SourceLocationManifestMagic)
        {
            // Trailing source-location manifest sections (#302, Phase 3) — not a block. Rewind so a separate
            // caller can read the trailer via TryReadSourceLocationManifest, and signal end-of-blocks.
            _stream.Position -= bytesRead;
            return false;
        }

        if (bytesRead < TraceBlockEncoder.BlockHeaderSize)
        {
            return false;
        }

        var (uncompressedBytes, compressedBytes, count) = TraceBlockEncoder.ReadBlockHeader(blockHeader);
        recordCount = count;

        if (_compressedBuffer.Length < compressedBytes)
        {
            _compressedBuffer = new byte[compressedBytes];
        }

        // Return the previously-rented block before renting a new one — the caller's ReadOnlyMemory<byte> from the prior call becomes invalid
        // at this point per the documented lifetime contract on this method. ArrayPool.Rent may hand back a buffer larger than requested, so we
        // always slice to `uncompressedBytes` when exposing it via ReadOnlyMemory. LZ4 decodes directly into the rented buffer — no staging copy.
        if (_rentedBlock != null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBlock);
            _rentedBlock = null;
        }
        _rentedBlock = ArrayPool<byte>.Shared.Rent(uncompressedBytes);

        _stream.ReadExactly(_compressedBuffer.AsSpan(0, compressedBytes));
        TraceBlockEncoder.DecodeBlock(_compressedBuffer.AsSpan(0, compressedBytes), uncompressedBytes, _rentedBlock);

        records = new ReadOnlyMemory<byte>(_rentedBlock, 0, uncompressedBytes);
        return true;
    }

    /// <summary>
    /// Reads the span name table if present at the current stream position. Returns the cumulative dictionary (merges with any previously-read
    /// span name table). Tolerates end-of-stream — if fewer than 4 bytes remain, the method returns the current dictionary unchanged instead of
    /// throwing, since the span name table is an optional trailing structure.
    /// </summary>
    public IReadOnlyDictionary<int, string> ReadSpanNames()
    {
        // Guard against EOF: the span name table is optional, so a stream at EOF (or with < 4 bytes left) is valid "no table present".
        if (_stream.CanSeek && _stream.Length - _stream.Position < sizeof(uint))
        {
            return _spanNames;
        }

        var magic = _binaryReader.ReadUInt32();
        if (magic != TraceFileWriter.SpanNameTableMagic)
        {
            _stream.Position -= 4;
            return _spanNames;
        }

        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            var id = _binaryReader.ReadUInt16();
            var name = ReadShortString();
            _spanNames[id] = name;
        }
        return _spanNames;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_rentedBlock != null)
        {
            ArrayPool<byte>.Shared.Return(_rentedBlock);
            _rentedBlock = null;
        }
        _binaryReader.Dispose();
        _stream.Dispose();
    }

    private string ReadShortString()
    {
        var len = _binaryReader.ReadByte();
        var bytes = _binaryReader.ReadBytes(len);
        return Encoding.UTF8.GetString(bytes);
    }

    private string[] ReadStringArray()
    {
        var count = _binaryReader.ReadUInt16();
        if (count == 0)
        {
            return [];
        }
        var arr = new string[count];
        for (var i = 0; i < count; i++)
        {
            arr[i] = ReadShortString();
        }
        return arr;
    }

    private string ReadVarString()
    {
        var len = _binaryReader.ReadUInt16();
        var bytes = _binaryReader.ReadBytes(len);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Read the trailing source-location manifest (#302, Phase 3 of profiler-source-attribution).
    /// Returns <c>false</c> if the trace file doesn't carry one (offsets in the header are zero).
    /// Uses absolute seeks; requires a seekable stream.
    /// See claude/design/observability/10-profiler-source-attribution.md §4.6.
    /// </summary>
    public bool TryReadSourceLocationManifest(out string[] files, out SourceLocationManifestEntry[] entries)
    {
        files = Array.Empty<string>();
        entries = Array.Empty<SourceLocationManifestEntry>();

        if (Header.FileTableOffset == 0 || Header.SourceLocationManifestOffset == 0)
        {
            return false;
        }
        if (!_stream.CanSeek)
        {
            throw new InvalidOperationException("TryReadSourceLocationManifest requires a seekable stream.");
        }

        var savedPos = _stream.Position;
        try
        {
            // FileTable
            _stream.Position = Header.FileTableOffset;
            var fileMagic = _binaryReader.ReadUInt32();
            if (fileMagic != TraceFileWriter.FileTableMagic)
            {
                throw new InvalidDataException(
                    $"Bad FileTable magic at offset {Header.FileTableOffset}: 0x{fileMagic:X8} (expected 0x{TraceFileWriter.FileTableMagic:X8})");
            }
            var fileCount = _binaryReader.ReadUInt32();
            files = new string[fileCount];
            for (uint i = 0; i < fileCount; i++)
            {
                var fileId = _binaryReader.ReadUInt16();
                var path = ReadVarString();
                if (fileId < files.Length)
                {
                    files[fileId] = path;
                }
            }

            // SourceLocationManifest
            _stream.Position = Header.SourceLocationManifestOffset;
            var manifestMagic = _binaryReader.ReadUInt32();
            if (manifestMagic != TraceFileWriter.SourceLocationManifestMagic)
            {
                throw new InvalidDataException(
                    $"Bad SourceLocationManifest magic at offset {Header.SourceLocationManifestOffset}: 0x{manifestMagic:X8} "
                    + $"(expected 0x{TraceFileWriter.SourceLocationManifestMagic:X8})");
            }
            var entryCount = _binaryReader.ReadUInt32();
            entries = new SourceLocationManifestEntry[entryCount];
            for (uint i = 0; i < entryCount; i++)
            {
                var id = _binaryReader.ReadUInt16();
                var fileId = _binaryReader.ReadUInt16();
                var line = _binaryReader.ReadUInt32();
                var kind = _binaryReader.ReadByte();
                var method = ReadShortString();
                entries[i] = new SourceLocationManifestEntry(id, fileId, line, kind, method);
            }
            return true;
        }
        finally
        {
            _stream.Position = savedPos;
        }
    }
}
