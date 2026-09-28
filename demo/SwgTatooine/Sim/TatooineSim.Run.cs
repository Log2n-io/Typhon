using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace SwgTatooine;

/// <summary>Running the simulation and measuring what it cost.</summary>
public sealed partial class TatooineSim
{
    private SimBridge _bridge;
    private EcsView<Player> _playerView;
    private EcsView<Creature> _creatureView;
    private EcsView<CityNpc> _npcView;
    private EcsView<Starship> _shipView;
    private EcsView<CreatureLair> _lairView;
    private EcsView<WorldObject> _structureView;

    /// <summary>
    /// Builds the six views the systems read, and gives back the transaction that created them before returning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The transaction is disposed here, and holding it was a leak (SWG-07).</b> Both entry points used to keep it in a field for the life of the object.
    /// <c>Transaction</c>'s constructor calls <c>EpochManager.EnterScope</c> and its <c>Dispose</c> calls <c>ExitScopeUnordered</c>, so an undisposed
    /// transaction holds an epoch scope open — and nothing deferred behind that epoch can be reclaimed while it is. A measured run ends, so there it was a
    /// bounded cost nobody noticed; <c>--serve</c> never ends, so there it was a server that could not reclaim for as long as it was up. It also held a UoW
    /// registry slot, which <c>CreateQuickTransaction</c>'s own remarks call the expensive half.
    /// </para>
    /// <para>
    /// <b>Disposing it is safe by rule, not by inspection.</b> <c>VIEW-01</c> (<c>rules/ecs.md</c>) states that a retained view query holds no transaction
    /// between operations and <i>enforces</i> that "every <c>EcsView</c> constructor calls <c>EcsQuery.DetachTransaction</c> on its own copy — the creator's
    /// lease ends at construction, not at first refresh". Each later refresh binds the transaction it is handed by the tick and detaches it in a
    /// <c>finally</c>. Nothing here reads the creator again, which is what <c>RunHygieneChecks</c> asserts by ticking a world whose creating transaction is
    /// gone.
    /// </para>
    /// </remarks>
    private void BuildViews()
    {
        using var tx = Dbe.CreateQuickTransaction();
        _playerView = tx.Query<Player>().ToView();
        _creatureView = tx.Query<Creature>().ToView();
        _npcView = tx.Query<CityNpc>().ToView();
        _shipView = SpaceRealm >= 0 ? tx.Query<Starship>().ToView() : null;
        _lairView = tx.Query<CreatureLair>().ToView();
        _structureView = tx.Query<WorldObject>().ToView();
    }

    /// <summary>Per-tick behavioural totals, drained by the telemetry system.</summary>
    public TickStats LastStats { get; private set; }

    /// <summary>GC activity from the runtime's start to its shutdown — the ticking part of the run, not the world build.</summary>
    public GcSnapshot LastGc { get; private set; }

    /// <summary>With <c>--chunk-stats</c>, how evenly the awareness system's chunks shared its work and the pool.</summary>
    public void PrintChunkStats() => _bridge?.PrintChunkStats();

    /// <summary>The systems' shared state, once a run or a serve has built it; null before that.</summary>
    /// <remarks>
    /// Public for the same reason <see cref="Runtime"/> is: the checks beside this demo read what it accumulated, and reflection is not a test.
    /// </remarks>
    public SimBridge Bridge => _bridge;

