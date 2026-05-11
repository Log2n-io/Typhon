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
    /// Oldest format version this reader still accepts. Bumped to 8 (2026-05-10) when
    /// <see cref="TraceEventKind.NamedSpan"/> was reassigned from value 200 to 246 to break a latent collision with
    /// <see cref="TraceEventKind.EcsQueryMaskAnd"/>. v7 traces with NamedSpan records would mis-decode under a v8 reader;
    /// hard-rejecting v7 surfaces the break loudly. Re-record against a v8 build.
    /// </summary>
    public const ushort MinSupportedVersion = 8;

    /// <summary>Reads and validates the file header. Must be called first.</summary>
    /// <exception cref="InvalidDataException">If magic or version is wrong.</exception>
    public TraceFileHeader ReadHeader()
    {
        // v7+ always uses the full header layout. Older versions had shorter on-disk headers (v4 stopped at
        // SamplingSessionStartQpc, 51 bytes; v5 added the trailer offsets); the partial-read code paths are
        // gone now that MinSupportedVersion == 7.
        var fullSize = Unsafe.SizeOf<TraceFileHeader>();
        Span<byte> headerBytes = stackalloc byte[fullSize];
        _stream.ReadExactly(headerBytes);

        // Validate magic FIRST. A file with wrong magic but plausible-looking bytes at offset 4-6 (where the version
        // lives) would otherwise emit a misleading "Unsupported version" error; the user's correct read is
        // "this isn't a Typhon trace file at all". Reading the u32 magic and the u16 version directly from the
        // span — no MemoryMarshal call needed before validation, so we avoid stamping a half-validated struct
        // onto Header until both checks pass.
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[..4]);
        if (magic != TraceFileHeader.MagicValue)
        {
            throw new InvalidDataException(
                $"Invalid trace file magic: 0x{magic:X8} (expected 0x{TraceFileHeader.MagicValue:X8})");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes[4..6]);
        if (version < MinSupportedVersion || version > TraceFileHeader.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported trace file version: {version}. This build reads versions "
                + $"{MinSupportedVersion}..{TraceFileHeader.CurrentVersion}. Re-record against a current build.");
        }

        Header = MemoryMarshal.Read<TraceFileHeader>(headerBytes);
        return Header;
    }

    /// <summary>Reads the system definition table. Call after <see cref="ReadHeader"/>.</summary>
    /// <remarks>
    /// v7+ — RFC 07 access declarations are always present (the v5/v6 partial-read paths were removed alongside
    /// the version bump in <see cref="MinSupportedVersion"/>).
    /// </remarks>
    public IReadOnlyList<SystemDefinitionRecord> ReadSystemDefinitions()
    {
        _systems.Clear();
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

            // RFC 07 access declarations — always present in v7+.
            var phaseName = ReadShortString();
            var isExclusivePhase = _binaryReader.ReadBoolean();
            var reads = ReadStringArray();
            var readsFresh = ReadStringArray();
            var readsSnapshot = ReadStringArray();
            var additionalReads = ReadStringArray();
            var writes = ReadStringArray();
            var sideWrites = ReadStringArray();
            var writesEvents = ReadStringArray();
            var readsEvents = ReadStringArray();
            var writesResources = ReadStringArray();
            var readsResources = ReadStringArray();
            var explicitAfter = ReadStringArray();
            var explicitBefore = ReadStringArray();

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

    /// <summary>Rich component-type definitions (v7+), available after <see cref="ReadComponentDefinitions"/>.</summary>
    public IReadOnlyList<ComponentDefinitionRecord> ComponentDefinitions => _componentDefinitions;
    private readonly List<ComponentDefinitionRecord> _componentDefinitions = [];

    /// <summary>Rich archetype definitions (v7+), available after <see cref="ReadArchetypeDefinitions"/>.</summary>
    public IReadOnlyList<ArchetypeDefinitionRecord> ArchetypeDefinitions => _archetypeDefinitions;
    private readonly List<ArchetypeDefinitionRecord> _archetypeDefinitions = [];

    /// <summary>Flat index catalog (v7+), available after <see cref="ReadIndexCatalog"/>.</summary>
    public IReadOnlyList<IndexCatalogEntry> IndexCatalog => _indexCatalog;
    private readonly List<IndexCatalogEntry> _indexCatalog = [];

    /// <summary>Runtime config snapshot (v7+), available after <see cref="ReadRuntimeConfig"/>. Null when the on-disk presence flag was clear.</summary>
    public RuntimeConfigRecord RuntimeConfig { get; private set; }

    /// <summary>Event-queue catalog (v7+), available after <see cref="ReadEventQueueCatalog"/>.</summary>
    public IReadOnlyList<EventQueueRecord> EventQueues => _eventQueues;
    private readonly List<EventQueueRecord> _eventQueues = [];

    /// <summary>Resource-graph snapshot (v7+), available after <see cref="ReadResourceGraphSnapshot"/>.</summary>
    public IReadOnlyList<ResourceGraphNodeRecord> ResourceGraphNodes => _resourceGraphNodes;
    private readonly List<ResourceGraphNodeRecord> _resourceGraphNodes = [];

    /// <summary>Reads the phases table. Call after <see cref="ReadComponentTypes"/>. v7+ — always present.</summary>
    public IReadOnlyList<string> ReadPhases()
    {
        _phases.Clear();
        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            _phases.Add(ReadShortString());
        }
        return _phases;
    }

    /// <summary>Reads the rich component-definitions table (v7+). Call after <see cref="ReadPhases"/>.</summary>
    public IReadOnlyList<ComponentDefinitionRecord> ReadComponentDefinitions()
    {
        _componentDefinitions.Clear();
        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            var componentTypeId = _binaryReader.ReadInt32();
            var name = ReadShortString();
            var revision = _binaryReader.ReadInt32();
            var storageMode = _binaryReader.ReadByte();
            var allowMultiple = _binaryReader.ReadBoolean();
            var storageSize = _binaryReader.ReadInt32();
            var storageOverhead = _binaryReader.ReadInt32();
            var storageTotal = _binaryReader.ReadInt32();
            var indicesCount = _binaryReader.ReadUInt16();
            var multipleIndicesCount = _binaryReader.ReadUInt16();
            var spatialField = ReadShortString();

            var fieldCount = _binaryReader.ReadUInt16();
            var fields = new FieldDefinitionRecord[fieldCount];
            for (var f = 0; f < fieldCount; f++)
            {
                fields[f] = new FieldDefinitionRecord
                {
                    FieldId = _binaryReader.ReadInt32(),
                    Name = ReadShortString(),
                    FieldType = _binaryReader.ReadByte(),
                    UnderlyingType = _binaryReader.ReadByte(),
                    Offset = _binaryReader.ReadInt32(),
                    Size = _binaryReader.ReadInt32(),
                    ArrayLength = _binaryReader.ReadInt32(),
                    Flags = _binaryReader.ReadByte(),
                    SpatialFieldType = _binaryReader.ReadByte(),
                    SpatialMode = _binaryReader.ReadByte(),
                    SpatialCellSize = _binaryReader.ReadSingle(),
                    SpatialMargin = _binaryReader.ReadSingle(),
                    SpatialCategory = _binaryReader.ReadUInt32(),
                    ForeignKeyTargetType = ReadShortString(),
                };
            }

            _componentDefinitions.Add(new ComponentDefinitionRecord
            {
                ComponentTypeId = componentTypeId,
                Name = name,
                Revision = revision,
                StorageMode = storageMode,
                AllowMultiple = allowMultiple,
                ComponentStorageSize = storageSize,
                ComponentStorageOverhead = storageOverhead,
                ComponentStorageTotalSize = storageTotal,
                IndicesCount = indicesCount,
                MultipleIndicesCount = multipleIndicesCount,
                SpatialField = spatialField,
                Fields = fields,
            });
        }
        return _componentDefinitions;
    }

    /// <summary>Reads the rich archetype-definitions table (v7+). Call after <see cref="ReadComponentDefinitions"/>.</summary>
    public IReadOnlyList<ArchetypeDefinitionRecord> ReadArchetypeDefinitions()
    {
        _archetypeDefinitions.Clear();
        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            var archetypeId = _binaryReader.ReadUInt16();
            var name = ReadShortString();
            var revision = _binaryReader.ReadInt32();
            var parentId = _binaryReader.ReadUInt16();

            var childCount = _binaryReader.ReadUInt16();
            var children = new ushort[childCount];
            for (var c = 0; c < childCount; c++) children[c] = _binaryReader.ReadUInt16();

            var componentCount = _binaryReader.ReadByte();
            var componentIdsLen = _binaryReader.ReadByte();
            var componentTypeIds = new int[componentIdsLen];
            for (var c = 0; c < componentIdsLen; c++) componentTypeIds[c] = _binaryReader.ReadInt32();

            var versionedMask = _binaryReader.ReadUInt16();
            var transientMask = _binaryReader.ReadUInt16();

            var cascadeCount = _binaryReader.ReadUInt16();
            var cascade = new ushort[cascadeCount];
            for (var c = 0; c < cascadeCount; c++) cascade[c] = _binaryReader.ReadUInt16();

            var flags = _binaryReader.ReadByte();
            var hasCluster = _binaryReader.ReadBoolean();
            ArchetypeClusterInfoRecord clusterInfo = null;
            if (hasCluster)
            {
                clusterInfo = new ArchetypeClusterInfoRecord
                {
                    ClusterSize = _binaryReader.ReadUInt16(),
                    ClusterStride = _binaryReader.ReadUInt32(),
                    HeaderSize = _binaryReader.ReadUInt32(),
                    EntityIdsOffset = _binaryReader.ReadUInt32(),
                    IndexElementIdsBaseOffset = _binaryReader.ReadUInt32(),
                    MultipleIndexedFieldCount = _binaryReader.ReadUInt16(),
                };
            }

            _archetypeDefinitions.Add(new ArchetypeDefinitionRecord
            {
                ArchetypeId = archetypeId,
                Name = name,
                Revision = revision,
                ParentArchetypeId = parentId,
                ChildArchetypeIds = children,
                ComponentCount = componentCount,
                ComponentTypeIds = componentTypeIds,
                VersionedSlotMask = versionedMask,
                TransientSlotMask = transientMask,
                CascadeTargets = cascade,
                Flags = flags,
                ClusterInfo = clusterInfo,
            });
        }
        return _archetypeDefinitions;
    }

    /// <summary>Reads the flat index-catalog table (v7+). Call after <see cref="ReadArchetypeDefinitions"/>.</summary>
    public IReadOnlyList<IndexCatalogEntry> ReadIndexCatalog()
    {
        _indexCatalog.Clear();
        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            _indexCatalog.Add(new IndexCatalogEntry
            {
                ComponentTypeId = _binaryReader.ReadInt32(),
                FieldId = _binaryReader.ReadInt32(),
                Variant = _binaryReader.ReadByte(),
                AllowMultiple = _binaryReader.ReadBoolean(),
                IsSpatial = _binaryReader.ReadBoolean(),
                IsAuto = _binaryReader.ReadBoolean(),
            });
        }
        return _indexCatalog;
    }

    /// <summary>
    /// Reads the runtime-config record (v7+). Call after <see cref="ReadIndexCatalog"/>. Returns null when the on-disk
    /// presence flag was clear (host had no engine-level config to capture, e.g., standalone profiling).
    /// </summary>
    public RuntimeConfigRecord ReadRuntimeConfig()
    {
        var present = _binaryReader.ReadBoolean();
        if (!present)
        {
            RuntimeConfig = null;
            return null;
        }

        var baseTickRate = _binaryReader.ReadInt32();
        var workerCount = _binaryReader.ReadInt32();
        var telemetryRingCapacity = _binaryReader.ReadInt32();
        var parallelMin = _binaryReader.ReadInt32();
        var defaultPhase = ReadShortString();

        var phaseCount = _binaryReader.ReadUInt16();
        var phases = new string[phaseCount];
        for (var i = 0; i < phaseCount; i++) phases[i] = ReadShortString();

        RuntimeConfig = new RuntimeConfigRecord
        {
            BaseTickRate = baseTickRate,
            WorkerCount = workerCount,
            TelemetryRingCapacity = telemetryRingCapacity,
            ParallelQueryMinChunkSize = parallelMin,
            DefaultPhase = defaultPhase,
            Phases = phases,
        };
        return RuntimeConfig;
    }

    /// <summary>Reads the event-queue catalog (v7+). Call after <see cref="ReadRuntimeConfig"/>.</summary>
    public IReadOnlyList<EventQueueRecord> ReadEventQueueCatalog()
    {
        _eventQueues.Clear();
        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            _eventQueues.Add(new EventQueueRecord
            {
                QueueIndex = _binaryReader.ReadUInt16(),
                Name = ReadShortString(),
                Capacity = _binaryReader.ReadInt32(),
                EventTypeName = ReadShortString(),
            });
        }
        return _eventQueues;
    }

    /// <summary>
    /// Convenience helper that walks the six v7 static-structure tables in order. Equivalent to calling each
    /// <c>Read...</c> in sequence; matches the writer's <c>WriteEmptyStaticStructures</c> shape so tests can
    /// pair them. After the call, the data is available via <see cref="ComponentDefinitions"/> /
    /// <see cref="ArchetypeDefinitions"/> / etc.
    /// </summary>
    public void ReadStaticStructures()
    {
        ReadComponentDefinitions();
        ReadArchetypeDefinitions();
        ReadIndexCatalog();
        ReadRuntimeConfig();
        ReadEventQueueCatalog();
        ReadResourceGraphSnapshot();
    }

    /// <summary>Reads the resource-graph snapshot (v7+). Call after <see cref="ReadEventQueueCatalog"/>.</summary>
    public IReadOnlyList<ResourceGraphNodeRecord> ReadResourceGraphSnapshot()
    {
        _resourceGraphNodes.Clear();
        var count = _binaryReader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            _resourceGraphNodes.Add(new ResourceGraphNodeRecord
            {
                Id = _binaryReader.ReadInt64(),
                Name = ReadShortString(),
                Type = _binaryReader.ReadByte(),
                ParentId = _binaryReader.ReadInt64(),
                CreatedAtUtcTicks = _binaryReader.ReadInt64(),
                ExhaustionPolicy = _binaryReader.ReadByte(),
            });
        }
        return _resourceGraphNodes;
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
    /// See claude/design/Profiler/10-profiler-source-attribution.md §4.6.
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
