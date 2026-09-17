using System;
using System.Collections.Generic;

namespace SwgTatooine;

/// <summary>
/// The fence's intra-cell maintenance per archetype, averaged over the measured window. The census's "last tick" table is one tick of hundreds; an A/B
/// that changes how much drift and repair the fence does has to read the whole window.
/// </summary>
public sealed partial class SimBridge
{
    private static readonly string[] TelemetryNames = ["WorldObject", "CreatureLair", "Creature", "CityNpc", "Player"];
    private int[] _telemetryIds;
    private readonly double[,] _telemetrySums = new double[5, 7];

    /// <summary>
    /// Prep's split per archetype: the nine sub-spans in ms, the clusters Prep masked, the ticks spent on each branch, and the repair queue's own
    /// maintenance — absorbing nominations and re-ranking (AC-11.5). That last term is reported separately from "plan" because "plan" is the whole
    /// planner, execution included, and the question #949 asks is about the rank alone.
    /// </summary>
    private readonly double[,] _prepSums = new double[5, 13];

    /// <summary>
    /// The engine's query tally per archetype — clusters the range queries opened, entities they tested, matches — then the budget the controller granted
    /// and its smoothed and best candidates per hit.
    /// </summary>
    private readonly double[,] _querySums = new double[5, 6];

    /// <summary>Candidates and hits over every archetype, per window of the measured run, oldest first: do queries test more per match as it goes?</summary>
    private readonly long[,] _queryWindows = new long[QueryWindows, 2];

    private const int QueryWindows = 16;

    /// <summary>Per archetype and window: candidates, hits and budget granted. The controller works per archetype; the pooled line averages it away.</summary>
    private readonly double[,,] _archetypeWindows = new double[5, QueryWindows, 3];

    /// <summary>Ticks folded into each window, for the per-window mean of the budget granted.</summary>
    private readonly int[] _windowTicks = new int[QueryWindows];

    private long _telemetryTicks;

    /// <summary>Report phase, every tick past warm-up: fold the previous fence's per-archetype counters.</summary>
    public void SpatialTelemetryTick(TickContext ctx)
    {
        // Measured ticks only. The run polls for its last tick and shuts down after it, and the ticks started in between (always one or two when unpaced)
        // used to land in a trailing window of their own, which every reader took for the last window.
        if (ctx.TickNumber < _config.WarmTicks || ctx.TickNumber >= _config.WarmTicks + _config.MeasuredTicks)
        {
            return;
        }

        _telemetryIds ??=
        [
            Archetype<WorldObject>.Metadata.ArchetypeId, Archetype<CreatureLair>.Metadata.ArchetypeId, Archetype<Creature>.Metadata.ArchetypeId,
            Archetype<CityNpc>.Metadata.ArchetypeId, Archetype<Player>.Metadata.ArchetypeId,
        ];
        var w = (int)Math.Min(QueryWindows - 1, (ctx.TickNumber - _config.WarmTicks) / QueryWindowTicks);
        for (var a = 0; a < _telemetryIds.Length; a++)
        {
            var t = Dbe.GetSpatialTelemetry(_telemetryIds[a]);
            _telemetrySums[a, 0] += t.DriftGatedClusters;
            _telemetrySums[a, 1] += t.DriftersDetected;
            _telemetrySums[a, 2] += t.RelocationsAdmitted;
            _telemetrySums[a, 3] += t.RepairedEntityCount;
            _telemetrySums[a, 4] += t.RepairUnitCount;
            _telemetrySums[a, 5] += t.MigrationCount;
            var clusterState = Dbe._archetypeStates[_telemetryIds[a]]?.ClusterState;
            _telemetrySums[a, 6] += clusterState?.DriftTargetBoost ?? 0f;

            _prepSums[a, 0] += t.PrepSnapshotMs;
            _prepSums[a, 1] += t.PrepMaskMs;
            _prepSums[a, 2] += t.PrepShadowMs;
            _prepSums[a, 3] += t.PrepZoneMapMs;
            _prepSums[a, 4] += t.PrepDetectMs;
            _prepSums[a, 5] += t.PrepThrottleMs;
            _prepSums[a, 6] += t.PrepPlanMs;
            _prepSums[a, 7] += t.PrepSortMs;
            _prepSums[a, 8] += t.PrepPreSizeMs;
            _prepSums[a, 9] += t.PrepDirtyClusters;
            var branch = clusterState?.FenceBranchPath ?? 0;
            _prepSums[a, 10] += branch == 1 ? 1 : 0;
            _prepSums[a, 11] += branch == 2 ? 1 : 0;
            _prepSums[a, 12] += t.RepairQueueMaintenanceMs;

            _querySums[a, 0] += t.QueryClustersOpened;
            _querySums[a, 1] += t.QueryCandidates;
            _querySums[a, 2] += t.QueryHits;
            _querySums[a, 3] += t.ReclusterBudgetGrantedMs;
            _querySums[a, 4] += t.QueryCandidatesPerHitSmoothed;
            _querySums[a, 5] += t.QueryCandidatesPerHitBest;
            _queryWindows[w, 0] += t.QueryCandidates;
            _queryWindows[w, 1] += t.QueryHits;
            _archetypeWindows[a, w, 0] += t.QueryCandidates;
            _archetypeWindows[a, w, 1] += t.QueryHits;
            _archetypeWindows[a, w, 2] += t.ReclusterBudgetGrantedMs;
        }

        _windowTicks[w]++;
        _telemetryTicks++;
    }

