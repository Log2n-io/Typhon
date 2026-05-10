// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.StoragePageCacheDirtyWalk"/>.</summary>
[TraceEvent(TraceEventKind.StoragePageCacheDirtyWalk, EmitEncoder = true)]
internal ref partial struct StoragePageCacheDirtyWalkEvent
{
    [BeginParam]
    public int RangeStart;
    [BeginParam]
    public int RangeLen;
    public int DirtyMs;

}
