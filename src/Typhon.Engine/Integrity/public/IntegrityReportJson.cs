using JetBrains.Annotations;
using System;
using System.Globalization;
using System.Text;

namespace Typhon.Engine;

/// <summary>
/// Renders an <see cref="IntegrityReport"/> as JSON for CI gates, scripts and the Workbench.
/// </summary>
/// <remarks>
/// The schema is versioned (<see cref="IntegrityReport.ReportVersion"/>) and finding codes are stable, because
/// <b>a finding code is an API</b> — renaming one breaks somebody's alert. Written by hand rather than through a
/// serializer so the shape is visible in one place and cannot drift when a property is added to a model type.
/// </remarks>
[PublicAPI]
public static class IntegrityReportJson
{
    /// <summary>Renders the report as JSON.</summary>
    /// <param name="report">The report to render.</param>
    /// <param name="indent">Whether to pretty-print.</param>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <c>null</c>.</exception>
    public static string Render(IntegrityReport report, bool indent = true)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder(8192);
        var w = new Writer(sb, indent);

        w.BeginObject();
        w.Prop("reportVersion", IntegrityReport.ReportVersion);
        w.Prop("verdict", report.Verdict.ToString());
        w.Prop("exitCode", report.ExitCode);
        w.Prop("source", report.Source);
        w.Prop("mode", report.Mode.ToString());
        w.Prop("depth", report.Depth.ToString());
        w.Prop("completedUtc", report.CompletedUtc.ToString("O", CultureInfo.InvariantCulture));
        w.Prop("durationMs", report.Duration.TotalMilliseconds);

        w.Key("identity");
        w.BeginObject();
        var id = report.Identity;
        w.Prop("name", id.Name);
        w.Prop("formatRevision", id.FormatRevision);
        w.Prop("pageCount", id.PageCount);
        w.Prop("sizeBytes", id.SizeBytes);
        w.Prop("checkpointLsn", id.CheckpointLsn);
        w.Prop("cleanShutdown", id.CleanShutdown);
        w.Prop("metaSlot", id.MetaSlot);
        w.Prop("metaGeneration", (long)id.MetaGeneration);
        w.Prop("walSegmentCount", id.WalSegmentCount);
        w.Prop("walBytes", id.WalBytes);
        w.EndObject();

        w.Key("totals");
        w.BeginObject();
        var t = report.Totals;
        w.Prop("pagesScanned", t.PagesScanned);
        w.Prop("pagesAllocated", t.PagesAllocated);
        w.Prop("checksumFailures", t.ChecksumFailures);
        w.Prop("pagesWithSectorFooters", t.PagesWithSectorFooters);
        w.Prop("sectorFailures", t.SectorFailures);
        w.Prop("segmentsWalked", t.SegmentsWalked);
        w.Prop("bytesLeaked", t.BytesLeaked);
        w.EndObject();

        w.Key("findings");
        w.BeginArray();
        for (var i = 0; i < report.Findings.Count; i++)
        {
            var f = report.Findings[i];
            w.BeginObject();
            w.Prop("code", f.Code);
            w.Prop("severity", f.Severity.ToString());
            w.Prop("confidence", f.Confidence.ToString());
            w.Prop("summary", f.Summary);
            w.Prop("detail", f.Detail);
            w.Prop("ruleId", f.RuleId);
            w.Prop("repair", f.Repair.ToString());
            w.Prop("occurrences", f.Occurrences);

            w.Key("locus");
            w.BeginObject();
            w.Prop("filePageIndex", f.Locus.FilePageIndex);
            w.Prop("segmentRootPage", f.Locus.SegmentRootPage);
            w.Prop("kind", f.Locus.Kind.ToString());
            w.Prop("archetype", f.Locus.ArchetypeName);
            w.Prop("component", f.Locus.ComponentName);
            w.Prop("chunkId", f.Locus.ChunkId);
            w.Prop("slot", f.Locus.Slot);
            w.Prop("entityId", (long)f.Locus.EntityId);
            w.Prop("text", f.Locus.ToString());
            w.EndObject();

            w.Key("loss");
            WriteLoss(w, f.Loss);
            w.EndObject();
        }

