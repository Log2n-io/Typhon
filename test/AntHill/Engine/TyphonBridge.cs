using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace AntHill;

public sealed class TyphonBridge : IDisposable
{
    public const int AntCount = 100_000;
    public const int FoodCount = 50;
    public const int NestCount = 5;
    public const float WorldSize = 20_000f;
    public const float CellSize = 1000f;
    public const float InvCellSize = 1f / CellSize;
    public const int GridCells = 20; // WorldSize / CellSize

    private ServiceProvider _serviceProvider;
    private IServiceScope _scope;
    internal DatabaseEngine _dbe;
    private TyphonRuntime _runtime;
    internal EcsView<Ant> _antView;

    private const int Stride = 12;

    // Per-worker render buffers — each worker writes to its own, no synchronization needed
    internal RenderWorkerBuffer[] _workerBuffers;
    internal RenderWorkerBuffer _overlayBuffer; // food + nests

    // Pheromone heatmap double-buffered RGBA (200×200×4)
    internal const int HeatmapPixels = RenderFrame.HeatmapSize * RenderFrame.HeatmapSize;
    internal byte[] _heatmapRGBA = new byte[HeatmapPixels * 4];
    internal byte[] _heatmapRGBARead = new byte[HeatmapPixels * 4];
    internal readonly float[] _heatMaxFood = new float[HeatmapPixels];
    internal readonly float[] _heatMaxHome = new float[HeatmapPixels];
    internal readonly float[] _heatMaxFight = new float[HeatmapPixels];   // Phase 6 polish — Fight alarm channel for the heatmap R lane
    internal volatile bool _heatmapEnabled;

    // Tier counts tracked during FillRenderBuffer
    internal readonly int[] _tierCounts = new int[4];
    public ReadOnlySpan<int> TierCounts => _tierCounts;

    // Ant state counters
    internal readonly int[] _stateCounts = new int[2]; // [0]=Foraging, [1]=Carrying
    public ReadOnlySpan<int> StateCounts => _stateCounts;

    // Camera world-space AABB (updated from Godot render thread)
    internal volatile float _camMinX;
    internal volatile float _camMinY;
    internal volatile float _camMaxX = 20_000f;
    internal volatile float _camMaxY = 20_000f;

    // Time control
    internal volatile float _timeScale = 1f;
    public float TimeScale { get => _timeScale; set => _timeScale = value; }

    // Phase 6A — environment (Daisyworld luminosity + sim-time day/night cycle).
    // Plain fields, not an ECS component: this is a global tunable, not entity state. Same pattern
    // as _timeScale / _simTimeSec. EnvironmentTickSystem writes _dayPhase + _envBrightness once per
    // tick; renderers poll EnvironmentBrightness from the Godot side each frame.
    internal const float DayCyclePeriodSec = 600f;   // 10 min sim-seconds at 1× speed
    internal volatile float _luminosity = 0.5f;      // [0,1] HUD slider
    internal volatile float _dayPhase   = 0.30f;     // [0,1] wraps every DayCyclePeriodSec sim-seconds — start at morning
    internal volatile bool  _pauseDayNight;          // HUD toggle freezes _dayPhase but not _luminosity
    internal volatile float _envBrightness = 0.5f;   // computed: _luminosity × daynightCurve(_dayPhase) with 0.15 floor

    public float Luminosity    { get => _luminosity;    set => _luminosity = Math.Clamp(value, 0f, 1f); }
    public bool  PauseDayNight { get => _pauseDayNight; set => _pauseDayNight = value; }
    public float EnvironmentBrightness => _envBrightness;
    public float DayPhase => _dayPhase;

    // Phase 6B — fire CA + global wind (also used by future vegetation sway in 6C).
    // Fire grid is owned outside ECS (dense 200×200 byte sweep, no per-cell entity payload).
    // Wind is a single global Vector2 that drifts slowly; the X/Y components are read by FireGrid
    // for spread anisotropy and (later) by vegetation for sway phase.
    internal readonly FireGrid _fireGrid = new();
    internal volatile float _windX = 0.7f;
    internal volatile float _windY = 0.0f;
    internal volatile float _windPhase;
    // Double-buffered fire texture data. _fireR8Write receives the CA snapshot in PrepareRender;
    // PublishRender hands _fireR8Read to the RenderFrame, then swaps. Mirrors the pheromone
    // heatmap double-buffer at line ~36.
    internal byte[] _fireR8Write = new byte[FireGrid.CellCount];
    internal byte[] _fireR8Read  = new byte[FireGrid.CellCount];

    public (float X, float Y) Wind => (_windX, _windY);
    public ReadOnlySpan<byte> FireState => _fireGrid.State;

    // Phase 6 polish — fire kills ants. Counter accumulates across CA ticks; FireTick drains it
    // into a single LogEntry (batches "N ants killed by fire" instead of spamming per-ant entries).
    // _fireKillLast{X,Y} is the last-killed position used as the event log's marker; plain int
    // writes are 32-bit atomic on x64 so the worst-case ordering risk is a stale by one kill — fine.
    internal int _fireKillsAccum;
    internal int _fireKillLastX;
    internal int _fireKillLastY;
    private const int FireCheckPeriodTicks = 6;     // 60 Hz / 10 Hz = 6 — matches CA cadence (no point sampling between updates)

    // Phase 6C — vegetation. PlantGrid is constructed lazily in SetHeightmap (needs the heightmap to
    // sample Y at spawn), so it's nullable until the renderer side wires the heightmap. The dirty-
    // index buffers carry per-frame state-change notifications to VegetationRenderer; double-buffered
    // for the same reason as _fireR8Write / _fireR8Read.
    public PlantGrid PlantGrid { get; private set; }
    internal const int PlantDirtyCapacity = 4096;
    internal int[] _plantDirtyWrite = new int[PlantDirtyCapacity];
    internal int[] _plantDirtyRead  = new int[PlantDirtyCapacity];
    internal int _plantDirtyWriteCount;
    internal int _plantDirtyReadCount;

    // Tier radii
    internal float _tier0Radius = 2000f;
    internal const float Tier2Radius = 8000f;

    // Tier mirror for rendering — linear indexed [cy * GridCells + cx]
    internal readonly byte[] _tierMirror = new byte[GridCells * GridCells];

    // Nest data
    internal (float x, float y)[] _nestPositions;
    internal int[] _nestFoodStock;
    internal const int InitialNestFood = 10_000;

    // Food data
    internal (float x, float y, float remaining)[] _foodCache;
    internal int[] _foodRemainingInt;
    internal int _foodDelivered;
    internal int _deathCount;

    // Food spatial grid: 40×40 cells (500-unit cells), per-cell list of food indices
    internal const int FoodGridCells = 40;
    internal const float FoodGridCellSize = WorldSize / FoodGridCells; // 500
    internal const float FoodGridInvCellSize = 1f / FoodGridCellSize;
    internal int[][] _foodGrid; // [cellIndex] → array of food source indices (null = empty)

    // Pheromone grid
    internal readonly PheromoneGrid _pheromones = new();

    // Heightmap (owned by Main.cs; reference wired post-Initialize via SetHeightmap). Sampled
    // in AntUpdateTick Step 1 for slope-aware step-length modulation. Null until wired — the
    // tick body null-checks once at the top.
    internal HeightmapResource _heightmap;

    // ── Phase 4: tool commands + event log + rocks ────────────────────────────
    //
    // _toolCommands: Godot main thread enqueues; ToolCommandSystem (Phase.Input) drains under a
    // transaction so spawn/destroy run on a single thread per CLAUDE.md transaction affinity.
    // _eventLog: simulation systems enqueue (filtered by ToolCommandSystem + AntStatsAggregator);
    // Godot main thread drains in _Process each frame.
    // _rockPositions / _rockCount: parallel array tracking spawned obstacles for RockRenderer.
    // ToolCommandSystem appends; Godot side reads under _rockCount watermark.
    internal readonly ConcurrentQueue<ToolCommand> _toolCommands = new();
    internal readonly ConcurrentQueue<LogEntry> _eventLog = new();
    internal (float x, float y)[] _rockPositions = new (float, float)[16];
    internal volatile int _rockCount;

    // Per-tick simulation time (seconds, accumulated by tick count × baseDt × timeScale).
    // Read by ToolCommandSystem + AntStatsAggregator when stamping LogEntry.TimeSec.
    internal volatile float _simTimeSec;

    public void EnqueueToolCommand(ToolCommand cmd) => _toolCommands.Enqueue(cmd);
    public bool TryDequeueLogEntry(out LogEntry entry) => _eventLog.TryDequeue(out entry);

    /// <summary>Snapshot of rock positions in sim units. RockRenderer reads this each frame
    /// and tops up its <c>MeshInstance3D</c> children for any new entries past its watermark.</summary>
    public ReadOnlySpan<(float x, float y)> RockPositions => new(_rockPositions, 0, _rockCount);

    /// <summary>Live food sources (sim units, initial amount). Length = <c>_foodCount</c> —
    /// includes depleted ones (caller filters via <see cref="FoodRemainingInt"/>).</summary>
    public ReadOnlySpan<(float x, float y, float initial)> FoodSources => new(_foodCache, 0, _foodCount);

    // ── Phase 5: colonies + combat + spiders ──────────────────────────────────
    //
    // Colony palettes — 5 distinct hues pushed to the ant shader once at Start. Order matches
    // NestColonyId (nest 0 → palette[0], ..., nest 4 → palette[4]).
    internal readonly System.Numerics.Vector3[] _colonyPalette =
    {
        new(1.00f, 0.30f, 0.20f),   // 0 — warm red
        new(0.25f, 0.55f, 1.00f),   // 1 — sky blue
        new(1.00f, 0.85f, 0.20f),   // 2 — gold
        new(0.30f, 0.85f, 0.30f),   // 3 — fresh green
        new(0.85f, 0.40f, 0.95f),   // 4 — magenta
    };
    public ReadOnlySpan<System.Numerics.Vector3> ColonyPalette => _colonyPalette;

    // Per-cell per-colony ant population, double-buffered:
    //   AntUpdate Step 1 (parallel) Interlocked-increments _ccWrite as ants land in cells.
    //   AntUpdate combat sub-step reads _ccRead (previous tick's complete totals).
    //   TierAssignment swaps the buffers at tick start (sequential) and zeros the new write side.
    // Indexed by [cy * GridCells + cx] * NestCount + colony.
    // Phase 6 polish — combat detection grid is FINER than the spatial grid (which stays at 20×20
    // for cluster-AABB / tier purposes). At 200² combat cells are 0.5 m worldspace each, so two
    // ants contest only when they're within ~half-a-meter of each other rather than a 5 m square.
    // Memory: 200×200 × NestCount × 4 B × 2 buffers ≈ 800 KB (fits in L2). Atomic-contention is
    // very low because ants are sparse per cell at this resolution.
    public const int CombatGridCells   = 200;
    public const float CombatCellSize   = WorldSize / CombatGridCells;   // 100 sim units = 0.5 m
    public const float CombatInvCellSize = 1f / CombatCellSize;
    internal int[] _cellColonyCountWrite = new int[CombatGridCells * CombatGridCells * NestCount];
    internal int[] _cellColonyCountRead  = new int[CombatGridCells * CombatGridCells * NestCount];

    // Spider data — plain arrays (NOT an ECS archetype). At Phase 5 scale (8 spiders) the archetype
    // overhead is pure cost; flat arrays mirror the same shape as `_nestPositions` + `_nestFoodStock`
    // and avoid cross-archetype access patterns that complicate the scheduler.
    internal const int SpiderCount = 8;
    internal (float x, float y)[] _spiderPositions;
    internal (float vx, float vy)[] _spiderVelocities;

    // Phase 6 polish — spider HP + respawn timer. Soldiers in melee range deal damage; on HP ≤ 0 the
    // spider goes off-screen for SpiderRespawnDelayTicks ticks then re-spawns at a random edge.
    internal float[] _spiderHealth;
    internal int[]   _spiderRespawnTicksLeft;

    // Soldier-position cache (per tick). Populated by AntUpdateTick after each soldier's position
    // commit, drained by SpiderUpdateTick to count "soldiers in melee range" per spider. Resets to
    // empty in EnvironmentTick (runs in Phase.Input before AntUpdateSystem). Sized to AntCount as
    // worst case; in practice <10 % of ants are soldiers, ~5-10 k writes/tick.
    internal (float x, float y)[] _soldierPositions = new (float, float)[AntCount];
    internal int _soldierCount;
    internal int[] _spiderTicksSinceKill;
    // Debug-aid state: per-spider snapshot of "am I chasing right now?" + "what XY am I aiming at?".
    // Mirrors are written at the end of SpiderUpdateTick and read by SpiderRenderer for visual aids
    // (state colour, target line).
    internal bool[] _spiderChasing;
    internal (float x, float y)[] _spiderChaseTarget;
    public ReadOnlySpan<(float x, float y)> SpiderPositions => _spiderPositions ?? System.Array.Empty<(float, float)>();
    public ReadOnlySpan<int> SpiderTicksSinceKill => _spiderTicksSinceKill ?? System.Array.Empty<int>();
    public ReadOnlySpan<bool> SpiderChasing => _spiderChasing ?? System.Array.Empty<bool>();
    public ReadOnlySpan<(float x, float y)> SpiderChaseTarget => _spiderChaseTarget ?? System.Array.Empty<(float, float)>();
    // Visible chase-radius bubble — exposed so SpiderRenderer can size the translucent sphere
    // that shows each spider's hunting reach.
    public static float SpiderChaseRadiusWorld => SpiderChaseRange * (100f / WorldSize);

    // Spider perf measurement — per-tick counters reset at the top of SpiderUpdateTick. HUD reads
    // these to display a live breakdown of where the ~10 ms cost goes. Volatile because Godot
    // reads from the render thread while the sim worker writes them.
    internal volatile int _spiderTier0Count;       // how many of SpiderCount entered the chase path this tick
    internal volatile int _spiderTotalHits;        // sum of chaseHits.Count across tier-0 spiders this tick
    internal volatile int _spiderKills;            // ants destroyed this tick
    // 64-bit ticks fields: plain access (long is naturally atomic on x64; volatile not permitted on long).
    internal long _spiderQueryTicks;               // Stopwatch ticks spent in WhereNearby.Execute()
    internal long _spiderForeachTicks;             // Stopwatch ticks spent in the per-hit foreach body
    internal long _spiderCommitTicks;              // Stopwatch ticks spent in Commit() at end of tick

    public int SpiderTier0Count    => _spiderTier0Count;
    public int SpiderTotalHits     => _spiderTotalHits;
    public int SpiderKills         => _spiderKills;
    public double SpiderQueryMs    => _spiderQueryTicks   * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public double SpiderForeachMs  => _spiderForeachTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public double SpiderCommitMs   => _spiderCommitTicks  * 1000.0 / System.Diagnostics.Stopwatch.Frequency;


    /// <summary>Live remaining count per food source. Parallel to <see cref="FoodSources"/>.
    /// Written by AntUpdateTick via Interlocked; stale reads on the Godot side are fine since
    /// int loads are atomic on x64 and a one-frame lag on the rendered ratio is imperceptible.</summary>
    public ReadOnlySpan<int> FoodRemainingInt => new(_foodRemainingInt, 0, _foodCount);

