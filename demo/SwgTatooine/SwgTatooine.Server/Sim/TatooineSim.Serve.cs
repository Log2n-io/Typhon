using SwgTatooine.Server.Accounts;
using SwgTatooine.Server.Terrain;
using System;
using System.IO;
using System.Threading.Tasks;

namespace SwgTatooine.Server;

/// <summary>Serving the simulation to clients, as opposed to measuring it.</summary>
/// <remarks>
/// <para>
/// <b>Why this is a second entry point and not a flag inside <c>Run</c>.</b> The measurement path is built around a fixed tick count, a warm-up window and a
/// summary; a server has none of those and would have to opt out of all three. Keeping them apart means the benchmark keeps measuring exactly what it always
/// measured, and the server is free to run forever.
/// </para>
/// <para>
/// Everything else is shared: the same world build, the same systems, the same schedule. The only additions are the replication declaration, the system that
/// binds an opened session to a profile, and the web host.
/// </para>
/// </remarks>
public sealed partial class TatooineSim
{
    /// <summary>
    /// Builds the views and the schedule, starts the runtime, and serves it over WebSocket until the process stops.
    /// </summary>
    /// <param name="port">The port to listen on.</param>
    /// <param name="clientRoot">A directory of built client files to serve at the root, or <see langword="null"/>.</param>
    /// <param name="bindAddress">The address to bind, or <see langword="null"/> for loopback.</param>
    /// <param name="origins">Origins a browser may connect from; required when <paramref name="bindAddress"/> is not loopback.</param>
    /// <returns>A task that completes when the host has stopped.</returns>
    public async Task ServeAsync(int port, string clientRoot, string bindAddress = null, string[] origins = null)
    {
        // Opened BEFORE the runtime is replicating: a published folder that is missing its catalogue is a deployment
        // fault, and the moment to say so is before a session can connect and be served a world with no geometry.
        var assets = OpenAssetOrigin();
        var shapes = _shapes;
        // Read through the field at each request: the bridge is made by StartReplication, below.
        EconomyEndpoints.CountersJson = () => _bridge?.EconomyCountersJson() ?? "{}";
        EconomyEndpoints.CrowdJson = () => _bridge?.CrowdCountersJson() ?? "{}";
        EconomyEndpoints.DemoJson = () => _bridge?.DemoStatsJson() ?? "{}";
        TatooineHost.DashboardPort = _config.DashboardPort;
        Console.WriteLine($"progression: XP table from the model, CL 50 at {Progression.MaxXp:N0} XP; --xp-rate {_config.XpRatePercent} %");
        StartReplication(shapes);
        try
        {
            await TatooineHost
                .ServeAsync(
                    _runtime, port, clientRoot, origins, RealmDirectoryJson(), RealmInventoryJson, bindAddress, _config.AuthBurst, _config.AuthRefillMs,
                    _config.OriginSecret, _config.AuthLocalOnly, _config.TlsChainPath, _config.TlsKeyPath, _config.TlsPort, _config.RedirectPort,
                    assets, shapes, _config.WorldEdgeM * 0.5)
                .ConfigureAwait(false);
        }
        finally
        {
            _runtime.Shutdown();

            // What the run did, on the way out. A served run is the only place this feature meets a real client
            // population, so it is the only place its counters mean anything -- and a run that has to be killed to
            // end still gets to say what it measured.
            PrintProximityReport();
            Console.WriteLine($"  economy: {_bridge?.EconomyCountersJson() ?? "{}"}");
            Console.WriteLine($"  items: {_bridge?.ItemCountersJson() ?? "{}"}");
            Console.WriteLine($"  crowd: {_bridge?.CrowdCountersJson() ?? "{}"}");
            Console.WriteLine($"  progression: kill queue overflow xp {KillXpOverflow}, loot {KillLootOverflow}");
            EconomyEndpoints.CountersJson = null;
            EconomyEndpoints.CrowdJson = null;
            EconomyEndpoints.DemoJson = null;
        }
    }

