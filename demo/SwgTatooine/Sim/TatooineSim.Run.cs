using System;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine;

/// <summary>Running the simulation and measuring what it cost.</summary>
public sealed partial class TatooineSim
{
    private SimBridge _bridge;
    private EcsView<Player> _playerView;
    private EcsView<Creature> _creatureView;
    private EcsView<CityNpc> _npcView;
    private EcsView<CreatureLair> _lairView;
    private EcsView<WorldObject> _structureView;
    private Transaction _viewTx;

    /// <summary>Per-tick behavioural totals, drained by the telemetry system.</summary>
    public TickStats LastStats { get; private set; }

    /// <summary>
    /// Build the views, wire the schedule and run the configured number of ticks.
    /// </summary>
    /// <remarks>
    /// <para><b>The runtime is driven at the simulation's own tick rate, not as fast as it will go.</b> A saturated loop
    /// would report throughput, and throughput is not the question a game server asks — it asks whether a tick fits in
    /// its budget. Running at 10 Hz with a 100 ms budget also keeps the overload manager out of the measurement: a
    /// sustained overrun makes the runtime start shedding systems, and a measurement taken while work is being dropped
    /// is not a measurement of the work.</para>
    /// </remarks>
    public RunResult Run()
    {
        _viewTx = Dbe.CreateQuickTransaction();
        _playerView = _viewTx.Query<Player>().ToView();
        _creatureView = _viewTx.Query<Creature>().ToView();
        _npcView = _viewTx.Query<CityNpc>().ToView();
        _lairView = _viewTx.Query<CreatureLair>().ToView();
        _structureView = _viewTx.Query<WorldObject>().ToView();

        _bridge = new SimBridge(_config, Map, Index)
        {
            Dbe = Dbe,
            PlayerView = _playerView,
            CreatureView = _creatureView,
            NpcView = _npcView,
            LairView = _lairView,
            StructureView = _structureView,
        };

        _runtime = TyphonRuntime.Create(Dbe, BuildSchedule, new RuntimeOptions
        {
            BaseTickRate = _config.TickRateHz,
            WorkerCount = _config.ResolveWorkerCount(),
            ParallelQueryMinChunkSize = _config.ParallelQueryMinChunkSize,

            // The fence is the thing under study, so it runs on the worker pool rather than serially on the tick driver.
            EnableParallelFence = _config.ParallelFence,
        });

        // A tick that aborts leaves the measurement meaningless, so surface it rather than reporting a median over
        // however many ticks happened to survive.
        var aborts = 0;
        _runtime.OnTickAborted += (_, outcome) =>
        {
            if (aborts++ < 3)
            {
                Console.WriteLine($"  !! tick {outcome.TickNumber} aborted: {outcome.Reason} in '{outcome.FailedSystemName}': "
                    + $"{outcome.FailedSystemException?.GetType().Name}: {outcome.FailedSystemException?.Message}");
            }
        };

        var total = _config.WarmTicks + _config.MeasuredTicks;
        _runtime.Start();

        // Poll rather than sleep for a computed duration: a tick that overruns would make a duration-based wait stop
        // early and measure fewer ticks than it reported.
        var deadline = DateTime.UtcNow.AddSeconds(10 + (total * 2.0 / Math.Max(1, _config.TickRateHz)));
        while (_runtime.CurrentTickNumber < total && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }

        var reached = _runtime.CurrentTickNumber;
        _runtime.Shutdown();

        if (reached < total)
        {
            Console.WriteLine($"  !! only reached tick {reached} of {total} ({aborts} aborts)");
        }

        var result = Summarise(reached);
        LastStats = _bridge.DrainStats();
        return result;
    }

    /// <summary>What the shuttles did and, with <c>--probe</c>, what their arrivals cost the ports' queries (#910).</summary>
    public void PrintShuttleReport() => _bridge?.PrintShuttleReport();

    /// <summary>The fence's per-archetype drift and repair counters, as per-tick means over the measured window.</summary>
    public void PrintSpatialTelemetry() => _bridge?.PrintSpatialTelemetry();

