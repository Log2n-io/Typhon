using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// What the parallel tick fence costs to maintain fence-maintained indexes under <see cref="TyphonRuntime"/>: the same 1M <see cref="QfUnit"/> population
/// as <see cref="IndexFenceCostTests"/>, written by a callback system every tick, the fence run by the Fence DAG on W workers (#1346).
/// </summary>
/// <remarks>
/// <para>The serial fixture times <c>WriteTickFence</c>; this one reads the runtime's own fence timings, because the sliced Prep, the IndexMoves phase (the
/// index moves applied in key ranges on the workers), the Prep tail that finishes them, and the other phases only exist on the parallel path.</para>
/// <para>One tick runs one arm, rotated tick by tick; the callback samples the previous tick's fence before writing, so each sample is attributed to the arm
/// that tick ran. Medians over <see cref="TicksPerArm"/> ticks. Run in Release, on a quiet machine.</para>
/// </remarks>
[TestFixture]
[Explicit("Measurement, not an assertion — run manually, in Release. See #1346.")]
[Category("Manual")]
[NonParallelizable]
class IndexFenceParallelCostTests : TestBase<IndexFenceParallelCostTests>
{
    private const int Rows = 1_000_000;
    private const int WarmupTicks = 6;
    private const int TicksPerArm = 9;

    private static readonly string[] ArmNames = ["keys unchanged", "Group moved (AllowMultiple)", "Key moved (unique)"];

    [Test]
    [Property("CacheSize", 1024 * 1024 * 1024)]
    public void Measure()
    {
        // TYPHON_IFC_WORKERS=<W> picks the worker count (default 8): one engine per run, so one W per process.
        var workerCount = int.TryParse(Environment.GetEnvironmentVariable("TYPHON_IFC_WORKERS"), out var w) ? w : 8;
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QfData>();
        dbe.RegisterComponentFromAccessor<QfOther>();
        dbe.RegisterComponentFromAccessor<QfVData>();
        dbe.InitializeArchetypes();
        for (var written = 0; written < Rows;)
        {
            var batch = Math.Min(10_000, Rows - written);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < batch; i++)
            {
                var idx = written + i;
                var d = new QfData(idx % 1000, idx * 2);
                var o = new QfOther();
                tx.Spawn<QfUnit>(QfUnit.Data.Set(in d), QfUnit.Other.Set(in o));
            }

            tx.Commit();
            written += batch;
        }

        dbe.WriteTickFence(1);

        const int arms = 3;
        var wall = NewLists(arms);
        var prep = NewLists(arms);
        var prepCpu = NewLists(arms);
        var tail = NewLists(arms);
        var migrate = NewLists(arms);
        var moves = NewLists(arms);
        var movesCpu = NewLists(arms);
        var movesSerial = NewLists(arms);
        var flips = new int[arms];
        var runtimeRef = new TyphonRuntime[1];
        var ticks = 0;
        var done = new ManualResetEventSlim();
        const int totalTicks = WarmupTicks + arms * TicksPerArm + 1;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Cost").CallbackSystem("Write", _ =>
                   {
                       var n = Interlocked.Increment(ref ticks);
                       if (n > WarmupTicks + 1 && n <= totalTicks)
                       {
                           // The previous tick's fence, run on the arm of tick n - 1.
                           var a = (n - 1) % arms;
                           var rt = runtimeRef[0];
                           wall[a].Add(Ms(rt.LastFenceWallTicks));
                           prep[a].Add(Ms(rt.LastPrepStats.SpanTicks));
                           prepCpu[a].Add(Ms(rt.LastPrepStats.CpuTicks));
                           tail[a].Add(Ms(rt.LastFenceSerialTicks.MigrateTail));
                           migrate[a].Add(Ms(rt.LastMigrateStats.SpanTicks));
                           moves[a].Add(Ms(rt.LastIndexMovesStats.SpanTicks));
                           movesCpu[a].Add(Ms(rt.LastIndexMovesStats.CpuTicks));
                           movesSerial[a].Add(Ms(rt.LastIndexMovesSerialTicks));
                       }

                       if (n >= totalTicks)
                       {
                           done.Set();
                           return;
                       }

                       var arm = n % arms;
                       Write(dbe, arm, (flips[arm]++ & 1) == 0 ? 1 : -1);
                   });
               }, new RuntimeOptions
               {
                   WorkerCount = workerCount,
                   // 4 Hz: the write pass and the fence fit with room to spare, so ticks do not overlap and each fence describes one tick's writes.
                   BaseTickRate = 4,
                   EnableParallelFence = true,
                   AdaptiveFenceCost = false,
               }))
        {
            runtimeRef[0] = runtime;
            Exception unhandled = null;
            runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);
            runtime.Start();
            var finished = done.Wait(TimeSpan.FromMinutes(3));
            runtime.Shutdown();
            Assert.That(unhandled, Is.Null, $"the parallel fence threw while being measured: {unhandled}");
            Assert.That(finished, Is.True, "the run did not reach its tick count");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"W={workerCount}  (medians of {TicksPerArm} ticks; ms)    fence wall   Prep span   Prep CPU   Moves span   Moves CPU"
            + "   Moves serial   Prep tail   Migrate span");
        for (var a = 0; a < arms; a++)
        {
            sb.AppendLine($"  {ArmNames[a],-30} {Median(wall[a]),10:F2} {Median(prep[a]),11:F2} {Median(prepCpu[a]),10:F2} {Median(moves[a]),12:F2}"
                + $" {Median(movesCpu[a]),11:F2} {Median(movesSerial[a]),14:F2} {Median(tail[a]),11:F2} {Median(migrate[a]),14:F2}");
        }

        TestContext.Progress.WriteLine(sb.ToString());
    }

    private static void Write(DatabaseEngine dbe, int arm, int delta)
    {
        using var tx = dbe.CreateQuickTransaction();
        foreach (var cluster in tx.For<QfUnit>().GetClusterEnumerator())
        {
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                var v = cluster.GetReadOnly(QfUnit.Data, slot);
                switch (arm)
                {
                    case 1:
                        v.Group += delta;   // alternating +1 / -1 keeps Group in 1 000 values
                        break;
                    case 2:
                        v.Key += delta;     // even keys, then odd: unique throughout
                        break;
                }

                cluster.Set(QfUnit.Data, slot, v);
            }
        }

        tx.Commit();
    }

    private static List<double>[] NewLists(int n)
    {
        var lists = new List<double>[n];
        for (var i = 0; i < n; i++)
        {
            lists[i] = [];
        }

        return lists;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static double Median(List<double> values)
    {
        values.Sort();
        return values.Count == 0 ? double.NaN : values[values.Count / 2];
    }
}
