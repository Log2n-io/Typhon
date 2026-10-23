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
    /// <summary>Whether <c>--db-name</c> is something other than a bare, portable file name.</summary>
    /// <param name="name">The requested database name.</param>
    /// <returns><see langword="true"/> when it must be refused.</returns>
    /// <remarks>
    /// <b><see cref="System.IO.Path.GetInvalidFileNameChars"/> alone is not enough, and the gap shows only on Linux.</b> On Windows it returns 41 characters —
    /// both separators and every control character; on Unix it returns exactly two, <c>'\0'</c> and <c>'/'</c>. So <c>--db-name "worlds\mine"</c> was
    /// refused on the dev box and accepted on the server, where it would have become one file literally named <c>worlds\mine</c> — the failure this check
    /// exists to turn into a refusal naming the flag. A control character went the same way, and a name carrying one is unreadable in a log and unquotable
    /// in a shell whatever the filesystem permits. So the rule is stated here rather than delegated: no separator of either kind, no control character, plus
    /// whatever else this platform rejects.
    /// </remarks>
    private static bool IsNotABareFileName(string name)
    {
        foreach (var ch in name)
        {
            if (ch is '/' or '\\' || char.IsControl(ch))
            {
                return true;
            }
        }

        return name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0;
    }

    /// <summary>What <c>--help</c> prints before the flags.</summary>
    private const string Usage =
        """
        SwgTatooine — a server-side Star Wars Galaxies workload for the Typhon engine.

          usage: SwgTatooine [flags]          measure a fixed number of ticks and report
                 SwgTatooine --serve <port>    serve the same world over WebSocket, forever
                 SwgTatooine --sweep           run the partitioning matrix and write a report

          --serve now REQUIRES its port. A bare --serve used to mean 8080, and so did a malformed one:
          `--serve 808O` listened on 8080 and said nothing. Every flag names its value or is refused.

        """;

    /// <summary>Parses a command line.</summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>The configuration. <see cref="SimConfig.HelpText"/> is set instead when the line asks for help.</returns>
    /// <exception cref="ArgumentException">
    /// A flag is unknown, misspelt, repeated, missing its value, or given a value that will not parse; or two settings contradict each other.
    /// </exception>
    public static SimConfig Parse(string[] args)
    {
        // Every fallback below is `c.X`, never a literal. A literal matching the field initialiser today is drift waiting to happen, and it would show up in
        // the one place meant to be authoritative: `--help` would print a default the program does not use.
        var c = new SimConfig();
        var r = new ArgReader(args ?? [], Usage);

        r.Group("mode");
        r.Switch("--help", "print this list and exit (or -h as the first argument)");
        c.ServePort = r.Int("--serve", c.ServePort, "serve over WebSocket on this port instead of measuring; 0 measures", min: 0, max: 65535);
        c.RunSweep = r.Switch("--sweep", "run the partitioning matrix instead of one configuration");
        c.SweepWorlds = r.Floats("--sweep-worlds", c.SweepWorlds, "sweep axis: world edges, km");
        c.SweepPops = r.Floats("--sweep-pops", c.SweepPops, "sweep axis: population scales");
        c.SweepCells = r.Floats("--sweep-cells", c.SweepCells, "sweep axis: cell sizes, m at the real planet's scale");

        r.Group("world and population");
        c.WorldEdgeKm = r.Float("--world", c.WorldEdgeKm, "planet edge, km; 16.384 is the real Tatooine", min: 0.001f);
        c.PopulationScale = r.Float("--pop", c.PopulationScale, "multiplier on every population count", min: 0.0001f);
        c.CellSizeM = r.Float("--cell", c.CellSizeM, "spatial cell edge, m; 0 derives 256 m scaled with the world", min: 0f);
        c.Planets = r.Int("--planets", c.Planets, "how many planet realms to build", min: 1);
        c.InteriorNpcs = r.Int("--interior-npcs", c.InteriorNpcs, "NPCs per interior", min: 0);
        c.InteriorShare = r.Float("--interior-share", c.InteriorShare, "share of players that go indoors", min: 0f, max: 1f);
        c.InteriorStayS = r.Float("--interior-stay", c.InteriorStayS, "seconds a player stays indoors", min: 0.001f);
        c.InteriorSleepS = r.Float("--interior-sleep", c.InteriorSleepS, "seconds an empty interior waits before going dormant", min: 0f);
        c.InterPlanetShare = r.Float("--interplanet-share", c.InterPlanetShare, "share of shuttle trips that cross planets", min: 0f, max: 1f);
        c.Space = r.Switch("--space", "build the space realm");
        c.Starships = r.Int("--starships", c.Starships, "starships in space", min: 0);
        c.RealmCensusHz = r.Float("--realm-census-hz", c.RealmCensusHz, "times a second a serving run counts each realm's population; 0 = never", min: 0f);
        c.PlanetDivisor = r.Int("--planet-divisor", c.PlanetDivisor, "divide each planet's population by this", min: 1);
        c.SpaceDivisor = r.Int("--space-divisor", c.SpaceDivisor, "divide space's population by this", min: 1);
        c.Dungeons = r.Int("--dungeons", c.Dungeons, "dungeon realms per planet", min: 0);
        c.DungeonIntervalS = r.Float("--dungeon-interval", c.DungeonIntervalS, "seconds between dungeon runs", min: 0.001f);
        c.DungeonStayS = r.Float("--dungeon-stay", c.DungeonStayS, "seconds a party stays in a dungeon", min: 0.001f);
        c.DungeonParty = r.Int("--dungeon-party", c.DungeonParty, "players per dungeon party", min: 1);
        c.DungeonMobs = r.Int("--dungeon-mobs", c.DungeonMobs, "creatures per dungeon", min: 0);
        c.ScaleContentWithWorld = !r.Switch("--no-content-scale", "keep authentic coordinates instead of stretching content to the world");
        c.Seed = r.Int("--seed", c.Seed, "seed for every random decision");

        r.Group("spatial partitioning");
        c.CellTreePromoteThreshold = r.Int("--promote", c.CellTreePromoteThreshold, "entities in a cell before it is promoted to a tree", min: 1);
        c.CellTreePromoteTightness = r.Float("--tightness", c.CellTreePromoteTightness, "extent/bound ratio a cell must exceed to be promoted", min: 0f);
        c.ReclusterBudgetMs = r.Float("--repair-budget", c.ReclusterBudgetMs, "per-tick cluster repair budget, ms; 0 is unbounded", min: 0f);
        c.ClusterTargetPackingSlack = r.Float("--packing-slack", c.ClusterTargetPackingSlack, "spare capacity a new cluster is given", min: 1f);
        c.ClusterTargetExtentRatio = r.Float("--target-ratio", c.ClusterTargetExtentRatio, "extent/bound a new cluster aims for", min: 0f);
        c.ClusterRepairExtentRatio = r.Float("--repair-ratio", c.ClusterRepairExtentRatio, "extent/bound above which a cluster is repaired", min: 0f);
        c.ClusterRepairCriticalExtentRatio = r.Float("--repair-critical", c.ClusterRepairCriticalExtentRatio,
            "extent/bound that makes a repair urgent", min: 0f);
        c.RepairWorstClustersPerUnit = r.Int("--repair-unit", c.RepairWorstClustersPerUnit, "clusters repaired per unit of budget", min: 1);
        c.RepairCooldownTicks = r.Int("--repair-cooldown", c.RepairCooldownTicks, "ticks before a repaired cluster may be repaired again", min: 0);
        c.QueryEfficiencyTolerance = r.Float("--eff-tol", c.QueryEfficiencyTolerance, "reported query efficiency tolerance", min: 0f);
        c.GridWideBound = r.Switch("--grid-wide-bound", "bound clusters by the grid rather than by the cell");
        c.SimdNarrowphase = !r.Switch("--scalar-narrowphase", "use the scalar narrowphase instead of SIMD");
        c.BatchSpawnSortThreshold = r.Int("--batch-sort", c.BatchSpawnSortThreshold, "batch size above which a spawn batch is sorted", min: 1);
        c.BatchedSpatialWrites = !r.Switch("--per-entity-writespatial", "write spatial positions one entity at a time (P9 off)");

        r.Group("shuttles");
        c.Shuttles = !r.Switch("--no-shuttles", "do not run shuttles at all");
        c.ShuttleShare = r.Float("--shuttle-share", c.ShuttleShare, "share of players that travel", min: 0f, max: 1f);
        c.ShuttleIntervalS = r.Float("--shuttle-interval", c.ShuttleIntervalS, "seconds between departures", min: 0.001f);
        c.BoardingWindowS = r.Float("--boarding-window", c.BoardingWindowS, "seconds a shuttle boards for", min: 0.001f);
        c.ShuttleBurst = r.Switch("--shuttle-burst", "land every passenger on one tick instead of trickling");

        r.Group("sessions and replication");
        c.MaxClients = r.Int("--max-clients", c.MaxClients, "player sessions admitted at once; 0 is unlimited", min: 0);
        c.MaxSpectators = r.Int("--max-spectators", c.MaxSpectators, "god-camera sessions admitted at once; 0 is unlimited", min: 0);
        c.SessionBudgetBytesPerSecond = r.Int("--session-budget", c.SessionBudgetBytesPerSecond,
            "per-player outbound budget, bytes per second; 0 is none", min: 0);
        c.IngressBytesPerSecond = r.Int("--ingress-budget", c.IngressBytesPerSecond, "per-session inbound budget, bytes per second", min: 0);
        c.PlayerLeaveM = r.Dbl("--player-leave", c.PlayerLeaveM, "players' leave radius, m; 0 is none", min: 0d);
        c.AvatarSaveIntervalS = r.Dbl(
            "--avatar-save-interval", c.AvatarSaveIntervalS, "how often a live avatar's position is saved, s; 0 disables it", min: 0d);
        c.SpawnSpread = r.Dbl(
            "--spawn-spread", c.SpawnSpread, "scale on every town's radius for new arrivals; 1 is the map, 0 is one point", min: 0d);
        c.MaxSessions = r.Int(
            "--max-sessions", c.MaxSessions, "clients connected at once; the engine's session table, 65535 is structural", min: 1, max: 65535);
        c.ResumeGraceS = r.Dbl(
            "--resume-grace", c.ResumeGraceS, "how long a link-dead session's avatar waits for its client, s; 0 disables resume", min: 0d);
        c.AuthBurst = r.Int("--auth-burst", c.AuthBurst, "/auth attempts one address may make at once; 0 keeps the default 10", min: 0);
        c.AuthRefillMs = r.Int("--auth-refill-ms", c.AuthRefillMs, "ms for one /auth attempt to come back; 0 keeps the default 6000", min: 0);
        c.GodRegionMaxEdgeM = r.Dbl("--god-region", c.GodRegionMaxEdgeM, "god camera's largest region edge, m; 0 keeps the whole-world camera", min: 0d);
        c.GodNearBudget = r.Int("--god-near", c.GodNearBudget, "god camera's near budget, entities", min: 0);
        c.SubscriptionsPushAutomatic = r.Choice("--subs-mode", "push", ["push", "push-auto"], "who detects a change: the simulation, or the engine")
            == "push-auto";
        c.SubscriptionsPhaseTiming = r.Switch("--subs-phases", "report per-phase replication timing");
        c.DormancyTicks = r.Int("--dormancy", c.DormancyTicks, "ticks of stillness before a cluster sleeps; 0 is never", min: 0);

        r.Group("runtime and scheduling");
        c.TickRateHz = r.Int("--hz", c.TickRateHz, "tick rate; 10 is the baseline the published numbers are quoted at", min: 1, max: 100_000);
        c.Unpaced = r.Switch("--unpaced", "tick back to back, for count-only comparisons; never for timings");
        c.CollisionAudit = r.Switch("--collision-audit", "re-check every written position against a fresh spatial query, and report any found inside a "
            + "structure; an independent oracle, far too slow for a normal run") || c.CollisionAudit;
        c.CollideCrowd = !r.Switch("--no-collide-crowd", "let the scripted crowd walk through structures; collision then costs only the bodies "
            + "real sessions drive, which is what every tick figure published before 2026-10-22 was measured with");
        c.WorkerCount = r.Int("--workers", c.WorkerCount, "worker threads; 0 takes the processor count", min: 0, max: 4096);
        c.WarmTicks = r.Int("--warm", c.WarmTicks, "ticks run before measuring starts", min: 0);
        c.MeasuredTicks = r.Int("--ticks", c.MeasuredTicks, "ticks measured", min: 1);
        c.PageCacheMiB = r.Int("--cache-mib", c.PageCacheMiB, "page cache size, MiB", min: 1);
        c.ParallelQueryMinChunkSize = r.Int("--min-chunk", c.ParallelQueryMinChunkSize, "global parallel-query chunk floor", min: 0);
        c.AwarenessMinChunk = r.Int("--awareness-min-chunk", c.AwarenessMinChunk, "chunk floor for the awareness system alone; 0 uses the global one", min: 0);
        c.CostBasedChunking = !r.Switch("--entity-chunking", "chunk by entity count instead of by cost (P2 off)");
        c.ParallelFence = !r.Switch("--serial-fence", "run the tick fence serially");
        c.RankWhenStarved = r.Switch("--rank-when-starved", "rank work when the pool is starved");
        c.WorkerIdleSpin = r.Int("--idle-spin", c.WorkerIdleSpin, "worker idle spin iterations", min: 0);
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
        c.IdleCreatureFraction = r.Dbl("--idle-creatures", c.IdleCreatureFraction, "share of creatures that never think", min: 0d, max: 1d);
        c.RespawnSeconds = r.Float("--respawn-s", c.RespawnSeconds, "seconds before a lair revives a killed creature", min: 0.001f);

        r.Group("persistence and output");
        c.DatabaseDirectory = r.Str("--db-dir", c.DatabaseDirectory, "where the database file is written", "<path>");
        c.DatabaseName = r.Str("--db-name", c.DatabaseName, "the database's file name, without directory or extension", "<name>");
        c.Persist = r.Switch("--persist", "keep the database across runs and reopen it instead of building a fresh world");
        c.ReportDirectory = r.Str("--report-dir", null, "where --sweep writes its report", "<path>");
        c.TickLogPath = r.Str("--tick-log", null, "write one line per tick here", "<path>");

        r.Group("hosting — only with --serve");
        c.BindAddress = r.Str("--bind", c.BindAddress, "address the web host binds; loopback when unset, 0.0.0.0 for every interface", "<addr>");
        // Comma-separated, not repeatable: this parser refuses a flag given twice (a corrected A/B arm used to run the value it was meant to replace), and
        // --sweep-worlds sets the house pattern for a list. Split in Validate so the help text shows the raw form.
        var origins = r.Str("--origin", null, "origins a browser may connect from, comma-separated. Required when --bind is not loopback", "<url,url>");
        c.ClientDirectory = r.Str("--client-dir", c.ClientDirectory, "directory of built client files to serve; the repo's dist/ when unset", "<path>");
        c.AssetDirectory = r.Str("--assets", c.AssetDirectory, "published asset folder (the exporter's build/assets) to serve at /assets", "<path>");
        c.TlsChainPath = r.Str("--tls-chain", c.TlsChainPath, "PEM chain to serve https with (certbot's fullchain.pem)", "<path>");
        c.TlsKeyPath = r.Str("--tls-key", c.TlsKeyPath, "PEM private key matching --tls-chain (certbot's privkey.pem)", "<path>");
        c.TlsPort = r.Int("--tls-port", c.TlsPort, "port https is served on; 443 needs CAP_NET_BIND_SERVICE", min: 1, max: 65535);
        c.RedirectPort = r.Int("--redirect-port", c.RedirectPort, "port that serves only a redirect to https; 0 maps none", min: 0, max: 65535);
        c.AuthLocalOnly = r.Switch("--auth-local-only", "answer /auth only for callers on a private address; required on a public bind with no CDN");
        c.AuthIterations = r.Int(
            "--auth-iterations", c.AuthIterations, "PBKDF2 work factor for new passwords; 0 keeps the default 600000", min: 0);
        c.Origins = origins == null ? [] : origins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Declared so --help lists them, RejectUnknown accepts them and their values are range-checked from one place like every other flag (SWG-07) — and then
        // REFUSED by Program when the profiler is not already on, because no host can switch its own profiler on: the engine's master gate is baked before Main
        // runs. See ProfilerArgs for the mechanism and scripts/swg-serve.ps1 for the equivalent that works. A flag that is listed and refused with instructions
        // beats both an unknown-flag error, which teaches nothing, and the silent no-op this started as.
        r.Group("live profiling — set before the process starts; these flags only tell you how");
        c.ProfilerLivePort = r.Int("--live", c.ProfilerLivePort, $"the Workbench's profiler port; refused in-process, use scripts/swg-serve.ps1 -Live "
            + $"{ProfilerArgs.DefaultLivePort}", min: 0, max: 65535);
        c.ProfilerBindAddress = r.Str("--profiler-bind", c.ProfilerBindAddress, "address the profiler port binds; loopback when unset, and a routable address "
            + "publishes your telemetry", "<addr>");
        c.ProfilerTracePath = r.Str("--trace", c.ProfilerTracePath, "write a .typhon-trace capture here instead of the database's profilings/ directory",
            "<path>");

        r.Group("diagnostics — measurement only, and refused with --serve");
        c.Probe = r.Switch("--probe", "time port queries around shuttle arrivals");
        c.WorkProbe = r.Switch("--work-probe", "replay each awareness query and report the work it did");
        c.ChunkStats = r.Switch("--chunk-stats", "report per-chunk parallel-query statistics");
        c.FaultAtTick = r.Int("--fault-at-tick", c.FaultAtTick, "throw from a system on this tick, to exercise the crash artefact; 0 never faults", min: 0);

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
    /// <para>
    /// It used to hold the single-flag ranges too, and it was incomplete in exactly the class of error it exists for: <c>--pop -1</c>, <c>--world -5</c>,
    /// <c>--ticks -1</c>, <c>--cache-mib 0</c> and seven more parsed and ran, and <c>--pop -1</c> built an empty world and reported it as a measurement. A
    /// range written beside its declaration cannot be forgotten when a flag is added; one written in a list at the end of the parse can, and was.
    /// </para>
    /// </remarks>
    private static void Validate(SimConfig c)
    {
        // Every single-flag range now lives on its own declaration, where it cannot be forgotten when a flag is added — see ArgReader.OutOfRange. What is left
        // here is what no single declaration can see: combinations.

        // --persist reopens one world; --sweep builds forty-five. Combined, every point after the first would measure whatever the point before it left behind,
        // at a different world size and a different cell size, and report it as its own. That is the exact failure --persist defaults to off to avoid, so a
        // command line asking for both is refused rather than silently resolved either way (P-1).
        if (c.Persist && c.RunSweep)
        {
            throw new ArgumentException("--persist reopens one world and --sweep builds a fresh one per point, so every point after the first would measure "
                + "the point before it. Pick one.");
        }

        if (string.IsNullOrWhiteSpace(c.DatabaseName) || IsNotABareFileName(c.DatabaseName))
        {
            throw new ArgumentException($"--db-name must be a bare file name with no directory and no invalid characters; got '{c.DatabaseName}'. "
                + "Use --db-dir for the directory.");
        }

        // A hosting flag without --serve is a command line that cannot do what it says: there is no host to bind, no origin to admit and no client to serve.
        // Refused rather than ignored, which is this parser's whole character (SWG-07).
        //
        // <b>--assets is NOT one of them any more.</b> It was, while an asset folder only fed the web host; it now also carries the collision shapes the
        // tick pushes bodies out of, and a measured run is the only way to put thousands of bodies on that path (--collide-crowd). Refusing it here made the
        // measured run silently collide against footprint boxes -- the fallback no real client ever meets -- or refuse the command line outright.
        if (c.ServePort == 0 && (c.BindAddress != null || c.Origins.Length > 0 || c.ClientDirectory != null))
        {
            throw new ArgumentException("--bind, --origin and --client-dir configure the web host, which only exists with --serve <port>. "
                + "--assets is accepted without it: it also carries the collision shapes a measured run collides against.");
        }

        if (c.AuthIterations > 0 && c.AuthIterations < Accounts.PasswordHash.MinimumIterations)
        {
            throw new ArgumentException(
                $"--auth-iterations {c.AuthIterations} is below the floor of {Accounts.PasswordHash.MinimumIterations}. Below that a leaked hash table is "
                + "crackable on one laptop, so the hashing would read as protection without being any.");
        }

        if (c.ServePort == 0 && (c.TlsChainPath.Length > 0 || c.TlsKeyPath.Length > 0 || c.RedirectPort > 0 || c.AuthLocalOnly))
        {
            throw new ArgumentException(
                "--tls-chain, --tls-key, --redirect-port and --auth-local-only configure the web host, which only exists with --serve <port>.");
        }

        // One without the other is the dangerous shape: a server asked for TLS that quietly served plain HTTP would look like a
        // browser problem, not a configuration one. Refused at the parse, before a world is built.
        if ((c.TlsChainPath.Length > 0) != (c.TlsKeyPath.Length > 0))
        {
            throw new ArgumentException(
                "--tls-chain and --tls-key go together: a chain with no key cannot complete a handshake, and a key with no chain"
                + " has nothing to present.");
        }

        if (c.RedirectPort > 0 && c.TlsChainPath.Length == 0)
        {
            throw new ArgumentException("--redirect-port redirects to https, so it needs --tls-chain and --tls-key.");
        }

        if (c.RedirectPort > 0 && (c.RedirectPort == c.ServePort || c.RedirectPort == c.TlsPort))
        {
            throw new ArgumentException($"--redirect-port {c.RedirectPort} collides with --serve or --tls-port; it must be a port of its own.");
        }

        if (c.TlsChainPath.Length > 0 && c.TlsPort == c.ServePort)
        {
            throw new ArgumentException($"--tls-port {c.TlsPort} collides with --serve {c.ServePort}; the plain and the TLS listeners need different ports.");
        }

        // The address and the origin pairing, checked HERE rather than where the host binds. Both used to be: the host's own version throws from inside the DI
        // options callback, which runs after the world is built — so a command line that could never serve spent a second and a half building a world first.
        // A cross-flag check that a parse can make is a parse's job (SWG-07).
        if (c.BindAddress != null)
        {
            if (!System.Net.IPAddress.TryParse(c.BindAddress.Trim(), out var bind))
            {
                throw new ArgumentException($"--bind '{c.BindAddress}' is not an IP address. Use 127.0.0.1 for local only, or 0.0.0.0 for every interface.");
            }

            if (!System.Net.IPAddress.IsLoopback(bind) && c.Origins.Length == 0)
            {
                throw new ArgumentException($"--bind {bind} is reachable from the network, so --origin <url> is required: allowing any origin there would let "
                    + "any page a visitor opens drive this server from their browser.");
            }
        }

        if (c.ClientDirectory != null && !System.IO.Directory.Exists(c.ClientDirectory))
        {
            // Checked here rather than left to the host, which skips a missing directory silently: a deployment whose payload unpacked wrong would serve a
            // WebSocket and no page, and the operator would be looking at the browser.
            throw new ArgumentException($"--client-dir '{c.ClientDirectory}' does not exist.");
        }

        if (c.AssetDirectory != null && !System.IO.Directory.Exists(c.AssetDirectory))
        {
            // Only the directory here. Whether it holds a publishable catalogue is AssetOrigin.Open's judgement, made at
            // startup where its error can name the file that is wrong; duplicating that check in the parser would mean two
            // places to keep in step with the asset contract.
            throw new ArgumentException($"--assets '{c.AssetDirectory}' does not exist.");
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

        if (c.FaultAtTick > 0)
        {
            throw new ArgumentException("--fault-at-tick deliberately kills a tick, to exercise the crash artefact. It cannot be combined with --serve.");
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