    /// <summary>
    /// Read the telemetry ring and reduce the measured window to medians, a p99, and a per-system breakdown.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the breakdown exists.</b> The tick median alone can rank two configurations but cannot say WHERE the
    /// difference landed. A cell-size sweep that only reports total tick time is claiming a spatial-layer effect from a
    /// number that also contains AI, integration, the economy walk and the WAL — none of which depend on cell size. The
    /// per-system durations are what turns "this setting is faster" into "this setting is faster in Awareness".</para>
    /// <para><b>The residual is the fence.</b> Systems are timed individually; the tick is timed as a whole. What is left
    /// after subtracting every system is the tick fence plus scheduler overhead — and since the fence is where cluster
    /// migration, AABB refresh and repair happen, that residual is the OTHER half of the spatial cost. Reporting it as a
    /// row rather than leaving it implicit is the point: a cell size that makes queries cheap by making the fence
    /// expensive should be visible as such.</para>
    /// <para>Each system's figure is the median of its per-tick <c>DurationUs</c> across the window, not the mean: one
    /// scheduler hiccup in sixty ticks should not move a system's attributed cost.</para>
    /// </remarks>
    private RunResult Summarise(long reached)
    {
        var ring = _runtime.Telemetry;
        var samples = new List<float>(_config.MeasuredTicks);
        var oldest = Math.Max(_config.WarmTicks, ring.OldestAvailableTick);

        var defs = _runtime.Scheduler.Systems;
        var perSystem = new List<float>[defs.Length];
        var entities = new long[defs.Length];
        var workers = new long[defs.Length];
        var ranTicks = new int[defs.Length];
        for (var i = 0; i < defs.Length; i++)
        {
            perSystem[i] = new List<float>(_config.MeasuredTicks);
        }

        for (var t = oldest; t <= ring.NewestTick && t < reached; t++)
        {
            ref readonly var tick = ref ring.GetTick(t);
            if (tick.ActualDurationMs <= 0f)
            {
                continue;
            }

            samples.Add(tick.ActualDurationMs);
            var metrics = ring.GetSystemMetrics(t);
            for (var i = 0; i < metrics.Length && i < defs.Length; i++)
            {
                if (metrics[i].WasSkipped)
                {
                    continue;
                }

                perSystem[i].Add(metrics[i].DurationUs);
                entities[i] += metrics[i].EntitiesProcessed;
                workers[i] += metrics[i].WorkersTouched;
                ranTicks[i]++;
            }
        }

        samples.Sort();
        var budgetMs = 1000f / _config.TickRateHz;
        var median = samples.Count == 0 ? 0f : samples[samples.Count / 2];

        var breakdown = new List<SystemCost>();
        var systemsUs = 0f;
        for (var i = 0; i < defs.Length; i++)
        {
            if (perSystem[i].Count == 0)
            {
                continue;
            }

            perSystem[i].Sort();
            var us = perSystem[i][perSystem[i].Count / 2];
            systemsUs += us;
            breakdown.Add(new SystemCost
            {
                Name = defs[i].Name,
                Phase = defs[i].Phase.Name ?? "",
                MedianUs = us,
                EntitiesPerTick = ranTicks[i] == 0 ? 0d : (double)entities[i] / ranTicks[i],
                WorkersPerTick = ranTicks[i] == 0 ? 0d : (double)workers[i] / ranTicks[i],
                TicksRun = ranTicks[i],
            });
        }

        breakdown.Sort((a, b) => b.MedianUs.CompareTo(a.MedianUs));

        return new RunResult
        {
            Config = _config,
            Census = Census,
            TicksMeasured = samples.Count,
            TickMedianMs = median,
            TickP99Ms = samples.Count == 0 ? 0f : samples[Math.Min(samples.Count - 1, (int)(samples.Count * 0.99f))],
            TickMaxMs = samples.Count == 0 ? 0f : samples[^1],
            BudgetMs = budgetMs,
            Systems = breakdown,
            SystemsUs = systemsUs,

            // What the systems did not account for: the tick fence, plus whatever the scheduler spends dispatching.
            // Negative would mean the parts out-measure the whole, which happens when systems overlap on the worker pool
            // — their durations are per-system wall-clock and add up to more than the tick if they ran concurrently.
            ResidualUs = (median * 1000f) - systemsUs,
        };
    }

