using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// What a write to a fence-maintained indexed field costs, in the tick and at the fence: 1M entities of <see cref="QfUnit"/>, whose <see cref="QfData"/>
/// carries an AllowMultiple index (<c>Group</c>, 1 000 values of 1 000 entities) and a unique one (<c>Key</c>) (#1346).
/// </summary>
/// <remarks>
/// One sample is one tick: a pass over every cluster doing the arm's writes, timed without its commit, then the fence, timed alone. Arms are interleaved and
/// their order rotated each round; each line reports the median of <see cref="Rounds"/>. Run in Release, on a quiet machine.
/// </remarks>
[TestFixture]
[Explicit("Measurement, not an assertion — run manually, in Release. See #1346.")]
// Manual, not Nightly: a per-entity nanosecond figure taken on the shared gate box measures whatever else is resident there, and there is no assertion to
// regress. The numbers exist to compare index write and fence costs before and after a change, on one quiet machine.
[Category("Manual")]
[NonParallelizable]
class IndexFenceCostTests : TestBase<IndexFenceCostTests>
{
    private const int Rows = 1_000_000;
    private const int Rounds = 7;

    private enum Arm
    {
        SetSame,
        SetGroupMoved,
        SetKeyMoved,
        SetSameOnePercent,
        OpenMutSetSame,
        SetUnindexed,
        SpanUnindexed,
        ReadOnly,
        BatchSetSame,
        BatchSetGroupMoved,
    }

    private static readonly string[] ArmNames =
    [
        "Set, every entity, keys unchanged",
        "Set, every entity, Group moved (AllowMultiple)",
        "Set, every entity, Key moved (unique)",
        "Set, 1 % of entities, keys unchanged",
        "OpenMut.Set, every entity, keys unchanged",
        "Set on QfOther (unindexed), every entity",
        "GetSpan + MarkDirty on QfOther (unindexed)",
        "GetReadOnlySpan on QfData, read only",
        "batched Set, every entity, keys unchanged",
        "batched Set, every entity, Group moved",
    ];

    private readonly int[] _flips = new int[16];   // per arm: a moved arm must alternate +1 / -1 on its own