    /// <summary>Nest positions in sim units. Fixed at init (<see cref="NestCount"/>).</summary>
    public ReadOnlySpan<(float x, float y)> NestPositions => _nestPositions;

    /// <summary>Live nest stockpiles. Parallel to <see cref="NestPositions"/>.</summary>
    public ReadOnlySpan<int> NestFoodStocks => _nestFoodStock;

    /// <summary>Initial stockpile assigned to each nest at spawn — used for fill-ratio rendering.</summary>
    public int InitialNestFoodPerNest => InitialNestFood;

    // Render bridge
    internal readonly RenderBridge _renderBridge = new();
    public RenderBridge RenderBridge => _renderBridge;

    // Migration tracking
    internal int _cellCrossingsThisTick;
    internal int _crossingsAccum;
    internal int _crossingsTickCount;
    public int CrossingsPerSecond { get; internal set; }

    // Event queues — wired in BuildSchedule, accessed by producer/consumer systems via the bridge.
    // Replace the previous Interlocked.Increment counters with proper RFC 07 event flow so the
    // System DAG view shows producer→consumer arrows.
    internal EventQueue<AntDiedEvent> _antDiedQueue;
    internal EventQueue<FoodPickedUpEvent> _foodPickedUpQueue;
    internal EventQueue<FoodDeliveredEvent> _foodDeliveredQueue;

    // Live runtime exposed to systems that need telemetry (PublishRender's periodic dump).
    internal TyphonRuntime Runtime => _runtime;

    // Public stats
    public int VisibleAnts { get; private set; }
    public int FoodDelivered => _foodDelivered;
    public int DeathCount => _deathCount;
    public int TotalNestFood
    {
        get
        {
            if (_nestFoodStock == null) return 0;
            var total = 0;
            for (var i = 0; i < _nestFoodStock.Length; i++) total += Math.Max(0, _nestFoodStock[i]);
            return total;
        }
    }
    public int FoodSourcesRemaining
    {
        get
        {
            if (_foodRemainingInt == null) return 0;
            var count = 0;
            var n = _foodCount;
            for (var i = 0; i < n; i++)
            {
                if (_foodRemainingInt[i] > 0) count++;
            }
            return count;
        }
    }

    public void UpdateCamera(float minX, float minY, float maxX, float maxY)
    {
        _camMinX = minX;
        _camMinY = minY;
        _camMaxX = maxX;
        _camMaxY = maxY;
    }

    /// <summary>Wire the procedural heightmap so the sim can sample slope. Called from Main.cs
    /// after the heightmap is generated. Safe to call before <see cref="Start"/>; the AntUpdateTick
    /// reads <c>_heightmap</c> per cluster and null-checks once.
    ///
    /// Also constructs the Phase 6C <see cref="PlantGrid"/> — plants need terrain Y at spawn so the
    /// grid can't be built until the heightmap arrives. Idempotent re-builds (heightmap swap) replace
    /// the grid; the renderer's <c>_Ready</c> snapshots positions before <c>Start</c>, so a swap mid-
    /// run would desync visuals (acceptable — heightmap is only ever set once today).</summary>
    public void SetHeightmap(HeightmapResource heightmap)
    {
        _heightmap = heightmap;
        if (heightmap != null) PlantGrid = new PlantGrid(heightmap.Sample);
    }

    public void Initialize()
    {
        var services = new ServiceCollection();
        services
            .AddLogging(cfg => cfg.AddConsole().SetMinimumLevel(LogLevel.Warning))
            // WAL is enabled solely by setting DatabaseEngineOptions.Wal below; the engine constructs
            // its own WalFileIO internally now (#329 §10.3 E leak-tightening).
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opt =>
            {
                opt.DatabaseName = "AntHill";
                opt.DatabaseDirectory = AppContext.BaseDirectory;
                opt.DatabaseCacheSize = 512 * 1024 * 1024;
            })
            .AddScopedDatabaseEngine(opt =>
            {
                // WAL stays enabled: counter-intuitively, disabling WAL made the fence 4×
                // slower — the no-WAL path forces UoW.Flush to sync pages itself instead of
                // delegating durability to the async WAL writer thread. Keep WAL on; tune
                // tick cost elsewhere.
                opt.Wal = new WalWriterOptions();
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        _scope = _serviceProvider.CreateScope();
        _dbe = _scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        Archetype<Ant>.Touch();
        Archetype<Food>.Touch();
        Archetype<Nest>.Touch();
        Archetype<Rock>.Touch();
        _dbe.RegisterComponentFromAccessor<WorldBounds>();
        _dbe.RegisterComponentFromAccessor<Velocity>();
        _dbe.RegisterComponentFromAccessor<Genetics>();
        _dbe.RegisterComponentFromAccessor<AntState>();
        _dbe.RegisterComponentFromAccessor<FoodSource>();
        _dbe.RegisterComponentFromAccessor<NestInfo>();
        _dbe.RegisterComponentFromAccessor<Obstacle>();

        _dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: Vector2.Zero,
            worldMax: new Vector2(WorldSize, WorldSize),
            cellSize: CellSize,
            migrationHysteresisRatio: 0.05f));

        _dbe.InitializeArchetypes();

        // Opt every cluster-spatial archetype into the WriteSpatial barrier fast path.
        //   - Ant: WriteSpatial covers every position update in AntUpdateTick (movement + respawn).
        //   - Food / Nest / Rock: stationary after spawn — no spatial writes after init, so the barrier-
        //     only path is trivially correct (their ClusterProcessBitmap stays empty, fence-time
        //     spatial passes early-return). Without this opt-in, their fence pass would still run the
        //     legacy unconditional ActiveClusterIds scan every tick — wasted work for entities that
        //     never move.
        _dbe.SetSpatialBarrierOnly<Ant>();
        _dbe.SetSpatialBarrierOnly<Food>();
        _dbe.SetSpatialBarrierOnly<Nest>();
        _dbe.SetSpatialBarrierOnly<Rock>();

        SpawnNests();
        SpawnFood();
        SpawnAnts();
        SpawnSpiders();
        Console.WriteLine($"AntHill spawn diag: ants={AntCount} nests={NestCount} spiders={SpiderCount} foodCount={_foodCount}");

        using var txView = _dbe.CreateQuickTransaction();
        _antView = txView.Query<Ant>().ToView();

        const int workerCount = 16;
        _runtime = TyphonRuntime.Create(_dbe, BuildSchedule, new RuntimeOptions
        {
            BaseTickRate = 60,
            WorkerCount = workerCount,
            // RFC 07 phase pipeline — declared as a total order. All systems in phase N complete
            // before any system in phase N+1 starts. The Workbench's System DAG view uses this
            // skeleton as the swim-lane structure.
            Phases =
            [
                Phase.Input,
                AntPhases.Simulation,
                AntPhases.Trail,
                AntPhases.Render,
            ],
            DefaultPhase = AntPhases.Render,
            // Parallelize WriteTickFence across the worker pool — registers FenceExec as an internal sub-DAG system
            // dispatched after the user DAG completes. Per-archetype/per-table fence work runs concurrently instead of
            // serially on TickDriver, reclaiming the idle-worker window that previously dominated AntHill's cluster-fence
            // wall-clock. K × WorkerCount chunk oversubscription smooths preemption jitter.
            EnableParallelFence = true,
        });