    /// <summary>
    /// The runtime this simulation is ticking, once <see cref="StartReplication"/> or <see cref="Run"/> has started one.
    /// </summary>
    /// <remarks>
    /// Public for the same reason <see cref="Dbe"/> is: this is a demo whose purpose is to be driven from outside — by <c>Program</c>, by a sweep, and by
    /// the checks beside it. A test that has to reach a private field through reflection is a test that breaks when the field is renamed, and stops
    /// testing when the field is removed.
    /// </remarks>
    public TyphonRuntime Runtime => _runtime;

    /// <summary>
    /// The published asset folder this run serves and collides against, or <see langword="null"/> when the config names none: a measured run without
    /// <c>--assets</c>, or a check. <c>Program</c> never serves without one (<see cref="ServedAssetRoot"/>).
    /// </summary>
    /// <returns>The origin, validated.</returns>
    /// <exception cref="ArgumentException">The folder, its pointer, or the catalogue it names is missing or malformed.</exception>
    internal AssetOrigin OpenAssetOrigin() => _config.AssetDirectory == null ? null : AssetOrigin.Open(_config.AssetDirectory);

    /// <summary>
    /// The collision shapes behind the footprints, read once.
    /// </summary>
    /// <param name="assets">What <see cref="OpenAssetOrigin"/> returned, or <see langword="null"/>.</param>
    /// <returns>The collider table, or <see langword="null"/> on a run with no asset set.</returns>
    /// <remarks>
    /// <b>Both the served and the measured path need this, and for a while only the served one had it.</b> Collision is
    /// per body and a measured run is the only way to put thousands of bodies on it, so a measured run that could not
    /// load the shapes could only ever measure the footprint fallback — the path a real client never takes. Only the
    /// assets whose colliders have been checked against the blocking rule are loaded; everything else keeps its
    /// footprint box (see <see cref="AssetColliders"/>).
    /// </remarks>
    internal static AssetColliders LoadAssetColliders(AssetOrigin assets)
    {
        if (assets == null)
        {
            return null;
        }

        var shapes = AssetColliders.Load(assets);
        // The gate's growth PER RESERVATION, not just the worst of them: a terminal reserving 1.6 m and overhanging
        // nothing is the common case, and a line reporting only the maximum hid that every structure was charged it.
        var gates = new System.Text.StringBuilder();
        foreach (var (halfM, overhangM) in shapes.OverhangTable)
        {
            gates.Append(gates.Length == 0 ? " gate" : ",").Append(FormattableString.Invariant($" {halfM * 2f:F1} m +{overhangM:F2}"));
        }

        Console.WriteLine($"  collision: {shapes.AssetCount} asset(s), {shapes.BoxCount} blocking primitive(s),"
            + $"{gates} (worst +{shapes.MaxOverhangM:F2} m)");
        return shapes;
    }