    [Test]
    [Property("CacheSize", 1024 * 1024 * 1024)]
    public void Measure()
    {
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

        long tick = 1;
        dbe.WriteTickFence(tick++);

        // Profiling mode: TYPHON_IFC_PROFILE=<arm name> runs that arm alone for a minute, for a sampling profiler to attach to (dotnet-trace -p).
        if (Enum.TryParse<Arm>(Environment.GetEnvironmentVariable("TYPHON_IFC_PROFILE"), out var profiled))
        {
            TestContext.Progress.WriteLine($"profiling {profiled}: pid {Environment.ProcessId}");
            var until = Stopwatch.StartNew();
            while (until.Elapsed.TotalSeconds < 60)
            {
                Pass(dbe, profiled);
                dbe.WriteTickFence(tick++);
            }

            return;
        }

        var arms = Enum.GetValues<Arm>();
        var pass = new List<double>[arms.Length];
        var fence = new List<double>[arms.Length];
        for (var a = 0; a < arms.Length; a++)
        {
            pass[a] = [];
            fence[a] = [];
            Pass(dbe, arms[a]);   // warm-up
            dbe.WriteTickFence(tick++);
        }

        for (var r = 0; r < Rounds; r++)
        {
            for (var k = 0; k < arms.Length; k++)
            {
                var a = (k + r) % arms.Length;
                pass[a].Add(Pass(dbe, arms[a]));
                var sw = Stopwatch.StartNew();
                dbe.WriteTickFence(tick++);
                fence[a].Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        for (var a = 0; a < arms.Length; a++)
        {
            pass[a].Sort();
            fence[a].Sort();
            TestContext.Progress.WriteLine($"{ArmNames[a],-48} pass {pass[a][Rounds / 2],7:F2} ms   fence {fence[a][Rounds / 2],7:F2} ms"
                + $"   (pass {pass[a][0]:F2}–{pass[a][^1]:F2}, fence {fence[a][0]:F2}–{fence[a][^1]:F2})");
        }
    }

    private double Pass(DatabaseEngine dbe, Arm arm)
    {
        if (arm == Arm.OpenMutSetSame)
        {
            return PassOpenMut(dbe);
        }

        // Moved arms alternate +1 / -1, so the keys come back every second round: Group stays in 1 000 values, Key stays unique (even keys, then odd).
        var delta = (_flips[(int)arm]++ & 1) == 0 ? 1 : -1;
        using var tx = dbe.CreateQuickTransaction();
        long sum = 0;
        var n = 0;
        Span<QfData> values = stackalloc QfData[64];
        var sw = Stopwatch.StartNew();
        foreach (var cluster in tx.For<QfUnit>().GetClusterEnumerator())
        {
            switch (arm)
            {
                case Arm.BatchSetSame:
                case Arm.BatchSetGroupMoved:
                {
                    var all = cluster.OccupancyBits;
                    var moved = arm == Arm.BatchSetGroupMoved ? delta : 0;
                    for (var rest = all; rest != 0; rest &= rest - 1)
                    {
                        var i = BitOperations.TrailingZeroCount(rest);
                        values[i] = cluster.GetReadOnly(QfUnit.Data, i);
                        values[i].Group += moved;
                    }

                    cluster.Set(QfUnit.Data, all, values);
                    continue;
                }
                case Arm.SpanUnindexed:
                {
                    var span = cluster.GetSpan(QfUnit.Other);
                    for (var s = 0; s < span.Length; s++)
                    {
                        span[s].Hp += 1;
                    }

                    cluster.MarkDirty(QfUnit.Other);
                    continue;
                }
                case Arm.ReadOnly:
                {
                    var span = cluster.GetReadOnlySpan(QfUnit.Data);
                    for (var s = 0; s < span.Length; s++)
                    {
                        sum += span[s].Group;
                    }

                    continue;
                }
            }

            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                switch (arm)
                {
                    case Arm.SetSame:
                        cluster.Set(QfUnit.Data, slot, cluster.GetReadOnly(QfUnit.Data, slot));
                        break;
                    case Arm.SetGroupMoved:
                    {
                        var v = cluster.GetReadOnly(QfUnit.Data, slot);
                        v.Group += delta;
                        cluster.Set(QfUnit.Data, slot, v);
                        break;
                    }
                    case Arm.SetKeyMoved:
                    {
                        var v = cluster.GetReadOnly(QfUnit.Data, slot);
                        v.Key += delta;
                        cluster.Set(QfUnit.Data, slot, v);
                        break;
                    }
                    case Arm.SetSameOnePercent:
                        if (n++ == 99)
                        {
                            n = 0;
                            cluster.Set(QfUnit.Data, slot, cluster.GetReadOnly(QfUnit.Data, slot));
                        }

                        break;
                    case Arm.SetUnindexed:
                    {
                        var v = cluster.GetReadOnly(QfUnit.Other, slot);
                        v.Hp += 1;
                        cluster.Set(QfUnit.Other, slot, v);
                        break;
                    }
                }
            }
        }

        var elapsed = sw.Elapsed.TotalMilliseconds;
        GC.KeepAlive(sum);
        tx.Commit();
        return elapsed;
    }

    private static double PassOpenMut(DatabaseEngine dbe)
    {
        var ids = new List<EntityId>(Rows);
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<QfUnit>().GetClusterEnumerator())
            {
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    ids.Add(cluster.GetEntityId(BitOperations.TrailingZeroCount(bits)));
                }
            }
        }

        using var wtx = dbe.CreateQuickTransaction();
        var sw = Stopwatch.StartNew();
        foreach (var id in ids)
        {
            var e = wtx.OpenMut(id);
            e.Set(QfUnit.Data, e.Read(QfUnit.Data));
        }

        var elapsed = sw.Elapsed.TotalMilliseconds;
        wtx.Commit();
        return elapsed;
    }
}
