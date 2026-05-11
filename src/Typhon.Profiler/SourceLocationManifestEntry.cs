namespace Typhon.Profiler;

/// <summary>
/// One entry in the trailing <c>SourceLocationManifest</c> of a <c>.typhon-trace</c> file.
/// Maps a compile-time <see cref="Id"/> (the value carried in span records when <c>SpanFlagsHasSourceLocation</c> is set) to a source location: the file path
/// (via <see cref="FileId"/> indexing into the parallel <c>FileTable</c>), the line number, the kind of factory the call site invoked, and the containing
/// method name for display.
/// See claude/design/Profiler/10-profiler-source-attribution.md §4.7.2.
/// </summary>
public readonly struct SourceLocationManifestEntry
{
    /// <summary>Site id (1-based, 0 = "unknown source").</summary>
    public ushort Id { get; }
    /// <summary>Index into the parallel <c>FileTable</c>.</summary>
    public ushort FileId { get; }
    /// <summary>1-based line number within the file.</summary>
    public uint Line { get; }
    /// <summary>
    /// Compile-time hint of the <c>TraceEventKind</c> for this site. May be 0 if the generator chose not to
    /// emit per-site kinds (the wire's record kind byte is the source of truth at runtime).
    /// </summary>
    public byte Kind { get; }
    /// <summary>Containing-method short name for display ("BTree.Insert", "ChunkedTransaction.CommitChanges", ...).</summary>
    public string Method { get; }

    public SourceLocationManifestEntry(ushort id, ushort fileId, uint line, byte kind, string method)
    {
        Id = id;
        FileId = fileId;
        Line = line;
        Kind = kind;
        Method = method ?? string.Empty;
    }
}
