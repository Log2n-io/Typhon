using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SpaceBattle;

public static class Program
{
    public static int Main(string[] args)
    {
        foreach (var a in args)
        {
            if (a is "--help" or "-h" or "/?")
            {
                Console.Write(Config.Help());
                return 0;
            }
        }

        var cfg = Config.Load(args);
        var errors = cfg.Validate();
        if (errors.Count > 0)
        {
            foreach (var e in errors)
            {
                Console.Error.WriteLine($"[config] {e}");
            }
            return 2;
        }
        if (cfg.PrintConfig)
        {
            Console.Write(cfg.Dump());
        }

        if (!string.IsNullOrWhiteSpace(cfg.CellSweep))
        {
            return CellSizeSweep(cfg);
        }

        var sw = Stopwatch.StartNew();
        using var host = new TyphonHost(cfg);
        host.Boot();
        var bootMs = sw.Elapsed.TotalMilliseconds;

        var sim = new Simulation(cfg, host);
        sim.BuildWorld();

        Console.WriteLine($"boot {bootMs:F0} ms · world ready {sw.Elapsed.TotalMilliseconds:F0} ms · " +
                          $"{sim.ShipsAlive[0]}+{sim.ShipsAlive[1]} ships · grid {host.GridConfig.GridWidth}x{host.GridConfig.GridHeight} " +
                          $"cells of {cfg.CellSize:F0}u");

        using var app = new App(cfg, host, sim);
        if (cfg.SelfTestKeys)
        {
            return app.SelfTestSpeedKeys() ? 0 : 1;
        }
        if (cfg.AutoTicks > 0)
        {
            app.RunAuto();
        }
        else
        {
            app.Run();
        }
        return 0;
    }

    /// <summary>
    /// Boots a fresh engine per cell size and reports broadphase selectivity for each. This is the experiment the
    /// research docs ask for: cell size is currently a global, immutable guess with no measurement behind it.
    /// </summary>
    private static int CellSizeSweep(Config cfg)
    {
        var sizes = new List<float>();
        foreach (var part in cfg.CellSweep.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (float.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
            {
                sizes.Add(v);
            }
        }
        var radii = new List<float>();
        foreach (var part in cfg.SweepRadii.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (float.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
            {
                radii.Add(v);
            }
        }
        var ticks = cfg.AutoTicks > 0 ? cfg.AutoTicks : 400;

        Console.WriteLine($"CELL-SIZE SWEEP — world {cfg.WorldSize:F0}u, {cfg.InitialShipsPerFaction * cfg.Factions} initial ships, {ticks} ticks per run");
        Console.WriteLine();
        Console.WriteLine("  cell   grid  clusters  meanAABB%  |  " + string.Join("  ", radii.ConvertAll(r => $"r={r,-5:F0}")));
        Console.WriteLine("  " + new string('-', 44 + radii.Count * 9));

        foreach (var cs in sizes)
        {
            var runCfg = Config.Load(args: Array.Empty<string>());
            foreach (var f in typeof(Config).GetFields())
            {
                f.SetValue(runCfg, f.GetValue(cfg));
            }
            runCfg.CellSize = cs;
            runCfg.CellSweep = "";
            if (runCfg.Validate().Count > 0)
            {
                Console.WriteLine($"  {cs,5:F0}   (invalid: {string.Join("; ", runCfg.Validate())})");
                continue;
            }

            using var host = new TyphonHost(runCfg);
            host.Boot();
            var sim = new Simulation(runCfg, host);
            sim.BuildWorld();
            var dt = 1f / runCfg.TickRate;
            for (var i = 0; i < ticks; i++)
            {
                sim.Step(dt);
            }

            var boxes = CollectClusterBoxes(host);
            var probe = new SpatialProbe();
            var cx = runCfg.WorldSize * 0.5f;
            var cy = runCfg.WorldSize * 0.5f;

            var cells = new List<string>();
            float meanAabbPct = 0;
            var clusterCount = 0;
            foreach (var r in radii)
            {
                probe.Measure(host, boxes, host.ShipArchetypeId, cx - r, cy - r, cx + r, cy + r);
                probe.MeasureMatches(host, cx - r, cy - r, cx + r, cy + r);
                cells.Add($"{probe.Selectivity * 100,7:F2}%");
                meanAabbPct = probe.MeanClusterAreaVsCell * 100;
                clusterCount = probe.ClustersInCells;
            }
            Console.WriteLine($"  {cs,5:F0}  {host.GridConfig.GridWidth,2}x{host.GridConfig.GridHeight,-2}  {clusterCount,8}  {meanAabbPct,8:F1}%  |  " + string.Join("  ", cells));
        }
        Console.WriteLine();
        Console.WriteLine("  Selectivity = entities the query actually wanted / entities the broadphase made it examine.");
        Console.WriteLine("  A projectile hit test lives at the smallest radius. That column is the one that matters.");
        return 0;
    }

    private static List<ClusterBox> CollectClusterBoxes(TyphonHost host)
    {
        var list = new List<ClusterBox>();
        using var tx = host.DBE.CreateQuickTransaction();
        using var acc = tx.For<Ship>();
        using var e = acc.GetClusterEnumerator();
        foreach (var cluster in e)
        {
            if (cluster.OccupancyBits == 0)
            {
                continue;
            }
            ref readonly var a = ref cluster.SpatialBounds;
            if (!(a.MinX <= a.MaxX) || float.IsInfinity(a.MinX))
            {
                continue;
            }
            var home = host.ClusterHomeCell(host.ShipArchetypeId, cluster.ChunkId);
            list.Add(new ClusterBox
            {
                ArchetypeId = host.ShipArchetypeId,
                ChunkId = cluster.ChunkId,
                MinX = a.MinX, MinY = a.MinY, MaxX = a.MaxX, MaxY = a.MaxY,
                LiveCount = cluster.LiveCount,
                HomeCellKey = home,
                CentreCellKey = host.Grid.WorldToCellKey(0.5f * (a.MinX + a.MaxX), 0.5f * (a.MinY + a.MaxY)),
            });
        }
        return list;
    }
}
