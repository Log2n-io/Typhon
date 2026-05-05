using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Decoded form of any page-cache span event. Covers six page-cache kinds: <see cref="TraceEventKind.PageCacheFetch"/>,
/// <see cref="TraceEventKind.PageCacheDiskRead"/>, <see cref="TraceEventKind.PageCacheDiskWrite"/>,
/// <see cref="TraceEventKind.PageCacheAllocatePage"/>, <see cref="TraceEventKind.PageCacheFlush"/>, and <see cref="TraceEventKind.PageEvicted"/>
/// (zero-duration marker span). Which of <see cref="FilePageIndex"/> and <see cref="PageCount"/> are set depends on the kind.
/// </summary>
public readonly struct PageCacheEventData
{
    public TraceEventKind Kind { get; }
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }

    /// <summary>Required for Fetch/DiskRead/DiskWrite/AllocatePage; meaningless for Flush.</summary>
    public int FilePageIndex { get; }

    /// <summary>Required for Flush; optional for DiskWrite (contiguous run length); meaningless for Fetch/DiskRead/AllocatePage.</summary>
    public int PageCount { get; }

    public byte OptionalFieldMask { get; }

    /// <summary>Phase 5: dirty-bit for <see cref="TraceEventKind.PageEvicted"/> (1 if the displaced page was dirty, 0 otherwise).</summary>
    public byte DirtyBit { get; }

    /// <summary>Source-location id assigned by <c>SourceLocationGenerator</c> (#302). Zero when the wire record didn't carry source attribution.</summary>
    public ushort SourceLocationId { get; }

    public bool HasPageCount => (OptionalFieldMask & PageCacheEventCodec.OptPageCount) != 0;
    public bool HasDirtyBit => (OptionalFieldMask & PageCacheEventCodec.OptDirtyBit) != 0;
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public bool HasSourceLocation => SourceLocationId != 0;

    public PageCacheEventData(TraceEventKind kind, byte threadSlot, long startTimestamp, long durationTicks,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        int filePageIndex, int pageCount, byte optionalFieldMask, byte dirtyBit = 0, ushort sourceLocationId = 0)
    {
        Kind = kind;
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        FilePageIndex = filePageIndex;
        PageCount = pageCount;
        OptionalFieldMask = optionalFieldMask;
        DirtyBit = dirtyBit;
        SourceLocationId = sourceLocationId;
    }
}

/// <summary>Decoded form of a <see cref="TraceEventKind.PageCacheBackpressure"/> event.</summary>
public readonly struct PageCacheBackpressureEventData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int RetryCount { get; }
    public int DirtyCount { get; }
    public int EpochCount { get; }

    /// <summary>Source-location id assigned by <c>SourceLocationGenerator</c> (#302). Zero when the wire record didn't carry source attribution.</summary>
    public ushort SourceLocationId { get; }

    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public bool HasSourceLocation => SourceLocationId != 0;

    public PageCacheBackpressureEventData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, int retryCount, int dirtyCount, int epochCount, ushort sourceLocationId = 0)
    {
        ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks;
        SpanId = spanId; ParentSpanId = parentSpanId; TraceIdHi = traceIdHi; TraceIdLo = traceIdLo;
        RetryCount = retryCount; DirtyCount = dirtyCount; EpochCount = epochCount;
        SourceLocationId = sourceLocationId;
    }
}

/// <summary>Wire codec for <see cref="TraceEventKind.PageCacheBackpressure"/>. Payload: <c>[i32 retryCount][i32 dirtyCount][i32 epochCount]</c>.</summary>
public static class PageCacheBackpressureCodec
{
    private const int PayloadSize = 12;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + PayloadSize;

    public static PageCacheBackpressureEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ulong traceIdHi = 0, traceIdLo = 0;
        if (hasTraceContext)
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        var payload = source[headerSize..];
        var retryCount = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var dirtyCount = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        var epochCount = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);
        return new PageCacheBackpressureEventData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId,
            traceIdHi, traceIdLo, retryCount, dirtyCount, epochCount, sourceLocationId);
    }
}

