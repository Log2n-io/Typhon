using Typhon.Engine;

namespace SimpleSpaceBattle;

/// <summary>
/// Phase <see cref="BattlePhases.Reap"/>, after the Reaper — one console line per simulated second. Reads counters
/// and the runtime's telemetry ring; never touches an entity, so its cost is independent of fleet size.
/// </summary>
internal sealed class ObserverSystem : CallbackSystem
{
    private readonly BattleWorld _world;
    private readonly float[] _durations;
    private long _lastReportedTick;
    private long _lastDeaths;
    private long _lastShots;
    private long _lastHits;
    private long _lastAcquisitions;
    private long _lastGathers;

    public ObserverSystem(BattleWorld world)
    {
        _world = world;
        _durations = new float[world.Config.TickRate * 2];
    }

    protected override void Configure(SystemBuilder b) => b
        .Name("Observer")
        .Phase(BattlePhases.Reap)
        .TickDivisor(_world.Config.TickRate)
        .ReadsResource("RunState")
        .After("Reaper");

    protected override void Execute(TickContext ctx)
    {
        BattleWorld world = _world;
        long tick = ctx.TickNumber;

        (float p50, float p95, float p99, int overruns) = SampleTickDurations(tick);

        long deaths = world.TotalDeaths;
        long shots = world.TotalShots;
        long hits = world.TotalHits;
        long acquisitions = world.TotalAcquisitions;
        long elapsedTicks = Math.Max(1, tick - _lastReportedTick);

        Console.WriteLine(
            $"t={tick,7}  alive={world.AliveCount,7:N0}  deaths/s={(deaths - _lastDeaths) * world.Config.TickRate / elapsedTicks,6:N0}  " +
            $"shots/s={(shots - _lastShots) * world.Config.TickRate / elapsedTicks,8:N0}  " +
            $"hit%={(shots > _lastShots ? (hits - _lastHits) * 100.0 / (shots - _lastShots) : 0),5:F1}  " +
            $"acq/s={(acquisitions - _lastAcquisitions) * world.Config.TickRate / elapsedTicks,7:N0}  " +
            $"gather/tick={(world.TotalGathers - _lastGathers) / (double)elapsedTicks,6:F0}  " +
            $"tick p50/p95/p99={p50,6:F2}/{p95,6:F2}/{p99,6:F2} ms  over={overruns}");

        if (Environment.GetEnvironmentVariable("SSB_BREAKDOWN") == "1")
        {
            PrintSystemBreakdown(tick, p50);
        }

        _lastReportedTick = tick;
        _lastDeaths = deaths;
        _lastShots = shots;
        _lastHits = hits;
        _lastAcquisitions = acquisitions;
        _lastGathers = world.TotalGathers;
    }

    /// <summary>
    /// Per-system wall-clock for the previous tick, plus the residual — the part of the tick that belongs to no
    /// system at all (view refresh at tick start, the tick fence, migration drain). That residual is where a
    /// serialised tick hides, so it is printed explicitly rather than left to be inferred.
    /// </summary>
    private void PrintSystemBreakdown(long tick, float tickMs)
    {
        TickTelemetryRing ring = _world.Runtime?.Telemetry;
        if (ring == null)
        {
            return;
        }

        long sampleTick = Math.Min(ring.NewestTick, tick - 1);
        if (sampleTick < ring.OldestAvailableTick)
        {
            return;
        }

        ReadOnlySpan<SystemTelemetry> systems = ring.GetSystemMetrics(sampleTick);
        ref readonly TickTelemetry sample = ref ring.GetTick(sampleTick);
        float accounted = 0f;

        Console.WriteLine($"    breakdown of tick {sampleTick} ({sample.ActualDurationMs:F2} ms):");
        IReadOnlyList<SystemDefinition> defs = _world.Runtime.Systems;

        for (int i = 0; i < systems.Length; i++)
        {
            SystemTelemetry s = systems[i];
            float ms = s.DurationUs / 1000f;
            accounted += ms;
            string name = s.SystemIndex >= 0 && s.SystemIndex < defs.Count ? defs[s.SystemIndex].Name : $"#{s.SystemIndex}";
            Console.WriteLine(
                $"      {name,-14} {ms,8:F2} ms  workers={s.WorkersTouched,3}  entities={s.EntitiesProcessed,7:N0}  " +
                $"straggler={s.StragglerGapUs / 1000f,7:F2} ms  {(s.WasSkipped ? s.SkipReason.ToString() : string.Empty)}");
        }

        Console.WriteLine($"      {"RESIDUAL",-14} {sample.ActualDurationMs - accounted,8:F2} ms  (view refresh + tick fence + migration)");
    }

    /// <summary>
    /// Percentiles over the ticks since the last report. The ring is the runtime's own measurement, so this reports
    /// what the scheduler saw rather than what the host could time from outside.
    /// </summary>
    private (float P50, float P95, float P99, int Overruns) SampleTickDurations(long tick)
    {
        TickTelemetryRing ring = _world.Runtime?.Telemetry;
        if (ring == null)
        {
            return (0f, 0f, 0f, 0);
        }

        long oldest = Math.Max(ring.OldestAvailableTick, _lastReportedTick);
        long newest = Math.Min(ring.NewestTick, tick);
        int count = 0;
        int overruns = 0;

        for (long t = oldest; t <= newest && count < _durations.Length; t++)
        {
            ref readonly TickTelemetry sample = ref ring.GetTick(t);
            if (sample.TickNumber != t)
            {
                continue;
            }

            _durations[count++] = sample.ActualDurationMs;
            if (sample.ActualDurationMs > sample.TargetDurationMs)
            {
                overruns++;
            }
        }

        if (count == 0)
        {
            return (0f, 0f, 0f, 0);
        }

        Span<float> window = _durations.AsSpan(0, count);
        window.Sort();
        return (Percentile(window, 0.50f), Percentile(window, 0.95f), Percentile(window, 0.99f), overruns);
    }

    private static float Percentile(ReadOnlySpan<float> sorted, float q)
    {
        int index = (int)(q * (sorted.Length - 1));
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
