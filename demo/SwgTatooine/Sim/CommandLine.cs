using System;

namespace SwgTatooine;

/// <summary>Argument parsing for the simulation driver. Every knob in <see cref="SimConfig"/> that a sweep moves, plus the flags that pick a mode.</summary>
/// <remarks>
/// <para>
/// <b>Every flag is declared exactly once, here, and the declaration is what <c>--help</c> prints.</b> <see cref="ArgReader"/> records each one as it is
/// read, so a flag that exists is listed and a token that is not a flag is refused — see its remarks for why a lenient parser made every A/B measurement
/// in this demo unfalsifiable (SWG-07).
/// </para>
/// <para>
/// <b><c>--serve</c> and <c>--sweep</c> are parsed here too, although they select a mode rather than configure the simulation.</b> They used to be read
/// from <c>Program</c> and <c>Sweep</c> with their own copies of <c>IndexOf</c>, which meant the strict parse could not see them: every one of the four
/// sweep flags would have been reported as unknown. A flag the mode reader owns and the argument reader does not know about is the same defect in a new
/// place.
/// </para>
/// </remarks>
public static class CommandLine
{
    /// <summary>Parses a command line.</summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>The configuration. <see cref="SimConfig.HelpText"/> is set instead when the line asks for help.</returns>
    /// <exception cref="ArgumentException">
    /// A flag is unknown, misspelt, repeated, missing its value, or given a value that will not parse; or two settings contradict each other.
    /// </exception>
    public static SimConfig Parse(string[] args)
    {
        var c = new SimConfig();
        var r = new ArgReader(args ?? []);

        r.Group("mode");
        r.Switch("--help", "print this list and exit");
        c.ServePort = r.Int("--serve", 0, "serve over WebSocket on this port instead of measuring; 0 measures");
        c.RunSweep = r.Switch("--sweep", "run the partitioning matrix instead of one configuration");
        c.SweepWorlds = r.Floats("--sweep-worlds", [TatooineData.PlanetEdgeM / 1000f, 64f, 128f], "sweep axis: world edges, km");
        c.SweepPops = r.Floats("--sweep-pops", [1f, 4f, 16f], "sweep axis: population scales");
        c.SweepCells = r.Floats("--sweep-cells", [64f, 128f, 256f, 512f, 1024f], "sweep axis: cell sizes, m at the real planet's scale");

        r.Group("world and population");
        c.WorldEdgeKm = r.Float("--world", c.WorldEdgeKm, "planet edge, km; 16.384 is the real Tatooine");
        c.PopulationScale = r.Float("--pop", c.PopulationScale, "multiplier on every population count");
        c.CellSizeM = r.Float("--cell", c.CellSizeM, "spatial cell edge, m; 0 derives 256 m scaled with the world");
        c.Planets = r.Int("--planets", c.Planets, "how many planet realms to build");
        c.Interiors = r.Switch("--interiors", "give buildings interior realms");
        c.InteriorNpcs = r.Int("--interior-npcs", c.InteriorNpcs, "NPCs per interior");
        c.InteriorShare = r.Float("--interior-share", c.InteriorShare, "share of players that go indoors, 0-1");
        c.InteriorStayS = r.Float("--interior-stay", c.InteriorStayS, "seconds a player stays indoors");
        c.InteriorSleepS = r.Float("--interior-sleep", c.InteriorSleepS, "seconds an empty interior waits before going dormant");
        c.InterPlanetShare = r.Float("--interplanet-share", c.InterPlanetShare, "share of shuttle trips that cross planets, 0-1");
        c.Space = r.Switch("--space", "build the space realm");
        c.Starships = r.Int("--starships", c.Starships, "starships in space");
        c.PlanetDivisor = r.Int("--planet-divisor", c.PlanetDivisor, "divide each planet's population by this");
        c.SpaceDivisor = r.Int("--space-divisor", c.SpaceDivisor, "divide space's population by this");
        c.Dungeons = r.Int("--dungeons", c.Dungeons, "dungeon realms per planet");
        c.DungeonIntervalS = r.Float("--dungeon-interval", c.DungeonIntervalS, "seconds between dungeon runs");
        c.DungeonStayS = r.Float("--dungeon-stay", c.DungeonStayS, "seconds a party stays in a dungeon");
        c.DungeonParty = r.Int("--dungeon-party", c.DungeonParty, "players per dungeon party");
        c.DungeonMobs = r.Int("--dungeon-mobs", c.DungeonMobs, "creatures per dungeon");
        c.ScaleContentWithWorld = !r.Switch("--no-content-scale", "keep authentic coordinates instead of stretching content to the world");
        c.Seed = r.Int("--seed", c.Seed, "seed for every random decision");

        r.Group("spatial partitioning");
        c.CellTreePromoteThreshold = r.Int("--promote", c.CellTreePromoteThreshold, "entities in a cell before it is promoted to a tree");
        c.CellTreePromoteTightness = r.Float("--tightness", c.CellTreePromoteTightness, "extent/bound ratio a cell must exceed to be promoted");
        c.ReclusterBudgetMs = r.Float("--repair-budget", c.ReclusterBudgetMs, "per-tick cluster repair budget, ms; 0 is unbounded");
        c.ClusterTargetPackingSlack = r.Float("--packing-slack", c.ClusterTargetPackingSlack, "spare capacity a new cluster is given");
        c.ClusterTargetExtentRatio = r.Float("--target-ratio", c.ClusterTargetExtentRatio, "extent/bound a new cluster aims for");
        c.ClusterRepairExtentRatio = r.Float("--repair-ratio", c.ClusterRepairExtentRatio, "extent/bound above which a cluster is repaired");
        c.ClusterRepairCriticalExtentRatio = r.Float("--repair-critical", c.ClusterRepairCriticalExtentRatio, "extent/bound that makes a repair urgent");
        c.RepairWorstClustersPerUnit = r.Int("--repair-unit", c.RepairWorstClustersPerUnit, "clusters repaired per unit of budget");
        c.RepairCooldownTicks = r.Int("--repair-cooldown", c.RepairCooldownTicks, "ticks before a repaired cluster may be repaired again");
        c.QueryEfficiencyTolerance = r.Float("--eff-tol", c.QueryEfficiencyTolerance, "reported query efficiency tolerance");
        c.GridWideBound = r.Switch("--grid-wide-bound", "bound clusters by the grid rather than by the cell");
        c.SimdNarrowphase = !r.Switch("--scalar-narrowphase", "use the scalar narrowphase instead of SIMD");
        c.BatchSpawnSortThreshold = r.Int("--batch-sort", c.BatchSpawnSortThreshold, "batch size above which a spawn batch is sorted");
        c.BatchedSpatialWrites = !r.Switch("--per-entity-writespatial", "write spatial positions one entity at a time (P9 off)");

        r.Group("shuttles");
        c.Shuttles = !r.Switch("--no-shuttles", "do not run shuttles at all");
        c.ShuttleShare = r.Float("--shuttle-share", c.ShuttleShare, "share of players that travel, 0-1");
        c.ShuttleIntervalS = r.Float("--shuttle-interval", c.ShuttleIntervalS, "seconds between departures");
        c.BoardingWindowS = r.Float("--boarding-window", c.BoardingWindowS, "seconds a shuttle boards for");
        c.ShuttleBurst = r.Switch("--shuttle-burst", "land every passenger on one tick instead of trickling");

        r.Group("sessions and replication");
        c.MaxClients = r.Int("--max-clients", c.MaxClients, "player sessions admitted at once; 0 is unlimited");
        c.MaxSpectators = r.Int("--max-spectators", c.MaxSpectators, "god-camera sessions admitted at once; 0 is unlimited");
        c.SessionBudgetBytesPerSecond = r.Int("--session-budget", 0, "per-player outbound budget, bytes per second; 0 is none");
        c.IngressBytesPerSecond = r.Int("--ingress-budget", c.IngressBytesPerSecond, "per-session inbound budget, bytes per second");
        c.PlayerLeaveM = r.Dbl("--player-leave", 0d, "players' leave radius, m; 0 is none");
        c.GodRegionMaxEdgeM = r.Dbl("--god-region", 0d, "god camera's largest region edge, m; 0 keeps the whole-world camera");
        c.GodNearBudget = r.Int("--god-near", c.GodNearBudget, "god camera's near budget, entities");
        c.SubscriptionsPushAutomatic = r.Choice("--subs-mode", "push", ["push", "push-auto"], "who detects a change: the simulation, or the engine")
            == "push-auto";
        c.SubscriptionsCollapseWorkUnits = r.Choice("--subs-pipeline", "staged", ["staged", "collapsed"], "run the frame pipeline staged or collapsed")
            == "collapsed" ? int.MaxValue : 0;
        c.SubscriptionsPhaseTiming = r.Switch("--subs-phases", "report per-phase replication timing");
        c.DormancyTicks = r.Int("--dormancy", 0, "ticks of stillness before a cluster sleeps; 0 is never");

        r.Group("runtime and scheduling");
        c.TickRateHz = r.Int("--hz", c.TickRateHz, "tick rate; 10 is the baseline the published numbers are quoted at");
        c.Unpaced = r.Switch("--unpaced", "tick back to back, for count-only comparisons; never for timings");
        c.WorkerCount = r.Int("--workers", c.WorkerCount, "worker threads; 0 takes the processor count");
        c.WarmTicks = r.Int("--warm", c.WarmTicks, "ticks run before measuring starts");
        c.MeasuredTicks = r.Int("--ticks", c.MeasuredTicks, "ticks measured");
        c.PageCacheMiB = r.Int("--cache-mib", c.PageCacheMiB, "page cache size, MiB");
        c.ParallelQueryMinChunkSize = r.Int("--min-chunk", c.ParallelQueryMinChunkSize, "global parallel-query chunk floor");
        c.AwarenessMinChunk = r.Int("--awareness-min-chunk", c.AwarenessMinChunk, "chunk floor for the awareness system alone; 0 uses the global one");
        c.CostBasedChunking = !r.Switch("--entity-chunking", "chunk by entity count instead of by cost (P2 off)");
        c.ParallelFence = !r.Switch("--serial-fence", "run the tick fence serially");
        c.RankWhenStarved = r.Switch("--rank-when-starved", "rank work when the pool is starved");
        c.WorkerIdleSpin = r.Int("--idle-spin", c.WorkerIdleSpin, "worker idle spin iterations");
        c.WorkerHotSpinners = r.IntOrNull("--sched-hot", "how many workers hot-spin; absent leaves the engine's own number");
        c.WorkerParkAfterUs = r.IntOrNull("--sched-park-us", "park a worker after this many idle microseconds; absent leaves the engine's own number");
        c.ForceAwareness = r.Switch("--awareness", "keep the awareness system even when replication is declared");
        c.SplitAwareness = r.Switch("--split-awareness", "split awareness into two systems");
        c.AwarenessApi = r.Choice("--awareness-api", "count", ["movenext", "count", "fill", "batch"], "how awareness drains each interest query") switch
        {
            "movenext" => AwarenessApi.MoveNext,
            "count" => AwarenessApi.Count,
            "fill" => AwarenessApi.Fill,
            _ => AwarenessApi.Batch,
        };
        c.CombatApi = r.Choice("--combat-api", "movenext", ["movenext", "batch"], "how creature combat asks which players are in range") switch
        {
            "movenext" => CombatApi.MoveNext,
            _ => CombatApi.Batch,
        };
        c.IdleCreatureFraction = Math.Clamp(r.Dbl("--idle-creatures", 0d, "share of creatures that never think, 0-1"), 0d, 1d);
        c.RespawnSeconds = r.Float("--respawn-s", c.RespawnSeconds, "seconds before a lair revives a killed creature");

        r.Group("persistence and output");
        c.DatabaseDirectory = r.Str("--db-dir", c.DatabaseDirectory, "where the database file is written", "<path>");
        c.ReportDirectory = r.Str("--report-dir", null, "where --sweep writes its report", "<path>");
        c.TickLogPath = r.Str("--tick-log", null, "write one line per tick here", "<path>");

        r.Group("diagnostics — measurement only, and refused with --serve");
        c.Probe = r.Switch("--probe", "time port queries around shuttle arrivals");
        c.WorkProbe = r.Switch("--work-probe", "replay each awareness query and report the work it did");
        c.ChunkStats = r.Switch("--chunk-stats", "report per-chunk parallel-query statistics");

        if (r.WantsHelp)
        {
            c.HelpText = r.Help();
            return c;
        }

        r.RejectUnknown();
        Validate(c);
        return c;
    }

