using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Typhon.Profiler;

namespace Typhon.Workbench.Fixtures;

/// <summary>
/// Builds individual profiler wire records byte-for-byte, so tests can compose an arbitrary engine record stream without
/// running an engine. Every layout here mirrors what <see cref="Typhon.Profiler.IncrementalCacheBuilder"/> parses — the
/// builder is the consumer under test, so a record that it would reject is a record this factory must not produce.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why hand-assembled.</b> The production encoders live behind <c>internal</c> producer ref structs in
/// <c>Typhon.Engine</c> that require a claimed <c>ThreadSlot</c> and a live ring buffer. A fixture cannot reach them, and
/// standing up a real engine to emit six records would make every filter test an integration test. The layouts are
/// small, fixed, and asserted against the builder's own parse offsets by <c>MockRecordFactoryTests</c>.
/// </para>
/// <para>
/// <b>Layouts.</b> Common header (12 B) on every record: <c>u16 size</c>, <c>u8 kind</c>, <c>u8 threadSlot</c>,
/// <c>i64 startTimestamp</c>. Span records append a 25 B extension: <c>i64 durationTicks</c>, <c>u64 spanId</c>,
/// <c>u64 parentSpanId</c>, <c>u8 spanFlags</c> — payload then starts at offset 37 when the trace-context bit is clear.
/// </para>
/// </remarks>
public static class MockRecordFactory
{
    /// <summary>Common record header size — mirrors <c>TraceRecordHeader.CommonHeaderSize</c>.</summary>
    public const int CommonHeaderSize = 12;

    /// <summary>Span header extension size — mirrors <c>TraceRecordHeader.SpanHeaderExtensionSize</c>.</summary>
    public const int SpanHeaderExtSize = 25;

    /// <summary>Offset at which a span record's payload begins when it carries no trace context.</summary>
    public const int SpanPayloadOffset = CommonHeaderSize + SpanHeaderExtSize;

    /// <summary>
    /// The kinds <c>AttachSessionRuntime</c> must never filter, whatever the arm state. Mirrors the exempt set in
    /// <c>claude/design/Profiler/12-on-demand-tick-capture.md</c> §4.1 — kept here so tests can assert against a single
    /// declaration rather than a literal list repeated per test.
    /// </summary>
    public static readonly TraceEventKind[] ExemptKinds =
    [
        TraceEventKind.TickStart,
        TraceEventKind.TickEnd,
        TraceEventKind.PerTickSnapshot,
        TraceEventKind.ThreadInfo,
        TraceEventKind.SchedulerMetronomeWait,
        TraceEventKind.SchedulerOverloadDetector,
    ];

    /// <summary><see cref="TraceEventKind.TickStart"/> — 12 B, header only, no payload.</summary>
    public static byte[] TickStart(long timestamp, byte threadSlot = 0)
    {
        var record = new byte[CommonHeaderSize];
        WriteCommonHeader(record, (ushort)record.Length, TraceEventKind.TickStart, threadSlot, timestamp);
        return record;
    }

    /// <summary><see cref="TraceEventKind.TickEnd"/> — 14 B: header + <c>u8 overloadLevel</c> + <c>u8 tickMultiplier</c>.</summary>
    public static byte[] TickEnd(long timestamp, byte overloadLevel = 0, byte tickMultiplier = 1, byte threadSlot = 0)
    {
        var record = new byte[CommonHeaderSize + 2];
        WriteCommonHeader(record, (ushort)record.Length, TraceEventKind.TickEnd, threadSlot, timestamp);
        record[CommonHeaderSize] = overloadLevel;
        record[CommonHeaderSize + 1] = tickMultiplier;
        return record;
    }