    /// <summary>
    /// Build the views, wire the schedule and run the configured number of ticks.
    /// </summary>
    /// <remarks>
    /// <para><b>The runtime is driven at the simulation's own tick rate, not as fast as it will go.</b> A saturated loop
    /// would report throughput, and throughput is not the question a game server asks — it asks whether a tick fits in
    /// its budget. Running at 10 Hz with a 100 ms budget also keeps the overload manager out of the measurement: a
    /// sustained overrun makes the runtime start shedding systems, and a measurement taken while work is being dropped
    /// is not a measurement of the work.</para>
    /// <para><c>--unpaced</c> (<see cref="SimConfig.Unpaced"/>) is the one exception, for count-only comparisons: ticks back to back, with the overload
    /// response off so that nothing is shed.</para>
    /// </remarks>
    public RunResult Run()
    {
        BuildViews();

        _bridge = new SimBridge(_config, Map, Index)
        {
            Dbe = Dbe,
            PlanetIndexes = Indexes,
            InteriorsPerPlanet = InteriorsPerPlanet,
            FirstDungeonRealm = FirstDungeonRealm,
            PlayerView = _playerView,
            CreatureView = _creatureView,
            NpcView = _npcView,
            ShipView = _shipView,
            LairView = _lairView,
            StructureView = _structureView,
        };

        // A lambda, not the method group: BuildSchedule takes an optional `replicating` that only the serve path sets.
        _runtime = TyphonRuntime.Create(Dbe, schedule => BuildSchedule(schedule), new RuntimeOptions
        {
            // Unpaced: a rate no tick can meet, so each starts when the last one ends, and an overload response that cannot escalate — at that overrun it
            // would shed systems and change the workload being counted.
            BaseTickRate = _config.Unpaced ? 100_000 : _config.TickRateHz,
            Overload = _config.Unpaced ? new OverloadOptions { OverrunThreshold = float.MaxValue, QueueGrowthTicks = 0 } : new OverloadOptions(),
            WorkerCount = _config.ResolveWorkerCount(),
            ParallelQueryMinChunkSize = _config.ParallelQueryMinChunkSize,
            CostBasedChunking = _config.CostBasedChunking,

            // The fence is the thing under study, so it runs on the worker pool rather than serially on the tick driver.
            EnableParallelFence = _config.ParallelFence,

            // Every measured tick must still be in the ring when the run is summarised, or a long run's tail loses its oldest ticks.
            TelemetryRingCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1024, _config.WarmTicks + _config.MeasuredTicks + 16)),

            // A SYSTEM THAT THROWS ENDS THE RUN (P-3). The engine's default is Isolate: the failing system's branch is skipped, the rest of the tick proceeds,
            // and the tick is reported as a SUCCESS — which for a measured run is the worst of the available outcomes. The world is missing whatever that system
            // was going to do, every tick after it compounds the omission, and the run still prints a median as though nothing had happened. That is precisely
            // the class of silent measurement corruption this package exists to remove.
            //
            // It also makes the abort handler below reachable. It has been registered since before SWG-02 and could never once have fired, because under Isolate
            // OnTickAborted is not raised for a system exception at all (design/Runtime/08-strict-tick-abort.md) — so the demo's own "tick aborted" reporting was
            // dead code that looked like coverage.
            //
            // AbortTickAndStop is terminal by design: no resumption. For a server that is right too — a system fault leaves the world undefined, and
            // 08-hosting's systemd unit already says Restart=on-failure, which is a restart from a checkpoint rather than a limp onwards.
            SystemExceptionPolicy = SystemExceptionPolicy.AbortTickAndStop,
        });

        // A tick that aborts leaves the measurement meaningless, so surface it rather than reporting a median over
        // however many ticks happened to survive.
        var aborts = 0;
        _runtime.OnTickAborted += (_, outcome) =>
        {
            if (aborts++ < 3)
            {
                // The whole exception, stack included: a type and a message name a symptom, not the line that raised it.
                Console.WriteLine($"  !! tick {outcome.TickNumber} aborted: {outcome.Reason} in '{outcome.FailedSystemName}': {outcome.FailedSystemException}");
            }

            // The artefact, on the FIRST abort only (P-3). A tick that aborts usually aborts again on the next one, and one directory per tick would bury the
            // first — which is the one that has the cause in it rather than the consequences.
            if (aborts == 1)
            {
                CrashArtefactPath = WriteCrashArtefact(
                    $"tick aborted: {outcome.Reason} in system '{outcome.FailedSystemName}'", outcome.FailedSystemException, outcome.TickNumber);
            }
        };

        var total = _config.WarmTicks + _config.MeasuredTicks;
        var gcBefore = GcSnapshot.Take();
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

        // Unpaced, the loop keeps ticking between the poll above noticing the count and Shutdown actually landing, so a run executes AT LEAST `total` ticks
        // and an unknown number more. Two runs of the same seed therefore end in different states. The measured window is clamped to `total` below so at
        // least the denominator is the same every run; an exact stop needs a tick limit the runtime does not offer (reported as an engine gap).
        Executed = _runtime.CurrentTickNumber;
        LastGc = GcSnapshot.Take() - gcBefore;

        if (reached < total)
        {
            Console.WriteLine($"  !! only reached tick {reached} of {total} ({aborts} aborts)");
        }
        else if (Executed > total)
        {
            Console.WriteLine($"  .. ran {Executed} ticks for a {total}-tick request; measured the first {total}");
        }

        // Clamped: the window must be the same size every run, whatever the shutdown overshoot was.
        var result = Summarise(Math.Min(reached, total));
        LastStats = _bridge.DrainStats();
        return result;
    }

    /// <summary>The crash artefact this run wrote, or <see langword="null"/> when it did not crash (P-3).</summary>
    public string CrashArtefactPath { get; private set; }

    /// <summary>
    /// Writes a crash artefact for this run: the fault, the ticks leading to it, the world and the configuration.
    /// </summary>
    /// <param name="reason">One line saying what happened.</param>
    /// <param name="exception">The fault, if there is one.</param>
    /// <param name="tickNumber">The tick it happened on; -1 when it happened outside the tick loop.</param>
    /// <returns>The directory written, or <see langword="null"/>.</returns>
    /// <remarks>
    /// <b>Public, because the two callers are in different places and one of them is not in the tick loop at all.</b> An aborted tick is caught by the handler
    /// above; an unhandled exception anywhere else — the world build, the summary, the report — is caught by <c>Program</c>, which has the simulation but not the
    /// runtime. Both want the same artefact, so both call this.
    /// </remarks>
    public string WriteCrashArtefact(string reason, Exception exception, long tickNumber = -1)
    {
        // Read the ring defensively: a fault during the world build has no runtime, and one during Shutdown has a runtime whose telemetry is going away.
        float[] ticks = [];
        try
        {
            if (_runtime?.Telemetry is { } ring)
            {
                var newest = ring.NewestTick;
                var oldest = Math.Max(ring.OldestAvailableTick, newest - CrashArtefact.TickHistory + 1);
                var list = new List<float>(CrashArtefact.TickHistory);
                for (var t = oldest; t <= newest; t++)
                {
                    var ms = ring.GetTick(t).ActualDurationMs;
                    if (ms > 0f)
                    {
                        list.Add(ms);
                    }
                }

                ticks = list.ToArray();
            }
        }
        catch (Exception ringFailure)
        {
            // The ring is a diagnostic, not the point. Losing it must not lose the exception.
            Console.WriteLine($"  !! crash artefact: the tick history could not be read ({ringFailure.GetType().Name}); writing the rest");
        }

        return CrashArtefact.Write(_config.DatabaseDirectory, _config.DatabaseName, reason, exception, ticks, Census, _config,
            tickNumber >= 0 ? tickNumber : (_runtime?.CurrentTickNumber ?? -1));
    }

    /// <summary>What the shuttles did and, with <c>--probe</c>, what their arrivals cost the ports' queries (#910).</summary>
    public void PrintShuttleReport() => _bridge?.PrintShuttleReport();

    /// <summary>What the portals did (Realms G1b).</summary>
    public void PrintPortalReport() => _bridge?.PrintPortalReport();

    /// <summary>True when a player inside pins interior <paramref name="realm"/> (Realms G2).</summary>
    public bool IsInteriorPinned(ushort realm) => _bridge?.IsInteriorPinned(realm) ?? false;

    /// <summary>What the dungeons did (Realms G2).</summary>
    public void PrintDungeonReport() => _bridge?.PrintDungeonReport();

    /// <summary>Dungeons opened and closed over the run (Realms G2).</summary>
    public (int Opened, int Closed) DungeonTotals => _bridge?.DungeonTotals ?? default;

    /// <summary>Crossings over the run: into interiors, out of them, between planets (Realms G1b).</summary>
    public (long Entries, long Exits, long InterPlanet) CrossingTotals => _bridge?.CrossingTotals ?? default;

    /// <summary>What the starships did (Realms G1c).</summary>
    public void PrintSpaceReport() => _bridge?.PrintSpaceReport();

    /// <summary>The fence's per-archetype drift and repair counters, as per-tick means over the measured window.</summary>
    public void PrintSpatialTelemetry() => _bridge?.PrintSpatialTelemetry();

    /// <summary>Measured ticks on which the WAL's durable LSN moved (SWG-02). See <c>SimBridge.WalAdvances</c>.</summary>
    public long WalAdvances => _bridge?.WalAdvances ?? 0L;

    /// <summary>LSNs the durability watermark gained across the measured window.</summary>
    public long WalLsnGained => _bridge?.WalLsnGained ?? 0L;

    /// <summary>Combat events dropped at the queue's growth ceiling — a dropped effect, and expected to be zero (SWG-02).</summary>
    public long CombatQueueOverflow => _bridge?.CombatQueue?.OverflowCount ?? 0L;

    /// <summary>The deepest the combat queue got on the last tick — what says whether the 1 024 budget is anywhere near binding.</summary>
    public long CombatQueuePeak => _bridge?.CombatQueue?.PeakDepth ?? 0L;

    /// <summary>Player-ticks the activity mix did not decide, because a session was driving (SWG-01).</summary>
    public long PossessedSkipped => _bridge?.PossessedSkipped ?? 0L;

    /// <summary>With <c>--work-probe</c>, what a sample of interest queries did, per queried archetype.</summary>
    public void PrintWorkProbe() => _bridge?.PrintWorkProbe();

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
        var flushes = new List<float>(_config.MeasuredTicks);
        var oldest = Math.Max(_config.WarmTicks, ring.OldestAvailableTick);

        var defs = _runtime.Scheduler.Systems;
        var perSystem = new List<float>[defs.Length];
        var entities = new long[defs.Length];
        var workers = new long[defs.Length];
        var ranTicks = new int[defs.Length];

        // Per chunked system and tick, its worker time, and how long the pool waited on it beyond a perfect split of that time over the whole pool.
        var work = new List<float>[defs.Length];
        var waits = new List<float>[defs.Length];
        var pool = _config.ResolveWorkerCount();
        for (var i = 0; i < defs.Length; i++)
        {
            perSystem[i] = new List<float>(_config.MeasuredTicks);
            work[i] = [];
            waits[i] = [];
        }

        // Each tick's own system durations (NaN when skipped), kept in tick order for the spike attribution below.
        var tickSystems = new List<float[]>(_config.MeasuredTicks);
        for (var t = oldest; t <= ring.NewestTick && t < reached; t++)
        {
            ref readonly var tick = ref ring.GetTick(t);
            if (tick.ActualDurationMs <= 0f)
            {
                continue;
            }

            samples.Add(tick.ActualDurationMs);

            // The wall-clock the tick driver spent blocked on the WAL writer publishing THIS tick's records (TickTelemetry.UowFlushMs). Before SWG-02 it was a
            // hard zero for every tick of every run, because nothing wrote a Versioned component after the world build; it is reported beside compute rather
            // than folded into it, because a budget spent waiting on an fsync and a budget spent computing are answered by different hardware.
            flushes.Add(tick.UowFlushMs);
            var metrics = ring.GetSystemMetrics(t);
            var row = new float[defs.Length];
            Array.Fill(row, float.NaN);
            for (var i = 0; i < metrics.Length && i < defs.Length; i++)
            {
                if (metrics[i].WasSkipped)
                {
                    continue;
                }

                row[i] = metrics[i].DurationUs;
                perSystem[i].Add(metrics[i].DurationUs);
                if (metrics[i].WorkUs > 0f)
                {
                    work[i].Add(metrics[i].WorkUs);
                    if (metrics[i].WorkersTouched > 1)
                    {
                        waits[i].Add(metrics[i].DurationUs - (metrics[i].WorkUs / pool));
                    }
                }
                entities[i] += metrics[i].EntitiesProcessed;
                workers[i] += metrics[i].WorkersTouched;
                ranTicks[i]++;
            }

            tickSystems.Add(row);
        }

        var ordered = samples.ToArray();
        if (!string.IsNullOrEmpty(_config.TickLogPath))
        {
            // Raw ticks, so two runs' spikes can be counted against one threshold instead of each against its own median.
            System.IO.File.WriteAllText(_config.TickLogPath,
                string.Join('\n', Array.ConvertAll(ordered, t => t.ToString("F4", System.Globalization.CultureInfo.InvariantCulture))));
        }

        samples.Sort();
        flushes.Sort();
        var budgetMs = 1000f / _config.TickRateHz;
        var median = samples.Count == 0 ? 0f : samples[samples.Count / 2];
        var spikes = Spikes(ordered, tickSystems, perSystem, defs, median);

        var breakdown = new List<SystemCost>();
        var systemsUs = 0f;
        for (var i = 0; i < defs.Length; i++)
        {
            if (perSystem[i].Count == 0)
            {
                continue;
            }

            perSystem[i].Sort();
            work[i].Sort();
            waits[i].Sort();
            var us = perSystem[i][perSystem[i].Count / 2];
            systemsUs += us;
            breakdown.Add(new SystemCost
            {
                Name = defs[i].Name,
                Phase = defs[i].Phase.Name ?? "",
                MedianUs = us,
                WorkMedianUs = work[i].Count == 0 ? float.NaN : Percentile(work[i], 0.5),
                WaitP50Us = waits[i].Count == 0 ? float.NaN : Percentile(waits[i], 0.5),
                WaitP99Us = waits[i].Count == 0 ? float.NaN : Percentile(waits[i], 0.99),
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
            TickP90Ms = Percentile(samples, 0.90),
            TickP99Ms = Percentile(samples, 0.99),
            TickP999Ms = Percentile(samples, 0.999),
            TickMaxMs = samples.Count == 0 ? 0f : samples[^1],
            DurabilityMedianMs = flushes.Count == 0 ? 0f : flushes[flushes.Count / 2],
            DurabilityP99Ms = Percentile(flushes, 0.99),
            DurabilityMaxMs = flushes.Count == 0 ? 0f : flushes[^1],
            TicksWithDurabilityWait = CountOver(flushes, RunResult.DurabilityWaitFloorMs),
            TicksOver125 = Count(ordered, median * 1.25f),
            TicksOver150 = Count(ordered, median * 1.5f),
            TicksOver200 = Count(ordered, median * 2f),
            Spikes = spikes,
            BudgetMs = budgetMs,
            Systems = breakdown,
            SystemsUs = systemsUs,

            // What the systems did not account for: the tick fence, plus whatever the scheduler spends dispatching.
            // Negative would mean the parts out-measure the whole, which happens when systems overlap on the worker pool
            // — their durations are per-system wall-clock and add up to more than the tick if they ran concurrently.
            ResidualUs = (median * 1000f) - systemsUs,
        };
    }

    /// <summary>How many of a sorted sample list are strictly above a threshold. Used for "how many ticks waited on durability at all".</summary>
    private static int CountOver(List<float> sorted, float threshold)
    {
        var n = 0;
        for (var i = sorted.Count - 1; i >= 0 && sorted[i] > threshold; i--)
        {
            n++;
        }

        return n;
    }

    /// <summary>Nearest rank: the p99.9 of 1000 ticks is the 999th, not the slowest.</summary>
    private static float Percentile(List<float> sorted, double p) =>
        sorted.Count == 0 ? 0f : sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * p) - 1, 0, sorted.Count - 1)];

    private static int Count(float[] ticks, float over)
    {
        var n = 0;
        foreach (var t in ticks)
        {
            if (t > over)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// Where the spikes came from: on every tick over 1.25x the median, each system's time over its OWN median, summed across those ticks.
    /// </summary>
    /// <remarks>
    /// A median cannot see a spike and a p99 names its size, not its cause. Systems that share the pool in one phase each charge their own excess, so the
    /// shares of the ticks' total excess need not add up to 100 %.
    /// </remarks>
    private static SpikeReport Spikes(float[] ticks, List<float[]> tickSystems, List<float>[] perSystem, SystemDefinition[] defs, float medianMs)
    {
        var medians = new float[defs.Length];
        for (var i = 0; i < defs.Length; i++)
        {
            var sorted = new List<float>(perSystem[i]);
            sorted.Sort();
            medians[i] = sorted.Count == 0 ? float.NaN : sorted[sorted.Count / 2];
        }

        var report = new SpikeReport();
        var excessUs = new double[defs.Length];
        for (var t = 0; t < ticks.Length; t++)
        {
            if (ticks[t] <= medianMs * 1.25f)
            {
                continue;
            }

            report.Ticks++;
            report.TickExcessMs += ticks[t] - medianMs;
            var row = tickSystems[t];
            for (var i = 0; i < defs.Length; i++)
            {
                if (!float.IsNaN(row[i]) && !float.IsNaN(medians[i]) && row[i] > medians[i])
                {
                    excessUs[i] += row[i] - medians[i];
                }
            }
        }

        for (var i = 0; i < defs.Length; i++)
        {
            if (excessUs[i] > 0)
            {
                report.Systems.Add((defs[i].Name, excessUs[i] / 1000));
            }
        }

        report.Systems.Sort((a, b) => b.ExcessMs.CompareTo(a.ExcessMs));
        return report;
    }

    /// <summary>
    /// The system DAG: one graph on the public track, with the seven phases in causal order.
    /// </summary>
    /// <param name="schedule">The schedule to declare into.</param>
    /// <param name="replicating">
    /// True when the caller also declares replication, which computes each session's interest for real — so the awareness pass is dropped unless
    /// <c>--awareness</c> asks for it. See <see cref="SimConfig.ForceAwareness"/>.
    /// </param>
    private void BuildSchedule(RuntimeSchedule schedule, bool replicating = false)
    {
        var dag = schedule.PublicTrack.DeclareDag("Tatooine")
            .Phases(SimPhases.Input, SimPhases.Spawn, SimPhases.Think, SimPhases.Move, SimPhases.Awareness, SimPhases.Resolve,
                SimPhases.Apply, SimPhases.Economy, SimPhases.Report)
            .DefaultPhase(SimPhases.Report);

        // The demo's one and only event queue (SWG-02). Created on the schedule rather than with `new`, which is what binds it to the resolved worker count:
        // an EventQueue built outside a scheduler keeps its single-slot default and GetWriter then throws for every worker but zero.
        _bridge.CombatQueue = dag.CreateEventQueue<CombatEvent>("Combat", 1024);

        dag.Add(new MissionSystem(_bridge));
        if (_config.Shuttles)
        {
            dag.Add(new ShuttleSystem(_bridge));
            dag.Add(new ShuttleProbeSystem(_bridge));
        }

        // Portal crossings, and with several planets the shuttles bound for another one: both are realm changes.
        if (InteriorsPerPlanet > 0 || (_config.Planets > 1 && _config.Shuttles) || _config.Dungeons > 0)
        {
            dag.Add(new TeleportSystem(_bridge, _config.Shuttles));
        }

        if (_config.Dungeons > 0)
        {
            dag.Add(new DungeonSystem(_bridge, _config.Shuttles));
        }

        dag.Add(new SpatialTelemetrySystem(_bridge));

        dag.Add(new CreatureThinkSystem(_bridge));
        dag.Add(new PlayerThinkSystem(_bridge));

        dag.Add(new CreatureMoveSystem(_bridge));
        dag.Add(new PlayerMoveSystem(_bridge));
        dag.Add(new NpcMoveSystem(_bridge));
        if (SpaceRealm >= 0)
        {
            dag.Add(new ShipMoveSystem(_bridge));
            dag.Add(new ShipScanSystem(_bridge));
        }

        // Interest is computed once. With replication declared it is replication that computes it, for real, per session; awareness would be a second pass
        // over the same players whose result is counted and dropped (#950). --awareness keeps it anyway, as a labelled spatial-query benchmark.
        if (!replicating || _config.ForceAwareness)
        {
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
        }

        dag.Add(new CreatureCombatSystem(_bridge));
        dag.Add(new PlayerCombatSystem(_bridge));
        dag.Add(new CombatResolveSystem(_bridge));

        // The event edges. Declared by name after the systems are added, and load-bearing rather than documentation: without them the consumer is a DAG root
        // with respect to its producers, free to drain a queue in the Apply phase before the Resolve phase has pushed anything into it. The Missions edge spans
        // four phases, which the deriver handles the same way — a producer in an earlier phase, a consumer in a later one.
        dag.Produces("PlayerCombat", _bridge.CombatQueue)
            .Produces("CreatureCombat", _bridge.CombatQueue)
            .Produces("Missions", _bridge.CombatQueue)
            .Consumes("CombatResolve", _bridge.CombatQueue);

        dag.Add(new EconomySystem(_bridge));

        // Only a server has sessions. In the simulation's own DAG rather than one of its own, because an intent writes PlayerMotion and PlayerThink writes it
        // too — see PlayerSessionSystem.
        if (replicating)
        {
            dag.Add(new PlayerSessionSystem());
            dag.Add(new ReplicationReportSystem());
        }
    }
}

/// <summary>GC counters at one point in time, and the difference between two.</summary>
public readonly record struct GcSnapshot(int Gen0, int Gen1, int Gen2, double PauseMs, long AllocatedBytes, long Timestamp)
{
    public static GcSnapshot Take() => new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
        GC.GetTotalPauseDuration().TotalMilliseconds, GC.GetTotalAllocatedBytes(), Stopwatch.GetTimestamp());

    public static GcSnapshot operator -(GcSnapshot a, GcSnapshot b) => new(a.Gen0 - b.Gen0, a.Gen1 - b.Gen1, a.Gen2 - b.Gen2, a.PauseMs - b.PauseMs,
        a.AllocatedBytes - b.AllocatedBytes, a.Timestamp - b.Timestamp);

    /// <summary>Wall time between the two snapshots of a difference.</summary>
    public double ElapsedMs => Timestamp * 1000.0 / Stopwatch.Frequency;
}

