using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// A single packed value inside a <see cref="TraceEventKind.PerTickSnapshot"/> record. Constructed via the typed factory helpers and
/// encoded into the snapshot payload verbatim.
/// </summary>
/// <remarks>
/// The struct carries the value in a 64-bit container regardless of the chosen <see cref="GaugeValueKind"/>; the codec writes only 4 or 8
/// bytes to the wire depending on the kind. Signed values (<see cref="GaugeValueKind.I64Signed"/>) are stored in the container as their raw
/// little-endian i64 bit-pattern.
/// </remarks>
public readonly struct GaugeValue
{
    /// <summary>Which gauge this value belongs to.</summary>
    public GaugeId Id { get; }

    /// <summary>On-wire representation of <see cref="RawValue"/>.</summary>
    public GaugeValueKind Kind { get; }

    /// <summary>Raw 64-bit container. For <see cref="GaugeValueKind.U32Count"/>/<see cref="GaugeValueKind.U32PercentHundredths"/> only the low 32 bits are significant.</summary>
    public ulong RawValue { get; }

    /// <summary>Construct a gauge value from its raw container. Prefer the typed <c>From*</c> factories for correct encoding.</summary>
    /// <param name="id">Gauge identifier.</param>
    /// <param name="kind">On-wire value kind.</param>
    /// <param name="rawValue">Raw 64-bit payload (signed values stored as their i64 bit-pattern).</param>
    public GaugeValue(GaugeId id, GaugeValueKind kind, ulong rawValue)
    {
        Id = id;
        Kind = kind;
        RawValue = rawValue;
    }

    /// <summary>On-wire payload size (4 or 8 bytes depending on <see cref="Kind"/>), excluding the 2-byte id and 1-byte kind tag.</summary>
    public int PayloadSize => Kind switch
    {
        GaugeValueKind.U32Count => 4,
        GaugeValueKind.U32PercentHundredths => 4,
        GaugeValueKind.U64Bytes => 8,
        GaugeValueKind.I64Signed => 8,
        _ => throw new InvalidOperationException($"Unknown gauge value kind {Kind}"),
    };

    /// <summary>Total on-wire footprint for this field: 2 (id) + 1 (kind) + payload.</summary>
    public int WireSize => GaugeFieldPrefixSize + PayloadSize;

    /// <summary>Size of the (id, kind) tag preceding every field payload on the wire: 2 + 1 = 3 bytes.</summary>
    public const int GaugeFieldPrefixSize = 3;

    /// <summary>Build a <see cref="GaugeValueKind.U32Count"/> value.</summary>
    public static GaugeValue FromU32(GaugeId id, uint value) => new(id, GaugeValueKind.U32Count, value);

    /// <summary>Build a <see cref="GaugeValueKind.U64Bytes"/> value.</summary>
    public static GaugeValue FromU64(GaugeId id, ulong value) => new(id, GaugeValueKind.U64Bytes, value);

    /// <summary>Build a <see cref="GaugeValueKind.I64Signed"/> value (stored as its i64 bit-pattern).</summary>
    public static GaugeValue FromI64(GaugeId id, long value) => new(id, GaugeValueKind.I64Signed, unchecked((ulong)value));

    /// <summary>Build a <see cref="GaugeValueKind.U32PercentHundredths"/> value (e.g. 5025 = 50.25%).</summary>
    public static GaugeValue FromPercentHundredths(GaugeId id, uint valueHundredths) => new(id, GaugeValueKind.U32PercentHundredths, valueHundredths);
}

/// <summary>
/// Decoded form of a <see cref="TraceEventKind.PerTickSnapshot"/> record.
/// </summary>
public readonly struct PerTickSnapshotData
{
    /// <summary>Typhon thread slot the snapshot was emitted on.</summary>
    public byte ThreadSlot { get; }

    /// <summary>Emit timestamp, in Stopwatch ticks.</summary>
    public long Timestamp { get; }

    /// <summary>Tick number the snapshot was taken at.</summary>
    public uint TickNumber { get; }

    /// <summary>Reserved flags word; writers currently emit 0.</summary>
    public uint Flags { get; }

    /// <summary>Decoded gauge values carried by this snapshot, in wire order.</summary>
    public GaugeValue[] Values { get; }

    /// <summary>Construct a decoded per-tick snapshot.</summary>
    /// <param name="threadSlot">Typhon thread slot.</param>
    /// <param name="timestamp">Emit timestamp, in Stopwatch ticks.</param>
    /// <param name="tickNumber">Tick number.</param>
    /// <param name="flags">Reserved flags word.</param>
    /// <param name="values">Decoded gauge values.</param>
    public PerTickSnapshotData(byte threadSlot, long timestamp, uint tickNumber, uint flags, GaugeValue[] values)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        TickNumber = tickNumber;
        Flags = flags;
        Values = values;
    }
}

