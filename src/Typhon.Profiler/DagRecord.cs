namespace Typhon.Profiler;

/// <summary>
/// Describes a DAG — the third level of the runtime partitioning hierarchy (<c>Engine → Track → DAG → Phase → System</c>) — stored in the
/// DagsTable of a <c>.typhon-trace</c> file (format v11+, #354). Phases are DAG-local: each DAG carries its own ordered phase-name list.
/// Variable-length record (name + phase-name strings are UTF-8 encoded).
/// </summary>
public sealed class DagRecord
{
    /// <summary>Flat global DAG id — referenced by <see cref="SystemDefinitionRecord.DagId"/>.</summary>
    public int Id { get; init; }

    /// <summary>DAG name — unique across the whole schedule.</summary>
    public string Name { get; init; }

    /// <summary>Index into the trace's TracksTable identifying the owning <see cref="TrackRecord"/>.</summary>
    public int TrackIndex { get; init; }

    /// <summary>The DAG's ordered phase names. A DAG that declared no phases carries a single implicit phase name.</summary>
    public string[] PhaseNames { get; init; } = [];
}