/// <summary>
/// Shared wire codec for all five page-cache event kinds. Every kind writes a 4-byte "primary value" slot (<c>FilePageIndex</c> for most,
/// <c>PageCount</c> for Flush) plus a 1-byte <c>optMask</c>, plus optional trailing fields keyed by the mask bits.
/// </summary>
/// <remarks>
/// <b>Phase 5 wire-additive extension:</b> <see cref="OptDirtyBit"/> (0x02) appends one trailing byte for the
/// <see cref="TraceEventKind.PageEvicted"/> dirty flag (1 = displaced page was dirty, 0 = clean). Old decoders see an
/// unknown mask bit and stop reading at the cursor they expect; the record-size header in the common header tells
/// them where the next record begins, so wire compatibility is preserved.
/// </remarks>
public static class PageCacheEventCodec
{
    /// <summary>Optional-mask bit 0 — <c>PageCount</c> on DiskWrite (contiguous run length).</summary>
    public const byte OptPageCount = 0x01;

    /// <summary>Optional-mask bit 1 (Phase 5) — trailing 1-byte <c>dirtyBit</c> on <see cref="TraceEventKind.PageEvicted"/>.</summary>
    public const byte OptDirtyBit = 0x02;

    private const int FilePageIndexSize = 4;
    private const int OptMaskSize = 1;
    private const int PageCountSize = 4;
    private const int DirtyBitSize = 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(TraceEventKind kind, bool hasTraceContext, byte optMask, ushort sourceLocationId = 0)
    {
        var hasSourceLocation = sourceLocationId != 0;
        var size = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation) + FilePageIndexSize + OptMaskSize;
        if ((optMask & OptPageCount) != 0) size += PageCountSize;
        if ((optMask & OptDirtyBit) != 0) size += DirtyBitSize;
        return size;
    }

    // PageCacheEventCodec.Encode is NOT obsolete — the page-cache family escape-hatch (PageCacheFetch, DiskRead,
    // DiskWrite, AllocatePage, Flush) keeps calling this codec because the generator's standard layout doesn't model
    // the always-on optMask byte or the FilePageIndex slot reuse for Flush. EmitPageEvicted in TyphonEvent.cs also
    // calls it. See PageCacheFlushEvent's <remarks> for the full escape-hatch rationale.
    //
    // Source attribution (#302): when sourceLocationId != 0, the SpanFlagsHasSourceLocation bit is set and 2 extra
    // bytes are written after the optional trace context, before the kind payload. EmitPageEvicted always passes
    // siteId = 0 (private internal emitter — never targeted by the SourceLocationGenerator interceptor).
    internal static void Encode(Span<byte> destination, long endTimestamp, TraceEventKind kind, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        int filePageIndex, int pageCount, byte optMask, out int bytesWritten, byte dirtyBit = 0, ushort sourceLocationId = 0)
    {
        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var hasSourceLocation = sourceLocationId != 0;
        var size = ComputeSize(kind, hasTraceContext, optMask, sourceLocationId);

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, kind, threadSlot, startTimestamp);
        var spanFlags = (byte)0;
        if (hasTraceContext) spanFlags |= TraceRecordHeader.SpanFlagsHasTraceContext;
        if (hasSourceLocation) spanFlags |= TraceRecordHeader.SpanFlagsHasSourceLocation;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);

        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        if (hasSourceLocation)
        {
            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        var payload = destination[headerSize..];
        BinaryPrimitives.WriteInt32LittleEndian(payload, filePageIndex);
        payload[FilePageIndexSize] = optMask;
        var cursor = FilePageIndexSize + OptMaskSize;

        if ((optMask & OptPageCount) != 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload[cursor..], pageCount);
            cursor += PageCountSize;
        }

        if ((optMask & OptDirtyBit) != 0)
        {
            payload[cursor] = dirtyBit;
            cursor += DirtyBitSize;
        }

        bytesWritten = size;
    }

    public static PageCacheEventData Decode(ReadOnlySpan<byte> source)
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
        var payload = source[headerSize..];
        var filePageIndex = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var optMask = payload[FilePageIndexSize];
        var cursor = FilePageIndexSize + OptMaskSize;

        int pageCount = 0;
        if ((optMask & OptPageCount) != 0)
        {
            pageCount = BinaryPrimitives.ReadInt32LittleEndian(payload[cursor..]);
            cursor += PageCountSize;
        }

        byte dirtyBit = 0;
        if ((optMask & OptDirtyBit) != 0)
        {
            dirtyBit = payload[cursor];
            cursor += DirtyBitSize;
        }

        return new PageCacheEventData(kind, threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo,
            filePageIndex, pageCount, optMask, dirtyBit, sourceLocationId);
    }
}