    /// <summary>
    /// Everything <see cref="ServeAsync"/> does except the web host: the views, the bridge, the replication declarations and a started runtime.
    /// </summary>
    /// <remarks>
    /// <b>Split out so that replication can be exercised without a socket.</b> A transport is a seam
    /// (<c>design/Subscriptions/04-transport.md § 2</c>): a test starts its own in-process transport against
    /// <see cref="TyphonRuntime.StartSubscriptionTransport"/> and receives the same frames a WebSocket would carry. Going through the web host instead would
    /// test Kestrel, bind a port, and make a check that is about a <c>KICK</c> fail for a reason that has nothing to do with one.
    /// </remarks>
    public void StartReplication(AssetColliders shapes = null)
    {
        // Before anything else, so a misconfigured SWG_AUTH_KEY stops the server here rather than surfacing as a wrapped
        // TypeInitializationException inside the admission hook, on a transport thread, for the first client that connects.
        AuthToken.EnsureConfigured();

        BuildViews();

        _bridge = new SimBridge(_config, Map, Index)
        {
            Dbe = Dbe,
            PlanetIndexes = Indexes,
            Portals = Portals,
            InteriorsPerPlanet = InteriorsPerPlanet,
            FirstDungeonRealm = FirstDungeonRealm,
            PlayerView = _playerView,
            CreatureView = _creatureView,
            NpcView = _npcView,
            ShipView = _shipView,
            LairView = _lairView,
            StructureView = _structureView,

            // Null on a run without --assets, which is what keeps the asset pipeline out of a measurement that does
            // not want it. Handed to the constructor so there is no moment at which the bridge exists without it.
            AssetShapes = shapes,
        };

        // The cities' buildings, as the crowd's walks round them read them (SimBridge.Routes.cs): once, now the world is built or reopened.
        _bridge.PlanCityWalks();

        _runtime = TyphonRuntime.Create(Dbe, BuildServeSchedule, new RuntimeOptions
        {
            BaseTickRate = _config.TickRateHz,
            WorkerCount = _config.ResolveWorkerCount(),
            ParallelQueryMinChunkSize = _config.ParallelQueryMinChunkSize,
            CostBasedChunking = _config.CostBasedChunking,
            EnableParallelFence = _config.ParallelFence,

            // The same strict policy the measured path takes (P-3), and for the server the argument is if anything stronger. Under the engine's default a
            // faulting system's branch is skipped and the tick is reported as a SUCCESS, so a server whose combat resolution threw would keep publishing frames
            // of a world in which nothing resolves — clients would see a frozen fight and no operator would learn why. A terminal stop is diagnosable and
            // 08-hosting's systemd unit already answers it with Restart=on-failure, which restarts from a checkpoint rather than limping on.
            SystemExceptionPolicy = SystemExceptionPolicy.AbortTickAndStop,

            // --subs-mode. Every other field of the options is left at its default: these are the ones an A/B moves, and moving another
            // would make the two arms differ in more than the thing being measured.
            Subscriptions = new SubscriptionsOptions
            {
                // <b>One ping a second, so the server's silence bound is 3 s rather than 750 ms.</b> The bound is
                // `3 s / PingHz` (SkipPolicy.SilenceBoundTicks) and the engine's default of 4 therefore cuts any
                // session it has not heard from for three quarters of a second. That is defensible for a machine-driven
                // client and wrong for a browser: Chrome throttles a background tab's timers to about 1 Hz, so merely
                // alt-tabbing exceeded it — measured here as 141 `closedSilent` closures in one evening against a server
                // whose tick p50 was 1.2 ms of a 100 ms budget with zero overruns. A long GC or a slow frame did it too.
                PingHz = 1,

                AllowAutomaticPushDetection = _config.SubscriptionsPushAutomatic,

                // The replication cell, set in Initialize. It TRACKS the planet's spatial cell and is not equal to it: a floor of PlayerRadiusM / 3 applies,
                // so --world 2 (spatial cell 31.25 m) replicates on 64 m cells, not 31.25 m ones. At the default 64 m that is an 11 x 11 window for a 192 m
                // session and 15 x 15 at the slider's ceiling; whenever the floor bites the two numbers differ and only the spatial one is printed.
                ReplicationCellM = TatooineReplication.ReplicationCellM,

                // Each session's inbound budget (--ingress-budget): required once clients can send commands, and the god camera's ClientRegion is one.
                IngressBytesPerSecond = _config.IngressBytesPerSecond,

                // --resume-grace (A13). Passed through rather than left at the engine's default, because this is the one option a client ACTS on: it reaches
                // the catalog as `limits.resumeGraceMs`, and an SDK reads it to choose between presenting its token and connecting fresh.
                ResumeGraceMs = (int)Math.Round(_config.ResumeGraceS * 1000d),

                // --max-sessions. Left at the engine's default, this is the ceiling a 10 000-bot fleet silently stopped at: 8 192, refused at connect time
                // with nothing said on either side. Passed through so the number is the operator's, and printed below so it is not discovered by a run.
                MaxSessions = _config.MaxSessions,

                // Required once a group set is declared, and it is an allocation rather than a ceiling: every session holds this many keys and cursors per
                // set whether it meets a city or not. See TatooineReplication.MaxCityGroupsPerSession for why four.
                MaxGroupsPerSession = TatooineReplication.MaxCityGroupsPerSession,
            },
        });

        // Before Start, because the catalog a client negotiates against is compiled there and the declarations are its source.
        TatooineReplication.PlayerLeaveM = _config.PlayerLeaveM;
        TatooineReplication.GodRegionMaxEdgeM = _config.GodRegionMaxEdgeM;
        TatooineReplication.GodNearBudget = _config.GodNearBudget;
        TatooineReplication.Planets = _config.Planets;
        TatooineReplication.MaxClients = _config.MaxClients;
        TatooineReplication.MaxSpectators = _config.MaxSpectators;
        TatooineReplication.SessionTableSize = _config.MaxSessions;

        // What an intent is validated against: the world it must stay inside, and the tick it gets one step of (SWG-01). Required rather than defaulted, so a
        // path that forgot it would refuse to start rather than clamp to the wrong world silently.
        TatooineReplication.ConfigureIntents(Dbe, _config.WorldEdgeM, _config.TickRateHz);

        // Where a first login arrives (A5): the capital's centre, on the ground, in planet 0. Taken from the map rather than
        // defaulted to the origin, which on this planet is open desert a long way from anything a new player could interact with.
        var capital = Map.Cities.Count > 0 ? Map.Cities[0] : default;
        var terrain = TerrainField.Shared(_config.ContentScale);
        var spawnY = terrain.GroundAt(capital.X, capital.Z);

        // <b>Every town, by the map's own playerWeight, rather than a stack in the capital.</b> One point was the original behaviour and
        // put the whole population inside every session's sphere; one TOWN still put ~1 770 bodies there at ten thousand arrivals, which
        // is 124 records a frame and a fleet process that died of its own inbound traffic while the server held its tick budget. The
        // seven towns are up to 8.8 km apart, so sessions in different ones are mutually invisible, and the weights have been sitting in
        // TatooineData.Cities since it was written.
        var spawnAreas = BuildSpawnAreas();
        // The last two arguments are A6: how often a live avatar's position is written back, which is the bound on what a kill -9
        // costs. Stated as seconds and turned into ticks here, so the same number means the same thing at 10 Hz and at 50 Hz.
        TatooineReplication.ConfigureAvatars(
            Dbe, new Point3F { X = capital.X, Y = spawnY, Z = capital.Z }, 0, _config.TickRateHz, _config.AvatarSaveIntervalS,
            spawnAreas, terrain.GroundAt);

        PrintSpawnAreas(spawnAreas);

        // States the obligation rather than leaving it to the first login to discover. See ThrowIfAvatarsNotConfigured.
        TatooineReplication.ThrowIfAvatarsNotConfigured();
        TatooineReplication.Declare(_runtime.Subscriptions, _config.SubscriptionsPushAutomatic);
        TatooineReplication.PlayerBudgetBytesPerSecond = _config.SessionBudgetBytesPerSecond;

        // What a BoardShuttle ask is validated against. Handed over rather than reached for: the ports live in the world index, which the bridge owns.
        // A server running without shuttles answers 0 cities, which refuses every ask as a destination that does not exist.
        TatooineReplication.ShuttleBoarding = _bridge;

        var aborts = 0;
        _runtime.OnTickAborted += (_, outcome) =>
        {
            NoteTickAbort(outcome.TickNumber, outcome.Reason.ToString(), outcome.FailedSystemName, outcome.FailedSystemException);
            Console.WriteLine($"  !! tick {outcome.TickNumber} aborted: {outcome.Reason} in '{outcome.FailedSystemName}': {outcome.FailedSystemException}");

            // The artefact, on the first abort only, exactly as the measured path does it (P-3). For a server this is the whole of the diagnosis: there is no
            // report at the end of a run that never ends, so a fault that is not written down is a fault nobody can look at afterwards.
            if (++aborts == 1)
            {
                CrashArtefactPath = WriteCrashArtefact(
                    $"tick aborted: {outcome.Reason} in system '{outcome.FailedSystemName}'", outcome.FailedSystemException, outcome.TickNumber);
            }
        };

        TatooineReplication.Scheduler = _runtime.Scheduler;
        _runtime.Start();

        // The catalog every client negotiates against, by its hash: two builds that print the same one serve the same wire (AC-25 compares the attribute
        // declarations against the builder ones this way).
        var catalog = _runtime.SubscriptionsCatalogJson;
        Console.WriteLine($"  catalog {Typhon.Protocol.CatalogSerializer.HashBytes(catalog.Span):X16}, {catalog.Length} B");

        // Where the token signing key came from, and NEVER the key itself. Printed because the consequence is invisible otherwise: with a per-process key,
        // every token a browser holds stops working the moment this process restarts, and the operator finds that out from a user rather than from the log.
        Console.WriteLine(AuthToken.KeyIsPersistent
            ? $"  auth: tokens signed with {AuthToken.KeyVariable} — they survive a restart"
            : $"  auth: tokens signed with a key generated for this process — every token stops working on restart. "
              + $"Set {AuthToken.KeyVariable} (openssl rand -base64 32) for an instance that is kept.");

        // The work factor, and LOUDLY when it has been lowered. A cheaper hash is a legitimate trade for a throwaway demo
        // and an indefensible one for anything holding real credentials, so the difference must be visible in the log
        // rather than buried in a command line nobody re-reads. Applied here, once, before any signup can run.
        if (_config.AuthIterations > 0 && _config.AuthIterations != PasswordHash.DefaultIterations)
        {
            PasswordHash.Iterations = _config.AuthIterations;
            Console.WriteLine(
                $"  !! password hashing LOWERED to {_config.AuthIterations:N0} iterations from the default "
                + $"{PasswordHash.DefaultIterations:N0}. Cheaper to attack offline if the hash table ever leaks — "
                + "acceptable for throwaway accounts, not for real credentials. Existing accounts keep their own work factor.");
        }
        else
        {
            Console.WriteLine($"  auth: passwords hashed with {PasswordHash.Iterations:N0} PBKDF2 iterations");
        }
    }

