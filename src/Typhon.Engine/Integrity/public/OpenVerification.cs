using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// How much of a database to verify while opening it.
/// </summary>
/// <remarks>
/// Three moments could carry verification and only one of them was empty. A crash-path open already runs the full rebuild
/// net. An offline <c>typhon check</c> is explicit. A <b>clean</b> open verified nothing at all — it skipped the rebuild
/// on the strength of a flag that only records that the last process closed properly, never that the bytes survived.
/// </remarks>
[PublicAPI]
public enum OpenVerification
{
    /// <summary>
    /// Verify nothing. Exists for benchmarks and in-memory fixtures, and emits a warning at open naming the risk — an
    /// escape hatch that is silent is just a default with extra steps.
    /// </summary>
    None = 0,

    /// <summary>
    /// Page-0 pair selection, the bootstrap stream, and that every segment pointer resolves to a real, allocated segment
    /// root. <b>O(segments)</b> rather than O(pages): kilobytes read, sub-millisecond. The default. It reads each segment's
    /// directory but not its forward page chain, which would read every data page (#1143).
    /// </summary>
    Spine = 1,

    /// <summary>
    /// Adds every page's header, and each segment's directory against its forward page chain, counted from the headers the
    /// sweep already read. O(pages) and IOPS-bound; no page bodies are hashed.
    /// </summary>
    Quick = 2,

    /// <summary>Adds a checksum sweep over every allocated page. O(pages) and bandwidth-bound.</summary>
    Standard = 3
}

/// <summary>
/// Thrown when opening a database whose structural spine does not verify.
/// </summary>
/// <remarks>
/// There is no <c>--force</c> and no degraded mode. This is not new behaviour in kind — the recovery net already fails an
/// open loudly rather than opening over suspect primary data; verify-on-open extends the same judgement to the clean path.
/// A database integrity problem is not a detail worth hiding to make an open succeed.
/// </remarks>
[PublicAPI]
public sealed class DatabaseIntegrityException : Exception
{
    /// <summary>Creates the exception from the report that refused the open.</summary>
    /// <param name="report">The verification report. Attached so the caller can see exactly what failed.</param>
    public DatabaseIntegrityException(IntegrityReport report)
        : base(BuildMessage(report)) => Report = report;

    /// <summary>The report that refused the open, with every finding and the scan's stated limits.</summary>
    public IntegrityReport Report { get; }

    /// <summary>
    /// Whether a finding refuses an open: every <see cref="IntegritySeverity.Fatal"/> one, and a segment whose directory and forward page chain
    /// disagree unless crash recovery will rebuild it. The scanner rates that one a repairable divergence, and the open used to refuse it itself; #1143
    /// moved the check out of the open. After a clean close nothing rebuilds the segment: a write was lost before the close. After an unclean close the
    /// same disagreement is what an interrupted checkpoint leaves behind, and recovery replaces a cluster, entity-map or index segment with a fresh one
    /// that WAL replay refills, so for those three it is reported and the open proceeds. Every other kind is loaded without that tolerance, and the
    /// open would fail on it anyway, without this report.
    /// </summary>
    /// <param name="finding">The finding.</param>
    /// <param name="cleanShutdown">Whether the last close was clean (<see cref="DatabaseIdentity.CleanShutdown"/>).</param>
    internal static bool RefusesOpen(IntegrityFinding finding, bool cleanShutdown) =>
        finding.Severity == IntegritySeverity.Fatal
        || (finding.Code == Typhon.Engine.Internals.SegmentChecks.DirectoryChain && (cleanShutdown || !RebuiltByRecovery(finding.Locus.Kind)));

    /// <summary>
    /// The segment kinds the crash path loads tolerating a torn segment, rebuilding it from the WAL (RB-01): see <c>TryLoadChunkBasedSegment</c>.
    /// </summary>
    private static bool RebuiltByRecovery(StorageSegmentKind kind) =>
        kind is StorageSegmentKind.Cluster or StorageSegmentKind.EntityMap or StorageSegmentKind.Index;

    private static string BuildMessage(IntegrityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new System.Text.StringBuilder(512);
        sb.Append("The database failed integrity verification and was not opened (verdict: ").Append(report.Verdict).Append(").\n");

        for (var i = 0; i < report.Findings.Count; i++)
        {
            var f = report.Findings[i];
            if (!RefusesOpen(f, report.Identity.CleanShutdown))
            {
                continue;
            }

            sb.Append("  ").Append(f.Code).Append(": ").Append(f.Summary).Append('\n');
            sb.Append("    ").Append(f.Detail).Append('\n');
        }

        sb.Append("\nRun `typhon check <bundle> --depth deep` for the full report, and `typhon repair <bundle> --plan` to see "
            + "what can be done about it.");
        return sb.ToString();
    }
}