/// <summary>What one configuration cost.</summary>
public sealed class RunResult
{
    public SimConfig Config;
    public WorldCensus Census;
    public int TicksMeasured;

    /// <summary>Median wall-clock tick, in milliseconds. The headline.</summary>
    public float TickMedianMs;

    public float TickP90Ms;

    /// <summary>99th percentile tick — the hitch column.</summary>
    public float TickP99Ms;

    /// <summary>99.9th percentile tick: with a thousand ticks, the second slowest.</summary>
    public float TickP999Ms;

    public float TickMaxMs;

    /// <summary>
    /// Median of the per-tick wall-clock the tick driver spent blocked on the WAL writer publishing that tick's records.
    /// </summary>
    /// <remarks>
    /// <b>Reported beside compute, never folded into it.</b> The tick median already contains this wait — it is wall-clock — so the pair says how the budget
    /// was spent rather than only how much of it went. Zero is a real reading for a tick that committed nothing or whose records were already durable, which is
    /// why <see cref="TicksWithDurabilityWait"/> is carried too: a median of zero over a window where a tenth of the ticks waited 3 ms is a true median and a
    /// misleading summary.
    /// </remarks>
    public float DurabilityMedianMs;

    /// <summary>99th percentile durability wait — the figure a per-tick fsync's viability is decided on.</summary>
    public float DurabilityP99Ms;

