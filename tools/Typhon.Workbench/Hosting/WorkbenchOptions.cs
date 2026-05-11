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

/// <summary>Profiler-related preferences (workspace root, future fields).</summary>
public sealed record ProfilerOptions
{
    /// <summary>
    /// Absolute path of the workspace root used to resolve repo-relative source paths from trace
    /// files (the "/_/..." form produced by <c>SourceLocationGenerator</c>). When empty, the
    /// Workbench falls back to the directory it was launched from.
    /// </summary>
    public string WorkspaceRoot { get; init; } = "";
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
