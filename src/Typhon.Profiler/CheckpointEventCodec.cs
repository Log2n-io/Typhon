using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Decoded form of a checkpoint span event (Cycle, Collect, Write, Fsync, Transition, Recycle). Which optional fields are valid depends on the
/// kind — see the per-event ref structs for the contract.
/// </summary>
public readonly struct CheckpointEventData
{
    public TraceEventKind Kind { get; }
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }

    /// <summary>#302 source-location id (0 = no attribution). Resolved through the trace's manifest to file:line.</summary>
    public ushort SourceLocationId { get; }

    /// <summary>Required for <see cref="TraceEventKind.CheckpointCycle"/> — target WAL LSN the checkpoint is advancing to.</summary>
    public long TargetLsn { get; }

    /// <summary>Required for <see cref="TraceEventKind.CheckpointCycle"/> — what triggered this checkpoint.</summary>
    public CheckpointReason Reason { get; }

    public byte OptionalFieldMask { get; }

    /// <summary>Optional — number of dirty pages collected (Cycle). Valid iff <see cref="HasDirtyPageCount"/>.</summary>
    public int DirtyPageCount { get; }

    /// <summary>Optional — number of pages written (Write). Valid iff <see cref="HasWrittenCount"/>.</summary>
    public int WrittenCount { get; }

    /// <summary>Optional — number of UoW entries transitioned (Transition). Valid iff <see cref="HasTransitionedCount"/>.</summary>
    public int TransitionedCount { get; }

    /// <summary>Optional — number of WAL segments recycled (Recycle). Valid iff <see cref="HasRecycledCount"/>.</summary>
    public int RecycledCount { get; }

    public bool HasDirtyPageCount => (OptionalFieldMask & CheckpointEventCodec.OptDirtyPageCount) != 0;
    public bool HasWrittenCount => (OptionalFieldMask & CheckpointEventCodec.OptWrittenCount) != 0;
    public bool HasTransitionedCount => (OptionalFieldMask & CheckpointEventCodec.OptTransitionedCount) != 0;
    public bool HasRecycledCount => (OptionalFieldMask & CheckpointEventCodec.OptRecycledCount) != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    public CheckpointEventData(
        TraceEventKind kind, byte threadSlot, long startTimestamp, long durationTicks,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId,
        long targetLsn, CheckpointReason reason, byte optionalFieldMask,
        int dirtyPageCount, int writtenCount, int transitionedCount, int recycledCount)
    {
        Kind = kind;
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        SourceLocationId = sourceLocationId;
        TargetLsn = targetLsn;
        Reason = reason;
        OptionalFieldMask = optionalFieldMask;
        DirtyPageCount = dirtyPageCount;
        WrittenCount = writtenCount;
        TransitionedCount = transitionedCount;
        RecycledCount = recycledCount;
    }
}

/// <summary>
/// Shared codec for the six checkpoint span events. Three different wire shapes:
/// <list type="bullet">
///   <item><b>Cycle</b> — required <c>targetLsn</c> + <c>reason</c>, optional <c>dirtyPageCount</c>.</item>
///   <item><b>Write / Transition / Recycle</b> — optional count field only (optMask + i32?).</item>
///   <item><b>Collect / Fsync</b> — no payload (span header only).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Cycle payload (after span header):</b>
/// <code>
/// [i64 targetLsn]       // 8 B — required
/// [u8  reason]           // 1 B — required (CheckpointReason)
/// [u8  optMask]          // 1 B
/// [i32 dirtyPageCount]?  // 4 B — present iff optMask &amp; OptDirtyPageCount
/// </code>
/// </para>
/// <para>
/// <b>Write / Transition / Recycle payload (after span header):</b>
/// <code>
/// [u8  optMask]    // 1 B
/// [i32 count]?     // 4 B — present iff optMask &amp; 0x01
/// </code>
/// </para>
/// <para>
/// <b>Collect / Fsync:</b> no payload — span header only.
/// </para>
/// </remarks>
public static class CheckpointEventCodec
{
    /// <summary>Optional-mask bit 0 — <c>DirtyPageCount</c> (Cycle).</summary>
    public const byte OptDirtyPageCount = 0x01;