    /// <summary>
    /// <see cref="TraceEventKind.SchedulerMetronomeWait"/> (241) — a 48 B <b>span</b>. Payload after the span extension:
    /// <c>i64 scheduledTs</c>, <c>u8 multiplier</c>, <c>u8 intentClass</c>, <c>u8 phaseFlags</c>.
    /// </summary>
    /// <remarks>
    /// Load-bearing for duration parity: the builder lets this record's <c>startTimestamp</c> push
    /// <c>_currentTickLastTs</c> forward before sealing the tick, so a tick finalized without it reports a shorter
    /// duration than one finalized with it. See design §4.1.
    /// </remarks>
    public static byte[] MetronomeWait(long startTimestamp, long durationTicks, byte tickMultiplier = 1, byte intentClass = 0, byte threadSlot = 0)
    {
        var record = new byte[SpanPayloadOffset + 11];
        WriteSpanHeader(record, (ushort)record.Length, TraceEventKind.SchedulerMetronomeWait, threadSlot, startTimestamp, durationTicks);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(SpanPayloadOffset), startTimestamp);
        record[SpanPayloadOffset + 8] = tickMultiplier;
        record[SpanPayloadOffset + 9] = intentClass;
        record[SpanPayloadOffset + 10] = 0;
        return record;
    }

    /// <summary>
    /// <see cref="TraceEventKind.SchedulerOverloadDetector"/> (242) — a 36 B instant. Payload: <c>i64 tick</c>,
    /// <c>f32 overrunRatio</c>, <c>u16 consecutiveOverrun</c>, <c>u16 consecutiveUnderrun</c>,
    /// <c>u16 consecutiveQueueGrowth</c>, <c>i32 queueDepth</c>, <c>u8 level</c>, <c>u8 multiplier</c>.
    /// </summary>
    public static byte[] OverloadDetector(long timestamp, long tick, ushort consecutiveOverrun = 0, ushort consecutiveUnderrun = 0,
        byte level = 0, byte tickMultiplier = 1, byte threadSlot = 0)
    {
        var record = new byte[CommonHeaderSize + 24];
        WriteCommonHeader(record, (ushort)record.Length, TraceEventKind.SchedulerOverloadDetector, threadSlot, timestamp);
        var p = record.AsSpan(CommonHeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(p, tick);
        BinaryPrimitives.WriteSingleLittleEndian(p[8..], 0f);
        BinaryPrimitives.WriteUInt16LittleEndian(p[12..], consecutiveOverrun);
        BinaryPrimitives.WriteUInt16LittleEndian(p[14..], consecutiveUnderrun);
        BinaryPrimitives.WriteUInt16LittleEndian(p[16..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(p[18..], 0);
        p[22] = level;
        p[23] = tickMultiplier;
        return record;
    }

    /// <summary>
    /// <see cref="TraceEventKind.PerTickSnapshot"/> (76) — the gauge bundle. Delegates to the production
    /// <see cref="PerTickSnapshotEventCodec"/> so the layout can never drift from the real one.
    /// </summary>
    /// <remarks>
    /// <paramref name="engineTickNumber"/> is the record's embedded <b>absolute</b> tick number, the only absolute tick
    /// number anywhere on the live wire. Tests use it both to drive the numbering offset and to detect a dropped
    /// <c>TickStart</c>.
    /// </remarks>
    public static byte[] PerTickSnapshot(long timestamp, uint engineTickNumber, byte threadSlot = 0, IReadOnlyList<GaugeValue> gauges = null)
    {
        var values = gauges != null ? [.. gauges] : DefaultGauges();
        var record = new byte[PerTickSnapshotEventCodec.ComputeSize(values)];
        PerTickSnapshotEventCodec.WritePerTickSnapshot(record, threadSlot, timestamp, engineTickNumber, flags: 0, values, out _);
        return record;
    }

    /// <summary><see cref="TraceEventKind.ThreadInfo"/> (77) — slot to name mapping. Uses the production codec.</summary>
    public static byte[] ThreadInfo(long timestamp, byte threadSlot, int managedThreadId, string name, ThreadKind threadKind = ThreadKind.Worker)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name ?? string.Empty);
        var record = new byte[ThreadInfoEventCodec.ComputeSize(nameBytes.Length)];
        ThreadInfoEventCodec.WriteThreadInfo(record, threadSlot, timestamp, managedThreadId, nameBytes, threadKind, out _);
        return record;
    }

    /// <summary>
    /// <see cref="TraceEventKind.SchedulerChunk"/> — a 47 B span, the highest-frequency detail record the profiler
    /// emits and therefore the canonical "should be filtered" record in these tests. Payload after the span extension:
    /// <c>u16 systemIndex</c>, <c>u16 chunkIndex</c>, <c>u16 totalChunks</c>, <c>i32 entitiesProcessed</c>.
    /// </summary>
    public static byte[] SchedulerChunk(long startTimestamp, long durationTicks, ushort systemIndex = 0, ushort chunkIndex = 0,
        ushort totalChunks = 1, int entitiesProcessed = 1, byte threadSlot = 0)
    {
        var record = new byte[SpanPayloadOffset + 10];
        WriteSpanHeader(record, (ushort)record.Length, TraceEventKind.SchedulerChunk, threadSlot, startTimestamp, durationTicks);
        var p = record.AsSpan(SpanPayloadOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(p, systemIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], chunkIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], totalChunks);
        BinaryPrimitives.WriteInt32LittleEndian(p[6..], entitiesProcessed);
        return record;
    }

    /// <summary>
    /// A generic span record of an arbitrary kind with an opaque zero payload. Lets a test assert that filtering is
    /// driven by the exempt set rather than by any property of a specific detail kind.
    /// </summary>
    public static byte[] GenericSpan(TraceEventKind kind, long startTimestamp, long durationTicks, int payloadBytes = 4, byte threadSlot = 0)
    {
        if (payloadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadBytes), "payload size must be non-negative");
        }
        var record = new byte[SpanPayloadOffset + payloadBytes];
        WriteSpanHeader(record, (ushort)record.Length, kind, threadSlot, startTimestamp, durationTicks);
        return record;
    }

    /// <summary>Concatenate record byte arrays into one back-to-back buffer, the shape a Block frame carries.</summary>
    public static byte[] Concat(params byte[][] records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var total = 0;
        foreach (var r in records)
        {
            total += r.Length;
        }
        var result = new byte[total];
        var pos = 0;
        foreach (var r in records)
        {
            r.CopyTo(result, pos);
            pos += r.Length;
        }
        return result;
    }

    /// <summary>Count size-prefixed records in a packed buffer. Mirrors the builder's walk, including its bail-out rules.</summary>
    public static int CountRecords(ReadOnlySpan<byte> records)
    {
        var count = 0;
        var pos = 0;
        while (pos + CommonHeaderSize <= records.Length)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(records[pos..]);
            if (size < CommonHeaderSize || pos + size > records.Length)
            {
                break;
            }
            count++;
            pos += size;
        }
        return count;
    }

    /// <summary>Enumerate the kinds present in a packed record buffer, in wire order.</summary>
    public static List<TraceEventKind> KindsIn(ReadOnlySpan<byte> records)
    {
        var kinds = new List<TraceEventKind>();
        var pos = 0;
        while (pos + CommonHeaderSize <= records.Length)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(records[pos..]);
            if (size < CommonHeaderSize || pos + size > records.Length)
            {
                break;
            }
            kinds.Add((TraceEventKind)records[pos + 2]);
            pos += size;
        }
        return kinds;
    }

    private static GaugeValue[] DefaultGauges() =>
    [
        GaugeValue.FromU64(GaugeId.MemoryUnmanagedTotalBytes, 1024),
        GaugeValue.FromU32(GaugeId.TxChainActiveCount, 2),
    ];

    private static void WriteCommonHeader(Span<byte> destination, ushort size, TraceEventKind kind, byte threadSlot, long timestamp)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, size);
        destination[2] = (byte)kind;
        destination[3] = threadSlot;
        BinaryPrimitives.WriteInt64LittleEndian(destination[4..], timestamp);
    }

    private static void WriteSpanHeader(Span<byte> destination, ushort size, TraceEventKind kind, byte threadSlot, long startTimestamp, long durationTicks)
    {
        WriteCommonHeader(destination, size, kind, threadSlot, startTimestamp);
        BinaryPrimitives.WriteInt64LittleEndian(destination[12..], durationTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[20..], 0);   // spanId — unused by the builder paths under test
        BinaryPrimitives.WriteUInt64LittleEndian(destination[28..], 0);   // parentSpanId
        destination[36] = 0;                                              // spanFlags: trace-context bit clear
    }
}
