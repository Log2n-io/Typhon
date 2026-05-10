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
    /// User-defined phase order from <c>RuntimeOptions.Phases</c> (RFC 07 §Q3). Surfaced through the trace v6 PhasesTable section and the
    /// Workbench Data API <c>/topology</c> endpoint so DAG-view consumers can render phase columns. Empty array when the host runs without a
    /// scheduler (e.g., standalone profiling) or with no phase declarations.
    /// </summary>
    public string[] Phases { get; }

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

    /// <summary>Engine runtime config snapshot (tick rate, worker count, phases). v7+ trace section. Null when no engine config is available.</summary>
    public RuntimeConfigRecord RuntimeConfig { get; }

    /// <summary>Per-queue static schema (capacity, event type). v7+ trace section. Empty when no queues are registered.</summary>
    public EventQueueRecord[] EventQueues { get; }

    /// <summary>Resource-graph pre-order tree snapshot. v7+ trace section. Empty when no resource graph is available.</summary>
    public ResourceGraphNodeRecord[] ResourceGraphNodes { get; }

    public ProfilerSessionMetadata(SystemDefinitionRecord[] systems, ArchetypeRecord[] archetypes, ComponentTypeRecord[] componentTypes, int workerCount,
        float baseTickRate, long startTimestamp, long stopwatchFrequency, DateTime startedUtc, long samplingSessionStartQpc = 0, string[] phases = null,
        ComponentDefinitionRecord[] componentDefinitions = null, ArchetypeDefinitionRecord[] archetypeDefinitions = null,
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
        Phases = phases ?? [];
        ComponentDefinitions = componentDefinitions ?? [];
        ArchetypeDefinitions = archetypeDefinitions ?? [];
        IndexCatalog = indexCatalog ?? [];
        RuntimeConfig = runtimeConfig;
        EventQueues = eventQueues ?? [];
        ResourceGraphNodes = resourceGraphNodes ?? [];
    }
}
