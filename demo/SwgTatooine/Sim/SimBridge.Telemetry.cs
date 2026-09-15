using System;

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

    /// <summary>Prep's split per archetype: the nine sub-spans in ms, the clusters Prep masked, and the ticks spent on each branch.</summary>
    private readonly double[,] _prepSums = new double[5, 12];
    private long _telemetryTicks;

    /// <summary>Report phase, every tick past warm-up: fold the previous fence's per-archetype counters.</summary>
    public void SpatialTelemetryTick(TickContext ctx)
    {
        if (ctx.TickNumber < _config.WarmTicks)
        {
            return;
        }

        _telemetryIds ??=
        [
            Archetype<WorldObject>.Metadata.ArchetypeId, Archetype<CreatureLair>.Metadata.ArchetypeId, Archetype<Creature>.Metadata.ArchetypeId,
            Archetype<CityNpc>.Metadata.ArchetypeId, Archetype<Player>.Metadata.ArchetypeId,
        ];
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
        }

        _telemetryTicks++;
    }

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

        // Wall time on the one worker that ran the archetype's Prep item: the sum is that item's timed part, and the planner is "plan".
        Console.WriteLine();
        Console.WriteLine($"  {"prep ms (mean)",-15} {"snap",6} {"mask",6} {"shadow",6} {"zmap",6} {"detect",6} {"throt",6} {"plan",6} {"sort",6} {"presz",6} "
            + $"{"sum",7} {"dirty cl",9} {"branch 1/2",11}");
        for (var a = 0; a < _telemetryIds.Length; a++)
        {
            double P(int k) => _prepSums[a, k] / _telemetryTicks;
            var sum = 0d;
            for (var k = 0; k < 9; k++)
            {
                sum += P(k);
            }

            Console.WriteLine($"  {TelemetryNames[a],-15} {P(0),6:F3} {P(1),6:F3} {P(2),6:F3} {P(3),6:F3} {P(4),6:F3} {P(5),6:F3} {P(6),6:F3} {P(7),6:F3} "
                + $"{P(8),6:F3} {sum,7:F3} {P(9),9:F0} {P(10),5:F2}/{P(11),-5:F2}");
        }
    }
}
