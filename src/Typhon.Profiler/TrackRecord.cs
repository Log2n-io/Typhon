namespace Typhon.Profiler;

/// <summary>
/// Describes a Track — the top level of the runtime partitioning hierarchy (<c>Engine → Track → DAG → Phase → System</c>) — stored in the
/// TracksTable of a <c>.typhon-trace</c> file (format v11+, #354). Variable-length record (name + tag strings are UTF-8 encoded).
/// </summary>
public sealed class TrackRecord
{
    /// <summary>Track name. Diagnostic only — no engine behaviour keys off it.</summary>
    public string Name { get; init; }

    /// <summary>Execution order index. Lower-indexed tracks run to completion before higher-indexed ones begin.</summary>
    public int OrderIndex { get; init; }

    /// <summary>The track's open tag set (e.g. the <c>engine</c> tag marking engine-internal tracks).</summary>
    public string[] Tags { get; init; } = [];
}
