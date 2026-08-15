using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
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
        //
        // #614 D-3: each record also carries the archetype's DURABLE per-database routing id alongside the per-process catalog id. Written once here, at table
        // build time, where RoutingIdForCatalog is in hand — events keep carrying the catalog id only, since resolving through this table costs nothing and
        // touching event payloads would cost the hot path.
        var archetypes = new ArchetypeRecord[archetypeDefinitions.Length];
        for (var i = 0; i < archetypeDefinitions.Length; i++)
        {
            var def = archetypeDefinitions[i];
            archetypes[i] = new ArchetypeRecord
            {
                ArchetypeId = def.ArchetypeId,
                Name = def.Name,
                RoutingId = engine.RoutingIdForCatalog(def.ArchetypeId),
            };
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

        // If a second engine is already live the WHOLE trace is mixed, not just its tail — worth saying out loud, because the degraded result (name-only
        // correlation) is easy to mistake later for a feature that was never built. The D-9 high-water mark itself is rebased by TyphonProfiler.Start.
        var liveEngines = ArchetypeRegistry.CurrentLiveEngineCount;
        if (liveEngines > 1)
        {
            ProfilerCaptureLog.MultipleEnginesAtCaptureStart(engine.Logger, liveEngines);
        }

        return new ProfilerSessionMetadata(
            SystemDefinitionRecordBuilder.BuildAll(systems), archetypes, componentTypes, workerCount, baseTickRate,
            Stopwatch.GetTimestamp(), Stopwatch.Frequency, DateTime.UtcNow, samplingSessionStartQpc, tracks, dags,
            componentDefinitions, archetypeDefinitions, indexCatalog, runtimeConfig, eventQueues, resourceGraphNodes,
            engine.DatabaseId, engine.MMF.DatabaseName, engine.TransactionChain.NextFreeId,
            ComputeSchemaFingerprint(componentDefinitions, archetypeDefinitions), runtime.CurrentTickNumber);
    }

    /// <summary>
    /// FNV-1a 64 over the schema's <c>(name, revision)</c> pairs — every component and every archetype. Equal fingerprints mean "same schema"; unequal means
    /// consult the database's <c>SchemaHistoryR1</c> for what actually moved between then and now.
    /// </summary>
    /// <remarks>
    /// Sorted by name with <see cref="StringComparer.Ordinal"/> before hashing, so the value depends on the schema and not on registration order — two
    /// processes that register the same components in different orders must agree, or the fingerprint reports drift that did not happen. Names are hashed as
    /// UTF-8 code units rather than through <c>string.GetHashCode</c>, which is randomised per process and would make the value useless across runs.
    /// </remarks>
    internal static ulong ComputeSchemaFingerprint(ComponentDefinitionRecord[] components, ArchetypeDefinitionRecord[] archetypes)
    {
        var entries = new List<(string Name, int Revision)>(components.Length + archetypes.Length);
        foreach (var c in components)
        {
            entries.Add((c.Name ?? string.Empty, c.Revision));
        }
        foreach (var a in archetypes)
        {
            entries.Add((a.Name ?? string.Empty, a.Revision));
        }
        entries.Sort(static (x, y) =>
        {
            var byName = string.CompareOrdinal(x.Name, y.Name);
            return byName != 0 ? byName : x.Revision.CompareTo(y.Revision);
        });

        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        Span<byte> revisionBytes = stackalloc byte[sizeof(int)];
        foreach (var (name, revision) in entries)
        {
            foreach (var b in Encoding.UTF8.GetBytes(name))
            {
                hash = (hash ^ b) * prime;
            }
            BinaryPrimitives.WriteInt32LittleEndian(revisionBytes, revision);
            foreach (var b in revisionBytes)
            {
                hash = (hash ^ b) * prime;
            }
            // Separator: without it ("Ab", 1) and ("A", …) could collide by concatenation. Cheap insurance on a value used to claim schemas match.
            hash = (hash ^ 0xFF) * prime;
        }
        return hash;
    }
}

/// <summary>Source-generated log messages for profiler capture setup. Separate holder so the messages are typed and allocation-free when the level is off.</summary>
internal static partial class ProfilerCaptureLog
{
    [LoggerMessage(EventId = 6140, Level = LogLevel.Warning,
        Message = "Profiling capture started with {LiveEngineCount} live DatabaseEngine instances. TyphonProfiler is process-global, so this capture will "
                + "interleave events from every engine and its archetype routing ids are ambiguous for the whole trace, not just part of it. Archetype "
                + "correlation will degrade to name-based joins.")]
    public static partial void MultipleEnginesAtCaptureStart(ILogger logger, int liveEngineCount);
}
