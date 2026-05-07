using System;
using System.Diagnostics;
using Typhon.Engine;
using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace AntHill;

/// <summary>
/// AntHill-specific glue between the runtime's <see cref="SystemDefinition"/> array and the profiler's
/// <see cref="ProfilerSessionMetadata"/> shape. The CLI/env parsing and exporter construction now live in
/// the engine — see <see cref="Typhon.Engine.Profiler.ProfilerLaunchConfig"/> and
/// <see cref="Typhon.Engine.Profiler.ProfilerLauncher"/>.
/// </summary>
/// <remarks>
/// What stays AntHill-specific: knowing AntHill's system list to build <see cref="SystemDefinitionRecord"/>s
/// that the trace file / TCP stream embed for the viewer's system-index → display-name lookup. That's a host
/// concern (each host has a different DAG), so it doesn't belong in the engine.
/// </remarks>
public static class ProfilerSetup
{
    /// <summary>
    /// Build the <see cref="ProfilerSessionMetadata"/> passed to <see cref="TyphonProfiler.Start"/>.
    /// Converts the runtime's <see cref="SystemDefinition"/> array into the serialized
    /// <see cref="SystemDefinitionRecord"/> shape expected by the trace file / TCP stream, so the
    /// viewer can resolve system-index → display name.
    /// </summary>
    /// <remarks>
    /// Archetype + component-type tables are left empty: the engine currently emits typed events
    /// containing numeric IDs only; name resolution for those tables is a follow-up when the
    /// AntHill workload needs per-archetype flame-graph labels. Timestamps anchor the session —
    /// all subsequent events are measured against <c>startTimestamp</c>.
    /// </remarks>
    public static ProfilerSessionMetadata BuildSessionMetadata(SystemDefinition[] systems, int workerCount, float baseTickRate,
        string[] phases = null,
        Func<long> currentEngineTickProvider = null)
    {
        // currentEngineTickProvider is accepted for forward-compat with callers but the current
        // ProfilerSessionMetadata schema does not expose a per-event tick-stamp hook — the parameter
        // is intentionally ignored. Archetype/ComponentType records are left empty: ArchetypeRegistry
        // has no public enumeration API yet, and the engine emits typed events with numeric IDs only,
        // so the viewer renders them un-resolved for the moment.
        _ = currentEngineTickProvider;
        return new ProfilerSessionMetadata(SystemDefinitionRecordBuilder.BuildAll(systems), [], [], workerCount, baseTickRate, Stopwatch.GetTimestamp(),
            Stopwatch.Frequency, DateTime.UtcNow, phases: phases ?? []);
    }
}
