using System;
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

    /// <summary>Entity handles and place geometry the systems address after the build.</summary>
    public WorldIndex Index { get; } = new();

    public TatooineSim(SimConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
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
                opt.DatabaseName = $"SwgTatooine_{Environment.ProcessId}";
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
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
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
        Dbe.RegisterComponentFromAccessor<PlayerState>();
        Dbe.RegisterComponentFromAccessor<Lair>();
        Dbe.RegisterComponentFromAccessor<Structure>();
        Dbe.RegisterComponentFromAccessor<Inventory>();

        // SWG's coordinates are centred on the planet: -8192..+8192 on each axis at the real size. Keeping the origin in
        // the middle rather than at a corner is not cosmetic — every authentic coordinate in the map data is expressed
        // that way, and shifting them would put a transcription error between the source and the world.
        var half = _config.WorldEdgeM * 0.5f;
        var cell = _config.ResolveCellSize();
        Dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(-half, -half),
            worldMax: new Vector2(half, half),
            cellSize: cell,
            clusterTargetExtentRatio: _config.ClusterTargetExtentRatio,
            clusterRepairExtentRatio: _config.ClusterRepairExtentRatio,
            reclusterBudgetMs: _config.ReclusterBudgetMs,
            repairWorstClustersPerUnit: _config.RepairWorstClustersPerUnit,
            clusterTargetPackingSlack: _config.ClusterTargetPackingSlack,
            batchSpawnSortThreshold: _config.BatchSpawnSortThreshold));

        Dbe.InitializeArchetypes();

        // Process-wide and read when each query is built, so setting it here covers every query the run makes.
        Typhon.Engine.Internals.SpatialQueryTuning.SimdNarrowphase = _config.SimdNarrowphase;

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

        Map = TatooineMap.Build(_config);
        Census = WorldBuilder.Populate(Dbe, Map, _config, Index);
    }

    public void Dispose()
    {
        _runtime?.Dispose();
        _playerView?.Dispose();
        _creatureView?.Dispose();
        _npcView?.Dispose();
        _lairView?.Dispose();
        _structureView?.Dispose();
        _viewTx?.Dispose();
        _scope?.Dispose();
        _serviceProvider?.Dispose();
        Typhon.Engine.Internals.SpatialQueryTuning.SimdNarrowphase = _simdNarrowphaseBefore;
    }
}
