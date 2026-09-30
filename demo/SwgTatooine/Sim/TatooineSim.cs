using System;
using System.IO;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace SwgTatooine;

/// <summary>
/// Owns the database, the world and the system schedule for one simulated Tatooine.
/// </summary>
/// <remarks>
/// <para>One instance is one configuration point in the sweep. It builds a database on disk, populates it, runs the
/// system DAG through <see cref="TyphonRuntime"/> for a fixed number of ticks and reports what that cost.</para>
/// <para><b>Why the database is real.</b> The engine's other spatial workloads run in <c>TestMode</c> against a page
/// file that is never durable. That measures the spatial layer cleanly, and it is the right choice there. This one runs
/// on a real file with a real WAL because the question here is what a game server costs, and a game server pays for its
/// durability. What it does NOT pay is a WAL record per creature per tick: every archetype but the inventory half of
/// <see cref="Player"/> declares <see cref="ClusterDurability.Checkpoint"/>, so simulation state reaches disk through
/// the checkpoint it was always going to be written by, and the WAL carries only what a player would miss.</para>
/// </remarks>
public sealed partial class TatooineSim : IDisposable
{
    private readonly SimConfig _config;

    // The process-wide narrowphase switch as it was before this simulation set it, put back on Dispose: a later engine in the same process must not inherit
    // this run's A/B arm.
    private readonly bool _simdNarrowphaseBefore;
    private ServiceProvider _serviceProvider;
    private IServiceScope _scope;
    private TyphonRuntime _runtime;

    /// <summary>The live database engine.</summary>
    public DatabaseEngine Dbe { get; private set; }

    /// <summary>The reconstructed planet — cities, points of interest, spawn regions.</summary>
    public TatooineMap Map { get; private set; }

    /// <summary>Populations actually created, for the report.</summary>
    public WorldCensus Census { get; private set; }

    /// <summary>
    /// What is standing in each realm right now, recounted once a second while this process is serving.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Census"/>, which is what the world was BUILT with and never changes: this one is walked
    /// off the clusters as they are, so it follows every crossing, spawn and dungeon. Empty in a run that is not serving
    /// — see <see cref="SimConfig.RealmCensusHz"/> for why the measured path does not pay for it.
    /// <para>
    /// <b>Allocated only by a serving run</b>, through <see cref="SizeRealmCensus"/>, which is the same rule the census system's own gating follows and is
    /// there for the same reason: a measured run must execute the code it always did. Sized in <c>Initialize</c> it allocated four arrays over every realm
    /// slot in EVERY run — about 19 KB on a two-planet world, nothing on a one-planet one — for a panel a measured run never serves. Small, and still the
    /// wrong side of the line this demo's CPU numbers are taken on.
    /// </para>
    /// </remarks>
    public RealmCensus RealmPopulation { get; private set; } = new(1);

    /// <summary>How many realm slots this run configured, for <see cref="SizeRealmCensus"/>.</summary>
    private int _realmSlots = 1;

    /// <summary>
    /// Sizes <see cref="RealmPopulation"/> for this run's realms — including the dungeon slots, since a dungeon is registered while a party is inside one,
    /// which is exactly when a viewer wants to see it. Called by the serving path only.
    /// </summary>
    private void SizeRealmCensus() => RealmPopulation = new RealmCensus(_realmSlots);

    /// <summary>
    /// How many ticks the runtime actually executed, read after the shutdown rather than at the poll that requested it.
    /// </summary>
    /// <remarks>
    /// Unpaced, this exceeds the requested count by however many ticks fit between the polling loop noticing the target and <c>Shutdown</c> landing — so a
    /// run's length is not reproducible and neither is its end state. The measured window is clamped to the request; this is the number to quote when a claim
    /// is per-tick.
    /// </remarks>
    public long Executed { get; private set; }

    /// <summary>
    /// True when this instance opened a database that already held a world, so the world was REOPENED rather than built (P-2).
    /// </summary>
    /// <remarks>
    /// Decided before the engine opens the file, because opening it creates it — after that there is no way to tell a world that was already there from one
    /// this process has just made.
    /// </remarks>
    public bool Reopened { get; private set; }

