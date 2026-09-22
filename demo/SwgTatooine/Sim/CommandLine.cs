using System;
using System.Globalization;

namespace SwgTatooine;

/// <summary>Argument parsing for the simulation driver. Every knob in <see cref="SimConfig"/> that a sweep moves.</summary>
public static class CommandLine
{
    public static SimConfig Parse(string[] args)
    {
        var c = new SimConfig();
        if (args == null)
        {
            return c;
        }

        c.WorldEdgeKm = Float(args, "--world", c.WorldEdgeKm);
        c.PopulationScale = Float(args, "--pop", c.PopulationScale);
        c.CellSizeM = Float(args, "--cell", c.CellSizeM);
        c.CellTreePromoteThreshold = Int(args, "--promote", c.CellTreePromoteThreshold);
        c.CellTreePromoteTightness = Float(args, "--tightness", c.CellTreePromoteTightness);
        c.ReclusterBudgetMs = Float(args, "--repair-budget", c.ReclusterBudgetMs);
        c.ClusterTargetPackingSlack = Float(args, "--packing-slack", c.ClusterTargetPackingSlack);
        c.ClusterTargetExtentRatio = Float(args, "--target-ratio", c.ClusterTargetExtentRatio);
        c.ClusterRepairExtentRatio = Float(args, "--repair-ratio", c.ClusterRepairExtentRatio);
        c.ClusterRepairCriticalExtentRatio = Float(args, "--repair-critical", c.ClusterRepairCriticalExtentRatio);
        c.RepairWorstClustersPerUnit = Int(args, "--repair-unit", c.RepairWorstClustersPerUnit);
        c.RepairCooldownTicks = Int(args, "--repair-cooldown", c.RepairCooldownTicks);
        c.QueryEfficiencyTolerance = Float(args, "--eff-tol", c.QueryEfficiencyTolerance);
        c.ShuttleShare = Float(args, "--shuttle-share", c.ShuttleShare);
        c.ShuttleIntervalS = Float(args, "--shuttle-interval", c.ShuttleIntervalS);
        c.BoardingWindowS = Float(args, "--boarding-window", c.BoardingWindowS);
        c.Shuttles = Array.IndexOf(args, "--no-shuttles") < 0;
        c.ShuttleBurst = Array.IndexOf(args, "--shuttle-burst") >= 0;
        c.Probe = Array.IndexOf(args, "--probe") >= 0;
        c.WorkProbe = Array.IndexOf(args, "--work-probe") >= 0;
        c.ChunkStats = Array.IndexOf(args, "--chunk-stats") >= 0;
        c.SimdNarrowphase = Array.IndexOf(args, "--scalar-narrowphase") < 0;
        c.BatchSpawnSortThreshold = Int(args, "--batch-sort", c.BatchSpawnSortThreshold);
        c.TickRateHz = Int(args, "--hz", c.TickRateHz);
        c.Unpaced = Array.IndexOf(args, "--unpaced") >= 0;
        c.GridWideBound = Array.IndexOf(args, "--grid-wide-bound") >= 0;
        c.RankWhenStarved = Array.IndexOf(args, "--rank-when-starved") >= 0;
        c.WorkerCount = Int(args, "--workers", c.WorkerCount);
        c.WarmTicks = Int(args, "--warm", c.WarmTicks);
        c.MeasuredTicks = Int(args, "--ticks", c.MeasuredTicks);
        c.PageCacheMiB = Int(args, "--cache-mib", c.PageCacheMiB);
        c.Seed = Int(args, "--seed", c.Seed);
        c.ParallelQueryMinChunkSize = Int(args, "--min-chunk", c.ParallelQueryMinChunkSize);
        c.AwarenessMinChunk = Int(args, "--awareness-min-chunk", c.AwarenessMinChunk);
        c.CostBasedChunking = Array.IndexOf(args, "--entity-chunking") < 0;
        c.BatchedSpatialWrites = Array.IndexOf(args, "--per-entity-writespatial") < 0;
        c.TickLogPath = Str(args, "--tick-log", null);
        c.AwarenessApi = Str(args, "--awareness-api", "count") switch
        {
            "movenext" => AwarenessApi.MoveNext,
            "count" => AwarenessApi.Count,
            "fill" => AwarenessApi.Fill,
            "batch" => AwarenessApi.Batch,
            var other => throw new ArgumentException($"--awareness-api takes movenext, count, fill or batch, not '{other}'"),
        };
        c.CombatApi = Str(args, "--combat-api", "movenext") switch
        {
            "movenext" => CombatApi.MoveNext,
            "batch" => CombatApi.Batch,
            var other => throw new ArgumentException($"--combat-api takes movenext or batch, not '{other}'"),
        };
        if (Array.IndexOf(args, "--split-awareness") >= 0)
        {
            c.SplitAwareness = true;
        }

        c.DatabaseDirectory = Str(args, "--db-dir", c.DatabaseDirectory);
        if (Array.IndexOf(args, "--serial-fence") >= 0)
        {
            c.ParallelFence = false;
        }

        if (Array.IndexOf(args, "--no-content-scale") >= 0)
        {
            c.ScaleContentWithWorld = false;
        }

        return c;
    }

    private static string Str(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    private static float Float(string[] args, string name, float fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static int Int(string[] args, string name, int fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
