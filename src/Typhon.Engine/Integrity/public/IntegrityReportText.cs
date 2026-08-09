using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Text;

namespace Typhon.Engine;

/// <summary>
/// Renders an <see cref="IntegrityReport"/> as human-readable text.
/// </summary>
/// <remarks>
/// Findings are ranked by severity and the loss summary comes <b>last</b>, so it is the parting message rather than
/// something scrolled past. The limits block prints on a green report too — that is the whole point of it.
/// </remarks>
[PublicAPI]
public static class IntegrityReportText
{
    /// <summary>Renders the full report.</summary>
    /// <param name="report">The report to render.</param>
    /// <param name="colour">Whether to emit ANSI colour escapes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <c>null</c>.</exception>
    public static string Render(IntegrityReport report, bool colour = false)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder(4096);
        var p = new Palette(colour);

        RenderHeader(sb, report, p);
        RenderVerdict(sb, report, p);
        RenderFindings(sb, report, p);
        RenderLoss(sb, report, p);
        RenderLimits(sb, report, p);
        RenderFooter(sb, report, p);

        return sb.ToString();
    }

    private static void RenderHeader(StringBuilder sb, IntegrityReport report, Palette p)
    {
        var id = report.Identity;
        sb.Append("\n  ").Append(p.Bold).Append(id.Name ?? "(unnamed)").Append(p.Reset);
        sb.Append(" · format v").Append(id.FormatRevision);
        sb.Append(" · ").Append(id.PageCount.ToString("N0")).Append(" pages (").Append(FormatBytes(id.SizeBytes)).Append(')');
        if (id.CheckpointLsn > 0)
        {
            sb.Append(" · checkpoint LSN ").Append(id.CheckpointLsn.ToString("N0"));
        }

        sb.Append('\n');
        sb.Append("  source: ").Append(report.Source).Append('\n');
        sb.Append("  clean shutdown: ").Append(id.CleanShutdown ? "yes" : p.Warn + "NO  (the last close did not set the clean flag)" + p.Reset).Append('\n');
        if (id.WalSegmentCount > 0)
        {
            sb.Append("  write-ahead log: ").Append(id.WalSegmentCount).Append(" segment(s), ").Append(FormatBytes(id.WalBytes)).Append('\n');
        }

        sb.Append('\n');
    }

    private static void RenderVerdict(StringBuilder sb, IntegrityReport report, Palette p)
    {
        var colour = report.Verdict switch
        {
            IntegrityVerdict.Sound => p.Ok,
            IntegrityVerdict.SoundWithLeaks => p.Ok,
            IntegrityVerdict.Divergent => p.Warn,
            _ => p.Bad
        };

        sb.Append("  ").Append(p.Bold).Append("VERDICT: ").Append(colour).Append(report.Verdict.ToString().ToUpperInvariant()).Append(p.Reset);

        var count = report.Findings.Count;
        sb.Append(count == 0 ? "                     no findings" : $"{new string(' ', Math.Max(1, 34 - report.Verdict.ToString().Length))}{count} finding{(count == 1 ? "" : "s")}");
        sb.Append("\n\n");
    }

    private static void RenderFindings(StringBuilder sb, IntegrityReport report, Palette p)
    {
        if (report.Findings.Count == 0)
        {
            return;
        }

        for (var i = 0; i < report.Findings.Count; i++)
        {
            var f = report.Findings[i];
            sb.Append("  ").Append(Glyph(f.Severity, p)).Append(' ');
            sb.Append(f.Severity.ToString().PadRight(11));
            sb.Append(f.Code.PadRight(13));
            sb.Append(f.Summary);
            if (f.Occurrences > 1)
            {
                sb.Append(p.Dim).Append("  (×").Append(f.Occurrences.ToString("N0")).Append(')').Append(p.Reset);
            }

            sb.Append('\n');

            if (f.Confidence == IntegrityConfidence.Suspected)
            {
                sb.Append("                              ").Append(p.Dim).Append("SUSPECTED — observed on a live database; re-run offline to confirm.").Append(p.Reset).Append('\n');
            }

            AppendWrapped(sb, f.Detail, "                              ", p.Dim, p.Reset);

            var trailer = new List<string>(3);
            if (f.RuleId.Length > 0)
            {
                trailer.Add(f.RuleId);
            }

            trailer.Add(f.Repair switch
            {
                Repairability.Lossless => "rebuildable, no loss",
                Repairability.Lossy => "repairable with loss",
                _ => "not repairable from within this database"
            });

            sb.Append("                              → ").Append(string.Join("  ·  ", trailer)).Append('\n').Append('\n');
        }
    }

    private static void RenderLoss(StringBuilder sb, IntegrityReport report, Palette p)
    {
        var losses = report.LossSummary;
        if (losses.Count == 0)
        {
            return;
        }

        sb.Append("  ").Append(p.Bold).Append("LOSS IF REPAIRED").Append(p.Reset).Append('\n');
        for (var i = 0; i < losses.Count; i++)
        {
            var l = losses[i];
            sb.Append("    ").Append(l.CountText).Append(' ');
            sb.Append(l.Kind switch
            {
                LossKind.Values => "component value(s)",
                LossKind.Entities => "entities",
                LossKind.Collection => "collection element(s)",
                LossKind.Strings => "interned string(s)",
                _ => "row(s), unit undetermined"
            });

            if (l.Archetype != null)
            {
                sb.Append(" in ").Append(l.Archetype);
                if (l.Component != null)
                {
                    sb.Append('.').Append(l.Component);
                }
            }

            sb.Append('\n');
            AppendWrapped(sb, l.Explanation, "      ", p.Dim, p.Reset);
        }

        sb.Append('\n');
    }

    private static void RenderLimits(StringBuilder sb, IntegrityReport report, Palette p)
    {
        sb.Append("  ").Append(p.Bold).Append("LIMITS OF THIS SCAN").Append(p.Reset).Append('\n');
        AppendWrapped(sb, ScanLimits.StructuralLimit, "    ", p.Dim, p.Reset);

        var limits = report.Limits;
        for (var i = 0; i < limits.Caveats.Count; i++)
        {
            AppendWrapped(sb, "· " + limits.Caveats[i], "    ", p.Dim, p.Reset);
        }

        if (limits.ChecksSkipped.Count > 0)
        {
            AppendWrapped(sb, "· Not run at this depth: " + string.Join("; ", limits.ChecksSkipped), "    ", p.Dim, p.Reset);
        }

        sb.Append('\n');
    }

    private static void RenderFooter(StringBuilder sb, IntegrityReport report, Palette p)
    {
        var t = report.Totals;
        sb.Append("  ").Append(p.Dim);
        sb.Append(t.PagesScanned.ToString("N0")).Append(" pages scanned · ");
        sb.Append(t.PagesAllocated.ToString("N0")).Append(" allocated · ");
        sb.Append(t.SegmentsWalked.ToString("N0")).Append(" segments");
        if (t.PagesWithSectorFooters > 0)
        {
            sb.Append(" · ").Append(t.PagesWithSectorFooters.ToString("N0")).Append(" pages with per-sector verification");
        }

        sb.Append(" · ").Append(report.Duration.TotalMilliseconds.ToString("N0")).Append(" ms").Append(p.Reset).Append('\n');

        if (!report.IsSound)
        {
            sb.Append('\n').Append("  Next: typhon repair ").Append(report.Source).Append(" --plan\n");
        }

        sb.Append('\n');
    }

    private static string Glyph(IntegritySeverity severity, Palette p) => severity switch
    {
        IntegritySeverity.Fatal => p.Bad + "✖" + p.Reset,
        IntegritySeverity.DataLoss => p.Bad + "✖" + p.Reset,
        IntegritySeverity.Divergence => p.Warn + "▲" + p.Reset,
        IntegritySeverity.Leak => p.Dim + "·" + p.Reset,
        _ => p.Dim + "i" + p.Reset
    };

    private static void AppendWrapped(StringBuilder sb, string text, string indent, string open, string close)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        const int width = 100;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder(width);

        for (var i = 0; i < words.Length; i++)
        {
            if (line.Length > 0 && line.Length + 1 + words[i].Length > width)
            {
                sb.Append(indent).Append(open).Append(line).Append(close).Append('\n');
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(words[i]);
        }

        if (line.Length > 0)
        {
            sb.Append(indent).Append(open).Append(line).Append(close).Append('\n');
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N1} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):N1} KiB",
        _ => $"{bytes} B"
    };

    private readonly struct Palette
    {
        public Palette(bool enabled)
        {
            Bold = enabled ? "\u001b[1m" : "";
            Dim = enabled ? "\u001b[2m" : "";
            Ok = enabled ? "\u001b[32m" : "";
            Warn = enabled ? "\u001b[33m" : "";
            Bad = enabled ? "\u001b[31m" : "";
            Reset = enabled ? "\u001b[0m" : "";
        }

        public string Bold { get; }
        public string Dim { get; }
        public string Ok { get; }
        public string Warn { get; }
        public string Bad { get; }
        public string Reset { get; }
    }
}
