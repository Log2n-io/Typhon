using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded TierIndex Rebuild span. Payload: <c>archetypeId u16, clusterCount i32, oldVersion i32, newVersion i32</c> (14 B).</summary>
[PublicAPI]
public readonly struct SpatialTierIndexRebuildData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort ArchetypeId { get; }
    public int ClusterCount { get; }
    public int OldVersion { get; }
    public int NewVersion { get; }

    public SpatialTierIndexRebuildData(byte threadSlot, long startTimestamp, long durationTicks, ushort archetypeId, int clusterCount, int oldVersion, int newVersion)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; ArchetypeId = archetypeId; ClusterCount = clusterCount; OldVersion = oldVersion; NewVersion = newVersion; }
}

/// <summary>Decoded TierIndex VersionSkip instant. Payload: <c>archetypeId u16, version i32, reason u8</c> (7 B).</summary>
[PublicAPI]
public readonly struct SpatialTierIndexVersionSkipData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort ArchetypeId { get; }
    public int Version { get; }
    public byte Reason { get; }

    public SpatialTierIndexVersionSkipData(byte threadSlot, long timestamp, ushort archetypeId, int version, byte reason)
    { ThreadSlot = threadSlot; Timestamp = timestamp; ArchetypeId = archetypeId; Version = version; Reason = reason; }
}

/// <summary>Wire codec for Spatial TierIndex events: Rebuild span (kind 136) + VersionSkip instant (kind 137).</summary>
public static class SpatialTierIndexEventCodec
{
    private const int RebuildPayload = 2 + 4 + 4 + 4;   // 14
    public const int VersionSkipSize = TraceRecordHeader.CommonHeaderSize + 2 + 4 + 1;  // 19

    public static int ComputeSizeRebuild(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + RebuildPayload;

    public static SpatialTierIndexRebuildData DecodeRebuild(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new SpatialTierIndexRebuildData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadInt32LittleEndian(payload[2..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[6..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[10..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteVersionSkip(Span<byte> destination, byte threadSlot, long timestamp, ushort archetypeId, int version, byte reason)
    {
        TraceRecordHeader.WriteCommonHeader(destination, VersionSkipSize, TraceEventKind.SpatialTierIndexVersionSkip, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, archetypeId);
        BinaryPrimitives.WriteInt32LittleEndian(p[2..], version);
        p[6] = reason;
    }

    public static SpatialTierIndexVersionSkipData DecodeVersionSkip(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SpatialTierIndexVersionSkipData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[2..]),
            p[6]);
    }
}