    /// <summary>Optional-mask bit 0 — <c>WrittenCount</c> (Write).</summary>
    public const byte OptWrittenCount = 0x01;

    /// <summary>Optional-mask bit 0 — <c>TransitionedCount</c> (Transition).</summary>
    public const byte OptTransitionedCount = 0x01;

    /// <summary>Optional-mask bit 0 — <c>RecycledCount</c> (Recycle).</summary>
    public const byte OptRecycledCount = 0x01;

    private const int TargetLsnSize = 8;
    private const int ReasonSize = 1;
    private const int OptMaskSize = 1;
    private const int CountSize = 4;

    // ── Cycle (required targetLsn + reason, optional dirtyPageCount) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeCycleSize(bool hasTraceContext, byte optMask)
    {
        var size = TraceRecordHeader.SpanHeaderSize(hasTraceContext) + TargetLsnSize + ReasonSize + OptMaskSize;
        if ((optMask & OptDirtyPageCount) != 0)
        {
            size += CountSize;
        }
        return size;
    }

    // ── Write / Transition / Recycle (optional count only) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeOptionalCountSize(bool hasTraceContext, byte optMask)
    {
        var size = TraceRecordHeader.SpanHeaderSize(hasTraceContext) + OptMaskSize;
        if ((optMask & 0x01) != 0)
        {
            size += CountSize;
        }
        return size;
    }

    // ── Collect / Fsync (no payload — span header only) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // ── Decode ──

    /// <summary>
    /// Decode any of the six checkpoint event kinds. The caller can use <see cref="CheckpointEventData.Kind"/> to disambiguate.
    /// </summary>
    public static CheckpointEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ulong traceIdHi = 0, traceIdLo = 0;
        if (hasTraceContext)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);

        long targetLsn = 0;
        var reason = CheckpointReason.Periodic;
        byte optMask = 0;
        int dirtyPageCount = 0;
        int writtenCount = 0;
        int transitionedCount = 0;
        int recycledCount = 0;

        switch (kind)
        {
            case TraceEventKind.CheckpointCycle:
            {
                var payload = source[headerSize..];
                targetLsn = BinaryPrimitives.ReadInt64LittleEndian(payload);
                var cursor = TargetLsnSize;

                reason = (CheckpointReason)payload[cursor];
                cursor += ReasonSize;

                optMask = payload[cursor];
                cursor += OptMaskSize;

                if ((optMask & OptDirtyPageCount) != 0)
                {
                    dirtyPageCount = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
                    cursor += CountSize;
                }
                break;
            }

            case TraceEventKind.CheckpointWrite:
            {
                var payload = source[headerSize..];
                optMask = payload[0];
                var cursor = OptMaskSize;
                if ((optMask & OptWrittenCount) != 0)
                {
                    writtenCount = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
                    cursor += CountSize;
                }
                break;
            }

            case TraceEventKind.CheckpointTransition:
            {
                var payload = source[headerSize..];
                optMask = payload[0];
                var cursor = OptMaskSize;
                if ((optMask & OptTransitionedCount) != 0)
                {
                    transitionedCount = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
                    cursor += CountSize;
                }
                break;
            }

            case TraceEventKind.CheckpointRecycle:
            {
                var payload = source[headerSize..];
                optMask = payload[0];
                var cursor = OptMaskSize;
                if ((optMask & OptRecycledCount) != 0)
                {
                    recycledCount = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
                    cursor += CountSize;
                }
                break;
            }

            // CheckpointCollect, CheckpointFsync — no payload
        }

        return new CheckpointEventData(kind, threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId,
            targetLsn, reason, optMask, dirtyPageCount, writtenCount, transitionedCount, recycledCount);
    }
}

