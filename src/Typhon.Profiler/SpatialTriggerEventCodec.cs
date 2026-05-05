using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Trigger Region instant. Payload: <c>op u8 (0=create, 1=destroy), regionId u16, categoryMask u32</c> (7 B).</summary>
[PublicAPI]
public readonly struct SpatialTriggerRegionData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte Op { get; }
    public ushort RegionId { get; }
    public uint CategoryMask { get; }

    public SpatialTriggerRegionData(byte threadSlot, long timestamp, byte op, ushort regionId, uint categoryMask)
    { ThreadSlot = threadSlot; Timestamp = timestamp; Op = op; RegionId = regionId; CategoryMask = categoryMask; }
}

/// <summary>Decoded Trigger Eval span. Payload: <c>regionId u16, occupantCount u16, enterCount u16, leaveCount u16</c> (8 B).</summary>
[PublicAPI]
public readonly struct SpatialTriggerEvalData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort RegionId { get; }
    public ushort OccupantCount { get; }
    public ushort EnterCount { get; }
    public ushort LeaveCount { get; }

    public SpatialTriggerEvalData(byte threadSlot, long startTimestamp, long durationTicks, ushort regionId, ushort occupantCount, ushort enterCount, ushort leaveCount)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; RegionId = regionId; OccupantCount = occupantCount; EnterCount = enterCount; LeaveCount = leaveCount; }
}

/// <summary>Decoded Trigger Occupant Diff instant (stats only — no bitmap). Payload: <c>regionId u16, prevCount u16, currCount u16, enterCount u16, leaveCount u16</c> (10 B).</summary>
[PublicAPI]
public readonly struct SpatialTriggerOccupantDiffData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort RegionId { get; }
    public ushort PrevCount { get; }
    public ushort CurrCount { get; }
    public ushort EnterCount { get; }
    public ushort LeaveCount { get; }

    public SpatialTriggerOccupantDiffData(byte threadSlot, long timestamp, ushort regionId, ushort prevCount, ushort currCount, ushort enterCount, ushort leaveCount)
    { ThreadSlot = threadSlot; Timestamp = timestamp; RegionId = regionId; PrevCount = prevCount; CurrCount = currCount; EnterCount = enterCount; LeaveCount = leaveCount; }
}

/// <summary>Decoded Trigger Cache Invalidate instant. Payload: <c>regionId u16, oldVersion i32, newVersion i32</c> (10 B).</summary>
[PublicAPI]
public readonly struct SpatialTriggerCacheInvalidateData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort RegionId { get; }
    public int OldVersion { get; }
    public int NewVersion { get; }

    public SpatialTriggerCacheInvalidateData(byte threadSlot, long timestamp, ushort regionId, int oldVersion, int newVersion)
    { ThreadSlot = threadSlot; Timestamp = timestamp; RegionId = regionId; OldVersion = oldVersion; NewVersion = newVersion; }
}

/// <summary>
/// Wire codec for Spatial Trigger events: Region/OccupantDiff/CacheInvalidate instants (kinds 142, 144-145) + Eval span (kind 143).
/// </summary>
public static class SpatialTriggerEventCodec
{
    public const int RegionSize = TraceRecordHeader.CommonHeaderSize + 1 + 2 + 4;            // 19
    private const int EvalPayload = 2 + 2 + 2 + 2;                                           // 8
    public const int OccupantDiffSize = TraceRecordHeader.CommonHeaderSize + 2 + 2 + 2 + 2 + 2;  // 22
    public const int CacheInvalidateSize = TraceRecordHeader.CommonHeaderSize + 2 + 4 + 4;   // 22

    public static int ComputeSizeEval(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + EvalPayload;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRegion(Span<byte> destination, byte threadSlot, long timestamp, byte op, ushort regionId, uint categoryMask)
    {
        TraceRecordHeader.WriteCommonHeader(destination, RegionSize, TraceEventKind.SpatialTriggerRegion, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = op;
        BinaryPrimitives.WriteUInt16LittleEndian(p[1..], regionId);
        BinaryPrimitives.WriteUInt32LittleEndian(p[3..], categoryMask);
    }

    public static SpatialTriggerRegionData DecodeRegion(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialTriggerRegionData(threadSlot, timestamp, p[0],
            BinaryPrimitives.ReadUInt16LittleEndian(p[1..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[3..]));
    }

    public static SpatialTriggerEvalData DecodeEval(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new SpatialTriggerEvalData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[6..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteOccupantDiff(Span<byte> destination, byte threadSlot, long timestamp, ushort regionId, ushort prevCount, ushort currCount, ushort enterCount, ushort leaveCount)
    {
        TraceRecordHeader.WriteCommonHeader(destination, OccupantDiffSize, TraceEventKind.SpatialTriggerOccupantDiff, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, regionId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], prevCount);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], currCount);
        BinaryPrimitives.WriteUInt16LittleEndian(p[6..], enterCount);
        BinaryPrimitives.WriteUInt16LittleEndian(p[8..], leaveCount);
    }

    public static SpatialTriggerOccupantDiffData DecodeOccupantDiff(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialTriggerOccupantDiffData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[8..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteCacheInvalidate(Span<byte> destination, byte threadSlot, long timestamp, ushort regionId, int oldVersion, int newVersion)
    {
        TraceRecordHeader.WriteCommonHeader(destination, CacheInvalidateSize, TraceEventKind.SpatialTriggerCacheInvalidate, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, regionId);
        BinaryPrimitives.WriteInt32LittleEndian(p[2..], oldVersion);
        BinaryPrimitives.WriteInt32LittleEndian(p[6..], newVersion);
    }

    public static SpatialTriggerCacheInvalidateData DecodeCacheInvalidate(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialTriggerCacheInvalidateData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[2..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[6..]));
    }
}
