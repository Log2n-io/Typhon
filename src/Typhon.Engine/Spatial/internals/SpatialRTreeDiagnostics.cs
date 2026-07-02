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
/// <b>Latch-safety (constraint #3):</b> the overflow site runs while holding an OLC read latch, so this path must
/// <b>never throw</b> — an exception under a latch leaks it and deadlocks. The counter is a lock-free
/// <see cref="Interlocked"/> increment and the optional log is wrapped defensively; neither can escape.
/// </para>
///
/// This class is deliberately <b>non-generic</b> so the counter is a single process-wide value shared across every
/// <c>SpatialRTree&lt;TStore&gt;</c> instantiation (a static on the generic type would be per-<c>TStore</c>).
/// </summary>
internal static partial class SpatialRTreeDiagnostics
{
    /// <summary>Total number of DFS-stack overflows recorded since process start. Read via <see cref="Interlocked.Read"/> in tests.</summary>
    internal static long DfsStackOverflowCount;

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
        Message = "Spatial {QueryKind} query DFS stack overflow (depth > 256) — the R-Tree is degenerate/corrupt and results " +
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
