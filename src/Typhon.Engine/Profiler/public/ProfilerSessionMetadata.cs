using System;
using Typhon.Profiler;

namespace Typhon.Engine;

/// <summary>
/// Static description of the profiling session, passed to each exporter once via <see cref="IProfilerExporter.Initialize"/>. Holds everything
/// the exporter needs to write the header + metadata tables.
/// </summary>
/// <remarks>
/// All fields are immutable for the lifetime of the session — set once at <c>TyphonProfiler.Start</c> and never mutated. Multiple exporters can
/// safely read them concurrently without synchronization.
/// </remarks>
public sealed class ProfilerSessionMetadata
{
    /// <summary>System DAG metadata captured at session start. Empty array if the profiler is started outside a runtime context.</summary>
    public SystemDefinitionRecord[] Systems { get; }

    /// <summary>Archetype table — maps <c>ArchetypeId</c> numbers in typed events back to human-readable names for the viewer.</summary>
    public ArchetypeRecord[] Archetypes { get; }

    /// <summary>Component type table — maps <c>ComponentTypeId</c> numbers in typed events back to C# type names for the viewer.</summary>
    public ComponentTypeRecord[] ComponentTypes { get; }

    /// <summary>Number of scheduler worker threads at session start. Zero if the profiler is running standalone (no scheduler).</summary>
    public int WorkerCount { get; }

    /// <summary>Target tick rate in Hz (e.g., 60.0). Zero for non-runtime profiling.</summary>
    public float BaseTickRate { get; }

    /// <summary><c>Stopwatch.GetTimestamp()</c> value captured at <c>TyphonProfiler.Start</c>. Anchors all subsequent event timestamps.</summary>
    public long StartTimestamp { get; }

    /// <summary><c>Stopwatch.Frequency</c> at session start. Lets exporters convert ticks to wall-clock time without re-querying.</summary>
    public long StopwatchFrequency { get; }

    /// <summary>UTC wall-clock time when the session started, for human-readable headers.</summary>
    public DateTime StartedUtc { get; }

    /// <summary>
    /// <c>Stopwatch.GetTimestamp()</c> captured when an EventPipe CPU-sampling session started. Zero when no sampling companion is running.
    /// Hosts that attach an EventPipe session (e.g., AntHill profile runner) populate this so the viewer can overlay .nettrace flame graphs on
    /// the same time base as the record stream.
    /// </summary>
    public long SamplingSessionStartQpc { get; }

    /// <summary>
    /// Track table (#354) — the top level of the runtime partitioning hierarchy (<c>Engine → Track → DAG → Phase → System</c>). Surfaced through
    /// the trace v11 TracksTable section. Empty array when the host runs without a scheduler (e.g., standalone profiling).
    /// </summary>
    public TrackRecord[] Tracks { get; }

    /// <summary>
    /// DAG table (#354) — each DAG references its owning track by index and carries its own ordered phase names. Surfaced through the trace v11
    /// DagsTable section. Empty array when the host runs without a scheduler.
    /// </summary>
    public DagRecord[] Dags { get; }

    // ── Static-structure tables (trace format v7+) ───────────────────────────
    // All optional. Hosts that don't have a live engine to introspect (e.g., the unit-test fixture builder) may pass empty
    // arrays; the FileExporter still writes the section headers so reader walking stays positionally consistent. Production
    // hosts (AntHill, embedded engines) populate them via ProfilerSessionMetadataBuilder.FromEngine().

    /// <summary>Rich component-type definitions (fields, types, sizes, indexes). v7+ trace section. Empty when no engine is attached.</summary>
    public ComponentDefinitionRecord[] ComponentDefinitions { get; }

    /// <summary>Rich archetype definitions (parent/child, slot map, cluster info). v7+ trace section. Empty when no engine is attached.</summary>
    public ArchetypeDefinitionRecord[] ArchetypeDefinitions { get; }

    /// <summary>Flat per-(component, field) index catalog. v7+ trace section. Empty when no engine is attached.</summary>
    public IndexCatalogEntry[] IndexCatalog { get; }

