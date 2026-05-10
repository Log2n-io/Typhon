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
    public static ProfilerSessionMetadata BuildSessionMetadata(SystemDefinition[] systems, int workerCount, float baseTickRate, string[] phases = null,
        Func<long> currentEngineTickProvider = null, DatabaseEngine engine = null, IResource resourceGraphRoot = null, TyphonRuntime runtime = null)
    {
        // currentEngineTickProvider is accepted for forward-compat with callers but the current ProfilerSessionMetadata schema does not expose a per-event
        // tick-stamp hook — the parameter is intentionally ignored. Archetype/ComponentType (thin id→name) records are left empty:
        // ArchetypeRegistry has no public enumeration API exposed at the thin-record level, and the engine emits typed events with numeric IDs only.
        //
        // The v7 rich static-structure tables ARE populated when an engine handle is available — that's what drives the Workbench schema panels for
        // trace sessions (#322 follow-up: the user explicitly wants component/archetype/index detail in the trace file so AntHill optimisation work has the
        // structural context offline). When no engine is supplied (e.g., legacy callers), all v7 fields stay empty and the trace still loads — the schema
        // panels just render "no data" for that session.
        _ = currentEngineTickProvider;

        ComponentDefinitionRecord[] componentDefinitions = [];
        ArchetypeDefinitionRecord[] archetypeDefinitions = [];
        IndexCatalogEntry[] indexCatalog = [];
        EventQueueRecord[] eventQueues = [];
        ResourceGraphNodeRecord[] resourceGraphNodes = [];
        ArchetypeRecord[] archetypes = [];
        ComponentTypeRecord[] componentTypes = [];
        if (engine != null)
        {
            var bundle = ProfilerStaticDataBuilder.BuildAll(engine, runtime);
            componentDefinitions = bundle.ComponentDefinitions;
            archetypeDefinitions = bundle.ArchetypeDefinitions;
            indexCatalog = bundle.IndexCatalog;
            eventQueues = bundle.EventQueues;

            // Derive thin id→name records from the rich definitions: the engine has no public enumeration API for the
            // thin tables, but the rich tables already cover every registered archetype / component type, so projecting
            // them here keeps the trace's two parallel tables consistent without a second registry walk.
            archetypes = new ArchetypeRecord[archetypeDefinitions.Length];
            for (int i = 0; i < archetypeDefinitions.Length; i++)
            {
                var def = archetypeDefinitions[i];
                archetypes[i] = new ArchetypeRecord { ArchetypeId = def.ArchetypeId, Name = def.Name };
            }
            componentTypes = new ComponentTypeRecord[componentDefinitions.Length];
            for (int i = 0; i < componentDefinitions.Length; i++)
            {
                var def = componentDefinitions[i];
                componentTypes[i] = new ComponentTypeRecord { ComponentTypeId = def.ComponentTypeId, Name = def.Name };
            }
        }
        if (resourceGraphRoot != null)
        {
            resourceGraphNodes = ProfilerStaticDataBuilder.BuildResourceGraphSnapshot(resourceGraphRoot);
        }

        // Runtime config record — populated from the values we have at call time. Telemetry ring capacity / parallel-query
        // chunk size aren't surfaced through TyphonBridge today; pass 0 (the Workbench renders "—" for those rows).
        var runtimeConfig = new RuntimeConfigRecord
        {
            BaseTickRate = (int)Math.Round(baseTickRate),
            WorkerCount = workerCount,
            TelemetryRingCapacity = 0,
            ParallelQueryMinChunkSize = 0,
            DefaultPhase = phases is { Length: > 0 } ? phases[0] : string.Empty,
            Phases = phases ?? [],
        };

        return new ProfilerSessionMetadata(
            SystemDefinitionRecordBuilder.BuildAll(systems), archetypes, componentTypes, workerCount, baseTickRate, Stopwatch.GetTimestamp(), Stopwatch.Frequency,
            DateTime.UtcNow,
            phases: phases ?? [], componentDefinitions: componentDefinitions, archetypeDefinitions: archetypeDefinitions, indexCatalog: indexCatalog,
            runtimeConfig: runtimeConfig, eventQueues: eventQueues, resourceGraphNodes: resourceGraphNodes);
    }
}
