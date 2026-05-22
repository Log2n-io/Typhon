using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side intern table for the v9 Query Definition Export feature (#342). Maps file paths and method names to stable <see cref="ushort"/> IDs that are
/// written to <see cref="Typhon.Profiler.TraceEventKind.QueryDefinitionDescribe"/> and <see cref="Typhon.Profiler.TraceEventKind.QueryPlan"/> events.
/// The deduplicated string table is then written to the trace file as <c>QuerySourceStringTable</c> at session end by <see cref="FileExporter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity reservation.</b> ID 0 is reserved as the sentinel "no string" — used for unattributed callers (e.g., the scheduler-driven
/// <c>View.RefreshFromScheduler</c> path) where the Workbench should fall back to the owning system attribution instead. <see cref="Intern"/> never returns 0
/// for non-empty input.
/// </para>
/// <para>
/// <b>Thread safety.</b> Producer-side reads happen on any thread that emits query trace events. The internal <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// handles concurrent <see cref="Intern"/> calls. The companion <see cref="System.Collections.Generic.List{T}"/> of strings is mutated under a lock so the
/// export-time <see cref="SnapshotStrings"/> sees a consistent index → string mapping.
/// </para>
/// <para>
/// <b>Lifecycle.</b> <see cref="Reset"/> is called at <c>TyphonProfiler.Start</c>. Tables grow throughout the session and are read once by the exporter at
/// <c>TyphonProfiler.Stop</c>.
/// </para>
/// <para>
/// <b>Overflow.</b> The id space is 16-bit (max 65,535 distinct strings — far beyond any realistic session). Past that limit, <see cref="Intern"/> returns 0
/// and the string is lost; the Workbench renders such records as having no source attribution.
/// </para>
/// </remarks>
internal static class QuerySourceStringInterner
{
    private static readonly Lock Lock = new();
    private static readonly ConcurrentDictionary<string, ushort> Ids = new(System.StringComparer.Ordinal);
    private static readonly List<string> Strings = [];
    private static int NextId = 1; // 0 is the "no string" sentinel

    /// <summary>
    /// Return the existing ID for the given string, or assign a new one. Empty / null input returns 0
    /// (the sentinel). Overflow past <see cref="ushort.MaxValue"/> also returns 0.
    /// </summary>
    public static ushort Intern(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        if (Ids.TryGetValue(value, out var existing))
        {
            return existing;
        }

        lock (Lock)
        {
            if (Ids.TryGetValue(value, out existing))
            {
                return existing;
            }

            var next = Interlocked.Increment(ref NextId) - 1;
            if (next > ushort.MaxValue)
            {
                // Overflow — return the sentinel. The string is lost for this session.
                return 0;
            }

            var id = (ushort)next;
            Strings.Add(value);
            Ids[value] = id;
            return id;
        }
    }

    /// <summary>
    /// Snapshot the current id → string table for export. Returns an array of length <c>maxId + 1</c> with entries indexed by id (entry 0 is null — sentinel
    /// slot). Safe to call concurrently with new <see cref="Intern"/> calls; the returned snapshot reflects state at call time.
    /// </summary>
    public static string[] SnapshotStrings()
    {
        lock (Lock)
        {
            var count = Strings.Count;
            var arr = new string[count + 1];  // index 0 = null sentinel
            for (var i = 0; i < count; i++)
            {
                arr[i + 1] = Strings[i];
            }
            return arr;
        }
    }

    /// <summary>Reset the interner. Called at <c>TyphonProfiler.Start</c>.</summary>
    public static void Reset()
    {
        lock (Lock)
        {
            Ids.Clear();
            Strings.Clear();
            NextId = 1;
        }
    }
}
