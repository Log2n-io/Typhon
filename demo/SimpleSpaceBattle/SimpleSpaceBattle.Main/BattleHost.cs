using Typhon.Engine;

namespace SimpleSpaceBattle;

/// <summary>
/// Opens the database, configures the grid, spawns the fleet, builds the schedule and runs the tick loop.
/// Deliberately small — the interesting content is in the four systems, not here.
/// </summary>
internal sealed class BattleHost : IDisposable
{
    private readonly DatabaseEngine _dbe;
    private readonly BattleWorld _world;
    private bool _disposed;
    private bool _stopped;

    private BattleHost(DatabaseEngine dbe, BattleWorld world)
    {
        _dbe = dbe;
        _world = world;
    }

    public BattleWorld World => _world;

    public TimeSpan SpawnDuration { get; private set; }

    public static BattleHost Create(SimulationConfig config, string databaseLocation, int workerCount)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseLocation);
        config.Validate();

        DatabaseEngine dbe = DatabaseEngine.Open(databaseLocation, options => options
            .Register<HullComponent>()
            .Register<MotionComponent>()
            .Register<VitalsComponent>()
            .Register<TargetingComponent>()
            .ConfigureSpatialGrid(new SpatialGridConfig(
                System.Numerics.Vector2.Zero,
                new System.Numerics.Vector2(config.WorldX, config.WorldY),
                config.CellSize)));

        var world = new BattleWorld(dbe, config, workerCount);
        var host = new BattleHost(dbe, world);

        // NOTE: SetSpatialBarrierOnly<Ship>() is deliberately NOT called. Movement writes Hull through GetSpan
        // because WriteSpatial rejects AABB3F; opting into the barrier-only path would make those writes invisible
        // to the fence's spatial maintenance and silently freeze the index (ArchetypeClusterState.cs:843-845).

        host.SpawnDuration = world.SpawnFleet();
        world.SizeTargetLane();
        world.ReattachAccessor();
        world.RefreshClusterCount();

        // NOTE: no EcsView is created. The three parallel systems are ChunkedParallel CallbackSystems that iterate
        // clusters directly, so they need no entity input — which also skips the runtime's per-tick pull-mode view
        // refresh, measured at 413 ms/tick at this fleet size (issue #797, SSB_VIEWBENCH=1).

        world.Runtime = TyphonRuntime.Create(dbe, host.BuildSchedule, new RuntimeOptions
        {
            BaseTickRate = config.TickRate,
            WorkerCount = workerCount,
            EnableParallelFence = true,
        });

        return host;
    }

    /// <summary>
    /// One DAG, four phases in a forced order (see <see cref="BattlePhases"/>). Three of the five systems are
    /// parallel and alone in their phase, which is the entire performance story: the DAG is a straight line and the
    /// width is inside each node, not across them.
    /// </summary>
    private void BuildSchedule(RuntimeSchedule schedule)
    {
        Dag dag = schedule.PublicTrack.DeclareDag("SimpleSpaceBattle")
            .Phases(BattlePhases.Acquire, BattlePhases.Fire, BattlePhases.Move, BattlePhases.Reap)
            .DefaultPhase(BattlePhases.Reap);

        dag.Add(new TargetingSystem(_world));
        dag.Add(new ResolutionSystem(_world));
        dag.Add(new MovementSystem(_world));
        dag.Add(new ReaperSystem(_world));
        dag.Add(new ObserverSystem(_world));
    }

    public void Start() => _world.Runtime.Start();

    /// <summary>Blocks until the run reaches a terminal state or the token is cancelled.</summary>
    public void WaitForTerminal(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_world.IsTerminal)
        {
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Stop the runtime and wait until no tick is in flight. Idempotent.
    /// <para>
    /// Reaching a terminal state does not stop the scheduler — it only sets a flag the host polls — so anything that
    /// reads world state after <see cref="WaitForTerminal"/> (a checksum, a final report) must call this first or it
    /// races the tick loop.
    /// </para>
    /// </summary>
    public void Stop()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;

        try
        {
            TyphonRuntime runtime = _world.Runtime;
            if (runtime != null)
            {
                runtime.Shutdown();

                // Shutdown stops the scheduler, but the high-resolution timer can already be inside a tick when it
                // returns. Disposing the engine out from under an in-flight tick is an AccessViolation, not a
                // catchable exception — so wait for the tick counter to go quiet before tearing anything down.
                long last = -1;
                for (int i = 0; i < 100 && last != runtime.CurrentTickNumber; i++)
                {
                    last = runtime.CurrentTickNumber;
                    Thread.Sleep(20);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Runtime shutdown failed: {ex.Message}");
        }

        _world.DisposeWorkerTransactions();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _dbe?.Dispose();
    }
}
