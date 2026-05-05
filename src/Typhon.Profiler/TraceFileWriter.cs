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

    /// <summary>
    /// Append the source-location manifest to the file. Returns the (fileTableOffset, manifestOffset)
    /// that the caller MUST patch into the file header via <see cref="RewriteHeader"/>. Phase 3 of the
    /// profiler-source-attribution feature (see claude/design/observability/10-profiler-source-attribution.md §4.6).
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
