// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>Producer-side <c>ref struct</c> for <see cref="TraceEventKind.PageCacheFetch"/>. Required: <c>FilePageIndex</c>. No optionals.</summary>
[TraceEvent(TraceEventKind.PageCacheFetch, EmitEncoder = true)]
internal ref partial struct PageCacheFetchEvent
{
    [BeginParam]
    public int FilePageIndex;
}

/// <summary>Producer-side <c>ref struct</c> for <see cref="TraceEventKind.PageCacheDiskRead"/>. Same shape as PageCacheFetch.</summary>
[TraceEvent(TraceEventKind.PageCacheDiskRead, EmitEncoder = true)]
internal ref partial struct PageCacheDiskReadEvent
{
    [BeginParam]
    public int FilePageIndex;
}

[TraceEvent(TraceEventKind.PageCacheDiskWrite, EmitEncoder = true)]
internal ref partial struct PageCacheDiskWriteEvent
{
    [BeginParam]
    public int FilePageIndex;

    [Optional(MaskValue = 0x01)]
    private int _pageCount;
}

[TraceEvent(TraceEventKind.PageCacheAllocatePage, EmitEncoder = true)]
internal ref partial struct PageCacheAllocatePageEvent
{
    [BeginParam]
    public int FilePageIndex;
}

/// <summary>Flush span — required: <see cref="PageCount"/>.</summary>
[TraceEvent(TraceEventKind.PageCacheFlush, EmitEncoder = true)]
internal ref partial struct PageCacheFlushEvent
{
    [BeginParam]
    public int PageCount;
}

/// <summary>Page cache backpressure wait — clock-sweep retry loop couldn't find a free page. Payload: 3 × i32 diagnostic counters.</summary>
[TraceEvent(TraceEventKind.PageCacheBackpressure, EmitEncoder = true)]
internal ref partial struct PageCacheBackpressureEvent
{
    public int RetryCount;
    public int DirtyCount;
    public int EpochCount;
}
