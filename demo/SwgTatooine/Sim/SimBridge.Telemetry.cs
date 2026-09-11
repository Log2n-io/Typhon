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
            _telemetrySums[a, 6] += Dbe._archetypeStates[_telemetryIds[a]]?.ClusterState?.DriftTargetBoost ?? 0f;
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
    }
}
