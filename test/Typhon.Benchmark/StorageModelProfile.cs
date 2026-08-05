using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Typhon.Engine;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ── Components ─────────────────────────────────────────────────────────────────────────────────────────────────
// Deliberately NOT reusing the AaBench* fixtures: those were built around cluster-eligible archetypes, and the whole
// question here is what happens to an archetype that used to have no cluster at all.

/// <summary>Versioned (the default), no indexed field.</summary>
[Component("Typhon.Bench.Sm.Health", 1)]
[StructLayout(LayoutKind.Sequential)]
struct SmHealth
{
    public int Current;
    public int Max;

    public SmHealth(int current, int max) { Current = current; Max = max; }
}

/// <summary>Versioned with a low-cardinality AllowMultiple index — the classification shape the write-path guard targets.</summary>
[Component("Typhon.Bench.Sm.Ranked", 1)]
[StructLayout(LayoutKind.Sequential)]
struct SmRanked
{
    [Index(AllowMultiple = true)] public int Tier;
    public int Score;

    public SmRanked(int tier, int score) { Tier = tier; Score = score; }
}

/// <summary>SingleVersion — only exists to make the control archetype cluster-eligible under BOTH builds.</summary>
[Component("Typhon.Bench.Sm.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmPos
{
    public float X;
    public float Y;

    public SmPos(float x, float y) { X = x; Y = y; }
}

// Three Versioned components on one archetype — the shape the HEAD copy is paid for three times over, once per slot.
// A single-component pure-Versioned archetype understates the cost of the model switch for anything realistic.

[Component("Typhon.Bench.Sm.A", 1)]
[StructLayout(LayoutKind.Sequential)]
struct SmA
{
    public int V;
    public int W;

    public SmA(int v, int w) { V = v; W = w; }
}

[Component("Typhon.Bench.Sm.B", 1)]
[StructLayout(LayoutKind.Sequential)]
struct SmB
{
    public long V;

    public SmB(long v) { V = v; }
}

[Component("Typhon.Bench.Sm.C", 1)]
[StructLayout(LayoutKind.Sequential)]
struct SmC
{
    public float X;
    public float Y;
    public float Z;
    public float W;

    public SmC(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
}

// ── Archetypes ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Indexed and held by exactly ONE archetype — the K=1 shape, which is the common case and the one YCSB-style
/// early-terminating scans use. Its enumerator must stream rather than materialise.</summary>
[Component("Typhon.Bench.Sm.Solo", 1)]
[StructLayout(LayoutKind.Sequential)]
struct SmSolo
{
    [Index(AllowMultiple = true)] public int Bucket;
    public int Payload;

    public SmSolo(int bucket, int payload) { Bucket = bucket; Payload = payload; }
}

[Archetype]
partial class SmSoloArch : Archetype<SmSoloArch>
{
    public static readonly Comp<SmSolo> Solo = Register<SmSolo>();
}

/// <summary>Pure-Versioned, no index. FLAT before the change, CLUSTER after.</summary>
[Archetype]
partial class SmPureV : Archetype<SmPureV>
{
    public static readonly Comp<SmHealth> Health = Register<SmHealth>();
}

/// <summary>Pure-Versioned, indexed. FLAT before, CLUSTER after — and its index moves homes with it.</summary>
[Archetype]
partial class SmPureVIdx : Archetype<SmPureVIdx>
{
    public static readonly Comp<SmRanked> Ranked = Register<SmRanked>();
}

/// <summary>
/// Pure-Versioned with THREE components (A, B, C) — the realistic shape. Flat before, cluster after, and the HEAD copy
/// is paid per Versioned slot, so this is where the write cost should scale.
/// </summary>
[Archetype]
partial class SmPureV3 : Archetype<SmPureV3>
{
    public static readonly Comp<SmA> A = Register<SmA>();
    public static readonly Comp<SmB> B = Register<SmB>();
    public static readonly Comp<SmC> C = Register<SmC>();
}

/// <summary>
/// PROBE. SingleVersion + TWO Versioned components — cluster-backed under BOTH eligibility rules, so if writing both
/// Versioned slots in one transaction fails here it fails on the baseline too, and the multi-Versioned cluster-commit
/// defect is pre-existing rather than caused by the eligibility flip.
/// </summary>
/// <remarks>
/// <c>SmA</c> is 8 bytes and <c>SmC</c> is 16 — DIFFERENT chunk strides on purpose. Two same-stride components hide the
/// defect: the wrong-segment lookup stays in range and silently writes the wrong bytes into the cluster slot instead of
/// throwing. Mismatched strides turn the same bug into a visible out-of-range failure.
/// </remarks>
[Archetype]
partial class SmMixed2V : Archetype<SmMixed2V>
{
    public static readonly Comp<SmPos> Pos = Register<SmPos>();
    public static readonly Comp<SmA> A = Register<SmA>();
    public static readonly Comp<SmC> C = Register<SmC>();
}

/// <summary>
/// CONTROL. The SV slot makes this cluster-backed under both the old and the new eligibility rule, so nothing about it
/// changes. Any delta measured here is harness noise or machine drift, not the storage-model switch — which is what
/// makes the pure-Versioned deltas trustworthy.
/// </summary>
[Archetype]
partial class SmMixed : Archetype<SmMixed>
{
    public static readonly Comp<SmPos> Pos = Register<SmPos>();
    public static readonly Comp<SmRanked> Ranked = Register<SmRanked>();
}

/// <summary>
/// Measures the pure-Versioned storage model old (flat) vs new (cluster). Run the SAME file in two worktrees and diff.
/// </summary>
/// <remarks>
/// Not BenchmarkDotNet on purpose: BDN cannot see across two working trees, collides on duplicate csproj identities when
/// worktrees exist, and cannot report on-disk size — which is half the question here.
/// </remarks>
internal static class StorageModelProfile
{
    private const int EntityCount = 20_000;
    private const int OpCount = 2_000;
    private const int Warmup = 3;
    private const int Reps = 7;

    private static readonly List<(string Scenario, double NsPerOp)> Results = [];
    private static readonly List<(string Metric, double Value)> Sizes = [];

    public static void Run(string[] args)
    {
        var label = args.FirstOrDefault(a => a.StartsWith("--label="))?["--label=".Length..] ?? "unlabelled";
        Console.WriteLine($"=== Storage-model profile [{label}] — {EntityCount:N0} entities, {OpCount:N0} ops/scenario ===");

        var dbName = $"SmProfile_{Environment.ProcessId}";
        var (sp, bundleDir) = BuildProvider(dbName, deleteFirst: true);

        EntityId[] pureIds;
        EntityId[] idxIds;
        EntityId[] mixedIds;

        using (var scope = sp.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);

            Console.WriteLine($"  eligibility: SmPureV.IsClusterEligible = {ArchetypeRegistry.GetMetadata<SmPureV>().IsClusterEligible}");
            Console.WriteLine($"               SmPureVIdx.IsClusterEligible = {ArchetypeRegistry.GetMetadata<SmPureVIdx>().IsClusterEligible}");
            Console.WriteLine($"               SmMixed.IsClusterEligible = {ArchetypeRegistry.GetMetadata<SmMixed>().IsClusterEligible} (control)");

            pureIds = MeasureSpawn("spawn.pureV", dbe, i => Spawn(dbe, i));
            idxIds = MeasureSpawn("spawn.pureV.indexed", dbe, i => SpawnIdx(dbe, i));
            var pure3Ids = MeasureSpawn("spawn.pureV3.threeComponents", dbe, i => SpawnPure3(dbe, i));
            mixedIds = MeasureSpawn("spawn.mixed.CONTROL", dbe, i => SpawnMixed(dbe, i));
            MeasureSpawn("spawn.solo.singleArchetype", dbe, i => SpawnSolo(dbe, i));

            dbe.WriteTickFence(0);

            ProbeMultiVersionedClusterCommit(dbe);
            MeasureRead(dbe, pureIds, pure3Ids);
            MeasureUpdate(dbe, pureIds, idxIds, mixedIds);
            MeasureUpdate3(dbe, pure3Ids);
            MeasureQuery(dbe);
            MeasureEnumerateIndex(dbe);
            MeasureDestroy(dbe);
        }

        (sp as IDisposable)?.Dispose();

        // ── On-disk cost, measured after a clean close so every dirty page has been flushed ──
        RecordDirectorySize("disk.total.bytes", bundleDir);

        // ── Reopen ──
        var (sp2, _) = BuildProvider(dbName, deleteFirst: false);
        var swOpen = Stopwatch.StartNew();
        using (var scope = sp2.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            swOpen.Stop();
            Sizes.Add(("reopen.ms", swOpen.Elapsed.TotalMilliseconds));
        }

        (sp2 as IDisposable)?.Dispose();

        try
        {
            using var s = sp2.CreateScope();
            s.ServiceProvider.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value.EnsureFileDeleted();
        }
        catch
        {
            // best-effort cleanup
        }

        Report(label);
    }

    private static void Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<SmHealth>();
        dbe.RegisterComponentFromAccessor<SmRanked>();
        dbe.RegisterComponentFromAccessor<SmPos>();
        dbe.RegisterComponentFromAccessor<SmA>();
        dbe.RegisterComponentFromAccessor<SmB>();
        dbe.RegisterComponentFromAccessor<SmC>();
        dbe.RegisterComponentFromAccessor<SmSolo>();
        dbe.InitializeArchetypes();
    }

    private static (ServiceProvider Provider, string BundleDir) BuildProvider(string dbName, bool deleteFirst)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = dbName;
              o.DatabaseCacheSize = (ulong)(200 * 1024 * PagedMMF.PageSize);
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        var sp = sc.BuildServiceProvider();
        string bundleDir;
        using (var scope = sp.CreateScope())
        {
            var opts = scope.ServiceProvider.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value;
            bundleDir = opts.BundleDirectory;
            if (deleteFirst)
            {
                opts.EnsureFileDeleted();
            }
        }

        return (sp, bundleDir);
    }

    // ── Scenario bodies ────────────────────────────────────────────────────────────────────────────────────────

    private static EntityId Spawn(DatabaseEngine dbe, int i)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<SmPureV>(SmPureV.Health.Set(new SmHealth(100, 100)));
        tx.Commit();
        return id;
    }

    private static EntityId SpawnIdx(DatabaseEngine dbe, int i)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<SmPureVIdx>(SmPureVIdx.Ranked.Set(new SmRanked(i % 8, i)));
        tx.Commit();
        return id;
    }

    private static EntityId SpawnPure3(DatabaseEngine dbe, int i)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<SmPureV3>(
            SmPureV3.A.Set(new SmA(i, i)),
            SmPureV3.B.Set(new SmB(i)),
            SmPureV3.C.Set(new SmC(i, i, i, i)));
        tx.Commit();
        return id;
    }

    private static EntityId SpawnSolo(DatabaseEngine dbe, int i)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<SmSoloArch>(SmSoloArch.Solo.Set(new SmSolo(i % 64, i)));
        tx.Commit();
        return id;
    }

    private static EntityId SpawnMixed(DatabaseEngine dbe, int i)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<SmMixed>(SmMixed.Pos.Set(new SmPos(i, i)), SmMixed.Ranked.Set(new SmRanked(i % 8, i)));
        tx.Commit();
        return id;
    }

    /// <summary>Spawn is measured over the whole population once — it is also how the database gets built.</summary>
    private static EntityId[] MeasureSpawn(string name, DatabaseEngine dbe, Func<int, EntityId> spawnOne)
    {
        var ids = new EntityId[EntityCount];
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < EntityCount; i++)
        {
            ids[i] = spawnOne(i);
        }

        sw.Stop();
        Results.Add((name, sw.Elapsed.TotalMilliseconds * 1_000_000.0 / EntityCount));
        return ids;
    }

    /// <summary>
    /// Writes BOTH Versioned components of a cluster-backed archetype in one transaction. Reports pass/fail rather than a
    /// timing: the point is whether the cluster commit path can address two different component segments in one drain.
    /// </summary>
    private static void ProbeMultiVersionedClusterCommit(DatabaseEngine dbe)
    {
        const int N = 2_000;
        const int Rounds = 15;   // enough revision churn to push the content segments past a growth boundary — the probe missed the defect at N=64 because it never got there
        var ids = new EntityId[N];
        try
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < N; i++)
                {
                    ids[i] = tx.Spawn<SmMixed2V>(SmMixed2V.Pos.Set(new SmPos(i, i)), SmMixed2V.A.Set(new SmA(i, i)), SmMixed2V.C.Set(new SmC(i, i, i, i)));
                }

                tx.Commit();
            }

            for (var r = 0; r < Rounds; r++)
            {
                using var tx = dbe.CreateQuickTransaction();
                for (var i = 0; i < N; i++)
                {
                    var e = tx.OpenMut(ids[i]);
                    e.Write(SmMixed2V.A) = new SmA(i + 1, i + 1);
                    e.Write(SmMixed2V.C) = new SmC(i + 1, i + 1, i + 1, i + 1);
                }

                tx.Commit();
            }

            using (var tx = dbe.CreateQuickTransaction())
            {
                var ok = true;
                for (var i = 0; i < N; i++)
                {
                    var e = tx.Open(ids[i]);
                    ok &= e.Read(SmMixed2V.A).V == i + 1 && (int)e.Read(SmMixed2V.C).X == i + 1;
                }

                Sizes.Add(("probe.multiVersionedCommit.OK", ok ? 1 : 0));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  PROBE multi-Versioned cluster commit THREW: {ex.GetType().Name}: {ex.Message}");
            Sizes.Add(("probe.multiVersionedCommit.OK", -1));
        }
    }

    private static void MeasureRead(DatabaseEngine dbe, EntityId[] pureIds, EntityId[] pure3Ids)
    {
        Measure("read.point.pureV", () =>
        {
            using var tx = dbe.CreateQuickTransaction();
            var acc = 0;
            for (var i = 0; i < OpCount; i++)
            {
                acc += tx.Open(pureIds[i]).Read(SmPureV.Health).Current;
            }

            return acc;
        });

        // Opening a 3-slot Versioned entity resolves EVERY Versioned slot's chain, so this is where per-slot open cost shows.
        Measure("read.point.pureV3.oneComponent", () =>
        {
            using var tx = dbe.CreateQuickTransaction();
            var acc = 0;
            for (var i = 0; i < OpCount; i++)
            {
                acc += pure3Ids[i].RawValue != 0 ? tx.Open(pure3Ids[i]).Read(SmPureV3.A).V : 0;
            }

            return acc;
        });

        Measure("read.point.pureV3.allThree", () =>
        {
            using var tx = dbe.CreateQuickTransaction();
            var acc = 0L;
            for (var i = 0; i < OpCount; i++)
            {
                var e = tx.Open(pure3Ids[i]);
                acc += e.Read(SmPureV3.A).V + e.Read(SmPureV3.B).V + (long)e.Read(SmPureV3.C).X;
            }

            return (int)acc;
        });
    }

    private static void MeasureUpdate(DatabaseEngine dbe, EntityId[] pureIds, EntityId[] idxIds, EntityId[] mixedIds)
    {
        var round = 0;

        Measure("update.pureV.noIndex", () =>
        {
            round++;
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < OpCount; i++)
            {
                tx.OpenMut(pureIds[i]).Write(SmPureV.Health) = new SmHealth(round, 100);
            }

            tx.Commit();
            return round;
        });

        // Key unchanged: the indexed field keeps its value, so the write-path guard should skip all tree work.
        Measure("update.pureV.indexed.keyUnchanged", () =>
        {
            round++;
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < OpCount; i++)
            {
                tx.OpenMut(idxIds[i]).Write(SmPureVIdx.Ranked) = new SmRanked(i % 8, round);
            }

            tx.Commit();
            return round;
        });

        // Key changed: a real index move per entity.
        Measure("update.pureV.indexed.keyChanged", () =>
        {
            round++;
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < OpCount; i++)
            {
                tx.OpenMut(idxIds[i]).Write(SmPureVIdx.Ranked) = new SmRanked((i + round) % 8, round);
            }

            tx.Commit();
            return round;
        });

        Measure("update.mixed.CONTROL", () =>
        {
            round++;
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < OpCount; i++)
            {
                tx.OpenMut(mixedIds[i]).Write(SmMixed.Ranked) = new SmRanked(i % 8, round);
            }

            tx.Commit();
            return round;
        });
    }

    /// <summary>The three-component pure-Versioned shape: one HEAD copy per written slot.</summary>
    private static void MeasureUpdate3(DatabaseEngine dbe, EntityId[] pure3Ids)
    {
        var round = 0;

        Measure("update.pureV3.oneComponent", () =>
        {
            round++;
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < OpCount; i++)
            {
                tx.OpenMut(pure3Ids[i]).Write(SmPureV3.A) = new SmA(round, i);
            }

            tx.Commit();
            return round;
        });

        Measure("update.pureV3.allThree", () =>
        {
            round++;
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < OpCount; i++)
            {
                var e = tx.OpenMut(pure3Ids[i]);
                e.Write(SmPureV3.A) = new SmA(round, i);
                e.Write(SmPureV3.B) = new SmB(round);
                e.Write(SmPureV3.C) = new SmC(round, i, round, i);
            }

            tx.Commit();
            return round;
        });
    }

    /// <summary>Destroy releases a cluster slot instead of freeing flat records — worth a number, not an assumption.</summary>
    private static void MeasureDestroy(DatabaseEngine dbe)
    {
        const int DestroyCount = 2_000;
        var victims = new EntityId[DestroyCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < DestroyCount; i++)
            {
                victims[i] = tx.Spawn<SmPureV>(SmPureV.Health.Set(new SmHealth(1, 1)));
            }

            tx.Commit();
        }

        var sw = Stopwatch.StartNew();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < DestroyCount; i++)
            {
                tx.Destroy(victims[i]);
            }

            tx.Commit();
        }

        sw.Stop();
        Results.Add(("destroy.pureV", sw.Elapsed.TotalMilliseconds * 1_000_000.0 / DestroyCount));
    }

    private static void MeasureQuery(DatabaseEngine dbe)
    {
        Measure("query.pureV.indexed.equality", () =>
        {
            using var tx = dbe.CreateQuickTransaction();
            return tx.Query<SmPureVIdx>().WhereField<SmRanked>(r => r.Tier == 3).Count();
        }, opsPerIter: 1);

        // Range must also be on an indexed field — WhereField rejects non-indexed ones outright, so Score is not eligible.
        Measure("query.pureV.indexed.range", () =>
        {
            using var tx = dbe.CreateQuickTransaction();
            return tx.Query<SmPureVIdx>().WhereField<SmRanked>(r => r.Tier >= 4).Count();
        }, opsPerIter: 1);

        Measure("query.mixed.equality.CONTROL", () =>
        {
            using var tx = dbe.CreateQuickTransaction();
            return tx.Query<SmMixed>().WhereField<SmRanked>(r => r.Tier == 3).Count();
        }, opsPerIter: 1);
    }

    private static void MeasureEnumerateIndex(DatabaseEngine dbe)
    {
        var idxRef = dbe.GetIndexRef<SmRanked, int>(r => r.Tier);

        // Ablation: create + dispose WITHOUT iterating. Everything the new enumerator does up front — draining each
        // archetype's tree into one buffer and ordering it — lands here, so (fullScan - setupOnly) is the per-entity cost.
        Measure("enumerateIndex.setupOnly", () =>
        {
            using var tx = dbe.CreateQuickTransaction();
            using var e = tx.EnumerateIndex<SmRanked, int>(idxRef, int.MinValue, int.MaxValue);
            return 1;
        }, opsPerIter: 1);

        Measure("enumerateIndex.fullScan", () =>
        {
            using var tx = dbe.CreateQuickTransaction();
            var n = 0;
            using var e = tx.EnumerateIndex<SmRanked, int>(idxRef, int.MinValue, int.MaxValue);
            foreach (var _ in e)
            {
                n++;
            }

            return n;
        }, opsPerIter: 1);

        // Early termination — the YCSB-E shape: open a full-range scan, take 100, break. Must cost ~100 entities of work, not the
        // whole range. A materialising enumerator makes this scale with the range instead of the take, which is a complexity
        // change rather than a constant factor, so it gets its own number.
        Measure("enumerateIndex.earlyBreak100", () =>
        {
            using var tx = dbe.CreateQuickTransaction();
            var n = 0;
            using var e = tx.EnumerateIndex<SmRanked, int>(idxRef, int.MinValue, int.MaxValue);
            foreach (var _ in e)
            {
                if (++n >= 100)
                {
                    break;
                }
            }

            return n;
        }, opsPerIter: 1);

        // K=1: one archetype holds SmSolo, so the enumerator streams. Early break must be ~O(take), independent of range size.
        var soloRef = dbe.GetIndexRef<SmSolo, int>(x => x.Bucket);

        Measure("enumerateIndex.solo.earlyBreak100", () =>
        {
            using var tx = dbe.CreateQuickTransaction();
            var n = 0;
            using var e = tx.EnumerateIndex<SmSolo, int>(soloRef, int.MinValue, int.MaxValue);
            foreach (var _ in e)
            {
                if (++n >= 100)
                {
                    break;
                }
            }

            return n;
        }, opsPerIter: 1);

        Measure("enumerateIndex.solo.fullScan", () =>
        {
            using var tx = dbe.CreateQuickTransaction();
            var n = 0;
            using var e = tx.EnumerateIndex<SmSolo, int>(soloRef, int.MinValue, int.MaxValue);
            foreach (var _ in e)
            {
                n++;
            }

            return n;
        }, opsPerIter: 1);

        // How many entities the scan actually yields. SmRanked is held by BOTH SmPureVIdx and SmMixed (20 000 each), so a
        // correct fan-out returns 40 000. Anything less means the enumerator is silently missing an archetype — which
        // would make a timing comparison against it meaningless.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var n = 0;
            using var e = tx.EnumerateIndex<SmRanked, int>(idxRef, int.MinValue, int.MaxValue);
            foreach (var _ in e)
            {
                n++;
            }

            Sizes.Add(("enumerateIndex.entitiesYielded", n));
        }
    }

    // ── Harness ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Median of <see cref="Reps"/> timed repetitions after <see cref="Warmup"/> untimed ones. Median, not mean: one GC pause must not move the number.</summary>
    private static void Measure(string name, Func<int> body, int opsPerIter = OpCount)
    {
        // A scenario that throws records -1 rather than aborting the run: one broken path must not cost the other twenty
        // their numbers, and "which scenarios cannot complete" is itself a result worth having on both sides of the A/B.
        try
        {
            for (var w = 0; w < Warmup; w++)
            {
                body();
            }

            var samples = new double[Reps];
            for (var r = 0; r < Reps; r++)
            {
                var sw = Stopwatch.StartNew();
                var sink = body();
                sw.Stop();
                GC.KeepAlive(sink);
                samples[r] = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / opsPerIter;
            }

            Array.Sort(samples);
            Results.Add((name, samples[Reps / 2]));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  SCENARIO FAILED [{name}]: {ex.GetType().Name}: {ex.Message}");
            Results.Add((name, -1));
        }
    }

    private static void RecordDirectorySize(string metric, string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                Sizes.Add((metric, -1));
                return;
            }

            long total = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                total += new FileInfo(f).Length;
            }

            Sizes.Add((metric, total));
            // Four archetypes are populated (pureV, pureVIdx, pureV3, mixed). Only the first three CHANGE storage model, so
            // the interesting figure is computed against those in the write-up, not here.
            Sizes.Add(("disk.bytesPerEntity.allFour", (double)total / (EntityCount * 4)));
            Sizes.Add(("disk.convertedEntities", EntityCount * 3));
        }
        catch
        {
            Sizes.Add((metric, -1));
        }
    }

    private static void Report(string label)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"RESULTS[{label}]");
        foreach (var (scenario, ns) in Results)
        {
            sb.AppendLine($"  {scenario,-42} {ns,12:F1} ns/op");
        }

        foreach (var (metric, value) in Sizes)
        {
            sb.AppendLine($"  {metric,-42} {value,12:F1}");
        }

        Console.WriteLine(sb.ToString());

        // Machine-readable, so two runs can be diffed without re-parsing the table.
        var json = new StringBuilder();
        json.Append("{\"label\":\"").Append(label).Append("\",\"scenarios\":{");
        json.Append(string.Join(",", Results.Select(r => $"\"{r.Scenario}\":{r.NsPerOp:F2}")));
        json.Append("},\"sizes\":{");
        json.Append(string.Join(",", Sizes.Select(s => $"\"{s.Metric}\":{s.Value:F2}")));
        json.Append("}}");
        Console.WriteLine("JSON " + json);
    }
}
