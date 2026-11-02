using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.HugeScan.Versioned", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct HsVersioned
{
    [Index] public long Seq;
    [Index(AllowMultiple = true)] public int Group;
    public int Reserved;
    public long Value;
    [Index] public long Shuffled;    // unique, and uncorrelated with where the row is stored
    public fixed byte Payload[80];   // 112 bytes in all: the most a spawned component may carry
}

[Component("Typhon.Test.HugeScan.Single", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct HsSingle
{
    [Index] public long Seq;
    [Index(AllowMultiple = true)] public int Group;
    public int Reserved;
    public long Value;
    [Index] public long Shuffled;
    public fixed byte Payload[80];
}

/// <summary>A component held by two archetypes: its index reads fan out over two trees, and EnumerateIndex materialises the range.</summary>
[Component("Typhon.Test.HugeScan.Shared", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct HsShared
{
    [Index] public long Seq;
    [Index(AllowMultiple = true)] public long Near;   // near-distinct keys across two archetypes: the fan-out's buffer path
    public long Value;
    public fixed byte Payload[88];
}

/// <summary>Every row under ONE key of an AllowMultiple index: a value buffer as long as the archetype.</summary>
[Component("Typhon.Test.HugeScan.Flat", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public struct HsFlat
{
    [Index(AllowMultiple = true)] public int Bucket;
    public int Reserved;
    public long Seq;
}

[Component("Typhon.Test.HugeScan.Child", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public struct HsChild
{
    [Index(AllowMultiple = true), ForeignKey(typeof(HsVersioned))] public long ParentId;
    public long Seq;
}

[Component("Typhon.Test.HugeScan.Hot", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct HsHot
{
    public long Key;
    public long Value;
    public fixed byte Payload[96];
}

[Component("Typhon.Test.HugeScan.Spatial", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct HsSpatial
{
    [Field]
    [SpatialIndex]
    public AABB3F Bounds;

    [Field]
    public long Seq;
}

[Archetype]
class HsVerArch : Archetype<HsVerArch>
{
    public static readonly Comp<HsVersioned> Row = Register<HsVersioned>();
}

[Archetype]
class HsSvArch : Archetype<HsSvArch>
{
    public static readonly Comp<HsSingle> Row = Register<HsSingle>();
}

[Archetype]
class HsFanAArch : Archetype<HsFanAArch>
{
    public static readonly Comp<HsShared> Row = Register<HsShared>();
}

[Archetype]
class HsFanBArch : Archetype<HsFanBArch>
{
    public static readonly Comp<HsShared> Row = Register<HsShared>();
}

[Archetype]
class HsFlatArch : Archetype<HsFlatArch>
{
    public static readonly Comp<HsFlat> Row = Register<HsFlat>();
}

[Archetype]
class HsChildArch : Archetype<HsChildArch>
{
    public static readonly Comp<HsChild> Row = Register<HsChild>();
}

[Archetype]
class HsHotArch : Archetype<HsHotArch>
{
    public static readonly Comp<HsHot> Row = Register<HsHot>();
}

[Archetype]
class HsSpatialArch : Archetype<HsSpatialArch>
{
    public static readonly Comp<HsSpatial> Row = Register<HsSpatial>();
}

/// <summary>
/// Challenges every public way of walking an archetype against a database many times the page cache: does it finish, what does it hold, what does it do to
/// the rest of the cache, what does it cost in managed memory, and what does it do to whoever else is using the engine.
/// </summary>
/// <remarks>
/// <para>
/// Built once by <see cref="Build"/>; each scenario then runs in its own process, on an APFS clone of the database, driven by
/// <c>scripts/huge-scan-assessment.py</c>: a crash, a hang or a write in one cannot touch the next. <c>TYPHON_HS_DIR</c> holds the base database and
/// the results, <c>TYPHON_HS_ROWS</c> sizes the build, <c>TYPHON_HS_CACHE_MIB</c> the cache of the run (default 64) and <c>TYPHON_HS_LIVE=1</c> runs a
/// writer and a reader beside the scan.
/// </para>
/// <para>
/// A scenario passes when it finishes, visits every row exactly once, never holds more than <see cref="PinBudgetFraction"/> of the cache by epoch, and
/// leaves at least <see cref="HotSurvivalFloor"/> of the hot set resident. Those last two are the assertions that fail today on purpose where the API
/// falls short: the report in <c>results.jsonl</c> is the deliverable, and a red case here is a measured gap, not a regression.
/// </para>
/// </remarks>
[Explicit("On-demand assessment: needs a database many times the cache, built by Build and driven by scripts/huge-scan-assessment.py. Never run by CI.")]
[Category("Manual")]
[NonParallelizable]
[TestFixture]
public unsafe class HugeScanAssessmentTests
{
    private const double PinBudgetFraction = 0.125;
    private const double HotSurvivalFloor = 0.5;
    private const int HotRows = 32_000;
    private const int FanRowsDivisor = 8;
    private const int SpatialRowsDivisor = 4;
    private const int Groups = 1_000;
    private const float WorldExtent = 10_000f;
    private const int KeysetPage = 10_000;
    private const int UpdateBatch = 10_000;

    private static readonly string Dir = Environment.GetEnvironmentVariable("TYPHON_HS_DIR") ?? Path.Combine(Path.GetTempPath(), "typhon-hs");
    private static readonly long RowsToBuild = long.TryParse(Environment.GetEnvironmentVariable("TYPHON_HS_ROWS"), out var r) ? r : 4_000_000;
    private static readonly long CacheMiB = long.TryParse(Environment.GetEnvironmentVariable("TYPHON_HS_CACHE_MIB"), out var c) ? c : 64;
    private static readonly bool Live = Environment.GetEnvironmentVariable("TYPHON_HS_LIVE") == "1";
    private static readonly bool Prefill = Environment.GetEnvironmentVariable("TYPHON_HS_PREFILL") == "1";
    private static string RunDb => Environment.GetEnvironmentVariable("TYPHON_HS_DB") ?? Path.Combine(Dir, "base", "hs.typhon");

    private static DatabaseEngine Open(long cacheMiB) =>
        DatabaseEngine.Open(RunDb, o => o
            .Register<HsVersioned>()
            .Register<HsSingle>()
            .Register<HsShared>()
            .Register<HsFlat>()
            .Register<HsChild>()
            .Register<HsHot>()
            .Register<HsSpatial>()
            .ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(-WorldExtent, -WorldExtent), new Vector2(WorldExtent, WorldExtent), cellSize: 100f))
            .PageCacheSize((ulong)cacheMiB << 20)
            .ConfigureStorage(s => s.VerifyOnOpen = OpenVerification.Spine));

    private static string IdsFile(string name) => Path.Combine(Path.GetDirectoryName(RunDb)!, name + ".ids");

    private static void WriteIds(string name, ulong[] ids) => File.WriteAllBytes(IdsFile(name), MemoryMarshal.AsBytes(ids.AsSpan()).ToArray());

    private static EntityId[] ReadIds(string name)
    {
        var bytes = File.ReadAllBytes(IdsFile(name));
        var raw = MemoryMarshal.Cast<byte, ulong>(bytes);
        var ids = new EntityId[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            ids[i] = EntityId.FromRawValue(raw[i]);
        }

        return ids;
    }

    private static int GroupOf(long seq) => (int)(seq * 2654435761L % Groups);

    /// <summary>Multiplying by an odd constant modulo 2^62 is a bijection: a unique key with no relation to the row's place in storage.</summary>
    private static long ShuffledOf(long seq) => (long)((ulong)seq * 0x9E3779B97F4A7C15UL & 0x3FFF_FFFF_FFFF_FFFFUL);

    private const int FlatRowsDivisor = 2;
    private const int ChildRowsDivisor = 4;

    [Test]
    public void Build()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RunDb)!);
        var sw = Stopwatch.StartNew();
        var options = new BulkLoadOptions { CheckpointTimeout = TimeSpan.FromMinutes(60) };
        using var dbe = Open(4096);
        const int sessionRows = 500_000;

        var hot = new ulong[HotRows];
        using (var s = dbe.BeginBulkLoad(options))
        {
            for (var i = 0; i < HotRows; i++)
            {
                hot[i] = s.Spawn<HsHotArch>(HsHotArch.Row.Set(new HsHot { Key = i + 1, Value = 1 })).RawValue;
            }

            s.CompleteBulkLoad();
        }

        WriteIds("hot", hot);
        TestContext.Progress.WriteLine($"hot done {sw.Elapsed.TotalSeconds:F0}s");

        var ver = new ulong[RowsToBuild];
        for (long first = 0; first < RowsToBuild; first += sessionRows)
        {
            using var s = dbe.BeginBulkLoad(options);
            for (var i = first; i < Math.Min(first + sessionRows, RowsToBuild); i++)
            {
                var row = new HsVersioned { Seq = i + 1, Group = GroupOf(i + 1), Value = i, Shuffled = ShuffledOf(i + 1) };
                ver[i] = s.Spawn<HsVerArch>(HsVerArch.Row.Set(in row)).RawValue;
            }

            s.CompleteBulkLoad();
            TestContext.Progress.WriteLine($"versioned {first + sessionRows:N0} {sw.Elapsed.TotalSeconds:F0}s");
        }

        WriteIds("ver", ver);

        var sv = new ulong[RowsToBuild];
        for (long first = 0; first < RowsToBuild; first += sessionRows)
        {
            using var s = dbe.BeginBulkLoad(options);
            for (var i = first; i < Math.Min(first + sessionRows, RowsToBuild); i++)
            {
                var row = new HsSingle { Seq = i + 1, Group = GroupOf(i + 1), Value = i, Shuffled = ShuffledOf(i + 1) };
                sv[i] = s.Spawn<HsSvArch>(HsSvArch.Row.Set(in row)).RawValue;
            }

            s.CompleteBulkLoad();
            TestContext.Progress.WriteLine($"single {first + sessionRows:N0} {sw.Elapsed.TotalSeconds:F0}s");
        }

        WriteIds("sv", sv);

        var fanRows = RowsToBuild / FanRowsDivisor;
        for (long first = 0; first < fanRows; first += sessionRows)
        {
            using var s = dbe.BeginBulkLoad(options);
            for (var i = first; i < Math.Min(first + sessionRows, fanRows); i++)
            {
                var row = new HsShared { Seq = i + 1, Near = i + 1, Value = i };
                if ((i & 1) == 0)
                {
                    s.Spawn<HsFanAArch>(HsFanAArch.Row.Set(in row));
                }
                else
                {
                    s.Spawn<HsFanBArch>(HsFanBArch.Row.Set(in row));
                }
            }

            s.CompleteBulkLoad();
        }

        var spatialRows = RowsToBuild / SpatialRowsDivisor;
        var rand = new Random(17);
        for (long first = 0; first < spatialRows; first += sessionRows)
        {
            using var s = dbe.BeginBulkLoad(options);
            for (var i = first; i < Math.Min(first + sessionRows, spatialRows); i++)
            {
                var x = (float)(rand.NextDouble() * 1.9 - 0.95) * WorldExtent;
                var y = (float)(rand.NextDouble() * 1.9 - 0.95) * WorldExtent;
                var row = new HsSpatial { Seq = i + 1, Bounds = new AABB3F { MinX = x, MinY = y, MinZ = 0, MaxX = x + 1, MaxY = y + 1, MaxZ = 1 } };
                s.Spawn<HsSpatialArch>(HsSpatialArch.Row.Set(in row));
            }

            s.CompleteBulkLoad();
        }

        var flatRows = RowsToBuild / FlatRowsDivisor;
        for (long first = 0; first < flatRows; first += sessionRows)
        {
            using var s = dbe.BeginBulkLoad(options);
            for (var i = first; i < Math.Min(first + sessionRows, flatRows); i++)
            {
                s.Spawn<HsFlatArch>(HsFlatArch.Row.Set(new HsFlat { Bucket = 1, Seq = i + 1 }));
            }

            s.CompleteBulkLoad();
        }

        var childRows = RowsToBuild / ChildRowsDivisor;
        for (long first = 0; first < childRows; first += sessionRows)
        {
            using var s = dbe.BeginBulkLoad(options);
            for (var i = first; i < Math.Min(first + sessionRows, childRows); i++)
            {
                s.Spawn<HsChildArch>(HsChildArch.Row.Set(new HsChild { ParentId = (long)ver[i * 3 % RowsToBuild], Seq = i + 1 }));
            }

            s.CompleteBulkLoad();
        }

        TestContext.Progress.WriteLine($"built in {sw.Elapsed.TotalSeconds:F0}s: {dbe.MMF.FileSize / (1024.0 * 1024 * 1024):F2} GiB");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(RunDb)!, "manifest.json"), JsonSerializer.Serialize(new { RowsToBuild, dbe.MMF.FileSize }));
    }

    #region Scenario plumbing

    private sealed class Ctx
    {
        public DatabaseEngine Dbe;
        public long Rows;
        public long Visited;
        public long Sum;
        public string Note;
        public EntityId[] Ids;
        public EntityId[] HotIds;

        public void Visit(long seq)
        {
            Visited++;
            Sum += seq;
        }
    }

    private sealed record Scenario(string Name, Action<Ctx> Run, bool NeedsIds = false, bool ChecksEveryRow = true, long ExpectedRows = -1,
        bool Writes = false);

    /// <summary>One archetype's scenarios: the same walks over a Versioned and a SingleVersion component, whose page costs differ.</summary>
    private sealed class Family<TArch, TRow> where TArch : Archetype<TArch> where TRow : unmanaged
    {
        public string Tag;
        public string IdsName;
        public Comp<TRow> Comp;
        public Func<TRow, long> Seq;
        public Func<TRow, long, TRow> WithValue;
        public Func<long, TRow> MakeRow;
        public Expression<Func<TRow, long>> SeqKey;
        public Expression<Func<TRow, int>> GroupKey;
        public Expression<Func<TRow, long>> ShuffledKey;
        public Expression<Func<TRow, bool>> Half;
        public Expression<Func<TRow, bool>> SeqAll;
        public Expression<Func<TRow, bool>> ShuffledAll;
        public Expression<Func<TRow, bool>> Broad;
        public Expression<Func<TRow, bool>> Selective;
        public Func<TRow, bool> HalfLambda;

        public IEnumerable<Scenario> Reads()
        {
            yield return new Scenario($"Count_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                ctx.Visited = tx.Query<TArch>().Count();
            }, ChecksEveryRow: false);

            yield return new Scenario($"Execute_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                ctx.Visited = tx.Query<TArch>().Execute().Count;
            }, ChecksEveryRow: false);

            yield return new Scenario($"ExecuteThenOpen_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                foreach (var id in tx.Query<TArch>().Execute())
                {
                    ctx.Visit(Seq(tx.Open(id).Read(Comp)));
                }
            });

            yield return new Scenario($"Foreach_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                foreach (var e in tx.Query<TArch>())
                {
                    ctx.Visit(Seq(e.Read(Comp)));
                }
            });

            // The snapshot walk (I3): one cluster per batch, at the transaction's snapshot, holding no epoch between batches.
            yield return new Scenario($"TxWalk_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                foreach (var batch in tx.Walk<TArch>())
                {
                    var span = batch.GetReadOnlySpan(Comp);
                    for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
                    {
                        ctx.Visit(Seq(span[BitOperations.TrailingZeroCount(bits)]));
                    }
                }
            });

            yield return new Scenario($"ForeachWhereLambda_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                var q = tx.Query<TArch>().Where(HalfLambda);
                foreach (var e in q)
                {
                    ctx.Visit(Seq(e.Read(Comp)));
                }
            }, ChecksEveryRow: false);

            yield return new Scenario($"WhereFieldBroad_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                ctx.Visited = tx.Query<TArch>().WhereField(Broad).Execute().Count;
            }, ChecksEveryRow: false);

            yield return new Scenario($"WhereFieldSelective_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                ctx.Visited = tx.Query<TArch>().WhereField(Selective).Execute().Count;
            }, ChecksEveryRow: false, ExpectedRows: 0);

            yield return new Scenario($"OrderedAll_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                ctx.Visited = tx.Query<TArch>().WhereField(SeqAll).OrderByField(SeqKey).ExecuteOrdered().Count;
            }, ChecksEveryRow: false);

            yield return new Scenario($"OrderedTake_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                ctx.Visited = tx.Query<TArch>().WhereField(SeqAll).OrderByField(SeqKey).Take(100).ExecuteOrdered().Count;
            }, ChecksEveryRow: false, ExpectedRows: 100);

            yield return new Scenario($"OrderedDeepSkip_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                ctx.Visited = tx.Query<TArch>().WhereField(SeqAll).OrderByField(SeqKey).Skip((int)ctx.Rows - 100).Take(100).ExecuteOrdered().Count;
            }, ChecksEveryRow: false, ExpectedRows: 100);

            yield return new Scenario($"OrderedFallbackTake_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                ctx.Visited = tx.Query<TArch>().WhereField(Broad).OrderByField(SeqKey).Take(100).ExecuteOrdered().Count;
            }, ChecksEveryRow: false, ExpectedRows: 100);

            yield return new Scenario($"ArchetypeEntitiesThenOpen_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                foreach (var id in tx.EnumerateArchetypeEntities(ctx.Dbe.ArchetypeIdOf<TArch>()))
                {
                    ctx.Visit(Seq(tx.Open(id).Read(Comp)));
                }
            });

            yield return new Scenario($"IndexUnique_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                using var rows = tx.EnumerateIndex<TRow, long>(ctx.Dbe.GetIndexRef(SeqKey), long.MinValue, long.MaxValue);
                while (rows.MoveNext())
                {
                    ctx.Visit(Seq(rows.CurrentComponent));
                }
            });

            yield return new Scenario($"IndexMultiple_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                using var rows = tx.EnumerateIndex<TRow, int>(ctx.Dbe.GetIndexRef(GroupKey), int.MinValue, int.MaxValue);
                while (rows.MoveNext())
                {
                    ctx.Visit(Seq(rows.CurrentComponent));
                }
            });

            yield return new Scenario($"IndexKeysetPaged_{Tag}", ctx =>
            {
                var from = long.MinValue;
                while (true)
                {
                    using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                    using var rows = tx.EnumerateIndex<TRow, long>(ctx.Dbe.GetIndexRef(SeqKey), from, long.MaxValue);
                    var n = 0;
                    while (n < KeysetPage && rows.MoveNext())
                    {
                        ctx.Visit(Seq(rows.CurrentComponent));
                        from = rows.CurrentKey + 1;
                        n++;
                    }

                    if (n < KeysetPage)
                    {
                        return;
                    }
                }
            });

            yield return new Scenario($"ViewIncremental_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                using var view = tx.Query<TArch>().WhereField(Broad).ToView();
                ctx.Visited = view.Count;
            }, ChecksEveryRow: false);

            yield return new Scenario($"ViewPull_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                using var view = tx.Query<TArch>().ToView();
                ctx.Visited = view.Count;
            }, ChecksEveryRow: false);

            yield return new Scenario($"PointReadsShuffled_{Tag}", ctx =>
            {
                var shuffled = (EntityId[])ctx.Ids.Clone();
                new Random(5).Shuffle(shuffled);
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                for (var i = 0; i < shuffled.Length / 10; i++)
                {
                    ctx.Visit(Seq(tx.Open(shuffled[i]).Read(Comp)));
                }
            }, NeedsIds: true, ChecksEveryRow: false);

            // The leak's three arms: the same walk read-only (IndexUnique), read-write, and read-write after one entity open gave the transaction a
            // ComponentInfo.
            yield return new Scenario($"IndexUniqueRw_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateQuickTransaction();
                using var rows = tx.EnumerateIndex<TRow, long>(ctx.Dbe.GetIndexRef(SeqKey), long.MinValue, long.MaxValue);
                while (rows.MoveNext())
                {
                    ctx.Visit(Seq(rows.CurrentComponent));
                }
            });

            yield return new Scenario($"IndexUniqueRwAfterOpen_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateQuickTransaction();
                tx.Open(ctx.Ids[0]).Read(Comp);
                using var rows = tx.EnumerateIndex<TRow, long>(ctx.Dbe.GetIndexRef(SeqKey), long.MinValue, long.MaxValue);
                while (rows.MoveNext())
                {
                    ctx.Visit(Seq(rows.CurrentComponent));
                }
            }, NeedsIds: true);

            yield return new Scenario($"IndexPointLoop_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                var ix = ctx.Dbe.GetIndexRef(SeqKey);
                var rand = new Random(3);
                for (var i = 0; i < 400_000; i++)
                {
                    var key = rand.NextInt64(1, ctx.Rows + 1);
                    using var rows = tx.EnumerateIndex<TRow, long>(ix, key, key);
                    if (rows.MoveNext())
                    {
                        ctx.Visit(Seq(rows.CurrentComponent));
                    }
                }
            }, ChecksEveryRow: false);

            yield return new Scenario($"IndexShuffled_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                using var rows = tx.EnumerateIndex<TRow, long>(ctx.Dbe.GetIndexRef(ShuffledKey), long.MinValue, long.MaxValue);
                while (rows.MoveNext())
                {
                    ctx.Visit(Seq(rows.CurrentComponent));
                }
            });

            yield return new Scenario($"OrderedShuffledAll_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                ctx.Visited = tx.Query<TArch>().WhereField(ShuffledAll).OrderByField(ShuffledKey).ExecuteOrdered().Count;
            }, ChecksEveryRow: false);

            yield return new Scenario($"OrderedShuffledSkip_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                ctx.Visited = tx.Query<TArch>().WhereField(ShuffledAll).OrderByField(ShuffledKey).Skip(100_000).Take(50).ExecuteOrdered().Count;
            }, ChecksEveryRow: false, ExpectedRows: 50);

            yield return new Scenario($"WhereFieldHalf_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                ctx.Visited = tx.Query<TArch>().WhereField(Half).Execute().Count;
            }, ChecksEveryRow: false);

            yield return new Scenario($"ClusterEnumTx_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateReadOnlyTransaction();
                using var clusters = tx.GetClusterEnumerator<TArch>();
                while (clusters.MoveNext())
                {
                    Visit(ctx, clusters.Current);
                }
            });

            yield return new Scenario($"ClusterEnumRwTx_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateQuickTransaction();
                using var clusters = tx.GetClusterEnumerator<TArch>();
                while (clusters.MoveNext())
                {
                    Visit(ctx, clusters.Current);
                }
            });

            yield return new Scenario($"ClusterEnumPtaOneLease_{Tag}", ctx =>
            {
                using var pta = PointInTimeAccessor.Create(ctx.Dbe);
                using var lease = pta.Enter(0);
                using var clusters = lease.Accessor.GetClusterEnumerator<TArch>();
                while (clusters.MoveNext())
                {
                    Visit(ctx, clusters.Current);
                }
            });

            yield return new Scenario($"ClusterEnumPtaPaged_{Tag}", ctx =>
            {
                using var pta = PointInTimeAccessor.Create(ctx.Dbe);
                var n = ctx.Dbe.GetArchetypeClusterChunkCount(Archetype<TArch>.Metadata.ArchetypeId);
                for (var i = 0; i < n; i += 256)
                {
                    using var lease = pta.Enter(0);
                    using var clusters = lease.Accessor.GetClusterEnumerator<TArch>(i, Math.Min(i + 256, n));
                    while (clusters.MoveNext())
                    {
                        Visit(ctx, clusters.Current);
                    }
                }
            });

            yield return new Scenario($"SameTxSlowConsumer_{Tag}", ctx =>
            {
                // The audits' shape: one snapshot, one row at a time, the caller's own work between rows.
                using var tx = ctx.Dbe.CreateQuickTransaction();
                using var rows = tx.EnumerateIndex<TRow, long>(ctx.Dbe.GetIndexRef(SeqKey), long.MinValue, long.MaxValue);
                var replay = new Dictionary<long, long>();
                while (rows.MoveNext())
                {
                    var seq = Seq(rows.CurrentComponent);
                    ctx.Visit(seq);
                    ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(replay, seq % 100_000, out _);
                    slot += seq;
                    Thread.SpinWait(200);
                }
            });
        }

        private void Visit(Ctx ctx, ClusterRef<TArch> cluster)
        {
            var span = cluster.GetReadOnlySpan(Comp);
            var bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                ctx.Visit(Seq(span[BitOperations.TrailingZeroCount(bits)]));
                bits &= bits - 1;
            }
        }

        public IEnumerable<Scenario> Writes()
        {
            yield return new Scenario($"UpdateAllOneTx_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateQuickTransaction();
                foreach (var id in ctx.Ids)
                {
                    tx.OpenMut(id).Set(Comp, WithValue(tx.Open(id).Read(Comp), 7));
                    ctx.Visited++;
                }

                tx.Commit();
            }, NeedsIds: true, ChecksEveryRow: false, Writes: true);

            foreach (var size in new[] { 100_000, 300_000, 600_000, 1_000_000 })
            {
                yield return new Scenario($"UpdateOneTx{size / 1000}k_{Tag}", ctx =>
                {
                    using var tx = ctx.Dbe.CreateQuickTransaction();
                    for (var i = 0; i < size; i++)
                    {
                        tx.OpenMut(ctx.Ids[i]).Set(Comp, WithValue(tx.Open(ctx.Ids[i]).Read(Comp), 7));
                        ctx.Visited++;
                    }

                    tx.Commit();
                }, NeedsIds: true, ChecksEveryRow: false, Writes: true);
            }

            yield return new Scenario($"DestroyBatched_{Tag}", ctx =>
            {
                var ids = ctx.Ids;
                for (var at = 0; at < ids.Length; at += UpdateBatch)
                {
                    using var tx = ctx.Dbe.CreateQuickTransaction();
                    var end = Math.Min(at + UpdateBatch, ids.Length);
                    for (var i = at; i < end; i++)
                    {
                        tx.Destroy(ids[i]);
                        ctx.Visited++;
                    }

                    tx.Commit();
                }
            }, NeedsIds: true, ChecksEveryRow: false, Writes: true);

            yield return new Scenario($"UpdateBatched_{Tag}", ctx =>
            {
                var ids = ctx.Ids;
                for (var at = 0; at < ids.Length; at += UpdateBatch)
                {
                    using var tx = ctx.Dbe.CreateQuickTransaction();
                    var end = Math.Min(at + UpdateBatch, ids.Length);
                    for (var i = at; i < end; i++)
                    {
                        tx.OpenMut(ids[i]).Set(Comp, WithValue(tx.Open(ids[i]).Read(Comp), 7));
                        ctx.Visited++;
                    }

                    tx.Commit();
                }
            }, NeedsIds: true, ChecksEveryRow: false, Writes: true);

            yield return new Scenario($"UpdateWhileEnumerating_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateQuickTransaction();
                foreach (var e in tx.Query<TArch>())
                {
                    tx.OpenMut(e.Id).Set(Comp, WithValue(e.Read(Comp), 7));
                    ctx.Visited++;
                }

                tx.Commit();
            }, ChecksEveryRow: false, Writes: true);

            yield return new Scenario($"DestroyAllOneTx_{Tag}", ctx =>
            {
                using var tx = ctx.Dbe.CreateQuickTransaction();
                foreach (var id in ctx.Ids)
                {
                    tx.Destroy(id);
                    ctx.Visited++;
                }

                tx.Commit();
            }, NeedsIds: true, ChecksEveryRow: false, Writes: true);

            yield return new Scenario($"BulkUpdateAll_{Tag}", ctx =>
            {
                using var s = ctx.Dbe.BeginBulkLoad(new BulkLoadOptions { CheckpointTimeout = TimeSpan.FromMinutes(30) });
                for (var i = 0; i < ctx.Ids.Length; i++)
                {
                    s.Update(ctx.Ids[i], MakeRow(i + 1));
                    ctx.Visited++;
                }

                s.CompleteBulkLoad();
            }, NeedsIds: true, ChecksEveryRow: false, Writes: true);
        }
    }

    private static readonly Family<HsVerArch, HsVersioned> Ver = new()
    {
        Tag = "Ver", IdsName = "ver", Comp = HsVerArch.Row, Seq = v => v.Seq,
        WithValue = (v, value) => { v.Value = value; return v; },
        MakeRow = seq => new HsVersioned { Seq = seq, Group = GroupOf(seq), Value = 7, Shuffled = ShuffledOf(seq) },
        SeqKey = v => v.Seq, GroupKey = v => v.Group, ShuffledKey = v => v.Shuffled, Half = v => v.Group < 500, SeqAll = v => v.Seq >= 0,
            ShuffledAll = v => v.Shuffled >= 0,
        Broad = v => v.Group >= 0, Selective = v => v.Group == 7, HalfLambda = v => (v.Seq & 1) == 0,
    };

    private static readonly Family<HsSvArch, HsSingle> Sv = new()
    {
        Tag = "Sv", IdsName = "sv", Comp = HsSvArch.Row, Seq = v => v.Seq,
        WithValue = (v, value) => { v.Value = value; return v; },
        MakeRow = seq => new HsSingle { Seq = seq, Group = GroupOf(seq), Value = 7, Shuffled = ShuffledOf(seq) },
        SeqKey = v => v.Seq, GroupKey = v => v.Group, ShuffledKey = v => v.Shuffled, Half = v => v.Group < 500, SeqAll = v => v.Seq >= 0,
            ShuffledAll = v => v.Shuffled >= 0,
        Broad = v => v.Group >= 0, Selective = v => v.Group == 7, HalfLambda = v => (v.Seq & 1) == 0,
    };

    private static IEnumerable<Scenario> Extra()
    {
        yield return new Scenario("IndexFanOut_Shared", ctx =>
        {
            using var tx = ctx.Dbe.CreateReadOnlyTransaction();
            using var rows = tx.EnumerateIndex<HsShared, long>(ctx.Dbe.GetIndexRef<HsShared, long>(v => v.Seq), long.MinValue, long.MaxValue);
            while (rows.MoveNext())
            {
                ctx.Visit(rows.CurrentComponent.Seq);
            }
        }, ChecksEveryRow: false);

        // What a walk's containment costs a walk that repeats: half a cache, three times. Today the second pass should hit.
        yield return new Scenario("RepeatHalfCacheWalk_Ver", ctx =>
        {
            var metrics = ctx.Dbe.MMF.GetMetrics();
            using var pta = PointInTimeAccessor.Create(ctx.Dbe);
            var passes = new List<int>();
            for (var pass = 0; pass < 3; pass++)
            {
                var before = metrics.ReadFromDiskCount;
                for (var i = 0; i < 4_096; i += 256)
                {
                    using var lease = pta.Enter(0);
                    using var clusters = lease.Accessor.GetClusterEnumerator<HsVerArch>(i, i + 256);
                    while (clusters.MoveNext())
                    {
                        ctx.Visited += clusters.Current.LiveCount;
                    }
                }

                passes.Add(metrics.ReadFromDiskCount - before);
            }

            ctx.Note = string.Join("/", passes);
        }, ChecksEveryRow: false);

        yield return new Scenario("IndexFanOutMultiple_Shared", ctx =>
        {
            using var tx = ctx.Dbe.CreateReadOnlyTransaction();
            using var rows = tx.EnumerateIndex<HsShared, long>(ctx.Dbe.GetIndexRef<HsShared, long>(v => v.Near), long.MinValue, long.MaxValue);
            while (rows.MoveNext())
            {
                ctx.Visit(rows.CurrentComponent.Seq);
            }
        }, ChecksEveryRow: false);

        yield return new Scenario("IndexFatKey_Flat", ctx =>
        {
            using var tx = ctx.Dbe.CreateReadOnlyTransaction();
            using var rows = tx.EnumerateIndex<HsFlat, int>(ctx.Dbe.GetIndexRef<HsFlat, int>(v => v.Bucket), 1, 1);
            while (rows.MoveNext())
            {
                ctx.Visit(rows.CurrentComponent.Seq);
            }
        }, ChecksEveryRow: false);

        yield return new Scenario("NavigateField_Child", ctx =>
        {
            using var tx = ctx.Dbe.CreateReadOnlyTransaction();
            ctx.Visited = tx.Query<HsChildArch>().NavigateField<HsChild, HsVersioned>(c => c.ParentId).Where((c, p) => p.Group == 7).Count();
        }, ChecksEveryRow: false);

        yield return new Scenario("SpatialWholeWorldCount", ctx =>
        {
            using var tx = ctx.Dbe.CreateReadOnlyTransaction();
            ctx.Visited = tx.Query<HsSpatialArch>().WhereInAABB<HsSpatial>(-WorldExtent, -WorldExtent, -1, WorldExtent, WorldExtent, 2).Count();
        }, ChecksEveryRow: false);

        yield return new Scenario("SpatialRayAcross", ctx =>
        {
            using var tx = ctx.Dbe.CreateReadOnlyTransaction();
            ctx.Visited = tx.Query<HsSpatialArch>().WhereRay<HsSpatial>(-WorldExtent, 0.5, 0.5, 1, 0, 0, 2 * WorldExtent).Execute().Count;
        }, ChecksEveryRow: false, ExpectedRows: 0);

        // I10: a snapshot walk paused 5 s after its first batch while a writer appends. Between batches the walk holds no epoch, so what the writer touches
        // stays evictable: the peak of epoch-held pages and the writer's back-pressure rounds against TailChase_Ver's idle transaction (8 128 pages).
        yield return new Scenario("PausedWalk_Ver", ctx =>
        {
            using var tx = ctx.Dbe.CreateReadOnlyTransaction();
            var latencies = new List<double>();
            Exception writerFault = null;
            var stop = false;
            var writer = new Thread(() =>
            {
                try
                {
                    var seq = ctx.Rows + 1;
                    while (!Volatile.Read(ref stop))
                    {
                        var commit = Stopwatch.StartNew();
                        using (var w = ctx.Dbe.CreateQuickTransaction())
                        {
                            for (var i = 0; i < 1000; i++, seq++)
                            {
                                var row = new HsVersioned { Seq = seq, Group = GroupOf(seq), Value = 1, Shuffled = ShuffledOf(seq) };
                                w.Spawn<HsVerArch>(HsVerArch.Row.Set(in row));
                            }

                            w.Commit();
                        }

                        latencies.Add(commit.Elapsed.TotalMilliseconds);
                    }
                }
                catch (Exception e)
                {
                    writerFault = e;
                }
            }) { IsBackground = true, Name = "hs-appender" };

            var suspendedDuringPause = false;
            var heldAfterPause = -1;
            var heldWhileAppending = -1;
            using (var walk = tx.Walk<HsVerArch>())
            {
                var first = true;
                while (walk.MoveNext())
                {
                    var batch = walk.Current;
                    var span = batch.GetReadOnlySpan(HsVerArch.Row);
                    for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
                    {
                        ctx.Visit(span[BitOperations.TrailingZeroCount(bits)].Seq);
                    }

                    if (first)
                    {
                        first = false;
                        writer.Start();
                        Thread.Sleep(5000);
                        suspendedDuringPause = tx.EpochSuspended && !ctx.Dbe.EpochManager.IsCurrentThreadInScope;
                        ctx.Dbe.MMF.GetMetrics().GetMemPageExtraInfo(out var running);
                        heldWhileAppending = running.EpochProtectedPageCount;
                        Volatile.Write(ref stop, true);
                        writer.Join();

                        // The appender gone, only this thread could still pin what it touched: the I10 measure, against TailChase_Ver's idle transaction.
                        ctx.Dbe.MMF.GetMetrics().GetMemPageExtraInfo(out var census);
                        heldAfterPause = census.EpochProtectedPageCount;
                    }
                }
            }

            if (writerFault != null)
            {
                throw new InvalidOperationException("the appender failed beside the paused walk", writerFault);
            }

            latencies.Sort();
            var p99 = latencies.Count == 0 ? 0 : latencies[(int)(latencies.Count * 0.99)];
            ctx.Note = $"suspended {suspendedDuringPause}, epoch-held while appending {heldWhileAppending}, after {heldAfterPause}, appender commits {latencies.Count}, p99 {p99:F1} ms, "
                       + $"max {(latencies.Count == 0 ? 0 : latencies[^1]):F1} ms";
        }, Writes: true);

        // A walk at an old snapshot, near the ledger's end, while a writer appends: every row the writer adds is invisible to the walk, so MoveNext skips
        // them and never emits, and the walk's refresh runs only on an emission.
        yield return new Scenario("TailChase_Ver", ctx =>
        {
            using var tx = ctx.Dbe.CreateReadOnlyTransaction();
            var stop = false;
            Exception writerFault = null;
            var writer = new Thread(() =>
            {
                try
                {
                    var seq = ctx.Rows + 1;
                    var deadline = Stopwatch.StartNew();
                    while (!Volatile.Read(ref stop) && deadline.Elapsed.TotalSeconds < 45)
                    {
                        using var w = ctx.Dbe.CreateQuickTransaction();
                        for (var i = 0; i < 1000; i++, seq++)
                        {
                            var row = new HsVersioned { Seq = seq, Group = GroupOf(seq), Value = 1, Shuffled = ShuffledOf(seq) };
                            w.Spawn<HsVerArch>(HsVerArch.Row.Set(in row));
                        }

                        w.Commit();
                    }
                }
                catch (Exception e)
                {
                    writerFault = e;
                }
            }) { IsBackground = true, Name = "hs-appender" };
            writer.Start();
            Thread.Sleep(5000);
            ctx.Dbe.MMF.GetMetrics().GetMemPageExtraInfo(out var idleCensus);
            ctx.Note = $"epoch-held while appending {idleCensus.EpochProtectedPageCount} (an idle transaction)";
            using (var rows = tx.EnumerateIndex<HsVersioned, long>(ctx.Dbe.GetIndexRef<HsVersioned, long>(v => v.Seq), ctx.Rows - 1000, long.MaxValue))
            {
                while (rows.MoveNext())
                {
                    ctx.Visit(rows.CurrentComponent.Seq);
                }
            }

            Volatile.Write(ref stop, true);
            writer.Join();
            if (writerFault != null)
            {
                throw new InvalidOperationException("the appender failed beside the walk", writerFault);
            }
        }, ChecksEveryRow: false, Writes: true);

        // A reader that outlives N updates of a small set: what the snapshot costs everyone else, and what the reader then pays at its own Dispose.
        yield return new Scenario("MvccReaderBesideUpdates", ctx =>
        {
            var reader = ctx.Dbe.CreateQuickTransaction();
            reader.Open(ctx.HotIds[0]).Read(HsHotArch.Row);
            try
            {
                var rand = new Random(9);
                for (var b = 0; b < 2_000; b++)
                {
                    using var w = ctx.Dbe.CreateQuickTransaction();
                    for (var i = 0; i < 64; i++)
                    {
                        var id = ctx.HotIds[rand.Next(256)];
                        var row = w.Open(id).Read(HsHotArch.Row);
                        row.Value++;
                        w.OpenMut(id).Set(HsHotArch.Row, row);
                        ctx.Visited++;
                    }

                    w.Commit();
                }
            }
            finally
            {
                var dispose = Stopwatch.StartNew();
                reader.Dispose();
                TestContext.Progress.WriteLine($"reader dispose {dispose.Elapsed.TotalMilliseconds:F0} ms");
            }
        }, ChecksEveryRow: false, Writes: true);

        yield return new Scenario("SpatialWholeWorldExecute", ctx =>
        {
            using var tx = ctx.Dbe.CreateReadOnlyTransaction();
            ctx.Visited = tx.Query<HsSpatialArch>().WhereInAABB<HsSpatial>(-WorldExtent, -WorldExtent, -1, WorldExtent, WorldExtent, 2).Execute().Count;
        }, ChecksEveryRow: false);

        // The audit's shape over the whole database: three walks of one snapshot, a ledger by index, then a whole archetype by foreach.
        yield return new Scenario("AuditShape", ctx =>
        {
            using var tx = ctx.Dbe.CreateQuickTransaction();
            var balance = new Dictionary<long, long>();
            using (var rows = tx.EnumerateIndex<HsVersioned, long>(ctx.Dbe.GetIndexRef<HsVersioned, long>(v => v.Seq), long.MinValue, long.MaxValue))
            {
                while (rows.MoveNext())
                {
                    var row = rows.CurrentComponent;
                    ctx.Visit(row.Seq);
                    balance[row.Group] = balance.GetValueOrDefault(row.Group) + row.Value;
                }
            }

            ctx.Visited = 0;
            ctx.Sum = 0;
            using (var rows = tx.EnumerateIndex<HsVersioned, int>(ctx.Dbe.GetIndexRef<HsVersioned, int>(v => v.Group), int.MinValue, int.MaxValue))
            {
                while (rows.MoveNext())
                {
                    ctx.Visit(rows.CurrentComponent.Seq);
                }
            }

            ctx.Visited = 0;
            ctx.Sum = 0;
            foreach (var e in tx.Query<HsVerArch>())
            {
                ctx.Visit(e.Read(HsVerArch.Row).Seq);
            }
        });
    }

    private static Dictionary<string, Scenario> All()
    {
        var all = new Dictionary<string, Scenario>();
        foreach (var s in Ver.Reads().Concat(Ver.Writes()).Concat(Sv.Reads()).Concat(Sv.Writes()).Concat(Extra()))
        {
            all[s.Name] = s;
        }

        return all;
    }

    /// <summary>The scenario names, one per line, for the driver: <c>TYPHON_HS_LIST=path</c>.</summary>
    [Test]
    public void ListScenarios() =>
        File.WriteAllLines(Environment.GetEnvironmentVariable("TYPHON_HS_LIST") ?? Path.Combine(Dir, "scenarios.txt"), All().Keys.Order());

    #endregion

    #region The run

    private sealed class Peaks
    {
        public int Epoch;
        public int SlotRef;
        public int Dirty;
        public long ManagedHeap;
        public long WorkingSet;
    }

    private sealed class Sampler : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        public readonly Peaks Peaks = new();

        public Sampler(DatabaseEngine dbe)
        {
            _thread = new Thread(() =>
            {
                var tick = 0;
                while (!_stop)
                {
                    dbe.MMF.GetMetrics().GetMemPageExtraInfo(out var census);
                    Peaks.Epoch = Math.Max(Peaks.Epoch, census.EpochProtectedPageCount);
                    Peaks.SlotRef = Math.Max(Peaks.SlotRef, census.SlotRefPageCount);
                    Peaks.Dirty = Math.Max(Peaks.Dirty, census.DirtyPageCount);
                    if (++tick % 10 == 0)
                    {
                        Peaks.ManagedHeap = Math.Max(Peaks.ManagedHeap, GC.GetTotalMemory(false));
                        Peaks.WorkingSet = Math.Max(Peaks.WorkingSet, Environment.WorkingSet);
                    }

                    Thread.Sleep(20);
                }
            }) { IsBackground = true, Name = "hs-sampler" };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
        }
    }

    /// <summary>What a live game's traffic sees beside the scan: small commits and small reads of the hot set, each timed.</summary>
    private sealed class Traffic : IDisposable
    {
        private readonly DatabaseEngine _dbe;
        private readonly EntityId[] _hot;
        private readonly Thread _writer;
        private readonly Thread _reader;
        private volatile bool _stop;
        private volatile int _phase;
        public readonly List<double>[] WriteMs = [new(), new()];
        public readonly List<double>[] ReadMs = [new(), new()];
        public long Failures;
        public string FirstFailure;

        /// <summary>Disk loads each traffic thread caused itself, before the scan and during it: what the walk cost the foreground, in pages.</summary>
        public readonly long[] WriterLoads = new long[2];
        public readonly long[] ReaderLoads = new long[2];

        public Traffic(DatabaseEngine dbe, EntityId[] hot)
        {
            _dbe = dbe;
            _hot = hot;
            _writer = new Thread(() => Loop(true)) { IsBackground = true, Name = "hs-writer" };
            _reader = new Thread(() => Loop(false)) { IsBackground = true, Name = "hs-reader" };
            _writer.Start();
            _reader.Start();
        }

        public void MeasureScan() => _phase = 1;

        private void Loop(bool write)
        {
            var rand = new Random(write ? 1 : 2);
            var ownLoads = typeof(PagedMMF).GetField("OwnLoads", BindingFlags.Static | BindingFlags.NonPublic);
            var seen = 0L;
            while (!_stop)
            {
                var start = Stopwatch.GetTimestamp();
                try
                {
                    using var tx = write ? _dbe.CreateQuickTransaction() : _dbe.CreateReadOnlyTransaction();
                    for (var i = 0; i < 16; i++)
                    {
                        var id = _hot[rand.Next(_hot.Length)];
                        if (write)
                        {
                            var row = tx.Open(id).Read(HsHotArch.Row);
                            row.Value++;
                            tx.OpenMut(id).Set(HsHotArch.Row, row);
                        }
                        else
                        {
                            tx.Open(id).Read(HsHotArch.Row);
                        }
                    }

                    if (write)
                    {
                        tx.Commit();
                    }
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref Failures);
                    Interlocked.CompareExchange(ref FirstFailure, $"{e.GetType().Name}: {e.Message}", null);
                }

                var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var list = (write ? WriteMs : ReadMs)[_phase];
                lock (list)
                {
                    list.Add(ms);
                }

                var own = ownLoads == null ? 0L : (long)ownLoads.GetValue(null);
                (write ? WriterLoads : ReaderLoads)[_phase] += own - seen;
                seen = own;
                Thread.Sleep(1);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _writer.Join(TimeSpan.FromSeconds(30));
            _reader.Join(TimeSpan.FromSeconds(30));
        }
    }

    private static object Percentiles(List<double> ms)
    {
        lock (ms)
        {
            if (ms.Count == 0)
            {
                return null;
            }

            var s = ms.Order().ToArray();
            return new { count = s.Length, p50 = s[s.Length / 2], p99 = s[(int)(s.Length * 0.99)], max = s[^1] };
        }
    }

    private static int ReadHot(DatabaseEngine dbe, EntityId[] hot, int rounds)
    {
        var before = dbe.MMF.GetMetrics().ReadFromDiskCount;
        for (var round = 0; round < rounds; round++)
        {
            using var tx = dbe.CreateReadOnlyTransaction();
            foreach (var id in hot)
            {
                tx.Open(id).Read(HsHotArch.Row);
            }
        }

        return dbe.MMF.GetMetrics().ReadFromDiskCount - before;
    }

    private static void Report(object row) => File.AppendAllText(Environment.GetEnvironmentVariable("TYPHON_HS_RESULTS") ?? Path.Combine(Dir, "results.jsonl"),
        JsonSerializer.Serialize(row) + Environment.NewLine);

    public static IEnumerable<string> ScenarioNames() => All().Keys.Order();

    [TestCaseSource(nameof(ScenarioNames))]
    public void Scan(string name)
    {
        var scenario = All()[name];
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(RunDb)!, "manifest.json"))).RootElement;
        var rows = manifest.GetProperty("RowsToBuild").GetInt64();
        var ctx = new Ctx { Rows = rows, HotIds = ReadIds("hot") };
        if (scenario.NeedsIds)
        {
            ctx.Ids = ReadIds(name.EndsWith("_Ver") ? "ver" : "sv");
        }

        var openWatch = Stopwatch.StartNew();
        var dbe = Open(CacheMiB);
        var openMs = openWatch.Elapsed.TotalMilliseconds;
        ctx.Dbe = dbe;
        var cachePages = (int)(CacheMiB << 20) / PagedMMF.PageSize;
        var metrics = dbe.MMF.GetMetrics();

        // A cache that starts full of cold pages, as a server's does: on an empty one a walk under one cache of reads evicts nothing, whatever the policy.
        if (Prefill)
        {
            using var pta = PointInTimeAccessor.Create(dbe);
            for (var i = 0; i < cachePages * 2; i += 256)
            {
                using var lease = pta.Enter(0);
                using var clusters = lease.Accessor.GetClusterEnumerator<HsSvArch>(i, i + 256);
                while (clusters.MoveNext())
                {
                }
            }
        }

        var hotColdReads = ReadHot(dbe, ctx.HotIds, 1);
        ReadHot(dbe, ctx.HotIds, 7);
        var hotWarmReads = ReadHot(dbe, ctx.HotIds, 1);

        Traffic traffic = null;
        if (Live)
        {
            traffic = new Traffic(dbe, ctx.HotIds);
            Thread.Sleep(3000);
            traffic.MeasureScan();
        }

        var readsBefore = metrics.ReadFromDiskCount;
        var writesBefore = metrics.PageWrittenToDiskCount;
        var roundsBefore = Interlocked.Read(ref PagedMMF.TotalBackpressureRounds);
        var drawsBefore = dbe.MMF.ReserveDraws;
        var allocBefore = GC.GetTotalAllocatedBytes(true);
        var walBefore = dbe.GetWalTotalBytes();

        string failure = null;
        string failureStack = null;
        string census = null;
        var sw = Stopwatch.StartNew();
        double scanMs;
        Peaks peaks;
        using (var sampler = new Sampler(dbe))
        {
            try
            {
                scenario.Run(ctx);
            }
            catch (Exception e)
            {
                failure = $"{e.GetType().Name}: {e.Message}";
                failureStack = e.StackTrace;
                census = dbe.MMF.DescribeEvictionBlockers();
            }

            scanMs = sw.Elapsed.TotalMilliseconds;
            peaks = sampler.Peaks;
        }

        var diskReads = metrics.ReadFromDiskCount - readsBefore;
        var diskWrites = metrics.PageWrittenToDiskCount - writesBefore;
        var rounds = Interlocked.Read(ref PagedMMF.TotalBackpressureRounds) - roundsBefore;
        var draws = dbe.MMF.ReserveDraws - drawsBefore;
        var alloc = GC.GetTotalAllocatedBytes(true) - allocBefore;
        var wal = dbe.GetWalTotalBytes() - walBefore;
        traffic?.Dispose();

        var hotAfterWatch = Stopwatch.StartNew();
        string health = "ok";
        var hotAfterReads = -1;
        try
        {
            hotAfterReads = ReadHot(dbe, ctx.HotIds, 1);
            using var tx = dbe.CreateQuickTransaction();
            tx.OpenMut(ctx.HotIds[0]).Set(HsHotArch.Row, new HsHot { Key = 1, Value = 2 });
            tx.Commit();
        }
        catch (Exception e)
        {
            health = $"{e.GetType().Name}: {e.Message}";
        }

        var hotAfterMs = hotAfterWatch.Elapsed.TotalMilliseconds;
        var hotSurvival = hotAfterReads < 0 ? 0 : 1 - (double)hotAfterReads / Math.Max(1, hotColdReads);

        Report(new
        {
            scenario = name, live = Live, cacheMiB = CacheMiB, prefill = Prefill, note = ctx.Note, rows, openMs, outcome = failure ?? "ok",
                stack = failureStack, census, health,
            visited = ctx.Visited, scanMs, usPerRow = ctx.Visited == 0 ? 0 : scanMs * 1000 / ctx.Visited,
            diskReads, diskWrites, backpressureRounds = rounds, reserveDraws = draws, allocMiB = alloc / (1024.0 * 1024), walDeltaMiB = wal / (1024.0 * 1024),
            peakEpochPages = peaks.Epoch, peakSlotRefPages = peaks.SlotRef, peakDirtyPages = peaks.Dirty, cachePages,
            peakManagedMiB = peaks.ManagedHeap / (1024.0 * 1024), peakWorkingSetMiB = peaks.WorkingSet / (1024.0 * 1024),
            hotColdReads, hotWarmReads, hotAfterReads, hotSurvival, hotAfterMs,
            writerBase = traffic == null ? null : Percentiles(traffic.WriteMs[0]), writerScan = traffic == null ? null : Percentiles(traffic.WriteMs[1]),
            readerBase = traffic == null ? null : Percentiles(traffic.ReadMs[0]), readerScan = traffic == null ? null : Percentiles(traffic.ReadMs[1]),
            trafficFailures = traffic?.Failures ?? 0, trafficFirstFailure = traffic?.FirstFailure,
            writerLoads = traffic?.WriterLoads, readerLoads = traffic?.ReaderLoads,
        });

        TestContext.Progress.WriteLine($"{name}: {failure ?? "ok"} {scanMs:F0} ms, {ctx.Visited:N0} rows, {diskReads:N0} "
            + $"reads, epoch peak {peaks.Epoch}, hot {hotSurvival:P0}");

        // Assert before Dispose, which on a wedged engine may not return: the row above is already written.
        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Null, "finished");
            Assert.That(health, Is.EqualTo("ok"), "the engine still works afterwards");
            if (scenario.ChecksEveryRow)
            {
                Assert.That(ctx.Visited, Is.EqualTo(rows), "every row, once");
                Assert.That(ctx.Sum, Is.EqualTo(rows * (rows + 1) / 2), "every row, once");
            }

            if (scenario.ExpectedRows > 0)
            {
                Assert.That(ctx.Visited, Is.EqualTo(scenario.ExpectedRows));
            }

            Assert.That(peaks.Epoch, Is.LessThanOrEqualTo((int)(cachePages * PinBudgetFraction)), "held by epoch");
            Assert.That(hotSurvival, Is.GreaterThanOrEqualTo(HotSurvivalFloor), "hot set survival");
        });

        dbe.Dispose();
    }

    #endregion
}
