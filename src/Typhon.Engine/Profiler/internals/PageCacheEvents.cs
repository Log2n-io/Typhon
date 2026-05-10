// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using System;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side <c>ref struct</c> for <see cref="TraceEventKind.PageCacheFetch"/>. Required: <c>FilePageIndex</c>. No optionals.
/// </summary>
/// <remarks>
/// <para>Escape-hatch from the Phase 3 generator-emitted encoder: the page-cache codec family always writes an optMask byte (size += OptMaskSize)
/// even when the kind has no [Optional] fields. The generator's standard layout omits the optMask when no optionals are declared, so these kinds
/// keep the hand-written codec call to preserve wire-format compatibility.</para>
/// </remarks>
[TraceEvent(TraceEventKind.PageCacheFetch)]
internal ref partial struct PageCacheFetchEvent
{
    [BeginParam]
    public int FilePageIndex;

    public readonly int ComputeSize()
        => PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheFetch, Header.TraceIdHi != 0 || Header.TraceIdLo != 0, 0, Header.SourceLocationId);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => PageCacheEventCodec.Encode(destination, endTimestamp, TraceEventKind.PageCacheFetch, Header.ThreadSlot, Header.StartTimestamp,
            Header.SpanId, Header.ParentSpanId, Header.TraceIdHi, Header.TraceIdLo, FilePageIndex, pageCount: 0, optMask: 0, out bytesWritten,
            sourceLocationId: Header.SourceLocationId);
}

/// <summary>
/// Producer-side <c>ref struct</c> for <see cref="TraceEventKind.PageCacheDiskRead"/>. Required: <c>FilePageIndex</c>. No optionals.
/// Same-thread dispose via <see cref="TyphonEvent.PublishEvent"/>. See <see cref="PageCacheFetchEvent"/> for why this is an escape-hatch from the generator.
/// </summary>
[TraceEvent(TraceEventKind.PageCacheDiskRead)]
internal ref partial struct PageCacheDiskReadEvent
{
    [BeginParam]
    public int FilePageIndex;

    public readonly int ComputeSize()
        => PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheDiskRead, Header.TraceIdHi != 0 || Header.TraceIdLo != 0, 0, Header.SourceLocationId);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => PageCacheEventCodec.Encode(destination, endTimestamp, TraceEventKind.PageCacheDiskRead, Header.ThreadSlot, Header.StartTimestamp,
            Header.SpanId, Header.ParentSpanId, Header.TraceIdHi, Header.TraceIdLo, FilePageIndex, pageCount: 0, optMask: 0, out bytesWritten,
            sourceLocationId: Header.SourceLocationId);
}

[TraceEvent(TraceEventKind.PageCacheDiskWrite, Codec = typeof(PageCacheEventCodec))]
internal ref partial struct PageCacheDiskWriteEvent
{
    [BeginParam]
    public int FilePageIndex;

    [Optional]
    private int _pageCount;

    public readonly int ComputeSize()
        => PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheDiskWrite, Header.TraceIdHi != 0 || Header.TraceIdLo != 0, _optMask, Header.SourceLocationId);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => PageCacheEventCodec.Encode(destination, endTimestamp, TraceEventKind.PageCacheDiskWrite, Header.ThreadSlot, Header.StartTimestamp,
            Header.SpanId, Header.ParentSpanId, Header.TraceIdHi, Header.TraceIdLo, FilePageIndex, _pageCount, _optMask, out bytesWritten,
            sourceLocationId: Header.SourceLocationId);
}

[TraceEvent(TraceEventKind.PageCacheAllocatePage)]
internal ref partial struct PageCacheAllocatePageEvent
{
    [BeginParam]
    public int FilePageIndex;

    public readonly int ComputeSize()
        => PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheAllocatePage, Header.TraceIdHi != 0 || Header.TraceIdLo != 0, 0, Header.SourceLocationId);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => PageCacheEventCodec.Encode(destination, endTimestamp, TraceEventKind.PageCacheAllocatePage, Header.ThreadSlot, Header.StartTimestamp,
            Header.SpanId, Header.ParentSpanId, Header.TraceIdHi, Header.TraceIdLo, FilePageIndex, pageCount: 0, optMask: 0, out bytesWritten,
            sourceLocationId: Header.SourceLocationId);
}

/// <summary>
/// Flush span — required: <see cref="PageCount"/> (number of pages flushed in this batch).
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire-format slot reuse.</b> <c>PageCacheEventCodec</c> declares its required payload as a single i32 slot
/// named <c>filePageIndex</c>. For Flush, that slot carries <see cref="PageCount"/> instead — the codec is shared
/// with Fetch / DiskRead / DiskWrite / AllocatePage and there is only one i32 slot in the wire layout. The
/// hand-written <see cref="EncodeTo"/> below passes <c>filePageIndex: PageCount</c> deliberately; the parameter
/// name reflects the codec contract, not the semantics of this kind. The <c>PageCacheEventData.FilePageIndex</c>
/// field on the consumer side carries the same overload — readers must check the kind to know what the int means.
/// </para>
/// <para>
/// <b>Why escape-hatch.</b> The shared codec also writes an <c>optMask</c> byte unconditionally, which the Phase-3
/// generator's standard layout omits when no <c>[Optional]</c> fields are declared. Both quirks together force
/// this kind to keep the hand-written codec call. Do NOT add <c>EmitEncoder = true</c> here without re-deriving the
/// generator template to model both behaviors.
/// </para>
/// </remarks>
[TraceEvent(TraceEventKind.PageCacheFlush)]
internal ref partial struct PageCacheFlushEvent
{
    [BeginParam]
    public int PageCount;

    public readonly int ComputeSize()
        => PageCacheEventCodec.ComputeSize(TraceEventKind.PageCacheFlush, Header.TraceIdHi != 0 || Header.TraceIdLo != 0, 0, Header.SourceLocationId);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        // PageCount intentionally goes into the codec's `filePageIndex` slot — see the <remarks> on this struct.
        => PageCacheEventCodec.Encode(destination, endTimestamp, TraceEventKind.PageCacheFlush, Header.ThreadSlot, Header.StartTimestamp,
            Header.SpanId, Header.ParentSpanId, Header.TraceIdHi, Header.TraceIdLo, filePageIndex: PageCount, pageCount: 0, optMask: 0, out bytesWritten,
            sourceLocationId: Header.SourceLocationId);
}

/// <summary>
/// Page cache backpressure wait — clock-sweep retry loop couldn't find a free page. Payload: 3 × i32 diagnostic counters.
/// </summary>
[TraceEvent(TraceEventKind.PageCacheBackpressure, EmitEncoder = true)]
internal ref partial struct PageCacheBackpressureEvent
{
    public int RetryCount;
    public int DirtyCount;
    public int EpochCount;

}
