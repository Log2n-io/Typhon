using System;
using System.ComponentModel.DataAnnotations;

namespace Typhon.Workbench.Hosting;

/// <summary>
/// User-facing Workbench options, persisted to JSON in the OS user-data folder.
/// See claude/design/Profiler/10-profiler-source-attribution.md §5.7 for the design.
/// </summary>
/// <remarks>
/// Records (immutable + value equality) + sparse JSON: only fields the user has changed are
/// persisted; missing fields default-construct via the record's <c>= new()</c> initializer.
/// New categories or properties added in future versions auto-default in old user files.
/// </remarks>
public sealed record WorkbenchOptions
{
    public EditorOptions Editor { get; init; } = new();
    public ProfilerOptions Profiler { get; init; } = new();
    public SchemaOptions Schema { get; init; } = new();
}

/// <summary>Editor handoff preferences for the "Open in editor" feature on profiler spans.</summary>
public sealed record EditorOptions
{
    /// <summary>Editor to launch when the user clicks "Open in editor" on a span. Default: VS Code.</summary>
    public EditorKind Kind { get; init; } = EditorKind.VsCode;

    /// <summary>
    /// argv template used when <see cref="Kind"/> is <see cref="EditorKind.Custom"/>. Tokens:
    /// <c>{file}</c>, <c>{line}</c>, <c>{column}</c>. Tokenized into discrete argv elements before
    /// <c>Process.Start</c> — never executed via a shell.
    /// </summary>
    public string CustomCommand { get; init; } = "";
}

/// <summary>Profiler-related preferences (workspace root, view-range debounce, future fields).</summary>
public sealed record ProfilerOptions
{
    /// <summary>
    /// Absolute path of the workspace root used to resolve repo-relative source paths from trace
    /// files (the "/_/..." form produced by <c>SourceLocationGenerator</c>). When empty, the
    /// Workbench falls back to the directory it was launched from.
    /// </summary>
    public string WorkspaceRoot { get; init; } = "";

    /// <summary>
    /// Debounce window (milliseconds) between pan/zoom in the profiler's TimeArea and the moment
    /// cross-panel consumers (SystemDag, CriticalPath, DataFlow, AccessMatrix) re-aggregate against
    /// the new viewport. See #345 / claude/design/Apps/Workbench/profiler-time-window-refactor.md.
    /// <para>
    /// <c>0</c> commits synchronously — useful for tests and for users who explicitly prefer zero
    /// latency at the cost of pan/zoom fluidity. Default 150 ms hits the "feels instant once you
    /// stop" sweet spot; the upper bound of 5000 ms accommodates extreme-workload diagnostic use.
    /// </para>
    /// </summary>
    [Range(0, 5000)]
    public int ViewRangeDebounceMs { get; init; } = 150;
}

/// <summary>
/// Schema-assembly resolution preferences (ADR-055 Phase 2). Directories the Workbench searches — at
/// priority 2, above its own bundled binaries — when resolving a database's recorded schema assemblies
/// on open. Lets a user point the Workbench at a custom or recompiled-from-git schema build without
/// copying DLLs next to the database file.
/// </summary>
public sealed record SchemaOptions
{
    /// <summary>
    /// Absolute directories searched for schema assemblies, in order, before the bundled (Workbench
    /// deployment) directory. A non-existent or non-matching entry is skipped at resolution time. Empty
    /// by default.
    /// </summary>
    public string[] Directories { get; init; } = [];

    // A record's auto-generated equality compares string[] by reference, so two SchemaOptions with
    // element-equal arrays would compare unequal — making every WorkbenchOptions comparison spuriously
    // unequal after a JSON round-trip and defeating OptionsStore's hot-reload dedup. Compare by element.
    public bool Equals(SchemaOptions other) =>
        other is not null && Directories.AsSpan().SequenceEqual(other.Directories);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var dir in Directories)
        {
            hash.Add(dir);
        }
        return hash.ToHashCode();
    }
}

/// <summary>Editor target enumeration. Wire-stable; never renumber.</summary>
public enum EditorKind
{
    VsCode = 0,
    Cursor = 1,
    Rider = 2,
    VisualStudio = 3,
    Custom = 4,
}
