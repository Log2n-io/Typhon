using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Wire codec for <see cref="TraceEventKind.QueueTickEnd"/> (kind 244) — per-(tick, queue) rollup emitted at end-of-tick.
/// Surfaces queue-depth telemetry (peak, end-of-tick, overflow, produced, consumed) for the Workbench Data API <c>queue/&lt;name&gt;</c> tracks (#311).
/// </summary>
/// <remarks>
/// Folded by <see cref="IncrementalCacheBuilder"/> into the v12 <see cref="CacheSectionId.QueueTickSummaries"/> cache section. One emission per active queue
/// per tick — small, fixed-size record (no payload variability), so we hand-code the codec and skip the source-generated path.
/// </remarks>
public static class QueueTickEndCodec
{
    /// <summary>Total record size in bytes (common header + payload).</summary>
    public const int Size = TraceRecordHeader.CommonHeaderSize + 4 + 2 + 2 + 4 + 4 + 4 + 4 + 4; // 12 + 28 = 40

    /// <summary>
    /// Encode a <see cref="TraceEventKind.QueueTickEnd"/> instant record. Caller is responsible for reserving exactly
    /// <see cref="Size"/> bytes from the producer ring.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> destination, byte threadSlot, long timestamp, uint tickNumber, ushort queueId, uint peakDepth, uint endOfTickDepth, 
        uint overflowCount, uint produced, uint consumed)
    {
        TraceRecordHeader.WriteCommonHeader(destination, Size, TraceEventKind.QueueTickEnd, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt32LittleEndian(p, tickNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], queueId);
        // Padding (2 bytes) to align the following u32s on a 4-byte boundary; readers ignore.
        BinaryPrimitives.WriteUInt16LittleEndian(p[6..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], peakDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(p[12..], endOfTickDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(p[16..], overflowCount);
        BinaryPrimitives.WriteUInt32LittleEndian(p[20..], produced);
        BinaryPrimitives.WriteUInt32LittleEndian(p[24..], consumed);
    }

    /// <summary>Decode a <see cref="TraceEventKind.QueueTickEnd"/> record back into structured form.</summary>
    public static QueueTickEndData Read(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new QueueTickEndData(
            threadSlot,
            timestamp,
            BinaryPrimitives.ReadUInt32LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[24..]));
    }
}

/// <summary>Decoded form of a <see cref="TraceEventKind.QueueTickEnd"/> record.</summary>
public readonly record struct QueueTickEndData(
    byte ThreadSlot,
    long Timestamp,
    uint TickNumber,
    ushort QueueId,
    uint PeakDepth,
    uint EndOfTickDepth,
    uint OverflowCount,
    uint Produced,
    uint Consumed);