        w.EndArray();

        w.Key("lossSummary");
        w.BeginArray();
        var losses = report.LossSummary;
        for (var i = 0; i < losses.Count; i++)
        {
            WriteLoss(w, losses[i]);
        }

        w.EndArray();

        w.Key("limits");
        w.BeginObject();
        w.Prop("structural", ScanLimits.StructuralLimit);
        w.Key("checksSkipped");
        w.BeginArray();
        for (var i = 0; i < report.Limits.ChecksSkipped.Count; i++)
        {
            w.Value(report.Limits.ChecksSkipped[i]);
        }

        w.EndArray();
        w.Key("caveats");
        w.BeginArray();
        for (var i = 0; i < report.Limits.Caveats.Count; i++)
        {
            w.Value(report.Limits.Caveats[i]);
        }

        w.EndArray();
        w.EndObject();

        w.EndObject();
        return sb.ToString();
    }

    private static void WriteLoss(Writer w, LossEstimate loss)
    {
        w.BeginObject();
        w.Prop("kind", loss.Kind.ToString());
        w.Prop("entityCount", loss.EntityCount);
        w.Prop("boundedMin", loss.BoundedMin);
        w.Prop("boundedMax", loss.BoundedMax);
        w.Prop("archetype", loss.Archetype);
        w.Prop("component", loss.Component);
        w.Prop("explanation", loss.Explanation);
        w.Key("sample");
        w.BeginArray();
        for (var i = 0; i < loss.Sample.Count; i++)
        {
            w.Value((long)loss.Sample[i]);
        }

        w.EndArray();
        w.EndObject();
    }

    /// <summary>
    /// A minimal JSON writer. Deliberately hand-rolled: the report shape is a published contract, and keeping it in one
    /// readable block is worth more than the convenience of reflection-driven serialization that changes shape whenever a
    /// model property is added.
    /// </summary>
    private sealed class Writer
    {
        private readonly StringBuilder _sb;
        private readonly bool _indent;
        private int _depth;
        private bool _needComma;

        public Writer(StringBuilder sb, bool indent)
        {
            _sb = sb;
            _indent = indent;
        }

        public void BeginObject()
        {
            Separate();
            _sb.Append('{');
            _depth++;
            _needComma = false;
        }

        public void EndObject()
        {
            _depth--;
            NewLine();
            _sb.Append('}');
            _needComma = true;
        }

        public void BeginArray()
        {
            Separate();
            _sb.Append('[');
            _depth++;
            _needComma = false;
        }

        public void EndArray()
        {
            _depth--;
            NewLine();
            _sb.Append(']');
            _needComma = true;
        }

        public void Key(string name)
        {
            Separate();
            AppendString(name);
            _sb.Append(_indent ? ": " : ":");
            _needComma = false;
        }

        public void Prop(string name, string value)
        {
            Key(name);
            if (value == null)
            {
                _sb.Append("null");
            }
            else
            {
                AppendString(value);
            }

            _needComma = true;
        }

        public void Prop(string name, long value)
        {
            Key(name);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            _needComma = true;
        }

        public void Prop(string name, double value)
        {
            Key(name);
            _sb.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            _needComma = true;
        }

        public void Prop(string name, bool value)
        {
            Key(name);
            _sb.Append(value ? "true" : "false");
            _needComma = true;
        }

        public void Value(string value)
        {
            Separate();
            AppendString(value);
            _needComma = true;
        }

        public void Value(long value)
        {
            Separate();
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            _needComma = true;
        }

        private void Separate()
        {
            if (_needComma)
            {
                _sb.Append(',');
            }

            NewLine();
        }

        private void NewLine()
        {
            if (!_indent)
            {
                return;
            }

            _sb.Append('\n');
            _sb.Append(' ', _depth * 2);
        }

        private void AppendString(string value)
        {
            _sb.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            _sb.Append(c);
                        }

                        break;
                }
            }

            _sb.Append('"');
        }
    }
}