    public float DurabilityMaxMs;

    /// <summary>Ticks whose durability wait exceeded <see cref="DurabilityWaitFloorMs"/> — how often the WAL was actually in the loop.</summary>
    /// <remarks>
    /// <b>A floor, not zero, because zero is not the floor.</b> <c>UowFlushMs</c> times the flush CALL, so it reads five to eleven microseconds on a tick that
    /// persisted nothing at all; counting ticks above zero therefore counted every tick in the run and reported "600 of 600 ticks waited", which is true of the
    /// timer and false of the claim a reader takes from it. The threshold is two orders of magnitude above that floor and two below anything that would matter to
    /// a 20 ms budget, so it separates a tick that waited from a tick that was merely measured.
    /// </remarks>
    public int TicksWithDurabilityWait;

    /// <summary>The wait below which a tick is taken not to have waited at all — the cost of the flush call itself. See <see cref="TicksWithDurabilityWait"/>.</summary>
    public const float DurabilityWaitFloorMs = 0.1f;

    /// <summary>The median durability wait as a share of the median tick — what decides whether an instance family's fsync is the binding constraint.</summary>
    public float DurabilityShareOfMedianPct => TickMedianMs <= 0f ? 0f : 100f * DurabilityMedianMs / TickMedianMs;

    /// <summary>Ticks over 1.25x, 1.5x and 2x the median: how often a frame spikes, which no percentile of the whole window says.</summary>
    public int TicksOver125;

