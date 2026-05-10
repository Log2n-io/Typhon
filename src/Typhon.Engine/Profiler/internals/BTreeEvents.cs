using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// B+Tree insert span — no typed payload (the event kind alone carries the operation identity). Used for the insert path in <c>BTree.Add</c>.
/// </summary>
/// <remarks>
/// <b>Size:</b> 37 bytes without trace context, 53 bytes with — that's just the span header. The old fixed 64 B struct wasted 27 B per insert on
/// fields that were always zero for this event type. At ~1M inserts/sec during an AntHill spawn burst, that's 27 MB/sec of wasted ring buffer
/// reclaimed.
/// </remarks>
[TraceEvent(TraceEventKind.BTreeInsert, EmitEncoder = true)]
internal ref partial struct BTreeInsertEvent
{
}

/// <summary>B+Tree delete span — same no-payload shape as <see cref="BTreeInsertEvent"/>.</summary>
[TraceEvent(TraceEventKind.BTreeDelete, EmitEncoder = true)]
internal ref partial struct BTreeDeleteEvent
{
}

/// <summary>B+Tree node split span — same no-payload shape.</summary>
[TraceEvent(TraceEventKind.BTreeNodeSplit, EmitEncoder = true)]
internal ref partial struct BTreeNodeSplitEvent
{
}

/// <summary>B+Tree node merge span — same no-payload shape.</summary>
[TraceEvent(TraceEventKind.BTreeNodeMerge, EmitEncoder = true)]
internal ref partial struct BTreeNodeMergeEvent
{
}