        // Per-worker render buffers: each parallel FillRender worker writes to its own buffer
        _workerBuffers = new RenderWorkerBuffer[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            _workerBuffers[i] = new RenderWorkerBuffer(AntCount / workerCount + 1024);
        }
        _overlayBuffer = new RenderWorkerBuffer(FoodCount + NestCount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // System DAG
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Build the schedule using class-based RFC 07 registrations. Phase-derived swim-lanes,
    /// per-system access declarations (Reads/Writes for components, ReadsResource/WritesResource
    /// for shared simulation state, ReadsEvents/WritesEvents for telemetry queues). The previous
    /// lambda-overload form is gone — it didn't carry the access metadata the auto-DAG (and the
    /// Workbench System DAG view) needs.
    /// </summary>
    private void BuildSchedule(RuntimeSchedule schedule)
    {
        // Event queues must exist before systems are registered (Configure captures them via
        // WritesEvents / ReadsEvents). Capacities tuned for AntCount = 200 K — death events
        // fire in bursts when energy crashes; food pickups are throttled by Interlocked checks.
        _antDiedQueue        = schedule.CreateEventQueue<AntDiedEvent>("AntDied",        capacity: 4096);
        _foodPickedUpQueue   = schedule.CreateEventQueue<FoodPickedUpEvent>("FoodPickedUp", capacity: 4096);
        _foodDeliveredQueue  = schedule.CreateEventQueue<FoodDeliveredEvent>("FoodDelivered", capacity: 4096);

        // Input phase
        // ToolCommandSystem drains the Godot-side command queue and applies sim-side mutations
        // (spawn food / rock, cull). Runs first so AntUpdateSystem (Phase.Simulation) sees the
        // new entities + updated _foodCache / _foodGrid this same tick.
        schedule.Add(new ToolCommandSystem(this));
        // Phase 6A — advances day/night phase + computes brightness scalar. Runs before
        // TierAssignment because the brightness scalar may eventually drive LOD curves; today
        // it's a pure write to a TyphonBridge field that the Godot side polls each frame.
        schedule.Add(new EnvironmentTickSystem(this));
        schedule.Add(new TierAssignmentSystem(this));

        // Simulation phase — single merged system that walks each Ant cluster once per tick and
        // runs metabolism+respawn / move / food-interact / brain-steer / phero-deposit in registers.
        // Per-cluster tier amortization is inside the body (AmortMetab/Brain/Phero tables).
        schedule.Add(new AntUpdateSystem(this));

        // Spider predators — Phase 5. Sequential (8 entities); after AntUpdate so it can read the
        // spatial index without racing AntUpdate's WorldBounds writes (AntUpdate finishes its parallel
        // walk before this system starts via cross-system W×W ordering on Velocity).
        schedule.Add(new SpiderUpdateSystem(this));

        // Trail phase — pheromone grid evaporation sweep. Single writer of PheromoneGrid alongside
        // AntUpdate's per-ant deposits; runs after AntUpdate via cross-phase ordering.
        schedule.Add(new PheroDecaySystem(this));

        // Phase 6B — Drossel-Schwabl fire CA at 10 Hz. Lives in the Trail phase next to PheroDecay
        // because both are "ambient environment" sweeps (no entity access, no MVCC, dense grid pass).
        schedule.Add(new FireTickSystem(this));

        // Phase 6C — Vegetation tick at 10 Hz. ReadsResource("FireGrid") + WritesResource("PlantGrid")
        // orders it after FireTickSystem this tick (FireGrid is what FireTickSystem just wrote), so the
        // plant scan sees the freshly-resolved Burning cells. Cell density feedback for next tick's
        // FireGrid.Tick goes via PlantGrid.DensityFactor (read in FireTick on the following CA tick).
        schedule.Add(new VegetationSystem(this));

        // Render phase — stats sink consumes the three event queues, prepare/fill/publish chain
        schedule.Add(new AntStatsAggregatorSystem(this));
        schedule.Add(new PrepareRenderBufferSystem(this));
        schedule.Add(new FillRenderBufferSystem(this));

        // Phase 6 polish — heatmap downsample split into 4 parallel systems (one per channel
        // for the max-reduce, plus an RGBA pack). The 3 ChunkedCallbackSystems run concurrently
        // with each other and with FillRenderBufferSystem; the pack accepts 1-tick tearing per
        // channel in exchange for also running in parallel.
        schedule.Add(new PheroMaxReduceSystem(this, "HeatmapMaxFood",  () => _pheromones.Food,  () => _heatMaxFood,  "HeatFoodAccum"));
        schedule.Add(new PheroMaxReduceSystem(this, "HeatmapMaxHome",  () => _pheromones.Home,  () => _heatMaxHome,  "HeatHomeAccum"));
        schedule.Add(new PheroMaxReduceSystem(this, "HeatmapMaxFight", () => _pheromones.Fight, () => _heatMaxFight, "HeatFightAccum"));
        schedule.Add(new HeatmapRgbaPackSystem(this));

        schedule.Add(new PublishRenderFrameSystem(this));
    }

    // ═══════════════════════════════════════════════════════════════════
    // TierAssignment
    // ═══════════════════════════════════════════════════════════════════

    internal void TierAssignment(TickContext ctx)
    {
        // Phase 5: swap cell-colony aggregator buffers (read ← previous-tick write; zero new write).
        // AntUpdate Step 1 incrementally fills the write buffer; the combat sub-step in the same
        // tick reads the previous tick's complete totals from the read buffer.
        (_cellColonyCountRead, _cellColonyCountWrite) = (_cellColonyCountWrite, _cellColonyCountRead);
        System.Array.Clear(_cellColonyCountWrite);

        var camX = (_camMinX + _camMaxX) * 0.5f;
        var camY = (_camMinY + _camMaxY) * 0.5f;

        var grid = ctx.SpatialGrid;
        grid.ResetAllTiers(SimTier.Tier3);

        var r0sq = _tier0Radius * _tier0Radius;
        var r1sq = (_tier0Radius * 3f) * (_tier0Radius * 3f);
        var r2sq = Tier2Radius * Tier2Radius;

        for (var cy = 0; cy < GridCells; cy++)
        {
            for (var cx = 0; cx < GridCells; cx++)
            {
                var cellCenterX = cx * CellSize + CellSize * 0.5f;
                var cellCenterY = cy * CellSize + CellSize * 0.5f;
                var dx = cellCenterX - camX;
                var dy = cellCenterY - camY;
                var distSq = dx * dx + dy * dy;

                SimTier tier;
                if (distSq < r0sq) tier = SimTier.Tier0;
                else if (distSq < r1sq) tier = SimTier.Tier1;
                else if (distSq < r2sq) tier = SimTier.Tier2;
                else
                {
                    _tierMirror[cy * GridCells + cx] = 3;
                    continue;
                }

                grid.SetCellTier(cx, cy, tier);
                _tierMirror[cy * GridCells + cx] = (byte)BitOperations.TrailingZeroCount((uint)(byte)tier);
            }
        }

        // Phase 5: spider proximity-promotion — cells within 2-cell radius of any spider get bumped
        // to T0. Mirror byte writes happen alongside grid writes so AntUpdate's tierMirror lookup sees
        // the promoted value within the same tick. SpatialGridAccessor only exposes SetCellTier (not
        // Min); guard with the mirror-byte check so we don't redundantly write cells already at T0.
        if (_spiderPositions != null)
        {
            const int PromoteRadius = 2;
            for (var si = 0; si < _spiderPositions.Length; si++)
            {
                var (sx, sy) = _spiderPositions[si];
                var scx = Math.Clamp((int)(sx * InvCellSize), 0, GridCells - 1);
                var scy = Math.Clamp((int)(sy * InvCellSize), 0, GridCells - 1);
                var minCx = Math.Max(0, scx - PromoteRadius);
                var maxCx = Math.Min(GridCells - 1, scx + PromoteRadius);
                var minCy = Math.Max(0, scy - PromoteRadius);
                var maxCy = Math.Min(GridCells - 1, scy + PromoteRadius);
                for (var cy = minCy; cy <= maxCy; cy++)
                {
                    for (var cx = minCx; cx <= maxCx; cx++)
                    {
                        var mi = cy * GridCells + cx;
                        if (_tierMirror[mi] > 0)   // worse than T0
                        {
                            grid.SetCellTier(cx, cy, SimTier.Tier0);
                            _tierMirror[mi] = 0;
                        }
                    }
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // AntUpdate constants (shared across all five logical steps of the merged
    // simulation tick body — see AntUpdateTick below).
    // ═══════════════════════════════════════════════════════════════════

    private const int AntArchetypeId = 100;
    private const float BaseDt = 1f / 60f;

    // Step 2 — Metabolism (energy decay + respawn)
    private const float EnergyDrainRate = 0.15f;

    // Step 3 — Food/nest interaction
    private const float FoodPickupRange = 30f;
    private const float FoodSmellRange = 250f;
    private const float NestDropRange = 40f;
    private const float FoodPickupRangeSq = FoodPickupRange * FoodPickupRange;
    private const float FoodSmellRangeSq = FoodSmellRange * FoodSmellRange;
    private const float NestDropRangeSq = NestDropRange * NestDropRange;

    // Step 4 — Brain (pheromone steering + wander)
    private const float SensorDistance = 40f;
    private const float SensorAngle = 0.52f;        // ~30 degrees
    private const float SteerStrength = 0.3f;       // radians per tick toward best sensor
    private const float WanderJitter = 0.03f;       // tiny per-tick jitter (±1.7°)
    private const int   WanderChangeTicks = 90;     // change direction every ~1.5s
    private const float WanderTurnMax = 0.8f;       // max turn on direction change (~45°)

    // Step 5 — PheroDep (deposit) + PheroDecay (evaporate)
    private const float BaseDeposit = 5f;
    private const float NearFoodMultiplier = 10f;   // deposit more food-pheromone near food
    private const float DepositFalloffRange = 200f;
    private const float DepositFalloffRangeSq = DepositFalloffRange * DepositFalloffRange;

    // Phase 6 polish — danger pheromones.
    // Fight deposit per-hit: combat damage roll OR spider-kill. Magnitude similar to Food deposit so
    //   the alarm gradient reads at the same range as food trails.
    // Fire deposit: per-ant per-CA-tick if any cell in the ant's 3×3 fire-neighborhood is Burning.
    //   Smaller per-deposit so a single ant scout doesn't paint the world; the colony's collective
    //   warning sums up over time.
    private const float FightDeposit = 150f;            // ~3 sim-sec above attract threshold; persists long enough for amortized brains to react
    private const float FireDeposit  = 20f;
    // Sensor thresholds — minimum phero magnitude that triggers caste-conditional steering. Tuned so
    // a single ant's deposit is below threshold but a small cluster of deposits is above. Fight has a
    // higher Soldier-attract threshold so soldiers don't commit on a stray scout's panic.
    private const float FireSteerThreshold      = 8f;
    private const float FightFleeThreshold      = 8f;    // workers run from any meaningful Fight signal
    private const float FightAttractThreshold   = 20f;   // soldiers commit only on stronger signal (multi-deposit hotspot)
    private const float DangerSteerMultiplier   = 2f;    // panic / charge steers turn 2× as hard as forage steers

    // Phase 5 — Combat + Larva maturation tuning. "Cruel world" pass: any opposing colony
    // presence triggers combat, damage is doubled, and worker hit rate is more than doubled.
    // Net effect: visible attrition along every nest border, fast colony collapse if a colony
    // gets cornered. Soldiers still take ~half the hit rate of workers so they outlast them
    // in mixed engagements.
    private const int LarvaMaturityTicks = 600;       // 10 s @ 60 Hz
    private const int CombatContestedThreshold = 1;   // ≥ 1 opposing ant in cell → fight (was 2)
    private const float CombatBaseDamage = 60f;       // doubled per-hit damage — most ants die in 1–2 hits
    private const int CombatRollWorkerPercent = 60;   // 60 % chance / tick / contested cell (was 25)
    // Soldiers engage every tick in a contested cell — guaranteed damage exchange. At 60 dmg/tick they
    // burn through energy fast, but the visible effect is "soldier is always fighting" (continuous red flash).
    private const int CombatRollSoldierPercent = 100;
    internal const int HitFlashDuration = 8;          // ~130 ms @ 60 Hz — red blip on damage

    // ═══════════════════════════════════════════════════════════════════
    // PheromoneDecay — evaporate grid (callback, every 6 ticks → 10 Hz).
    // The system stays in the DAG so cross-phase ordering is unchanged; it
    // early-returns on 5 of 6 ticks. Decay factor 0.995^6 = 0.9704 keeps the
    // long-term decay rate equivalent to the previous 60 Hz schedule.
    // Deposits stay at 60 Hz (AntUpdateTick writes directly into the grid);
    // the 6-tick deposit accumulation simply lives in the grid itself —
    // no staging buffer needed because deposit and decay never race in
    // our single-system layout (cross-phase Simulation → Trail order).
    // ═══════════════════════════════════════════════════════════════════

    private const float PheroDecayFactor = 0.9704f;   // 0.995^6 — runs once per 6 ticks
    // Phase 6 polish — danger channel decay rates (applied once per 6 sim ticks = 10 Hz).
    // Re-tuned after runtime test: Fight was decaying too fast (0.83/CA tick → half-life 0.4 s),
    // making the gradient invisible before soldiers could traverse to it.
    //   Fight: 0.98 / CA tick → half-life ~3.4 sim-seconds. At deposit 150, stays above attract
    //     threshold (20) for ~10 s — long enough for soldiers running at ~0.08 m/s to cover the
    //     ~0.8 m the gradient typically reaches.
    //   Fire:  0.97 / CA tick → half-life ~2.3 sim-seconds. Fire is replenished every CA tick by
    //     all nearby ants, so steady-state is high even with faster decay.
    // Fight intentionally decays slightly SLOWER than Fire because Fight deposits are rare events
    // (per hit / per kill) while Fire deposits are continuous (per ant per CA tick near a fire).
    private const float FightDecayFactor = 0.98f;
    private const float FireDecayFactor  = 0.97f;

    internal void PheromoneDecayTick(TickContext ctx)
    {
        _pheromones.Evaporate(PheroDecayFactor);
        _pheromones.EvaporateChannel(_pheromones.Fight, FightDecayFactor);
        _pheromones.EvaporateChannel(_pheromones.Fire,  FireDecayFactor);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Slope-aware movement
    // ═══════════════════════════════════════════════════════════════════

    // Heightmap is in render metres (0..100); sim is in 0..20000. Convert sim → render
    // by SimToWorld. Defined locally to keep the engine independent of the renderer side.
    private const float SimToWorld = 100f / WorldSize;

    // Slope step-length gain. clamp(1 - slope * gain, 0.5, 1.5):
    //   • At gain=1.5, a slope of 0.33 (33% grade) bottoms out at 0.5x step.
    //   • At gain=1.5, a slope of -0.33 saturates at 1.5x step.
    // Heightmap relief is ±1m over 100m; local slopes commonly reach 0.2–0.4 inside
    // a 5-octave Perlin field, so this band sits where the modulation is most useful.
    private const float SlopeGain = 1.5f;

    // Phase 6D — rock hard collision. Rocks are point-circle obstacles; if an ant's integrated
    // step ends inside the radius, the ant is pushed radially to the surface and the inward
    // component of its velocity is zeroed so it slides tangentially instead of stopping/bouncing.
    internal const float RockCollisionRadius   = 100f;          // sim units = 0.5 m worldspace
    internal const float RockCollisionRadiusSq = RockCollisionRadius * RockCollisionRadius;

    // ═══════════════════════════════════════════════════════════════════
    // EnvironmentTick — Phase 6A
    // Advances the day/night cycle in sim-time (dt × _timeScale) and computes the global
    // brightness scalar = luminosity × day-curve, clamped to a 0.15 floor so the world never
    // goes pitch black. Runs sequentially in Phase.Input; ~50 ns per tick.
    // ═══════════════════════════════════════════════════════════════════

    internal void EnvironmentTick(TickContext ctx)
    {
        // Reset the per-tick soldier-position cache. AntUpdateTick (parallel, in Simulation phase)
        // appends soldier positions into the array using Interlocked.Increment for slot allocation;
        // SpiderUpdateTick reads it to count "soldiers in melee range" per spider. EnvironmentTick
        // runs in Phase.Input (single-threaded, before Simulation) so the reset is race-free.
        _soldierCount = 0;

        var dt = ctx.DeltaTime * _timeScale;
        if (!_pauseDayNight)
        {
            var dp = _dayPhase + dt / DayCyclePeriodSec;
            if (dp >= 1f) dp -= MathF.Floor(dp);
            _dayPhase = dp;
        }

        // Dawn 0.10→0.30, plateau 0.30→0.70, dusk 0.70→0.90. The smoothstep difference is a
        // smooth bump in [0,1]; the 0.15 floor preserves visibility at midnight.
        var p = _dayPhase;
        var dayCurve = Smoothstep01(0.10f, 0.30f, p) - Smoothstep01(0.70f, 0.90f, p);
        var lightLevel = 0.15f + 0.85f * Math.Clamp(dayCurve, 0f, 1f);

        _envBrightness = _luminosity * lightLevel;
    }

    private static float Smoothstep01(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FireTick — Phase 6B
    // Drifts the wind vector and advances the Drossel-Schwabl CA one step. Called by
    // FireTickSystem at 10 Hz (every 6th tick). Wind drift is sim-time-coupled so it freezes
    // when the user pauses the sim.
    // ═══════════════════════════════════════════════════════════════════

    internal void FireTick(TickContext ctx)
    {
        var dt = ctx.DeltaTime * _timeScale * 6f;  // CA runs once per 6 base ticks; integrate over the period
        // Slow rotation of the wind direction — ~one full revolution per 60 sim-seconds. Magnitude
        // fixed at 0.7 (well below saturation given WindBias = 0.5).
        _windPhase += dt * (MathF.PI * 2f / 60f);
        if (_windPhase > MathF.PI * 2f) _windPhase -= MathF.PI * 2f;
        _windX = MathF.Cos(_windPhase) * 0.7f;
        _windY = MathF.Sin(_windPhase) * 0.7f;

        // Phase 6C — if vegetation is wired, hand the per-cell density factor to the CA so dense
        // plant areas spread faster than rocky pockets. Untyped (default) span → uniform 1× spread.
        var density = PlantGrid?.DensityFactor;
        if (density != null) _fireGrid.Tick(_windX, _windY, density);
        else _fireGrid.Tick(_windX, _windY);

        // Phase 6 polish — drain the per-tick fire-kill accumulator into one event log entry.
        // The counter is bumped in AntUpdateTick's Step 1.5; FireTick runs at the same 10 Hz so
        // the log entry naturally aligns with the visible flame-front.
        var killed = Interlocked.Exchange(ref _fireKillsAccum, 0);
        if (killed > 0)
        {
            var wx = _fireKillLastX * SimToWorld;
            var wy = _fireKillLastY * SimToWorld;
            _eventLog.Enqueue(new LogEntry(
                _simTimeSec,
                killed == 1 ? "1 ant killed by fire" : $"{killed} ants killed by fire",
                wx, wy, LogSeverity.Action));
        }
    }

    /// <summary>
    /// Phase 6B — ignite a small patch of Fuel cells around <paramref name="simX"/>,
    /// <paramref name="simY"/>. Called by ToolCommandSystem when the user clicks the Ignite tool.
    /// Logs an event if any cell flipped (i.e. there was Fuel to burn).
    /// </summary>
    public void IgniteAt(float simX, float simY, int radius = 3)
    {
        // Pass the plant density factor so manually-ignited cells capture their plant load into
        // BurnIntensity and emanate more strongly while burning (Phase 6C producer-side boost).
        var density = PlantGrid?.DensityFactor;
        var ignited = density != null
            ? _fireGrid.Ignite(simX, simY, radius, density)
            : _fireGrid.Ignite(simX, simY, radius);
        if (ignited)
        {
            _eventLog.Enqueue(new LogEntry(
                _simTimeSec,
                $"Ignition at ({simX * SimToWorld:F1}, {simY * SimToWorld:F1})",
                simX * SimToWorld, simY * SimToWorld, LogSeverity.Action));
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // AntUpdate — merged per-ant simulation (replaces MoveAll + Metabolism_T0..T3 +
    //             FoodDetect + Brain_T0..T3 + PheroDep_T0..T3 = 14 systems → 1).
    // Walks each Ant cluster once per tick; performs all five logical steps in registers.
    // Tier amortization is per-cluster gating inside the body (see AmortMetab/Brain/Phero
    // tables) — preserves the pre-merge CellAmortize(1/8/30/60) for Metabolism+Brain and
    // CellAmortize(1/2/4/8) for PheroDep. amortScale per step preserves time-integrated
    // semantics (energy decay totals, pheromone field strength).
    // ═══════════════════════════════════════════════════════════════════

    // Tier amortization periods (T0..T3) — fire when (tickNumber % rate) == 0.
    // Unbiased (TierBias = 0 across the board) for exact behavioral match with the pre-merge
    // topology, where all four Metabolism_Tn / Brain_Tn / PheroDep_Tn systems fired
    // on `tick % CellAmortize == 0` without per-tier stagger. Stagger can be added later for
    // CPU load smoothing if needed.
    private static readonly int[] AmortMetab = { 1, 8, 30, 60 };
    private static readonly int[] AmortBrain = { 1, 8, 30, 60 };
    private static readonly int[] AmortPhero = { 1, 2,  4,  8 };

    internal void AntUpdateTick(TickContext ctx)
    {
        var tick = ctx.TickNumber;
        var dt = ctx.DeltaTime * _timeScale;
        _simTimeSec += dt;
        var phero = _pheromones;
        var food = _foodCache;
        var foodRemaining = _foodRemainingInt;
        var foodGrid = _foodGrid;
        var nests = _nestPositions;
        var nestStock = _nestFoodStock;
        var deathQueue = _antDiedQueue;
        var pickedUpQueue = _foodPickedUpQueue;
        var deliveredQueue = _foodDeliveredQueue;
        var tierMirror = _tierMirror;
        var heightmap = _heightmap;            // null-check once per tick
        var ccRead = _cellColonyCountRead;     // previous tick's complete per-cell-per-colony totals
        var ccWrite = _cellColonyCountWrite;   // this tick's accumulator (Interlocked.Increment)
        // Phase 6 polish — fire kills. The fire CA only changes at 10 Hz so checking more often is
        // wasted work; gate per-ant fire sampling to every 6th 60 Hz tick.
        var fireState = _fireGrid.State;
        var checkFire = (tick % FireCheckPeriodTicks) == 0;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Ant>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Ant>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        // Phase 6D — reusable scratch buffer for the per-cluster rock broadphase. Hoisted out of
        // the cluster loop because stack-allocated memory inside a loop accumulates across
        // iterations (CA2014); a single 16-int slot (64 B) reused per cluster is the right shape.
        const int MaxNearbyRocks = 16;
        Span<int> nearby = stackalloc int[MaxNearbyRocks];

        foreach (var cluster in clusters)
        {
            var bits0 = cluster.OccupancyBits;
            if (bits0 == 0)
            {
                continue;
            }

            // Bounds reads only (the write path goes through cluster.WriteSpatial in Step 1 / respawn);
            // ReadOnlySpan silences TYPHON009 and documents intent. Velocity/State/Genetics are non-spatial
            // — mutable GetSpan is correct.
            var bounds = cluster.GetReadOnlySpan(Ant.Bounds);
            var velocities = cluster.GetSpan(Ant.Velocity);
            var states = cluster.GetSpan(Ant.State);
            var genetics = cluster.GetSpan(Ant.Genetics);  // mutable so Larva maturation can flip Caste

            // Tier lookup via first occupied entity's position (same pattern as FillRender lines 894-899).
            // _tierMirror stores 0/1/2/3 directly (TierAssignment writes TrailingZeroCount(SimTier bit)).
            var firstIdx = BitOperations.TrailingZeroCount(bits0);
            var firstX = bounds[firstIdx].Bounds.MinX;
            var firstY = bounds[firstIdx].Bounds.MinY;
            var tcx = Math.Clamp((int)(firstX * InvCellSize), 0, GridCells - 1);
            var tcy = Math.Clamp((int)(firstY * InvCellSize), 0, GridCells - 1);
            int t = Math.Min((int)tierMirror[tcy * GridCells + tcx], 3);

            bool doMetab = (tick % AmortMetab[t]) == 0;
            bool doBrain = (tick % AmortBrain[t]) == 0;
            bool doPhero = (tick % AmortPhero[t]) == 0;
            // dtScaleMetab matches today's MetabolismTick `dtScale = ctx.AmortizedDeltaTime / BaseDt * _timeScale`.
            // dt already includes _timeScale (see assignment above); cellAmortize period × dt gives the amortized dt.
            float dtScaleMetab = AmortMetab[t] * dt / BaseDt;
            // pheroAmortScale matches today's PheromoneDepositTick `amortScale = ctx.AmortizedDeltaTime / BaseDt`
            // (no _timeScale — today's deposit code uses ctx.DeltaTime, not dt*_timeScale).
            float pheroAmortScale = AmortPhero[t] * ctx.DeltaTime / BaseDt;
            int amortBrainTicks = AmortBrain[t];

            // Phase 6D — gather rocks whose center is within (cluster.SpatialBounds expanded by
            // collision radius + a step margin). Linear scan over _rockPositions[]; rock count is
            // small (< 100 at demo scale) so a grid is overkill. nearby[] is reused across
            // clusters (stackalloc'd outside the loop) — reset count + refill each cluster.
            int nearbyCount = 0;
            int rockCountLocal = _rockCount;
            if (rockCountLocal > 0)
            {
                ref readonly var caabb = ref cluster.SpatialBounds;
                const float ExpandMargin = RockCollisionRadius + 50f;
                float rMinX = caabb.MinX - ExpandMargin;
                float rMaxX = caabb.MaxX + ExpandMargin;
                float rMinY = caabb.MinY - ExpandMargin;
                float rMaxY = caabb.MaxY + ExpandMargin;
                var rocksLocal = _rockPositions;
                for (int r = 0; r < rockCountLocal && nearbyCount < MaxNearbyRocks; r++)
                {
                    var rp = rocksLocal[r];
                    if (rp.x >= rMinX && rp.x <= rMaxX && rp.y >= rMinY && rp.y <= rMaxY)
                    {
                        nearby[nearbyCount++] = r;
                    }
                }
            }

            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref readonly var pos = ref bounds[idx];
                ref var vel = ref velocities[idx];
                ref var state = ref states[idx];
                ref var gen = ref genetics[idx];   // Phase 5: Larva maturation writes gen.Caste

                // Decay the hit-flash counter (set by combat damage roll; renderer reads it for the red tint).
                if (state.HitFlashTicks > 0) state.HitFlashTicks--;

                // ── Phase 5 Step 0: caste branch ──
                // Queens: contribute colony presence + nothing else.
                // Larvae: age up, contribute colony presence, no behaviour. Mature into Worker/Soldier.
                {
                    // Phase 6 polish — combat-grid indexing uses CombatGridCells / CombatInvCellSize,
                    // NOT the spatial-grid GridCells / InvCellSize. Combat detection runs at finer
                    // resolution (100² ≈ 1 m cells) while the spatial grid stays at 20² for cluster
                    // AABB / tier work.
                    var pCx = Math.Clamp((int)(pos.Bounds.MinX * CombatInvCellSize), 0, CombatGridCells - 1);
                    var pCy = Math.Clamp((int)(pos.Bounds.MinY * CombatInvCellSize), 0, CombatGridCells - 1);
                    var ccIdx = (pCy * CombatGridCells + pCx) * NestCount + gen.ColonyId;

                    if (gen.Caste == global::AntHill.Caste.Queen)
                    {
                        Interlocked.Increment(ref ccWrite[ccIdx]);
                        continue;   // queens skip movement / metabolism / combat — they're immobile + immune
                    }
                    if (gen.Caste == global::AntHill.Caste.Larva)
                    {
                        Interlocked.Increment(ref ccWrite[ccIdx]);
                        state.TicksAsLarva++;
                        if (state.TicksAsLarva >= LarvaMaturityTicks)
                        {
                            // Pseudo-random 80/20 Worker/Soldier split using a cheap hash.
                            var hh = (uint)(idx * 2654435761u + cluster.ChunkId * 40503u + tick);
                            gen.Caste = (hh % 100u) < 20u ? global::AntHill.Caste.Soldier : global::AntHill.Caste.Worker;
                            state.TicksAsLarva = 0;
                            // Spring into motion with a random heading.
                            var ang = (hh % 6283u) * 0.001f;
                            var spd = gen.Speed * 40f;
                            vel.X = MathF.Cos(ang) * spd;
                            vel.Y = MathF.Sin(ang) * spd;
                            _dbe.MarkClusterSlotDirty(AntArchetypeId, cluster.ChunkId, idx);
                        }
                        continue;
                    }

                    // Worker / Soldier: contribute presence, then run the rest of the body.
                    Interlocked.Increment(ref ccWrite[ccIdx]);
                }

                // ── Step 1: MoveAll — position integration + edge bounce ───────────────
                // Order matches today's phase ordering: Movement → Lifecycle → Sense → Brain → Trail.
                // Step delta is slope-modulated against the heightmap (rise/run along the motion
                // vector), so ants slow uphill and speed up downhill. Velocity itself is preserved
                // so the modulation doesn't drift across ticks.
                float postMoveX, postMoveY;
                {
                    var curX = pos.Bounds.MinX;
                    var curY = pos.Bounds.MinY;
                    var stepX = vel.X * dt;
                    var stepY = vel.Y * dt;

                    if (heightmap != null)
                    {
                        float sx = curX * SimToWorld;
                        float sy = curY * SimToWorld;
                        float tx = (curX + stepX) * SimToWorld;
                        float ty = (curY + stepY) * SimToWorld;
                        float dxz = MathF.Sqrt((tx - sx) * (tx - sx) + (ty - sy) * (ty - sy));
                        if (dxz > 0.001f)
                        {
                            float h1 = heightmap.Sample(sx, sy);
                            float h2 = heightmap.Sample(tx, ty);
                            float slope = (h2 - h1) / dxz;
                            float scale = Math.Clamp(1f - slope * SlopeGain, 0.5f, 1.5f);
                            stepX *= scale;
                            stepY *= scale;
                        }
                    }

                    var x = curX + stepX;
                    var y = curY + stepY;
                    var vx = vel.X;
                    var vy = vel.Y;
                    if (x < 0f) { x = -x; vx = -vx; }
                    else if (x > WorldSize) { x = 2f * WorldSize - x; vx = -vx; }
                    if (y < 0f) { y = -y; vy = -vy; }
                    else if (y > WorldSize) { y = 2f * WorldSize - y; vy = -vy; }

                    // Phase 6D — rock hard collision: push ant out of any rock it ended inside,
                    // then slide tangentially around the surface. Brain (Step 4) later this tick
                    // re-reads heading via Atan2(vy, vx) AND speed via sqrt(vx²+vy²), so we must
                    // preserve speed AND emit a non-zero tangent direction — otherwise the ant
                    // gets pinned at the rock surface and stops making progress.
                    if (nearbyCount > 0)
                    {
                        var rocksLocal2 = _rockPositions;
                        for (int r = 0; r < nearbyCount; r++)
                        {
                            var rp = rocksLocal2[nearby[r]];
                            float dx = x - rp.x;
                            float dy = y - rp.y;
                            float d2 = dx * dx + dy * dy;
                            if (d2 < RockCollisionRadiusSq)
                            {
                                float nx, ny;
                                if (d2 > 0.01f)
                                {
                                    float d = MathF.Sqrt(d2);
                                    float push = (RockCollisionRadius - d) / d;
                                    x += dx * push;
                                    y += dy * push;
                                    // Unit outward normal is dx/d, NOT dx/R. The previous dx/R version
                                    // had length d/R < 1, so the slide formula only partially zeroed the
                                    // inward velocity component and ants kept oozing back into the rock.
                                    nx = dx / d;
                                    ny = dy / d;
                                }
                                else
                                {
                                    // Degenerate: rock placed directly on top of ant. Eject east.
                                    x = rp.x + RockCollisionRadius;
                                    y = rp.y;
                                    nx = 1f;
                                    ny = 0f;
                                }
                                float vRadial = vx * nx + vy * ny;
                                if (vRadial < 0f)
                                {
                                    float origSpeed2 = vx * vx + vy * vy;
                                    if (origSpeed2 >= 0.01f)
                                    {
                                        // Standard slide: tangent = v − (v·n)·n. Magnitude shrinks by
                                        // sin(angle of incidence) — i.e., head-on hits go to zero.
                                        vx -= vRadial * nx;
                                        vy -= vRadial * ny;
                                        float newSpeed2 = vx * vx + vy * vy;
                                        if (newSpeed2 > 0.01f)
                                        {
                                            // Glancing collision: renormalize tangent to the ant's
                                            // original speed. Ants are self-propelled, not rigid bodies
                                            // — energy preservation is the right model.
                                            float scale = MathF.Sqrt(origSpeed2 / newSpeed2);
                                            vx *= scale;
                                            vy *= scale;
                                        }
                                        else
                                        {
                                            // Head-on hit: tangent is zero. Rotate the normal 90° so
                                            // the ant peels off along the surface. Per-ant sign avoids
                                            // every neighbor swinging to the same side (would clump).
                                            float origSpeed = MathF.Sqrt(origSpeed2);
                                            float sign = ((idx + cluster.ChunkId) & 1) == 0 ? 1f : -1f;
                                            vx = -ny * origSpeed * sign;
                                            vy =  nx * origSpeed * sign;
                                        }
                                    }
                                    // else: ant essentially stationary — let brain reassign velocity
                                    // this tick (line ~1037 restores speed when |vel| < 0.01).
                                }
                            }
                        }
                        // Rock push-out can shove the ant past the world edge — re-clamp.
                        if (x < 0f) x = 0f; else if (x > WorldSize) x = WorldSize;
                        if (y < 0f) y = 0f; else if (y > WorldSize) y = WorldSize;
                    }

                    // Route the spatial-field write through the WriteSpatial barrier so the engine flags
                    // migration / AABB grow / AABB shrink inline (eliminates the fence-time slot scan that
                    // previously cost ~8 ms per tick). The barrier marks the slot dirty internally; no
                    // separate MarkClusterSlotDirty needed for the position.
                    cluster.WriteSpatial(Ant.Bounds, idx, new WorldBounds { Bounds = new AABB2F { MinX = x, MaxX = x, MinY = y, MaxY = y } });
                    vel.X = vx;
                    vel.Y = vy;
                    postMoveX = x;
                    postMoveY = y;
                }

                // ── Soldier position cache (Phase 6 polish) — drains in SpiderUpdateTick for the
                // "soldiers in melee range damage the spider" mechanic. Skipped for non-soldiers so
                // the typical-cluster overhead is just a caste-check branch.
                if (gen.Caste == global::AntHill.Caste.Soldier)
                {
                    int slot = Interlocked.Increment(ref _soldierCount) - 1;
                    if (slot < _soldierPositions.Length)
                    {
                        _soldierPositions[slot] = (postMoveX, postMoveY);
                    }
                }

                // ── Step 1.5: Fire kill + fire-smell pheromone deposit (Phase 6 polish) ─
                // First scan a 3×3 neighbourhood around the ant's cell — any Burning neighbour
                // means the ant "smells fire" and emits a Fire-warning pheromone (the colony's
                // collective danger signal). Then if the ant's own cell is Burning, kill it inline
                // (deposit happens before kill so a dying ant still warns its neighbours). Done
                // inline rather than via Energy=0 + metabolism so tier-amortized clusters can't let
                // killed ants visibly wander the flames for up to 60 ticks.
                // Gated to 10 Hz (matches CA cadence — the fire grid doesn't change between CA ticks).
                if (checkFire)
                {
                    int fcx = (int)(postMoveX * FireGrid.InvCellSizeSim);
                    int fcy = (int)(postMoveY * FireGrid.InvCellSizeSim);
                    if ((uint)fcx < (uint)FireGrid.Size && (uint)fcy < (uint)FireGrid.Size)
                    {
                        // 3×3 fire-smell scan (incl. own cell).
                        bool nearFire = false;
                        int nx0 = fcx > 0 ? fcx - 1 : fcx;
                        int nx1 = fcx < FireGrid.Size - 1 ? fcx + 1 : fcx;
                        int ny0 = fcy > 0 ? fcy - 1 : fcy;
                        int ny1 = fcy < FireGrid.Size - 1 ? fcy + 1 : fcy;
                        for (int ny = ny0; ny <= ny1 && !nearFire; ny++)
                        {
                            int rowBase = ny * FireGrid.Size;
                            for (int nxs = nx0; nxs <= nx1; nxs++)
                            {
                                byte s = fireState[rowBase + nxs];
                                if (s >= FireGrid.BurnMin && s <= FireGrid.BurnStart) { nearFire = true; break; }
                            }
                        }
                        if (nearFire)
                        {
                            PheromoneGrid.Deposit(phero.Fire, PheromoneGrid.WorldToIndex(postMoveX, postMoveY), FireDeposit);
                        }

                        byte fireCell = fireState[fcy * FireGrid.Size + fcx];
                        if (fireCell >= FireGrid.BurnMin && fireCell <= FireGrid.BurnStart)
                        {
                            // Single-tick red flash on the renderer for the moment of death (the
                            // ant teleports to its nest immediately so the flash is brief but visible).
                            state.HitFlashTicks = HitFlashDuration;
                            var ni = gen.HomeNestIndex;
                            deathQueue?.Push(new AntDiedEvent((uint)((cluster.ChunkId << 8) | idx), ni));

                            var freeE = gen.BaseEnergy * 0.5f;
                            var bonusE = 0f;
                            if (ni >= 0 && ni < NestCount &&
                                Interlocked.Add(ref nestStock[ni], -gen.EatAmount) >= 0)
                            {
                                bonusE = gen.BaseEnergy * 0.5f;
                            }
                            else if (ni >= 0 && ni < NestCount)
                            {
                                Interlocked.Add(ref nestStock[ni], gen.EatAmount);
                            }
                            state.Energy = freeE + bonusE;
                            state.State = AntState.Foraging;
                            gen.Caste = global::AntHill.Caste.Larva;
                            state.TicksAsLarva = 0;

                            cluster.WriteSpatial(Ant.Bounds, idx,
                                new WorldBounds { Bounds = new AABB2F { MinX = nests[ni].x, MaxX = nests[ni].x, MinY = nests[ni].y, MaxY = nests[ni].y } });
                            vel.X = 0f;
                            vel.Y = 0f;
                            _dbe.MarkClusterSlotDirty(AntArchetypeId, cluster.ChunkId, idx);

                            Interlocked.Increment(ref _fireKillsAccum);
                            _fireKillLastX = (int)postMoveX;
                            _fireKillLastY = (int)postMoveY;
                            continue;   // skip downstream steps (metabolism / food / brain / phero)
                        }
                    }
                }

                // ── Step 2: Metabolism — energy decay + respawn (gated by doMetab) ─────
                if (doMetab)
                {
                    state.Energy -= EnergyDrainRate * dtScaleMetab;

                    if (state.Energy <= 0f)
                    {
                        var ni = gen.HomeNestIndex;
                        deathQueue?.Push(new AntDiedEvent((uint)((cluster.ChunkId << 8) | idx), ni));

                        var freeE = gen.BaseEnergy * 0.5f;
                        var bonusE = 0f;
                        if (ni >= 0 && ni < NestCount &&
                            Interlocked.Add(ref nestStock[ni], -gen.EatAmount) >= 0)
                        {
                            bonusE = gen.BaseEnergy * 0.5f;
                        }
                        else if (ni >= 0 && ni < NestCount)
                        {
                            Interlocked.Add(ref nestStock[ni], gen.EatAmount);
                        }
                        state.Energy = freeE + bonusE;
                        state.State = AntState.Foraging;

                        // Phase 5: respawn as Larva — re-enter the maturation loop. Queens skipped
                        // (continued out at Step 0.5). Workers / Soldiers that died here flip to Larva
                        // so the player visibly sees population renewal as small ants growing.
                        gen.Caste = global::AntHill.Caste.Larva;
                        state.TicksAsLarva = 0;

                        // Teleport to nest + zero velocity (Larva doesn't move). Position write goes through
                        // the WriteSpatial barrier (handles migration detection — respawn teleports across
                        // the map). The MarkClusterSlotDirty call covers the non-spatial writes above
                        // (gen.Caste, state.TicksAsLarva, state.Energy, state.State) — WriteSpatial only
                        // marks the slot once but the double-mark below is harmless.
                        cluster.WriteSpatial(Ant.Bounds, idx,
                            new WorldBounds { Bounds = new AABB2F { MinX = nests[ni].x, MaxX = nests[ni].x, MinY = nests[ni].y, MaxY = nests[ni].y } });
                        vel.X = 0f;
                        vel.Y = 0f;
                        _dbe.MarkClusterSlotDirty(AntArchetypeId, cluster.ChunkId, idx);
                    }
                }

                // Skip downstream steps for dead ants (matches today's per-system `if (state.Energy <= 0f) continue;`).
                if (state.Energy <= 0f)
                {
                    continue;
                }

                // ── Step 3: Food/nest interaction (every tick) ─────────────────────────
                if (state.State == AntState.Foraging)
                {
                    var gcx = Math.Clamp((int)(pos.X * FoodGridInvCellSize), 0, FoodGridCells - 1);
                    var gcy = Math.Clamp((int)(pos.Y * FoodGridInvCellSize), 0, FoodGridCells - 1);
                    var candidates = foodGrid[gcy * FoodGridCells + gcx];
                    if (candidates != null)
                    {
                        var bestDistSq = float.MaxValue;
                        var bestIdx = -1;
                        for (var ci = 0; ci < candidates.Length; ci++)
                        {
                            var fi = candidates[ci];
                            if (foodRemaining[fi] <= 0) continue;
                            var dx = pos.X - food[fi].x;
                            var dy = pos.Y - food[fi].y;
                            var distSq = dx * dx + dy * dy;

                            if (distSq < FoodPickupRangeSq)
                            {
                                var after = Interlocked.Decrement(ref foodRemaining[fi]);
                                if (after >= 0)
                                {
                                    state.State = AntState.ReturningFrom(fi);
                                    state.Energy = gen.BaseEnergy;
                                    vel.X = -vel.X;
                                    vel.Y = -vel.Y;
                                    pickedUpQueue?.Push(new FoodPickedUpEvent((uint)((cluster.ChunkId << 8) | idx), fi));
                                    // Depletion: the count just transitioned from 1 → 0. Rare (~once per food source);
                                    // ConcurrentQueue.Enqueue is thread-safe across all sim workers.
                                    if (after == 0)
                                    {
                                        const float SimToWorld = 100f / WorldSize;
                                        _eventLog.Enqueue(new LogEntry(_simTimeSec,
                                            $"Food source depleted at ({food[fi].x * SimToWorld:F1}, {food[fi].y * SimToWorld:F1})",
                                            food[fi].x * SimToWorld, food[fi].y * SimToWorld, LogSeverity.Depletion));
                                    }
                                }
                                else
                                {
                                    Interlocked.Increment(ref foodRemaining[fi]);
                                }
                                bestIdx = -1;
                                break;
                            }

                            if (distSq < FoodSmellRangeSq && distSq < bestDistSq)
                            {
                                bestDistSq = distSq;
                                bestIdx = fi;
                            }
                        }

                        if (bestIdx >= 0)
                        {
                            var heading = MathF.Atan2(food[bestIdx].y - pos.Y, food[bestIdx].x - pos.X);
                            var speed = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y);
                            if (speed < 0.01f) speed = gen.Speed * 40f;
                            vel.X = MathF.Cos(heading) * speed;
                            vel.Y = MathF.Sin(heading) * speed;
                        }
                    }
                }
                else // Returning
                {
                    var ni = gen.HomeNestIndex;
                    var dx = pos.X - nests[ni].x;
                    var dy = pos.Y - nests[ni].y;
                    if (dx * dx + dy * dy < NestDropRangeSq)
                    {
                        Interlocked.Add(ref nestStock[ni], 3);
                        deliveredQueue?.Push(new FoodDeliveredEvent((uint)((cluster.ChunkId << 8) | idx), ni, 3));
                        state.State = AntState.Foraging;
                    }
                }

                // ── Phase 5 Step 3.5: Combat ──────────────────────────────────────────
                // Intra-cluster probabilistic damage: when ccRead (previous tick's complete totals)
                // shows opposing-colony presence in this ant's cell above CombatContestedThreshold,
                // roll for damage. Damage applied to OWN cluster's AntState only — no cross-cluster
                // writes, no race. Gated to T0 clusters; combat is a "what you see" phenomenon.
                if (t == 0)
                {
                    // Combat grid is finer than spatial grid (see CombatGridCells definition).
                    var pCx = Math.Clamp((int)(pos.Bounds.MinX * CombatInvCellSize), 0, CombatGridCells - 1);
                    var pCy = Math.Clamp((int)(pos.Bounds.MinY * CombatInvCellSize), 0, CombatGridCells - 1);
                    var cellBase = (pCy * CombatGridCells + pCx) * NestCount;
                    var myColony = gen.ColonyId;
                    int opposingTotal = 0;
                    for (var c = 0; c < NestCount; c++)
                    {
                        if (c == myColony) continue;
                        opposingTotal += ccRead[cellBase + c];
                    }
                    if (opposingTotal >= CombatContestedThreshold)
                    {
                        var rollPct = gen.Caste == global::AntHill.Caste.Soldier
                            ? CombatRollSoldierPercent
                            : CombatRollWorkerPercent;
                        var hh = (uint)(idx * 2246822519u + cluster.ChunkId * 3266489917u + tick);
                        if ((hh % 100u) < (uint)rollPct)
                        {
                            state.Energy -= CombatBaseDamage;
                            state.HitFlashTicks = HitFlashDuration;
                            // Phase 6 polish — emit Fight alarm pheromone in a 3×3 stamp around the
                            // victim. Single-cell deposit was too localized: ants whose sensors
                            // didn't fall in that exact cell read 0. 3×3 with falloff covers a
                            // ~1.5-cell radius (≈ 30 sim units / 0.15 m) so a typical 40-sim sensor
                            // catches the gradient reliably.
                            PheromoneGrid.DepositArea(phero.Fight, pos.Bounds.MinX, pos.Bounds.MinY, FightDeposit);
                        }
                    }
                }

                // ── Step 4: Brain — pheromone steering + wander (gated by doBrain) ─────
                // Inter-cluster note: this reads phero.Food/Home, which Step 5 writes for OTHER clusters
                // running on parallel workers. Race is bounded by 0.995/tick decay; documented as
                // accepted looseness for the cluster-walk cache-locality win.
                if (doBrain)
                {
                    var speed = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y);
                    if (speed < 0.01f) speed = gen.Speed * 40f;
                    var heading = MathF.Atan2(vel.Y, vel.X);

                    var steered = false;

                    // ── Phase 6 polish: caste-conditional danger steering ──
                    // Sample Fire + Fight at the same 3-sensor positions used for food/home below.
                    // Priorities (both castes): Fire > Fight > (food/home, handled later).
                    // Worker on Fight signal: flee (steer AWAY from max-fight sensor).
                    // Soldier on Fight signal: converge (steer TOWARD max-fight sensor).
                    {
                        var lx = pos.X + MathF.Cos(heading - SensorAngle) * SensorDistance;
                        var ly = pos.Y + MathF.Sin(heading - SensorAngle) * SensorDistance;
                        var cxs = pos.X + MathF.Cos(heading) * SensorDistance;
                        var cys = pos.Y + MathF.Sin(heading) * SensorDistance;
                        var rxs = pos.X + MathF.Cos(heading + SensorAngle) * SensorDistance;
                        var rys = pos.Y + MathF.Sin(heading + SensorAngle) * SensorDistance;

                        var iL = PheromoneGrid.WorldToIndex(lx, ly);
                        var iC = PheromoneGrid.WorldToIndex(cxs, cys);
                        var iR = PheromoneGrid.WorldToIndex(rxs, rys);

                        var fireL = phero.Fire[iL];
                        var fireC = phero.Fire[iC];
                        var fireR = phero.Fire[iR];
                        var maxFire = MathF.Max(fireL, MathF.Max(fireC, fireR));

                        var dangerStep = SteerStrength * DangerSteerMultiplier;
                        var isSoldier = gen.Caste == global::AntHill.Caste.Soldier;

                        if (maxFire > FireSteerThreshold)
                        {
                            // Both castes flee fire. Steer AWAY from the sensor with max fire reading.
                            if (fireL >= fireC && fireL >= fireR) heading += dangerStep;
                            else if (fireR >= fireL && fireR >= fireC) heading -= dangerStep;
                            else
                            {
                                // Center sensor max → danger directly ahead. Turn hard to one side
                                // (per-ant deterministic sign so neighbors don't all swing the same way).
                                var sign = ((idx + cluster.ChunkId) & 1) == 0 ? 1f : -1f;
                                heading += dangerStep * 2f * sign;
                            }
                            steered = true;
                        }
                        else
                        {
                            var fightL = phero.Fight[iL];
                            var fightC = phero.Fight[iC];
                            var fightR = phero.Fight[iR];
                            var maxFight = MathF.Max(fightL, MathF.Max(fightC, fightR));

                            if (isSoldier)
                            {
                                if (maxFight > FightAttractThreshold)
                                {
                                    // Converge — steer TOWARD max-fight sensor.
                                    if (fightL >= fightC && fightL >= fightR) heading -= dangerStep;
                                    else if (fightR >= fightL && fightR >= fightC) heading += dangerStep;
                                    // Center max → already on track; no steer needed.
                                    steered = true;
                                }
                            }
                            else if (maxFight > FightFleeThreshold)
                            {
                                // Worker — flee Fight phero same way as fire.
                                if (fightL >= fightC && fightL >= fightR) heading += dangerStep;
                                else if (fightR >= fightL && fightR >= fightC) heading -= dangerStep;
                                else
                                {
                                    var sign = ((idx + cluster.ChunkId) & 1) == 0 ? 1f : -1f;
                                    heading += dangerStep * 2f * sign;
                                }
                                steered = true;
                            }
                        }
                    }

                    // Existing food/home steering — skipped if danger already took the wheel.
                    if (!steered && state.State == AntState.Foraging)
                    {
                        var sL = phero.Food[PheromoneGrid.WorldToIndex(
                            pos.X + MathF.Cos(heading - SensorAngle) * SensorDistance,
                            pos.Y + MathF.Sin(heading - SensorAngle) * SensorDistance)];
                        var sC = phero.Food[PheromoneGrid.WorldToIndex(
                            pos.X + MathF.Cos(heading) * SensorDistance,
                            pos.Y + MathF.Sin(heading) * SensorDistance)];
                        var sR = phero.Food[PheromoneGrid.WorldToIndex(
                            pos.X + MathF.Cos(heading + SensorAngle) * SensorDistance,
                            pos.Y + MathF.Sin(heading + SensorAngle) * SensorDistance)];

                        if (sL > sC && sL > sR) { heading -= SteerStrength; steered = true; }
                        else if (sR > sC && sR > sL) { heading += SteerStrength; steered = true; }
                        else if (sC > 0.1f) { steered = true; }
                    }
                    else if (!steered) // Returning — pheromone + nest direction validation
                    {
                        var ni = gen.HomeNestIndex;
                        var toNestX = nests[ni].x - pos.X;
                        var toNestY = nests[ni].y - pos.Y;
                        var nestHeading = MathF.Atan2(toNestY, toNestX);

                        var sL = phero.Home[PheromoneGrid.WorldToIndex(
                            pos.X + MathF.Cos(heading - SensorAngle) * SensorDistance,
                            pos.Y + MathF.Sin(heading - SensorAngle) * SensorDistance)];
                        var sC = phero.Home[PheromoneGrid.WorldToIndex(
                            pos.X + MathF.Cos(heading) * SensorDistance,
                            pos.Y + MathF.Sin(heading) * SensorDistance)];
                        var sR = phero.Home[PheromoneGrid.WorldToIndex(
                            pos.X + MathF.Cos(heading + SensorAngle) * SensorDistance,
                            pos.Y + MathF.Sin(heading + SensorAngle) * SensorDistance)];

                        var pheroHeading = heading;
                        if (sL > sC && sL > sR) pheroHeading -= SteerStrength;
                        else if (sR > sC && sR > sL) pheroHeading += SteerStrength;

                        var dot = MathF.Cos(pheroHeading) * toNestX + MathF.Sin(pheroHeading) * toNestY;
                        heading = dot > 0f ? pheroHeading : nestHeading;
                        steered = true;
                    }

                    if (!steered)
                    {
                        var h = (uint)(idx * 2654435761 + cluster.ChunkId * 40503);
                        var jitter = ((h + (uint)tick * 2246822519u) % 1000u / 1000f - 0.5f) * 2f * WanderJitter;
                        heading += jitter;

                        // Today's wander epoch trigger uses (long)ctx.AmortizedDeltaTime which truncates
                        // to 0 for tiers T0/T1/T2 (their AmortizedDeltaTime < 1.0s). So `epoch != prevEpoch`
                        // is always false there — direction-change only fires for T3. Preserved here for
                        // exact behavioral match with the pre-merge topology.
                        var amortDt = amortBrainTicks * BaseDt;
                        var epoch = (uint)(tick / WanderChangeTicks);
                        var prevEpoch = (uint)((tick - (long)amortDt * 60) / WanderChangeTicks);
                        if (epoch != prevEpoch)
                        {
                            var turn = ((h * 48271u + epoch * 16807u) % 1000u / 1000f - 0.5f) * 2f * WanderTurnMax;
                            heading += turn;
                        }
                    }

                    vel.X = MathF.Cos(heading) * speed;
                    vel.Y = MathF.Sin(heading) * speed;
                }

                // ── Step 5: PheroDep — deposit at current cell (gated by doPhero) ──────
                if (doPhero)
                {
                    var cellIdx = PheromoneGrid.WorldToIndex(pos.X, pos.Y);

                    if (state.State == AntState.Foraging)
                    {
                        // Foraging ants leave only a faint home trail.
                        PheromoneGrid.Deposit(phero.Home, cellIdx, BaseDeposit * 0.1f * pheroAmortScale);
                    }
                    else
                    {
                        // Returning: stronger food pheromone, scaled by distance to source.
                        var deposit = BaseDeposit;
                        var fi = state.FoodSourceIndex;
                        if (fi >= 0 && fi < food.Length)
                        {
                            var dx = pos.X - food[fi].x;
                            var dy = pos.Y - food[fi].y;
                            var distSq = dx * dx + dy * dy;
                            if (distSq < DepositFalloffRangeSq)
                            {
                                deposit += (1f - MathF.Sqrt(distSq) / DepositFalloffRange) * NearFoodMultiplier;
                            }
                        }
                        PheromoneGrid.Deposit(phero.Food, cellIdx, deposit * pheroAmortScale);
                    }
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Render Pipeline
    // ═══════════════════════════════════════════════════════════════════

    internal void PrepareRender(TickContext ctx)
    {
        var crossings = Interlocked.Exchange(ref _cellCrossingsThisTick, 0);
        _crossingsAccum += crossings;
        _crossingsTickCount++;
        if (_crossingsTickCount >= 60)
        {
            CrossingsPerSecond = _crossingsAccum;
            _crossingsAccum = 0;
            _crossingsTickCount = 0;
        }

        _tierCounts[0] = 0; _tierCounts[1] = 0; _tierCounts[2] = 0; _tierCounts[3] = 0;
        _stateCounts[0] = 0; _stateCounts[1] = 0;

        // Anchor instances: two invisible points per worker buffer at world corners.
        // Forces Godot's computed AABB to span the world so it never culls the node.
        for (var w = 0; w < _workerBuffers.Length; w++)
        {
            var wb = _workerBuffers[w];
            wb.EnsureCapacity(2);
            var buf = wb.Data;
            // Anchor at (0, 0) — scale 0, alpha 0
            buf[0] = 0f; buf[1] = 0f; buf[2] = 0f; buf[3] = 0f;
            buf[4] = 0f; buf[5] = 0f; buf[6] = 0f; buf[7] = 0f;
            buf[8] = 0f; buf[9] = 0f; buf[10] = 0f; buf[11] = 0f;
            // Anchor at (WorldSize, WorldSize)
            buf[12] = 0f; buf[13] = 0f; buf[14] = 0f; buf[15] = WorldSize;
            buf[16] = 0f; buf[17] = 0f; buf[18] = 0f; buf[19] = WorldSize;
            buf[20] = 0f; buf[21] = 0f; buf[22] = 0f; buf[23] = 0f;
            wb.Count = 2;
        }

        // Food + nests into overlay buffer (small, runs once)
        _overlayBuffer.Reset();
        _overlayBuffer.EnsureCapacity(_foodCount + NestCount);
        var oBuf = _overlayBuffer.Data;
        var oi = 0;

        for (var fi = 0; fi < _foodCount; fi++)
        {
            var (fx, fy, initial) = _foodCache[fi];
            var rem = _foodRemainingInt[fi];
            var ratio = initial > 0 ? Math.Max(rem, 0) / initial : 0f;
            var scale = 2f + ratio * 10f;
            var green = 0.3f + ratio * 0.7f;
            var off = oi * Stride;
            oBuf[off + 0] = scale; oBuf[off + 1] = 0f; oBuf[off + 2] = 0f; oBuf[off + 3] = fx;
            oBuf[off + 4] = 0f; oBuf[off + 5] = scale; oBuf[off + 6] = 0f; oBuf[off + 7] = fy;
            oBuf[off + 8] = 0.2f; oBuf[off + 9] = green; oBuf[off + 10] = 0.2f; oBuf[off + 11] = 0.5f;
            oi++;
        }

        for (var ni = 0; ni < _nestPositions.Length; ni++)
        {
            var (nx, ny) = _nestPositions[ni];
            var stock = _nestFoodStock[ni];
            var nestRatio = Math.Clamp(stock / (float)InitialNestFood, 0f, 3f);
            var nestScale = 3f + nestRatio * 20f;
            var off = oi * Stride;
            oBuf[off + 0] = nestScale; oBuf[off + 1] = 0f; oBuf[off + 2] = 0f; oBuf[off + 3] = nx;
            oBuf[off + 4] = 0f; oBuf[off + 5] = nestScale; oBuf[off + 6] = 0f; oBuf[off + 7] = ny;
            oBuf[off + 8] = 0.6f; oBuf[off + 9] = 0.3f; oBuf[off + 10] = 0.1f; oBuf[off + 11] = 0.5f;
            oi++;
        }

        _overlayBuffer.Count = oi;

        // Heatmap downsample + RGBA pack moved into dedicated parallel systems (Phase 6 polish):
        //   3× PheroMaxReduceSystem (Food/Home/Fight) — ChunkedCallbackSystem, parallel across workers
        //   1× HeatmapRgbaPackSystem — runs concurrent with the max systems (1-tick tearing tolerated)
        // See HeatmapSystems.cs. PrepareRender keeps its non-heatmap work only.

        // Phase 6B — snapshot the current fire-grid state into the write buffer so PublishRender
        // can hand it to the RenderFrame. Cheap (40 KB BlockCopy, ~5 µs) and decouples the
        // render-side read from the sim-side double-buffer swap inside FireGrid.Tick.
        Buffer.BlockCopy(_fireGrid.State, 0, _fireR8Write, 0, FireGrid.CellCount);

        // Phase 6C — drain plant-state dirty indices into the write buffer. Bounded to PlantDirtyCapacity;
        // overflow is benign (the next CA tick re-enqueues any plant on the same Burning cell).
        var pg = PlantGrid;
        var n = 0;
        if (pg != null)
        {
            var queue = pg.DirtyIndices;
            while (n < _plantDirtyWrite.Length && queue.TryDequeue(out var idx))
            {
                _plantDirtyWrite[n++] = idx;
            }
        }
        _plantDirtyWriteCount = n;
    }

    internal void FillRender(TickContext ctx)
    {
        var wb = _workerBuffers[ctx.WorkerId];
        var tierMirror = _tierMirror;
        var invCellSize = 1f / CellSize;

        // Snapshot camera AABB for clipping.
        // Cluster AABBs are one tick stale (recomputed at tick fence, after FillRender).
        // Add a margin so clusters near the camera edge aren't wrongly rejected due to
        // entity movement since the last AABB recompute. The per-entity clip (below)
        // still does exact filtering.
        const float clusterMargin = CellSize;
        var clipMinX = _camMinX - clusterMargin;
        var clipMinY = _camMinY - clusterMargin;
        var clipMaxX = _camMaxX + clusterMargin;
        var clipMaxY = _camMaxY + clusterMargin;

        Span<int> localTiers = stackalloc int[4];
        int sForaging = 0, sCarrying = 0;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Ant>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Ant>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var liveCount = cluster.LiveCount;

            // Fast reject: cluster tight AABB fully outside camera (with margin)
            ref readonly var bounds = ref cluster.SpatialBounds;
            if (bounds.MaxX < clipMinX || bounds.MinX > clipMaxX || bounds.MaxY < clipMinY || bounds.MinY > clipMaxY)
            {
                continue;
            }

            // Cluster overlaps camera — render visible ants
            var positions = cluster.GetReadOnlySpan(Ant.Bounds);
            var statesVis = cluster.GetReadOnlySpan(Ant.State);
            var genetics = cluster.GetReadOnlySpan(Ant.Genetics);

            // Tier from first entity position
            var bitsVis = cluster.OccupancyBits;
            var firstIdx = BitOperations.TrailingZeroCount(bitsVis);
            ref readonly var firstPos = ref positions[firstIdx];
            var tcx = Math.Clamp((int)(firstPos.X * invCellSize), 0, GridCells - 1);
            var tcy = Math.Clamp((int)(firstPos.Y * invCellSize), 0, GridCells - 1);
            localTiers[Math.Min((int)tierMirror[tcy * GridCells + tcx], 3)] += liveCount;

            wb.EnsureCapacity(liveCount);
            var buf = wb.Data;
            var writeIdx = wb.Count;

            while (bitsVis != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bitsVis);
                bitsVis &= bitsVis - 1;
                ref readonly var pos = ref positions[idx];
                ref readonly var state = ref statesVis[idx];

                sForaging += 1 - state.State;
                sCarrying += state.State;

                // Per-entity clip for clusters that straddle the camera edge
                if (pos.X < clipMinX || pos.X > clipMaxX || pos.Y < clipMinY || pos.Y > clipMaxY)
                {
                    continue;
                }

                ref readonly var gen = ref genetics[idx];
                var energyRatio = gen.BaseEnergy > 0f ? Math.Clamp(state.Energy / gen.BaseEnergy, 0f, 1f) : 0f;
                var alpha = state.IsReturning ? 1.0f : (0.15f + energyRatio * 0.70f);

                // Phase 5: colony palette drives the ant's color. Returning ants get a small brightness
                // boost so the player still reads the carry signal at Loupe band.
                var paletteIdx = gen.ColonyId < NestCount ? gen.ColonyId : (byte)0;
                var palette = _colonyPalette[paletteIdx];
                var brightness = state.IsReturning ? 1.20f : 0.95f;
                var r = Math.Min(1f, palette.X * brightness);
                var g = Math.Min(1f, palette.Y * brightness);
                var b = Math.Min(1f, palette.Z * brightness);

                // Phase 5: hit-flash — recently-damaged ants flash bright red. Linearly fades over
                // HitFlashDuration ticks (~130 ms). Continuous damage keeps the ant red.
                if (state.HitFlashTicks > 0)
                {
                    float flashT = state.HitFlashTicks / (float)HitFlashDuration;
                    r = r + (1.0f - r) * flashT;
                    g = g + (0.10f - g) * flashT;
                    b = b + (0.10f - b) * flashT;
                }

                // Caste packed into buf[off + 1] so AntRenderer.WriteState reads it for scale_byte.
                // Worker / Soldier / Larva / Queen map onto 0.6×..2.0× via the renderer's scale_min/max.
                var off = writeIdx * Stride;
                buf[off + 0] = 1f;  buf[off + 1] = gen.Caste; buf[off + 2] = 0f; buf[off + 3] = pos.X;
                buf[off + 4] = 0f;  buf[off + 5] = 1f; buf[off + 6] = 0f; buf[off + 7] = pos.Y;
                buf[off + 8] = r;   buf[off + 9] = g;  buf[off + 10] = b; buf[off + 11] = alpha;
                writeIdx++;
            }

            wb.Count = writeIdx;
        }

        Interlocked.Add(ref _tierCounts[0], localTiers[0]);
        Interlocked.Add(ref _tierCounts[1], localTiers[1]);
        Interlocked.Add(ref _tierCounts[2], localTiers[2]);
        Interlocked.Add(ref _tierCounts[3], localTiers[3]);
        Interlocked.Add(ref _stateCounts[0], sForaging);
        Interlocked.Add(ref _stateCounts[1], sCarrying);
    }

    internal void PublishRender(TickContext ctx)
    {
        // Snapshot current Data/Count into immutable frame — Godot reads only this
        var buffers = new BufferSnapshot[_workerBuffers.Length];
        var total = 0;
        for (var i = 0; i < _workerBuffers.Length; i++)
        {
            buffers[i] = new BufferSnapshot { Data = _workerBuffers[i].Data, Count = _workerBuffers[i].Count };
            total += _workerBuffers[i].Count;
        }
        VisibleAnts = total;

        var frame = new RenderFrame
        {
            Buffers = buffers,
            Overlay = new BufferSnapshot { Data = _overlayBuffer.Data, Count = _overlayBuffer.Count },
            VisibleAnts = total,
            HeatmapRGBA = _heatmapRGBARead,
            FireR8 = _fireR8Read,
            PlantDirty = _plantDirtyRead,
            PlantDirtyCount = _plantDirtyReadCount,
        };

        _renderBridge.Publish(frame);

        // Swap heatmap buffer
        (_heatmapRGBA, _heatmapRGBARead) = (_heatmapRGBARead, _heatmapRGBA);
        // Swap fire buffer (Phase 6B). After publish, _fireR8Read points to the array Godot will
        // upload next frame; _fireR8Write becomes the target for next PrepareRender's BlockCopy.
        (_fireR8Write, _fireR8Read) = (_fireR8Read, _fireR8Write);
        // Swap plant-dirty buffer (Phase 6C). _plantDirtyRead now points to the indices that
        // PrepareRender just drained from PlantGrid.DirtyIndices; VegetationRenderer reads
        // [0, _plantDirtyReadCount) from it next Godot frame. Write buffer becomes next tick's target.
        (_plantDirtyWrite, _plantDirtyRead) = (_plantDirtyRead, _plantDirtyWrite);
        _plantDirtyReadCount = _plantDirtyWriteCount;
        _plantDirtyWriteCount = 0;

        // Swap all render buffers AFTER publish — next frame writes to the other slot
        for (var i = 0; i < _workerBuffers.Length; i++)
        {
            _workerBuffers[i].Reset();
        }
        _overlayBuffer.Reset();

        // Console timing dump every ~2s (120 ticks at 60fps)
        if (ctx.TickNumber % 120 == 0 && _runtime?.Telemetry != null)
        {
            var telemetry = _runtime.Telemetry;
            if (telemetry.TotalTicksRecorded > 0)
            {
                var tickNum = telemetry.NewestTick;
                ref readonly var tick = ref telemetry.GetTick(tickNum);
                var systems = telemetry.GetSystemMetrics(tickNum);
                var sysDefs = _runtime.Systems;

                Console.Write($"T{ctx.TickNumber,6} {tick.ActualDurationMs:F1}ms | T0:{_tierCounts[0]} T1:{_tierCounts[1]} T2:{_tierCounts[2]} T3:{_tierCounts[3]} |");
                for (var i = 0; i < systems.Length && i < sysDefs.Length; i++)
                {
                    ref readonly var s = ref systems[i];
                    if (!s.WasSkipped && s.DurationUs > 1f)
                    {
                        Console.Write($" {sysDefs[i].Name}:{s.DurationUs:F0}");
                    }
                }
                Console.WriteLine();
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Entity Spawning
    // ═══════════════════════════════════════════════════════════════════

    private void SpawnAnts()
    {
        var rng = new Random(42);
        const int batchSize = 1_000;
        var antsPerNest = AntCount / NestCount;
        var spawnRadius = 200f;

        for (var nestIdx = 0; nestIdx < NestCount; nestIdx++)
        {
            var (nx, ny) = _nestPositions[nestIdx];
            var remaining = (nestIdx == NestCount - 1) ? AntCount - antsPerNest * (NestCount - 1) : antsPerNest;
            int antIndexInNest = 0;   // first ant per nest = Queen

            while (remaining > 0)
            {
                var count = Math.Min(batchSize, remaining);
                remaining -= count;
                using var tx = _dbe.CreateQuickTransaction();

                for (var i = 0; i < count; i++, antIndexInNest++)
                {
                    // Phase 5: caste mix per nest — first ant = Queen (planted), then ~15% Soldier,
                    // ~4% Larva, rest Worker. Queens / Larvae spawn at nest centre with zero velocity.
                    byte caste;
                    if (antIndexInNest == 0) caste = global::AntHill.Caste.Queen;
                    else
                    {
                        var roll = rng.NextDouble();
                        if (roll < 0.04) caste = global::AntHill.Caste.Larva;
                        else if (roll < 0.19) caste = global::AntHill.Caste.Soldier;
                        else caste = global::AntHill.Caste.Worker;
                    }

                    float x, y;
                    if (caste == global::AntHill.Caste.Queen)
                    {
                        x = nx;
                        y = ny;
                    }
                    else
                    {
                        var angle = (float)(rng.NextDouble() * Math.PI * 2);
                        var dist = (float)(rng.NextDouble() * spawnRadius);
                        x = Math.Clamp(nx + MathF.Cos(angle) * dist, 0f, WorldSize);
                        y = Math.Clamp(ny + MathF.Sin(angle) * dist, 0f, WorldSize);
                    }
                    var headAngle = (float)(rng.NextDouble() * Math.PI * 2);
                    var baseSpeed = 40f + (float)(rng.NextDouble() * 40);
                    var speedMul = 0.8f + (float)(rng.NextDouble() * 0.7f);
                    var finalSpeed = baseSpeed * speedMul;
                    var baseEnergy = 800f + (float)(rng.NextDouble() * 800f);
                    var eatAmount = 1 + rng.Next(3);

                    // Queens + Larvae sit still; AntUpdateTick also gates movement / metabolism by caste.
                    var stationary = caste == global::AntHill.Caste.Queen || caste == global::AntHill.Caste.Larva;

                    var bounds = new WorldBounds
                    {
                        Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }
                    };
                    var vel = new Velocity
                    {
                        X = stationary ? 0f : MathF.Cos(headAngle) * finalSpeed,
                        Y = stationary ? 0f : MathF.Sin(headAngle) * finalSpeed
                    };
                    var genetics = new Genetics
                    {
                        Speed = speedMul,
                        HomeNestX = nx,
                        HomeNestY = ny,
                        BaseEnergy = baseEnergy,
                        EatAmount = eatAmount,
                        HomeNestIndex = nestIdx,
                        ColonyId = (byte)nestIdx,
                        Caste = caste,
                    };
                    var state = new AntState
                    {
                        State = AntState.Foraging,
                        Energy = baseEnergy * (0.5f + (float)rng.NextDouble() * 0.5f),
                        TicksAsLarva = 0,
                    };

                    tx.Spawn<Ant>(
                        Ant.Bounds.Set(in bounds),
                        Ant.Velocity.Set(in vel),
                        Ant.Genetics.Set(in genetics),
                        Ant.State.Set(in state));
                }

                tx.Commit();
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Spider update (Phase 5) — sequential predator pass over 8 flat-array spiders.
    // Each spider wanders by default; in T0 cells it queries for nearby ants and
    // chases / kills the closest. Position is mirrored to _spiderPositions so the
    // Godot-side SpiderRenderer reads it directly each frame.
    // ═══════════════════════════════════════════════════════════════════

    private const float SpiderHuntRange = 800f;
    private const float SpiderHuntRangeSq = SpiderHuntRange * SpiderHuntRange;
    // Kill range matches the visual sphere radius in SpiderRenderer (0.35 m). With SimToWorld = 100/20000 = 0.005,
    // 80 sim units = 0.40 m, slightly larger than the rendered sphere so any ant the player SEES touching the
    // spider gets eaten. The previous 30-sim value (15 cm) was less than half the visual radius — that's why
    // ants appeared to walk through the spider untouched.
    private const float SpiderKillRange = 80f;
    private const float SpiderKillRangeSq = SpiderKillRange * SpiderKillRange;
    private const int SpiderKillsPerTick = 1;         // one kill per tick per spider
    // Real-ant chase range: query ants within this radius, steer toward the nearest one. 1.75 m render
    // (350 sim units) — tuned down from 500 after the ClusterSpatialQuery switch. Hit count grows
    // as r² so 350/500 = 49% fewer candidates per query → ~half the foreach cost. At chase speed
    // 350 sim/s the spider still closes 350 sim in 1 s, perceptually still "I see you from across
    // the path". Drop further (≤ 200) only if foreach becomes hot again.
    private const float SpiderChaseRange = 350f;
    private const float SpiderChaseRangeSq = SpiderChaseRange * SpiderChaseRange;
    private const float SpiderWanderSpeed = 200f;     // ≈ 1 m/s render — visibly moving at any LOD
    private const float SpiderChaseSpeed = 350f;      // outrun ants (40–80 sim/s)
    private const float SpiderTurnJitter = 0.10f;
    // Phase 6 polish — spider HP + soldier melee. Re-tuned after first runtime test showed soldiers
    // couldn't kill spiders because the kill rate (1 kill/tick = 60 ants/sec) vastly outran soldier
    // damage. Now the spider has a per-kill cooldown so soldiers actually get to apply DPS.
    //   Outcomes:
    //     1 soldier vs spider → spider takes 5 HP × 6 s = 30 HP and dies, but eats 6 soldiers (loss).
    //     3 soldiers in melee → 15 HP/s → kill in 2 s; spider eats 2 soldiers (1 survives, win).
    //     5+ soldiers → decisive defensive win.
    private const float SpiderInitialHP         = 30f;
    private const float SoldierDpsPerSoldier    = 5f;
    private const float SoldierMeleeRange       = 80f;            // sim units — matches SpiderKillRange (~0.40 m)
    private const float SoldierMeleeRangeSq     = SoldierMeleeRange * SoldierMeleeRange;
    private const int   SpiderRespawnDelayTicks = 1800;           // 30 sim-seconds @ 60 Hz
    private const int   SpiderKillCooldownTicks = 60;             // 1 kill / sim-sec — gives soldiers time to apply damage before being eaten
    private uint _spiderRng = 0xBADCAFE;
    private int _spiderTickDiagCount;   // bumped each tick; used only to throttle exception logs

    internal void SpiderUpdateTick(TickContext ctx)
    {
        if (_spiderPositions == null) return;
        var dt = ctx.DeltaTime * _timeScale;
        if (dt <= 0f) return;

        _spiderTickDiagCount++;

        // Perf measurement — reset per-tick counters. Aggregated across all 8 spiders below.
        int dbgTier0 = 0, dbgHits = 0, dbgKills = 0;
        long dbgQueryTicks = 0, dbgForeachTicks = 0;

        // One shared transaction for the whole spider tick. Was previously one tx per spider
        // (8 setup/commit pairs per tick); consolidating saves significant overhead at 60 Hz.
        Transaction sharedTx = null;
        try
        {
            for (var i = 0; i < SpiderCount; i++)
            {
                // Phase 6 polish — respawn gate. A killed spider sits off-screen for SpiderRespawnDelayTicks
                // ticks (30 s @ 60 Hz), then respawns at a random edge with full HP. While dead, skip the
                // rest of this spider's tick logic so it doesn't render, chase, or take damage.
                if (_spiderRespawnTicksLeft[i] > 0)
                {
                    _spiderRespawnTicksLeft[i]--;
                    if (_spiderRespawnTicksLeft[i] == 0)
                    {
                        _spiderRng = _spiderRng * 1664525u + 1013904223u;
                        float rx = (_spiderRng >> 8) * (1f / 16777216f);
                        _spiderRng = _spiderRng * 1664525u + 1013904223u;
                        float ry = (_spiderRng >> 8) * (1f / 16777216f);
                        _spiderRng = _spiderRng * 1664525u + 1013904223u;
                        float ra = (_spiderRng >> 8) * (1f / 16777216f);
                        var newX = rx * WorldSize;
                        var newY = ry * WorldSize;
                        var ang = ra * MathF.PI * 2f;
                        _spiderPositions[i] = (newX, newY);
                        _spiderVelocities[i] = (MathF.Cos(ang) * SpiderWanderSpeed, MathF.Sin(ang) * SpiderWanderSpeed);
                        _spiderHealth[i] = SpiderInitialHP;
                        _spiderChasing[i] = false;
                        _spiderChaseTarget[i] = (newX, newY);
                        _spiderTicksSinceKill[i] = int.MaxValue / 2;
                        _eventLog.Enqueue(new LogEntry(_simTimeSec,
                            "Spider respawned",
                            newX * SimToWorld, newY * SimToWorld, LogSeverity.Milestone));
                    }
                    continue;
                }

                var (px0, py0) = _spiderPositions[i];   // start-of-tick position; restored if we kill
                var px = px0;
                var py = py0;
                var (vx, vy) = _spiderVelocities[i];

                // Integrate + bounce
                px += vx * dt;
                py += vy * dt;
                if (px < 0f) { px = -px; vx = -vx; }
                else if (px > WorldSize) { px = 2f * WorldSize - px; vx = -vx; }
                if (py < 0f) { py = -py; vy = -vy; }
                else if (py > WorldSize) { py = 2f * WorldSize - py; vy = -vy; }

                var cx = Math.Clamp((int)(px * InvCellSize), 0, GridCells - 1);
                var cy = Math.Clamp((int)(py * InvCellSize), 0, GridCells - 1);
                int tier = _tierMirror[cy * GridCells + cx];

                bool killedThisTick = false;
                bool chasing = false;
                float targetX = px;
                float targetY = py;

                // ── Chase + kill in the shared transaction ──────────────────────────
                // Query real ants within SpiderChaseRange, read each candidate's WorldBounds (which
                // goes through the component table — fresh, not the spatial index), pick the nearest
                // ant for chase target, and destroy anyone inside the kill bubble. Replaces the
                // pre-fix two-query pattern (chase 1000 + kill 80) because Typhon's small-radius
                // spatial queries used to miss ants near the spider; the engine fix
                // (ArchetypeClusterState.RecomputeDirtyClusterAabbs unconditional refresh) closes
                // that footgun, but the single-query / verified-distance pattern here is still the
                // most CPU-efficient path: one spatial walk, no second index hit, distance verified
                // against MVCC bounds anyway as a defensive measure.
                if (tier == 0)
                {
                    dbgTier0++;
                    try
                    {
                        // Phase 5 → Phase 6 evolution: switched from sharedTx.Query<Ant>().WhereNearby<WorldBounds>(...).Execute()
                        // + per-hit TryOpen+Read(Bounds) to the direct cluster-spatial query. The old path paid ~1.1 µs / hit
                        // on a cache-cold MVCC component-table read; the new path reads each entity's tight AABB inside the
                        // narrowphase and exposes it on the hit struct, so we just consume hit.MinX/MinY without a second read.
                        // Transaction is still needed for Destroy() (and constructs an epoch scope under the hood).
                        sharedTx ??= _dbe.CreateQuickTransaction();
                        var qStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        var sphere = new BSphere2F { CenterX = px, CenterY = py, Radius = SpiderChaseRange };
                        var enumerator = _dbe.ClusterSpatialQuery<Ant>().Radius(in sphere);
                        dbgQueryTicks += System.Diagnostics.Stopwatch.GetTimestamp() - qStart;
                        int killsLeft = SpiderKillsPerTick;
                        int killsDone = 0;
                        int localHits = 0;
                        try
                        {
                            var fStart = System.Diagnostics.Stopwatch.GetTimestamp();
                            float bestD2 = float.MaxValue;
                            float bestX = 0f, bestY = 0f;
                            bool foundTarget = false;
                            while (enumerator.MoveNext())
                            {
                                localHits++;
                                var hit = enumerator.Current;
                                // Bounds come straight from the narrowphase — no second read.
                                float ax = hit.MinX;
                                float ay = hit.MinY;
                                float d2 = hit.DistanceSq;   // already computed against entity's tight AABB
                                if (d2 <= SpiderKillRangeSq && killsLeft > 0
                                    && _spiderTicksSinceKill[i] >= SpiderKillCooldownTicks)
                                {
                                    var antId = EntityId.FromRaw(hit.EntityId);
                                    sharedTx.Destroy(antId);
                                    // Phase 6 polish — Fight alarm phero (3×3 stamp) at the victim
                                    // position so the colony "hears the scream". Drives workers to flee
                                    // and soldiers to converge on the spider's neighbourhood.
                                    PheromoneGrid.DepositArea(_pheromones.Fight, hit.MinX, hit.MinY, FightDeposit);
                                    killsLeft--;
                                    killsDone++;
                                    continue;
                                }
                                if (d2 < bestD2)
                                {
                                    bestD2 = d2;
                                    bestX = ax;
                                    bestY = ay;
                                    foundTarget = true;
                                }
                            }
                            dbgForeachTicks += System.Diagnostics.Stopwatch.GetTimestamp() - fStart;
                            if (foundTarget)
                            {
                                targetX = bestX;
                                targetY = bestY;
                                float dx = bestX - px;
                                float dy = bestY - py;
                                float len = MathF.Max(MathF.Sqrt(dx * dx + dy * dy), 0.001f);
                                vx = dx / len * SpiderChaseSpeed;
                                vy = dy / len * SpiderChaseSpeed;
                                chasing = true;
                            }
                        }
                        finally
                        {
                            enumerator.Dispose();
                        }
                        dbgHits += localHits;

                        if (killsDone > 0)
                        {
                            _spiderTicksSinceKill[i] = 0;
                            killedThisTick = true;
                            dbgKills += killsDone;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_spiderTickDiagCount < 5) Console.WriteLine($"Spider update threw: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (!chasing)
                {
                    // Wander — no prey in range, or non-T0 cell.
                    var heading = MathF.Atan2(vy, vx);
                    _spiderRng = _spiderRng * 1664525u + 1013904223u;
                    var jitter = ((int)(_spiderRng >> 16) & 0xFFFF) / 65536f * 2f - 1f;
                    heading += jitter * SpiderTurnJitter;
                    vx = MathF.Cos(heading) * SpiderWanderSpeed;
                    vy = MathF.Sin(heading) * SpiderWanderSpeed;
                }

                // Phase 6 polish — soldier melee damage. Iterate the per-tick soldier-position cache
                // (filled by AntUpdateTick this same tick); count those within SoldierMeleeRange and
                // subtract SoldierDpsPerSoldier × count × dt from the spider's HP. On HP ≤ 0 the
                // spider goes off-screen for SpiderRespawnDelayTicks ticks then respawns at a random
                // edge (handled at the top of the next iteration's loop body).
                int meleeSoldiers = 0;
                {
                    var sCount = _soldierCount;
                    if (sCount > _soldierPositions.Length) sCount = _soldierPositions.Length;
                    for (int s = 0; s < sCount; s++)
                    {
                        var (sx, sy) = _soldierPositions[s];
                        float ddx = sx - px;
                        float ddy = sy - py;
                        if (ddx * ddx + ddy * ddy <= SoldierMeleeRangeSq) meleeSoldiers++;
                    }
                }
                if (meleeSoldiers > 0)
                {
                    _spiderHealth[i] -= SoldierDpsPerSoldier * dt * meleeSoldiers;
                    if (_spiderHealth[i] <= 0f)
                    {
                        _eventLog.Enqueue(new LogEntry(_simTimeSec,
                            $"Spider killed by {meleeSoldiers} soldier{(meleeSoldiers == 1 ? "" : "s")}",
                            px * SimToWorld, py * SimToWorld, LogSeverity.Action));
                        _spiderRespawnTicksLeft[i] = SpiderRespawnDelayTicks;
                        _spiderHealth[i] = 0f;
                        _spiderChasing[i] = false;
                        // Park off-screen so the renderer skips it for the respawn window.
                        _spiderPositions[i] = (-1000f, -1000f);
                        _spiderVelocities[i] = (0f, 0f);
                        continue;   // skip the position/state write below — already parked
                    }
                }

                // Kill-pause: spider stops on the frame it eats so it visibly lingers on the prey.
                if (killedThisTick)
                {
                    px = px0;
                    py = py0;
                }

                _spiderPositions[i] = (px, py);
                _spiderVelocities[i] = (vx, vy);
                _spiderChasing[i] = chasing;
                _spiderChaseTarget[i] = (targetX, targetY);
                _spiderTicksSinceKill[i]++;
            }
        }
        finally
        {
            long commitTicks = 0;
            if (sharedTx != null)
            {
                var cStart = System.Diagnostics.Stopwatch.GetTimestamp();
                try { sharedTx.Commit(); } catch { /* swallow — diagnostic catch above already logged */ }
                sharedTx.Dispose();
                commitTicks = System.Diagnostics.Stopwatch.GetTimestamp() - cStart;
            }

            // Publish per-tick measurement counters for the HUD readout.
            _spiderTier0Count   = dbgTier0;
            _spiderTotalHits    = dbgHits;
            _spiderKills        = dbgKills;
            _spiderQueryTicks   = dbgQueryTicks;
            _spiderForeachTicks = dbgForeachTicks;
            _spiderCommitTicks  = commitTicks;
        }
    }

    private void SpawnSpiders()
    {
        var rng = new Random(777);
        _spiderPositions = new (float, float)[SpiderCount];
        _spiderVelocities = new (float, float)[SpiderCount];
        _spiderTicksSinceKill = new int[SpiderCount];
        _spiderChasing = new bool[SpiderCount];
        _spiderChaseTarget = new (float, float)[SpiderCount];
        _spiderHealth = new float[SpiderCount];
        _spiderRespawnTicksLeft = new int[SpiderCount];
        for (var i = 0; i < SpiderCount; i++)
        {
            var x = (float)(rng.NextDouble() * WorldSize);
            var y = (float)(rng.NextDouble() * WorldSize);
            var angle = (float)(rng.NextDouble() * Math.PI * 2);
            _spiderPositions[i] = (x, y);
            _spiderVelocities[i] = (MathF.Cos(angle) * SpiderWanderSpeed, MathF.Sin(angle) * SpiderWanderSpeed);
            _spiderTicksSinceKill[i] = int.MaxValue / 2;   // start black (no recent kill)
            _spiderChaseTarget[i] = (x, y);                 // default = self (no line)
            _spiderHealth[i] = SpiderInitialHP;
            _spiderRespawnTicksLeft[i] = 0;
        }
    }

    private void SpawnFood()
    {
        var rng = new Random(123);
        // Reserve headroom so the first dozen tool-placed foods don't trigger a resize.
        _foodCache = new (float, float, float)[Math.Max(FoodCount * 4, 64)];
        _foodRemainingInt = new int[_foodCache.Length];
        using var tx = _dbe.CreateQuickTransaction();
        for (var i = 0; i < FoodCount; i++)
        {
            var x = (float)(rng.NextDouble() * WorldSize);
            var y = (float)(rng.NextDouble() * WorldSize);
            var remaining = 5000f + (float)(rng.NextDouble() * 15000f);
            _foodCache[i] = (x, y, remaining);
            _foodRemainingInt[i] = (int)remaining;
            var source = new FoodSource
            {
                Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y },
                RemainingFood = remaining
            };
            tx.Spawn<Food>(Food.Source.Set(in source));
        }
        _foodCount = FoodCount;
        tx.Commit();
        BuildFoodGrid();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Phase 4: runtime sim mutations (called from ToolCommandSystem).
    // All four methods run on the worker that owns the ToolCommandSystem
    // tick at Phase.Input, which is the only place a transaction is opened
    // outside of init. AntUpdateSystem (Phase.Simulation) only runs after
    // this returns, so array growth + food-grid rebuild is safe.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Spawn one food source at the given sim-space position. Grows <c>_foodCache</c>
    /// + <c>_foodRemainingInt</c> if needed; the caller is responsible for invoking
    /// <see cref="RebuildFoodGrid"/> after a batch of placements (once per tick, not per spawn).
    /// Returns the new food's index.</summary>
    internal int RuntimeSpawnFood(Transaction tx, float x, float y, float amount)
    {
        var idx = _foodCount;
        if (idx >= _foodCache.Length)
        {
            var newCap = Math.Max(16, _foodCache.Length * 2);
            Array.Resize(ref _foodCache, newCap);
            Array.Resize(ref _foodRemainingInt, newCap);
        }
        _foodCache[idx] = (x, y, amount);
        _foodRemainingInt[idx] = (int)amount;
        _foodCount = idx + 1;

        var source = new FoodSource
        {
            Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y },
            RemainingFood = amount,
        };
        tx.Spawn<Food>(Food.Source.Set(in source));
        return idx;
    }

    /// <summary>Spawn one rock at the given sim-space position. Tracks position in
    /// <c>_rockPositions</c> so the Godot-side RockRenderer can render it.</summary>
    internal void RuntimeSpawnRock(Transaction tx, float x, float y)
    {
        var idx = _rockCount;
        if (idx >= _rockPositions.Length)
        {
            Array.Resize(ref _rockPositions, _rockPositions.Length * 2);
        }
        _rockPositions[idx] = (x, y);
        _rockCount = idx + 1;

        var obs = new Obstacle
        {
            Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y },
            Kind = 0,
        };
        tx.Spawn<Rock>(Rock.Info.Set(in obs));
    }

    /// <summary>Kill all ants within <paramref name="radius"/> sim units of <c>(x, y)</c>.
    /// Returns the number of ants destroyed.</summary>
    internal int RuntimeCullAnts(Transaction tx, float x, float y, float radius)
    {
        var hits = tx.Query<Ant>().WhereNearby<WorldBounds>(x, y, 0, radius).Execute();
        if (hits.Count == 0) return 0;
        // DestroyBatch wants a span — copy the HashSet to a pooled buffer.
        var ids = new EntityId[hits.Count];
        var i = 0;
        foreach (var id in hits) ids[i++] = id;
        tx.DestroyBatch(ids);
        return ids.Length;
    }

    /// <summary>Tracks live food count under the <c>_foodCache</c> watermark. Set by
    /// init (<see cref="SpawnFood"/>) and grown by <see cref="RuntimeSpawnFood"/>.
    /// AntUpdateTick + PrepareRender iterate <c>0..</c>this.</summary>
    internal int _foodCount;

    /// <summary>Public wrapper so <c>ToolCommandSystem</c> can rebuild the food spatial
    /// grid after a batch of <see cref="RuntimeSpawnFood"/> calls within a tick.</summary>
    internal void RebuildFoodGridPublic() => BuildFoodGrid();

    private void BuildFoodGrid()
    {
        // Bucket each food source into all cells whose area overlaps the smell range
        var lists = new System.Collections.Generic.List<int>[FoodGridCells * FoodGridCells];
        var smellCells = (int)MathF.Ceiling(FoodSmellRange * FoodGridInvCellSize); // cells radius

        for (var fi = 0; fi < _foodCount; fi++)
        {
            if (_foodRemainingInt[fi] <= 0) continue;   // depleted sources: skip — ants won't find them anyway
            var (fx, fy, _) = _foodCache[fi];
            var cx = Math.Clamp((int)(fx * FoodGridInvCellSize), 0, FoodGridCells - 1);
            var cy = Math.Clamp((int)(fy * FoodGridInvCellSize), 0, FoodGridCells - 1);

            var minCx = Math.Max(0, cx - smellCells);
            var maxCx = Math.Min(FoodGridCells - 1, cx + smellCells);
            var minCy = Math.Max(0, cy - smellCells);
            var maxCy = Math.Min(FoodGridCells - 1, cy + smellCells);

            for (var gy = minCy; gy <= maxCy; gy++)
            {
                for (var gx = minCx; gx <= maxCx; gx++)
                {
                    var gi = gy * FoodGridCells + gx;
                    lists[gi] ??= new System.Collections.Generic.List<int>();
                    lists[gi].Add(fi);
                }
            }
        }

        _foodGrid = new int[FoodGridCells * FoodGridCells][];
        for (var i = 0; i < lists.Length; i++)
        {
            _foodGrid[i] = lists[i]?.ToArray();
        }
    }

    private void SpawnNests()
    {
        _nestPositions = new (float, float)[]
        {
            (5000f, 5000f), (15000f, 5000f), (10000f, 10000f), (5000f, 15000f), (15000f, 15000f)
        };
        _nestFoodStock = new int[NestCount];
        for (var i = 0; i < NestCount; i++)
        {
            _nestFoodStock[i] = InitialNestFood;
        }

        using var tx = _dbe.CreateQuickTransaction();
        for (var ni = 0; ni < _nestPositions.Length; ni++)
        {
            var (nx, ny) = _nestPositions[ni];
            var info = new NestInfo
            {
                Bounds = new AABB2F { MinX = nx, MinY = ny, MaxX = nx, MaxY = ny },
                FoodStored = 0f,
                Population = AntCount / NestCount,
                ColonyId = (byte)ni,
            };
            tx.Spawn<Nest>(Nest.Info.Set(in info));
        }
        tx.Commit();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════════

    public void Start()
    {
        _runtime.Start();
        StartHangWatchdog();
    }

    /// <summary>
    /// Polls <see cref="TyphonRuntime.CurrentTickNumber"/> from a background task. If the tick
    /// counter doesn't advance for {HangThresholdSeconds}s the watchdog dumps the scheduler's
    /// per-system state to stdout — predecessor count, ready flag, chunk progress, skip
    /// reason — and stops. Designed for the 1st-tick-never-completes case where a CPU spin in
    /// the worker pool prevents the engine from making progress; without this dump we'd have
    /// no signal at all about which system is wedged.
    /// </summary>
    private void StartHangWatchdog()
    {
        const int hangThresholdSeconds = 5;
        const int pollIntervalMs = 1_000;
        System.Threading.Tasks.Task.Run(async () =>
        {
            var lastTick = -1L;
            var stuckSince = DateTime.UtcNow;
            while (true)
            {
                await System.Threading.Tasks.Task.Delay(pollIntervalMs).ConfigureAwait(false);
                var rt = _runtime;
                if (rt == null) return;
                var current = rt.CurrentTickNumber;
                if (current != lastTick)
                {
                    lastTick = current;
                    stuckSince = DateTime.UtcNow;
                    continue;
                }
                if ((DateTime.UtcNow - stuckSince).TotalSeconds < hangThresholdSeconds) continue;
                Console.WriteLine($"[hang-watchdog] tick {current} stalled for >{hangThresholdSeconds}s — dumping scheduler state");
                Console.WriteLine(rt.Scheduler.DumpHangDiagnostic());
                return;
            }
        });
    }

    public void SetHeatmapEnabled(bool enabled) => _heatmapEnabled = enabled;
    public TickTelemetryRing Telemetry => _runtime?.Telemetry;
    public SystemDefinition[] Systems => _runtime?.Systems;
    public string[] PhaseNames => _runtime?.PhaseNames ?? [];

    /// <summary>
    /// Active <see cref="DatabaseEngine"/>. Exposed so <see cref="AntHill.ProfilerSetup"/> can build the v7
    /// static-structure tables (component definitions, archetype definitions, index catalog) into the trace
    /// file via <see cref="Typhon.Engine.Profiler.ProfilerStaticDataBuilder"/>. Returns null before <see cref="Initialize"/>.
    /// </summary>
    public DatabaseEngine DatabaseEngine => _dbe;

    /// <summary>
    /// Parent resource under which the engine's resource graph hangs. Used by <see cref="AntHill.ProfilerSetup"/>
    /// to build the <see cref="Typhon.Profiler.ResourceGraphNodeRecord"/> snapshot. Same handle the profiler exporters
    /// use; aliasing it here lets the static-data builder walk the tree without needing DI.
    /// </summary>
    public IResource ResourceGraphRoot => _dbe;

    /// <summary>
    /// Active <see cref="TyphonRuntime"/>. Exposed for <see cref="AntHill.ProfilerSetup"/> to walk
    /// <see cref="Typhon.Engine.Runtime.DagScheduler.EventQueues"/> when building the v7 event-queue catalog. Null
    /// before <see cref="Start"/>.
    /// </summary>
    public TyphonRuntime ActiveRuntime => _runtime;

    // Parent resource under which profiler exporters (FileExporter / TcpExporter) must be created.
    // Available only after Initialize() has built the service provider.
    public IResource ProfilerParent => _serviceProvider?.GetRequiredService<IResourceRegistry>().Profiler;

    // Current scheduler tick. Passed as a Func<long> provider into ProfilerSessionMetadata so TcpExporter can stamp the Init frame with the
    // live engine-tick at every client connect — lets the viewer display absolute tick numbers across reconnects. Returns 0 before bridge.Start.
    public long CurrentTick => _runtime?.CurrentTickNumber ?? 0;

    public string GetTimingInfo()
    {
        var telemetry = _runtime?.Telemetry;
        if (telemetry == null || telemetry.TotalTicksRecorded == 0)
        {
            return "no ticks";
        }

        try
        {
            var tickNum = telemetry.NewestTick;
            ref readonly var tick = ref telemetry.GetTick(tickNum);
            var systems = telemetry.GetSystemMetrics(tickNum);
            var sysDefs = _runtime.Systems;

            var parts = new System.Text.StringBuilder();
            parts.Append($"Tick: {tick.ActualDurationMs:F1}ms");

            for (var i = 0; i < systems.Length && i < sysDefs.Length; i++)
            {
                ref readonly var s = ref systems[i];
                if (s.WasSkipped)
                {
                    continue;
                }

                parts.Append($"\n  {sysDefs[i].Name}: {s.DurationUs:F0}us");
            }

            return parts.ToString();
        }
        catch
        {
            return "telemetry error";
        }
    }

    public void Dispose()
    {
        try { _runtime?.Shutdown(); }
        catch
        {
            // ignored
        }

        try { _runtime?.Dispose(); }
        catch
        {
            // ignored
        }

        try { _antView?.Dispose(); }
        catch
        {
            // ignored
        }

        try { _serviceProvider?.Dispose(); }
        catch
        {
            // ignored
        }
    }
}