/// <summary>
/// Wire-format codec for the <see cref="TraceEventKind.PerTickSnapshot"/> record — packed bundle of gauge values emitted at tick boundary.
/// </summary>
/// <remarks>
/// <para>
/// Layout after the 12-byte common header:
/// <code>
/// offset 12..15  u32  TickNumber
/// offset 16..17  u16  FieldCount
/// offset 18..21  u32  Flags            // reserved; writers emit 0
/// offset 22+     repeated {u16 gaugeId; u8 valueKind; [4 or 8 B] value}
/// </code>
/// </para>
/// <para>
/// Records are variable-size. Callers pre-compute required bytes via <see cref="ComputeSize"/> before claiming ring space, then hand the
/// exact-sized span to <see cref="WritePerTickSnapshot"/>. A ref-struct incremental builder will be added in Phase 1 for the in-ring
/// emit path; this codec is phase-0 infrastructure for scratch-buffer round-trip tests and the decoder.
/// </para>
/// </remarks>
public static class PerTickSnapshotEventCodec
{
    /// <summary>Fixed prefix size: 12 B common header + 4 B tickNumber + 2 B fieldCount + 4 B flags = 22 B.</summary>
    public const int PrefixSize = TraceRecordHeader.CommonHeaderSize + 4 + 2 + 4;

    /// <summary>Compute the exact on-wire size of a snapshot carrying the given gauge values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(ReadOnlySpan<GaugeValue> values)
    {
        var size = PrefixSize;
        for (var i = 0; i < values.Length; i++)
        {
            size += values[i].WireSize;
        }
        return size;
    }

    /// <summary>
    /// Encode a <see cref="TraceEventKind.PerTickSnapshot"/> record into <paramref name="destination"/>. The span must be at least
    /// <see cref="ComputeSize"/> bytes long. <paramref name="values"/> length must fit in a <c>u16</c> (65,535 fields — far beyond any
    /// realistic snapshot).
    /// </summary>
    public static void WritePerTickSnapshot(Span<byte> destination, byte threadSlot, long timestamp, uint tickNumber, uint flags, 
        ReadOnlySpan<GaugeValue> values, out int bytesWritten)
    {
        var total = ComputeSize(values);
        if (total > ushort.MaxValue)
        {
            throw new ArgumentException($"Snapshot size {total} exceeds max record size {ushort.MaxValue}", nameof(values));
        }
        if (values.Length > ushort.MaxValue)
        {
            throw new ArgumentException($"Snapshot carries {values.Length} fields, max is {ushort.MaxValue}", nameof(values));
        }

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)total, TraceEventKind.PerTickSnapshot, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, tickNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], (ushort)values.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(p[6..], flags);

        var offset = 10; // relative to start of 'p' (after prefix tail)
        for (var i = 0; i < values.Length; i++)
        {
            ref readonly var v = ref values[i];
            BinaryPrimitives.WriteUInt16LittleEndian(p[offset..], (ushort)v.Id);
            p[offset + 2] = (byte)v.Kind;
            offset += GaugeValue.GaugeFieldPrefixSize;

            switch (v.Kind)
            {
                case GaugeValueKind.U32Count:
                case GaugeValueKind.U32PercentHundredths:
                    BinaryPrimitives.WriteUInt32LittleEndian(p[offset..], (uint)v.RawValue);
                    offset += 4;
                    break;

                case GaugeValueKind.U64Bytes:
                    BinaryPrimitives.WriteUInt64LittleEndian(p[offset..], v.RawValue);
                    offset += 8;
                    break;

                case GaugeValueKind.I64Signed:
                    BinaryPrimitives.WriteInt64LittleEndian(p[offset..], unchecked((long)v.RawValue));
                    offset += 8;
                    break;

                default:
                    throw new InvalidOperationException($"Unknown gauge value kind {v.Kind}");
            }
        }

        bytesWritten = total;
    }

    /// <summary>Decode a <see cref="TraceEventKind.PerTickSnapshot"/> record from <paramref name="source"/>.</summary>
    public static PerTickSnapshotData DecodePerTickSnapshot(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out var size, out var kind, out var threadSlot, out var timestamp);
        if (kind != TraceEventKind.PerTickSnapshot)
        {
            throw new ArgumentException($"Expected PerTickSnapshot, got {kind}", nameof(source));
        }
        if (size > source.Length)
        {
            throw new ArgumentException($"Record size {size} exceeds source buffer {source.Length}", nameof(source));
        }

        var p = source[TraceRecordHeader.CommonHeaderSize..size];
        var tickNumber = BinaryPrimitives.ReadUInt32LittleEndian(p);
        var fieldCount = BinaryPrimitives.ReadUInt16LittleEndian(p[4..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(p[6..]);

        var values = new GaugeValue[fieldCount];
        var offset = 10;
        for (var i = 0; i < fieldCount; i++)
        {
            var id = (GaugeId)BinaryPrimitives.ReadUInt16LittleEndian(p[offset..]);
            var valueKind = (GaugeValueKind)p[offset + 2];
            offset += GaugeValue.GaugeFieldPrefixSize;

            ulong raw;
            switch (valueKind)
            {
                case GaugeValueKind.U32Count:
                case GaugeValueKind.U32PercentHundredths:
                    raw = BinaryPrimitives.ReadUInt32LittleEndian(p[offset..]);
                    offset += 4;
                    break;

                case GaugeValueKind.U64Bytes:
                    raw = BinaryPrimitives.ReadUInt64LittleEndian(p[offset..]);
                    offset += 8;
                    break;

                case GaugeValueKind.I64Signed:
                    raw = unchecked((ulong)BinaryPrimitives.ReadInt64LittleEndian(p[offset..]));
                    offset += 8;
                    break;

                default:
                    throw new InvalidOperationException($"Unknown gauge value kind {valueKind} at field {i}");
            }
            values[i] = new GaugeValue(id, valueKind, raw);
        }

        return new PerTickSnapshotData(threadSlot, timestamp, tickNumber, flags, values);
    }
}
