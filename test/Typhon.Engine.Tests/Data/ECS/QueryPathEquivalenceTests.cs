using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Schema — one component per key type the planner can stream

/// <summary>
/// One indexed field per streamable <see cref="KeyType"/>, so a single spawn populates every typed B+Tree the query planner can choose between and one
/// fixture covers the whole key-type axis instead of one fixture per type.
/// </summary>
/// <remarks>
/// <c>AllowMultiple</c> throughout: the values below repeat across entities by construction (a byte column cannot hold 240 distinct values), and a unique
/// index physically cannot represent that — the engine now rejects it at write time rather than silently dropping the incumbent.
/// </remarks>
[Component("Typhon.Test.QPath.Data", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct QPathData
{
    [Index(AllowMultiple = true)] public int I;
    [Index(AllowMultiple = true)] public long L;
    [Index(AllowMultiple = true)] public float F;
    [Index(AllowMultiple = true)] public double D;
    [Index(AllowMultiple = true)] public uint U;
    [Index(AllowMultiple = true)] public short S;
    [Index(AllowMultiple = true)] public sbyte SB;
    [Index(AllowMultiple = true)] public byte B;
}

[Archetype]
class QPathUnit : Archetype<QPathUnit>
{
    public static readonly Comp<QPathData> Data = Register<QPathData>();
}

/// <summary>Versioned twin of <see cref="QPathData"/>: only a Versioned archetype promises snapshot isolation, so only it can show a phantom.</summary>
[Component("Typhon.Test.QPath.Ver", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct QPathVer
{
    [Index(AllowMultiple = true)] public int I;
    public QPathVer(int i) { I = i; }
}

[Archetype]
class QPathVerUnit : Archetype<QPathVerUnit>
{
    public static readonly Comp<QPathVer> Data = Register<QPathVer>();
}

// ── A tree whose indexed component is declared by only PART of the subtree ─────────────────────────────────────────────────────────────────────────────────
// The root carries no Loot; both children do. A query over the root must answer from the children and ignore the root, rather than throwing because the root
// has no index for a component it does not have.

[Component("Typhon.Test.QPath.Base", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct QPathCreature
{
    [Index(AllowMultiple = true)] public int Hp;
    public QPathCreature(int hp) { Hp = hp; }
}

[Component("Typhon.Test.QPath.Loot", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct QPathLoot
{
    [Index(AllowMultiple = true)] public int Rarity;
    public QPathLoot(int rarity) { Rarity = rarity; }
}

[Archetype]
class QPathCreatureUnit : Archetype<QPathCreatureUnit>
{
    public static readonly Comp<QPathCreature> Body = Register<QPathCreature>();
}

[Archetype]
class QPathMonsterUnit : Archetype<QPathMonsterUnit, QPathCreatureUnit>
{
    public static readonly Comp<QPathLoot> Loot = Register<QPathLoot>();
}

[Archetype]
class QPathCritterUnit : Archetype<QPathCritterUnit, QPathCreatureUnit>
{
    public static readonly Comp<QPathLoot> Loot = Register<QPathLoot>();
}

#endregion

/// <summary>
/// Asserts that the selective B+Tree scan (Path A) and the zone-map + SoA scan (Path B) answer every query identically, and that both answer it correctly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this fixture exists.</b> #629 pinned stream selection off, which made Path A unreachable; re-enabling it (#675) turns on a code path that had never
/// executed in CI. Two defects were already known when the issue was filed, and three more were found by reading the path before switching it on — a missing
/// MVCC born/died gate, key types with no typed tree behind them, and a <c>ULong</c> index whose keys are stored as SIGNED longs. Not one of those would have
/// been caught by the ~4 000 tests that existed, because every one of them asserted the query RESULT of whichever path the planner happened to pick.
/// </para>
/// <para>
/// <b>Why the path is forced rather than provoked.</b> The choice is made from an estimate (<c>EstimateClusterSelectivity(...) &lt; 0.05f</c>). A test that
/// writes a very selective predicate and hopes Path A is taken asserts nothing durable: the day a statistic shifts it silently becomes a second Path B test,
/// still green, still counted in the coverage number. <see cref="QueryPathProbe"/> makes the path an INPUT, and every Path A run here additionally asserts
/// that the selective scan actually ran — so "the fixture quietly stopped testing Path A" is itself a failure.
/// </para>
/// <para>
/// <b>Why both an absolute and a differential oracle.</b> The expected set is computed from the spawn function, so a defect common to BOTH paths still fails.
/// Comparing the two paths on top of that localises a defect to one of them. Either alone is weaker: a pure differential passes when both paths are wrong the
/// same way, and a pure absolute check on one path says nothing about the other.
/// </para>
/// <para>
/// <b>Axes covered.</b> Key type (8 of the 9 streamable ones) × comparison operator (all 6) × threshold sign (negative, zero, positive) × range windows, plus
/// the descending and OrderBy variants. <c>Bool</c> and <c>String64</c> are deliberately absent — <see cref="KeyRange.IsStreamable"/> rejects them, so they
/// have no Path A to compare against, and <see cref="NonStreamableKeyType_StillAnswersCorrectly"/> covers them where they do run. <c>ULong</c> became
/// streamable with #676 and is covered end to end by <c>UlongIndexOrderingTests</c>; giving it a column here so it joins this fixture's operator × sign
/// matrix is worthwhile follow-up, and is why that is called out rather than left implied.
/// </para>
/// </remarks>
[TestFixture]
class QueryPathEquivalenceTests : TestBase<QueryPathEquivalenceTests>
{
    private const int EntityCount = 240;   // spans 4 clusters at ClusterSize 64, so cluster-boundary handling is exercised rather than assumed

    /// <summary>Field values for entity <paramref name="i"/>. The single definition of truth the absolute oracle is computed from.</summary>
    /// <remarks>
    /// Every column is distinctive per entity where its width allows, straddles zero where its type allows, and uses a different multiplier per column — so a
    /// field-offset mix-up surfaces as another column's value rather than as a plausible one.
    /// </remarks>
    private static QPathData ValuesFor(int i)
    {
        var v = i - EntityCount / 2;   // -120 .. 119
        return new QPathData
        {
            I = v,
            L = v * 1_000_000_007L,
            F = v * 1.5f,
            D = v * 2.5,
            U = (uint)(v + 200),
            S = (short)(v * 100),
            SB = (sbyte)(v / 4),
            B = (byte)(i % 251)
        };
    }

    // ── Axes ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One key type under test: the field to predicate on, and thresholds either side of zero within the spawned range.</summary>
    internal sealed record KeyAxis(string Field, KeyType KeyType, object Negative, object Zero, object Positive, object WindowLow, object WindowHigh)
    {
        public override string ToString() => $"{KeyType}_{Field}";
    }

    private static readonly KeyAxis[] Axes =
    [
        new("I", KeyType.Int, -60, 0, 60, -20, 20),
        new("L", KeyType.Long, -60_000_000_420L, 0L, 60_000_000_420L, -20_000_000_140L, 20_000_000_140L),
        new("F", KeyType.Float, -60.0f, 0.0f, 60.0f, -20.0f, 20.0f),
        new("D", KeyType.Double, -60.0, 0.0, 60.0, -20.0, 20.0),
        new("U", KeyType.UInt, 100u, 200u, 300u, 150u, 250u),
        new("S", KeyType.Short, (short)-6000, (short)0, (short)6000, (short)-2000, (short)2000),
        new("SB", KeyType.SByte, (sbyte)-25, (sbyte)0, (sbyte)25, (sbyte)-10, (sbyte)10),
        new("B", KeyType.Byte, (byte)10, (byte)125, (byte)240, (byte)60, (byte)190)
    ];

    private static readonly CompareOp[] Ops =
    [
        CompareOp.Equal, CompareOp.NotEqual, CompareOp.GreaterThan, CompareOp.GreaterThanOrEqual, CompareOp.LessThan, CompareOp.LessThanOrEqual
    ];

    public static IEnumerable<TestCaseData> AxisCases()
    {
        foreach (var axis in Axes)
        {
            yield return new TestCaseData(axis).SetName($"{{m}}_{axis}");
        }
    }

    // ── The tests ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every operator × threshold sign, on both paths, against a set computed from the spawn function.</summary>
    [TestCaseSource(nameof(AxisCases))]
    public void PathsAgreeWithEachOtherAndWithTheData(KeyAxis axis)
    {
        using var dbe = SetupEngine();
        var ids = SpawnAll(dbe);

        using var tx = dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            foreach (var op in Ops)
            {
                CheckSingle(tx, ids, axis, op, "neg", axis.Negative);
                CheckSingle(tx, ids, axis, op, "zero", axis.Zero);
                CheckSingle(tx, ids, axis, op, "pos", axis.Positive);
            }
        });

        IndexDataOracle.AssertIndexAgreesWithData<QPathUnit>(dbe, $"after {EntityCount} spawns ({axis})");
    }

    /// <summary>
    /// The two-sided window — the shape #675 actually failed on.
    /// </summary>
    /// <remarks>
    /// <c>Value &gt;= -20f &amp;&amp; Value &lt;= 20f</c> returned 71 rows instead of 41 because the planner intersected the two predicates' bounds with signed
    /// <c>long</c> comparison over raw IEEE bit patterns: <c>max(bits(-20f), bits(float.MinValue))</c> is <c>bits(float.MinValue)</c>, so the lower bound never
    /// tightened. It needs BOTH predicates on ONE field to reproduce — a single-sided test cannot, which is why the operator loop above is not enough on its
    /// own.
    /// </remarks>
    [TestCaseSource(nameof(AxisCases))]
    public void TwoSidedWindow_IsNotWidenedByBoundIntersection(KeyAxis axis)
    {
        using var dbe = SetupEngine();
        var ids = SpawnAll(dbe);

        using var tx = dbe.CreateQuickTransaction();
        var predicate = And(
            Compare(axis.Field, CompareOp.GreaterThanOrEqual, axis.WindowLow),
            Compare(axis.Field, CompareOp.LessThanOrEqual, axis.WindowHigh));

        var expected = ExpectedWindow(ids, axis);

        var selective = RunForced(tx, predicate, ClusterScanPath.Selective, out var selectiveScans);
        var fullScan = RunForced(tx, predicate, ClusterScanPath.FullScan, out _);

        Assert.Multiple(() =>
        {
            Assert.That(selectiveScans, Is.GreaterThan(0), $"{axis}: Path A was requested but the planner never ran a selective scan — the query answered "
                + "from the SoA scan and this case is silently no longer testing Path A.");
            Assert.That(selective, Is.EquivalentTo(expected), $"{axis}: Path A over [{axis.WindowLow}, {axis.WindowHigh}]");
            Assert.That(fullScan, Is.EquivalentTo(expected), $"{axis}: Path B over [{axis.WindowLow}, {axis.WindowHigh}]");
        });
    }

    /// <summary>An ordered two-sided window: same bounds, but consumed by the K-way merge rather than the selective scan.</summary>
    /// <remarks>
    /// The merge reaches the bounds through a different branch (<c>ExecuteOrderedClustered</c>), so a bound bug fixed for the selective scan can survive here.
    /// This is the branch the originally reported <c>KWay_FloatOrdered_NegativeValues_CorrectOrder</c> failure ran through.
    /// </remarks>
    [TestCaseSource(nameof(AxisCases))]
    public void OrderedWindow_ReturnsTheWindowInOrder(KeyAxis axis)
    {
        using var dbe = SetupEngine();
        var ids = SpawnAll(dbe);

        using var tx = dbe.CreateQuickTransaction();
        var predicate = And(
            Compare(axis.Field, CompareOp.GreaterThanOrEqual, axis.WindowLow),
            Compare(axis.Field, CompareOp.LessThanOrEqual, axis.WindowHigh));

        var ordered = tx.Query<QPathUnit>().WhereField(predicate).OrderByField<QPathData, int>(d => d.I).ExecuteOrdered();
        var expected = ExpectedWindow(ids, axis);

        Assert.Multiple(() =>
        {
            Assert.That(ordered, Is.EquivalentTo(expected), $"{axis}: ordered query must return the same SET as the unordered one");

            var keys = new List<int>(ordered.Count);
            foreach (var id in ordered)
            {
                keys.Add(tx.Open(id).Read(QPathUnit.Data).I);
            }

            Assert.That(keys, Is.Ordered, $"{axis}: ordered query must return the window sorted by I");
        });
    }

    /// <summary>
    /// A non-streamable key type must still answer correctly rather than empty, whichever path is forced.
    /// </summary>
    /// <remarks>
    /// The guard that keeps such types off Path A lives in <see cref="KeyRange.IsStreamable"/>, and a guard nobody exercises is a guard that gets deleted.
    /// <c>ULong</c> was on that list until #676: its index was an <c>L64BTree&lt;long&gt;</c>, so the full range <c>[0, ulong.MaxValue]</c> encoded to the
    /// signed range <c>[0, -1]</c> — empty — and Path A would have answered nothing at all, silently. The trees are genuinely ulong-keyed now and it streams;
    /// <c>UlongIndexOrderingTests</c> covers it end to end across the sign boundary. <c>Bool</c> and <c>String64</c> remain genuinely non-streamable.
    /// </remarks>
    [Test]
    public void NonStreamableKeyType_StillAnswersCorrectly()
    {
        Assert.Multiple(() =>
        {
            Assert.That(KeyRange.IsStreamable(KeyType.ULong), Is.True, "#676 gave the ULong trees a genuine ulong key, so they range-scan correctly");
            Assert.That(KeyRange.IsStreamable(KeyType.Bool), Is.False, "no typed B+Tree scan exists for Bool");
            Assert.That(KeyRange.IsStreamable(KeyType.String64), Is.False, "no typed B+Tree scan exists for String64");
            Assert.That(KeyRange.IsStreamable(KeyType.Float), Is.True);
            Assert.That(KeyRange.IsStreamable(KeyType.Double), Is.True);
            Assert.That(KeyRange.IsStreamable(KeyType.UInt), Is.True);
        });

        using var dbe = SetupEngine();
        var ids = SpawnAll(dbe);

        // Forcing Path A must NOT be able to route a non-streamable key type there: the field never becomes a primary stream, so the force is inert and the
        // query still answers from the SoA scan.
        using var tx = dbe.CreateQuickTransaction();
        var expected = new List<EntityId>();
        for (var i = 0; i < EntityCount; i++)
        {
            if (ValuesFor(i).B > 200)
            {
                expected.Add(ids[i]);
            }
        }

        var got = RunForced(tx, Compare("B", CompareOp.GreaterThan, (byte)200), ClusterScanPath.Selective, out _);
        Assert.That(got, Is.EquivalentTo(expected), "a byte-keyed query must answer from the data whichever path is requested");
    }

    /// <summary>
    /// Neither path may emit an entity that was committed after the reader's snapshot.
    /// </summary>
    /// <remarks>
    /// Path B grew this gate for #674; Path A never had one, and nothing tested either. Both scans walk CURRENT occupancy and read the committed HEAD column,
    /// so without the <c>BornTSN</c> check a Versioned archetype leaks exactly the phantom a fixed snapshot is specified to prevent — and it leaks it on
    /// whichever path the planner picked, which is why this runs on both.
    /// </remarks>
    [TestCase(ClusterScanPath.Selective)]
    [TestCase(ClusterScanPath.FullScan)]
    public void NeitherPathEmitsAPhantom(ClusterScanPath path)
    {
        using var dbe = SetupEngine();

        using (var seed = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 100; i++)
            {
                seed.Spawn<QPathVerUnit>(QPathVerUnit.Data.Set(new QPathVer(i)));
            }

            seed.Commit();
        }

        // Reader's snapshot is fixed here, before the writer commits.
        using var reader = dbe.CreateQuickTransaction();
        var before = RunForcedVer(reader, ClusterScanPath.FullScan);

        using (var writer = dbe.CreateQuickTransaction())
        {
            for (var i = 100; i < 150; i++)
            {
                writer.Spawn<QPathVerUnit>(QPathVerUnit.Data.Set(new QPathVer(i)));
            }

            writer.Commit();
        }

        var after = RunForcedVer(reader, path);
        Assert.That(after, Has.Count.EqualTo(before.Count),
            $"{path}: the reader's snapshot predates the second commit, so those 50 entities must stay invisible to it");
    }

    // ── A component declared by only part of the subtree ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A polymorphic query over an archetype whose subtree carries the where-component only on SOME descendants must answer from those descendants, not throw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The archetype mask is the queried archetype's whole subtree and <c>WhereField</c> never narrows it, so the root — which has no <see cref="QPathLoot"/>
    /// at all — reached the guard that exists for an archetype whose component has no index home, and the query died with
    /// <c>InvalidOperationException</c>. The guard's own comment recorded it as unreachable ("instrumenting the old fallthrough and running the full suite
    /// showed it was never entered"), which held only because no test queried a supertype on a component declared below it.
    /// </para>
    /// <para>
    /// The two cases are now distinguished: an archetype that does not CARRY the component is skipped (it cannot match a predicate on a field it does not
    /// have); an archetype that carries it but has no index home still throws, because answering that one from the cluster scan alone would silently omit its
    /// entities — the #663 shape the guard was built for.
    /// </para>
    /// </remarks>
    [Test]
    public void QueryOverAncestor_WhenOnlyDescendantsDeclareTheComponent_AnswersFromThem()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<QPathCreatureUnit>(QPathCreatureUnit.Body.Set(new QPathCreature(1)));
            tx.Spawn<QPathMonsterUnit>(QPathCreatureUnit.Body.Set(new QPathCreature(2)), QPathMonsterUnit.Loot.Set(new QPathLoot(9)));
            tx.Spawn<QPathCritterUnit>(QPathCreatureUnit.Body.Set(new QPathCreature(3)), QPathCritterUnit.Loot.Set(new QPathLoot(9)));
            tx.Spawn<QPathCritterUnit>(QPathCreatureUnit.Body.Set(new QPathCreature(4)), QPathCritterUnit.Loot.Set(new QPathLoot(1)));
            tx.Commit();
        }

        using var q = dbe.CreateQuickTransaction();

        var hits = q.Query<QPathCreatureUnit>().WhereField<QPathLoot>(l => l.Rarity == 9).Execute();
        Assert.That(hits, Has.Count.EqualTo(2), "both descendants that declare the component must contribute; the root carries no Loot and cannot match");

        // Controls: the leaf query is unchanged, and a component the WHOLE subtree carries still answers over all three archetypes.
        var leafHits = q.Query<QPathMonsterUnit>().WhereField<QPathLoot>(l => l.Rarity == 9).Execute();
        Assert.That(leafHits, Has.Count.EqualTo(1), "querying the declaring archetype directly is unaffected");

        var wholeSubtree = q.Query<QPathCreatureUnit>().WhereField<QPathCreature>(b => b.Hp >= 1).Execute();
        Assert.That(wholeSubtree, Has.Count.EqualTo(4), "a component the whole subtree carries still spans every archetype in it");
    }

    // ── Machinery ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QPathData>();
        dbe.RegisterComponentFromAccessor<QPathVer>();
        dbe.RegisterComponentFromAccessor<QPathCreature>();
        dbe.RegisterComponentFromAccessor<QPathLoot>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId[] SpawnAll(DatabaseEngine dbe)
    {
        var ids = new EntityId[EntityCount];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            var v = ValuesFor(i);
            ids[i] = tx.Spawn<QPathUnit>(QPathUnit.Data.Set(in v));
        }

        tx.Commit();
        return ids;
    }

    /// <summary>
    /// Left to the planner, a highly selective predicate must take Path B — <c>SelectivePathThreshold</c> is zero because Path A is not faster at any
    /// selectivity on any distribution measured.
    /// </summary>
    /// <remarks>
    /// This pins a decision, not a mechanism: every other case in this fixture FORCES its path, so nothing here would notice the planner silently reverting to
    /// choosing Path A — it would simply get slower. A 1-of-240 predicate is the shape the old <c>0.05f</c> threshold selected Path A for most confidently, and
    /// the one where it measured 15–63 % slower than Path B.
    /// </remarks>
    [Test]
    public void ThePlannerDoesNotSelectPathA()
    {
        using var dbe = SetupEngine();
        SpawnAll(dbe);

        // Without this the statistics are empty, EstimateClusterSelectivity returns its 0.5 "unknown" fallback, and the planner takes Path B for a reason that
        // has nothing to do with the threshold — the assertions below would pass at ANY threshold. The cluster scan reads the archetype's active cluster list,
        // which is settled at the tick fence, so the fence has to precede the rebuild.
        dbe.WriteTickFence(1);
        var clusterState = dbe._archetypeStates[ArchetypeRegistry.GetMetadata<QPathUnit>().ArchetypeId].ClusterState;
        StatisticsRebuilder.RebuildClusterAll(clusterState, dbe.EpochManager, 1);

        using var tx = dbe.CreateQuickTransaction();

        QueryPathProbe.Reset();
        try
        {
            // Left at ClusterScanPath.Planner deliberately — the point is what the planner does when nobody forces it.
            var hits = tx.Query<QPathUnit>().WhereField<QPathData>(d => d.I >= 119).Execute();

            Assert.Multiple(() =>
            {
                Assert.That(hits, Has.Count.EqualTo(1), "sanity: the predicate really is 1-of-240, i.e. the most selective shape the old threshold covered");
                Assert.That(QueryPathProbe.SelectiveScans, Is.Zero, "the planner must not select Path A: measured never faster, and up to 8x slower");
                Assert.That(QueryPathProbe.FullScans, Is.GreaterThan(0), "and it must have actually scanned — a zero/zero result would prove nothing");
            });
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }

    /// <summary>Run one predicate on a forced path and report how many selective scans it actually performed.</summary>
    private static HashSet<EntityId> RunForced(Transaction tx, Expression<Func<QPathData, bool>> predicate, ClusterScanPath path, out int selectiveScans)
    {
        QueryPathProbe.Reset();
        QueryPathProbe.Forced = path;
        try
        {
            var result = tx.Query<QPathUnit>().WhereField(predicate).Execute();
            selectiveScans = QueryPathProbe.SelectiveScans;
            return result;
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }

    private static HashSet<EntityId> RunForcedVer(Transaction tx, ClusterScanPath path)
    {
        QueryPathProbe.Reset();
        QueryPathProbe.Forced = path;
        try
        {
            return tx.Query<QPathVerUnit>().WhereField<QPathVer>(d => d.I >= 0).Execute();
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }

    private static void CheckSingle(Transaction tx, EntityId[] ids, KeyAxis axis, CompareOp op, string signLabel, object threshold)
    {
        var predicate = Compare(axis.Field, op, threshold);
        var expected = Expected(ids, axis.Field, op, threshold);

        var selective = RunForced(tx, predicate, ClusterScanPath.Selective, out var selectiveScans);
        var fullScan = RunForced(tx, predicate, ClusterScanPath.FullScan, out _);

        var what = $"{axis} {op} {signLabel}({threshold})";

        // NotEqual cannot narrow a range, so SelectPrimaryStream declines it and SelectFullScanStream supplies the field over its full extent — Path A is still
        // reachable, just not selective. If neither selector proposes anything the force is inert, and saying so is the point of this assertion.
        Assert.That(selectiveScans, Is.GreaterThan(0), $"{what}: Path A was requested but no selective scan ran — this case is no longer testing Path A.");
        Assert.That(selective, Is.EquivalentTo(expected), $"{what}: Path A disagrees with the data");
        Assert.That(fullScan, Is.EquivalentTo(expected), $"{what}: Path B disagrees with the data");
    }

    // ── Oracles, computed from ValuesFor rather than from the engine ──────────────────────────────────────────────────────────────────────────────────────

    private static List<EntityId> Expected(EntityId[] ids, string field, CompareOp op, object threshold)
    {
        var result = new List<EntityId>();
        for (var i = 0; i < EntityCount; i++)
        {
            if (Holds(Read(ValuesFor(i), field), op, threshold))
            {
                result.Add(ids[i]);
            }
        }

        return result;
    }

    private static List<EntityId> ExpectedWindow(EntityId[] ids, KeyAxis axis)
    {
        var result = new List<EntityId>();
        for (var i = 0; i < EntityCount; i++)
        {
            var value = Read(ValuesFor(i), axis.Field);
            if (Holds(value, CompareOp.GreaterThanOrEqual, axis.WindowLow) && Holds(value, CompareOp.LessThanOrEqual, axis.WindowHigh))
            {
                result.Add(ids[i]);
            }
        }

        return result;
    }

    /// <summary>The field's value boxed as <see cref="IComparable"/>, preserving its OWN type. Test-side only — clarity beats allocation here.</summary>
    /// <remarks>
    /// Each arm is cast to <see cref="IComparable"/> explicitly, and that is load-bearing rather than noise. Without the casts the switch expression has a
    /// natural type — <c>double</c>, the best common type of every arm — so the compiler widens each field to <c>double</c> and boxes THAT. Every comparison
    /// then ran double-against-int and threw, and had the thresholds also been doubles it would instead have compared silently-widened values and passed while
    /// testing nothing. An oracle that quietly changes the type of the thing it is checking is worse than no oracle.
    /// </remarks>
    private static IComparable Read(QPathData d, string field) =>
        field switch
        {
            "I" => (IComparable)d.I,
            "L" => (IComparable)d.L,
            "F" => (IComparable)d.F,
            "D" => (IComparable)d.D,
            "U" => (IComparable)d.U,
            "S" => (IComparable)d.S,
            "SB" => (IComparable)d.SB,
            "B" => (IComparable)d.B,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "unknown field")
        };

    private static bool Holds(IComparable value, CompareOp op, object threshold)
    {
        var c = value.CompareTo(threshold);
        return op switch
        {
            CompareOp.Equal => c == 0,
            CompareOp.NotEqual => c != 0,
            CompareOp.GreaterThan => c > 0,
            CompareOp.GreaterThanOrEqual => c >= 0,
            CompareOp.LessThan => c < 0,
            CompareOp.LessThanOrEqual => c <= 0,
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "unknown operator")
        };
    }

    // ── Predicate construction ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Built rather than written out: the alternative is 8 key types × 6 operators × 3 signs of hand-written lambdas, and a matrix that costs 150 near-identical
    // lines per axis added is a matrix nobody extends.

    private static Expression<Func<QPathData, bool>> Compare(string field, CompareOp op, object threshold)
    {
        var param = Expression.Parameter(typeof(QPathData), "d");
        var member = Expression.Field(param, field);
        var constant = Expression.Constant(threshold, member.Type);
        Expression body = op switch
        {
            CompareOp.Equal => Expression.Equal(member, constant),
            CompareOp.NotEqual => Expression.NotEqual(member, constant),
            CompareOp.GreaterThan => Expression.GreaterThan(member, constant),
            CompareOp.GreaterThanOrEqual => Expression.GreaterThanOrEqual(member, constant),
            CompareOp.LessThan => Expression.LessThan(member, constant),
            CompareOp.LessThanOrEqual => Expression.LessThanOrEqual(member, constant),
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "unknown operator")
        };

        return Expression.Lambda<Func<QPathData, bool>>(body, param);
    }

    private static Expression<Func<QPathData, bool>> And(Expression<Func<QPathData, bool>> left, Expression<Func<QPathData, bool>> right)
    {
        var param = left.Parameters[0];
        var rightBody = new ParameterSwap(right.Parameters[0], param).Visit(right.Body);
        return Expression.Lambda<Func<QPathData, bool>>(Expression.AndAlso(left.Body, rightBody), param);
    }

    /// <summary>Rewrites one lambda's parameter to another's, so two independently built predicates can share one parameter in an <c>AndAlso</c>.</summary>
    private sealed class ParameterSwap(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