    /// <summary>This planet's interior realm ids (Realms G1b), or empty without <c>--interiors</c>.</summary>
    private ushort[] InteriorRealmsOf(int planet)
    {
        if (InteriorsPerPlanet == 0)
        {
            return [];
        }

        var first = _config.Planets + (planet * InteriorsPerPlanet);
        var realms = new ushort[InteriorsPerPlanet];
        for (var i = 0; i < InteriorsPerPlanet; i++)
        {
            realms[i] = (ushort)(first + i);
        }

        return realms;
    }

    /// <summary>Is there already a database where this configuration would open one?</summary>
    /// <remarks>
    /// <para>
    /// <b><c>GetFileSystemEntries</c>, not <c>GetFiles</c>, and the difference is the whole method.</b> A Typhon database is a DIRECTORY — <c>&lt;name&gt;.typhon</c>
    /// — so <c>GetFiles</c> answers "no database here" for every database that exists. Measured: with <c>GetFiles</c> the reopen path never fired once, and three
    /// consecutive <c>--persist</c> runs each built a fresh world on top of the last while reporting a census that matched perfectly, because the census counts
    /// what the generator made rather than what the database holds.
    /// </para>
    /// <para>Any entry matching the name counts. Treating a half-written database as absent would delete the rest of it by building on top.</para>
    /// </remarks>
    private bool DatabaseFileExists()
    {
        var dir = _config.DatabaseDirectory ?? AppContext.BaseDirectory;
        if (!Directory.Exists(dir))
        {
            return false;
        }

        return Directory.GetFileSystemEntries(dir, _config.DatabaseName + ".*").Length > 0;
    }

    /// <summary>Entity handles and place geometry the systems address after the build.</summary>
    /// <summary>Each planet's destinations and handles, indexed by its realm (Realms G1).</summary>
    public WorldIndex[] Indexes { get; private set; } = [new WorldIndex()];

    /// <summary>Planet 0's index — the single world's.</summary>
    public WorldIndex Index => Indexes[0];

    /// <summary>Interior realms per planet (Realms G1b): the enterable city buildings, 0 without <c>--interiors</c>.</summary>
    public int InteriorsPerPlanet { get; private set; }

    /// <summary>The space realm (Realms G1c), after the planets and the interiors; -1 without <c>--space</c>.</summary>
    public int SpaceRealm { get; private set; } = -1;

    /// <summary>The first dungeon slot's realm id (Realms G2): <c>--dungeons</c> ids from here, registered at run time.</summary>
    public int FirstDungeonRealm { get; private set; }

    /// <summary>A realm simulated at full rate always (divisor 1), or unobserved at <paramref name="divisor"/>; served to sessions as <paramref name="replication"/>.</summary>
    private static RealmConfig Divided(SpatialGridConfig grid, int divisor, RealmReplicationConfig replication) =>
        new() { Grid = grid, WhenUnobserved = RealmUnobserved.Simulate, UnobservedTickDivisor = Math.Max(1, divisor), Replication = replication };

    // How each kind of realm is served to sessions (Realms G3, 12-realms § 2.1): planets at the planet's replication cell, an interior as one cell,
    // space at its own 500 m cell. Planet 0 is ConfigureSpatialGrid's realm, served at SubscriptionsOptions.ReplicationCellM.
    //
    // Each carries an AppTag (RealmTag): the one field a client reads to choose a scene, so a browser never has to
    // reproduce "interior j of planet p is realm Planets + p*InteriorsPerPlanet + j" from flags it cannot see. They are
    // built PER REALM rather than shared for that reason — the tag differs even where the rest of the config does not.
    private static RealmReplicationConfig PlanetReplication(int planet) =>
        new() { CellM = TatooineReplication.ReplicationCellM, AppTag = RealmTag.Planet(planet) };

    internal static RealmReplicationConfig InteriorReplication(int portal) =>
        new() { Kind = TatooineReplication.InteriorKind, CellM = WorldBuilder.InteriorEdgeM, AppTag = RealmTag.Interior(portal) };

    /// <summary>A dungeon is served exactly like an interior and tagged as its own scene, so a client can tell them apart.</summary>
    internal static RealmReplicationConfig DungeonReplication(int slot) =>
        new() { Kind = TatooineReplication.InteriorKind, CellM = WorldBuilder.InteriorEdgeM, AppTag = RealmTag.Dungeon(slot) };

    private static RealmReplicationConfig SpaceReplication() =>
        new() { Kind = TatooineReplication.SpaceKind, CellM = 500d, AppTag = RealmTag.Space() };

