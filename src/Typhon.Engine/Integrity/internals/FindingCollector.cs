using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// Accumulates findings while keeping a report readable on a database where one systemic fault is individually true a
/// million times.
/// </summary>
/// <remarks>
/// Per check code, the first <see cref="PerCodeDetailLimit"/> occurrences are reported in full — each with its own
/// <see cref="Locus"/>, which is what makes a finding actionable. Beyond that the collector stops enumerating and starts
/// counting, and the surviving finding carries the true total in <see cref="IntegrityFinding.Occurrences"/>. A truncated
/// enumeration is always disclosed in the report's <see cref="ScanLimits.Caveats"/> — silently dropping occurrences would
/// make a catastrophic database look like a mildly damaged one.
/// </remarks>
internal sealed class FindingCollector
{
    /// <summary>Distinct loci reported per check code before the collector switches to counting.</summary>
    public const int PerCodeDetailLimit = 50;

    private readonly List<IntegrityFinding> _findings = [];
    private readonly Dictionary<string, CodeState> _byCode = new(StringComparer.Ordinal);
    private readonly IntegrityOptions _options;
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);
    private readonly List<string> _caveats = [];

    private sealed class CodeState
    {
        public int Detailed;
        public long Total;
        public IntegrityFinding Representative;
    }

    /// <summary>Creates a collector bound to a scan's options.</summary>
    /// <param name="options">The scan options, which bound how much is recorded.</param>
    public FindingCollector(IntegrityOptions options) => _options = options;

    /// <summary>Whether the collector has reached the scan's global finding cap.</summary>
    public bool IsSaturated => _findings.Count >= _options.MaxFindings;

    /// <summary>Extra caveats to surface in the report's limits block.</summary>
    public IReadOnlyList<string> Caveats => _caveats;

    /// <summary>Check codes that were filtered out or not applicable at this depth.</summary>
    public IReadOnlyList<string> Skipped
    {
        get
        {
            var list = new List<string>(_skipped);
            list.Sort(StringComparer.Ordinal);
            return list;
        }
    }

    /// <summary>Records that a check did not run.</summary>
    /// <param name="code">The check code.</param>
    /// <param name="reason">Why it did not run — appended to the code for the report.</param>
    public void NoteSkipped(string code, string reason) => _skipped.Add(reason == null ? code : $"{code} ({reason})");

    /// <summary>Records a scan-specific caveat for the limits block.</summary>
    /// <param name="caveat">The caveat text.</param>
    public void NoteCaveat(string caveat)
    {
        if (!_caveats.Contains(caveat))
        {
            _caveats.Add(caveat);
        }
    }

    /// <summary>
    /// Adds a finding, collapsing repeats of the same code past the detail limit into an occurrence count.
    /// </summary>
    /// <param name="finding">The finding to record.</param>
    public void Add(IntegrityFinding finding)
    {
        if (!_byCode.TryGetValue(finding.Code, out var state))
        {
            state = new CodeState();
            _byCode[finding.Code] = state;
        }

        state.Total++;

        if (state.Detailed < PerCodeDetailLimit && !IsSaturated)
        {
            state.Detailed++;
            state.Representative ??= finding;
            _findings.Add(finding);
            return;
        }

        if (state.Detailed == PerCodeDetailLimit)
        {
            state.Detailed++;   // one-shot: only note the truncation once per code
            NoteCaveat($"{finding.Code}: only the first {PerCodeDetailLimit} occurrences are enumerated; the rest are counted.");
        }
    }

    /// <summary>
    /// Produces the final severity-ranked list, stamping each code's representative finding with the true occurrence count.
    /// </summary>
    public IReadOnlyList<IntegrityFinding> Build()
    {
        var result = new List<IntegrityFinding>(_findings.Count);
        var stamped = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < _findings.Count; i++)
        {
            var f = _findings[i];
            var total = _byCode[f.Code].Total;
            if (total > 1 && stamped.Add(f.Code))
            {
                result.Add(new IntegrityFinding
                {
                    Code = f.Code,
                    Severity = f.Severity,
                    Confidence = f.Confidence,
                    Locus = f.Locus,
                    Summary = f.Summary,
                    Detail = f.Detail,
                    RuleId = f.RuleId,
                    Repair = f.Repair,
                    Loss = f.Loss,
                    Occurrences = total
                });
                continue;
            }

            result.Add(f);
        }

        result.Sort(static (x, y) =>
        {
            var bySeverity = ((int)x.Severity).CompareTo((int)y.Severity);
            if (bySeverity != 0)
            {
                return bySeverity;
            }

            var byCode = string.CompareOrdinal(x.Code, y.Code);
            return byCode != 0 ? byCode : x.Locus.FilePageIndex.CompareTo(y.Locus.FilePageIndex);
        });

        return result;
    }

    /// <summary>Total occurrences recorded for a check code, including ones collapsed past the detail limit.</summary>
    /// <param name="code">The check code.</param>
    public long CountFor(string code) => _byCode.TryGetValue(code, out var s) ? s.Total : 0;

    /// <summary>Counts findings by severity, for the report totals.</summary>
    public IReadOnlyList<int> SeverityHistogram()
    {
        var counts = new int[5];
        foreach (var state in _byCode.Values)
        {
            if (state.Representative != null)
            {
                counts[(int)state.Representative.Severity] += (int)Math.Min(state.Total, int.MaxValue);
            }
        }

        return counts;
    }
}