    public int TicksOver150;
    public int TicksOver200;

    /// <summary>Which systems the ticks over 1.25x the median spent their excess in.</summary>
    public SpikeReport Spikes;

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

/// <summary>The ticks over 1.25x the median, and each system's summed time over its own median on them.</summary>
public sealed class SpikeReport
{
    public int Ticks;
    public double TickExcessMs;
    public List<(string Name, double ExcessMs)> Systems = [];
}

/// <summary>What one system cost, over the measured window.</summary>
public sealed class SystemCost
{
    public string Name;
    public string Phase;

    /// <summary>Median of this system's per-tick wall-clock span, in microseconds.</summary>
    public float MedianUs;

    /// <summary>Median of a parallel system's per-tick worker time (summed chunk durations), in microseconds. NaN for other systems.</summary>
    public float WorkMedianUs;

    /// <summary>
    /// A chunked system's span beyond a perfect split of its worker time over the whole pool, p50 and p99 across ticks: the pool waiting on its last
    /// chunks, or busy with a concurrent system's. NaN for a system that never ran in more than one chunk.
    /// </summary>
    public float WaitP50Us;

    public float WaitP99Us;

    /// <summary>Mean entities this system was dispatched over per tick.</summary>
    public double EntitiesPerTick;

    /// <summary>Mean workers that touched it per tick — 1 means it ran serially.</summary>
    public double WorkersPerTick;

    /// <summary>Ticks on which it actually ran (a skipped tick is not counted).</summary>
    public int TicksRun;
}
