using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Knobs for a scan. The defaults are the ones an operator should get by typing nothing.
/// </summary>
[PublicAPI]
public sealed class IntegrityOptions
{
    /// <summary>Spine-only: what verify-on-open runs. Bounded by segment count, not database size.</summary>
    public static IntegrityOptions Spine => new() { Depth = ScanDepth.Spine };

    /// <summary>Page headers only. No checksum sweep, no cross-structure work.</summary>
    public static IntegrityOptions Quick => new() { Depth = ScanDepth.Quick };

    /// <summary>Headers plus a checksum sweep over every allocated page. The default for <c>typhon check</c>.</summary>
    public static IntegrityOptions Standard => new() { Depth = ScanDepth.Standard };

    /// <summary>Everything, including cross-structure checks. The depth the crash suite asserts at.</summary>
    public static IntegrityOptions Deep => new() { Depth = ScanDepth.Deep };

    /// <summary>How much work to do.</summary>
    public ScanDepth Depth { get; init; } = ScanDepth.Standard;

    /// <summary>
    /// Check codes or family prefixes to run, e.g. <c>["CHK-IDX", "CHK-PHY-01"]</c>. Empty means every check applicable to
    /// <see cref="Depth"/>. Anything excluded is named in <see cref="ScanLimits.ChecksSkipped"/>.
    /// </summary>
    public IReadOnlyList<string> IncludeChecks { get; init; } = [];

    /// <summary>Check codes or family prefixes to skip. Applied after <see cref="IncludeChecks"/>.</summary>
    public IReadOnlyList<string> ExcludeChecks { get; init; } = [];

    /// <summary>
    /// Maximum findings to record before the scan starts aggregating rather than enumerating. Protects a report against a
    /// database where one systemic fault produces millions of individually-true findings.
    /// </summary>
    public int MaxFindings { get; init; } = 10_000;

    /// <summary>Maximum entity ids carried inline in a <see cref="LossEstimate.Sample"/>. The full set goes to the manifest.</summary>
    public int MaxLossSample { get; init; } = 32;

    /// <summary>
    /// Soft ceiling on scanner working-set bytes for cross-structure set comparison. The <see cref="ScanDepth.Deep"/>
    /// checks degrade to counting rather than enumerating when a comparison would exceed it, and say so in
    /// <see cref="ScanLimits.Caveats"/>.
    /// </summary>
    public long MemoryBudgetBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Cancellation for a long <see cref="ScanDepth.Deep"/> sweep. Cancelling produces a partial report, never an exception.</summary>
    public System.Threading.CancellationToken Cancellation { get; init; }

    /// <summary>Optional progress callback: <c>(phase, done, total)</c>. Invoked from the scanning thread; keep it cheap.</summary>
    public Action<string, long, long> Progress { get; init; }

    /// <summary>Whether a check code passes this option set's include/exclude filters.</summary>
    /// <param name="code">Full check code, e.g. <c>"CHK-IDX-04"</c>.</param>
    public bool IsCheckEnabled(string code)
    {
        if (IncludeChecks.Count > 0)
        {
            var matched = false;
            for (var i = 0; i < IncludeChecks.Count; i++)
            {
                if (code.StartsWith(IncludeChecks[i], StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return false;
            }
        }

        for (var i = 0; i < ExcludeChecks.Count; i++)
        {
            if (code.StartsWith(ExcludeChecks[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