    /// <summary>
    /// The cross-flag checks: a value that is individually well-formed but wrong, or a pair of settings that contradict each other.
    /// </summary>
    /// <param name="c">The parsed configuration.</param>
    /// <exception cref="ArgumentException">A value is out of range, or two settings cannot both hold.</exception>
    /// <remarks>
    /// Separate from the reads so that <c>--help</c> reaches the flag list without passing through them. Every message names the flag rather than the
    /// field, because the flag is what the operator typed.
    /// </remarks>
    private static void Validate(SimConfig c)
    {
        if (c.ServePort != 0 && c.ServePort is < 1 or > 65535)
        {
            throw new ArgumentException($"--serve takes a port in 1-65535, not {c.ServePort}");
        }

        if (c.Planets < 1)
        {
            throw new ArgumentException($"--planets takes 1 or more, not {c.Planets}");
        }

        if (!(c.InteriorStayS > 0f) || c.InterPlanetShare is < 0f or > 1f)
        {
            throw new ArgumentException($"--interior-stay takes a positive value and --interplanet-share one in [0, 1], not {c.InteriorStayS} / "
                + $"{c.InterPlanetShare}");
        }

        if (c.InteriorSleepS < 0f || c.PlanetDivisor < 1 || c.SpaceDivisor < 1 || c.Dungeons < 0 || !(c.DungeonIntervalS > 0f) || !(c.DungeonStayS > 0f)
            || c.DungeonParty < 1 || c.DungeonMobs < 0)
        {
            throw new ArgumentException("--interior-sleep >= 0, --planet-divisor/--space-divisor >= 1, --dungeons >= 0, positive dungeon interval and stay, "
                + "--dungeon-party >= 1, --dungeon-mobs >= 0");
        }

        if (c.Starships < 0)
        {
            throw new ArgumentException($"--starships takes 0 or more, not {c.Starships}");
        }

        if (c.InteriorNpcs < 0 || c.InteriorShare is < 0f or > 1f)
        {
            throw new ArgumentException($"--interior-npcs takes 0 or more and --interior-share a value in [0, 1], not {c.InteriorNpcs} / {c.InteriorShare}");
        }

        if (c.TickRateHz < 1)
        {
            throw new ArgumentException($"--hz takes 1 or more, not {c.TickRateHz}");
        }

        if (c.MaxClients < 0 || c.MaxSpectators < 0)
        {
            throw new ArgumentException($"--max-clients and --max-spectators take 0 (unlimited) or more, not {c.MaxClients} / {c.MaxSpectators}");
        }

        if (c.ServePort == 0)
        {
            return;
        }

        // A server runs forever; the three probes accumulate one sample per event for the whole run so that a median can be taken at the end. In a run with
        // no end there is no end to take it at and the sample lists grow without bound — which is the same defect SWG-07 fixed in the shuttle report, and
        // refusing the combination is cheaper and more honest than capping a measurement nobody can read.
        if (c.Probe || c.WorkProbe || c.ChunkStats)
        {
            throw new ArgumentException("--probe, --work-probe and --chunk-stats collect samples for a report printed when a measured run ends, so they "
                + "cannot be combined with --serve, which never ends. Run them without --serve.");
        }

        if (c.RunSweep)
        {
            throw new ArgumentException("--sweep runs a matrix of measured runs and --serve runs one world forever; they are different modes. Pick one.");
        }

        if (c.Unpaced)
        {
            throw new ArgumentException("--unpaced ticks as fast as the box will go and disables the overload response, which is right for counting and "
                + "wrong for a server clients connect to. It cannot be combined with --serve.");
        }
    }
}