    /// <summary>The same window as the awareness chunk statistics' per-window line: a tenth of the measured run, at least 100 ticks.</summary>
    private long QueryWindowTicks => Math.Max(100, _config.MeasuredTicks / 10);

    public void PrintSpatialTelemetry()
    {
        if (_telemetryTicks == 0 || _telemetryIds == null)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  {"per tick (mean)",-15} {"gated",8} {"drifters",9} {"reloc adm",10} {"repaired",9} {"units",7} {"migrations",11} "
            + $"{"boost",6}   over {_telemetryTicks} ticks");
        for (var a = 0; a < _telemetryIds.Length; a++)
        {
            double M(int k) => _telemetrySums[a, k] / _telemetryTicks;
            Console.WriteLine($"  {TelemetryNames[a],-15} {M(0),8:F1} {M(1),9:F1} {M(2),10:F1} {M(3),9:F1} {M(4),7:F2} {M(5),11:F1} {M(6),6:F2}");
        }

        // Wall time on the one worker that ran the archetype's Prep item: the sum is that item's timed part, and the planner is "plan". "qmaint" brackets
        // the repair queue's cooldown release, absorb, source-exclusion rebuild and rank — so it is what a starved budget skips, but it is NOT the rank
        // alone and a movement in it cannot be attributed to the sort by itself.
        Console.WriteLine();
        Console.WriteLine($"  {"prep ms (mean)",-15} {"snap",6} {"mask",6} {"shadow",6} {"zmap",6} {"detect",6} {"throt",6} {"plan",6} {"sort",6} {"presz",6} "
            + $"{"sum",7} {"qmaint",7} {"dirty cl",9} {"branch 1/2",11}");
        for (var a = 0; a < _telemetryIds.Length; a++)
        {
            double P(int k) => _prepSums[a, k] / _telemetryTicks;
            var sum = 0d;
            for (var k = 0; k < 9; k++)
            {
                sum += P(k);
            }

            Console.WriteLine($"  {TelemetryNames[a],-15} {P(0),6:F3} {P(1),6:F3} {P(2),6:F3} {P(3),6:F3} {P(4),6:F3} {P(5),6:F3} {P(6),6:F3} {P(7),6:F3} "
                + $"{P(8),6:F3} {sum,7:F3} {P(12),7:F4} {P(9),9:F0} {P(10),5:F2}/{P(11),-5:F2}");
        }

        // The engine's query tally: per tick, and as ratios of the window's sums rather than means of per-tick ratios.
        Console.WriteLine();
        Console.WriteLine($"  {"queries (mean)",-15} {"clusters",10} {"candidates",12} {"hits",11} {"cand/hit",9} {"cand/cluster",13} {"granted ms",11} "
            + $"{"smoothed",9} {"best",7}");
        for (var a = 0; a < _telemetryIds.Length; a++)
        {
            double Q(int k) => _querySums[a, k] / _telemetryTicks;
            Console.WriteLine($"  {TelemetryNames[a],-15} {Q(0),10:F0} {Q(1),12:F0} {Q(2),11:F0} {Ratio(Q(1), Q(2)),9:F3} {Ratio(Q(1), Q(0)),13:F2} "
                + $"{Q(3),11:F3} {Q(4),9:F3} {Q(5),7:F3}");
        }

        var lastWindow = -1;
        for (var w = 0; w < QueryWindows; w++)
        {
            if (_queryWindows[w, 0] > 0)
            {
                lastWindow = w;
            }
        }

        var perWindow = new List<string>();
        for (var w = 0; w <= lastWindow; w++)
        {
            perWindow.Add($"{Ratio(_queryWindows[w, 0], _queryWindows[w, 1]):F3}");
        }

        Console.WriteLine($"  cand/hit by {QueryWindowTicks}-tick window: {string.Join(" ", perWindow)}");

        // Per archetype, after a blank line so the queries table above still ends where its parsers expect.
        Console.WriteLine();
        Console.WriteLine($"  per {QueryWindowTicks}-tick window: cand/hit | budget granted ms");
        for (var a = 0; a < _telemetryIds.Length; a++)
        {
            if (_querySums[a, 1] <= 0)
            {
                continue;
            }

            var cph = new List<string>();
            var granted = new List<string>();
            for (var w = 0; w <= lastWindow; w++)
            {
                cph.Add($"{Ratio(_archetypeWindows[a, w, 0], _archetypeWindows[a, w, 1]):F3}");
                granted.Add($"{Ratio(_archetypeWindows[a, w, 2], _windowTicks[w]):F3}");
            }

            Console.WriteLine($"  {TelemetryNames[a],-15} {string.Join(" ", cph)} | {string.Join(" ", granted)}");
        }
    }

    private static double Ratio(double numerator, double denominator) => denominator > 0 ? numerator / denominator : 0d;
}
