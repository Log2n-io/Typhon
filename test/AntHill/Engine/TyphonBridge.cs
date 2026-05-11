using System;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace AntHill;

public sealed class TyphonBridge : IDisposable
{
    public const int AntCount = 200_000;
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
            for (var i = 0; i < _foodRemainingInt.Length; i++)
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
                opt.Wal = new WalWriterOptions();
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        _scope = _serviceProvider.CreateScope();
        _dbe = _scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        Archetype<Ant>.Touch();
        Archetype<Food>.Touch();
        Archetype<Nest>.Touch();
        _dbe.RegisterComponentFromAccessor<WorldBounds>();
        _dbe.RegisterComponentFromAccessor<Velocity>();
        _dbe.RegisterComponentFromAccessor<Genetics>();
        _dbe.RegisterComponentFromAccessor<AntState>();
        _dbe.RegisterComponentFromAccessor<FoodSource>();
        _dbe.RegisterComponentFromAccessor<NestInfo>();

        _dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: Vector2.Zero,
            worldMax: new Vector2(WorldSize, WorldSize),
            cellSize: CellSize,
            migrationHysteresisRatio: 0.05f));

        _dbe.InitializeArchetypes();

        SpawnNests();
        SpawnFood();
        SpawnAnts();

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
                AntPhases.Movement,
                AntPhases.Lifecycle,
                AntPhases.Sense,
                AntPhases.Brain,
                AntPhases.Trail,
                AntPhases.Render,
            ],
            DefaultPhase = AntPhases.Render,
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
        schedule.Add(new TierAssignmentSystem(this));

        // Movement phase
        schedule.Add(new MoveAllSystem(this));

        // Lifecycle phase — tier chain (W×W on AntState/WorldBounds/Velocity/NestInventory)
        schedule.Add(new MetabolismT0System(this));
        schedule.Add(new MetabolismT1System(this));
        schedule.Add(new MetabolismT2System(this));
        schedule.Add(new MetabolismT3System(this));

        // Sense phase
        schedule.Add(new FoodDetectSystem(this));

        // Brain phase — tier chain (W×W on Velocity)
        schedule.Add(new BrainT0System(this));
        schedule.Add(new BrainT1System(this));
        schedule.Add(new BrainT2System(this));
        schedule.Add(new BrainT3System(this));

        // Trail phase — tier chain on PheromoneGrid + decay sweep
        schedule.Add(new PheroDepT0System(this));
        schedule.Add(new PheroDepT1System(this));
        schedule.Add(new PheroDepT2System(this));
        schedule.Add(new PheroDepT3System(this));
        schedule.Add(new PheroDecaySystem(this));

        // Render phase — stats sink consumes the three event queues, prepare/fill/publish chain
        schedule.Add(new AntStatsAggregatorSystem(this));
        schedule.Add(new PrepareRenderBufferSystem(this));
        schedule.Add(new FillRenderBufferSystem(this));
        schedule.Add(new PublishRenderFrameSystem(this));
    }

    // ═══════════════════════════════════════════════════════════════════
    // TierAssignment
    // ═══════════════════════════════════════════════════════════════════

    internal void TierAssignment(TickContext ctx)
    {
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
    }

    // ═══════════════════════════════════════════════════════════════════
    // MoveAll — position update from velocity, every frame, all ants
    // ═══════════════════════════════════════════════════════════════════

    private const int AntArchetypeId = 100;

    internal void MoveAllAnts(TickContext ctx)
    {
        var dt = ctx.DeltaTime * _timeScale;
        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Ant>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Ant>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            var bounds = cluster.GetSpan(Ant.Bounds);
            var velocities = cluster.GetSpan(Ant.Velocity);
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref var pos = ref bounds[idx];
                ref var vel = ref velocities[idx];

                var x = pos.Bounds.MinX + vel.X * dt;
                var y = pos.Bounds.MinY + vel.Y * dt;
                var vx = vel.X;
                var vy = vel.Y;

                if (x < 0f) { x = -x; vx = -vx; }
                else if (x > WorldSize) { x = 2f * WorldSize - x; vx = -vx; }
                if (y < 0f) { y = -y; vy = -vy; }
                else if (y > WorldSize) { y = 2f * WorldSize - y; vy = -vy; }

                pos.Bounds.MinX = x;
                pos.Bounds.MaxX = x;
                pos.Bounds.MinY = y;
                pos.Bounds.MaxY = y;
                vel.X = vx;
                vel.Y = vy;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Metabolism — energy decay + death/respawn (all tiers)
    // Reads: AntState, Genetics, Position  |  Writes: AntState, Position (on respawn)
    // ═══════════════════════════════════════════════════════════════════

    private const float BaseDt = 1f / 60f;
    private const float EnergyDrainRate = 0.15f;

    internal void MetabolismTick(TickContext ctx)
    {
        var dtScale = ctx.AmortizedDeltaTime / BaseDt * _timeScale;
        var nests = _nestPositions;
        var nestStock = _nestFoodStock;
        var deathQueue = _antDiedQueue;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Ant>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Ant>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            var bounds = cluster.GetSpan(Ant.Bounds);
            var velocities = cluster.GetSpan(Ant.Velocity);
            var states = cluster.GetSpan(Ant.State);
            var genetics = cluster.GetReadOnlySpan(Ant.Genetics);
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref var state = ref states[idx];
                ref readonly var gen = ref genetics[idx];

                state.Energy -= EnergyDrainRate * dtScale;

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

                    // Teleport to nest + random heading immediately (same pass, no heuristic)
                    ref var pos = ref bounds[idx];
                    ref var vel = ref velocities[idx];
                    pos.X = nests[ni].x;
                    pos.Y = nests[ni].y;

                    var h = (uint)(idx * 2654435761 + cluster.ChunkId * 40503 + ctx.TickNumber);
                    var angle = (h % 6283u) * 0.001f; // 0 to ~2π
                    var speed = gen.Speed * 40f;
                    vel.X = MathF.Cos(angle) * speed;
                    vel.Y = MathF.Sin(angle) * speed;
                    _dbe.MarkClusterSlotDirty(AntArchetypeId, cluster.ChunkId, idx);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // FoodDetect — food smell/pickup + nest drop. Every tick, all ants.
    // Reads: Position, AntState, Genetics  |  Writes: Position (heading), AntState (state)
    // ═══════════════════════════════════════════════════════════════════

    private const float FoodPickupRange = 30f;
    private const float FoodSmellRange = 250f;
    private const float NestDropRange = 40f;
    private const float FoodPickupRangeSq = FoodPickupRange * FoodPickupRange;
    private const float FoodSmellRangeSq = FoodSmellRange * FoodSmellRange;
    private const float NestDropRangeSq = NestDropRange * NestDropRange;

    internal void FoodDetectTick(TickContext ctx)
    {
        var food = _foodCache;
        var foodRemaining = _foodRemainingInt;
        var foodGrid = _foodGrid;
        var nests = _nestPositions;
        var nestStock = _nestFoodStock;
        var pickedUpQueue = _foodPickedUpQueue;
        var deliveredQueue = _foodDeliveredQueue;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Ant>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Ant>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            var bounds = cluster.GetReadOnlySpan(Ant.Bounds);
            var velocities = cluster.GetSpan(Ant.Velocity);
            var states = cluster.GetSpan(Ant.State);
            var genetics = cluster.GetReadOnlySpan(Ant.Genetics);
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref readonly var pos = ref bounds[idx];
                ref var vel = ref velocities[idx];
                ref var state = ref states[idx];

                if (state.Energy <= 0f) continue;

                if (state.State == AntState.Foraging)
                {
                    // Grid lookup: only check food sources in this cell
                    var gcx = Math.Clamp((int)(pos.X * FoodGridInvCellSize), 0, FoodGridCells - 1);
                    var gcy = Math.Clamp((int)(pos.Y * FoodGridInvCellSize), 0, FoodGridCells - 1);
                    var candidates = foodGrid[gcy * FoodGridCells + gcx];
                    if (candidates == null) continue;

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
                            if (Interlocked.Decrement(ref foodRemaining[fi]) >= 0)
                            {
                                state.State = AntState.ReturningFrom(fi);
                                state.Energy = genetics[idx].BaseEnergy;
                                vel.X = -vel.X;
                                vel.Y = -vel.Y;
                                pickedUpQueue?.Push(new FoodPickedUpEvent((uint)((cluster.ChunkId << 8) | idx), fi));
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
                        ref readonly var gen = ref genetics[idx];
                        if (speed < 0.01f) speed = gen.Speed * 40f;
                        vel.X = MathF.Cos(heading) * speed;
                        vel.Y = MathF.Sin(heading) * speed;
                    }
                }
                else // Returning
                {
                    ref readonly var gen = ref genetics[idx];
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
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // AntBrain — pheromone sensing + steering + wander (tier-gated)
    // Reads: Position, AntState, Genetics + pheromone grid
    // Writes: Position (velocity)
    // ═══════════════════════════════════════════════════════════════════

    private const float SensorDistance = 40f;
    private const float SensorAngle = 0.52f;   // ~30 degrees
    private const float SteerStrength = 0.3f;   // radians per tick toward best sensor
    private const float WanderJitter = 0.03f;    // tiny per-tick jitter (±1.7°)
    private const int WanderChangeTicks = 90;     // change direction every ~1.5s
    private const float WanderTurnMax = 0.8f;     // max turn on direction change (~45°)

    internal void AntBrainTick(TickContext ctx)
    {
        var phero = _pheromones;
        var nests = _nestPositions;
        var tick = ctx.TickNumber;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Ant>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Ant>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            var bounds = cluster.GetReadOnlySpan(Ant.Bounds);
            var velocities = cluster.GetSpan(Ant.Velocity);
            var states = cluster.GetReadOnlySpan(Ant.State);
            var genetics = cluster.GetReadOnlySpan(Ant.Genetics);
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref readonly var pos = ref bounds[idx];
                ref var vel = ref velocities[idx];
                ref readonly var state = ref states[idx];
                ref readonly var gen = ref genetics[idx];

                if (state.Energy <= 0f) continue;

                var speed = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y);
                if (speed < 0.01f) speed = gen.Speed * 40f;
                var heading = MathF.Atan2(vel.Y, vel.X);

                var steered = false;

                if (state.State == AntState.Foraging)
                {
                    // Pheromone trail following (food trail)
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
                else // Returning — pheromone + nest direction validation
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

                    // Validate: does pheromone heading take us closer to nest?
                    var dot = MathF.Cos(pheroHeading) * toNestX + MathF.Sin(pheroHeading) * toNestY;
                    heading = dot > 0f ? pheroHeading : nestHeading;
                    steered = true;
                }

                // Wander: tiny jitter + periodic direction change
                if (!steered)
                {
                    var h = (uint)(idx * 2654435761 + cluster.ChunkId * 40503);

                    var jitter = ((h + (uint)tick * 2246822519u) % 1000u / 1000f - 0.5f) * 2f * WanderJitter;
                    heading += jitter;

                    var epoch = (uint)(tick / WanderChangeTicks);
                    var prevEpoch = (uint)((tick - (long)ctx.AmortizedDeltaTime * 60) / WanderChangeTicks);
                    if (epoch != prevEpoch)
                    {
                        var turn = ((h * 48271u + epoch * 16807u) % 1000u / 1000f - 0.5f) * 2f * WanderTurnMax;
                        heading += turn;
                    }
                }

                vel.X = MathF.Cos(heading) * speed;
                vel.Y = MathF.Sin(heading) * speed;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // PheromoneDeposit — deposit trail markers (T0/T1 only)
    // Reads: Position, AntState, Genetics  |  Writes: pheromone grid
    // ═══════════════════════════════════════════════════════════════════

    private const float BaseDeposit = 5f;
    private const float NearFoodMultiplier = 10f;  // deposit more food-pheromone near food
    private const float DepositFalloffRange = 200f;
    private const float DepositFalloffRangeSq = DepositFalloffRange * DepositFalloffRange;

    internal void PheromoneDepositTick(TickContext ctx)
    {
        var phero = _pheromones;
        var food = _foodCache;
        var nests = _nestPositions;

        // Scale deposit by amortization so all tiers produce the same pheromone per second
        var amortScale = ctx.AmortizedDeltaTime / BaseDt;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Ant>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Ant>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            var bounds = cluster.GetReadOnlySpan(Ant.Bounds);
            var states = cluster.GetReadOnlySpan(Ant.State);
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ref readonly var pos = ref bounds[idx];
                ref readonly var state = ref states[idx];

                if (state.Energy <= 0f) continue;

                var cellIdx = PheromoneGrid.WorldToIndex(pos.X, pos.Y);

                if (state.State == AntState.Foraging)
                {
                    // Foraging ants leave only a faint home trail — strong trails come from successful food runs
                    PheromoneGrid.Deposit(phero.Home, cellIdx, BaseDeposit * 0.1f * amortScale);
                }
                else
                {
                    // Returning: deposit food pheromone, stronger near the food source this ant came from
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
                    PheromoneGrid.Deposit(phero.Food, cellIdx, deposit * amortScale);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // PheromoneDecay — evaporate grid (callback, every tick)
    // ═══════════════════════════════════════════════════════════════════

    private const float PheroDecayFactor = 0.995f;

    internal void PheromoneDecayTick(TickContext ctx)
    {
        _pheromones.Evaporate(PheroDecayFactor);
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
        _overlayBuffer.EnsureCapacity(FoodCount + NestCount);
        var oBuf = _overlayBuffer.Data;
        var oi = 0;

        for (var fi = 0; fi < _foodCache.Length; fi++)
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

        // Downsample pheromone grid only when overlay is visible
        if (_heatmapEnabled)
        {
            const int hs = RenderFrame.HeatmapSize; // 200
            const int gs = PheromoneGrid.GridSize;   // 1000
            var foodSrc = _pheromones.Food;
            var homeSrc = _pheromones.Home;
            var maxF = _heatMaxFood;
            var maxH = _heatMaxHome;

            // Linear scan over source arrays — fully sequential reads, cache-friendly
            for (var sy = 0; sy < gs; sy++)
            {
                var hy = sy / 5;
                var srcRow = sy * gs;
                var hiRow = hy * hs;
                for (var sx = 0; sx < gs; sx++)
                {
                    var hi = hiRow + sx / 5;
                    var si = srcRow + sx;
                    var f = foodSrc[si];
                    var h = homeSrc[si];
                    if (f > maxF[hi]) maxF[hi] = f;
                    if (h > maxH[hi]) maxH[hi] = h;
                }
            }

            // Convert to RGBA + reset accumulators in one pass
            var invMax = 255f / PheromoneGrid.MaxPheromone;
            var rgba = _heatmapRGBA;
            for (var i = 0; i < HeatmapPixels; i++)
            {
                var gv = (byte)Math.Min(maxF[i] * invMax, 255f);
                var bv = (byte)Math.Min(maxH[i] * invMax, 255f);
                var p = i * 4;
                rgba[p + 0] = 0;
                rgba[p + 1] = gv;
                rgba[p + 2] = bv;
                rgba[p + 3] = Math.Max(gv, bv);
                maxF[i] = 0f;
                maxH[i] = 0f;
            }
        }
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
                var alpha = 0.15f + energyRatio * 0.70f;

                float r = 1f, g = 0.3f, b = 0.3f;
                if (state.IsReturning) { r = 0.3f; g = 1f; b = 0.3f; }

                var off = writeIdx * Stride;
                buf[off + 0] = 1f;  buf[off + 1] = 0f; buf[off + 2] = 0f; buf[off + 3] = pos.X;
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
        };

        _renderBridge.Publish(frame);

        // Swap heatmap buffer
        (_heatmapRGBA, _heatmapRGBARead) = (_heatmapRGBARead, _heatmapRGBA);

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

            while (remaining > 0)
            {
                var count = Math.Min(batchSize, remaining);
                remaining -= count;
                using var tx = _dbe.CreateQuickTransaction();

                for (var i = 0; i < count; i++)
                {
                    var angle = (float)(rng.NextDouble() * Math.PI * 2);
                    var dist = (float)(rng.NextDouble() * spawnRadius);
                    var x = Math.Clamp(nx + MathF.Cos(angle) * dist, 0f, WorldSize);
                    var y = Math.Clamp(ny + MathF.Sin(angle) * dist, 0f, WorldSize);
                    var headAngle = (float)(rng.NextDouble() * Math.PI * 2);
                    var baseSpeed = 40f + (float)(rng.NextDouble() * 40);
                    var speedMul = 0.8f + (float)(rng.NextDouble() * 0.7f);
                    var finalSpeed = baseSpeed * speedMul; // baked into velocity
                    var baseEnergy = 800f + (float)(rng.NextDouble() * 800f);
                    var eatAmount = 1 + rng.Next(3);

                    var bounds = new WorldBounds
                    {
                        Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }
                    };
                    var vel = new Velocity
                    {
                        X = MathF.Cos(headAngle) * finalSpeed,
                        Y = MathF.Sin(headAngle) * finalSpeed
                    };
                    var genetics = new Genetics
                    {
                        Speed = speedMul,
                        HomeNestX = nx,
                        HomeNestY = ny,
                        BaseEnergy = baseEnergy,
                        EatAmount = eatAmount,
                        HomeNestIndex = nestIdx
                    };
                    var state = new AntState
                    {
                        State = AntState.Foraging,
                        Energy = baseEnergy * (0.5f + (float)rng.NextDouble() * 0.5f)
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

    private void SpawnFood()
    {
        var rng = new Random(123);
        _foodCache = new (float, float, float)[FoodCount];
        _foodRemainingInt = new int[FoodCount];
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
        tx.Commit();
        BuildFoodGrid();
    }

    private void BuildFoodGrid()
    {
        // Bucket each food source into all cells whose area overlaps the smell range
        var lists = new System.Collections.Generic.List<int>[FoodGridCells * FoodGridCells];
        var smellCells = (int)MathF.Ceiling(FoodSmellRange * FoodGridInvCellSize); // cells radius

        for (var fi = 0; fi < _foodCache.Length; fi++)
        {
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
        foreach (var (nx, ny) in _nestPositions)
        {
            var info = new NestInfo
            {
                Bounds = new AABB2F { MinX = nx, MinY = ny, MaxX = nx, MaxY = ny },
                FoodStored = 0f,
                Population = AntCount / NestCount
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