    /// <summary>
    /// The system DAG: one graph on the public track, with the seven phases in causal order.
    /// </summary>
    private void BuildSchedule(RuntimeSchedule schedule)
    {
        var dag = schedule.PublicTrack.DeclareDag("Tatooine")
            .Phases(SimPhases.Spawn, SimPhases.Think, SimPhases.Move, SimPhases.Awareness, SimPhases.Resolve,
                SimPhases.Economy, SimPhases.Report)
            .DefaultPhase(SimPhases.Report);

        dag.Add(new MissionSystem(_bridge));
        if (_config.Shuttles)
        {
            dag.Add(new ShuttleSystem(_bridge));
            dag.Add(new ShuttleProbeSystem(_bridge));
        }

        dag.Add(new SpatialTelemetrySystem(_bridge));

        dag.Add(new CreatureThinkSystem(_bridge));
        dag.Add(new PlayerThinkSystem(_bridge));

        dag.Add(new CreatureMoveSystem(_bridge));
        dag.Add(new PlayerMoveSystem(_bridge));
        dag.Add(new NpcMoveSystem(_bridge));

        if (_config.SplitAwareness)
        {
            // Four systems over the same players, one per queried archetype. They share no write, so they run
            // concurrently and each gets its own chunk allocation.
            dag.Add(new AwarenessSplitSystem(_bridge, AwarenessTarget.Structures));
            dag.Add(new AwarenessSplitSystem(_bridge, AwarenessTarget.Creatures));
            dag.Add(new AwarenessSplitSystem(_bridge, AwarenessTarget.Npcs));
            dag.Add(new AwarenessSplitSystem(_bridge, AwarenessTarget.Players));
        }
        else
        {
            dag.Add(new AwarenessSystem(_bridge));
        }

        dag.Add(new CreatureCombatSystem(_bridge));

        dag.Add(new EconomySystem(_bridge));
    }
}

/// <summary>What one configuration cost.</summary>
public sealed class RunResult
{
    public SimConfig Config;
    public WorldCensus Census;
    public int TicksMeasured;

    /// <summary>Median wall-clock tick, in milliseconds. The headline.</summary>
    public float TickMedianMs;

    /// <summary>99th percentile tick — the hitch column.</summary>
    public float TickP99Ms;

    public float TickMaxMs;

    /// <summary>The tick budget at the simulation's rate, which is what the median has to fit inside.</summary>
    public float BudgetMs;

    /// <summary>Per-system medians, largest first.</summary>
    public List<SystemCost> Systems = [];

    /// <summary>Sum of the per-system medians, in microseconds.</summary>
    public float SystemsUs;

    /// <summary>
    /// Tick median minus the summed systems: the tick fence plus scheduler dispatch.
    /// </summary>
    /// <remarks>
    /// Can be NEGATIVE, and that is information rather than an error. Every system's duration is its own wall-clock
    /// span; systems that ran concurrently on different workers each charge their full span, so the parts can exceed the
    /// whole. A negative residual therefore means the phase is genuinely running in parallel — and a residual near the
    /// whole tick means it is not.
    /// </remarks>
    public float ResidualUs;

    /// <summary>Median tick as a share of the budget. Above 100 % the server cannot keep up.</summary>
    public float BudgetPct => BudgetMs <= 0f ? 0f : 100f * TickMedianMs / BudgetMs;
}

/// <summary>What one system cost, over the measured window.</summary>
public sealed class SystemCost
{
    public string Name;
    public string Phase;

    /// <summary>Median of this system's per-tick wall-clock span, in microseconds.</summary>
    public float MedianUs;

    /// <summary>Mean entities this system was dispatched over per tick.</summary>
    public double EntitiesPerTick;

    /// <summary>Mean workers that touched it per tick — 1 means it ran serially.</summary>
    public double WorkersPerTick;

    /// <summary>Ticks on which it actually ran (a skipped tick is not counted).</summary>
    public int TicksRun;
}
