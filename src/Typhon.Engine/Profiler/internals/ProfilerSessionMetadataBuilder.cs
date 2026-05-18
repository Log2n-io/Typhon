using System;
using System.Diagnostics;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Composes the <see cref="ProfilerSessionMetadata"/> for a profiling session entirely from a live <see cref="TyphonRuntime"/>.
/// </summary>
/// <remarks>
/// This is the engine-side replacement for the old host glue (<c>AntHill.Core.ProfilerSetup.BuildSessionMetadata</c>, issue #332):
/// every input it needs is derivable from the runtime, so hosts no longer assemble metadata by hand. Systems / worker count / tick rate / ring capacity / chunk
/// size come from <see cref="TyphonRuntime.Systems"/> + <see cref="TyphonRuntime.Options"/>; the v7 static-structure tables and the resource graph from
/// <see cref="TyphonRuntime.Engine"/>; timestamps are captured here.
/// </remarks>
internal static class ProfilerSessionMetadataBuilder
{
    /// <summary>
    /// Build the session metadata. <paramref name="samplingSessionStartQpc"/> is the CPU-sampler QPC anchor (<c>0</c> when CPU sampling is not active) —
    /// captured by the bootstrap before this call so it lands in the trace header.
    /// </summary>
    public static ProfilerSessionMetadata Build(TyphonRuntime runtime, long samplingSessionStartQpc)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var engine = runtime.Engine;
        var systems = runtime.Systems;
        var options = runtime.Options;
        var workerCount = options.ResolveWorkerCount();
        float baseTickRate = options.BaseTickRate;

        // v7 rich static-structure tables — drive the Workbench schema panels for trace sessions.
        var bundle = ProfilerStaticDataBuilder.BuildAll(engine, runtime);
        var componentDefinitions = bundle.ComponentDefinitions;
        var archetypeDefinitions = bundle.ArchetypeDefinitions;
        var indexCatalog = bundle.IndexCatalog;
        var eventQueues = bundle.EventQueues;

        // Project the thin id→name tables from the rich definitions — the engine has no separate enumeration API for them,
        // and the rich tables already cover every registered archetype / component type.
        var archetypes = new ArchetypeRecord[archetypeDefinitions.Length];
        for (var i = 0; i < archetypeDefinitions.Length; i++)
        {
            var def = archetypeDefinitions[i];
            archetypes[i] = new ArchetypeRecord { ArchetypeId = def.ArchetypeId, Name = def.Name };
        }
        var componentTypes = new ComponentTypeRecord[componentDefinitions.Length];
        for (var i = 0; i < componentDefinitions.Length; i++)
        {
            var def = componentDefinitions[i];
            componentTypes[i] = new ComponentTypeRecord { ComponentTypeId = def.ComponentTypeId, Name = def.Name };
        }

        // The engine is the resource-graph root (DatabaseEngine : IResource).
        var resourceGraphNodes = ProfilerStaticDataBuilder.BuildResourceGraphSnapshot(engine);

        // Track→DAG hierarchy (#354) — built directly from the runtime's scheduler.
        var (tracks, dags) = ProfilerStaticDataBuilder.BuildTrackHierarchy(runtime);

        // Runtime config — fully derived from RuntimeOptions; no host-supplied or stubbed values (issue #332).
        var runtimeConfig = new RuntimeConfigRecord
        {
            BaseTickRate = options.BaseTickRate,
            WorkerCount = workerCount,
            TelemetryRingCapacity = options.TelemetryRingCapacity,
            ParallelQueryMinChunkSize = options.ParallelQueryMinChunkSize,
        };

        return new ProfilerSessionMetadata(
            SystemDefinitionRecordBuilder.BuildAll(systems), archetypes, componentTypes, workerCount, baseTickRate,
            Stopwatch.GetTimestamp(), Stopwatch.Frequency, DateTime.UtcNow, samplingSessionStartQpc, tracks, dags,
            componentDefinitions, archetypeDefinitions, indexCatalog, runtimeConfig, eventQueues, resourceGraphNodes);
    }
}
