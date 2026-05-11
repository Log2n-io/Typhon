// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.SchedulerSystemArchetype"/>. Emitted once per (system, archetype) pair
/// at parallel-query completion when <c>TelemetryConfig.SchedulerArchetypeTouchesActive</c> is true. Captures the cross-dimension
/// that <see cref="SchedulerChunkEvent"/> (per-system, per-chunk) and the EcsQuery* events (per-archetype) leave separate.
/// Span duration covers the system's parallel-query bracket (start of first chunk → end of last chunk).
/// </summary>
[TraceEvent(TraceEventKind.SchedulerSystemArchetype, GenerateFactory = false, EmitEncoder = true, ExternalTimestamps = true)]
internal ref partial struct SchedulerSystemArchetypeEvent
{
    [BeginParam(ParamType = "int")] public ushort SystemIndex;
    [BeginParam(ParamType = "int")] public ushort ArchetypeId;
    [BeginParam] public int EntityCount;
    [BeginParam] public int ChunkCount;
}