    public TatooineSim(SimConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;

        // A latency knob on the shared pool, set here rather than defaulted in the engine so a sweep can move it: see DagScheduler.WorkerIdleSpinBudget.
        DagScheduler.WorkerIdleSpinBudget = config.WorkerIdleSpin;
        if (config.WorkerHotSpinners is { } hot)
        {
            DagScheduler.WorkerHotSpinners = hot;
        }

        if (config.WorkerParkAfterUs is { } parkUs)
        {
            DagScheduler.WorkerParkAfterUs = parkUs;
        }

        DagScheduler.MeasureWorkerIdle = config.SubscriptionsPhaseTiming;
        _simdNarrowphaseBefore = Typhon.Engine.Internals.SpatialQueryTuning.SimdNarrowphase;
    }

    /// <summary>The configuration this simulation was built from.</summary>
    public SimConfig Config => _config;

    /// <summary>
    /// Build the database, configure the grid and populate the world. Nothing ticks yet.
    /// </summary>
    public void Initialize()
    {
        var services = new ServiceCollection();
        services
            .AddLogging(cfg => cfg.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opt =>
            {
                // NO process id (P-1). It used to be `SwgTatooine_{Environment.ProcessId}`, so every run had a database of its own and every run KILLED before
                // it could delete its own file left one behind — 154 GB of them, once, because the only process that knew the name was gone. One stable name
                // makes "a killed run leaves ONE database" structural instead of a rule somebody has to remember.
                opt.DatabaseName = _config.DatabaseName;
                opt.DatabaseDirectory = _config.DatabaseDirectory ?? AppContext.BaseDirectory;
                opt.DatabaseCacheSize = (ulong)_config.PageCacheMiB * 1024UL * 1024UL;
            })
            .AddScopedDatabaseEngine(opt =>
            {
                // WAL on, deliberately. Turning it off is not the way to make a fence cheap — it forces the unit of work
                // to sync pages itself instead of handing durability to the writer thread, and AntHill measured that as
                // 4x SLOWER. The per-archetype ClusterDurability.Checkpoint declarations are the mechanism that keeps
                // simulation state out of the WAL, and they leave the writer thread doing the work it is good at.
                opt.Wal = new WalWriterOptions();

                // The promotion gates are engine options rather than grid config, because they govern the broadphase a
                // cell uses rather than the geometry of the grid itself.
                opt.Spatial.CellTreePromoteThreshold = _config.CellTreePromoteThreshold;
                opt.Spatial.CellTreePromoteTightness = _config.CellTreePromoteTightness;
            });

        _serviceProvider = services.BuildServiceProvider();

        // The one persistence decision in the program (P-1), taken here and nowhere else. Without --persist the file goes, which is the benchmark's default
        // because a run that quietly measures a world some earlier run left behind is a corrupted A/B that says nothing about itself.
        if (!_config.Persist)
        {
            _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        }

        // Whether this open found a world already there is what decides between building one and rebuilding the index over one (P-2). Read BEFORE the engine
        // opens the file, because opening it creates it.
        Reopened = _config.Persist && DatabaseFileExists();
        _scope = _serviceProvider.CreateScope();
        Dbe = _scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        Dbe.RegisterComponentFromAccessor<StructurePlacement>();
        Dbe.RegisterComponentFromAccessor<LairPlacement>();
        Dbe.RegisterComponentFromAccessor<NpcPlacement>();
        Dbe.RegisterComponentFromAccessor<CreaturePlacement>();
        Dbe.RegisterComponentFromAccessor<PlayerPlacement>();
        Dbe.RegisterComponentFromAccessor<CreatureMotion>();
        Dbe.RegisterComponentFromAccessor<PlayerMotion>();
        Dbe.RegisterComponentFromAccessor<NpcMotion>();
        Dbe.RegisterComponentFromAccessor<CreatureVitals>();
        Dbe.RegisterComponentFromAccessor<PlayerVitals>();
        Dbe.RegisterComponentFromAccessor<LairVitals>();
        Dbe.RegisterComponentFromAccessor<CreatureBrain>();
        Dbe.RegisterComponentFromAccessor<NpcBrain>();

        // Scheduling, split out of the two brains above so a write to it reaches no subscriber. See CreatureTimers.
        Dbe.RegisterComponentFromAccessor<CreatureTimers>();
        Dbe.RegisterComponentFromAccessor<NpcTimers>();
        Dbe.RegisterComponentFromAccessor<PlayerState>();

        // Possession and targeting, split out of PlayerState so a write to it reaches no subscriber (SWG-01). See PlayerControl.
        Dbe.RegisterComponentFromAccessor<PlayerControl>();
        Dbe.RegisterComponentFromAccessor<PlayerSession>();
        Dbe.RegisterComponentFromAccessor<Lair>();
        Dbe.RegisterComponentFromAccessor<Structure>();
        Dbe.RegisterComponentFromAccessor<Inventory>();
        Dbe.RegisterComponentFromAccessor<StructureRealm>();
        Dbe.RegisterComponentFromAccessor<LairRealm>();
        Dbe.RegisterComponentFromAccessor<NpcRealm>();
        Dbe.RegisterComponentFromAccessor<CreatureRealm>();
        Dbe.RegisterComponentFromAccessor<PlayerRealm>();
        Dbe.RegisterComponentFromAccessor<ShipPlacement>();
        Dbe.RegisterComponentFromAccessor<ShipMotion>();
        Dbe.RegisterComponentFromAccessor<ShipRealm>();

        // SWG's coordinates are centred on the planet: -8192..+8192 on each axis at the real size. Keeping the origin in
        // the middle rather than at a corner is not cosmetic — every authentic coordinate in the map data is expressed
        // that way, and shifting them would put a transcription error between the source and the world.
        var half = _config.WorldEdgeM * 0.5f;
        var cell = _config.ResolveCellSize();
        var planetGrid = SpatialGridConfig.Flat(
            worldMin: new Vector2(-half, -half),
            worldMax: new Vector2(half, half),
            cellSize: cell,
            clusterTargetExtentRatio: _config.ClusterTargetExtentRatio,
            clusterRepairExtentRatio: _config.ClusterRepairExtentRatio,
            clusterRepairCriticalExtentRatio: _config.ClusterRepairCriticalExtentRatio,
            reclusterBudgetMs: _config.ReclusterBudgetMs,
            repairWorstClustersPerUnit: _config.RepairWorstClustersPerUnit,
            repairCooldownTicks: _config.RepairCooldownTicks,
            queryEfficiencyTolerance: _config.QueryEfficiencyTolerance,
            clusterTargetPackingSlack: _config.ClusterTargetPackingSlack,
            batchSpawnSortThreshold: _config.BatchSpawnSortThreshold);

        // Realms (G1): planet 0 is realm 0, configured as the single world always was; every further planet is a realm of its own with the same grid,
        // simulated always (per-realm policy is G2's). With --interiors, every enterable city building of every planet is a one-cell realm after the
        // planets: portal j of planet p is realm Planets + p·N + j. The map is built first because it fixes N, and realms are registered at open.
        // Reset BEFORE anything can be registered. It is process-static, so a run whose Initialize threw part-way — a
        // test fixture, a bad flag — would otherwise leave the previous run's larger value standing, and this one's
        // ViewRealm would admit realm ids its own engine never registered. Enter into an unregistered realm throws, on
        // the tick path.
        TatooineReplication.ViewableRealms = 1;

        Map = TatooineMap.Build(_config);
        InteriorsPerPlanet = _config.Interiors ? WorldBuilder.CountEnterable(Map) : 0;
        var realms = _config.Planets * (1 + InteriorsPerPlanet);
        SpaceRealm = _config.Space ? realms++ : -1;

        // Realm ids are ushort on the wire, and every registration below casts to one. With 617 interiors per planet the
        // count passes 65 535 at about 107 planets, and an unchecked cast would silently alias a later realm onto an
        // earlier one — while ViewableRealms, an int, still advertised the un-truncated range. Refused at start-up, where
        // a flag can be corrected, rather than mis-rendered at run time.
        if (realms + _config.Dungeons > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(_config.Planets),
                realms + _config.Dungeons,
                $"{realms + _config.Dungeons} realms exceeds the {ushort.MaxValue} a RealmId can hold: reduce --planets, --interiors or --dungeons.");
        }

