using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// #1394 — archetype component-set migration, design 07 §12. The A/B harness for the open-time scenarios.
//
// Not BDN: every measured open MUTATES the file (a migration commits), so each run builds its own database and opens it once, which BDN's iteration model
// cannot express without putting the setup inside the measurement. A plain timed loop with an explicit reset, as RebuildBench does. Counts (migrations,
// foreign slots, clusters) are read from the engine, never inferred from timing.
//
// A/B: the harness is meant to be copied onto the merge base unchanged. Members that only exist on the branch are read by reflection and print "n/a" on A,
// and a set migration on A is refused (A has none): the row reads "refused".
//
// Building A: copy SchemaMigrationOpenBench.cs, SchemaMigrationOpenBench.Names.g.cs and the --schema-* / --emit-world / --recovery-replay dispatch lines of
// Program.cs onto the merge base, and NOT WalSlotTranslateBenchmarks.cs: it drives RecoveryDriver.TranslateRecord and DatabaseEngine.TranslatesWalSlots,
// which exist only on the branch, and A has no hook to measure (gate (d) compares the hook against the same loop without it, on B alone). Delete it, or
// add <Compile Remove="WalSlotTranslateBenchmarks.cs" /> to A's Typhon.Benchmark.csproj.
//
// --runs is the number of MEASURED samples: every scenario runs one more, first, labelled "(warmup)" and left out of any comparison.
//
//   --schema-migration-open [--n=2000] [--shape=sv|mixed|spatial] [--kind=field|set] [--runs=3]   (a) migration cost at open
//   --schema-open-names     [--k=16|128|512] [--runs=3]                                            (b) non-migrating open of K archetypes
//   --schema-first-fence    [--n=2000] [--kind=field|set] [--runs=3]                               (c) first fence after a spatial migration
//   --emit-world            [--n=2000] [--shape=sv|mixed|spatial]                                  (e) migrated world vs fresh world
//   --recovery-replay       [--records=1000000] [--runs=3]                                         (d) crash-open replay with no set migration
//   (f) is RebuildBench: --rebuild-bench [--n=100000]
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

#region Shape "sv": SWG-like, 12 SingleVersion components

