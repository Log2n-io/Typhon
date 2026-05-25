using System;
using System.Collections.Generic;
using Typhon.Profiler;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Per-session index of every instrumented span instance in a trace, grouped by <see cref="TraceEventKind"/> (#351 Phase 5,
/// §8.3). Built lazily by a one-pass walk of the cache chunk stream — the same pattern as
/// <see cref="TraceSessionRuntime.ComputeGcSuspensions"/> — and consumed by the <see cref="Services.ScopeResolver"/> to turn a
/// "scope to span kind X" request into the union of that kind's time-windows.
/// </summary>
/// <remarks>
/// Each window is a <c>[startQpc, endQpc)</c> pair in the trace's QPC base (the same base as the CPU samples and tick
/// summaries — file traces have <c>baselineQpc 0</c>). Windows per kind are sorted by start. Best-effort: any read failure
/// yields <see cref="Empty"/> — absent span data is surfaced, never fatal to the session.
/// </remarks>
public sealed class SpanInstanceIndex
{
    /// <summary>Sentinel for traces with no decodable span instances (or any build failure).</summary>
    public static readonly SpanInstanceIndex Empty = new(new Dictionary<int, (long, long)[]>());

    private readonly Dictionary<int, (long Start, long End)[]> _windowsByKind;

    private SpanInstanceIndex(Dictionary<int, (long Start, long End)[]> windowsByKind)
    {
        _windowsByKind = windowsByKind;
    }

    /// <summary>The <see cref="TraceEventKind"/> values (as ints) that have at least one span instance in the trace.</summary>
    public IReadOnlyCollection<int> AvailableKinds => _windowsByKind.Keys;

    /// <summary>
    /// The start-sorted <c>[startQpc, endQpc)</c> windows of every instance of <paramref name="kind"/>. Empty when the trace
    /// carries no span of that kind.
    /// </summary>
    public (long Start, long End)[] WindowsForKind(int kind)
        => _windowsByKind.TryGetValue(kind, out var windows) ? windows : [];

    /// <summary>
    /// Builds an index directly from a pre-computed <c>kind → windows</c> map (the non-chunk-walk construction path).
    /// Each window list is start-sorted defensively. An empty map yields <see cref="Empty"/>.
    /// </summary>
    public static SpanInstanceIndex FromWindows(IReadOnlyDictionary<int, (long Start, long End)[]> windowsByKind)
    {
        ArgumentNullException.ThrowIfNull(windowsByKind);
        if (windowsByKind.Count == 0)
        {
            return Empty;
        }
        var copy = new Dictionary<int, (long Start, long End)[]>(windowsByKind.Count);
        foreach (var kv in windowsByKind)
        {
            var src = kv.Value ?? [];
            var windows = new (long Start, long End)[src.Length];
            Array.Copy(src, windows, src.Length);
            Array.Sort(windows, static (a, b) => a.Start.CompareTo(b.Start));
            copy[kv.Key] = windows;
        }
        return new SpanInstanceIndex(copy);
    }

    /// <summary>
    /// Builds the index from a one-pass walk of <paramref name="reader"/>'s chunk stream. Delegates to
    /// <see cref="TraceChunkScan.BuildIndexes"/> — the shared walk that produces this index and the
    /// <see cref="SampleClassifier"/> together from one decompression — and keeps the span half. Returns
    /// <see cref="Empty"/> for a reader with no chunks.
    /// </summary>
    public static SpanInstanceIndex Build(TraceFileCacheReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        TraceChunkScan.BuildIndexes(reader, out var spans, out _);
        return spans;
    }
}
