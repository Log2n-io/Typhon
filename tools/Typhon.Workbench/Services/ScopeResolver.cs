using System;
using System.Collections.Generic;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Services;

/// <summary>
/// Resolves a <see cref="CallTreeRequestDto"/>'s composite scope into the concrete set of QPC time-windows the call-tree fold
/// filters against (#351 Phase 5, §8.3). A scope is exactly one axis, in this precedence: span-kind ▸ system ▸ phase ▸ manual
/// range ▸ whole session. Runs server-side at request time, off any engine hot path.
/// </summary>
/// <remarks>
/// The returned interval set is <b>sorted, merged and disjoint</b>. Two sentinels matter: <see cref="WholeSession"/> (a single
/// all-covering window — "no filtering") and the empty array (a scope that resolves to nothing — e.g. a system that never ran;
/// the fold then yields an empty tree). QPC math mirrors the Phase-4 fold: <c>qpc = µs · frequency / 1e6</c>.
/// </remarks>
public static class ScopeResolver
{
    /// <summary>The "scope to everything" interval set — one window spanning all representable QPC values.</summary>
    public static readonly (long Start, long End)[] WholeSession = [(long.MinValue, long.MaxValue)];

    /// <summary>
    /// Resolves <paramref name="request"/>'s scope to a sorted, merged, disjoint QPC interval set. Returns
    /// <see cref="WholeSession"/> when no scope axis is set (or when a µs-based axis is requested but
    /// <paramref name="timestampFrequency"/> is unusable); an empty array when the chosen axis resolves to no window.
    /// </summary>
    /// <param name="request">The composite scope request.</param>
    /// <param name="systems">The trace's system definitions — for the phase → systems lookup.</param>
    /// <param name="tickSummaries">Per-tick summaries — supply the absolute start of each tick.</param>
    /// <param name="systemTickSummaries">Per-(system, tick) rollups — the system/phase execution windows.</param>
    /// <param name="spanIndex">
    /// Lazy accessor for the per-session span-instance index — supplies span-kind windows. Invoked <i>only</i> for a
    /// span-kind scope, so a whole-session / system / phase / range request never pays the index's chunk-walk build.
    /// </param>
    /// <param name="timestampFrequency">QPC ticks per second — converts the µs-based axes to QPC.</param>
    public static (long Start, long End)[] Resolve(
        CallTreeRequestDto request,
        SystemDefinitionDto[] systems,
        TickSummaryDto[] tickSummaries,
        SystemTickSummary[] systemTickSummaries,
        Func<SpanInstanceIndex> spanIndex,
        long timestampFrequency)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(spanIndex);

        // span-kind — windows are already in QPC, no frequency needed.
        if (request.SpanKind.HasValue)
        {
            return Merge(spanIndex().WindowsForKind(request.SpanKind.Value));
        }

        // The remaining axes are all µs-based — a missing frequency means we cannot convert, so degrade to whole-session
        // rather than silently dropping every sample.
        if (request.SystemIndex.HasValue)
        {
            return timestampFrequency > 0
                ? Merge(SystemWindows(systems: null, request.SystemIndex.Value, tickSummaries, systemTickSummaries, timestampFrequency))
                : WholeSession;
        }

        if (!string.IsNullOrEmpty(request.Phase))
        {
            return timestampFrequency > 0
                ? Merge(PhaseWindows(request.Phase, systems, tickSummaries, systemTickSummaries, timestampFrequency))
                : WholeSession;
        }

        if (request.StartUs.HasValue || request.EndUs.HasValue)
        {
            if (timestampFrequency <= 0)
            {
                return WholeSession;
            }
            var start = request.StartUs.HasValue ? UsToQpc(request.StartUs.Value, timestampFrequency) : long.MinValue;
            var end = request.EndUs.HasValue ? UsToQpc(request.EndUs.Value, timestampFrequency) : long.MaxValue;
            return end >= start ? [(start, end)] : [];
        }

        return WholeSession;
    }

    /// <summary>
    /// The execution windows of one system (when <paramref name="systems"/> is <c>null</c>) or of a set of systems
    /// (a phase). The per-row <c>StartUs</c>/<c>EndUs</c> of a <see cref="SystemTickSummary"/> are <i>tick-relative</i> —
    /// the absolute window adds the owning tick's <c>StartUs</c>. Skipped rows (<c>SkipReasonCode != 0</c>) are dropped.
    /// </summary>
    private static List<(long Start, long End)> SystemWindows(
        HashSet<int> systems,
        int systemIndex,
        TickSummaryDto[] tickSummaries,
        SystemTickSummary[] systemTickSummaries,
        long frequency)
    {
        var windows = new List<(long Start, long End)>();
        var tickStartUs = BuildTickStartMap(tickSummaries);
        foreach (var s in systemTickSummaries)
        {
            if (s.SkipReasonCode != 0)
            {
                continue;
            }
            var matches = systems != null ? systems.Contains(s.SystemIndex) : s.SystemIndex == systemIndex;
            if (!matches)
            {
                continue;
            }
            if (!tickStartUs.TryGetValue(s.TickNumber, out var tickStart))
            {
                continue;
            }
            var startUs = tickStart + s.StartUs;
            var endUs = tickStart + s.EndUs;
            if (endUs > startUs)
            {
                windows.Add((UsToQpc(startUs, frequency), UsToQpc(endUs, frequency)));
            }
        }
        return windows;
    }

    /// <summary>The union of the execution windows of every system assigned to <paramref name="phase"/>.</summary>
    private static List<(long Start, long End)> PhaseWindows(
        string phase,
        SystemDefinitionDto[] systems,
        TickSummaryDto[] tickSummaries,
        SystemTickSummary[] systemTickSummaries,
        long frequency)
    {
        var phaseSystems = new HashSet<int>();
        foreach (var sys in systems)
        {
            if (string.Equals(sys.PhaseName, phase, StringComparison.Ordinal))
            {
                phaseSystems.Add(sys.Index);
            }
        }
        return phaseSystems.Count == 0
            ? []
            : SystemWindows(phaseSystems, systemIndex: -1, tickSummaries, systemTickSummaries, frequency);
    }

    private static Dictionary<uint, double> BuildTickStartMap(TickSummaryDto[] tickSummaries)
    {
        var map = new Dictionary<uint, double>(tickSummaries.Length);
        foreach (var t in tickSummaries)
        {
            map[t.TickNumber] = t.StartUs;
        }
        return map;
    }

    private static long UsToQpc(double us, long frequency) => (long)(us * frequency / 1_000_000.0);

    /// <summary>Sorts windows by start and folds overlapping or touching ones into a disjoint set.</summary>
    private static (long Start, long End)[] Merge(IReadOnlyList<(long Start, long End)> windows)
    {
        if (windows.Count == 0)
        {
            return [];
        }

        var sorted = new (long Start, long End)[windows.Count];
        for (var i = 0; i < windows.Count; i++)
        {
            sorted[i] = windows[i];
        }
        Array.Sort(sorted, static (a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<(long Start, long End)>();
        var cur = sorted[0];
        for (var i = 1; i < sorted.Length; i++)
        {
            var next = sorted[i];
            if (next.Start <= cur.End)
            {
                if (next.End > cur.End)
                {
                    cur.End = next.End;
                }
            }
            else
            {
                merged.Add(cur);
                cur = next;
            }
        }
        merged.Add(cur);
        return merged.ToArray();
    }
}
