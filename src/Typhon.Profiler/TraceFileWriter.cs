using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using K4os.Compression.LZ4;

namespace Typhon.Profiler;

/// <summary>
/// Writes a <c>.typhon-trace</c> binary trace file (format v3, the variable-size typed-record layout introduced in the Tracy-style profiler
/// rewrite). Owns the underlying stream; not thread-safe — callers must serialize writes (the profiler's exporter thread is the only writer).
/// </summary>
/// <remarks>
/// <para>
/// File layout (v3):
/// <code>
/// [TraceFileHeader]           64 B, fixed
/// [SystemDefinitionTable]     variable — system DAG definitions
/// [ArchetypeTable]            variable — archetype ID → name map
/// [ComponentTypeTable]        variable — component type ID → name map
/// [CompressedBlock]*          repeating: block header + LZ4-compressed raw record bytes
/// [SpanNameTable]             optional trailing table of runtime-interned NamedSpan names
/// </code>
/// </para>
/// <para>
/// Each compressed block wraps an LZ4-encoded byte run that is a concatenation of variable-size typed records as they come off the producer's
/// ring buffer. The block header declares the record count and uncompressed byte count; the reader uses those to walk records one at a time via
/// the u16 size field at the start of each.
/// </para>
/// </remarks>
public sealed class TraceFileWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly BinaryWriter _writer;
    private byte[] _compressedBuffer;
    private bool _disposed;

    /// <summary>Maximum bytes per compressed block. Batches larger than this are split by the exporter before calling <see cref="WriteRecords"/>.</summary>
    public const int MaxBlockBytes = 256 * 1024;

    public TraceFileWriter(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        _compressedBuffer = new byte[LZ4Codec.MaximumOutputSize(MaxBlockBytes)];
    }

    /// <summary>Writes the file header. Must be called exactly once before any other writes.</summary>
    public void WriteHeader(in TraceFileHeader header)
    {
        var span = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in header, 1));
        _stream.Write(span);
    }

    /// <summary>Writes the system definition table. Must be called exactly once after the header.</summary>
    /// <remarks>
    /// Format v6 (current) appends RFC 07 access declarations per system. Reader negotiates: v5 traces lack
    /// the trailing fields and the reader fills them with empty defaults.
    /// </remarks>
    public void WriteSystemDefinitions(ReadOnlySpan<SystemDefinitionRecord> systems)
    {
        _writer.Write((ushort)systems.Length);
        foreach (var sys in systems)
        {
            _writer.Write(sys.Index);
            WriteShortString(sys.Name);
            _writer.Write(sys.Type);
            _writer.Write(sys.Priority);
            _writer.Write(sys.IsParallel);
            _writer.Write(sys.TierFilter);

            _writer.Write((byte)sys.Predecessors.Length);
            foreach (var pred in sys.Predecessors)
            {
                _writer.Write(pred);
            }

            _writer.Write((byte)sys.Successors.Length);
            foreach (var succ in sys.Successors)
            {
                _writer.Write(succ);
            }

            // ── RFC 07 access declarations (v6+) ─────────────────────────────
            WriteShortString(sys.PhaseName ?? string.Empty);
            _writer.Write(sys.IsExclusivePhase);
            WriteStringArray(sys.Reads);
            WriteStringArray(sys.ReadsFresh);
            WriteStringArray(sys.ReadsSnapshot);
            WriteStringArray(sys.AdditionalReads);
            WriteStringArray(sys.Writes);
            WriteStringArray(sys.SideWrites);
            WriteStringArray(sys.WritesEvents);
            WriteStringArray(sys.ReadsEvents);
            WriteStringArray(sys.WritesResources);
            WriteStringArray(sys.ReadsResources);
            WriteStringArray(sys.ExplicitAfter);
            WriteStringArray(sys.ExplicitBefore);
        }
        _writer.Flush();
    }

    /// <summary>
    /// Writes the phases table (v6+). One entry per <c>RuntimeOptions.Phases</c> name in declaration order.
    /// Empty array is valid (legacy session with no phase declarations); the reader returns an empty list.
    /// </summary>
    public void WritePhases(ReadOnlySpan<string> phaseNames)
    {
        _writer.Write((ushort)phaseNames.Length);
        foreach (var p in phaseNames)
        {
            WriteShortString(p ?? string.Empty);
        }
        _writer.Flush();
    }

    /// <summary>Writes the archetype table. Must be called once after system definitions.</summary>
    public void WriteArchetypes(ReadOnlySpan<ArchetypeRecord> archetypes)
    {
        _writer.Write((ushort)archetypes.Length);
        foreach (var a in archetypes)
        {
            _writer.Write(a.ArchetypeId);
            WriteShortString(a.Name);
        }
        _writer.Flush();
    }

    /// <summary>Writes the component type table. Must be called once after the archetype table.</summary>
    public void WriteComponentTypes(ReadOnlySpan<ComponentTypeRecord> componentTypes)
    {
        _writer.Write((ushort)componentTypes.Length);
        foreach (var c in componentTypes)
        {
            _writer.Write(c.ComponentTypeId);
            WriteShortString(c.Name);
        }
        _writer.Flush();
    }

    /// <summary>
    /// Writes the rich component-definitions table (v7+). One <see cref="ComponentDefinitionRecord"/> per registered component
    /// type — sits after <c>WritePhases</c> in the on-disk sequence. Empty array is valid (no components → 0 length prefix; the
    /// reader returns an empty list).
    /// </summary>
    public void WriteComponentDefinitions(ReadOnlySpan<ComponentDefinitionRecord> components)
    {
        _writer.Write((ushort)components.Length);
        foreach (var c in components)
        {
            _writer.Write(c.ComponentTypeId);
            WriteShortString(c.Name);
            _writer.Write(c.Revision);
            _writer.Write(c.StorageMode);
            _writer.Write(c.AllowMultiple);
            _writer.Write(c.ComponentStorageSize);
            _writer.Write(c.ComponentStorageOverhead);
            _writer.Write(c.ComponentStorageTotalSize);
            _writer.Write(c.IndicesCount);
            _writer.Write(c.MultipleIndicesCount);
            WriteShortString(c.SpatialField ?? string.Empty);

            var fields = c.Fields ?? [];
            _writer.Write((ushort)fields.Length);
            foreach (var f in fields)
            {
                _writer.Write(f.FieldId);
                WriteShortString(f.Name);
                _writer.Write(f.FieldType);
                _writer.Write(f.UnderlyingType);
                _writer.Write(f.Offset);
                _writer.Write(f.Size);
                _writer.Write(f.ArrayLength);
                _writer.Write(f.Flags);
                // Spatial sub-block — always present on the wire (4 bytes overhead) so the reader can walk records
                // without a flag-driven branch. Engine ignores the values when Flags & 0x08 is clear.
                _writer.Write(f.SpatialFieldType);
                _writer.Write(f.SpatialMode);
                _writer.Write(f.SpatialCellSize);
                _writer.Write(f.SpatialMargin);
                _writer.Write(f.SpatialCategory);
                WriteShortString(f.ForeignKeyTargetType ?? string.Empty);
            }
        }
        _writer.Flush();
    }

    /// <summary>
    /// Writes the rich archetype-definitions table (v7+). Variable-size per record — child id list and component-id list are
    /// length-prefixed; the cluster-info inline block is gated by the <see cref="ArchetypeDefinitionRecord.Flags"/> 0x01 bit.
    /// </summary>
    public void WriteArchetypeDefinitions(ReadOnlySpan<ArchetypeDefinitionRecord> archetypes)
    {
        _writer.Write((ushort)archetypes.Length);
        foreach (var a in archetypes)
        {
            _writer.Write(a.ArchetypeId);
            WriteShortString(a.Name);
            _writer.Write(a.Revision);
            _writer.Write(a.ParentArchetypeId);

            var children = a.ChildArchetypeIds ?? [];
            _writer.Write((ushort)children.Length);
            foreach (var ch in children) _writer.Write(ch);

            _writer.Write(a.ComponentCount);
            var componentIds = a.ComponentTypeIds ?? [];
            _writer.Write((byte)componentIds.Length);
            foreach (var id in componentIds) _writer.Write(id);

            _writer.Write(a.VersionedSlotMask);
            _writer.Write(a.TransientSlotMask);

            var cascade = a.CascadeTargets ?? [];
            _writer.Write((ushort)cascade.Length);
            foreach (var t in cascade) _writer.Write(t);

            _writer.Write(a.Flags);
            // Cluster-info presence is dictated by Flags & 0x01 (IsClusterEligible). Writing a one-byte presence
            // flag keeps the wire format unambiguous when ClusterInfo is null but Flags say cluster-eligible
            // (defensive — the engine guarantees the invariant but tests can build inconsistent records).
            var hasCluster = a.ClusterInfo != null;
            _writer.Write(hasCluster);
            if (hasCluster)
            {
                var ci = a.ClusterInfo;
                _writer.Write(ci.ClusterSize);
                _writer.Write(ci.ClusterStride);
                _writer.Write(ci.HeaderSize);
                _writer.Write(ci.EntityIdsOffset);
                _writer.Write(ci.IndexElementIdsBaseOffset);
                _writer.Write(ci.MultipleIndexedFieldCount);
            }
        }
        _writer.Flush();
    }

    /// <summary>Writes the flat (componentTypeId, fieldId) → index variant catalog (v7+).</summary>
    public void WriteIndexCatalog(ReadOnlySpan<IndexCatalogEntry> indexes)
    {
        _writer.Write((ushort)indexes.Length);
        foreach (var i in indexes)
        {
            _writer.Write(i.ComponentTypeId);
            _writer.Write(i.FieldId);
            _writer.Write(i.Variant);
            _writer.Write(i.AllowMultiple);
            _writer.Write(i.IsSpatial);
            _writer.Write(i.IsAuto);
        }
        _writer.Flush();
    }

    /// <summary>
    /// Writes the runtime-config record (v7+). Single record. Wire format prefixes with a one-byte presence flag so the reader
    /// can distinguish "no runtime config available" (flag=0) from "default-valued config" (flag=1, all zero fields).
    /// </summary>
    public void WriteRuntimeConfig(RuntimeConfigRecord config)
    {
        var present = config != null;
        _writer.Write(present);
        if (!present)
        {
            _writer.Flush();
            return;
        }

        _writer.Write(config.BaseTickRate);
        _writer.Write(config.WorkerCount);
        _writer.Write(config.TelemetryRingCapacity);
        _writer.Write(config.ParallelQueryMinChunkSize);
        WriteShortString(config.DefaultPhase ?? string.Empty);

        var phases = config.Phases ?? [];
        _writer.Write((ushort)phases.Length);
        foreach (var p in phases) WriteShortString(p ?? string.Empty);
        _writer.Flush();
    }

    /// <summary>Writes the per-queue static-schema catalog (v7+).</summary>
    public void WriteEventQueueCatalog(ReadOnlySpan<EventQueueRecord> queues)
    {
        _writer.Write((ushort)queues.Length);
        foreach (var q in queues)
        {
            _writer.Write(q.QueueIndex);
            WriteShortString(q.Name ?? string.Empty);
            _writer.Write(q.Capacity);
            WriteShortString(q.EventTypeName ?? string.Empty);
        }
        _writer.Flush();
    }

    /// <summary>
    /// Writes the resource-graph snapshot (v7+) — a pre-order tree walk; readers reconstruct the tree via
    /// <see cref="ResourceGraphNodeRecord.ParentId"/> (-1 for root).
    /// </summary>
    public void WriteResourceGraphSnapshot(ReadOnlySpan<ResourceGraphNodeRecord> nodes)
    {
        _writer.Write(nodes.Length);
        foreach (var n in nodes)
        {
            _writer.Write(n.Id);
            WriteShortString(n.Name ?? string.Empty);
            _writer.Write(n.Type);
            _writer.Write(n.ParentId);
            _writer.Write(n.CreatedAtUtcTicks);
            _writer.Write(n.ExhaustionPolicy);
        }
        _writer.Flush();
    }

    /// <summary>
    /// Convenience helper for fixtures + tests that don't care about populating the v7 static-structure tables — writes empty
    /// versions of all six (component definitions, archetype definitions, index catalog, runtime config, event queue catalog,
    /// resource graph snapshot). Production code should call the individual <c>Write...</c> methods with real data; this helper
    /// exists so test scaffolding stays readable.
    /// </summary>
    public void WriteEmptyStaticStructures()
    {
        WriteComponentDefinitions(ReadOnlySpan<ComponentDefinitionRecord>.Empty);
        WriteArchetypeDefinitions(ReadOnlySpan<ArchetypeDefinitionRecord>.Empty);
        WriteIndexCatalog(ReadOnlySpan<IndexCatalogEntry>.Empty);
        WriteRuntimeConfig(null);
        WriteEventQueueCatalog(ReadOnlySpan<EventQueueRecord>.Empty);
        WriteResourceGraphSnapshot(ReadOnlySpan<ResourceGraphNodeRecord>.Empty);
    }

    /// <summary>Magic marker for the trailing span-name table (distinguishes it from an event block header).</summary>
    public const uint SpanNameTableMagic = 0x4E_41_50_53; // "SPAN" little-endian

    /// <summary>Writes the span name intern table. Called at shutdown with the runtime-interned NamedSpan names.</summary>
    public void WriteSpanNames(IReadOnlyDictionary<int, string> spanNames)
    {
        _writer.Write(SpanNameTableMagic);
        _writer.Write((ushort)spanNames.Count);
        foreach (var kv in spanNames)
        {
            _writer.Write((ushort)kv.Key);
            WriteShortString(kv.Value);
        }
        _writer.Flush();
    }

    /// <summary>
    /// Writes a batch of raw trace records as one LZ4-compressed block. The caller guarantees <paramref name="records"/> contains exactly
    /// <paramref name="recordCount"/> valid size-prefixed records and is no larger than <see cref="MaxBlockBytes"/>.
    /// </summary>
    public void WriteRecords(ReadOnlySpan<byte> records, int recordCount)
    {
        if (records.IsEmpty)
        {
            return;
        }

        if (records.Length > MaxBlockBytes)
        {
            throw new ArgumentException($"Block byte count {records.Length} exceeds max {MaxBlockBytes}", nameof(records));
        }

        if (_compressedBuffer.Length < LZ4Codec.MaximumOutputSize(records.Length))
        {
            _compressedBuffer = new byte[LZ4Codec.MaximumOutputSize(records.Length)];
        }

        Span<byte> blockHeader = stackalloc byte[TraceBlockEncoder.BlockHeaderSize];
        var compressedSize = TraceBlockEncoder.EncodeBlock(records, recordCount, _compressedBuffer, blockHeader);

        _stream.Write(blockHeader);
        _stream.Write(_compressedBuffer.AsSpan(0, compressedSize));
    }

    /// <summary>Magic marker for the trailing FileTable (interned source-file paths). "SFLB" LE.</summary>
    public const uint FileTableMagic = 0x42_4C_46_53;

    /// <summary>Magic marker for the trailing SourceLocationManifest. "SLMN" LE.</summary>
    public const uint SourceLocationManifestMagic = 0x4E_4D_4C_53;

    /// <summary>Magic marker for the trailing QuerySourceStringTable (deduped query source-file paths + method names). "QSST" LE (#342).</summary>
    public const uint QuerySourceStringTableMagic = 0x54_53_53_51;

    /// <summary>Magic marker for the trailing QueryDefinitionTable (materialized definition catalog). "QDFT" LE (#342).</summary>
    public const uint QueryDefinitionTableMagic = 0x54_46_44_51;

    /// <summary>Magic marker for the trailing CpuSampleSection (interned CPU stack samples). "CPUS" LE (#351).</summary>
    public const uint CpuSampleSectionMagic = 0x53_55_50_43;

    /// <summary>
    /// Append the source-location manifest to the file. Returns the (fileTableOffset, manifestOffset)
    /// that the caller MUST patch into the file header via <see cref="RewriteHeader"/>. Phase 3 of the
    /// profiler-source-attribution feature (see claude/design/Profiler/10-profiler-source-attribution.md §4.6).
    /// </summary>
    public (long fileTableOffset, long manifestOffset) WriteSourceLocationManifest(
        IReadOnlyList<string> files,
        IReadOnlyList<SourceLocationManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(entries);

        _writer.Flush();
        var fileTableOffset = _stream.Position;
        _writer.Write(FileTableMagic);
        _writer.Write((uint)files.Count);
        for (ushort i = 0; i < files.Count; i++)
        {
            _writer.Write(i);
            WriteVarString(files[i]);
        }

        _writer.Flush();
        var manifestOffset = _stream.Position;
        _writer.Write(SourceLocationManifestMagic);
        _writer.Write((uint)entries.Count);
        foreach (var e in entries)
        {
            _writer.Write(e.Id);
            _writer.Write(e.FileId);
            _writer.Write(e.Line);
            _writer.Write(e.Kind);
            WriteShortString(e.Method);
        }
        _writer.Flush();
        return (fileTableOffset, manifestOffset);
    }

    /// <summary>
    /// Append the QuerySourceStringTable trailer section (#342, v9) and return the offset for the header patch. The table is a packed array of length-prefixed
    /// UTF-8 strings indexed by 16-bit ID. Slot 0 is reserved as the "no source" sentinel and always serializes as the empty string. Empty input → no section
    /// written (return 0).
    /// </summary>
    /// <remarks>
    /// Wire layout:
    /// <code>
    /// u32  Magic ("QSST")
    /// u32  Count (including the index-0 sentinel)
    /// for i in 0..Count: u16 length, byte[length] utf8
    /// </code>
    /// </remarks>
    public long WriteQuerySourceStringTable(IReadOnlyList<string> strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        if (strings.Count == 0)
        {
            return 0;
        }

        _writer.Flush();
        var offset = _stream.Position;
        _writer.Write(QuerySourceStringTableMagic);
        _writer.Write((uint)strings.Count);
        for (var i = 0; i < strings.Count; i++)
        {
            WriteVarString(strings[i] ?? string.Empty);
        }
        _writer.Flush();
        return offset;
    }

    /// <summary>
    /// Append the CpuSampleSection trailer (#351, v10) and return the offset for the header patch. CPU samples reference an interned stack table; each stack
    /// references interned frame symbols, whose <c>FileId</c> indexes the same <c>FileTable</c> the source-location manifest uses. Empty input → no section
    /// written (return 0).
    /// </summary>
    /// <remarks>
    /// Wire layout:
    /// <code>
    /// u32  Magic ("CPUS")
    /// u32  SampleCount;     per sample: i64 qpc, u8 threadSlot (0xFF = unslotted), u8 sampleType, u32 stackIndex
    /// u32  StackCount;      per stack:  u16 frameCount, u16 frameId * frameCount
    /// u32  FrameSymbolCount; per symbol: u16 frameId, u16 fileId, u32 line, u8 methodLen, byte[methodLen] utf8
    /// </code>
    /// </remarks>
    public long WriteCpuSampleSection(IReadOnlyList<CpuSampleRecord> samples, IReadOnlyList<ushort[]> stacks, IReadOnlyList<CpuFrameSymbol> frameSymbols)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(stacks);
        ArgumentNullException.ThrowIfNull(frameSymbols);
        if (samples.Count == 0)
        {
            return 0;
        }

        _writer.Flush();
        var offset = _stream.Position;
        _writer.Write(CpuSampleSectionMagic);

        _writer.Write((uint)samples.Count);
        foreach (var s in samples)
        {
            _writer.Write(s.Qpc);
            _writer.Write(s.ThreadSlot < 0 ? (byte)0xFF : (byte)s.ThreadSlot);
            _writer.Write(s.SampleType);
            _writer.Write(s.StackIndex);
        }

        _writer.Write((uint)stacks.Count);
        foreach (var stack in stacks)
        {
            var frames = stack ?? [];
            _writer.Write((ushort)frames.Length);
            foreach (var frameId in frames)
            {
                _writer.Write(frameId);
            }
        }

        _writer.Write((uint)frameSymbols.Count);
        foreach (var f in frameSymbols)
        {
            _writer.Write(f.FrameId);
            _writer.Write(f.FileId);
            _writer.Write(f.Line);
            WriteShortString(f.Method);
        }

        _writer.Flush();
        return offset;
    }

    /// <summary>
    /// Rewrite the file header at offset 0 with updated trailer offsets. Required to seek; throws on non-seekable streams.
    /// Used to record the trailing-section offsets after the manifest is appended.
    /// </summary>
    public void RewriteHeader(in TraceFileHeader header)
    {
        if (!_stream.CanSeek)
        {
            throw new InvalidOperationException("RewriteHeader requires a seekable stream.");
        }
        _writer.Flush();
        var savedPos = _stream.Position;
        _stream.Position = 0;
        var span = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in header, 1));
        _stream.Write(span);
        _stream.Position = savedPos;
    }

    public void Flush() => _stream.Flush();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer.Dispose();
        _stream.Dispose();
    }

    private void WriteShortString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var len = (byte)Math.Min(bytes.Length, 255);
        _writer.Write(len);
        _writer.Write(bytes, 0, len);
    }

    /// <summary>u16 length prefix followed by that many <see cref="WriteShortString"/> entries.</summary>
    private void WriteStringArray(string[] values)
    {
        if (values == null)
        {
            _writer.Write((ushort)0);
            return;
        }
        _writer.Write((ushort)values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            WriteShortString(values[i] ?? string.Empty);
        }
    }

    /// <summary>
    /// Variable-length string with a u16 byte-count prefix. Used for FileTable entries where paths can exceed
    /// 255 bytes (the WriteShortString limit).
    /// </summary>
    private void WriteVarString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var len = (ushort)Math.Min(bytes.Length, ushort.MaxValue);
        _writer.Write(len);
        _writer.Write(bytes, 0, len);
    }
}
