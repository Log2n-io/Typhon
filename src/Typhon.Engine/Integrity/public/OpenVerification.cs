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
    /// root. <b>O(segments)</b> rather than O(pages): kilobytes read, sub-millisecond. The default.
    /// </summary>
    Spine = 1,

    /// <summary>Adds every page's header. O(pages) and IOPS-bound; no page bodies are hashed.</summary>
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

    private static string BuildMessage(IntegrityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new System.Text.StringBuilder(512);
        sb.Append("The database failed integrity verification and was not opened (verdict: ").Append(report.Verdict).Append(").\n");

        for (var i = 0; i < report.Findings.Count; i++)
        {
            var f = report.Findings[i];
            if (f.Severity != IntegritySeverity.Fatal)
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
