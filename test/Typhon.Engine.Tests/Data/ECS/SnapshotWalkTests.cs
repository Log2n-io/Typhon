using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.SnapshotWalk.Ver", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
unsafe struct SwVer
{
    [Index]
    public int Key;

    public long Value;
    public fixed byte Pad[40];
}

[Component("Typhon.Test.SnapshotWalk.Sv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
unsafe struct SwSv
{
    [Index]
    public int Key;

    public long Value;
    public fixed byte Pad[40];
}

[Archetype]
class SwVerArch : Archetype<SwVerArch>
{
    public static readonly Comp<SwVer> Row = Register<SwVer>();
}

[Archetype]
class SwSvArch : Archetype<SwSvArch>
{
    public static readonly Comp<SwSv> Row = Register<SwSv>();
}

// The mixed archetype has components of its own: one shared with SwVerArch would give its index two source archetypes, and an index walk over
// two archetypes is merged eagerly, holding no live cursor.
[Component("Typhon.Test.SnapshotWalk.MixVer", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SwMixVer
{
    public int Key;
    public long Value;
}

[Component("Typhon.Test.SnapshotWalk.MixSv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SwMixSv
{
    public int Key;
    public long Value;
}

[Archetype]
class SwMixedArch : Archetype<SwMixedArch>
{
    public static readonly Comp<SwMixVer> Ver = Register<SwMixVer>();
    public static readonly Comp<SwMixSv> Sv = Register<SwMixSv>();
}

[Component("Typhon.Test.SnapshotWalk.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SwPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Component("Typhon.Test.SnapshotWalk.Tag", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SwTag
{
    public int Key;
    public long Value;
}

[Archetype]
class SwSpatialArch : Archetype<SwSpatialArch>
{
    public static readonly Comp<SwPos> Pos = Register<SwPos>();
    public static readonly Comp<SwTag> Tag = Register<SwTag>();
}

[Component("Typhon.Test.SnapshotWalk.SvTag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct SwSvTag
{
    public int Key;
    public long Value;
}

[Archetype]
class SwSpatialSvArch : Archetype<SwSpatialSvArch>
{
    public static readonly Comp<SwPos> Pos = Register<SwPos>();
    public static readonly Comp<SwSvTag> Tag = Register<SwSvTag>();
}

/// <summary>Shared setup for the snapshot-walk fixtures: archetypes whose row <c>Key</c> is its spawn order and whose <c>Value</c> is <c>Key * 7</c>.</summary>
abstract class SnapshotWalkTestBase<T> : TestBase<T> where T : SnapshotWalkTestBase<T>
{
    protected const int Batch = 500;

    protected DatabaseEngine Build()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SwVer>();
        dbe.RegisterComponentFromAccessor<SwSv>();
        dbe.RegisterComponentFromAccessor<SwMixVer>();
        dbe.RegisterComponentFromAccessor<SwMixSv>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    protected DatabaseEngine BuildSpatial()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SwPos>();
        dbe.RegisterComponentFromAccessor<SwTag>();
        dbe.RegisterComponentFromAccessor<SwSvTag>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(1000f, 1000f), cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    protected static Dictionary<EntityId, int> SpawnVer(DatabaseEngine dbe, int from, int count)
    {
        var keys = new Dictionary<EntityId, int>();
        for (var first = from; first < from + count; first += Batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = first; i < Math.Min(first + Batch, from + count); i++)
            {
                var row = new SwVer { Key = i, Value = i * 7L };
                keys[tx.Spawn<SwVerArch>(SwVerArch.Row.Set(in row))] = i;
            }

            Assert.That(tx.Commit(), Is.True);
        }

        return keys;
    }

    protected static Dictionary<EntityId, int> SpawnSv(DatabaseEngine dbe, int from, int count)
    {
        var keys = new Dictionary<EntityId, int>();
        for (var first = from; first < from + count; first += Batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = first; i < Math.Min(first + Batch, from + count); i++)
            {
                var row = new SwSv { Key = i, Value = i * 7L };
                keys[tx.Spawn<SwSvArch>(SwSvArch.Row.Set(in row))] = i;
            }

            Assert.That(tx.Commit(), Is.True);
        }

        return keys;
    }

    protected static Dictionary<EntityId, int> SpawnMixed(DatabaseEngine dbe, int count)
    {
        var keys = new Dictionary<EntityId, int>();
        for (var first = 0; first < count; first += Batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = first; i < Math.Min(first + Batch, count); i++)
            {
                var ver = new SwMixVer { Key = i, Value = i * 7L };
                var sv = new SwMixSv { Key = i, Value = i * 11L };
                keys[tx.Spawn<SwMixedArch>(SwMixedArch.Ver.Set(in ver), SwMixedArch.Sv.Set(in sv))] = i;
            }

            Assert.That(tx.Commit(), Is.True);
        }

        return keys;
    }

    protected static SwPos PointAt(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    protected static Dictionary<EntityId, int> SpawnSpatial(DatabaseEngine dbe, int count, int seed)
    {
        var rand = new Random(seed);
        var keys = new Dictionary<EntityId, int>();
        for (var first = 0; first < count; first += Batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = first; i < Math.Min(first + Batch, count); i++)
            {
                var pos = PointAt((float)(rand.NextDouble() * 990), (float)(rand.NextDouble() * 990));
                var tag = new SwTag { Key = i, Value = i * 7L };
                keys[tx.Spawn<SwSpatialArch>(SwSpatialArch.Pos.Set(in pos), SwSpatialArch.Tag.Set(in tag))] = i;
            }

            Assert.That(tx.Commit(), Is.True);
        }

        return keys;
    }

    protected static (int SlotReferenced, int EpochProtected) Held(DatabaseEngine dbe)
    {
        dbe.MMF.GetMetrics().GetMemPageExtraInfo(out var census);
        return (census.SlotRefPageCount, census.EpochProtectedPageCount);
    }
}

/// <summary>
/// <c>foreach</c> over a query streams the entity maps in batches instead of collecting every match before the first: memory and pages do not grow with the
/// result, and its semantics — snapshot, own destroys, own spawns, read-your-writes — are today's.
/// </summary>
[TestFixture]
[NonParallelizable]   // ForeachWalk.FillsForTest is per thread, the census is engine-wide
class StreamingForeachTests : SnapshotWalkTestBase<StreamingForeachTests>
{
    [Test]
    [CancelAfter(60_000)]
    public void AWalkLargerThanABatch_SeesEveryRowOnce()
    {
        var dbe = Build();
        var keys = SpawnVer(dbe, 0, 10_000);
        EcsQuery<SwVerArch>.ForeachWalk.FillsForTest = 0;

        using var tx = dbe.CreateReadOnlyTransaction();
        var seen = new HashSet<EntityId>();
        foreach (var e in tx.Query<SwVerArch>())
        {
            Assert.That(seen.Add(e.Id), Is.True, "a row yielded twice");
            var row = e.Read(SwVerArch.Row);
            Assert.That(row.Key, Is.EqualTo(keys[e.Id]));
            Assert.That(row.Value, Is.EqualTo(row.Key * 7L));
        }

        Assert.That(seen.Count, Is.EqualTo(keys.Count));
        Assert.That(EcsQuery<SwVerArch>.ForeachWalk.FillsForTest, Is.GreaterThan(1), "premise: the walk took more than one batch");
    }

    [Test]
    [CancelAfter(60_000)]
    public void ABreakAfterOneRow_FillsOneBatch()
    {
        var dbe = Build();
        SpawnSv(dbe, 0, 10_000);
        EcsQuery<SwSvArch>.ForeachWalk.FillsForTest = 0;

        using var tx = dbe.CreateReadOnlyTransaction();
        foreach (var e in tx.Query<SwSvArch>())
        {
            Assert.That(e.Id.IsNull, Is.False);
            break;
        }

        Assert.That(EcsQuery<SwSvArch>.ForeachWalk.FillsForTest, Is.EqualTo(1), "a break after one row read more than the first batch");
    }

    [Test]
    [CancelAfter(60_000)]
    public void OwnSpawnsBeforeTheLoop_AreVisitedOnce_AndSpawnsDuringIt_AreNot()
    {
        var dbe = Build();
        var keys = SpawnVer(dbe, 0, 2_000);

        using var tx = dbe.CreateQuickTransaction();
        var own = new HashSet<EntityId>();
        for (var i = 0; i < 3; i++)
        {
            var row = new SwVer { Key = 100_000 + i, Value = 1 };
            own.Add(tx.Spawn<SwVerArch>(SwVerArch.Row.Set(in row)));
        }

        var seen = new HashSet<EntityId>();
        var spawnedDuring = new HashSet<EntityId>();
        foreach (var e in tx.Query<SwVerArch>())
        {
            Assert.That(seen.Add(e.Id), Is.True, "a row yielded twice");
            if (seen.Count % 500 == 1)
            {
                var row = new SwVer { Key = 200_000 + seen.Count, Value = 2 };
                spawnedDuring.Add(tx.Spawn<SwVerArch>(SwVerArch.Row.Set(in row)));
            }
        }

        Assert.That(seen.IsSupersetOf(own), Is.True, "an own spawn made before the loop was not visited");
        Assert.That(seen.Overlaps(spawnedDuring), Is.False, "a spawn made during the loop was visited");
        Assert.That(seen.Count, Is.EqualTo(keys.Count + own.Count));
    }

    [Test]
    [CancelAfter(60_000)]
    public void ADestroyDuringTheLoop_HidesARowNotYetVisited()
    {
        var dbe = Build();
        var keys = SpawnVer(dbe, 0, 5_000);

        using var tx = dbe.CreateQuickTransaction();
        var seen = new HashSet<EntityId>();
        HashSet<EntityId> destroyed = null;
        foreach (var e in tx.Query<SwVerArch>())
        {
            Assert.That(destroyed == null || !destroyed.Contains(e.Id), Is.True, "a row the loop destroyed before reaching it was yielded");
            seen.Add(e.Id);
            if (destroyed == null)
            {
                // Rows not yet visited — some in the batch already filled, most not yet filled.
                var current = e.Id;
                destroyed = keys.Keys.Where(id => id != current).Take(300).ToHashSet();
                foreach (var id in destroyed)
                {
                    tx.Destroy(id);
                }
            }
        }

        Assert.That(seen.Count, Is.EqualTo(keys.Count - destroyed!.Count));
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EMAP-02")]
    public void AMapSplitBetweenFills_LosesAndRepeatsNothing()
    {
        var dbe = Build();
        var keys = SpawnVer(dbe, 0, 3_000);
        var map = dbe._archetypeStates[Archetype<SwVerArch>.Metadata.ArchetypeId].EntityMap;
        var bucketsBefore = map.BucketCount;

        using var tx = dbe.CreateReadOnlyTransaction();
        var seen = new HashSet<EntityId>();
        var grown = false;
        foreach (var e in tx.Query<SwVerArch>())
        {
            Assert.That(seen.Add(e.Id), Is.True, "a row yielded twice across the map's splits");
            if (!grown)
            {
                grown = true;
                SpawnVer(dbe, 10_000, 12_000);   // committed after the snapshot: invisible, but they split the map under the walk
            }
        }

        Assert.That(map.BucketCount, Is.GreaterThan(bucketsBefore), "premise: the map split between two fills");
        Assert.That(seen.SetEquals(keys.Keys), Is.True, "the walk lost or added a row across the splits");
    }

    [Test]
    [CancelAfter(60_000)]
    public void WhereLambda_ReadsOwnWrites_WhileStreaming()
    {
        var dbe = Build();
        var keys = SpawnVer(dbe, 0, 3_000);
        var target = keys.First(kv => kv.Value == 1234).Key;

        using var tx = dbe.CreateQuickTransaction();
        var row = new SwVer { Key = 1234, Value = -1 };
        tx.OpenMut(target).Set(SwVerArch.Row, in row);

        var matched = new List<EntityId>();
        foreach (var e in tx.Query<SwVerArch>().Where<SwVer>(c => c.Value == -1))
        {
            matched.Add(e.Id);
        }

        Assert.That(matched, Is.EqualTo(new[] { target }));
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void AStreamingWalk_HoldsABoundedSetOfPages()
    {
        var dbe = Build();
        SpawnVer(dbe, 0, 30_000);
        var clusterPages = dbe._archetypeStates[Archetype<SwVerArch>.Metadata.ArchetypeId].ClusterState.ClusterSegment.Length;
        Assert.That(clusterPages, Is.GreaterThan(200), "premise: the walk spans more pages than the bound");

        using var tx = dbe.CreateReadOnlyTransaction();
        int peakSlot = 0, peakEpoch = 0, rows = 0;
        foreach (var e in tx.Query<SwVerArch>())
        {
            if (++rows % 500 == 0)
            {
                var (slot, epoch) = Held(dbe);
                peakSlot = Math.Max(peakSlot, slot);
                peakEpoch = Math.Max(peakEpoch, epoch);
            }
        }

        Assert.That(rows, Is.EqualTo(30_000));
        Assert.That(peakSlot, Is.LessThan(200), "the walk kept the pages it left slot-referenced");
        Assert.That(peakEpoch, Is.LessThan(200), "the walk kept the pages it left epoch-held");
    }
}

/// <summary><c>tx.Walk&lt;TArch&gt;()</c>: every entity visible at the snapshot exactly once, in batches, across tick fences (WALK-01).</summary>
[TestFixture]
[NonParallelizable]   // SnapshotWalker's seams are process-wide
class TransactionWalkTests : SnapshotWalkTestBase<TransactionWalkTests>
{
    private const string WalkMarker = "WALK-01";

    [TearDown]
    public void ResetSeams()
    {
        SnapshotWalker.AfterReadProbeForTest = null;
        SnapshotWalker.IgnoreReclamationForTest = false;
        SnapshotWalker.ForceClusterPathForTest = false;
    }

    public enum Mode
    {
        Versioned,
        SingleVersion,
        Mixed,
    }

    [TestCase(Mode.Versioned)]
    [TestCase(Mode.SingleVersion)]
    [TestCase(Mode.Mixed)]
    [CancelAfter(60_000)]
    public void AWalk_SeesEveryRowOnce_WithItsValues(Mode mode)
    {
        var dbe = Build();
        var keys = mode switch
        {
            Mode.Versioned => SpawnVer(dbe, 0, 5_000),
            Mode.SingleVersion => SpawnSv(dbe, 0, 5_000),
            _ => SpawnMixed(dbe, 5_000),
        };

        using var tx = dbe.CreateReadOnlyTransaction();
        var seen = new HashSet<EntityId>();
        var batches = 0;
        switch (mode)
        {
            case Mode.Versioned:
                foreach (var batch in tx.Walk<SwVerArch>())
                {
                    batches++;
                    var rows = batch.GetReadOnlySpan(SwVerArch.Row);
                    for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
                    {
                        var slot = BitOperations.TrailingZeroCount(bits);
                        var id = batch.GetEntityId(slot);
                        Assert.That(seen.Add(id), Is.True, "an entity yielded twice");
                        Assert.That(rows[slot].Key, Is.EqualTo(keys[id]));
                        Assert.That(rows[slot].Value, Is.EqualTo(keys[id] * 7L));
                    }
                }

                break;
            case Mode.SingleVersion:
                foreach (var batch in tx.Walk<SwSvArch>())
                {
                    batches++;
                    var rows = batch.GetReadOnlySpan(SwSvArch.Row);
                    for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
                    {
                        var slot = BitOperations.TrailingZeroCount(bits);
                        var id = batch.GetEntityId(slot);
                        Assert.That(seen.Add(id), Is.True, "an entity yielded twice");
                        Assert.That(rows[slot].Key, Is.EqualTo(keys[id]));
                    }
                }

                break;
            default:
                foreach (var batch in tx.Walk<SwMixedArch>())
                {
                    batches++;
                    var ver = batch.GetReadOnlySpan(SwMixedArch.Ver);
                    var sv = batch.GetReadOnlySpan(SwMixedArch.Sv);
                    for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
                    {
                        var slot = BitOperations.TrailingZeroCount(bits);
                        var id = batch.GetEntityId(slot);
                        Assert.That(seen.Add(id), Is.True, "an entity yielded twice");
                        Assert.That(ver[slot].Key, Is.EqualTo(keys[id]));
                        Assert.That(sv[slot].Value, Is.EqualTo(keys[id] * 11L));
                    }
                }

                break;
        }

        Assert.That(seen.Count, Is.EqualTo(keys.Count));
        Assert.That(batches, Is.GreaterThan(1), "premise: more than one batch");
    }

    [Test]
    [CancelAfter(60_000)]
    public void AWalk_SeesTheSnapshot_NotLaterCommits()
    {
        var dbe = Build();
        var keys = SpawnVer(dbe, 0, 3_000);

        using var tx = dbe.CreateReadOnlyTransaction();

        // After the snapshot: every value changed, 200 destroyed, 500 spawned.
        var ids = keys.Keys.ToArray();
        using (var w = dbe.CreateQuickTransaction())
        {
            foreach (var id in ids)
            {
                var row = new SwVer { Key = keys[id], Value = -5 };
                w.OpenMut(id).Set(SwVerArch.Row, in row);
            }

            for (var i = 0; i < 200; i++)
            {
                w.Destroy(ids[i]);
            }

            Assert.That(w.Commit(), Is.True);
        }

        SpawnVer(dbe, 50_000, 500);

        var seen = new HashSet<EntityId>();
        foreach (var batch in tx.Walk<SwVerArch>())
        {
            var rows = batch.GetReadOnlySpan(SwVerArch.Row);
            for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                var id = batch.GetEntityId(slot);
                Assert.That(seen.Add(id), Is.True);
                Assert.That(rows[slot].Value, Is.EqualTo(keys[id] * 7L), "a value committed after the snapshot");
            }
        }

        Assert.That(seen.SetEquals(keys.Keys), Is.True, "the walk did not see exactly the snapshot's entities");
    }

    [Test]
    [CancelAfter(60_000)]
    public void AWalk_HidesOwnDestroys_AndShowsNoOwnSpawns()
    {
        var dbe = Build();
        var keys = SpawnSv(dbe, 0, 2_000);

        using var tx = dbe.CreateQuickTransaction();
        var destroyed = keys.Keys.Take(37).ToHashSet();
        foreach (var id in destroyed)
        {
            tx.Destroy(id);
        }

        var row = new SwSv { Key = -1 };
        var spawned = tx.Spawn<SwSvArch>(SwSvArch.Row.Set(in row));

        var seen = new HashSet<EntityId>();
        foreach (var batch in tx.Walk<SwSvArch>())
        {
            for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
            {
                seen.Add(batch.GetEntityId(BitOperations.TrailingZeroCount(bits)));
            }
        }

        Assert.That(seen.Overlaps(destroyed), Is.False, "an own pending destroy was walked");
        Assert.That(seen.Contains(spawned), Is.False, "an own pending spawn was walked");
        Assert.That(seen.Count, Is.EqualTo(keys.Count - destroyed.Count));
    }

    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void AWalk_HoldsABoundedSetOfPages(bool readWrite)
    {
        var dbe = Build();
        SpawnVer(dbe, 0, 30_000);
        var clusterPages = dbe._archetypeStates[Archetype<SwVerArch>.Metadata.ArchetypeId].ClusterState.ClusterSegment.Length;
        Assert.That(clusterPages, Is.GreaterThan(200), "premise: the walk spans more pages than the bound");

        using var tx = readWrite ? dbe.CreateQuickTransaction() : dbe.CreateReadOnlyTransaction();

        // Every tenth entity updated after the snapshot: the walk reads those through their chains.
        var keys = new List<(EntityId Id, int Key)>();
        using (var probe = dbe.CreateReadOnlyTransaction())
        {
            foreach (var batch in probe.Walk<SwVerArch>())
            {
                var values = batch.GetReadOnlySpan(SwVerArch.Row);
                for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
                {
                    var slot = BitOperations.TrailingZeroCount(bits);
                    keys.Add((batch.GetEntityId(slot), values[slot].Key));
                }
            }
        }

        for (var first = 0; first < keys.Count; first += 5_000)
        {
            using var w = dbe.CreateQuickTransaction();
            for (var i = first; i < Math.Min(first + 5_000, keys.Count); i += 10)
            {
                var row = new SwVer { Key = keys[i].Key, Value = -1 };
                w.OpenMut(keys[i].Id).Set(SwVerArch.Row, in row);
            }

            Assert.That(w.Commit(), Is.True);
        }

        int peakSlot = 0, peakEpoch = 0, rows = 0;
        foreach (var batch in tx.Walk<SwVerArch>())
        {
            rows += batch.Count;
            var values = batch.GetReadOnlySpan(SwVerArch.Row);
            for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
            {
                Assert.That(values[BitOperations.TrailingZeroCount(bits)].Value, Is.Not.EqualTo(-1L), "a value committed after the snapshot");
            }

            var (slot, epoch) = Held(dbe);
            peakSlot = Math.Max(peakSlot, slot);
            peakEpoch = Math.Max(peakEpoch, epoch);
        }

        Assert.That(rows, Is.EqualTo(30_000));
        Assert.That(peakSlot, Is.LessThan(200), "the walk kept the pages it left slot-referenced");
        Assert.That(peakEpoch, Is.LessThan(200), "the walk kept the pages it left epoch-held");
    }

    [Test]
    [CancelAfter(60_000)]
    public void ABatchUsedAfterItsStep_Throws()
    {
        var dbe = Build();
        SpawnSv(dbe, 0, 1_000);
        using var tx = dbe.CreateReadOnlyTransaction();
        using var walk = tx.Walk<SwSvArch>();
        Assert.That(walk.MoveNext(), Is.True);
        var batch = walk.Current;
        _ = batch.Rows;
        Assert.That(walk.MoveNext(), Is.True);

        var threw = false;
        try
        {
            _ = batch.GetReadOnlySpan(SwSvArch.Row);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.That(threw, Is.True, "a batch read after its step");
    }

    [Test]
    [CancelAfter(60_000)]
    public void AWalkOverEmptiedClusters_SkipsThem()
    {
        var dbe = Build();
        var keys = SpawnSv(dbe, 0, 3_000);
        var destroyed = keys.Keys.Take(1_000).ToHashSet();   // the first clusters, emptied and not yet reclaimed (no fence)
        using (var w = dbe.CreateQuickTransaction())
        {
            foreach (var id in destroyed)
            {
                w.Destroy(id);
            }

            Assert.That(w.Commit(), Is.True);
        }

        using var tx = dbe.CreateReadOnlyTransaction();
        var seen = 0;
        foreach (var batch in tx.Walk<SwSvArch>())
        {
            Assert.That(batch.Rows, Is.Not.Zero, "a batch with no entity");
            seen += batch.Count;
        }

        Assert.That(seen, Is.EqualTo(keys.Count - destroyed.Count));
    }

    [Test]
    [CancelAfter(60_000)]
    public void AVersionedColumn_ReadsItsSnapshotValue_WhenUpdatedMidStep()
    {
        var dbe = Build();
        var keys = SpawnVer(dbe, 0, 1_000);
        using var tx = dbe.CreateReadOnlyTransaction();
        using var walk = tx.Walk<SwVerArch>();
        Assert.That(walk.MoveNext(), Is.True);
        var batch = walk.Current;

        // An update of every entity of the batch, committed between the step and the read of its column.
        using (var w = dbe.CreateQuickTransaction())
        {
            for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
            {
                var id = batch.GetEntityId(BitOperations.TrailingZeroCount(bits));
                var row = new SwVer { Key = keys[id], Value = -9 };
                w.OpenMut(id).Set(SwVerArch.Row, in row);
            }

            Assert.That(w.Commit(), Is.True);
        }

        var rows = batch.GetReadOnlySpan(SwVerArch.Row);
        for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            Assert.That(rows[slot].Value, Is.EqualTo(keys[batch.GetEntityId(slot)] * 7L), "the column read the HEAD's newer value");
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public void AWalkInAReadWriteTx_GivesBackItsMarksEachStep()
    {
        var dbe = Build();
        var keys = SpawnVer(dbe, 0, 3_000);
        using var tx = dbe.CreateQuickTransaction();
        foreach (var id in keys.Keys.Take(500))
        {
            var row = new SwVer { Key = keys[id], Value = 3 };
            tx.OpenMut(id).Set(SwVerArch.Row, in row);
        }

        Assert.That(tx.MarkedPageCount, Is.GreaterThan(0), "premise: the writes marked pages");
        using var walk = tx.Walk<SwVerArch>();
        Assert.That(walk.MoveNext(), Is.True);
        Assert.That(tx.EpochSuspended, Is.True, "premise: the step suspended the epoch");
        Assert.That(tx.MarkedPageCount, Is.Zero, "a step kept the transaction's dirty marks");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // WALK-01: across tick fences
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The reclamation compacts the active list between two steps: clusters the walk visited and clusters it had not, emptied by destroys, leave the list,
    /// and new spawns may take the freed chunk ids. Every entity present for the whole walk is seen exactly once.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("WALK-01")]
    public void AReclamationBetweenSteps_LosesAndRepeatsNothing()
    {
        var dbe = Build();
        var (stable, seen) = WalkAcrossAReclamation(dbe);
        AssertExactlyOnce(stable, seen);
    }

    [Test]
    [CancelAfter(60_000)]
    [RuleMutant("WALK-01")]
    public void Mutant_AWalkThatKeepsItsPositionAcrossAReclamation_IsRejected()
    {
        var dbe = Build();
        SnapshotWalker.IgnoreReclamationForTest = true;
        var (stable, seen) = WalkAcrossAReclamation(dbe);
        RuleMutants.AssertDetects("WALK-01", WalkMarker, () => AssertExactlyOnce(stable, seen));
    }

    private (HashSet<EntityId> Stable, List<EntityId> Seen) WalkAcrossAReclamation(DatabaseEngine dbe)
    {
        var keys = SpawnSv(dbe, 0, 64 * 40);
        dbe.WriteTickFence(1);
        var state = dbe._archetypeStates[Archetype<SwSvArch>.Metadata.ArchetypeId].ClusterState;

        // The clusters in list order, with their entities.
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

        using var tx = dbe.CreateReadOnlyTransaction();
        using var walk = tx.Walk<SwSvArch>();
        var seen = new List<EntityId>();
        for (var step = 0; step < 3; step++)
        {
            Assert.That(walk.MoveNext(), Is.True);
            Collect(walk.Current, seen);
        }

        // Empty one visited cluster and two the walk has not reached, reclaim them, and let new spawns reuse the chunk ids.
        var emptied = clusters[1].Concat(clusters[10]).Concat(clusters[11]).ToHashSet();
        using (var w = dbe.CreateQuickTransaction())
        {
            foreach (var id in emptied)
            {
                w.Destroy(id);
            }

            Assert.That(w.Commit(), Is.True);
        }

        var listed = state.ReadActiveClusterList(out var listedCount)[..listedCount];
        var emptiedChunks = new HashSet<int> { listed[1], listed[10], listed[11] };
        var generation = Volatile.Read(ref state.ReclaimGeneration);
        dbe.WriteTickFence(2);
        Assert.That(Volatile.Read(ref state.ReclaimGeneration), Is.Not.EqualTo(generation), "premise: the fence reclaimed the emptied clusters");
        // New clusters, listed after the walk began: outside its bound, whether or not one reuses a reclaimed chunk id (a freed chunk is quarantined first).
        SpawnSv(dbe, 100_000, 3 * 64);
        var relisted = state.ReadActiveClusterList(out var relistedCount)[..relistedCount];
        Assert.That(relisted.Any(emptiedChunks.Contains), Is.False, "premise: the reclaimed clusters left the list");

        while (walk.MoveNext())
        {
            Collect(walk.Current, seen);
        }

        var stable = keys.Keys.ToHashSet();
        stable.ExceptWith(emptied);
        return (stable, seen);
    }

    /// <summary>
    /// The fence migrates entities between clusters while a walk of a spatial archetype is paused: among them the entities the walk already saw. Every
    /// entity is seen exactly once, each with its own values.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("WALK-01")]
    public void AMigrationBetweenSteps_LosesAndRepeatsNothing()
    {
        var dbe = BuildSpatial();
        var (keys, seen) = WalkAcrossAMigration(dbe);
        AssertExactlyOnce(keys.Keys.ToHashSet(), seen);
    }

    [Test]
    [CancelAfter(60_000)]
    [RuleMutant("WALK-01")]
    public void Mutant_ASpatialWalkInClusterOrder_IsRejected()
    {
        var dbe = BuildSpatial();
        SnapshotWalker.ForceClusterPathForTest = true;
        var (keys, seen) = WalkAcrossAMigration(dbe);
        RuleMutants.AssertDetects("WALK-01", WalkMarker, () => AssertExactlyOnce(keys.Keys.ToHashSet(), seen));
    }

    private (Dictionary<EntityId, int> Keys, List<EntityId> Seen) WalkAcrossAMigration(DatabaseEngine dbe)
    {
        var keys = SpawnSpatial(dbe, 1_000, seed: 23);
        dbe.WriteTickFence(1);
        var state = dbe._archetypeStates[Archetype<SwSpatialArch>.Metadata.ArchetypeId].ClusterState;

        using var tx = dbe.CreateReadOnlyTransaction();
        using var walk = tx.Walk<SwSpatialArch>();
        var seen = new List<EntityId>();
        for (var step = 0; step < 2; step++)
        {
            Assert.That(walk.MoveNext(), Is.True);
            CollectChecked(walk.Current, keys, seen);
        }

        // Move every entity seen so far, and as many others, to a corner cell the walk has not reached.
        var moved = seen.Concat(keys.Keys.Except(seen).Take(seen.Count)).ToList();
        using (var w = dbe.CreateQuickTransaction())
        {
            var i = 0;
            foreach (var id in moved)
            {
                var pos = PointAt(905f + (i % 90), 905f + (i / 90 % 90));
                w.OpenMut(id).Set(SwSpatialArch.Pos, in pos);
                i++;
            }

            Assert.That(w.Commit(), Is.True);
        }

        var moveEpoch = Volatile.Read(ref state.SlotMoveEpoch);
        dbe.WriteTickFence(2);
        Assert.That(Volatile.Read(ref state.SlotMoveEpoch), Is.GreaterThan(moveEpoch), "premise: the fence migrated entities");

        while (walk.MoveNext())
        {
            CollectChecked(walk.Current, keys, seen);
        }

        return (keys, seen);
    }

    /// <summary>
    /// On a SingleVersion spatial archetype a destroy releases its slot at publish and a spawn reuses it, with no fence between: a batch gathered after that
    /// must not report the newcomer's values under the destroyed entity's id.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("WALK-01")]
    public void AGatheredBatch_NeverReportsAReusedSlotsValuesUnderTheOldId()
    {
        var dbe = BuildSpatial();
        var keys = new Dictionary<EntityId, int>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 64; i++)
            {
                var pos = PointAt(50f + i % 8, 50f + i / 8);
                var tag = new SwSvTag { Key = i, Value = i };
                keys[tx.Spawn<SwSpatialSvArch>(SwSpatialSvArch.Pos.Set(in pos), SwSpatialSvArch.Tag.Set(in tag))] = i;
            }

            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);

        using var reader = dbe.CreateReadOnlyTransaction();
        using var walk = reader.Walk<SwSpatialSvArch>();
        Assert.That(walk.MoveNext(), Is.True);
        var batch = walk.Current;

        // Every entity of the batch destroyed, and as many spawned into the same cell: they take the released slots.
        using (var w = dbe.CreateQuickTransaction())
        {
            foreach (var id in keys.Keys)
            {
                w.Destroy(id);
            }

            Assert.That(w.Commit(), Is.True);
        }

        using (var w = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 64; i++)
            {
                var pos = PointAt(50f + i % 8, 50f + i / 8);
                var tag = new SwSvTag { Key = 1_000 + i, Value = 1_000 + i };
                w.Spawn<SwSpatialSvArch>(SwSpatialSvArch.Pos.Set(in pos), SwSpatialSvArch.Tag.Set(in tag));
            }

            Assert.That(w.Commit(), Is.True);
        }

        var tags = batch.GetReadOnlySpan(SwSpatialSvArch.Tag);
        for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            var id = batch.GetEntityId(slot);
            Assert.That(tags[slot].Key == keys[id] || tags[slot].Key == 0, Is.True,
                $"{WalkMarker}: entity {id} reported with key {tags[slot].Key}, another entity's values from its reused slot");
        }
    }

    /// <summary>
    /// A walk is bounded to the clusters listed when it began: beside a steady appender it ends. SingleVersion, whose new entities a walk may see, so a
    /// cluster read past the bound shows as a batch.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void AWalkBesideASteadyAppender_IsBoundedToItsStart()
    {
        var dbe = Build();
        var keys = SpawnSv(dbe, 0, 64 * 8);
        var state = dbe._archetypeStates[Archetype<SwSvArch>.Metadata.ArchetypeId].ClusterState;
        state.ReadActiveClusterList(out var clustersAtStart);

        using var tx = dbe.CreateReadOnlyTransaction();
        var seen = new List<EntityId>();
        var batches = 0;
        var appended = 0;
        foreach (var batch in tx.Walk<SwSvArch>())
        {
            batches++;
            Assert.That(batches, Is.LessThanOrEqualTo(clustersAtStart), "the walk read clusters listed after it began: unbounded, it chases the appender");
            Collect(batch, seen);
            SpawnSv(dbe, 100_000 + appended, 64);   // a cluster's worth after every batch
            appended += 64;
        }

        Assert.That(batches, Is.LessThanOrEqualTo(clustersAtStart), "the walk read clusters listed after it began");
        AssertExactlyOnce(keys.Keys.ToHashSet(), seen);
    }

    /// <summary>A fence that runs while a step reads makes the step read again.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void AFenceDuringAStep_RedoesIt()
    {
        var dbe = Build();
        var keys = SpawnSv(dbe, 0, 64 * 10);
        dbe.WriteTickFence(1);
        SnapshotWalker.RedosForTest = 0;
        using var tx = dbe.CreateReadOnlyTransaction();

        // At the first step's read, another thread opens the fence's window and holds it until the walker has left its epoch scope to wait.
        var fenced = false;
        var sawSuspendedWhileOpen = false;
        Thread fence = null;
        SnapshotWalker.AfterReadProbeForTest = () =>
        {
            if (fenced)
            {
                return;
            }

            fenced = true;
            using var opened = new ManualResetEventSlim();
            fence = new Thread(() =>
            {
                using (dbe.EpochManager.FenceWindow.Open())
                {
                    opened.Set();
                    sawSuspendedWhileOpen = SpinWait.SpinUntil(() => tx.EpochSuspended, 5_000);
                }
            });
            fence.Start();
            opened.Wait();
        };

        var seen = new List<EntityId>();
        foreach (var batch in tx.Walk<SwSvArch>())
        {
            Collect(batch, seen);
        }

        fence?.Join();
        Assert.That(SnapshotWalker.RedosForTest, Is.GreaterThanOrEqualTo(1), "premise: a fence overlapped a step");
        Assert.That(sawSuspendedWhileOpen, Is.True, "the walker waited out the fence holding its epoch pin");
        AssertExactlyOnce(keys.Keys.ToHashSet(), seen);
    }

    [Test]
    [CancelAfter(60_000)]
    public void AWalkAfterItsTransactionIsDisposed_Throws()
    {
        var dbe = Build();
        SpawnSv(dbe, 0, 1_000);
        var tx = dbe.CreateReadOnlyTransaction();
        var walk = tx.Walk<SwSvArch>();
        Assert.That(walk.MoveNext(), Is.True);
        tx.Dispose();

        var threw = false;
        try
        {
            walk.MoveNext();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        walk.Dispose();
        Assert.That(threw, Is.True, "a walk stepped after its transaction was disposed");
        Assert.That(dbe.EpochManager.IsCurrentThreadInScope, Is.False);
    }

    [Test]
    [CancelAfter(60_000)]
    public void TwoWalksOfOneTransaction_EachSeeEveryEntityOnce()
    {
        var dbe = Build();
        var ver = SpawnVer(dbe, 0, 1_500);
        var sv = SpawnSv(dbe, 0, 1_500);
        using var tx = dbe.CreateReadOnlyTransaction();
        var seenVer = new List<EntityId>();
        var seenSv = new List<EntityId>();
        using (var outer = tx.Walk<SwVerArch>())
        using (var inner = tx.Walk<SwSvArch>())
        {
            var outerLive = true;
            var innerLive = true;
            while (outerLive || innerLive)
            {
                if (outerLive && (outerLive = outer.MoveNext()))
                {
                    Collect(outer.Current, seenVer);
                }

                if (innerLive && (innerLive = inner.MoveNext()))
                {
                    Collect(inner.Current, seenSv);
                }
            }
        }

        AssertExactlyOnce(ver.Keys.ToHashSet(), seenVer);
        AssertExactlyOnce(sv.Keys.ToHashSet(), seenSv);
    }

    private static void Collect<TArch>(WalkBatch<TArch> batch, List<EntityId> seen) where TArch : class
    {
        for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
        {
            seen.Add(batch.GetEntityId(BitOperations.TrailingZeroCount(bits)));
        }
    }

    private static void CollectChecked(WalkBatch<SwSpatialArch> batch, Dictionary<EntityId, int> keys, List<EntityId> seen)
    {
        var tags = batch.GetReadOnlySpan(SwSpatialArch.Tag);
        for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            var id = batch.GetEntityId(slot);
            Assert.That(tags[slot].Key, Is.EqualTo(keys[id]), "an entity's batch slot read another entity's values");
            seen.Add(id);
        }
    }

    private static void AssertExactlyOnce(HashSet<EntityId> stable, List<EntityId> seen)
    {
        var counts = seen.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        var repeated = counts.Count(kv => kv.Value > 1);
        var missed = stable.Count(id => !counts.ContainsKey(id));
        Assert.That(repeated, Is.Zero, $"{WalkMarker}: {repeated} entities yielded more than once");
        Assert.That(missed, Is.Zero, $"{WalkMarker}: {missed} entities present for the whole walk were never yielded");
    }
}

/// <summary>A walk holds its transaction's epoch only while it reads a batch (EP-03, EP-04): a consumer paused between batches pins nothing.</summary>
[TestFixture]
[NonParallelizable]   // EntityAccessor.SafetyNetResumesForTest and the census are process- and engine-wide
class WalkEpochSuspensionTests : SnapshotWalkTestBase<WalkEpochSuspensionTests>
{
    private static void WriteBeside(DatabaseEngine dbe, int from, int count)
    {
        var writer = new Thread(() => SpawnVer(dbe, from, count));
        writer.Start();
        writer.Join();
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    public void APausedWalk_HoldsNoEpoch()
    {
        var dbe = Build();
        SpawnVer(dbe, 0, 2_000);

        // Control: an idle transaction holds every page the writer touches after it.
        int idleHeld;
        using (var idle = dbe.CreateReadOnlyTransaction())
        {
            _ = idle.Query<SwVerArch>().Count();
            WriteBeside(dbe, 10_000, 3_000);
            idleHeld = Held(dbe).EpochProtected;
        }

        using var tx = dbe.CreateReadOnlyTransaction();
        using var walk = tx.Walk<SwVerArch>();
        Assert.That(walk.MoveNext(), Is.True);
        Assert.That(tx.EpochSuspended, Is.True, "the step did not suspend the epoch");
        Assert.That(dbe.EpochManager.IsCurrentThreadInScope, Is.False, "the thread is still pinned between steps");

        WriteBeside(dbe, 20_000, 3_000);
        var pausedHeld = Held(dbe).EpochProtected;

        Assert.That(idleHeld, Is.GreaterThan(100), "premise: an idle transaction holds what the writer touched");
        Assert.That(pausedHeld, Is.LessThan(50), $"a paused walk held {pausedHeld} pages epoch-protected (an idle transaction: {idleHeld})");
        Assert.That(walk.MoveNext(), Is.True, "the walk goes on after its pause");
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    public void APausedWalk_StillHoldsItsSnapshot()
    {
        var dbe = Build();
        var keys = SpawnVer(dbe, 0, 2_000);

        using var tx = dbe.CreateReadOnlyTransaction();
        using var walk = tx.Walk<SwVerArch>();
        Assert.That(walk.MoveNext(), Is.True);
        var seen = new HashSet<EntityId>();

        // Paused: every value rewritten twice, and transactions come and go so the revision cleanup runs.
        for (var round = 0; round < 2; round++)
        {
            using var w = dbe.CreateQuickTransaction();
            foreach (var id in keys.Keys)
            {
                var row = new SwVer { Key = keys[id], Value = -round - 1 };
                w.OpenMut(id).Set(SwVerArch.Row, in row);
            }

            Assert.That(w.Commit(), Is.True);
        }

        for (var i = 0; i < 4; i++)
        {
            using var other = dbe.CreateQuickTransaction();
            other.Commit();
        }

        do
        {
            var batch = walk.Current;
            var rows = batch.GetReadOnlySpan(SwVerArch.Row);
            for (var bits = batch.Rows; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                var id = batch.GetEntityId(slot);
                Assert.That(seen.Add(id), Is.True);
                Assert.That(rows[slot].Value, Is.EqualTo(keys[id] * 7L), "the snapshot's revision was not held through the pause");
            }
        } while (walk.MoveNext());

        Assert.That(seen.Count, Is.EqualTo(keys.Count));
    }

    /// <summary>
    /// Every entry point re-enters a suspended scope before it reads a page: the page layer's net under them never fires. Each call is made with the scope
    /// suspended again.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    public void EveryEntryPoint_ResumesBeforeReadingAPage() => SweepEntryPoints(Build());

    [Test]
    [CancelAfter(60_000)]
    [RuleMutant("EP-03")]
    public void Mutant_AnEntryPointThatDoesNotResume_IsRejected()
    {
        var dbe = Build();
        EntityAccessor.ResumeHooksOffForTest = true;
        try
        {
            RuleMutants.AssertDetects("EP-03", "EP-03:", () => SweepEntryPoints(dbe));
        }
        finally
        {
            EntityAccessor.ResumeHooksOffForTest = false;
        }
    }

    private static void SweepEntryPoints(DatabaseEngine dbe)
    {
        var keys = SpawnVer(dbe, 0, 2_000);
        var ids = keys.Keys.ToArray();
        var index = dbe.GetIndexRef<SwVer, int>(x => x.Key);

        using var tx = dbe.CreateQuickTransaction();
        var held = tx.Open(ids[5]);
        using var walk = tx.Walk<SwVerArch>();
        Assert.That(walk.MoveNext(), Is.True);
        var before = EntityAccessor.SafetyNetResumesForTest;

        void Suspend(string what)
        {
            if (!tx.EpochSuspended)
            {
                Assert.That(tx.TrySuspendEpochAtWalkStep(), Is.True, $"premise: suspended before {what}");
            }
        }

        void Checked(string what) =>
            Assert.That(EntityAccessor.SafetyNetResumesForTest, Is.EqualTo(before), $"EP-03: {what} read a page with the scope suspended");

        Suspend("Open");
        _ = tx.Open(ids[1]).Read(SwVerArch.Row);
        Checked("Open");

        Suspend("TryOpen");
        tx.TryOpen(ids[2], out _);
        Checked("TryOpen");

        Suspend("IsAlive");
        tx.IsAlive(ids[3]);
        Checked("IsAlive");

        Suspend("an EntityRef held across the pause");
        _ = held.Read(SwVerArch.Row);
        Checked("an EntityRef held across the pause");

        Suspend("OpenMut");
        var written = new SwVer { Key = keys[ids[4]], Value = 1 };
        tx.OpenMut(ids[4]).Set(SwVerArch.Row, in written);
        Checked("OpenMut");

        Suspend("Count");
        tx.Query<SwVerArch>().Count();
        Checked("Count");

        Suspend("Execute");
        tx.Query<SwVerArch>().Execute();
        Checked("Execute");

        Suspend("WhereField");
        tx.Query<SwVerArch>().WhereField<SwVer>(c => c.Key < 10).Execute();
        Checked("WhereField");

        Suspend("foreach");
        foreach (var e in tx.Query<SwVerArch>())
        {
            _ = e.Read(SwVerArch.Row);
            break;
        }

        Checked("foreach");

        Suspend("EnumerateIndex");
        using (var e = tx.EnumerateIndex<SwVer, int>(index, 0, 5))
        {
            while (e.MoveNext())
            {
            }
        }

        Checked("EnumerateIndex");

        Suspend("For().Open");
        _ = tx.For<SwVerArch>().Open(ids[6]).Read(SwVerArch.Row);
        Checked("For().Open");

        Suspend("GetClusterEnumerator");
        using (var e = tx.GetClusterEnumerator<SwVerArch>())
        {
            e.MoveNext();
        }

        Checked("GetClusterEnumerator");

        Suspend("EcsCount");
        tx.EcsCount<SwVerArch>();
        Checked("EcsCount");

        Suspend("Spawn");
        var spawned = new SwVer { Key = -1 };
        tx.Spawn<SwVerArch>(SwVerArch.Row.Set(in spawned));
        Checked("Spawn");

        Suspend("Destroy");
        tx.Destroy(ids[7]);
        Checked("Destroy");

        Suspend("the walk's own column");
        _ = walk.Current.GetReadOnlySpan(SwVerArch.Row);
        Checked("the walk's own column");

        Suspend("Commit");
        Assert.That(tx.Commit(), Is.True);
        Checked("Commit");
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    public void AWalkSteppedInsideAForeach_EveryFillResumesFirst()
    {
        var dbe = Build();
        var sv = SpawnSv(dbe, 0, 3_000);
        SpawnVer(dbe, 0, 64 * 80);
        var before = EntityAccessor.SafetyNetResumesForTest;
        EcsQuery<SwSvArch>.ForeachWalk.FillsForTest = 0;

        using var tx = dbe.CreateReadOnlyTransaction();
        using var walk = tx.Walk<SwVerArch>();
        var seen = new HashSet<EntityId>();
        foreach (var e in tx.Query<SwSvArch>())
        {
            seen.Add(e.Id);
            if (walk.MoveNext())
            {
                Assert.That(tx.EpochSuspended, Is.True, "premise: the step left the scope inside the loop");
            }
        }

        Assert.That(EcsQuery<SwSvArch>.ForeachWalk.FillsForTest, Is.GreaterThan(1), "premise: the loop refilled after a step");
        Assert.That(EntityAccessor.SafetyNetResumesForTest, Is.EqualTo(before), "EP-03: a fill read a page with the scope suspended");
        Assert.That(seen.Count, Is.EqualTo(sv.Count));
    }

    [Test]
    [CancelAfter(60_000)]
    public void AnOperationAfterAPause_PinsTheCurrentEpoch()
    {
        var dbe = Build();
        var keys = SpawnVer(dbe, 0, 1_000);
        using var tx = dbe.CreateReadOnlyTransaction();
        using var walk = tx.Walk<SwVerArch>();
        Assert.That(walk.MoveNext(), Is.True);
        Assert.That(tx.EpochSuspended, Is.True);

        WriteBeside(dbe, 10_000, 1_000);   // the global epoch moves during the pause
        var global = dbe.EpochManager.GlobalEpoch;
        _ = tx.Open(keys.Keys.First()).Read(SwVerArch.Row);

        Assert.That(dbe.EpochManager.CurrentThreadPinnedEpoch, Is.GreaterThanOrEqualTo(global),
            "the resume pinned the epoch the scope left at, protecting again every page the pause let others tag");
    }

    [Test]
    [CancelAfter(60_000)]
    public void AnOperationDuringAPause_ResumesTheEpoch()
    {
        var dbe = Build();
        var keys = SpawnVer(dbe, 0, 1_000);
        using var tx = dbe.CreateReadOnlyTransaction();
        using var walk = tx.Walk<SwVerArch>();
        Assert.That(walk.MoveNext(), Is.True);
        Assert.That(tx.EpochSuspended, Is.True);

        _ = tx.Open(keys.Keys.First()).Read(SwVerArch.Row);

        Assert.That(tx.EpochSuspended, Is.False);
        Assert.That(dbe.EpochManager.IsCurrentThreadInScope, Is.True);
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-04")]
    public void AWalkUnderAnotherScope_DoesNotSuspend()
    {
        var dbe = Build();
        SpawnVer(dbe, 0, 1_000);
        using var tx = dbe.CreateReadOnlyTransaction();
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            using var walk = tx.Walk<SwVerArch>();
            Assert.That(walk.MoveNext(), Is.True);
            Assert.That(tx.EpochSuspended, Is.False, "an unordered exit beneath a LIFO scope breaks its depth check");
        }

        Assert.That(dbe.EpochManager.IsCurrentThreadInScope, Is.True, "the transaction's own unit is still held");
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-04")]
    public void AScopeOpenedDuringAPause_IsBorrowed_AndStaysLifo()
    {
        var dbe = Build();
        var keys = SpawnVer(dbe, 0, 1_000);
        using var tx = dbe.CreateReadOnlyTransaction();
        using var walk = tx.Walk<SwVerArch>();
        Assert.That(walk.MoveNext(), Is.True);
        Assert.That(tx.EpochSuspended, Is.True);

        using (EpochGuard.Enter(dbe.EpochManager))
        {
            _ = tx.Open(keys.Keys.First()).Read(SwVerArch.Row);
            Assert.That(tx.EpochSuspended, Is.True, "the operation borrowed the open scope instead of nesting the transaction's above it");
        }

        Assert.That(dbe.EpochManager.IsCurrentThreadInScope, Is.False, "the guard's LIFO exit left the thread pinned");
        _ = tx.Open(keys.Keys.First()).Read(SwVerArch.Row);
        Assert.That(tx.EpochSuspended, Is.False);
    }

    [Test]
    [CancelAfter(60_000)]
    public void DisposingASuspendedTransaction_LeavesTheThreadUnpinned()
    {
        var dbe = Build();
        SpawnVer(dbe, 0, 1_000);

        var tx = dbe.CreateReadOnlyTransaction();
        var walk = tx.Walk<SwVerArch>();
        Assert.That(walk.MoveNext(), Is.True);
        Assert.That(tx.EpochSuspended, Is.True);
        tx.Dispose();
        walk.Dispose();
        Assert.That(dbe.EpochManager.IsCurrentThreadInScope, Is.False);

        // The same with a scope opened during the pause, which the dispose borrows.
        tx = dbe.CreateReadOnlyTransaction();
        walk = tx.Walk<SwVerArch>();
        Assert.That(walk.MoveNext(), Is.True);
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            tx.Dispose();
        }

        walk.Dispose();
        Assert.That(dbe.EpochManager.IsCurrentThreadInScope, Is.False, "the dispose exited a unit it no longer held, or kept one");
    }

    [Test]
    [CancelAfter(60_000)]
    public void AWalkUnderALiveIndexCursor_DoesNotSuspend()
    {
        var dbe = Build();
        SpawnVer(dbe, 0, 1_000);
        using var tx = dbe.CreateReadOnlyTransaction();
        using var cursor = tx.EnumerateIndex<SwVer, int>(dbe.GetIndexRef<SwVer, int>(x => x.Key), int.MinValue, int.MaxValue);
        Assert.That(cursor.MoveNext(), Is.True);
        Assert.That(EntityAccessor.LiveIndexCursors, Is.GreaterThan(0), "premise: a cursor is live");
        using var walk = tx.Walk<SwVerArch>();
        Assert.That(walk.MoveNext(), Is.True);
        Assert.That(tx.EpochSuspended, Is.False, "the cursor stands on what the pin protects (EP-02)");
    }
}