        // What a god camera may ask to look at: everything registered at start-up, which is everything below the dungeon slots.
        TatooineReplication.ViewableRealms = realms;

        // Dungeon slots (G2): ids after space, each used by one instance — an id unregistered this session is not registrable again (RLM-06).
        FirstDungeonRealm = realms;
        realms += _config.Dungeons;

        // The census is NOT sized here, deliberately: see SizeRealmCensus, which the serving path calls and a measured run does not.
        _realmSlots = realms;
        if (realms > 1)
        {
            Dbe.ConfigureRealms(realms);
        }

        Dbe.ConfigureSpatialGrid(planetGrid);
        // Planet 0 runs at full rate always — it is the measured workload. A further planet is simulated at --planet-divisor (G2).
        for (var planet = 1; planet < _config.Planets; planet++)
        {
            Dbe.Realms.Register(new RealmId((ushort)planet), Divided(planetGrid, _config.PlanetDivisor, PlanetReplication(planet)));
        }

        // Interiors sleep once unobserved for --interior-sleep seconds (G2): a player walking in wakes one, and pins it while inside.
        var interiorGridConfig = SpatialGridConfig.Flat(Vector2.Zero, new Vector2(WorldBuilder.InteriorEdgeM, WorldBuilder.InteriorEdgeM),
            WorldBuilder.InteriorEdgeM);
        // Each interior's parent is its planet (Realms G3): a planet's news reaches the players in its buildings (RouteToRealm, subtree).
        RealmConfig InteriorOf(int planet, int portal) => new()
        {
            Grid = interiorGridConfig,
            WhenUnobserved = _config.InteriorSleepS > 0f ? RealmUnobserved.Sleep : RealmUnobserved.Simulate,
            UnobservedTickDivisor = 1,
            SleepAfterTicks = _config.InteriorSleepS > 0f ? Math.Max(1, (int)(_config.InteriorSleepS * _config.TickRateHz)) : 0,
            Parent = new RealmId((ushort)planet),
            Replication = InteriorReplication(portal),
        };