[Component("Typhon.Bench.Smo.SvWiden", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSvWiden
{
    [Field] public int A;
    [Field] public int B;
}

/// <summary>The field migration of every shape: <see cref="SmoSvWiden"/> gains a field.</summary>
[Component("Typhon.Bench.Smo.SvWiden", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSvWidenV2
{
    [Field] public int A;
    [Field] public int B;
    [Field] public long C;
}

[Component("Typhon.Bench.Smo.Sv1", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSv1
{
    public long V;
}
[Component("Typhon.Bench.Smo.Sv2", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSv2
{
    public long V;
}
[Component("Typhon.Bench.Smo.Sv3", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSv3
{
    public long V;
}
[Component("Typhon.Bench.Smo.Sv4", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSv4
{
    public long V;
}
[Component("Typhon.Bench.Smo.Sv5", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSv5
{
    public long V;
}
[Component("Typhon.Bench.Smo.Sv6", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSv6
{
    public long V;
}
[Component("Typhon.Bench.Smo.Sv7", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSv7
{
    public long V;
}
[Component("Typhon.Bench.Smo.Sv8", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSv8
{
    public long V;
}
[Component("Typhon.Bench.Smo.Sv9", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSv9
{
    public long V;
}
[Component("Typhon.Bench.Smo.Sv10", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSv10
{
    public long V;
}
[Component("Typhon.Bench.Smo.Sv11", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSv11
{
    public long V;
}

/// <summary>The set migration of every shape: a 16-byte SingleVersion component appended ("PlayerBuffs"-shaped).</summary>
[Component("Typhon.Bench.Smo.Added", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoAdded
{
    public long A;
    public long B;
}

[Archetype(1, Name = "Smo.Sv")]
class SmoSvV1 : Archetype<SmoSvV1>
{
    public static readonly Comp<SmoSvWiden> Widen = Register<SmoSvWiden>();
    public static readonly Comp<SmoSv1> C1 = Register<SmoSv1>();
    public static readonly Comp<SmoSv2> C2 = Register<SmoSv2>();
    public static readonly Comp<SmoSv3> C3 = Register<SmoSv3>();
    public static readonly Comp<SmoSv4> C4 = Register<SmoSv4>();
    public static readonly Comp<SmoSv5> C5 = Register<SmoSv5>();
    public static readonly Comp<SmoSv6> C6 = Register<SmoSv6>();
    public static readonly Comp<SmoSv7> C7 = Register<SmoSv7>();
    public static readonly Comp<SmoSv8> C8 = Register<SmoSv8>();
    public static readonly Comp<SmoSv9> C9 = Register<SmoSv9>();
    public static readonly Comp<SmoSv10> C10 = Register<SmoSv10>();
    public static readonly Comp<SmoSv11> C11 = Register<SmoSv11>();
}

[Archetype(2, Name = "Smo.Sv")]
class SmoSvV2 : Archetype<SmoSvV2>
{
    public static readonly Comp<SmoSvWiden> Widen = Register<SmoSvWiden>();
    public static readonly Comp<SmoSv1> C1 = Register<SmoSv1>();
    public static readonly Comp<SmoSv2> C2 = Register<SmoSv2>();
    public static readonly Comp<SmoSv3> C3 = Register<SmoSv3>();
    public static readonly Comp<SmoSv4> C4 = Register<SmoSv4>();
    public static readonly Comp<SmoSv5> C5 = Register<SmoSv5>();
    public static readonly Comp<SmoSv6> C6 = Register<SmoSv6>();
    public static readonly Comp<SmoSv7> C7 = Register<SmoSv7>();
    public static readonly Comp<SmoSv8> C8 = Register<SmoSv8>();
    public static readonly Comp<SmoSv9> C9 = Register<SmoSv9>();
    public static readonly Comp<SmoSv10> C10 = Register<SmoSv10>();
    public static readonly Comp<SmoSv11> C11 = Register<SmoSv11>();
    public static readonly Comp<SmoAdded> Added = Register<SmoAdded>();
}

#endregion

#region Shape "mixed": SingleVersion + Versioned

[Component("Typhon.Bench.Smo.MxWiden", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoMxWiden
{
    [Field] public int A;
    [Field] public int B;
}

[Component("Typhon.Bench.Smo.MxWiden", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoMxWidenV2
{
    [Field] public int A;
    [Field] public int B;
    [Field] public long C;
}

[Component("Typhon.Bench.Smo.MxSv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoMxSv
{
    public long V;
}
[Component("Typhon.Bench.Smo.MxV1", 1)]
[StructLayout(LayoutKind.Sequential)]
struct SmoMxVer1
{
    public long V;
}
[Component("Typhon.Bench.Smo.MxV2", 1)]
[StructLayout(LayoutKind.Sequential)]
struct SmoMxVer2
{
    public long V;
}
[Component("Typhon.Bench.Smo.MxAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoMxAdded
{
    public long A;
    public long B;
}

[Archetype(1, Name = "Smo.Mixed")]
class SmoMxV1 : Archetype<SmoMxV1>
{
    public static readonly Comp<SmoMxWiden> Widen = Register<SmoMxWiden>();
    public static readonly Comp<SmoMxSv> Sv = Register<SmoMxSv>();
    public static readonly Comp<SmoMxVer1> Ver1 = Register<SmoMxVer1>();
    public static readonly Comp<SmoMxVer2> Ver2 = Register<SmoMxVer2>();
}

[Archetype(2, Name = "Smo.Mixed")]
class SmoMxV2 : Archetype<SmoMxV2>
{
    public static readonly Comp<SmoMxWiden> Widen = Register<SmoMxWiden>();
    public static readonly Comp<SmoMxSv> Sv = Register<SmoMxSv>();
    public static readonly Comp<SmoMxVer1> Ver1 = Register<SmoMxVer1>();
    public static readonly Comp<SmoMxVer2> Ver2 = Register<SmoMxVer2>();
    public static readonly Comp<SmoMxAdded> Added = Register<SmoMxAdded>();
}

#endregion

#region Shape "spatial": realm-keyed, two realms

[Component("Typhon.Bench.Smo.SpPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSpPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    [RealmKey]
    public ushort Realm;

    [Field] public ushort Pad;
    [Field] public int Tag;
}

[Component("Typhon.Bench.Smo.SpWiden", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSpWiden
{
    [Field] public int A;
    [Field] public int B;
}

[Component("Typhon.Bench.Smo.SpWiden", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSpWidenV2
{
    [Field] public int A;
    [Field] public int B;
    [Field] public long C;
}

[Component("Typhon.Bench.Smo.SpAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SmoSpAdded
{
    public long A;
    public long B;
}

[Archetype(1, Name = "Smo.Spatial", ClusterDurability = ClusterDurability.Checkpoint)]
class SmoSpV1 : Archetype<SmoSpV1>
{
    public static readonly Comp<SmoSpPos> Pos = Register<SmoSpPos>();
    public static readonly Comp<SmoSpWiden> Widen = Register<SmoSpWiden>();
}

[Archetype(2, Name = "Smo.Spatial", ClusterDurability = ClusterDurability.Checkpoint)]
class SmoSpV2 : Archetype<SmoSpV2>
{
    public static readonly Comp<SmoSpPos> Pos = Register<SmoSpPos>();
    public static readonly Comp<SmoSpWiden> Widen = Register<SmoSpWiden>();
    public static readonly Comp<SmoSpAdded> Added = Register<SmoSpAdded>();
}

#endregion

#region Crash-open replay: a Versioned component, one Slot record per update

[Component("Typhon.Bench.Smo.RpVal", 1)]
[StructLayout(LayoutKind.Sequential)]
struct SmoRpVal
{
    public long V;
}

[Archetype(1, Name = "Smo.Replay")]
class SmoRpArch : Archetype<SmoRpArch>
{
    public static readonly Comp<SmoRpVal> Val = Register<SmoRpVal>();
}

#endregion

internal static class SchemaMigrationOpenBench
{
    private enum Shape { Sv, Mixed, Spatial }

    private enum Kind { Field, Set }

    private const float World = 4_000f;
    private const float CellSize = 100f;
    private const int Batch = 10_000;

    // ── Dispatch ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>True when <paramref name="args"/> names one of this harness's flags, which it then ran.</summary>
    public static bool TryRun(string[] args)
    {
        var n = IntArg(args, "--n=", 2_000);
        var runs = IntArg(args, "--runs=", 3);
        var shape = StringArg(args, "--shape=", "sv") switch
        {
            "mixed" => Shape.Mixed,
            "spatial" => Shape.Spatial,
            _ => Shape.Sv,
        };
        var kind = StringArg(args, "--kind=", "set") == "field" ? Kind.Field : Kind.Set;

        if (Array.IndexOf(args, "--schema-migration-open") >= 0)
        {
            RunMigrationOpen(n, shape, kind, runs);
            return true;
        }

        if (Array.IndexOf(args, "--schema-open-names") >= 0)
        {
            RunOpenNames(IntArg(args, "--k=", 16), runs);
            return true;
        }

        if (Array.IndexOf(args, "--schema-first-fence") >= 0)
        {
            RunFirstFence(n, kind, runs);
            return true;
        }

        if (Array.IndexOf(args, "--emit-world") >= 0)
        {
            RunEmitWorld(n, shape);
            return true;
        }

        if (Array.IndexOf(args, "--recovery-replay") >= 0)
        {
            RunRecoveryReplay(IntArg(args, "--records=", 1_000_000), runs);
            return true;
        }

        return false;
    }

    private static int IntArg(string[] args, string prefix, int fallback)
    {
        foreach (var a in args)
        {
            if (a.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(a.AsSpan(prefix.Length), out var v))
            {
                return v;
            }
        }

        return fallback;
    }

    private static string StringArg(string[] args, string prefix, string fallback)
    {
        foreach (var a in args)
        {
            if (a.StartsWith(prefix, StringComparison.Ordinal))
            {
                return a[prefix.Length..];
            }
        }

        return fallback;
    }

    // ── (a) Migration cost at open ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void RunMigrationOpen(int n, Shape shape, Kind kind, int runs)
    {
        Console.WriteLine();
        Console.WriteLine($"  (a) schema migration at open — shape {shape}, {kind} migration, N {n:N0}, {runs} measured run(s) + 1 warmup");
        for (var run = 0; run < runs + 1; run++)
        {
            using var dir = new BenchDir();
            using (var seed = Engine.Open(dir.Path))
            {
                Register(seed.Dbe, shape, Kind.Field, v2: false);
                seed.Dbe.InitializeArchetypes();
                Seed(seed.Dbe, shape, n);
            }

            using var open = Engine.Open(dir.Path, capture: true);
            Register(open.Dbe, shape, kind, v2: true);
            // Process-wide: the rebuilds fan out to workers, whose allocations a per-thread counter would miss.
            var alloc = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            string outcome;
            try
            {
                open.Dbe.InitializeArchetypes();
                outcome = "ok";
            }
            catch (InvalidOperationException ex)
            {
                outcome = "refused: " + FirstLine(ex.Message);
            }

            var initMs = sw.Elapsed.TotalMilliseconds;
            var bytesPerEntity = (GC.GetTotalAllocatedBytes(true) - alloc) / (double)n;
            Console.WriteLine($"    run {run}: construct {open.ConstructMs,8:F1} ms   InitializeArchetypes {initMs,8:F1} ms   "
                              + $"managed {bytesPerEntity,7:F1} B/entity   "
                              + $"re-placed {DescribeReplaced(open.Dbe)}   set migrations {DescribeSetMigrations(open.Dbe)}   [{outcome}]"
                              + (run == 0 ? "   (warmup)" : string.Empty));
            open.PrintCaptured("      ");
        }
    }

    // ── (b) Non-migrating open: the cost of reading the stored names ────────────────────────────────────────────────────────────────────────────────────

    private static void RunOpenNames(int k, int runs)
    {
        k = Math.Clamp(k, 1, SmoNamesArchetypes.Count);
        Console.WriteLine();
        Console.WriteLine($"  (b) non-migrating clean reopen — {k} archetypes (each 4 entities), {runs} measured run(s) + 1 warmup");
        // A proxy: the time is the WHOLE InitializeArchetypes, of which matching each stored row by name is one part; the other 512 - k generated
        // archetypes are skipped for an unresolved slot at the same cost on A and B, so the A/B difference is the per-archetype cost the gate is about.
        Console.WriteLine($"      times the whole InitializeArchetypes as an A/B proxy ({SmoNamesArchetypes.Count - k} other archetypes skipped unresolved)");
        using var dir = new BenchDir();
        using (var seed = Engine.Open(dir.Path))
        {
            SmoNamesArchetypes.RegisterFirst(seed.Dbe, k);
            seed.Dbe.InitializeArchetypes();
            using var tx = seed.Dbe.CreateQuickTransaction();
            for (var a = 0; a < k; a++)
            {
                for (var e = 0; e < 4; e++)
                {
                    SmoNamesArchetypes.Spawn(tx, a, a * 4 + e);
                }
            }

            tx.Commit();
        }

        for (var run = 0; run < runs + 1; run++)
        {
            using var open = Engine.Open(dir.Path, capture: true);
            SmoNamesArchetypes.RegisterFirst(open.Dbe, k);
            var sw = Stopwatch.StartNew();
            open.Dbe.InitializeArchetypes();
            var ms = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"    run {run}: InitializeArchetypes {ms,8:F3} ms   = {ms * 1000 / k,8:F2} us/archetype"
                              + (run == 0 ? "   (warmup)" : string.Empty));
        }
    }

    // ── (c) First fence after a spatial migration ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void RunFirstFence(int n, Kind kind, int runs)
    {
        Console.WriteLine();
        Console.WriteLine($"  (c) first fence after a realm-keyed spatial {kind} migration — N {n:N0}, {runs} measured run(s) + 1 warmup");
        for (var run = 0; run < runs + 1; run++)
        {
            using var dir = new BenchDir();
            using (var seed = Engine.Open(dir.Path))
            {
                Register(seed.Dbe, Shape.Spatial, Kind.Field, v2: false);
                seed.Dbe.InitializeArchetypes();
                Seed(seed.Dbe, Shape.Spatial, n);
            }

            double migratedFence;
            double migratedSecond;
            string migrated;
            using (var open = Engine.Open(dir.Path))
            {
                Register(open.Dbe, Shape.Spatial, kind, v2: true);
                try
                {
                    open.Dbe.InitializeArchetypes();
                }
                catch (InvalidOperationException ex)
                {
                    Console.WriteLine($"    run {run}: refused — {FirstLine(ex.Message)}");
                    continue;
                }

                var cs = SpatialState(open.Dbe, kind);
                var foreign = cs.LastRebuildForeignCellSlots;
                migratedFence = TimedFence(open.Dbe, kind, 3);
                migrated = $"foreign cells {foreign}, realms {cs.LastRebuildForeignRealmSlots}, fence migrations {cs.LastTickMigrationCount}";
                migratedSecond = TimedFence(open.Dbe, kind, 4);
            }

            using var plain = Engine.Open(dir.Path);
            Register(plain.Dbe, Shape.Spatial, kind, v2: true);
            plain.Dbe.InitializeArchetypes();
            var plainCs = SpatialState(plain.Dbe, kind);
            var plainFence = TimedFence(plain.Dbe, kind, 5);
            var plainMigrations = plainCs.LastTickMigrationCount;
            var plainSecond = TimedFence(plain.Dbe, kind, 6);

            // The first fence is the gated one (its migration count, and its time against the plain reopen's first fence); the second is printed beside it
            // because a first fence's time also carries whatever the open left running (the schema commit's checkpoint), which is noise to this question.
            Console.WriteLine($"    run {run}: after migration — first fence {migratedFence,8:F3} ms, second {migratedSecond,8:F3} ms, {migrated}");
            Console.WriteLine($"           plain reopen    — first fence {plainFence,8:F3} ms, second {plainSecond,8:F3} ms, foreign cells "
                              + $"{plainCs.LastRebuildForeignCellSlots}, fence migrations {plainMigrations}   first-fence ratio {migratedFence / plainFence:F2}"
                              + (run == 0 ? "   (warmup)" : string.Empty));
        }
    }

    /// <summary>The first fence of the open, timed, through the archetype class the open consumed.</summary>
    private static double TimedFence(DatabaseEngine dbe, Kind kind, long tick) =>
        kind == Kind.Set ? TimedFence<SmoSpV2>(dbe, SmoSpV2.Pos, tick) : TimedFence<SmoSpV1>(dbe, SmoSpV1.Pos, tick);

    /// <summary>Moves every hundredth entity by a hundredth of a unit (never across a cell), then times one fence.</summary>
    private static double TimedFence<TArch>(DatabaseEngine dbe, Comp<SmoSpPos> posHandle, long tick) where TArch : Archetype<TArch>, new()
    {
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var i = 0;
            foreach (var e in tx.Query<TArch>())
            {
                if (i++ % 100 == 0)
                {
                    ids.Add(e.Id);
                }
            }

            foreach (var id in ids)
            {
                var mut = tx.OpenMut(id);
                var pos = mut.Read(posHandle);
                pos.Bounds.MinX += 0.01f;
                pos.Bounds.MaxX += 0.01f;
                mut.Set(posHandle, pos);
            }

            tx.Commit();
        }

        var sw = Stopwatch.StartNew();
        dbe.WriteTickFence(tick);
        return sw.Elapsed.TotalMilliseconds;
    }

    // ── (e) Migrated world vs a world created fresh at the new schema ───────────────────────────────────────────────────────────────────────────────────

    private static void RunEmitWorld(int n, Shape shape)
    {
        Console.WriteLine();
        Console.WriteLine($"  (e) migrated world vs fresh world — shape {shape}, N {n:N0} (set migration)");

        using var migratedDir = new BenchDir();
        using (var seed = Engine.Open(migratedDir.Path))
        {
            Register(seed.Dbe, shape, Kind.Field, v2: false);
            seed.Dbe.InitializeArchetypes();
            Seed(seed.Dbe, shape, n);
        }

        using var freshDir = new BenchDir();
        using (var seed = Engine.Open(freshDir.Path))
        {
            Register(seed.Dbe, shape, Kind.Set, v2: true);
            seed.Dbe.InitializeArchetypes();
            Seed(seed.Dbe, shape, n, v2: true);
        }

        WorldMetrics migrated;
        using (var open = Engine.Open(migratedDir.Path))
        {
            Register(open.Dbe, shape, Kind.Set, v2: true);
            try
            {
                open.Dbe.InitializeArchetypes();
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"    refused — {FirstLine(ex.Message)}");
                return;
            }

            // Twice, the first pass discarded: it carries the JIT and whatever the open left running (the schema commit's checkpoint).
            Measure(open.Dbe, shape, n, 10);
            migrated = Measure(open.Dbe, shape, n, 11);
        }

        WorldMetrics fresh;
        using (var open = Engine.Open(freshDir.Path))
        {
            Register(open.Dbe, shape, Kind.Set, v2: true);
            open.Dbe.InitializeArchetypes();
            Measure(open.Dbe, shape, n, 10);
            fresh = Measure(open.Dbe, shape, n, 11);
        }

        Console.WriteLine("    (second of two passes per world)");
        Console.WriteLine($"    {"",-9} {"clusters",9} {"fill",7} {"cell query ms",14} {"iterate ms",11} {"fence ms",9}");
        Console.WriteLine($"    {"migrated",-9} {migrated.Clusters,9} {migrated.Fill,7:P0} {migrated.QueryMs,14:F3} {migrated.IterateMs,11:F3} "
                          + $"{migrated.FenceMs,9:F3}");
        Console.WriteLine($"    {"fresh",-9} {fresh.Clusters,9} {fresh.Fill,7:P0} {fresh.QueryMs,14:F3} {fresh.IterateMs,11:F3} {fresh.FenceMs,9:F3}");
        var queryRatio = shape == Shape.Spatial ? (migrated.QueryMs / fresh.QueryMs).ToString("F2") : "n/a";
        Console.WriteLine($"    {"ratio",-9} {(double)migrated.Clusters / fresh.Clusters,9:F2} {"",7} {queryRatio,14} "
                          + $"{migrated.IterateMs / fresh.IterateMs,11:F2} {migrated.FenceMs / fresh.FenceMs,9:F2}");
    }

    private readonly record struct WorldMetrics(int Clusters, double Fill, double QueryMs, double IterateMs, double FenceMs);

    private static WorldMetrics Measure(DatabaseEngine dbe, Shape shape, int n, long tick)
    {
        var id = shape switch
        {
            Shape.Sv => Archetype<SmoSvV2>.Metadata.ArchetypeId,
            Shape.Mixed => Archetype<SmoMxV2>.Metadata.ArchetypeId,
            _ => Archetype<SmoSpV2>.Metadata.ArchetypeId,
        };
        var cs = dbe._archetypeStates[id].ClusterState;
        var clusters = cs.ActiveClusterCount;
        var fill = n / (double)(clusters * cs.Layout.ClusterSize);

        var queryMs = 0.0;
        if (shape == Shape.Spatial)
        {
            using var epoch = EpochGuard.Enter(dbe.EpochManager);

            // Timed: one query per grid cell, the boxes tiling the world with no gap. Closed boxes, so an entity on a shared edge is returned by every box
            // touching it — the same entities in both worlds, so the migrated/fresh ratio still compares like with like. The count is not taken from these.
            var sw = Stopwatch.StartNew();
            var cellHits = 0;
            foreach (var realm in new ushort[] { 1, 2 })
            {
                for (var cx = 0; cx < World / CellSize; cx++)
                {
                    for (var cy = 0; cy < World / CellSize; cy++)
                    {
                        var box = new AABB2F { MinX = cx * CellSize, MinY = cy * CellSize, MaxX = (cx + 1) * CellSize, MaxY = (cy + 1) * CellSize };
                        foreach (var _ in dbe.ClusterSpatialQuery<SmoSpV2>(new RealmId(realm)).AABB(box))
                        {
                            cellHits++;
                        }
                    }
                }
            }

            queryMs = sw.Elapsed.TotalMilliseconds;

            // Untimed: the completeness check, one world-sized box per realm — each entity counted exactly once.
            var hits = 0;
            foreach (var realm in new ushort[] { 1, 2 })
            {
                var world = new AABB2F { MinX = 0, MinY = 0, MaxX = World, MaxY = World };
                foreach (var _ in dbe.ClusterSpatialQuery<SmoSpV2>(new RealmId(realm)).AABB(world))
                {
                    hits++;
                }
            }

            if (hits != n || cellHits < n)
            {
                Console.WriteLine($"    !! spatial queries found {hits} of {n} entities ({cellHits} hits over the cell boxes)");
            }
        }

        double iterateMs;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var sw = Stopwatch.StartNew();
            var count = shape switch
            {
                Shape.Sv => Iterate<SmoSvV2>(tx),
                Shape.Mixed => Iterate<SmoMxV2>(tx),
                _ => Iterate<SmoSpV2>(tx),
            };
            iterateMs = sw.Elapsed.TotalMilliseconds;
            if (count != n)
            {
                Console.WriteLine($"    !! iteration found {count} of {n} entities");
            }
        }

        var fence = Stopwatch.StartNew();
        dbe.WriteTickFence(tick);
        return new WorldMetrics(clusters, fill, queryMs, iterateMs, fence.Elapsed.TotalMilliseconds);
    }

    private static int Iterate<TArch>(Transaction tx) where TArch : Archetype<TArch>, new()
    {
        var count = 0;
        foreach (var _ in tx.Query<TArch>())
        {
            count++;
        }

        return count;
    }

    // ── (d) Crash-open replay with no set migration ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void RunRecoveryReplay(int records, int runs)
    {
        var entities = Math.Clamp(records / 10, 1, 100_000);
        var rounds = Math.Max(1, records / entities);
        Console.WriteLine();
        Console.WriteLine($"  (d) crash-open WAL replay — {entities:N0} entities x {rounds} updates = {entities * rounds:N0} Slot records, no set migration, "
                          + $"{runs} measured run(s) + 1 warmup");
        for (var run = 0; run < runs + 1; run++)
        {
            using var dir = new BenchDir();
            using (var seed = Engine.Open(dir.Path, noCheckpoints: true))
            {
                seed.Dbe.RegisterComponentFromAccessor<SmoRpVal>();
                seed.Dbe.InitializeArchetypes();
                var ids = new EntityId[entities];
                for (var s = 0; s < entities; s += Batch)
                {
                    using var tx = seed.Dbe.CreateQuickTransaction();
                    for (var i = s; i < Math.Min(entities, s + Batch); i++)
                    {
                        ids[i] = tx.Spawn<SmoRpArch>(SmoRpArch.Val.Set(new SmoRpVal { V = i }));
                    }

                    tx.Commit();
                }

                for (var r = 1; r <= rounds; r++)
                {
                    for (var s = 0; s < entities; s += Batch)
                    {
                        var last = r == rounds && s + Batch >= entities;
                        using var tx = seed.Dbe.CreateQuickTransaction(last ? DurabilityMode.Immediate : DurabilityMode.Deferred);
                        for (var i = s; i < Math.Min(entities, s + Batch); i++)
                        {
                            tx.OpenMut(ids[i]).Set(SmoRpArch.Val, new SmoRpVal { V = (long)r * entities + i });
                        }

                        tx.Commit();
                    }
                }

                seed.Dbe.SimulateHardCrash();
            }

            using var open = Engine.Open(dir.Path, capture: true, noCheckpoints: true);
            open.Dbe.RegisterComponentFromAccessor<SmoRpVal>();
            var sw = Stopwatch.StartNew();
            open.Dbe.InitializeArchetypes();
            var replayMs = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"    run {run}: construct (WAL scan) {open.ConstructMs,8:F1} ms   InitializeArchetypes (replay) {replayMs,8:F1} ms   total "
                              + $"{open.ConstructMs + replayMs,8:F1} ms{(run == 0 ? "   (warmup)" : string.Empty)}");
            open.PrintCaptured("      ");
        }
    }

    // ── Shapes ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a shape's components. Revision 1 (<paramref name="v2"/> false) registers the base set. Revision 2 of a FIELD migration registers the widened
    /// component in place of the base one; of a SET migration, the base set plus the added component, which is what makes the revision-2 class resolve.
    /// </summary>
    private static void Register(DatabaseEngine dbe, Shape shape, Kind kind, bool v2)
    {
        var widen = v2 && kind == Kind.Field;
        var add = v2 && kind == Kind.Set;
        switch (shape)
        {
            case Shape.Sv:
                if (widen)
                {
                    dbe.RegisterComponentFromAccessor<SmoSvWidenV2>();
                }
                else
                {
                    dbe.RegisterComponentFromAccessor<SmoSvWiden>();
                }

                dbe.RegisterComponentFromAccessor<SmoSv1>();
                dbe.RegisterComponentFromAccessor<SmoSv2>();
                dbe.RegisterComponentFromAccessor<SmoSv3>();
                dbe.RegisterComponentFromAccessor<SmoSv4>();
                dbe.RegisterComponentFromAccessor<SmoSv5>();
                dbe.RegisterComponentFromAccessor<SmoSv6>();
                dbe.RegisterComponentFromAccessor<SmoSv7>();
                dbe.RegisterComponentFromAccessor<SmoSv8>();
                dbe.RegisterComponentFromAccessor<SmoSv9>();
                dbe.RegisterComponentFromAccessor<SmoSv10>();
                dbe.RegisterComponentFromAccessor<SmoSv11>();

                if (add)
                {
                    dbe.RegisterComponentFromAccessor<SmoAdded>();
                }

                break;

            case Shape.Mixed:
                if (widen)
                {
                    dbe.RegisterComponentFromAccessor<SmoMxWidenV2>();
                }
                else
                {
                    dbe.RegisterComponentFromAccessor<SmoMxWiden>();
                }

                dbe.RegisterComponentFromAccessor<SmoMxSv>();
                dbe.RegisterComponentFromAccessor<SmoMxVer1>();
                dbe.RegisterComponentFromAccessor<SmoMxVer2>();

                if (add)
                {
                    dbe.RegisterComponentFromAccessor<SmoMxAdded>();
                }

                break;

            default:
                dbe.RegisterComponentFromAccessor<SmoSpPos>();
                if (widen)
                {
                    dbe.RegisterComponentFromAccessor<SmoSpWidenV2>();
                }
                else
                {
                    dbe.RegisterComponentFromAccessor<SmoSpWiden>();
                }


                if (add)
                {
                    dbe.RegisterComponentFromAccessor<SmoSpAdded>();
                }

                var grid = SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(World, World), CellSize, reclusterBudgetMs: 0f);
                dbe.ConfigureRealms(4);
                dbe.Realms.Register(new RealmId(1), RealmConfig.SimulatedAlways(grid));
                dbe.Realms.Register(new RealmId(2), RealmConfig.SimulatedAlways(grid));
                break;
        }
    }

    /// <summary>
    /// Spawns <paramref name="n"/> entities of the shape (revision 1, or revision 2 for a fresh world), then runs a fence for a spatial shape.
    /// </summary>
    private static void Seed(DatabaseEngine dbe, Shape shape, int n, bool v2 = false)
    {
        var rnd = new Random(1394);
        for (var s = 0; s < n; s += Batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = s; i < Math.Min(n, s + Batch); i++)
            {
                var w = new SmoSvWiden { A = i, B = -i };
                switch (shape)
                {
                    case Shape.Sv when v2:
                        tx.Spawn<SmoSvV2>(SmoSvV2.Widen.Set(w), SmoSvV2.C1.Set(new SmoSv1 { V = i }), SmoSvV2.C2.Set(new SmoSv2 { V = i }),
                            SmoSvV2.C3.Set(new SmoSv3 { V = i }), SmoSvV2.C4.Set(new SmoSv4 { V = i }), SmoSvV2.C5.Set(new SmoSv5 { V = i }),
                            SmoSvV2.C6.Set(new SmoSv6 { V = i }), SmoSvV2.C7.Set(new SmoSv7 { V = i }), SmoSvV2.C8.Set(new SmoSv8 { V = i }),
                            SmoSvV2.C9.Set(new SmoSv9 { V = i }), SmoSvV2.C10.Set(new SmoSv10 { V = i }), SmoSvV2.C11.Set(new SmoSv11 { V = i }));
                        break;
                    case Shape.Sv:
                        tx.Spawn<SmoSvV1>(SmoSvV1.Widen.Set(w), SmoSvV1.C1.Set(new SmoSv1 { V = i }), SmoSvV1.C2.Set(new SmoSv2 { V = i }),
                            SmoSvV1.C3.Set(new SmoSv3 { V = i }), SmoSvV1.C4.Set(new SmoSv4 { V = i }), SmoSvV1.C5.Set(new SmoSv5 { V = i }),
                            SmoSvV1.C6.Set(new SmoSv6 { V = i }), SmoSvV1.C7.Set(new SmoSv7 { V = i }), SmoSvV1.C8.Set(new SmoSv8 { V = i }),
                            SmoSvV1.C9.Set(new SmoSv9 { V = i }), SmoSvV1.C10.Set(new SmoSv10 { V = i }), SmoSvV1.C11.Set(new SmoSv11 { V = i }));
                        break;
                    case Shape.Mixed when v2:
                        tx.Spawn<SmoMxV2>(SmoMxV2.Widen.Set(new SmoMxWiden { A = i, B = -i }), SmoMxV2.Sv.Set(new SmoMxSv { V = i }),
                            SmoMxV2.Ver1.Set(new SmoMxVer1 { V = i }), SmoMxV2.Ver2.Set(new SmoMxVer2 { V = i }));
                        break;
                    case Shape.Mixed:
                        tx.Spawn<SmoMxV1>(SmoMxV1.Widen.Set(new SmoMxWiden { A = i, B = -i }), SmoMxV1.Sv.Set(new SmoMxSv { V = i }),
                            SmoMxV1.Ver1.Set(new SmoMxVer1 { V = i }), SmoMxV1.Ver2.Set(new SmoMxVer2 { V = i }));
                        break;
                    default:
                        var x = (float)rnd.NextDouble() * (World - 1);
                        var y = (float)rnd.NextDouble() * (World - 1);
                        var pos = new SmoSpPos { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = (ushort)(1 + (i & 1)), Tag = i };
                        if (v2)
                        {
                            tx.Spawn<SmoSpV2>(SmoSpV2.Pos.Set(pos), SmoSpV2.Widen.Set(new SmoSpWiden { A = i, B = -i }));
                        }
                        else
                        {
                            tx.Spawn<SmoSpV1>(SmoSpV1.Pos.Set(pos), SmoSpV1.Widen.Set(new SmoSpWiden { A = i, B = -i }));
                        }

                        break;
                }
            }

            tx.Commit();
        }

        if (shape == Shape.Spatial)
        {
            dbe.WriteTickFence(1);
        }
    }

    private static ArchetypeClusterState SpatialState(DatabaseEngine dbe, Kind kind) =>
        dbe._archetypeStates[kind == Kind.Set ? Archetype<SmoSpV2>.Metadata.ArchetypeId : Archetype<SmoSpV1>.Metadata.ArchetypeId].ClusterState;

    // ── Branch-only members, read by reflection so the file builds on the merge base ────────────────────────────────────────────────────────────────────

    private static string DescribeSetMigrations(DatabaseEngine dbe)
    {
        var value = typeof(DatabaseEngine).GetProperty("LastOpenArchetypeSetMigrations", BindingFlags.Public | BindingFlags.Instance)?.GetValue(dbe);
        if (value is not System.Collections.IEnumerable list)
        {
            return "n/a";
        }

        var parts = new List<string>();
        foreach (var report in list)
        {
            parts.Add(report.ToString());
        }

        return parts.Count == 0 ? "none" : string.Join("; ", parts);
    }

    /// <summary>
    /// How many archetypes the open re-placed (a field or a set migration): those whose EntityMap it allocated afresh rather than reloaded, the production
    /// state every re-placement leaves (on A too). The branch's placement seam is a test flag, and setting it would add its cost to B alone.
    /// </summary>
    private static string DescribeReplaced(DatabaseEngine dbe)
    {
        var replaced = 0;
        foreach (var state in dbe._archetypeStates)
        {
            if (state?.ClusterState != null && !state.EntityMapLoaded)
            {
                replaced++;
            }
        }

        return replaced.ToString();
    }

    private static string FirstLine(string message)
    {
        var cut = message.IndexOf('.');
        return cut > 0 ? message[..(cut + 1)] : message;
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A fresh database directory, deleted on dispose.</summary>
    private sealed class BenchDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "typhon-smo-bench", Guid.NewGuid().ToString("N"));

        public BenchDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort: a handle the OS has not released yet, or a read-only file the engine left, leaves the directory for the temp cleaner.
            }
        }
    }

    /// <summary>One engine over a directory: its own service provider and scope, so each open is a real reopen of the file.</summary>
    private sealed class Engine : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;
        private readonly OpenLog _log;

        public DatabaseEngine Dbe { get; }

        /// <summary>Engine construction (system-schema load, WAL scan on a crash open).</summary>
        public double ConstructMs { get; }

        private Engine(ServiceProvider provider, IServiceScope scope, DatabaseEngine dbe, OpenLog log, double constructMs)
        {
            _provider = provider;
            _scope = scope;
            Dbe = dbe;
            _log = log;
            ConstructMs = constructMs;
        }

        public static Engine Open(string dir, bool capture = false, bool noCheckpoints = false)
        {
            var log = capture ? new OpenLog() : null;
            var services = new ServiceCollection();
            services
                .AddLogging(b =>
                {
                    b.SetMinimumLevel(LogLevel.Error);
                    if (log != null)
                    {
                        b.AddProvider(log);
                        b.AddFilter<OpenLog>("Typhon.Engine.DatabaseEngine", LogLevel.Information);
                    }
                })
                .AddResourceRegistry()
                .AddMemoryAllocator()
                .AddEpochManager()
                .AddHighResolutionSharedTimer()
                .AddDeadlineWatchdog()
                .AddScopedManagedPagedMemoryMappedFile(o =>
                {
                    o.DatabaseName = "smo";
                    o.DatabaseDirectory = dir;
                    o.DatabaseCacheSize = 1024UL * 1024 * 1024;
                })
                .AddScopedDatabaseEngine(o =>
                {
                    o.Wal = new WalWriterOptions { UseFUA = false };
                    if (noCheckpoints)
                    {
                        o.Resources.CheckpointIntervalMs = int.MaxValue;
                    }
                });

            var provider = services.BuildServiceProvider();
            var scope = provider.CreateScope();
            var sw = Stopwatch.StartNew();
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            return new Engine(provider, scope, dbe, log, sw.Elapsed.TotalMilliseconds);
        }

        public void PrintCaptured(string indent) => _log?.Print(indent);

        public void Dispose()
        {
            Dbe.Dispose();
            _scope.Dispose();
            _provider.Dispose();
        }
    }

    /// <summary>Keeps the engine's open-time breakdown lines ("Open: InitializeArchetypes …", "Open: WAL recovery …", the migration line).</summary>
    private sealed class OpenLog : ILoggerProvider, ILogger
    {
        private readonly List<string> _lines = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var line = formatter(state, exception);
            if (line.StartsWith("Open: InitializeArchetypes", StringComparison.Ordinal) || line.StartsWith("Open: WAL recovery", StringComparison.Ordinal)
                || line.Contains("migrated its component set", StringComparison.Ordinal))
            {
                lock (_lines)
                {
                    _lines.Add(line);
                }
            }
        }

        public void Print(string indent)
        {
            lock (_lines)
            {
                foreach (var line in _lines)
                {
                    Console.WriteLine(indent + line);
                }
            }
        }

        public void Dispose() { }
    }
}
