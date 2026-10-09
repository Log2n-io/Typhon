using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

/// <summary>No indexed field — the baseline every other arm is differenced against.</summary>
[Component("Typhon.Bench.SpBurst.Plain", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SbPlain
{
    [Field] public int CellId;
    [Field] public int SpawnKey;
    [Field] public int Payload;
}

/// <summary>One <c>AllowMultiple</c> index over a low-cardinality field — what a game puts on a mob's cell or lair.</summary>
[Component("Typhon.Bench.SpBurst.Multi", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SbMulti
{
    [Field][Index(AllowMultiple = true)] public int CellId;
    [Field] public int SpawnKey;
    [Field] public int Payload;
}

/// <summary>One unique index over a high-cardinality field.</summary>
[Component("Typhon.Bench.SpBurst.Uniq", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SbUniq
{
    [Field] public int CellId;
    [Field][Index] public int SpawnKey;
    [Field] public int Payload;
}

[Archetype]
partial class SbPlainMob : Archetype<SbPlainMob>
{
    public static readonly Comp<SbPlain> Data = Register<SbPlain>();
}

[Archetype]
partial class SbMultiMob : Archetype<SbMultiMob>
{
    public static readonly Comp<SbMulti> Data = Register<SbMulti>();
}

[Archetype]
partial class SbUniqMob : Archetype<SbUniqMob>
{
    public static readonly Comp<SbUniq> Data = Register<SbUniq>();
}

/// <summary>
/// What a repeated mass-arrival costs inside a running tick loop, and whether it gets worse as the world fills.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than a stopwatch in a test.</b> A fixture measuring a single burst shares an engine, a WAL and a checkpointer with whatever
/// ran before it, and those dominate: adding a 200-entity warm-up to one such harness moved a no-index arm's commit from 1.4 ms to 13.9 ms, a tenfold swing
/// from a change that should not have mattered. The figures it produced were not a cost breakdown. This runs the real tick loop, repeats the burst a hundred
/// times, and reports the distribution and the trend — so a number is only believed when it is stable across bursts, and the question "does spawn 290 000
/// cost more than spawn 3 000" is answered rather than assumed away.
/// </para>
/// <para>
/// <b>The shape, which is the one asked for.</b> Burst tick spawns <see cref="BurstSize"/> entities; the next <see cref="IdleTicks"/> ticks spawn nothing,
/// so the fence, the checkpointer and the page cache get the same room between bursts that they would in a game. A hundred bursts reaches ~300 000
/// entities, which is where a per-entity cost that depends on world size has to show up.
/// </para>
/// <para>
/// <b>Three arms, differenced.</b> No index, one <c>AllowMultiple</c> index over 4 distinct keys, one unique index over 3 000. Subtracting the first from
/// the other two gives each index shape's own cost without instrumenting the spawn path, which would change what is being timed.
/// </para>
/// </remarks>
static class SpawnBurstProfile
{
    private const int BurstSize = 3000;
    private const int IdleTicks = 20;
    private const int Bursts = 100;

    /// <summary>Live entities of the arm's archetype, read back through an ordinary query.</summary>
    private static int CountEntities(DatabaseEngine dbe, int arm)
    {
        using var tx = dbe.CreateQuickTransaction();
        var n = 0;
        switch (arm)
        {
            case 0:
                foreach (var _ in tx.Query<SbPlainMob>())
                {
                    n++;
                }

                break;
            case 1:
                foreach (var _ in tx.Query<SbMultiMob>())
                {
                    n++;
                }

                break;
            default:
                foreach (var _ in tx.Query<SbUniqMob>())
                {
                    n++;
                }

                break;
        }

        return n;
    }

    /// <summary>Median of an unsorted list, or 0 when empty. Copies, because the caller's order carries the trend.</summary>
    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var a = values.ToArray();
        Array.Sort(a);
        return a[a.Length / 2];
    }

    private static double Pct(List<double> values, int pct)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var a = values.ToArray();
        Array.Sort(a);
        return a[Math.Clamp(pct * (a.Length - 1) / 100, 0, a.Length - 1)];
    }

    public static void Run()
    {
        Console.WriteLine($"SpawnBurstProfile: {Bursts} bursts of {BurstSize}, {IdleTicks} idle ticks between, "
            + $"{Bursts * BurstSize:N0} entities per arm");
        Console.WriteLine($"strict mode: {CheckConfig.Enabled}");
        Console.WriteLine();

        Console.WriteLine("--- direct: ctx.Transaction.Spawn, the path a serial system uses today ---");
        RunArm("plain  (no index)", 0, false);
        RunArm("multi  (1 AllowMultiple)", 1, false);
        RunArm("unique (1 unique)", 2, false);

        Console.WriteLine("--- deferred: ctx.Commands.Spawn + the #1102 drain, queued and applied on the same tick ---");
        RunArm("plain  (no index)", 0, true);
        RunArm("multi  (1 AllowMultiple)", 1, true);
        RunArm("unique (1 unique)", 2, true);
    }

    /// <param name="label">Printed name of the schema arm.</param>
    /// <param name="arm">0 = no index, 1 = one AllowMultiple, 2 = one unique.</param>
    /// <param name="deferred">
    /// When true the burst goes through <c>ctx.Commands.Spawn</c> and the #1102 drain instead of <c>ctx.Transaction.Spawn</c>. Both land on the SAME tick —
    /// the drain runs before that tick's fence — so the burst tick's duration is directly comparable between the two.
    /// </param>
    private static void RunArm(string label, int arm, bool deferred)
    {
        // 1.5 GiB: the size this profile was calibrated at. The 2 GiB ceiling it once had to respect is gone (#945).
        var dcs = 1536L * 1024 * 1024;
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = $"SpawnBurstProfile_{arm}_{(deferred ? "d" : "s")}_{Environment.ProcessId}";
              options.DatabaseCacheSize = (ulong)dcs;
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SbPlain>();
        dbe.RegisterComponentFromAccessor<SbMulti>();
        dbe.RegisterComponentFromAccessor<SbUniq>();
        dbe.InitializeArchetypes();

        var ticks = 0;
        var key = 0;
        var spawnUs = new List<double>(Bursts);
        var commitUs = new List<double>(Bursts);
        var fenceUs = new List<double>(Bursts);
        var sw = new Stopwatch();
        var toUs = 1_000_000.0 / Stopwatch.Frequency;

        var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Burst").CallbackSystem("Spawn", ctx =>
            {
                var t = Interlocked.Increment(ref ticks);

                // Burst on every (IdleTicks + 1)-th tick. The system still runs on the idle ticks, so its own dispatch cost is in both and cancels.
                if (t % (IdleTicks + 1) != 1 || t > Bursts * (IdleTicks + 1))
                {
                    return;
                }

                // The system's OWN transaction is used, so this is the cost a game system pays — not a side transaction's.
                sw.Restart();
                var cmds = ctx.Commands;
                for (var i = 0; i < BurstSize; i++)
                {
                    var k = key + i;
                    switch (arm)
                    {
                        case 0:
                            var p = SbPlainMob.Data.Set(new SbPlain { CellId = i % 4, SpawnKey = k, Payload = i });
                            if (deferred)
                            {
                                cmds.Spawn<SbPlainMob>(p);
                            }
                            else
                            {
                                ctx.Transaction.Spawn<SbPlainMob>(p);
                            }

                            break;
                        case 1:
                            var m = SbMultiMob.Data.Set(new SbMulti { CellId = i % 4, SpawnKey = k, Payload = i });
                            if (deferred)
                            {
                                cmds.Spawn<SbMultiMob>(m);
                            }
                            else
                            {
                                ctx.Transaction.Spawn<SbMultiMob>(m);
                            }

                            break;
                        default:
                            var u = SbUniqMob.Data.Set(new SbUniq { CellId = i % 4, SpawnKey = k, Payload = i });
                            if (deferred)
                            {
                                cmds.Spawn<SbUniqMob>(u);
                            }
                            else
                            {
                                ctx.Transaction.Spawn<SbUniqMob>(u);
                            }

                            break;
                    }
                }

                key += BurstSize;
                spawnUs.Add(sw.Elapsed.TotalMicroseconds);
            });
        }, new RuntimeOptions
        {
            WorkerCount = 4,
            BaseTickRate = 500,
            AdaptiveFenceCost = false,
            // Must exceed the burst: at the 4 096 default a 3 000-entity burst fits, but the margin is what stops a skewed slot dropping commands and
            // turning this into a measurement of overflow handling.
            EntityCommandsPerTick = 8192,
            // The ring has to outlast the run: at the default 1 024 the first ~1 100 ticks of a 2 105-tick run are evicted before anything reads them.
            TelemetryRingCapacity = 4096,
        });

        using var scope = runtime;
        var totalTicks = Bursts * (IdleTicks + 1) + 5;
        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= totalTicks, TimeSpan.FromSeconds(300));

        // Read the ring BEFORE Shutdown: it is a ring, and the capacity has to cover the run or the early bursts are evicted before they can be read.
        var burstTicks = new List<double>(Bursts);
        var idleTicks = new List<double>(Bursts * IdleTicks);
        var burstSystemUs = new List<double>(Bursts);
        var burstFlushMs = new List<double>(Bursts);
        var ring = runtime.Scheduler.Telemetry;
        if (ring.TryGetRange(0, out var first, out var last))
        {
            for (var t = first; t <= last; t++)
            {
                if (!ring.TryGetTick(t, out var tick))
                {
                    continue;
                }

                // Burst ticks are the ones the system actually spawned on. TickNumber is 0-based and the system's counter is 1-based, hence the + 1.
                var isBurst = (tick.TickNumber + 1) % (IdleTicks + 1) == 1 && tick.TickNumber + 1 <= Bursts * (IdleTicks + 1);
                if (isBurst)
                {
                    burstTicks.Add(tick.ActualDurationMs);
                    burstFlushMs.Add(tick.UowFlushMs);
                    if (ring.TryGetSystemMetrics(t, out var systems) && systems.Length > 0)
                    {
                        burstSystemUs.Add(systems[0].DurationUs);
                    }
                }
                else
                {
                    idleTicks.Add(tick.ActualDurationMs);
                }
            }
        }

        runtime.Shutdown();

        Report(label, spawnUs, burstTicks, idleTicks, burstSystemUs, burstFlushMs);

        // THE validity guard, and the deferred arm needs it more than the direct one: a drain that silently applied nothing would be the fastest arm here
        // and completely wrong. Counted from the entity map, not from the buffer's own accounting, so the buffer cannot vouch for itself.
        var expected = Bursts * BurstSize;
        var live = CountEntities(dbe, arm);
        Console.WriteLine($"  buffer: overflow={runtime.EntityCommands?.OverflowCount ?? 0} rejected={runtime.EntityCommands?.RejectedCount ?? 0}"
            + $"   entities: {live:N0} of {expected:N0} expected{(live == expected ? "" : "   *** MISMATCH ***")}");
        dbe.Dispose();
        sp.Dispose();
    }

    /// <summary>
    /// Prints the distribution and the trend.
    /// </summary>
    /// <remarks>
    /// The median is the headline because one burst in a hundred will collide with a checkpoint; the first and last deciles are what say whether the cost
    /// depends on how full the world is, which is the question a single-burst measurement cannot answer at all.
    /// </remarks>
    private static void Report(string label, List<double> samples, List<double> burstTicks, List<double> idleTicks,
        List<double> burstSystemUs, List<double> burstFlushMs)
    {
        if (samples.Count == 0)
        {
            Console.WriteLine($"{label}: NO SAMPLES — the burst system never fired");
            return;
        }

        // First ten bursts against the last ten, BEFORE sorting, which is the only way to see a trend.
        var firstTen = 0.0;
        var lastTen = 0.0;
        var n = Math.Min(10, samples.Count);
        for (var i = 0; i < n; i++)
        {
            firstTen += samples[i];
            lastTen += samples[samples.Count - 1 - i];
        }

        firstTen /= n;
        lastTen /= n;

        var sorted = samples.ToArray();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];

        Console.WriteLine($"{label}");
        Console.WriteLine($"  spawn-loop only (staging): median {median / BurstSize,7:F3} us/spawn  first10 {firstTen / BurstSize,7:F3}  "
            + $"last10 {lastTen / BurstSize,7:F3}  trend {(firstTen > 0 ? lastTen / firstTen : 0),5:F2}x");
        Console.WriteLine($"  system DurationUs:         median {Median(burstSystemUs) / BurstSize,7:F3} us/spawn   (the body, as the scheduler times it)");
        Console.WriteLine($"  BURST tick ActualDuration: median {Median(burstTicks),7:F3} ms = {Median(burstTicks) * 1000 / BurstSize,7:F3} us/spawn");
        Console.WriteLine($"  IDLE  tick ActualDuration: median {Median(idleTicks),7:F3} ms   (subtract: the burst's true marginal cost)");
        Console.WriteLine($"  burst minus idle:          {(Median(burstTicks) - Median(idleTicks)) * 1000 / BurstSize,7:F3} us/spawn");
        Console.WriteLine($"  UowFlushMs on burst ticks: median {Median(burstFlushMs),7:F3} ms   (the commit's own wait)");
        Console.WriteLine($"  burst tick p0/p50/p90/max: {Pct(burstTicks, 0),6:F2} / {Pct(burstTicks, 50),6:F2} / "
            + $"{Pct(burstTicks, 90),6:F2} / {Pct(burstTicks, 100),6:F2} ms");
        Console.WriteLine();
    }
}
