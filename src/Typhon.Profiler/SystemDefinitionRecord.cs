namespace Typhon.Profiler;

/// <summary>
/// Describes a system in the DAG, stored in the system definition table of a <c>.typhon-trace</c> file.
/// Variable-length record (name is UTF-8 encoded).
/// </summary>
/// <remarks>
/// RFC 07 access declaration fields (Phase / Reads / Writes / etc.) were added in trace format v6.
/// Older v5 traces yield empty defaults — readers do not throw.
/// </remarks>
public sealed class SystemDefinitionRecord
{
    /// <summary>System index in the DAG array.</summary>
    public ushort Index { get; init; }

    /// <summary>Display name of the system.</summary>
    public string Name { get; init; }

    /// <summary>Execution model (Pipeline = 0, Query = 1, Callback = 2).</summary>
    public byte Type { get; init; }

    /// <summary>Priority for overload management (Normal = 0, Low = 1, High = 2, Critical = 3).</summary>
    public byte Priority { get; init; }

    /// <summary>Whether this system uses parallel chunk dispatch.</summary>
    public bool IsParallel { get; init; }

    /// <summary>Simulation tier filter (SimTier flags byte). 0x0F = All.</summary>
    public byte TierFilter { get; init; }

    /// <summary>Indices of predecessor systems in the DAG.</summary>
    public ushort[] Predecessors { get; init; } = [];

    /// <summary>Indices of successor systems in the DAG.</summary>
    public ushort[] Successors { get; init; } = [];

    // ─── RFC 07 access declarations (trace format v6+) ───────────────────────

    /// <summary>Phase token name (RFC 07 §Q3) the system runs in. Empty string when no phase was declared.</summary>
    public string PhaseName { get; init; } = string.Empty;

    /// <summary>True iff the system runs alone in its phase (no concurrent peers; <see cref="SystemBuilder.ExclusivePhase"/>).</summary>
    public bool IsExclusivePhase { get; init; }

    /// <summary>Component type names declared with <c>Reads&lt;T&gt;()</c> — ambiguous-staleness reads.</summary>
    public string[] Reads { get; init; } = [];

    /// <summary>Component type names declared with <c>ReadsFresh&lt;T&gt;()</c> — ordered after writers in the same phase.</summary>
    public string[] ReadsFresh { get; init; } = [];

    /// <summary>Component type names declared with <c>ReadsSnapshot&lt;T&gt;()</c> — ordered before writers in the same phase.</summary>
    public string[] ReadsSnapshot { get; init; } = [];

    /// <summary>Component type names read beyond the system's primary input view (cross-entity reads).</summary>
    public string[] AdditionalReads { get; init; } = [];

    /// <summary>Component type names mutated via <c>Writes&lt;T&gt;()</c>.</summary>
    public string[] Writes { get; init; } = [];

    /// <summary>Component type names written via Immediate side-transactions. Surfaced for tooling; does not affect scheduler order.</summary>
    public string[] SideWrites { get; init; } = [];

    /// <summary>Names of event queues this system publishes to.</summary>
    public string[] WritesEvents { get; init; } = [];

    /// <summary>Names of event queues this system consumes from.</summary>
    public string[] ReadsEvents { get; init; } = [];

    /// <summary>Names of named resources this system mutates.</summary>
    public string[] WritesResources { get; init; } = [];

    /// <summary>Names of named resources this system reads.</summary>
    public string[] ReadsResources { get; init; } = [];

    /// <summary>Names of systems this one is explicitly ordered AFTER (escape-hatch edge — RFC 07 §Q5).</summary>
    public string[] ExplicitAfter { get; init; } = [];

    /// <summary>Names of systems this one is explicitly ordered BEFORE.</summary>
    public string[] ExplicitBefore { get; init; } = [];

    // ─── Track→DAG hierarchy (trace format v11+, #354) ───────────────────────

    /// <summary>Flat global id of the DAG this system belongs to — indexes the trace's <see cref="DagRecord"/> table by <see cref="DagRecord.Id"/>.</summary>
    public ushort DagId { get; init; }
}
