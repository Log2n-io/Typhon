// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialTierIndexRebuild"/>.</summary>
[TraceEvent(TraceEventKind.SpatialTierIndexRebuild, EmitEncoder = true)]
internal ref partial struct SpatialTierIndexRebuildEvent
{
    [BeginParam]
    public ushort ArchetypeId;
    public int ClusterCount;
    public int OldVersion;
    public int NewVersion;

}

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.SpatialTriggerEval"/>.</summary>
[TraceEvent(TraceEventKind.SpatialTriggerEval, EmitEncoder = true)]
internal ref partial struct SpatialTriggerEvalEvent
{
    [BeginParam]
    public ushort RegionId;
    public ushort OccupantCount;
    public ushort EnterCount;
    public ushort LeaveCount;

}