    /// <summary>The simulation's own schedule, plus the two systems a server needs that a benchmark does not.</summary>
    /// <remarks>
    /// <b>They join the simulation's DAG rather than a DAG of their own, which they had until SWG-01.</b> Two DAGs on one track have no barrier between them
    /// and cannot carry an edge to each other, so a session system in its own DAG could run concurrently with <c>PlayerThink</c> — which matters the moment it
    /// applies an intent, because both write <c>PlayerMotion</c> — and concurrently with a second session system, which matters because session requests share
    /// one unsynchronized segment. See <see cref="TatooineReplication.SessionTick"/>.
    /// </remarks>
    private void BuildServeSchedule(RuntimeSchedule schedule)
        => BuildSchedule(schedule, replicating: true);

    /// <summary>Where the built browser client is expected, relative to the repository root.</summary>
    /// <param name="baseDirectory">The process's base directory.</param>
    /// <returns>The directory, whether or not it exists.</returns>
    /// <remarks>
    /// The client is a Vite build under <c>demo/SwgTatooine/SwgTatooine.Client/dist</c>, and <c>dotnet build</c> does not run Vite — so a fresh checkout has no client to
    /// serve and the host says so rather than answering 404 for the root and leaving the operator guessing.
    /// </remarks>
    public static string DefaultClientRoot(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "demo", "SwgTatooine", "SwgTatooine.Client", "dist");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>The published asset folder a served run uses: <c>--assets</c>, else one beside the binary, else the repository's.</summary>
    /// <param name="assetDirectory"><c>--assets</c>, or <see langword="null"/>.</param>
    /// <param name="baseDirectory">The process's base directory.</param>
    /// <returns>The folder to open, never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException">No folder holding a <see cref="AssetOrigin.PointerName"/> was found.</exception>
    /// <remarks>
    /// <para>
    /// <b>A served run without assets is refused, not degraded.</b> Collision, portals, seats and interactions all come from the descriptors; a world served
    /// on footprint boxes is one no client can play, so the moment to say so is before the world is built.
    /// </para>
    /// <para>
    /// <c>&lt;binary&gt;/assets</c> is a deployment's layout (<c>scripts/publish-swg.ps1</c> copies the set there);
    /// <c>demo/SwgTatooine/SwgTatooine.Assets/build/assets</c>, walked up to from the binary, is a dev tree's, where <c>assets.py fetch published</c> unpacks
    /// it. Served from where it is rather than copied into <c>bin/</c>, the way <see cref="DefaultClientRoot"/> serves the client: a copy goes stale on the
    /// next fetch. A measured run does not come here: what it collides against is part of what it measures, so it stays what <c>--assets</c> says.
    /// </para>
    /// </remarks>
    public static string ServedAssetRoot(string assetDirectory, string baseDirectory)
    {
        if (assetDirectory != null)
        {
            return assetDirectory;
        }

        var beside = Path.Combine(baseDirectory, "assets");
        if (File.Exists(Path.Combine(beside, AssetOrigin.PointerName)))
        {
            return beside;
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "demo", "SwgTatooine", "SwgTatooine.Assets", "build", "assets");
            if (File.Exists(Path.Combine(candidate, AssetOrigin.PointerName)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new ArgumentException(
            $"--serve needs the published asset set, and there is none: no --assets, no {AssetOrigin.PointerName} in '{beside}', and no "
            + $"demo/SwgTatooine/SwgTatooine.Assets/build/assets above '{baseDirectory}'. "
            + "Fetch it with: python demo/SwgTatooine/SwgTatooine.Assets/scripts/assets.py fetch published");
    }

    /// <summary>The towns new characters are spread across, from the map and <c>--spawn-spread</c>.</summary>
    /// <returns>One area per town, or empty for the single-point behaviour.</returns>
    /// <remarks>
    /// <c>playerWeight</c> is taken as it is: it is the map author's statement about where players live, it already sums to 1.00, and
    /// re-deriving a distribution from building or NPC counts would be inventing a second opinion about the same thing.
    /// </remarks>
    private TatooineReplication.SpawnArea[] BuildSpawnAreas()
    {
        if (_config.SpawnSpread <= 0 || Map.Cities.Count == 0)
        {
            return [];
        }

        var areas = new TatooineReplication.SpawnArea[Map.Cities.Count];
        for (var i = 0; i < areas.Length; i++)
        {
            var city = Map.Cities[i];
            areas[i] = new TatooineReplication.SpawnArea(city.X, city.Z, city.Radius * _config.SpawnSpread, city.PlayerWeight);
        }

        return areas;
    }

    /// <summary>Prints where arrivals will land and how crowded that makes each town.</summary>
    /// <param name="areas">The areas, before normalisation.</param>
    /// <remarks>
    /// <b>Printed because the crush was discovered ninety seconds into a run rather than read off the banner.</b> The density that
    /// matters is not bots per town but how many of them land inside one session's sphere, since that is what becomes records in a
    /// frame — so it is stated per thousand players, which is a figure that does not need to know the fleet's size.
    /// <para>
    /// It also makes the uneven case visible: the weights are population shares and the radii are physical sizes, so the smallest town
    /// with a non-trivial share is the densest. Anchorhead is 125 m with 8 % of arrivals, which is denser than the capital at 34 %.
    /// </para>
    /// </remarks>
    private static void PrintSpawnAreas(TatooineReplication.SpawnArea[] areas)
    {
        if (areas.Length == 0)
        {
            Console.WriteLine("  spawn: ONE POINT at the capital's centre (--spawn-spread 0) — every arrival inside every other's sphere");
            return;
        }

        var sphere = Math.PI * TatooineReplication.PlayerRadiusM * TatooineReplication.PlayerRadiusM;
        var total = 0d;
        foreach (var area in areas)
        {
            total += area.Weight;
        }

        Console.WriteLine($"  spawn: {areas.Length} town(s), per 1000 players — share, radius, and how many land in one {TatooineReplication.PlayerRadiusM:F0} m sphere:");
        foreach (var area in areas)
        {
            var share = total > 0 ? area.Weight / total : 0d;
            var disc = Math.PI * area.RadiusM * area.RadiusM;

            // Capped at the town's own population: a sphere wider than the town cannot hold more than the town has.
            var inSphere = disc > 0 ? Math.Min(1000d * share, 1000d * share * sphere / disc) : 1000d * share;
            Console.WriteLine($"    {area.X,8:F0},{area.Z,8:F0}  r{area.RadiusM,6:F0} m  {share * 100,5:F1} %  ->{inSphere,7:F0} within a sphere");
        }
    }
}