        for (var planet = 0; planet < _config.Planets && InteriorsPerPlanet > 0; planet++)
        {
            var first = _config.Planets + (planet * InteriorsPerPlanet);
            for (var realm = first; realm < first + InteriorsPerPlanet; realm++)
            {
                // One config per interior now, where a single shared one used to serve them all: the tag names which
                // portal this is, and that is exactly what a client needs to label the room it walked into.
                Dbe.Realms.Register(new RealmId((ushort)realm), InteriorOf(planet, realm - first));
            }
        }

        // Space (G1c): the last realm, a deep grid — a 16 km cube in 500 m cells, 32 deep — for the f64 starships.
        if (SpaceRealm >= 0)
        {
            var edge = WorldBuilder.SpaceEdgeM * 0.5;
            Dbe.Realms.Register(new RealmId((ushort)SpaceRealm),
                Divided(new SpatialGridConfig(new Vector3D(-edge, -edge, -edge), new Vector3D(edge, edge, edge), 500d), _config.SpaceDivisor, SpaceReplication()));
        }

        Dbe.InitializeArchetypes();

        // Process-wide and read when each query is built, so setting it here covers every query the run makes.
        Typhon.Engine.Internals.SpatialQueryTuning.SimdNarrowphase = _config.SimdNarrowphase;
        Typhon.Engine.Internals.FrameAssembler.PhaseTimingEnabled = _config.SubscriptionsPhaseTiming;

        // #927's A/B arm, process-wide and read per cell resolve. Same binary, one switch: two builds differ in JIT codegen as well as in the line under
        // test, which is why this repo's perf rule asks for a switch rather than a rebuild.
        Typhon.Engine.Internals.ArchetypeClusterState.GridWidePackingBound = _config.GridWideBound;

        // #949's A/B arm, same shape as the line above: process-wide, read once per planning tick.
        Typhon.Engine.Internals.ArchetypeClusterState.SkipRankWhenBudgetStarved = !_config.RankWhenStarved;

        // Buildings, terminals, houses, factories and harvesters never move. Telling the fence so is the difference
        // between a per-tick scan of the largest population in the world and nothing at all — and this population is
        // large precisely because a planet is mostly scenery.
        Dbe.SetSpatialBarrierOnly<WorldObject>();
        Dbe.SetSpatialBarrierOnly<CreatureLair>();

        // Creatures, players and NPCs move, but every one of those writes goes through cluster.WriteSpatial, which flags
        // migration and AABB growth inline. That is what makes the barrier-only opt-in correct for them as well as for
        // the scenery — and it is the setting a real server would ship, so measuring anything else would be measuring a
        // configuration nobody would use.
        Dbe.SetSpatialBarrierOnly<CityNpc>();
        Dbe.SetSpatialBarrierOnly<Creature>();
        Dbe.SetSpatialBarrierOnly<Player>();
        Dbe.SetSpatialBarrierOnly<Starship>();

