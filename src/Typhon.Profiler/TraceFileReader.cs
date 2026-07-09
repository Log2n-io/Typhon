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
/// Reads a <c>.typhon-trace</c> binary trace file — the variable-size typed-record layout (see <see cref="TraceFileHeader.CurrentVersion"/> for the
/// current format version; <see cref="MinSupportedVersion"/> is the oldest still accepted). Provides sequential block-by-block access, yielding a raw
/// byte span that the caller walks as a sequence of size-prefixed records.
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

    /// <summary>
    /// Wraps <paramref name="stream"/> for reading a <c>.typhon-trace</c> file. Call <see cref="ReadHeader"/> and the metadata-table readers, then
    /// iterate <see cref="ReadNextBlock"/> — see the type remarks for the full sequence. The reader takes ownership and disposes the stream in
    /// <see cref="Dispose"/>.
    /// </summary>
    /// <param name="stream">Input stream positioned at the start of a <c>.typhon-trace</c> file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    public TraceFileReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _binaryReader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        _compressedBuffer = new byte[64 * 1024];
    }

    /// <summary>
    /// Oldest format version this reader still accepts. Bumped to 11 (2026-05-17) for the Track→DAG partitioning hierarchy (#354): the SystemDefinitionTable
    /// layout gained a trailing <c>DagId</c> field and the global PhasesTable was replaced by a TracksTable + DagsTable. That is a layout-breaking change —
    /// v10-and-older traces would mis-decode, so the reader hard-rejects them. Re-record against a v11 build.
    /// </summary>
    public const ushort MinSupportedVersion = 11;

    /// <summary>
    /// On-disk header layout segments. v11 is the oldest supported version, so every segment is always present on disk.
    /// The prefix runs Magic .. SourceLocationManifestOffset; the query-offset + CPU-offset segments follow, then the trailing reserved pad.
    /// Total on-disk size: v11 = 91 bytes.
    /// </summary>
    private const int HeaderCommonPrefixSize = 63; // Magic .. SourceLocationManifestOffset (incl. TrackCount + DagCount, v11)
    private const int HeaderQueryOffsetsSize = 16; // QuerySourceStringTableOffset + QueryDefinitionTableOffset
    private const int HeaderCpuOffsetSize = 8;     // CpuSampleSectionOffset
    private const int HeaderReservedSize = 4;      // Reserved0 + Reserved1

    /// <summary>Reads and validates the file header. Must be called first.</summary>
    /// <exception cref="InvalidDataException">If magic or version is wrong.</exception>
    public TraceFileHeader ReadHeader()
    {
        // The header is read as raw bytes and decoded by on-disk version. v11 is the oldest supported version, so every header segment is always present on
        // disk — the version-conditional segment reads below are unconditional in practice but kept structured for clarity. Older versions are rejected by
        // MinSupportedVersion.
        var fullSize = Unsafe.SizeOf<TraceFileHeader>();
        Span<byte> headerBytes = stackalloc byte[fullSize];

        // Peek the first 6 bytes (Magic + Version) to pick the layout before reading the rest.
        Span<byte> peek = stackalloc byte[6];
        _stream.ReadExactly(peek);

        // Validate magic FIRST. A file with wrong magic but plausible-looking bytes at offset 4-6 (where the version lives) would otherwise emit a misleading
        // "Unsupported version" error; the user's correct read is "this isn't a Typhon trace file at all".
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(peek[..4]);
        if (magic != TraceFileHeader.MagicValue)
        {
            throw new InvalidDataException(
                $"Invalid trace file magic: 0x{magic:X8} (expected 0x{TraceFileHeader.MagicValue:X8})");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(peek[4..6]);
        if (version < MinSupportedVersion || version > TraceFileHeader.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported trace file version: {version}. This build reads versions "
                + $"{MinSupportedVersion}..{TraceFileHeader.CurrentVersion}. Re-record against a current build.");
        }

        peek.CopyTo(headerBytes);

        // Common prefix beyond the peek — present in every supported version.
        _stream.ReadExactly(headerBytes[6..HeaderCommonPrefixSize]);
        var cursor = HeaderCommonPrefixSize;

        // QuerySourceStringTableOffset + QueryDefinitionTableOffset — on disk for v9+, zeroed for v8.
        if (version >= 9)
        {
            _stream.ReadExactly(headerBytes.Slice(cursor, HeaderQueryOffsetsSize));
        }
        else
        {
            headerBytes.Slice(cursor, HeaderQueryOffsetsSize).Clear();
        }
        cursor += HeaderQueryOffsetsSize;

        // CpuSampleSectionOffset — on disk for v10+, zeroed for earlier versions.
        if (version >= 10)
        {
            _stream.ReadExactly(headerBytes.Slice(cursor, HeaderCpuOffsetSize));
        }
        else
        {
            headerBytes.Slice(cursor, HeaderCpuOffsetSize).Clear();
        }
        cursor += HeaderCpuOffsetSize;

        // Reserved0 + Reserved1 trail every on-disk version.
        _stream.ReadExactly(headerBytes.Slice(cursor, HeaderReservedSize));

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

            // Track→DAG hierarchy (v11+) — trailing DagId ushort.
            var dagId = _binaryReader.ReadUInt16();

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
                DagId = dagId,
            });
        }
        return _systems;
    }

    /// <summary>
    /// Tracks table (v11+, #354) — the top level of the runtime partitioning hierarchy. Available after <see cref="ReadTracks"/>.
    /// </summary>
    public IReadOnlyList<TrackRecord> Tracks => _tracks;
    private readonly List<TrackRecord> _tracks = [];

    /// <summary>
    /// DAGs table (v11+, #354) — each DAG references its owning track by index and carries its own ordered phase names.
    /// Available after <see cref="ReadDags"/>.
    /// </summary>
    public IReadOnlyList<DagRecord> Dags => _dags;
    private readonly List<DagRecord> _dags = [];

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

    /// <summary>Reads the tracks table (v11+, #354). Call after <see cref="ReadComponentTypes"/>, before <see cref="ReadDags"/>.</summary>
    public IReadOnlyList<TrackRecord> ReadTracks()
    {
        _tracks.Clear();
        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            var name = ReadShortString();
            var orderIndex = _binaryReader.ReadInt32();
            var tags = ReadStringArray();
            _tracks.Add(new TrackRecord { Name = name, OrderIndex = orderIndex, Tags = tags });
        }
        return _tracks;
    }

    /// <summary>Reads the DAGs table (v11+, #354). Call after <see cref="ReadTracks"/>, before <see cref="ReadComponentDefinitions"/>.</summary>
    public IReadOnlyList<DagRecord> ReadDags()
    {
        _dags.Clear();
        var count = _binaryReader.ReadUInt16();
        for (var i = 0; i < count; i++)
        {
            var id = _binaryReader.ReadInt32();
            var name = ReadShortString();
            var trackIndex = _binaryReader.ReadInt32();
            var phaseNames = ReadStringArray();
            _dags.Add(new DagRecord { Id = id, Name = name, TrackIndex = trackIndex, PhaseNames = phaseNames });
        }
        return _dags;
    }

    /// <summary>Reads the rich component-definitions table (v7+). Call after <see cref="ReadDags"/>.</summary>
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

        RuntimeConfig = new RuntimeConfigRecord
        {
            BaseTickRate = baseTickRate,
            WorkerCount = workerCount,
            TelemetryRingCapacity = telemetryRingCapacity,
            ParallelQueryMinChunkSize = parallelMin,
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
            firstWord == TraceFileWriter.SourceLocationManifestMagic ||
            firstWord == TraceFileWriter.QuerySourceStringTableMagic ||
            firstWord == TraceFileWriter.QueryDefinitionTableMagic ||
            firstWord == TraceFileWriter.CpuSampleSectionMagic)
        {
            // First trailing section marks end-of-blocks — none of these are event blocks. Rewind so the dedicated TryRead* helpers can seek to the trailer,
            // and signal end-of-blocks. Trailer sections can appear in any order / subset (a CPU-sampled trace with no source-resolved frames carries no
            // FileTable), so every known trailer magic must terminate the block scan, not just the #302 pair.
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

    /// <summary>Disposes the reader, returns any pooled block buffer to <see cref="ArrayPool{T}"/>, and closes the underlying stream. Idempotent.</summary>
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
        files = [];
        entries = [];

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

    /// <summary>
    /// Read the trailing <c>FileTable</c> (interned source-file paths) on its own. Unlike <see cref="TryReadSourceLocationManifest"/> — which requires the
    /// <c>SourceLocationManifest</c> trailer to be present too — this reads the <c>FileTable</c> whenever <see cref="TraceFileHeader.FileTableOffset"/> is
    /// non-zero. A trace can carry a populated <c>FileTable</c> (because CPU-sample frames resolved to source, #351) without any #302 call-site manifest, so
    /// the CPU-sample loader needs a manifest-independent way to map a <c>CpuFrameSymbol.FileId</c> back to a path. Returns <c>false</c> when no FileTable
    /// was written. Uses absolute seek; requires a seekable stream.
    /// </summary>
    /// <param name="files">Output: array of source-file paths indexed by <c>FileId</c>. Empty when the trace carries no FileTable.</param>
    public bool TryReadFileTable(out string[] files)
    {
        files = [];
        if (Header.FileTableOffset == 0)
        {
            return false;
        }
        if (!_stream.CanSeek)
        {
            throw new InvalidOperationException("TryReadFileTable requires a seekable stream.");
        }

        var savedPos = _stream.Position;
        try
        {
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
            return true;
        }
        finally
        {
            _stream.Position = savedPos;
        }
    }

    /// <summary>
    /// Read the trailing QuerySourceStringTable (#342, v9+). Returns <c>false</c> if the trace file doesn't carry one (offset in the header is zero — e.g.,
    /// v8 trace, or v9 trace with no Query Definition Export activity). Uses absolute seek; requires a seekable stream.
    /// </summary>
    /// <param name="strings">
    /// Output: array of source strings indexed by ID. Slot 0 is the sentinel ("no string") and is always empty/null. Caller uses these IDs to resolve
    /// <c>DefinitionSourceFileId</c>, <c>ExecutionSourceFileId</c>, etc. on Query Definition Export events.
    /// </param>
    public bool TryReadQuerySourceStringTable(out string[] strings)
    {
        strings = [];
        if (Header.QuerySourceStringTableOffset == 0)
        {
            return false;
        }
        if (!_stream.CanSeek)
        {
            throw new InvalidOperationException("TryReadQuerySourceStringTable requires a seekable stream.");
        }

        var savedPos = _stream.Position;
        try
        {
            _stream.Position = Header.QuerySourceStringTableOffset;
            var magic = _binaryReader.ReadUInt32();
            if (magic != TraceFileWriter.QuerySourceStringTableMagic)
            {
                throw new InvalidDataException(
                    $"Bad QuerySourceStringTable magic at offset {Header.QuerySourceStringTableOffset}: 0x{magic:X8} "
                    + $"(expected 0x{TraceFileWriter.QuerySourceStringTableMagic:X8})");
            }
            var count = _binaryReader.ReadUInt32();
            strings = new string[count];
            for (uint i = 0; i < count; i++)
            {
                strings[i] = ReadVarString();
            }
            return true;
        }
        finally
        {
            _stream.Position = savedPos;
        }
    }

    /// <summary>
    /// Read the trailing CpuSampleSection (#351, v10+). Returns <c>false</c> if the trace file doesn't carry one (the header offset is zero — a v9-or-earlier
    /// trace, or a v10 trace captured without CPU sampling). Uses absolute seek; requires a seekable stream.
    /// </summary>
    /// <param name="samples">Output: CPU samples, sorted by qpc and grouped per thread slot; each references a stack by <c>StackIndex</c>.</param>
    /// <param name="stacks">Output: the interned stack table — each entry is a leaf-first array of frame ids into <paramref name="frameSymbols"/>.</param>
    /// <param name="frameSymbols">Output: the interned frame symbols; <c>FileId</c> indexes the same <c>FileTable</c> the source-location manifest uses.</param>
    public bool TryReadCpuSampleSection(out CpuSampleRecord[] samples, out ushort[][] stacks, out CpuFrameSymbol[] frameSymbols)
    {
        samples = [];
        stacks = [];
        frameSymbols = [];
        if (Header.CpuSampleSectionOffset == 0)
        {
            return false;
        }
        if (!_stream.CanSeek)
        {
            throw new InvalidOperationException("TryReadCpuSampleSection requires a seekable stream.");
        }

        var savedPos = _stream.Position;
        try
        {
            _stream.Position = Header.CpuSampleSectionOffset;
            var magic = _binaryReader.ReadUInt32();
            if (magic != TraceFileWriter.CpuSampleSectionMagic)
            {
                throw new InvalidDataException(
                    $"Bad CpuSampleSection magic at offset {Header.CpuSampleSectionOffset}: 0x{magic:X8} "
                    + $"(expected 0x{TraceFileWriter.CpuSampleSectionMagic:X8})");
            }

            var sampleCount = _binaryReader.ReadUInt32();
            samples = new CpuSampleRecord[sampleCount];
            for (uint i = 0; i < sampleCount; i++)
            {
                var qpc = _binaryReader.ReadInt64();
                var rawSlot = _binaryReader.ReadByte();
                var sampleType = _binaryReader.ReadByte();
                var stackIndex = _binaryReader.ReadUInt32();
                samples[i] = new CpuSampleRecord(qpc, rawSlot == 0xFF ? -1 : rawSlot, sampleType, stackIndex);
            }

            var stackCount = _binaryReader.ReadUInt32();
            stacks = new ushort[stackCount][];
            for (uint i = 0; i < stackCount; i++)
            {
                var frameCount = _binaryReader.ReadUInt16();
                var frames = new ushort[frameCount];
                for (var f = 0; f < frameCount; f++)
                {
                    frames[f] = _binaryReader.ReadUInt16();
                }
                stacks[i] = frames;
            }

            var frameSymbolCount = _binaryReader.ReadUInt32();
            frameSymbols = new CpuFrameSymbol[frameSymbolCount];
            for (uint i = 0; i < frameSymbolCount; i++)
            {
                var frameId = _binaryReader.ReadUInt16();
                var fileId = _binaryReader.ReadUInt16();
                var line = _binaryReader.ReadUInt32();
                var method = ReadShortString();
                frameSymbols[i] = new CpuFrameSymbol(frameId, fileId, line, method);
            }
            return true;
        }
        finally
        {
            _stream.Position = savedPos;
        }
    }
}