    /// <summary>Engine runtime config snapshot (tick rate, worker count). v7+ trace section. Null when no engine config is available.</summary>
    public RuntimeConfigRecord RuntimeConfig { get; }

    /// <summary>Per-queue static schema (capacity, event type). v7+ trace section. Empty when no queues are registered.</summary>
    public EventQueueRecord[] EventQueues { get; }

    /// <summary>Resource-graph pre-order tree snapshot. v7+ trace section. Empty when no resource graph is available.</summary>
    public ResourceGraphNodeRecord[] ResourceGraphNodes { get; }

    /// <summary>
    /// Construct session metadata from captured engine/runtime tables. Every optional array defaults to empty (never <c>null</c>);
    /// <see cref="RuntimeConfig"/> defaults to <c>null</c> when no engine config is available.
    /// </summary>
    /// <param name="systems">System DAG metadata; empty when started outside a runtime context.</param>
    /// <param name="archetypes">Archetype id → name table for the viewer.</param>
    /// <param name="componentTypes">Component-type id → CLR name table for the viewer.</param>
    /// <param name="workerCount">Scheduler worker-thread count at session start; <c>0</c> when standalone.</param>
    /// <param name="baseTickRate">Target tick rate in Hz; <c>0</c> for non-runtime profiling.</param>
    /// <param name="startTimestamp"><c>Stopwatch.GetTimestamp()</c> anchor captured at session start.</param>
    /// <param name="stopwatchFrequency"><c>Stopwatch.Frequency</c> at session start, for tick → wall-clock conversion.</param>
    /// <param name="startedUtc">UTC wall-clock time the session started.</param>
    /// <param name="samplingSessionStartQpc">CPU-sampling QPC anchor; <c>0</c> when no EventPipe sampling companion is running.</param>
    /// <param name="tracks">Track table (top of the runtime partitioning hierarchy); empty without a scheduler.</param>
    /// <param name="dags">DAG table; empty without a scheduler.</param>
    /// <param name="componentDefinitions">Rich component-type definitions; empty when no engine is attached.</param>
    /// <param name="archetypeDefinitions">Rich archetype definitions; empty when no engine is attached.</param>
    /// <param name="indexCatalog">Per-(component, field) index catalog; empty when no engine is attached.</param>
    /// <param name="runtimeConfig">Engine runtime-config snapshot, or <c>null</c> when unavailable.</param>
    /// <param name="eventQueues">Per-queue static schema; empty when no queues are registered.</param>
    /// <param name="resourceGraphNodes">Resource-graph pre-order tree snapshot; empty when unavailable.</param>
    public ProfilerSessionMetadata(SystemDefinitionRecord[] systems, ArchetypeRecord[] archetypes, ComponentTypeRecord[] componentTypes, int workerCount,
        float baseTickRate, long startTimestamp, long stopwatchFrequency, DateTime startedUtc, long samplingSessionStartQpc = 0, TrackRecord[] tracks = null,
        DagRecord[] dags = null, ComponentDefinitionRecord[] componentDefinitions = null, ArchetypeDefinitionRecord[] archetypeDefinitions = null,
        IndexCatalogEntry[] indexCatalog = null, RuntimeConfigRecord runtimeConfig = null, EventQueueRecord[] eventQueues = null,
        ResourceGraphNodeRecord[] resourceGraphNodes = null)
    {
        Systems = systems ?? [];
        Archetypes = archetypes ?? [];
        ComponentTypes = componentTypes ?? [];
        WorkerCount = workerCount;
        BaseTickRate = baseTickRate;
        StartTimestamp = startTimestamp;
        StopwatchFrequency = stopwatchFrequency;
        StartedUtc = startedUtc;
        SamplingSessionStartQpc = samplingSessionStartQpc;
        Tracks = tracks ?? [];
        Dags = dags ?? [];
        ComponentDefinitions = componentDefinitions ?? [];
        ArchetypeDefinitions = archetypeDefinitions ?? [];
        IndexCatalog = indexCatalog ?? [];
        RuntimeConfig = runtimeConfig;
        EventQueues = eventQueues ?? [];
        ResourceGraphNodes = resourceGraphNodes ?? [];
    }
}