        // Cluster dormancy, off unless asked for (--dormancy N). It is applied to the populations that can genuinely go quiet and NOT to Player: a
        // player's cluster sleeping would stop dispatching the system that integrates its position, and a session's own avatar is the one entity whose
        // latency is never worth trading. Creatures and NPCs mark their moved columns dirty (SimBridge), which is what keeps a moving cluster awake.
        if (_config.DormancyTicks > 0)
        {
            Dbe.SetClusterDormancy<Creature>(_config.DormancyTicks);
            Dbe.SetClusterDormancy<CityNpc>(_config.DormancyTicks);
            Dbe.SetClusterDormancy<WorldObject>(_config.DormancyTicks);
            Dbe.SetClusterDormancy<CreatureLair>(_config.DormancyTicks);
        }

        Indexes = new WorldIndex[_config.Planets];
        for (var planet = 0; planet < _config.Planets; planet++)
        {
            Indexes[planet] = new WorldIndex();

            // BUILD or REBUILD, and everything above this line runs either way (P-2). The realm registrations, the grid, SetSpatialBarrierOnly, the dormancy
            // declarations and InitializeArchetypes are all statements about the database rather than about its contents, and a reopened database needs every
            // one of them re-issued — none of them is persisted. Only the contents are, which is why this is the single branch.
            var census = Reopened
                ? WorldRebuild.Rebuild(Dbe, Map, Indexes[planet], (ushort)planet, InteriorRealmsOf(planet))
                : WorldBuilder.Populate(Dbe, Map, _config, Indexes[planet], (ushort)planet);

            if (InteriorsPerPlanet > 0)
            {
                // Portal j of planet p is realm Planets + p·N + j: every planet must have exactly N portals, or the decode reads another door.
                if (Indexes[planet].Portals.Count != InteriorsPerPlanet)
                {
                    throw new InvalidOperationException(
                        $"Planet {planet} has {Indexes[planet].Portals.Count} portals where the realm layout reserved {InteriorsPerPlanet}.");
                }

                // An interior's NPCs are already on disk when the world is reopened; re-populating would put a second set in every building.
                if (!Reopened)
                {
                    WorldBuilder.PopulateInteriors(Dbe, _config, Indexes[planet], _config.Planets + (planet * InteriorsPerPlanet), census);
                }
            }

            Census = planet == 0 ? census : Census.Plus(census);
        }

        if (SpaceRealm >= 0)
        {
            Census.Starships = Reopened
                ? WorldRebuild.CountStarships(Dbe, (ushort)SpaceRealm)
                : WorldBuilder.PopulateSpace(Dbe, _config, (ushort)SpaceRealm);
        }
    }

    public void Dispose()
    {
        // A CHECKPOINT before the engine closes, when the world is meant to survive (P-1).
        //
        // The engine writes the clean-shutdown watermark on its own Dispose, strictly after it has flushed dirty pages, so a clean close is already recorded
        // without this. What this adds is that the checkpoint LSN is ADVANCED first: without it every page this run dirtied reaches disk through the close
        // flush but the checkpoint watermark still names whatever LSN the last automatic cycle reached, so the next open replays the WAL from there. On a
        // world of a million entities that is the difference between a reopen that reads pages and one that replays a run.
        //
        // Guarded, and it must be: ForceCheckpoint on a run that is about to have its file deleted is pure cost, and this is a Dispose — it runs on the
        // failure path too, where the engine may be in no state to checkpoint and where throwing would mask the original fault.
        if (_config.Persist && Dbe != null)
        {
            try
            {
                Dbe.ForceCheckpoint();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  !! final checkpoint failed, the next open will replay the WAL instead: {ex.GetType().Name}: {ex.Message}");
            }
        }

        _runtime?.Dispose();
        _playerView?.Dispose();
        _creatureView?.Dispose();
        _npcView?.Dispose();
        _shipView?.Dispose();
        _lairView?.Dispose();
        _structureView?.Dispose();
        _scope?.Dispose();
        _serviceProvider?.Dispose();
        Typhon.Engine.Internals.SpatialQueryTuning.SimdNarrowphase = _simdNarrowphaseBefore;
    }
}
