// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialRTreeInsert"/>.</summary>
[TraceEvent(TraceEventKind.SpatialRTreeInsert, EmitEncoder = true)]
internal ref partial struct SpatialRTreeInsertEvent
{
    [BeginParam]
    public long EntityId;
    public byte Depth;
    public byte DidSplit;
    public byte RestartCount;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialRTreeRemove"/>.</summary>
[TraceEvent(TraceEventKind.SpatialRTreeRemove, EmitEncoder = true)]
internal ref partial struct SpatialRTreeRemoveEvent
{
    [BeginParam]
    public long EntityId;
    public byte LeafCollapse;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialRTreeNodeSplit"/>.</summary>
[TraceEvent(TraceEventKind.SpatialRTreeNodeSplit, EmitEncoder = true)]
internal ref partial struct SpatialRTreeNodeSplitEvent
{
    [BeginParam]
    public byte Depth;
    public byte SplitAxis;
    public byte LeftCount;
    public byte RightCount;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialRTreeBulkLoad"/>.</summary>
[TraceEvent(TraceEventKind.SpatialRTreeBulkLoad, EmitEncoder = true)]
internal ref partial struct SpatialRTreeBulkLoadEvent
{
    [BeginParam]
    public int EntityCount;
    public int LeafCount;

}
