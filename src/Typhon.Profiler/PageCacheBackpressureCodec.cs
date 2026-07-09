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
    /// <summary>Which page-cache event kind this record decodes to.</summary>
    public TraceEventKind Kind { get; }

    /// <summary>Typhon thread slot the event was emitted on.</summary>
    public byte ThreadSlot { get; }

    /// <summary>Span start timestamp, in Stopwatch ticks.</summary>
    public long StartTimestamp { get; }

    /// <summary>Span duration, in Stopwatch ticks (0 for the <see cref="TraceEventKind.PageEvicted"/> marker).</summary>
    public long DurationTicks { get; }

    /// <summary>Span id of this event.</summary>
    public ulong SpanId { get; }

    /// <summary>Span id of the parent span, or 0 when none.</summary>
    public ulong ParentSpanId { get; }

    /// <summary>High 64 bits of the W3C trace id. 0 when no trace context was carried — see <see cref="HasTraceContext"/>.</summary>
    public ulong TraceIdHi { get; }

    /// <summary>Low 64 bits of the W3C trace id. 0 when no trace context was carried — see <see cref="HasTraceContext"/>.</summary>
    public ulong TraceIdLo { get; }

    /// <summary>Required for Fetch/DiskRead/DiskWrite/AllocatePage; meaningless for Flush.</summary>
    public int FilePageIndex { get; }

    /// <summary>Required for Flush; optional for DiskWrite (contiguous run length); meaningless for Fetch/DiskRead/AllocatePage.</summary>
    public int PageCount { get; }

    /// <summary>Bit mask of the optional trailing fields present — see the <c>Opt*</c> constants on <see cref="PageCacheEventCodec"/>.</summary>
    public byte OptionalFieldMask { get; }

    /// <summary>Phase 5: dirty-bit for <see cref="TraceEventKind.PageEvicted"/> (1 if the displaced page was dirty, 0 otherwise).</summary>
    public byte DirtyBit { get; }

    /// <summary>Source-location id assigned by <c>SourceLocationGenerator</c> (#302). Zero when the wire record didn't carry source attribution.</summary>
    public ushort SourceLocationId { get; }

    /// <summary>True when the record carried the optional <see cref="PageCount"/> field (mask bit <see cref="PageCacheEventCodec.OptPageCount"/>).</summary>
    public bool HasPageCount => (OptionalFieldMask & PageCacheEventCodec.OptPageCount) != 0;

    /// <summary>True when the record carried the optional <see cref="DirtyBit"/> field (mask bit <see cref="PageCacheEventCodec.OptDirtyBit"/>).</summary>
    public bool HasDirtyBit => (OptionalFieldMask & PageCacheEventCodec.OptDirtyBit) != 0;

    /// <summary>True when a W3C trace context (<see cref="TraceIdHi"/>/<see cref="TraceIdLo"/>) was present on the wire.</summary>
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    /// <summary>True when the record carried a non-zero <see cref="SourceLocationId"/>.</summary>
    public bool HasSourceLocation => SourceLocationId != 0;

    /// <summary>Construct a decoded page-cache event. Optional fields default to absent (0).</summary>
    /// <param name="kind">Page-cache event kind.</param>
    /// <param name="threadSlot">Typhon thread slot.</param>
    /// <param name="startTimestamp">Span start, in Stopwatch ticks.</param>
    /// <param name="durationTicks">Span duration, in Stopwatch ticks.</param>
    /// <param name="spanId">Span id.</param>
    /// <param name="parentSpanId">Parent span id, or 0.</param>
    /// <param name="traceIdHi">High 64 bits of the trace id, or 0.</param>
    /// <param name="traceIdLo">Low 64 bits of the trace id, or 0.</param>
    /// <param name="filePageIndex">Target file page index (meaningless for Flush).</param>
    /// <param name="pageCount">Contiguous page-run length (present per <paramref name="optionalFieldMask"/>).</param>
    /// <param name="optionalFieldMask">Bit mask of optional trailing fields present.</param>
    /// <param name="dirtyBit">Eviction dirty flag; meaningful only when the mask sets <see cref="PageCacheEventCodec.OptDirtyBit"/>.</param>
    /// <param name="sourceLocationId">Source-location id, or 0 when no source attribution was carried.</param>
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
    /// <summary>Typhon thread slot the backpressure stall was observed on.</summary>
    public byte ThreadSlot { get; }

    /// <summary>Span start timestamp, in Stopwatch ticks.</summary>
    public long StartTimestamp { get; }

    /// <summary>Span duration (length of the stall), in Stopwatch ticks.</summary>
    public long DurationTicks { get; }

    /// <summary>Span id of this event.</summary>
    public ulong SpanId { get; }

    /// <summary>Span id of the parent span, or 0 when none.</summary>
    public ulong ParentSpanId { get; }

    /// <summary>High 64 bits of the W3C trace id. 0 when no trace context was carried — see <see cref="HasTraceContext"/>.</summary>
    public ulong TraceIdHi { get; }

    /// <summary>Low 64 bits of the W3C trace id. 0 when no trace context was carried — see <see cref="HasTraceContext"/>.</summary>
    public ulong TraceIdLo { get; }

    /// <summary>Number of allocation retries the writer spun through before a page freed up.</summary>
    public int RetryCount { get; }

    /// <summary>Count of dirty pages in the cache at the time of the stall.</summary>
    public int DirtyCount { get; }

    /// <summary>Count of epoch-protected pages in the cache at the time of the stall.</summary>
    public int EpochCount { get; }

    /// <summary>Source-location id assigned by <c>SourceLocationGenerator</c> (#302). Zero when the wire record didn't carry source attribution.</summary>
    public ushort SourceLocationId { get; }

    /// <summary>True when a W3C trace context (<see cref="TraceIdHi"/>/<see cref="TraceIdLo"/>) was present on the wire.</summary>
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    /// <summary>True when the record carried a non-zero <see cref="SourceLocationId"/>.</summary>
    public bool HasSourceLocation => SourceLocationId != 0;

    /// <summary>Construct a decoded page-cache backpressure event.</summary>
    /// <param name="threadSlot">Typhon thread slot.</param>
    /// <param name="startTimestamp">Span start, in Stopwatch ticks.</param>
    /// <param name="durationTicks">Stall duration, in Stopwatch ticks.</param>
    /// <param name="spanId">Span id.</param>
    /// <param name="parentSpanId">Parent span id, or 0.</param>
    /// <param name="traceIdHi">High 64 bits of the trace id, or 0.</param>
    /// <param name="traceIdLo">Low 64 bits of the trace id, or 0.</param>
    /// <param name="retryCount">Allocation retries before a page freed up.</param>
    /// <param name="dirtyCount">Dirty page count at stall time.</param>
    /// <param name="epochCount">Epoch-protected page count at stall time.</param>
    /// <param name="sourceLocationId">Source-location id, or 0 when no source attribution was carried.</param>
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

    /// <summary>Total on-wire record size in bytes for a backpressure event.</summary>
    /// <param name="hasTraceContext">Whether a 16-byte trace context is included in the span header.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + PayloadSize;

    /// <summary>Decode a <see cref="TraceEventKind.PageCacheBackpressure"/> record into structured form.</summary>
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

    /// <summary>Total on-wire record size in bytes for a page-cache event, accounting for the optional fields the mask selects.</summary>
    /// <param name="kind">Event kind (does not affect size; carried for symmetry with the encoder).</param>
    /// <param name="hasTraceContext">Whether a 16-byte trace context is included in the span header.</param>
    /// <param name="optMask">Optional-field mask — see <see cref="OptPageCount"/> / <see cref="OptDirtyBit"/>.</param>
    /// <param name="sourceLocationId">Non-zero adds a 2-byte source-location id to the header.</param>
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

    /// <summary>Decode any page-cache span record into structured form. The event kind is read from the common header.</summary>
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

