using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Typhon.Engine.Internals;

/// <summary>
/// Always-on diagnostics for the spatial R-Tree query DFS traversal (issue #422, Tier-0).
///
/// <para>
/// The DFS stack (<see cref="QueryStackBuffer"/>, 256 slots) bounds the number of pending sibling nodes across a query.
/// With realistic fan-out and a tree depth capped at <see cref="SpatialRTreeConstants.MaxTreeDepth"/>, it can never fill —
/// so an overflow means a degenerate/corrupt tree and would silently drop children (incomplete results). In Release the old
/// <c>Debug.Fail</c> was compiled out, hiding this. This helper makes the overflow an <b>always-on record</b>:
/// a process counter (machine-observable, testable) plus a one-shot <c>[LoggerMessage]</c> warning.
/// </para>
///
/// <para>
/// The ray query's priority queue reports here too, but only at its post-spill ceiling
/// (<see cref="SpatialRTreeConstants.MaxRayHeapCapacity"/>). It is not a fixed buffer: it grows on demand, so reaching the ceiling likewise means a
/// degenerate or cyclic tree rather than merely a dense scene. Ordinary growth is counted separately by <see cref="RayHeapSpillCount"/>, which is a
/// performance signal, not a correctness one. Before #589 the ray path had neither — it dropped children silently.
/// </para>
///
/// <para>
/// <b>Latch-safety (constraint #3):</b> the overflow sites run inside an optimistic (OLC) read section, so this path must
/// <b>never throw</b> — an escaping exception would abandon the traversal mid-protocol. The counter is a lock-free
/// <see cref="Interlocked"/> increment and the optional log is wrapped defensively; neither can escape.
/// </para>
///
/// <para>
/// Precisely: an OLC reader holds nothing — <see cref="OlcLatch.ReadVersion"/> only snapshots a version — so there is no
/// latch here to leak, and the earlier wording ("while holding an OLC read latch") overstated it. The non-throwing
/// discipline is kept regardless: it costs nothing, and it is what lets the ray path rent pooled buffers on this same
/// traversal without a throw stranding the enumeration.
/// </para>
///
/// This class is deliberately <b>non-generic</b> so the counter is a single process-wide value shared across every
/// <c>SpatialRTree&lt;TStore&gt;</c> instantiation (a static on the generic type would be per-<c>TStore</c>).
/// </summary>
internal static partial class SpatialRTreeDiagnostics
{
    /// <summary>
    /// Total number of DFS-stack overflows recorded since process start. Read via <see cref="Interlocked.Read(ref readonly long)"/> in tests.
    /// </summary>
    internal static long DfsStackOverflowCount;

    /// <summary>
    /// Number of times a ray query's priority queue outgrew its inline buffer and spilled to pooled arrays, since process start.
    /// </summary>
    /// <remarks>
    /// <b>Not an error.</b> The spill is precisely what keeps results complete when the traversal frontier is large (#589), and it is the expected outcome for
    /// a scene whose subtrees share an entry distance along the ray. Treat it as a performance signal: a workload that spills on every query is paying two
    /// pool rentals and a copy each time, and would be better served by a larger
    /// <see cref="SpatialRTreeConstants.RayHeapInlineCapacity"/>.
    /// </remarks>
    internal static long RayHeapSpillCount;

    /// <summary>
    /// Optional sink for the one-shot overflow warning. Set once at engine construction (first non-null wins). When null the
    /// counter still records; only the human-readable warning is suppressed. Kept optional so the always-on record path needs
    /// no logger plumbed into the query enumerators.
    /// </summary>
    internal static ILogger DiagnosticsLogger;

    // One-shot guard so a single degenerate query (which can hit the overflow branch on every over-256 push) does not spam the
    // log while holding the latch. The counter still increments on every occurrence.
    private static int Warned;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Spatial {QueryKind} query traversal buffer overflow — the R-Tree is degenerate/corrupt and results " +
                  "may be incomplete. Total overflows this process: {TotalCount}.")]
    private static partial void LogDfsStackOverflow(ILogger logger, string queryKind, long totalCount);

    /// <summary>
    /// Record a DFS-stack overflow. Always-on, allocation-free, and latch-safe (never throws). Increments the process counter
    /// and, at most once per process, emits a warning through <see cref="DiagnosticsLogger"/> when one is registered.
    /// </summary>
    /// <param name="queryKind">Short query-shape label for the warning (e.g. "AABB", "frustum", "count").</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void RecordDfsStackOverflow(string queryKind)
    {
        var total = Interlocked.Increment(ref DfsStackOverflowCount);

        var logger = DiagnosticsLogger;
        if (logger != null && Interlocked.CompareExchange(ref Warned, 1, 0) == 0)
        {
            try
            {
                LogDfsStackOverflow(logger, queryKind, total);
            }
            catch
            {
                // Never let a misbehaving logger escape under an OLC latch (constraint #3). The counter already recorded the event.
            }
        }
    }
}
