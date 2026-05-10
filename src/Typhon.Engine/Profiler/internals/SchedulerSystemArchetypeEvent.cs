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
[TraceEvent(TraceEventKind.SchedulerSystemArchetype, GenerateFactory = false, EmitEncoder = true)]
internal ref partial struct SchedulerSystemArchetypeEvent
{
    public ushort SystemIndex;
    public ushort ArchetypeId;
    public int EntityCount;
    public int ChunkCount;

    // Intentionally no Dispose() method: SchedulerSystemArchetypeEvent is emitted in one shot via TyphonEvent.EmitSchedulerSystemArchetype, never via `using var`.
    // (See SchedulerChunkEvent for the same rationale: a `using var` scope would silently drop the record if no ring publish happens.)
}
