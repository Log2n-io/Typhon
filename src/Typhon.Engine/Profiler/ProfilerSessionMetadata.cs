using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

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

    public ProfilerSessionMetadata(SystemDefinitionRecord[] systems, ArchetypeRecord[] archetypes, ComponentTypeRecord[] componentTypes, int workerCount,
        float baseTickRate, long startTimestamp, long stopwatchFrequency, DateTime startedUtc, long samplingSessionStartQpc = 0, string[] phases = null)
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
    }
}
