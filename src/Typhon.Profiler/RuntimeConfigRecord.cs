namespace Typhon.Profiler;

/// <summary>
/// Runtime configuration snapshot captured at trace start (v7+). Single record (no count prefix in the wire format) — readers consume it once and bind to the
/// runtime-config panel.
/// </summary>
/// <remarks>
/// Some fields duplicate values already in the <see cref="TraceFileHeader"/> (BaseTickRate, WorkerCount) — kept here for a self-describing one-stop record that
/// exposes the full <c>RuntimeOptions</c> shape, not just the subset the header happens to have promoted to fixed fields.
/// </remarks>
public sealed class RuntimeConfigRecord
{
    /// <summary>Target tick rate in Hz (<c>RuntimeOptions.BaseTickRate</c>).</summary>
    public int BaseTickRate { get; init; }

    /// <summary>Worker thread count (<c>RuntimeOptions.WorkerCount</c>; 0 ⇒ engine auto-detected).</summary>
    public int WorkerCount { get; init; }

    /// <summary>Telemetry ring capacity (power-of-two slot count for the per-thread spillover ring).</summary>
    public int TelemetryRingCapacity { get; init; }

    /// <summary>Minimum entities per parallel-query chunk (<c>RuntimeOptions.ParallelQueryMinChunkSize</c>).</summary>
    public int ParallelQueryMinChunkSize { get; init; }

    /// <summary>Default phase name (<c>RuntimeOptions.DefaultPhase</c>). Empty when no phases were declared.</summary>
    public string DefaultPhase { get; init; } = string.Empty;

    /// <summary>
    /// Phase names in declaration order — same content as the existing v6 PhasesTable, but kept here too so the runtime-config record is self-contained for the
    /// Workbench's options panel without cross-table joins.
    /// </summary>
    public string[] Phases { get; init; } = [];
}
