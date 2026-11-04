using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.FieldWalk.Keyed", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SfKeyed
{
    [Index]
    public int Key;

    public long Value;
}

[Component("Typhon.Test.FieldWalk.Flag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SfFlag
{
    public int Value;
}

[Archetype]
class SfGateArch : Archetype<SfGateArch>
{
    public static readonly Comp<SfKeyed> Keyed = Register<SfKeyed>();
    public static readonly Comp<SfFlag> Flag = Register<SfFlag>();
}

// A Versioned component under a non-unique index: a spawn may leave it out, and a later write supply it.
[Component("Typhon.Test.FieldWalk.Late", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SfLate
{
    [Index(AllowMultiple = true)]
    public int Key;

    public long Value;
}

[Archetype]
class SfLateArch : Archetype<SfLateArch>
{
    public static readonly Comp<SfLate> Late = Register<SfLate>();
    public static readonly Comp<SfFlag> Flag = Register<SfFlag>();
}

[Component("Typhon.Test.FieldWalk.SpTag", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SfSpTag
{
    [Index]
    public int Key;

    public long Value;
}

[Archetype]
class SfSpatialArch : Archetype<SfSpatialArch>
{
    public static readonly Comp<SwPos> Pos = Register<SwPos>();
    public static readonly Comp<SfSpTag> Tag = Register<SfSpTag>();
}

/// <summary>
/// <c>foreach</c> over a <c>WhereField</c> query streams the matches in storage order, a cluster at a time at the transaction's snapshot, and yields what
/// <c>Execute()</c> returns: whichever path the planner takes, across fences, with the transaction's own writes, the enabled gate and a spatial archetype.
/// </summary>
[TestFixture]
[NonParallelizable]   // QueryPathProbe.Forced and the walk counters are static or per thread
class StreamingFieldForeachTests : SnapshotWalkTestBase<StreamingFieldForeachTests>
{
    private const string FieldMarker = "WALK-02";

    [TearDown]
    public void ResetSeams()
    {
        QueryPathProbe.Forced = ClusterScanPath.Planner;
        SnapshotWalker.IgnoreChangedClustersForTest = false;
    }

    private DatabaseEngine BuildField()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SwVer>();
        dbe.RegisterComponentFromAccessor<SwSv>();
        dbe.RegisterComponentFromAccessor<SfKeyed>();
        dbe.RegisterComponentFromAccessor<SfFlag>();
        dbe.RegisterComponentFromAccessor<SwPos>();
        dbe.RegisterComponentFromAccessor<SfSpTag>();
        dbe.RegisterComponentFromAccessor<SfLate>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(1000f, 1000f), cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static HashSet<EntityId> Stream<TArch>(EcsQuery<TArch> query) where TArch : class
    {
        var seen = new HashSet<EntityId>();
        foreach (var e in query)
        {
            Assert.That(seen.Add(e.Id), Is.True, $"{FieldMarker}: a row yielded twice ({e.Id})");
        }

        return seen;
    }

    private static void AssertSameSet(HashSet<EntityId> expected, HashSet<EntityId> seen, string what)
    {
        var missing = expected.Except(seen).Take(5).ToList();
        var extra = seen.Except(expected).Take(5).ToList();
        Assert.That(missing.Count + extra.Count, Is.Zero,
            $"{FieldMarker}: {what} — missing {string.Join(", ", missing)}; extra {string.Join(", ", extra)}");
    }

    /// <summary>Both paths, both storage modes: the stream is exactly <c>Execute()</c>'s set, over more than one batch.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void AFieldWalk_YieldsWhatExecuteReturns([Values(ClusterScanPath.Planner, ClusterScanPath.Selective, ClusterScanPath.FullScan)] ClusterScanPath path,
        [Values] bool versioned)
    {
        var dbe = BuildField();
        QueryPathProbe.Forced = path;
        HashSet<EntityId> expected, seen;
        if (versioned)
        {
            SpawnVer(dbe, 0, 10_000);
            EcsQuery<SwVerArch>.ForeachWalk.FillsForTest = 0;
            using var read = dbe.CreateReadOnlyTransaction();
            expected = read.Query<SwVerArch>().WhereField<SwVer>(r => r.Key >= 2000 && r.Key < 8000).Execute();
            seen = Stream(read.Query<SwVerArch>().WhereField<SwVer>(r => r.Key >= 2000 && r.Key < 8000));
            Assert.That(EcsQuery<SwVerArch>.ForeachWalk.FillsForTest, Is.GreaterThan(1), "premise: more than one batch");
        }
        else
        {
            SpawnSv(dbe, 0, 10_000);
            EcsQuery<SwSvArch>.ForeachWalk.FillsForTest = 0;
            using var read = dbe.CreateReadOnlyTransaction();
            expected = read.Query<SwSvArch>().WhereField<SwSv>(r => r.Key >= 2000 && r.Key < 8000).Execute();
            seen = Stream(read.Query<SwSvArch>().WhereField<SwSv>(r => r.Key >= 2000 && r.Key < 8000));
            Assert.That(EcsQuery<SwSvArch>.ForeachWalk.FillsForTest, Is.GreaterThan(1), "premise: more than one batch");
        }

        Assert.That(expected.Count, Is.EqualTo(6000), "premise: Execute's answer");
        AssertSameSet(expected, seen, "the stream against Execute");
    }

    /// <summary>
    /// Changes committed after the transaction's snapshot — before the loop, then between two of its batches: matching rows moved out of the range and
    /// destroyed, rows moved into it, new matching spawns. The stream is the snapshot's matches, whichever path; on the selective path the clusters of the
    /// rows moved out before the tree was read hold no candidate any more, and are read because a change above the snapshot touched them.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("WALK-02")]
    public void AFieldWalk_YieldsTheSnapshotsMatches_ChangedBeforeAndDuringTheLoop(
        [Values(ClusterScanPath.Planner, ClusterScanPath.Selective, ClusterScanPath.FullScan)] ClusterScanPath path)
    {
        var dbe = BuildField();
        var (expected, seen) = WalkAcrossChanges(dbe, path);
        AssertSameSet(expected, seen, "the snapshot's matches");
    }

    [Test]
    [CancelAfter(60_000)]
    [RuleMutant("WALK-02")]
    public void Mutant_ACandidateWalkThatSkipsChangedClusters_IsRejected()
    {
        var dbe = BuildField();
        SnapshotWalker.IgnoreChangedClustersForTest = true;
        var (expected, seen) = WalkAcrossChanges(dbe, ClusterScanPath.Selective);
        RuleMutants.AssertDetects("WALK-02", FieldMarker, () => AssertSameSet(expected, seen, "the snapshot's matches"));
    }

    private static (HashSet<EntityId> Expected, HashSet<EntityId> Seen) WalkAcrossChanges(DatabaseEngine dbe, ClusterScanPath path)
    {
        // Even keys only: the index is unique, and the changes below need free keys inside the range.
        var byKey = new Dictionary<int, EntityId>();
        for (var first = 0; first < 10_000; first += Batch)
        {
            using var tx0 = dbe.CreateQuickTransaction();
            for (var i = first; i < first + Batch; i++)
            {
                var row = new SwVer { Key = 2 * i, Value = i };
                byKey[2 * i] = tx0.Spawn<SwVerArch>(SwVerArch.Row.Set(in row));
            }

            Assert.That(tx0.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        QueryPathProbe.Forced = path;
        var expected = byKey.Where(kv => kv.Key is >= 4000 and < 16000).Select(kv => kv.Value).ToHashSet();

        using var tx = dbe.CreateReadOnlyTransaction();

        // Before the loop, near the range's start: 100 matching rows move out, 50 are destroyed, 50 from outside move in, 50 matching are spawned.
        Change(dbe, byKey, 4000, 5000, 18000);
        dbe.WriteTickFence(2);

        var seen = new HashSet<EntityId>();
        var changedMidLoop = false;
        foreach (var e in tx.Query<SwVerArch>().WhereField<SwVer>(r => r.Key >= 4000 && r.Key < 16000))
        {
            Assert.That(seen.Add(e.Id), Is.True, $"{FieldMarker}: a row yielded twice ({e.Id})");
            if (!changedMidLoop && seen.Count == 100)
            {
                // Between two batches: the same changes near the range's end, on rows the walk has not reached.
                Change(dbe, byKey, 14000, 15000, 19000);
                dbe.WriteTickFence(3);
                changedMidLoop = true;
            }
        }

        Assert.That(changedMidLoop, Is.True, "premise: the loop ran past its first batch");
        return (expected, seen);
    }

    /// <summary>
    /// One committed transaction: the 100 rows of even keys from <paramref name="outFrom"/> leave the range, the 50 from <paramref name="destroyFrom"/> are
    /// destroyed, the 50 from <paramref name="inFrom"/> (outside it) enter it at odd keys, and 50 new rows are spawned inside it at odd keys.
    /// </summary>
    private static void Change(DatabaseEngine dbe, Dictionary<int, EntityId> byKey, int outFrom, int destroyFrom, int inFrom)
    {
        using var w = dbe.CreateQuickTransaction();
        for (var i = 0; i < 100; i++)
        {
            var row = new SwVer { Key = 100_000 + outFrom + 2 * i, Value = 0 };
            w.OpenMut(byKey[outFrom + 2 * i]).Set(SwVerArch.Row, in row);
        }

        for (var i = 0; i < 50; i++)
        {
            w.Destroy(byKey[destroyFrom + 2 * i]);
        }

        for (var i = 0; i < 50; i++)
        {
            var row = new SwVer { Key = outFrom + 2 * i + 1, Value = 0 };
            w.OpenMut(byKey[inFrom + 2 * i]).Set(SwVerArch.Row, in row);
        }

        for (var i = 0; i < 50; i++)
        {
            var row = new SwVer { Key = destroyFrom + 2 * i + 1, Value = 0 };
            w.Spawn<SwVerArch>(SwVerArch.Row.Set(in row));
        }

        Assert.That(w.Commit(), Is.True, "the changes");
    }

    /// <summary>
    /// Every entity of one cluster inside the range destroyed after the snapshot, and nothing else changed: their tree entries go at the publish (#1347),
    /// so on the selective path the cluster holds no candidate, and only its died watermark sends the walk to it. The snapshot still sees them all.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("WALK-02")]
    public void AClusterEmptiedByDestroysAfterTheSnapshot_IsStillRead()
    {
        var dbe = BuildField();
        var (expected, seen) = WalkAcrossAClusterOfDestroys(dbe);
        AssertSameSet(expected, seen, "the snapshot's matches, the destroyed cluster's included");
    }

    [Test]
    [CancelAfter(60_000)]
    [RuleMutant("WALK-02")]
    public void Mutant_ACandidateWalkThatSkipsAClusterOfDestroys_IsRejected()
    {
        var dbe = BuildField();
        SnapshotWalker.IgnoreChangedClustersForTest = true;
        var (expected, seen) = WalkAcrossAClusterOfDestroys(dbe);
        RuleMutants.AssertDetects("WALK-02", FieldMarker, () => AssertSameSet(expected, seen, "the snapshot's matches"));
    }

    private static (HashSet<EntityId> Expected, HashSet<EntityId> Seen) WalkAcrossAClusterOfDestroys(DatabaseEngine dbe)
    {
        var keys = SpawnVer(dbe, 0, 10_000);
        dbe.WriteTickFence(1);
        QueryPathProbe.Forced = ClusterScanPath.Selective;
        var expected = keys.Where(kv => kv.Value is >= 2000 and < 8000).Select(kv => kv.Key).ToHashSet();

        // A cluster whose entities are all inside the range.
        List<EntityId> victims = null;
        using (var probe = dbe.CreateReadOnlyTransaction())
        {
            foreach (var batch in probe.Walk<SwVerArch>())
            {
                var ids = new List<EntityId>();
                for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
                {
                    ids.Add(batch.GetEntityId(BitOperations.TrailingZeroCount(bits)));
                }

                if (victims == null && ids.Count > 1 && ids.TrueForAll(id => keys[id] is >= 4000 and < 6000))
                {
                    victims = ids;
                }
            }
        }

        Assert.That(victims, Is.Not.Null, "premise: a cluster inside the range");

        using var tx = dbe.CreateReadOnlyTransaction();
        using (var w = dbe.CreateQuickTransaction())
        {
            foreach (var id in victims)
            {
                w.Destroy(id);
            }

            Assert.That(w.Commit(), Is.True);
        }

        dbe.WriteTickFence(2);
        return (expected, Stream(tx.Query<SwVerArch>().WhereField<SwVer>(r => r.Key >= 2000 && r.Key < 8000)));
    }

    /// <summary>
    /// A Versioned component first supplied after the snapshot, at a matching value: the entity had no value at the snapshot, so it does not match there,
    /// as <c>Execute()</c> decides.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void AComponentSuppliedAfterTheSnapshot_DoesNotMatch([Values(ClusterScanPath.Planner, ClusterScanPath.Selective, ClusterScanPath.FullScan)] ClusterScanPath path)
    {
        var dbe = BuildField();
        var bare = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 200; i++)
            {
                var flag = new SfFlag { Value = i };
                if (i % 2 == 0)
                {
                    var late = new SfLate { Key = 7, Value = i };
                    tx.Spawn<SfLateArch>(SfLateArch.Late.Set(in late), SfLateArch.Flag.Set(in flag));
                }
                else
                {
                    bare.Add(tx.Spawn<SfLateArch>(SfLateArch.Flag.Set(in flag)));
                }
            }

            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        QueryPathProbe.Forced = path;
        using var read = dbe.CreateReadOnlyTransaction();
        using (var w = dbe.CreateQuickTransaction())
        {
            foreach (var id in bare)
            {
                var late = new SfLate { Key = 7, Value = 0 };
                w.OpenMut(id).Enable(SfLateArch.Late, in late);
            }

            Assert.That(w.Commit(), Is.True);
        }

        dbe.WriteTickFence(2);
        var expected = read.Query<SfLateArch>().WhereField<SfLate>(x => x.Key >= 0).Execute();
        var seen = Stream(read.Query<SfLateArch>().WhereField<SfLate>(x => x.Key >= 0));
        Assert.That(expected.Count, Is.EqualTo(100), "premise: Execute sees the 100 supplied at spawn");
        AssertSameSet(expected, seen, "a component supplied after the snapshot");
    }

    /// <summary>
    /// A SingleVersion archetype, which promises no snapshot: clusters the stream has not reached are emptied and reclaimed between two batches, and new
    /// rows outside the range are spawned. Every row present throughout is yielded once, and nothing outside the range is.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("WALK-01")]
    public void AReclamationDuringTheLoop_LosesAndRepeatsNothing([Values(ClusterScanPath.Planner, ClusterScanPath.Selective, ClusterScanPath.FullScan)] ClusterScanPath path)
    {
        var dbe = BuildField();
        var keys = SpawnSv(dbe, 0, 64 * 40);
        dbe.WriteTickFence(1);
        QueryPathProbe.Forced = path;

        var clusters = new List<List<EntityId>>();
        using (var probe = dbe.CreateReadOnlyTransaction())
        {
            foreach (var batch in probe.Walk<SwSvArch>())
            {
                var ids = new List<EntityId>();
                for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
                {
                    ids.Add(batch.GetEntityId(BitOperations.TrailingZeroCount(bits)));
                }

                clusters.Add(ids);
            }
        }

        Assert.That(clusters.Count, Is.GreaterThan(12), "premise: enough clusters");
        var emptied = clusters[10].Concat(clusters[11]).ToHashSet();

        using var tx = dbe.CreateReadOnlyTransaction();
        var seen = new HashSet<EntityId>();
        var reclaimed = false;
        foreach (var e in tx.Query<SwSvArch>().WhereField<SwSv>(r => r.Key < 2000))
        {
            Assert.That(seen.Add(e.Id), Is.True, $"a row yielded twice ({e.Id})");
            Assert.That(keys.TryGetValue(e.Id, out var key) && key < 2000, Is.True, $"a row outside the range ({e.Id})");
            if (!reclaimed && seen.Count == 64)
            {
                using (var w = dbe.CreateQuickTransaction())
                {
                    foreach (var id in emptied)
                    {
                        w.Destroy(id);
                    }

                    Assert.That(w.Commit(), Is.True);
                }

                dbe.WriteTickFence(2);
                SpawnSv(dbe, 100_000, 3 * 64);
                reclaimed = true;
            }
        }

        Assert.That(reclaimed, Is.True, "premise: the loop ran past its first batch");
        var stable = keys.Where(kv => kv.Value < 2000).Select(kv => kv.Key).ToHashSet();
        stable.ExceptWith(emptied);
        Assert.That(stable.Except(seen), Is.Empty, "every row present throughout is yielded");
    }

    /// <summary>A read-write transaction: its pending spawns that match, its own destroys hidden, and a <c>Where</c> lambda over the stream.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void OwnWrites_AndAWhereLambda_MatchExecute([Values(ClusterScanPath.Planner, ClusterScanPath.Selective, ClusterScanPath.FullScan)] ClusterScanPath path)
    {
        var dbe = BuildField();
        var keys = SpawnVer(dbe, 0, 3_000);
        QueryPathProbe.Forced = path;
        var byKey = keys.ToDictionary(kv => kv.Value, kv => kv.Key);

        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < 20; i++)
        {
            var row = new SwVer { Key = i % 2 == 0 ? 1500 + i : 50_000 + i, Value = 0 };
            tx.Spawn<SwVerArch>(SwVerArch.Row.Set(in row));
        }

        for (var k = 1000; k < 1010; k++)
        {
            tx.Destroy(byKey[k]);
        }

        var expected = tx.Query<SwVerArch>().WhereField<SwVer>(r => r.Key >= 500 && r.Key < 2500).Where<SwVer>(r => r.Key % 3 != 0).Execute();
        var seen = Stream(tx.Query<SwVerArch>().WhereField<SwVer>(r => r.Key >= 500 && r.Key < 2500).Where<SwVer>(r => r.Key % 3 != 0));
        Assert.That(expected.Count, Is.GreaterThan(1000), "premise: Execute's answer");
        AssertSameSet(expected, seen, "own writes and the lambda");
    }

    /// <summary>QENABLE-01: the predicate's component disabled on some rows, another component on others; plain, Disabled and Enabled queries.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void TheEnabledGate_MatchesExecute([Values(ClusterScanPath.Planner, ClusterScanPath.Selective, ClusterScanPath.FullScan)] ClusterScanPath path)
    {
        var dbe = BuildField();
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 1_000; i++)
            {
                var keyed = new SfKeyed { Key = i, Value = i };
                var flag = new SfFlag { Value = i };
                ids.Add(tx.Spawn<SfGateArch>(SfGateArch.Keyed.Set(in keyed), SfGateArch.Flag.Set(in flag)));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Count; i += 7)
            {
                tx.OpenMut(ids[i]).Disable(SfGateArch.Keyed);
            }

            for (var i = 3; i < ids.Count; i += 5)
            {
                tx.OpenMut(ids[i]).Disable(SfGateArch.Flag);
            }

            Assert.That(tx.Commit(), Is.True);
        }

        QueryPathProbe.Forced = path;
        using var read = dbe.CreateReadOnlyTransaction();
        AssertSameSet(read.Query<SfGateArch>().WhereField<SfKeyed>(k => k.Key < 800).Execute(),
            Stream(read.Query<SfGateArch>().WhereField<SfKeyed>(k => k.Key < 800)), "plain");
        AssertSameSet(read.Query<SfGateArch>().WhereField<SfKeyed>(k => k.Key < 800).Disabled<SfKeyed>().Execute(),
            Stream(read.Query<SfGateArch>().WhereField<SfKeyed>(k => k.Key < 800).Disabled<SfKeyed>()), "the predicate's component disabled");
        AssertSameSet(read.Query<SfGateArch>().WhereField<SfKeyed>(k => k.Key < 800).Disabled<SfFlag>().Execute(),
            Stream(read.Query<SfGateArch>().WhereField<SfKeyed>(k => k.Key < 800).Disabled<SfFlag>()), "another component disabled");
        AssertSameSet(read.Query<SfGateArch>().WhereField<SfKeyed>(k => k.Key < 800).Enabled<SfFlag>().Execute(),
            Stream(read.Query<SfGateArch>().WhereField<SfKeyed>(k => k.Key < 800).Enabled<SfFlag>()), "another component enabled");

        // The gate's per-slot half: a read-write transaction's own pending disables and enables, which the cluster's words do not show yet.
        using var rw = dbe.CreateQuickTransaction();
        for (var i = 1; i < 400; i += 11)
        {
            rw.OpenMut(ids[i]).Disable(SfGateArch.Keyed);
        }

        for (var i = 0; i < 400; i += 7)
        {
            rw.OpenMut(ids[i]).Enable(SfGateArch.Keyed);
        }

        AssertSameSet(rw.Query<SfGateArch>().WhereField<SfKeyed>(k => k.Key < 800).Execute(),
            Stream(rw.Query<SfGateArch>().WhereField<SfKeyed>(k => k.Key < 800)), "the transaction's own pending disables and enables");
        AssertSameSet(rw.Query<SfGateArch>().WhereField<SfKeyed>(k => k.Key < 800).Disabled<SfKeyed>().Execute(),
            Stream(rw.Query<SfGateArch>().WhereField<SfKeyed>(k => k.Key < 800).Disabled<SfKeyed>()), "pending, the predicate's component disabled");
    }

    /// <summary>A spatial archetype's matches are collected whole and handed out in batches: the same set as <c>Execute()</c>.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void ASpatialArchetype_MatchesExecute([Values(ClusterScanPath.Planner, ClusterScanPath.Selective, ClusterScanPath.FullScan)] ClusterScanPath path)
    {
        var dbe = BuildField();
        var rand = new Random(31);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 2_000; i++)
            {
                var pos = PointAt((float)(rand.NextDouble() * 990), (float)(rand.NextDouble() * 990));
                var tag = new SfSpTag { Key = i, Value = i };
                tx.Spawn<SfSpatialArch>(SfSpatialArch.Pos.Set(in pos), SfSpatialArch.Tag.Set(in tag));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        QueryPathProbe.Forced = path;
        EcsQuery<SfSpatialArch>.ForeachWalk.FillsForTest = 0;
        using var read = dbe.CreateReadOnlyTransaction();
        var expected = read.Query<SfSpatialArch>().WhereField<SfSpTag>(t => t.Key < 1500).Execute();
        var seen = Stream(read.Query<SfSpatialArch>().WhereField<SfSpTag>(t => t.Key < 1500));
        Assert.That(expected.Count, Is.EqualTo(1500), "premise: Execute's answer");
        AssertSameSet(expected, seen, "a spatial archetype");
        Assert.That(EcsQuery<SfSpatialArch>.ForeachWalk.FillsForTest, Is.GreaterThan(1), "handed out in batches");
    }

    /// <summary>A <c>break</c> after the first row reads one batch's clusters, not the archetype.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void ABreakAfterOneRow_ReadsOneBatch()
    {
        var dbe = BuildField();
        SpawnVer(dbe, 0, 10_000);
        QueryPathProbe.Forced = ClusterScanPath.FullScan;
        using var tx = dbe.CreateReadOnlyTransaction();
        SnapshotWalker.ClustersReadForTest = 0;
        foreach (var _ in tx.Query<SwVerArch>().WhereField<SwVer>(r => r.Key >= 0))
        {
            break;
        }

        var clusterSize = dbe._archetypeStates[Archetype<SwVerArch>.Metadata.ArchetypeId].ClusterState.Layout.ClusterSize;
        Assert.That(SnapshotWalker.ClustersReadForTest, Is.LessThanOrEqualTo(64 / clusterSize + 1), "the first batch, 64 rows: their clusters only");
    }

    /// <summary>
    /// A narrow range reads only the clusters that can hold it: the index's on the selective path, the zone maps' on the full scan, as the SoA scan
    /// prunes. A range over every key reads every cluster.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void ANarrowRange_ReadsOnlyTheClustersThatCanHoldIt()
    {
        var dbe = BuildField();
        SpawnVer(dbe, 0, 10_000);
        dbe.WriteTickFence(1);
        using var tx = dbe.CreateReadOnlyTransaction();
        var clusterSize = dbe._archetypeStates[Archetype<SwVerArch>.Metadata.ArchetypeId].ClusterState.Layout.ClusterSize;

        var read = new Dictionary<ClusterScanPath, int>();
        foreach (var path in new[] { ClusterScanPath.Selective, ClusterScanPath.FullScan })
        {
            QueryPathProbe.Forced = path;
            SnapshotWalker.ClustersReadForTest = 0;
            Assert.That(Stream(tx.Query<SwVerArch>().WhereField<SwVer>(r => r.Key >= 5000 && r.Key < 5010)), Has.Count.EqualTo(10));
            read[path] = SnapshotWalker.ClustersReadForTest;
        }

        QueryPathProbe.Forced = ClusterScanPath.FullScan;
        SnapshotWalker.ClustersReadForTest = 0;
        Assert.That(Stream(tx.Query<SwVerArch>().WhereField<SwVer>(r => r.Key >= 0)), Has.Count.EqualTo(10_000));
        var all = SnapshotWalker.ClustersReadForTest;

        Assert.That(read[ClusterScanPath.Selective], Is.LessThanOrEqualTo(2), "ten consecutive keys: the index names one cluster, two at most");
        Assert.That(read[ClusterScanPath.FullScan], Is.LessThanOrEqualTo(2), "the zone maps prune every other cluster");
        Assert.That(all, Is.GreaterThanOrEqualTo(10_000 / clusterSize), "a range over every key reads every cluster");
    }
}
