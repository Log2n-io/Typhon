// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialQueryAabb"/>. Stats payload, no coords.</summary>
[TraceEvent(TraceEventKind.SpatialQueryAabb, EmitEncoder = true)]
internal ref partial struct SpatialQueryAabbEvent
{
    public ushort NodesVisited;
    public ushort LeavesEntered;
    public ushort ResultCount;
    public byte RestartCount;
    [BeginParam]
    public uint CategoryMask;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialQueryRadius"/>.</summary>
[TraceEvent(TraceEventKind.SpatialQueryRadius, EmitEncoder = true)]
internal ref partial struct SpatialQueryRadiusEvent
{
    public ushort NodesVisited;
    public ushort ResultCount;
    [BeginParam]
    public float Radius;
    public byte RestartCount;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialQueryRay"/>.</summary>
[TraceEvent(TraceEventKind.SpatialQueryRay, EmitEncoder = true)]
internal ref partial struct SpatialQueryRayEvent
{
    public ushort NodesVisited;
    public ushort ResultCount;
    [BeginParam]
    public float MaxDist;
    public byte RestartCount;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialQueryFrustum"/>.</summary>
[TraceEvent(TraceEventKind.SpatialQueryFrustum, EmitEncoder = true)]
internal ref partial struct SpatialQueryFrustumEvent
{
    public ushort NodesVisited;
    public ushort ResultCount;
    [BeginParam]
    public byte PlaneCount;
    public byte RestartCount;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialQueryKnn"/>.</summary>
[TraceEvent(TraceEventKind.SpatialQueryKnn, EmitEncoder = true)]
internal ref partial struct SpatialQueryKnnEvent
{
    [BeginParam]
    public ushort K;
    public byte IterCount;
    public float FinalRadius;
    public ushort ResultCount;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialQueryCount"/>.</summary>
[TraceEvent(TraceEventKind.SpatialQueryCount, EmitEncoder = true)]
internal ref partial struct SpatialQueryCountEvent
{
    [BeginParam]
    public byte Variant;
    public ushort NodesVisited;
    public int ResultCount;

}
