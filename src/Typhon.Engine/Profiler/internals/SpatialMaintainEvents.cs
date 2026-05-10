// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialMaintainInsert"/>.</summary>
[TraceEvent(TraceEventKind.SpatialMaintainInsert, EmitEncoder = true)]
internal ref partial struct SpatialMaintainInsertEvent
{
    [BeginParam]
    public long EntityPK;
    [BeginParam]
    public ushort ComponentTypeId;
    public byte DidDegenerate;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialMaintainUpdateSlowPath"/>.</summary>
[TraceEvent(TraceEventKind.SpatialMaintainUpdateSlowPath, EmitEncoder = true)]
internal ref partial struct SpatialMaintainUpdateSlowPathEvent
{
    [BeginParam]
    public long EntityPK;
    [BeginParam]
    public ushort ComponentTypeId;
    [BeginParam]
    public float EscapeDistSq;

}
