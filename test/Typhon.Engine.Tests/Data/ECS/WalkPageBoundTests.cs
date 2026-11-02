using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.WalkBound.Single", 1, StorageMode = StorageMode.SingleVersion)]
unsafe struct WbSingle
{
    [Index]
    public int Key;

    public fixed byte Payload[108];
}

[Component("Typhon.Test.WalkBound.Versioned", 1, StorageMode = StorageMode.Versioned)]
unsafe struct WbVersioned
{
    [Index]
    public int Key;

    public fixed byte Payload[108];
}

[Component("Typhon.Test.WalkBound.Child", 1, StorageMode = StorageMode.Versioned)]
struct WbChild
{
    [Index(AllowMultiple = true), ForeignKey(typeof(WbVersioned))]
    public long ParentId;
}

[Archetype]
class WbChildArch : Archetype<WbChildArch>
{
    public static readonly Comp<WbChild> Row = Register<WbChild>();
}

[Component("Typhon.Test.WalkBound.Plain", 1, StorageMode = StorageMode.SingleVersion)]
unsafe struct WbPlain
{
    public int Value;
    public fixed byte Payload[108];
}

[Archetype]
class WbPlainArch : Archetype<WbPlainArch>
{
    public static readonly Comp<WbPlain> Row = Register<WbPlain>();
}

[Component("Typhon.Test.WalkBound.Multi", 1, StorageMode = StorageMode.Versioned)]
unsafe struct WbMulti
{
    [Index(AllowMultiple = true)]
    public int Group;

    public fixed byte Payload[60];
}

[Archetype]
class WbMultiArch : Archetype<WbMultiArch>
{
    public static readonly Comp<WbMulti> Row = Register<WbMulti>();
}

[Archetype]
class WbMultiOtherArch : Archetype<WbMultiOtherArch>
{
    public static readonly Comp<WbMulti> Row = Register<WbMulti>();
}

[Component("Typhon.Test.WalkBound.Spatial", 1, StorageMode = StorageMode.SingleVersion)]
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct WbSpatial
{
    [Field]
    [SpatialIndex]
    public AABB3F Bounds;

    [Field]
    public long Seq;
}

[Archetype]
class WbSpatialArch : Archetype<WbSpatialArch>
{
    public static readonly Comp<WbSpatial> Row = Register<WbSpatial>();
}

[Component("Typhon.Test.WalkBound.Merged", 1, StorageMode = StorageMode.SingleVersion)]
unsafe struct WbMerged
{
    [Index]
    public int Key;

    public fixed byte Payload[108];
}

[Archetype]
class WbMergedArch : Archetype<WbMergedArch>
{
    public static readonly Comp<WbMerged> Row = Register<WbMerged>();
}

[Archetype]
class WbMerged1 : Archetype<WbMerged1, WbMergedArch>
{
}

[Archetype]
class WbMerged2 : Archetype<WbMerged2, WbMergedArch>
{
}

[Archetype]
class WbMerged3 : Archetype<WbMerged3, WbMergedArch>
{
}

[Archetype]
class WbMerged4 : Archetype<WbMerged4, WbMergedArch>
{
}

[Archetype]
class WbMerged5 : Archetype<WbMerged5, WbMergedArch>
{
}

[Archetype]
class WbMerged6 : Archetype<WbMerged6, WbMergedArch>
{
}

[Archetype]
class WbMerged7 : Archetype<WbMerged7, WbMergedArch>
{
}

[Component("Typhon.Test.WalkBound.MergedVersioned", 1, StorageMode = StorageMode.Versioned)]
unsafe struct WbMergedVer
{
    [Index]
    public int Key;

    public fixed byte Payload[108];
}

[Archetype]
class WbMergedVerArch : Archetype<WbMergedVerArch>
{
    public static readonly Comp<WbMergedVer> Row = Register<WbMergedVer>();
}

[Archetype]
class WbMergedVer1 : Archetype<WbMergedVer1, WbMergedVerArch>
{
}

[Archetype]
class WbMergedVer2 : Archetype<WbMergedVer2, WbMergedVerArch>
{
}

[Archetype]
class WbMergedVer3 : Archetype<WbMergedVer3, WbMergedVerArch>
{
}

[Archetype]
class WbMergedVer4 : Archetype<WbMergedVer4, WbMergedVerArch>
{
}

[Archetype]
class WbMergedVer5 : Archetype<WbMergedVer5, WbMergedVerArch>
{
}

[Archetype]
class WbMergedVer6 : Archetype<WbMergedVer6, WbMergedVerArch>
{
}

[Archetype]
class WbMergedVer7 : Archetype<WbMergedVer7, WbMergedVerArch>
{
}

[Archetype]
class WbSingleArch : Archetype<WbSingleArch>
{
    public static readonly Comp<WbSingle> Row = Register<WbSingle>();
}

[Archetype]
class WbVersionedArch : Archetype<WbVersionedArch>
{
    public static readonly Comp<WbVersioned> Row = Register<WbVersioned>();
}

/// <summary>
/// Every read that walks a whole archetype holds a bounded set of pages, whichever API it goes through and whichever transaction it runs in (EP-02).
/// </summary>
/// <remarks>
/// As <see cref="QueryScanCacheBoundTests"/>: the archetype spans many times the bound and does not need to outgrow the cache. A page a walk still holds
/// mid-walk is the failure, long before the cache runs out, so what is asserted is the largest count seen while the walk runs. These bounds are generous on
/// purpose: an enumerator's windows hold up to 32 pages each, and a refresh every 128 rows leaves what those rows touched.
/// </remarks>
[TestFixture]
class WalkPageBoundTests : TestBase<WalkPageBoundTests>
{
    private const int Entities = 30_000;

    /// <summary>
    /// A spawn batch over the cache, at the 8 MiB floor: a cache under it has no slot reserve (PS-24), and there the checkpoint's own read of the UoW registry,
    /// after a cycle the batch had refilled the cache behind, waited for a slot only its next cycle could free, until the batch timed out (2 MiB, under load).
    /// </summary>
    private const int BatchEntities = 80_000;
    private const int Batch = 1000;
    private const int HeldPagesBound = 320;

    public enum Storage
    {
        Single,
        Versioned,
    }

    private DatabaseEngine Build(Storage storage)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<WbSingle>();
        dbe.RegisterComponentFromAccessor<WbVersioned>();
        dbe.RegisterComponentFromAccessor<WbChild>();
        dbe.RegisterComponentFromAccessor<WbPlain>();
        dbe.RegisterComponentFromAccessor<WbMulti>();
        dbe.InitializeArchetypes();
        Spawn(dbe, storage, 0, Entities);
        var state = storage == Storage.Single
            ? dbe._archetypeStates[Archetype<WbSingleArch>.Metadata.ArchetypeId]
            : dbe._archetypeStates[Archetype<WbVersionedArch>.Metadata.ArchetypeId];
        Assert.That(state.ClusterState.ClusterSegment.Length, Is.GreaterThan(HeldPagesBound), "premise: the walk spans more pages than the bound");
        return dbe;
    }

    private static void Spawn(DatabaseEngine dbe, Storage storage, int from, int count)
    {
        for (var first = from; first < from + count; first += Batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = first; i < first + Batch; i++)
            {
                if (storage == Storage.Single)
                {
                    var row = new WbSingle { Key = i };
                    tx.Spawn<WbSingleArch>(WbSingleArch.Row.Set(in row));
                }
                else
                {
                    var row = new WbVersioned { Key = i };
                    tx.Spawn<WbVersionedArch>(WbVersionedArch.Row.Set(in row));
                }
            }

            tx.Commit();
        }
    }

    private static (int SlotReferenced, int EpochProtected) Held(DatabaseEngine dbe)
    {
        dbe.MMF.GetMetrics().GetMemPageExtraInfo(out var census);
        return (census.SlotRefPageCount, census.EpochProtectedPageCount);
    }

    private static void NoteHeld(DatabaseEngine dbe, ref int peakSlot, ref int peakEpoch)
    {
        var (slot, epoch) = Held(dbe);
        peakSlot = System.Math.Max(peakSlot, slot);
        peakEpoch = System.Math.Max(peakEpoch, epoch);
    }

    /// <summary>
    /// The walk of the audits: a whole index, in the default read-write transaction, with no Versioned entity opened. Its accessors defer their slot-reference
    /// releases to the transaction's ChangeSet, and only a refresh drains them. It used to drain them through the entities the transaction had opened: with
    /// none, every page the walk left stayed slot-referenced until the enumerator's Dispose, and the walk ended in a back-pressure timeout with the whole
    /// cache held and nothing to blame.
    /// </summary>
    [TestCase(Storage.Single)]
    [TestCase(Storage.Versioned)]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void AWholeIndexWalk_InAReadWriteTransaction_HoldsABoundedSetOfPages(Storage storage)
    {
        var dbe = Build(storage);
        using var tx = dbe.CreateQuickTransaction();
        int peakSlot = 0, peakEpoch = 0, rows = 0;
        if (storage == Storage.Single)
        {
            using var walk = tx.EnumerateIndex<WbSingle, int>(dbe.GetIndexRef<WbSingle, int>(x => x.Key), int.MinValue, int.MaxValue);
            while (walk.MoveNext())
            {
                Assert.That(walk.Current.Key, Is.EqualTo(rows), "the row in hand, after whatever the walk released");
                if (++rows % 500 == 0)
                {
                    NoteHeld(dbe, ref peakSlot, ref peakEpoch);
                }
            }
        }
        else
        {
            using var walk = tx.EnumerateIndex<WbVersioned, int>(dbe.GetIndexRef<WbVersioned, int>(x => x.Key), int.MinValue, int.MaxValue);
            while (walk.MoveNext())
            {
                Assert.That(walk.Current.Key, Is.EqualTo(rows), "the row in hand, after whatever the walk released");
                if (++rows % 500 == 0)
                {
                    NoteHeld(dbe, ref peakSlot, ref peakEpoch);
                }
            }
        }

        Assert.That(rows, Is.EqualTo(Entities));
        Assert.That(peakSlot, Is.LessThan(HeldPagesBound), "the walk kept the pages it left slot-referenced");
        Assert.That(peakEpoch, Is.LessThan(HeldPagesBound), "the walk kept the pages it left epoch-held");
    }

    /// <summary>
    /// A row's span stays valid until the next MoveNext (EP-03), including across the refresh that same MoveNext ran on its way out: the refresh now gives back
    /// what the walk's windows let go of, and the row in hand must not be among them.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    public void TheRowInHand_SurvivesTheRefreshOfItsOwnStep()
    {
        var dbe = Build(Storage.Versioned);
        var pressureBefore = EntityAccessor.PressuredEpochRefreshInterval;
        using var tx = dbe.CreateQuickTransaction();
        var pinned = dbe.EpochManager.CurrentThreadPinnedEpoch;
        var refreshes = 0;
        var rows = 0;
        using var walk = tx.EnumerateIndex<WbVersioned, int>(dbe.GetIndexRef<WbVersioned, int>(x => x.Key), int.MinValue, int.MaxValue);
        while (walk.MoveNext())
        {
            var now = dbe.EpochManager.CurrentThreadPinnedEpoch;
            if (now != pinned)
            {
                pinned = now;
                refreshes++;
            }

            Assert.That(walk.CurrentComponent.Key, Is.EqualTo(rows));
            rows++;
        }

        Assert.That(rows, Is.EqualTo(Entities));
        Assert.That(refreshes, Is.GreaterThanOrEqualTo(Entities / EntityAccessor.EpochRefreshInterval - 1),
            $"premise: the walk refreshed (pressure {pressureBefore})");
    }

    /// <summary>
    /// A lookup per key in one transaction. The refresh count was the enumerator's, and each lookup is a new enumerator: it never reached 128, and the
    /// loop kept every page it read until the transaction ended: 2 052 lookups filled a 64 MiB cache.
    /// </summary>
    [TestCase(Storage.Single, true)]
    [TestCase(Storage.Versioned, true)]
    [TestCase(Storage.Versioned, false)]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    public void AnIndexLookupPerKey_HoldsABoundedSetOfPages(Storage storage, bool readOnly)
    {
        var dbe = Build(storage);
        using var tx = readOnly ? dbe.CreateReadOnlyTransaction() : dbe.CreateQuickTransaction();
        int peakSlot = 0, peakEpoch = 0;
        for (var key = 0; key < Entities; key++)
        {
            if (storage == Storage.Single)
            {
                using var one = tx.EnumerateIndex<WbSingle, int>(dbe.GetIndexRef<WbSingle, int>(x => x.Key), key, key);
                Assert.That(one.MoveNext(), Is.True);
            }
            else
            {
                using var one = tx.EnumerateIndex<WbVersioned, int>(dbe.GetIndexRef<WbVersioned, int>(x => x.Key), key, key);
                Assert.That(one.MoveNext(), Is.True);
            }

            if (key % 500 == 0)
            {
                NoteHeld(dbe, ref peakSlot, ref peakEpoch);
            }
        }

        Assert.That(peakSlot, Is.LessThan(HeldPagesBound), "the loop kept the pages it read slot-referenced");
        Assert.That(peakEpoch, Is.LessThan(HeldPagesBound), "the loop kept the pages it read epoch-held");
    }

    /// <summary>
    /// A row the snapshot cannot see costs the walk the same pages as one it can. Refreshes counted emissions only, so a walk over rows committed after its
    /// snapshot read all of them, emitted none and never moved its epoch.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    public void AWalkOverRowsItsSnapshotCannotSee_StillMovesItsEpoch()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<WbSingle>();
        dbe.RegisterComponentFromAccessor<WbVersioned>();
        dbe.RegisterComponentFromAccessor<WbChild>();
        dbe.RegisterComponentFromAccessor<WbPlain>();
        dbe.RegisterComponentFromAccessor<WbMulti>();
        dbe.InitializeArchetypes();
        Spawn(dbe, Storage.Versioned, 0, Batch);

        using var tx = dbe.CreateReadOnlyTransaction();
        var writer = new System.Threading.Thread(() => Spawn(dbe, Storage.Versioned, Batch, 4 * Batch));   // small: this snapshot's epoch holds what it writes
        writer.Start();
        writer.Join();

        var pinned = dbe.EpochManager.CurrentThreadPinnedEpoch;
        var rows = 0;
        using (var walk = tx.EnumerateIndex<WbVersioned, int>(dbe.GetIndexRef<WbVersioned, int>(x => x.Key), Batch, int.MaxValue))
        {
            while (walk.MoveNext())
            {
                rows++;
            }
        }

        Assert.That(rows, Is.Zero, "premise: every row of the range is newer than the snapshot");
        Assert.That(dbe.EpochManager.CurrentThreadPinnedEpoch, Is.GreaterThan(pinned), "the walk read the whole range under one epoch");
    }

    /// <summary>
    /// <c>Destroy</c> reads the entity and its clusters, and nothing on its path moved the epoch: 300 000 destroys in one transaction filled a 64 MiB cache
    /// with pages it held only by its own pin. What a commit must keep is its marks, which are not this.
    /// </summary>
    [TestCase(Storage.Single)]
    [TestCase(Storage.Versioned)]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    public void DestroyingAWholeArchetypeInOneTransaction_MovesItsEpoch(Storage storage)
    {
        var dbe = Build(storage);
        var ids = new List<EntityId>();
        using (var read = dbe.CreateReadOnlyTransaction())
        {
            ids.AddRange(storage == Storage.Single ? read.Query<WbSingleArch>().Execute() : read.Query<WbVersionedArch>().Execute());
        }

        using var tx = dbe.CreateQuickTransaction();
        var pinned = dbe.EpochManager.CurrentThreadPinnedEpoch;
        var refreshes = 0;
        foreach (var id in ids)
        {
            tx.Destroy(id);
            var now = dbe.EpochManager.CurrentThreadPinnedEpoch;
            if (now != pinned)
            {
                pinned = now;
                refreshes++;
            }
        }

        Assert.That(refreshes, Is.GreaterThanOrEqualTo(ids.Count / EntityAccessor.EpochRefreshInterval - 1), "the destroys ran under one epoch");
        Assert.That(tx.Commit(), Is.True);
    }

    public enum ClusterWalk
    {
        ReadOnlyTransaction,
        ReadWriteTransaction,
        PointInTimeLease,
    }

    /// <summary>
    /// A cluster walk tagged every page and never moved its epoch: it died at one cache of rows, whichever way it was entered. Its window now holds what it
    /// reads, as a query's scan does, so the bound is the same on a lease, which never refreshes.
    /// </summary>
    [TestCase(ClusterWalk.ReadOnlyTransaction)]
    [TestCase(ClusterWalk.ReadWriteTransaction)]
    [TestCase(ClusterWalk.PointInTimeLease)]
    [CancelAfter(60_000)]
    [NonParallelizable] // LongWalkClusters is process-wide
    [VerifiesRule("EP-02")]
    public void AClusterWalkOverAWholeArchetype_HoldsABoundedSetOfPages(ClusterWalk how)
    {
        var dbe = Build(Storage.Single);
        var rows = 0;
        if (how == ClusterWalk.PointInTimeLease)
        {
            var longWalk = EntityAccessor.LongWalkClusters;
            EntityAccessor.LongWalkClusters = 0;   // this archetype's walk is short of the threshold, under which a lease's own end bounds it
            try
            {
                using var pta = PointInTimeAccessor.Create(dbe);
                using var lease = pta.Enter(0);
                using var clusters = lease.Accessor.GetClusterEnumerator<WbSingleArch>();
                while (clusters.MoveNext())
                {
                    rows += System.Numerics.BitOperations.PopCount(clusters.Current.OccupancyBits);
                }
            }
            finally
            {
                EntityAccessor.LongWalkClusters = longWalk;
            }

            Assert.That(rows, Is.EqualTo(Entities));
            Assert.That(Held(dbe).EpochProtected, Is.LessThan(HeldPagesBound), "the lease kept the pages it read epoch-held");
            return;
        }

        using var tx = how == ClusterWalk.ReadOnlyTransaction ? dbe.CreateReadOnlyTransaction() : dbe.CreateQuickTransaction();
        using (var clusters = tx.GetClusterEnumerator<WbSingleArch>())
        {
            while (clusters.MoveNext())
            {
                rows += System.Numerics.BitOperations.PopCount(clusters.Current.OccupancyBits);
            }
        }

        Assert.That(rows, Is.EqualTo(Entities));
        Assert.That(Held(dbe).EpochProtected, Is.LessThan(HeldPagesBound), "the walk kept the pages it read epoch-held for the transaction");
    }

    /// <summary>
    /// A transaction's long cluster walk moves its epoch at its yield points (#1364): held, it keeps every page another thread tags meanwhile. A write
    /// through a
    /// ClusterRef is recorded before the step that yields, so it reaches the page's write record whatever the epoch did.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [NonParallelizable] // LongWalkClusters, ScanYieldProbe and the refresh interval are process-wide
    [VerifiesRule("EP-02")]
    public void ALongClusterWalk_MovesItsEpoch_AndKeepsTheWritesItMade()
    {
        var dbe = Build(Storage.Single);
        for (var first = 0; first < Entities; first += Batch)
        {
            using var spawn = dbe.CreateQuickTransaction();
            for (var i = 0; i < Batch; i++)
            {
                var row = new WbPlain { Value = first + i };
                spawn.Spawn<WbPlainArch>(WbPlainArch.Row.Set(in row));
            }

            spawn.Commit();
        }

        var longWalk = EntityAccessor.LongWalkClusters;
        var interval = EntityAccessor.ScanEpochRefreshInterval;
        var yields = 0;
        EntityAccessor.LongWalkClusters = 0;
        EntityAccessor.ScanEpochRefreshInterval = System.TimeSpan.Zero;
        EntityAccessor.ScanYieldProbe = () => yields++;
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            var pinned = dbe.EpochManager.CurrentThreadPinnedEpoch;
            using (var clusters = tx.GetClusterEnumerator<WbPlainArch>())
            {
                while (clusters.MoveNext())
                {
                    var cluster = clusters.Current;
                    var rows = cluster.GetSpan(WbPlainArch.Row);
                    var bits = cluster.OccupancyBits;
                    while (bits != 0)
                    {
                        var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        rows[slot].Value = -1 - rows[slot].Value;
                    }

                    clusters.MarkCurrentDirty();
                }
            }

            Assert.That(yields, Is.GreaterThan(1), "premise: the walk reached its yield points");
            Assert.That(dbe.EpochManager.CurrentThreadPinnedEpoch, Is.GreaterThan(pinned), "the walk ran under one epoch");
            tx.Commit();
        }
        finally
        {
            EntityAccessor.LongWalkClusters = longWalk;
            EntityAccessor.ScanEpochRefreshInterval = interval;
            EntityAccessor.ScanYieldProbe = null;
        }

        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(System.TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(dbe.MMF.EvictEvictablePagesForTest(), Is.GreaterThan(0), "premise: pages left the cache, so what follows is read back");

        using var read = dbe.CreateReadOnlyTransaction();
        long sum = 0;
        var seen = 0;
        foreach (var e in read.Query<WbPlainArch>())
        {
            sum += e.Read(WbPlainArch.Row).Value;
            seen++;
        }

        Assert.That(seen, Is.EqualTo(Entities));
        Assert.That(sum, Is.EqualTo(-(long)Entities * (Entities + 1) / 2), "every write of the walk reached the page");
    }

    /// <summary>
    /// An ordered query's merge read its streams through accessors that tagged every page, under live cursors no refresh can move: a whole archetype, or a
    /// deep Skip, died before its first row.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void AnOrderedQueryOverAWholeArchetype_HoldsABoundedSetOfPages(bool deepSkip)
    {
        var dbe = Build(Storage.Single);
        using var tx = dbe.CreateReadOnlyTransaction();
        var query = tx.Query<WbSingleArch>().WhereField<WbSingle>(x => x.Key >= 0).OrderByField<WbSingle, int>(x => x.Key);
        var rows = deepSkip ? query.Skip(Entities - 50).Take(50).ExecuteOrdered() : query.ExecuteOrdered();

        Assert.That(rows, Has.Count.EqualTo(deepSkip ? 50 : Entities));
        Assert.That(tx.Open(rows[0]).Read(WbSingleArch.Row).Key, Is.EqualTo(deepSkip ? Entities - 50 : 0), "in key order, from the right row");
        Assert.That(Held(dbe).EpochProtected, Is.LessThan(HeldPagesBound), "the merge kept the pages it read epoch-held for the transaction");
    }

    /// <summary>
    /// A merge has no point at which to let go of its windows, and each stream's hold 32 pages of clusters and as many of leaves: eight archetypes filled a
    /// 256-slot cache by themselves, and the merge timed out on the pins of its own streams. Their windows now share a quarter of the cache.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [Property("CacheSize", 2 * 1024 * 1024)]
    [VerifiesRule("EP-02")]
    public void AnOrderedQueryOverManyArchetypes_StaysInsideASmallCache()
    {
        const int perArchetype = 6_000;
        const int archetypes = 8;
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<WbSingle>();
        dbe.RegisterComponentFromAccessor<WbVersioned>();
        dbe.RegisterComponentFromAccessor<WbChild>();
        dbe.RegisterComponentFromAccessor<WbPlain>();
        dbe.RegisterComponentFromAccessor<WbMulti>();
        dbe.RegisterComponentFromAccessor<WbMerged>();
        dbe.RegisterComponentFromAccessor<WbMergedVer>();
        dbe.InitializeArchetypes();

        for (var first = 0; first < perArchetype * archetypes; first += Batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var key = first; key < first + Batch; key++)
            {
                var row = new WbMerged { Key = key };
                switch (key % archetypes)
                {
                    case 0: tx.Spawn<WbMergedArch>(WbMergedArch.Row.Set(in row)); break;
                    case 1: tx.Spawn<WbMerged1>(WbMergedArch.Row.Set(in row)); break;
                    case 2: tx.Spawn<WbMerged2>(WbMergedArch.Row.Set(in row)); break;
                    case 3: tx.Spawn<WbMerged3>(WbMergedArch.Row.Set(in row)); break;
                    case 4: tx.Spawn<WbMerged4>(WbMergedArch.Row.Set(in row)); break;
                    case 5: tx.Spawn<WbMerged5>(WbMergedArch.Row.Set(in row)); break;
                    case 6: tx.Spawn<WbMerged6>(WbMergedArch.Row.Set(in row)); break;
                    default: tx.Spawn<WbMerged7>(WbMergedArch.Row.Set(in row)); break;
                }
            }

            Assert.That(tx.Commit(), Is.True);
        }

        using var read = dbe.CreateReadOnlyTransaction();
        var rows = read.Query<WbMergedArch>().WhereField<WbMerged>(x => x.Key >= 0).OrderByField<WbMerged, int>(x => x.Key).ExecuteOrdered();

        Assert.That(rows, Has.Count.EqualTo(perArchetype * archetypes));
        for (var i = 0; i < rows.Count; i += 4_001)
        {
            Assert.That(read.Open(rows[i]).Read(WbMergedArch.Row).Key, Is.EqualTo(i), "in key order, across the archetypes");
        }
    }

    /// <summary>
    /// A row a later commit touched sends the merge to its stream's entity map, through a window of the same two slots: eight Versioned archetypes on 256
    /// slots is the narrowest a merge runs at, and the map's reads must not outlive the cluster's. What the snapshot saw comes back, in order, and nothing else.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [Property("CacheSize", 2 * 1024 * 1024)]
    [VerifiesRule("EP-02")]
    public void AVersionedMergeAtItsNarrowestWindows_AnswersAtItsSnapshot()
    {
        const int perArchetype = 3_000;
        const int archetypes = 8;
        var total = perArchetype * archetypes;
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<WbSingle>();
        dbe.RegisterComponentFromAccessor<WbVersioned>();
        dbe.RegisterComponentFromAccessor<WbChild>();
        dbe.RegisterComponentFromAccessor<WbPlain>();
        dbe.RegisterComponentFromAccessor<WbMulti>();
        dbe.RegisterComponentFromAccessor<WbMerged>();
        dbe.RegisterComponentFromAccessor<WbMergedVer>();
        dbe.InitializeArchetypes();
        Assert.That(KWayMergeState.WindowSlots(archetypes, dbe.MMF.CacheSlotCount), Is.EqualTo(2), "premise: the narrowest windows");

        static EntityId Spawn(Transaction tx, int key)
        {
            var row = new WbMergedVer { Key = key };
            return (key % archetypes) switch
            {
                0 => tx.Spawn<WbMergedVerArch>(WbMergedVerArch.Row.Set(in row)),
                1 => tx.Spawn<WbMergedVer1>(WbMergedVerArch.Row.Set(in row)),
                2 => tx.Spawn<WbMergedVer2>(WbMergedVerArch.Row.Set(in row)),
                3 => tx.Spawn<WbMergedVer3>(WbMergedVerArch.Row.Set(in row)),
                4 => tx.Spawn<WbMergedVer4>(WbMergedVerArch.Row.Set(in row)),
                5 => tx.Spawn<WbMergedVer5>(WbMergedVerArch.Row.Set(in row)),
                6 => tx.Spawn<WbMergedVer6>(WbMergedVerArch.Row.Set(in row)),
                _ => tx.Spawn<WbMergedVer7>(WbMergedVerArch.Row.Set(in row)),
            };
        }

        var ids = new EntityId[total];
        for (var first = 0; first < total; first += Batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var key = first; key < Math.Min(first + Batch, total); key++)
            {
                ids[key] = Spawn(tx, key);
            }

            Assert.That(tx.Commit(), Is.True);
        }

        using var read = dbe.CreateReadOnlyTransaction();

        // After the snapshot: rows it still sees are gone, and rows it must not see arrive, in every archetype.
        for (var first = 0; first < total; first += Batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var key = first; key < Math.Min(first + Batch, total); key += 5)
            {
                tx.Destroy(ids[key]);
                Spawn(tx, total + key);
            }

            Assert.That(tx.Commit(), Is.True);
        }

        var rows = read.Query<WbMergedVerArch>().WhereField<WbMergedVer>(x => x.Key >= 0).OrderByField<WbMergedVer, int>(x => x.Key).ExecuteOrdered();

        Assert.That(rows, Has.Count.EqualTo(total), "what the snapshot saw: the destroyed rows, and none of the later ones");
        for (var i = 0; i < total; i++)
        {
            if (rows[i] != ids[i])
            {
                Assert.Fail($"row {i}: {rows[i]}, not {ids[i]}");
            }
        }
    }

    /// <summary>Every stream's windows, at the widest that leaves the merge a quarter of the cache: as wide as they ever were when that is enough.</summary>
    [TestCase(1, 32_768, 32)]
    [TestCase(64, 32_768, 32)]
    [TestCase(64, 8_192, 8)]
    [TestCase(11, 1_024, 4)]
    [TestCase(8, 256, 2)]
    [TestCase(200, 256, 2)]
    [TestCase(0, 256, 32)]
    public void AMergesWindows_ShareAQuarterOfTheCache(int streams, int cacheSlots, int expected) =>
        Assert.That(KWayMergeState.WindowSlots(streams, cacheSlots), Is.EqualTo(expected));

    /// <summary>A loop of walks too short for a yield point reads as many pages as one long walk, and each new enumerator looked like the first.</summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    public void ALoopOfShortClusterWalks_MovesItsEpoch()
    {
        var dbe = Build(Storage.Single);
        using var tx = dbe.CreateReadOnlyTransaction();
        var pinned = dbe.EpochManager.CurrentThreadPinnedEpoch;
        for (var i = 0; i < 2 * EntityAccessor.EpochRefreshInterval; i++)
        {
            using var walk = tx.GetClusterEnumerator<WbSingleArch>(0, 1);
            Assert.That(walk.MoveNext(), Is.True);
        }

        Assert.That(dbe.EpochManager.CurrentThreadPinnedEpoch, Is.GreaterThan(pinned), "the loop ran under one epoch");
    }

    /// <summary>
    /// An index on near-distinct keys has one value buffer per key, and each buffer's reader tagged the pages it read. Two archetypes holding the component
    /// read their trees whole inside <c>EnumerateIndex</c>, with no walk to refresh for them: the call died before it returned.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void AnIndexOverTwoArchetypes_OfNearDistinctKeys_HoldsABoundedSetOfPages()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<WbMulti>();
        dbe.InitializeArchetypes();
        for (var first = 0; first < Entities; first += Batch)
        {
            using var spawn = dbe.CreateQuickTransaction();
            for (var i = first; i < first + Batch; i++)
            {
                var row = new WbMulti { Group = i / 2 };   // two values per key: a buffer each
                if ((i & 1) == 0)
                {
                    spawn.Spawn<WbMultiArch>(WbMultiArch.Row.Set(in row));
                }
                else
                {
                    spawn.Spawn<WbMultiOtherArch>(WbMultiOtherArch.Row.Set(in row));
                }
            }

            spawn.Commit();
        }

        using var tx = dbe.CreateReadOnlyTransaction();
        var rows = 0;
        using (var walk = tx.EnumerateIndex<WbMulti, int>(dbe.GetIndexRef<WbMulti, int>(x => x.Group), int.MinValue, int.MaxValue))
        {
            Assert.That(Held(dbe).EpochProtected, Is.LessThan(HeldPagesBound), "reading the two trees kept their buffers epoch-held");
            while (walk.MoveNext())
            {
                rows++;
            }
        }

        Assert.That(rows, Is.EqualTo(Entities));
    }

    public enum SpatialShape
    {
        BoxExecute,
        BoxCount,
        Radius,
        Frustum,
        Nearest,
    }

    /// <summary>
    /// A spatial query over the whole world walks every cluster in a window that tagged each page: it died before its first row. Whatever its shape, and on a
    /// cache smaller than the absolute threshold, which this world never reaches.
    /// </summary>
    [TestCase(SpatialShape.BoxExecute)]
    [TestCase(SpatialShape.BoxCount)]
    [TestCase(SpatialShape.Radius)]
    [TestCase(SpatialShape.Frustum)]
    [TestCase(SpatialShape.Nearest)]
    [CancelAfter(60_000)]
    [Property("CacheSize", 2 * 1024 * 1024)]
    [VerifiesRule("EP-02")]
    public void ASpatialQueryOverTheWholeWorld_HoldsABoundedSetOfPages(SpatialShape shape)
    {
        var dbe = SpatialWorld();
        using var tx = dbe.CreateReadOnlyTransaction();
        var whole = tx.Query<WbSpatialArch>();
        double far = 2 * SpatialExtent;
        int rows;
        switch (shape)
        {
            case SpatialShape.BoxExecute:
                rows = whole.WhereInAABB<WbSpatial>(-far, -far, -1, far, far, 2).Execute().Count;
                break;
            case SpatialShape.BoxCount:
                rows = whole.WhereInAABB<WbSpatial>(-far, -far, -1, far, far, 2).Count();
                break;
            case SpatialShape.Radius:
                rows = whole.WhereNearby<WbSpatial>(0, 0, 0, 2 * far).Count();
                break;
            case SpatialShape.Frustum:
                double[] planes = [1, 0, 0, far, -1, 0, 0, far, 0, 1, 0, far, 0, -1, 0, far, 0, 0, 1, 10, 0, 0, -1, 10];
                rows = whole.WhereFrustum<WbSpatial>(planes, 6, -far, -far, -10, far, far, 10).Count();
                break;
            default:
                var hits = new (long entityId, double distSq)[Entities];
                rows = dbe._archetypeStates[Archetype<WbSpatialArch>.Metadata.ArchetypeId].ClusterState.QueryNearest(dbe.Realm0Grid, 0, 0, 0, Entities, hits,
                    categoryMask: 0);
                break;
        }

        Assert.That(rows, Is.EqualTo(Entities));
        var (slotReferenced, epochProtected) = Held(dbe);
        Assert.That(epochProtected, Is.LessThan(HeldPagesBound / 4), "the query kept the clusters it read epoch-held for the transaction");
        Assert.That(slotReferenced, Is.LessThanOrEqualTo(40), "what stays pinned is the warm window's 32: the query's scan window went with its rent");
    }

    /// <summary>Queries near an actor each tag a few pages, and in one transaction they added up to the cache: nothing on their path moved the epoch.</summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-03")]
    public void ALoopOfSpatialQueries_MovesItsEpoch()
    {
        var dbe = SpatialWorld();
        using var tx = dbe.CreateReadOnlyTransaction();
        var pinned = dbe.EpochManager.CurrentThreadPinnedEpoch;
        for (var i = 0; i < 2 * EntityAccessor.EpochRefreshInterval; i++)
        {
            tx.Query<WbSpatialArch>().WhereInAABB<WbSpatial>(i, 0, -1, i + 50, 50, 2).Count();
        }

        Assert.That(dbe.EpochManager.CurrentThreadPinnedEpoch, Is.GreaterThan(pinned), "the loop ran under one epoch");
    }

    private const float SpatialExtent = 2_000f;

    private DatabaseEngine SpatialWorld()
    {
        const float extent = SpatialExtent;
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<WbSpatial>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new System.Numerics.Vector2(-extent, -extent), new System.Numerics.Vector2(extent, extent),
            cellSize: 100));
        dbe.InitializeArchetypes();
        var rand = new System.Random(17);
        for (var first = 0; first < Entities; first += Batch)
        {
            using var spawn = dbe.CreateQuickTransaction();
            for (var i = first; i < first + Batch; i++)
            {
                var x = (float)(rand.NextDouble() * 1.9 - 0.95) * extent;
                var y = (float)(rand.NextDouble() * 1.9 - 0.95) * extent;
                var row = new WbSpatial { Seq = i, Bounds = new AABB3F { MinX = x, MinY = y, MinZ = 0, MaxX = x + 1, MaxY = y + 1, MaxZ = 1 } };
                spawn.Spawn<WbSpatialArch>(WbSpatialArch.Row.Set(in row));
            }

            spawn.Commit();
        }

        dbe.WriteTickFence(1);
        var clusterPages = dbe._archetypeStates[Archetype<WbSpatialArch>.Metadata.ArchetypeId].ClusterState.ClusterSegment.Length;
        Assert.That(clusterPages, Is.GreaterThan(HeldPagesBound), "premise: the walk spans more pages than the bound");
        return dbe;
    }

    /// <summary>
    /// A cluster walk runs its caller's code between steps, and that code can run scans, each of which restarts the transaction's yield clock. On the
    /// transaction's clock the walk then never saw a millisecond pass, and never moved its epoch.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [NonParallelizable] // LongWalkClusters is process-wide
    [VerifiesRule("EP-02")]
    public void ALongClusterWalk_KeepsItsOwnYieldClock()
    {
        var dbe = Build(Storage.Single);
        var longWalk = EntityAccessor.LongWalkClusters;
        EntityAccessor.LongWalkClusters = 0;
        try
        {
            using var tx = dbe.CreateReadOnlyTransaction();
            var pinned = dbe.EpochManager.CurrentThreadPinnedEpoch;
            using var clusters = tx.GetClusterEnumerator<WbSingleArch>();
            while (clusters.MoveNext())
            {
                var until = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 20_000;
                while (System.Diagnostics.Stopwatch.GetTimestamp() < until)
                {
                }

                tx.BeginScanYield();   // what a query in the loop's body does
            }

            Assert.That(dbe.EpochManager.CurrentThreadPinnedEpoch, Is.GreaterThan(pinned), "the walk ran under one epoch");
        }
        finally
        {
            EntityAccessor.LongWalkClusters = longWalk;
        }
    }

    /// <summary>
    /// A target's sources are unbounded. The lookup kept its cursor open across them, so no read an action made could refresh, and each record it checked was
    /// tagged: one target with more sources than the cache holds pages filled it.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void ANavigationToOneTargetWithManySources_HoldsABoundedSetOfPages()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<WbSingle>();
        dbe.RegisterComponentFromAccessor<WbVersioned>();
        dbe.RegisterComponentFromAccessor<WbChild>();
        dbe.InitializeArchetypes();
        EntityId parent;
        using (var write = dbe.CreateQuickTransaction())
        {
            var row = new WbVersioned { Key = 1 };
            parent = write.Spawn<WbVersionedArch>(WbVersionedArch.Row.Set(in row));
            write.Commit();
        }

        for (var first = 0; first < Entities; first += Batch)
        {
            using var write = dbe.CreateQuickTransaction();
            for (var i = 0; i < Batch; i++)
            {
                var child = new WbChild { ParentId = (long)parent.RawValue };
                write.Spawn<WbChildArch>(WbChildArch.Row.Set(in child));
            }

            write.Commit();
        }

        using var tx = dbe.CreateReadOnlyTransaction();
        var found = tx.Query<WbChildArch>().NavigateField<WbChild, WbVersioned>(c => c.ParentId).Where((c, p) => c.ParentId != 0 && p.Key == 1).Count();

        Assert.That(found, Is.EqualTo(Entities));
        Assert.That(Held(dbe).EpochProtected, Is.LessThan(HeldPagesBound), "the lookup kept the records it read epoch-held for the transaction");
    }

    /// <summary>
    /// A view's refresh re-evaluates every source of a target that changed, one read each, and nothing counted them: the same target over the same sources as
    /// the query above, through the other door.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void ANavigationViewRefresh_OfOneTargetWithManySources_HoldsABoundedSetOfPages()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<WbSingle>();
        dbe.RegisterComponentFromAccessor<WbVersioned>();
        dbe.RegisterComponentFromAccessor<WbChild>();
        dbe.InitializeArchetypes();
        EntityId parent;
        using (var write = dbe.CreateQuickTransaction())
        {
            var row = new WbVersioned { Key = 1 };
            parent = write.Spawn<WbVersionedArch>(WbVersionedArch.Row.Set(in row));
            write.Commit();
        }

        for (var first = 0; first < Entities; first += Batch)
        {
            using var write = dbe.CreateQuickTransaction();
            for (var i = 0; i < Batch; i++)
            {
                var child = new WbChild { ParentId = (long)parent.RawValue };
                write.Spawn<WbChildArch>(WbChildArch.Row.Set(in child));
            }

            write.Commit();
        }

        ViewBase view;
        using (var tx = dbe.CreateReadOnlyTransaction())
        {
            view = tx.Query<WbChildArch>().NavigateField<WbChild, WbVersioned>(c => c.ParentId).Where((c, p) => c.ParentId != 0 && p.Key == 1).ToView();
        }

        using (view)
        {
            using (var settle = dbe.CreateReadOnlyTransaction())
            {
                ((NavigationView<WbChild, WbVersioned>)view).Refresh(settle);
            }

            Assert.That(view.Count, Is.EqualTo(Entities), "premise: every child is in the view");
            using (var write = dbe.CreateQuickTransaction())
            {
                var row = write.Open(parent).Read(WbVersionedArch.Row);
                row.Key = 2;
                write.OpenMut(parent).Set(WbVersionedArch.Row, row);
                write.Commit();
            }

            using var tx = dbe.CreateReadOnlyTransaction();
            ((NavigationView<WbChild, WbVersioned>)view).Refresh(tx);

            Assert.That(view.Count, Is.Zero, "premise: the refresh took every child out");
            Assert.That(Held(dbe).EpochProtected, Is.LessThan(HeldPagesBound), "the refresh kept the records it read epoch-held for the transaction");
        }
    }

    /// <summary>
    /// A navigation walks every target's entity map, then looks each target's sources up. Both tagged what they read, and a whole archetype of targets
    /// filled a 64 MiB cache before the first row.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void ANavigationOverAWholeArchetype_HoldsABoundedSetOfPages()
    {
        var dbe = Build(Storage.Versioned);
        var parents = new List<EntityId>();
        using (var read = dbe.CreateReadOnlyTransaction())
        {
            parents.AddRange(read.Query<WbVersionedArch>().Execute());
        }

        using (var write = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < parents.Count; i += 100)
            {
                var child = new WbChild { ParentId = (long)parents[i].RawValue };
                write.Spawn<WbChildArch>(WbChildArch.Row.Set(in child));
            }

            write.Commit();
        }

        using var tx = dbe.CreateReadOnlyTransaction();
        var found = tx.Query<WbChildArch>().NavigateField<WbChild, WbVersioned>(c => c.ParentId).Where((c, p) => p.Key >= 0).Count();

        Assert.That(found, Is.EqualTo(Entities / 100));
        Assert.That(Held(dbe).EpochProtected, Is.LessThan(HeldPagesBound), "the navigation kept the pages it read epoch-held for the transaction");
    }

    /// <summary>
    /// A fence reclaims every cluster the commits since the last one emptied, and reads each one's occupancy to seal it. Those reads tagged their pages
    /// under the reclamation's one epoch scope, so an archetype emptied in batches filled the cache with its own tags and the fence, or the clean close
    /// after it, waited out the back-pressure bound with nothing to release. Thrown at a close, it read as frees lost.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [Property("CacheSize", 2 * 1024 * 1024)]
    [VerifiesRule("EP-02")]
    public void AReclamationOfMoreClustersThanTheCacheHolds_HoldsABoundedSetOfPages()
    {
        var clusters = EmptiedArchetype(out var dbe);
        var peakSlot = 0;
        var peakEpoch = 0;
        clusters.DrainAfterSealProbe = _ => NoteHeld(dbe, ref peakSlot, ref peakEpoch);
        clusters.DrainAfterFreeProbe = _ => NoteHeld(dbe, ref peakSlot, ref peakEpoch);
        try
        {
            Assert.DoesNotThrow(() => dbe.WriteTickFence(1), "the reclamation holds what it reads, and only that");
        }
        finally
        {
            clusters.DrainAfterSealProbe = null;
            clusters.DrainAfterFreeProbe = null;
        }

        Assert.That(clusters.LastTickClustersReclaimed, Is.GreaterThan(HeldPagesBound), "premise: it read more clusters than the bound");
        Assert.That(clusters.ActiveClusterCount, Is.Zero);
        Assert.That(peakEpoch, Is.LessThan(HeldPagesBound / 4), "the cache has 256 slots: tagged, the reclamation's pages fill it");
        Assert.That(peakSlot, Is.LessThan(HeldPagesBound / 4));
    }

    /// <summary>
    /// A reclamation takes the whole count before it reads the first record, and a read can time out. Only what it had sealed was put back: the records past
    /// the fault were gone, and their clusters stayed listed, empty, until the next open.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [Property("CacheSize", 2 * 1024 * 1024)]
    [VerifiesRule("CLUSTERWALK-03")]
    public void AThrowInAReclamation_LosesNoRecord()
    {
        var clusters = EmptiedArchetype(out var dbe);
        var seen = 0;
        clusters.DrainBeforeSealProbe = _ =>
        {
            if (++seen == 5)
            {
                throw new System.InvalidOperationException("staged fault");
            }
        };

        try
        {
            Assert.That(() => dbe.WriteTickFence(1), Throws.InvalidOperationException);
        }
        finally
        {
            clusters.DrainBeforeSealProbe = null;
        }

        Assert.That(clusters.ActiveClusterCount, Is.GreaterThan(0), "premise: the fault left the reclamation unfinished");
        dbe.WriteTickFence(2);
        Assert.That(clusters.ActiveClusterCount, Is.Zero, "every record reached a reclamation in the end");
    }

    private ArchetypeClusterState EmptiedArchetype(out DatabaseEngine dbe)
    {
        dbe = Build(Storage.Single);
        var ids = new List<EntityId>();
        using (var read = dbe.CreateReadOnlyTransaction())
        {
            ids.AddRange(read.Query<WbSingleArch>().Execute());
        }

        for (var first = 0; first < ids.Count; first += Batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = first; i < first + Batch; i++)
            {
                tx.Destroy(ids[i]);
            }

            Assert.That(tx.Commit(), Is.True);
        }

        return dbe._archetypeStates[Archetype<WbSingleArch>.Metadata.ArchetypeId].ClusterState;
    }

    /// <summary>
    /// A batch's content chunks are marked as they are written, and the refresh the batch made every 128 entities left the marks where they were: one larger
    /// than the cache filled it with them, before the call returned. Its rollback reads as many pages again, and moved its epoch only once the cache's
    /// pressure flag was up: with the flag expired, on a loaded box, it met the wall under one pin, and waited on itself.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [Property("CacheSize", 8 * 1024 * 1024)]
    [VerifiesRule("EP-03")]
    public void ASpawnBatchLargerThanTheCache_GivesBackItsMarks()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<WbSingle>();
        dbe.RegisterComponentFromAccessor<WbVersioned>();
        dbe.RegisterComponentFromAccessor<WbChild>();
        dbe.RegisterComponentFromAccessor<WbPlain>();
        dbe.RegisterComponentFromAccessor<WbMulti>();
        dbe.InitializeArchetypes();

        using var tx = dbe.CreateQuickTransaction();
        var ids = new EntityId[BatchEntities];
        var row = new WbMulti { Group = 1 };
        Assert.DoesNotThrow(() => tx.SpawnBatch<WbMultiArch>(ids, WbMultiArch.Row.Set(in row)));
        Assert.That(tx.Rollback(), Is.True);
    }

    /// <summary>
    /// What the generated <c>SpawnBatch</c> goes through: the Versioned content is allocated at the write, past the allocation's own refresh, and a batch larger
    /// than the cache filled it with its marks.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [Property("CacheSize", 8 * 1024 * 1024)]
    [VerifiesRule("EP-03")]
    public void ASpawnBatchWrittenAfterItsAllocation_GivesBackItsMarks()
    {
        var dbe = BuildForBatches();
        using var tx = dbe.CreateQuickTransaction();
        var ids = new EntityId[BatchEntities];
        var values = new WbMulti[BatchEntities];
        Assert.DoesNotThrow(() => tx.SpawnBatchWriteAll(tx.SpawnBatchAllocate<WbMultiArch>(BatchEntities, ids), BatchEntities, WbMultiArch.Row, values));
        Assert.That(tx.Rollback(), Is.True);
    }

    /// <summary>The same through what the fence's command drain goes through, one value at a time.</summary>
    [Test]
    [CancelAfter(60_000)]
    [Property("CacheSize", 8 * 1024 * 1024)]
    [VerifiesRule("EP-03")]
    public void ACommandDrainsSpawnBatch_GivesBackItsMarks()
    {
        var dbe = BuildForBatches();
        using var tx = dbe.CreateQuickTransaction();
        var meta = Array.Find(dbe.OwnArchetypes(), m => m.ArchetypeType == typeof(WbMultiArch));
        var keys = new long[BatchEntities];
        for (var i = 0; i < BatchEntities; i++)
        {
            keys[i] = 1_000_000 + i;
        }

        var ids = new EntityId[BatchEntities];
        var row = new WbMulti { Group = 1 };
        var value = WbMultiArch.Row.Set(in row);
        Assert.DoesNotThrow(() =>
        {
            var first = tx.SpawnBatchAllocateRaw(meta, keys, ids);
            for (var i = 0; i < BatchEntities; i++)
            {
                tx.SpawnWriteValueRaw(first + i, in value);
            }
        });
        Assert.That(tx.Rollback(), Is.True);
    }

    private DatabaseEngine BuildForBatches()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<WbSingle>();
        dbe.RegisterComponentFromAccessor<WbVersioned>();
        dbe.RegisterComponentFromAccessor<WbChild>();
        dbe.RegisterComponentFromAccessor<WbPlain>();
        dbe.RegisterComponentFromAccessor<WbMulti>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// A live cursor stops the epoch, and only the epoch: a batch inside an index scan kept every mark as well. The marks go back; the epoch cannot, so a batch
    /// larger than the cache still cannot run inside a scan (EP-02), and this one fits.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [Property("CacheSize", 2 * 1024 * 1024)]
    [VerifiesRule("EP-03")]
    public void ASpawnBatchInsideAnIndexScan_GivesBackItsMarks()
    {
        var dbe = BuildForBatches();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Batch; i++)
            {
                var seed = new WbVersioned { Key = i };
                tx.Spawn<WbVersionedArch>(WbVersionedArch.Row.Set(in seed));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        const int InsideAScan = 3_000;
        using var batch = dbe.CreateQuickTransaction();
        var ids = new EntityId[InsideAScan];
        using (var walk = batch.EnumerateIndex<WbVersioned, int>(dbe.GetIndexRef<WbVersioned, int>(x => x.Key), int.MinValue, int.MaxValue))
        {
            Assert.That(walk.MoveNext(), Is.True);
            Assert.That(EntityAccessor.LiveIndexCursors, Is.GreaterThan(0), "premise: the batch runs under a live cursor");
            var values = new WbMulti[InsideAScan];
            var first = batch.SpawnBatchAllocate<WbMultiArch>(InsideAScan, ids);
            batch.SpawnBatchWriteAll(first, InsideAScan, WbMultiArch.Row, values);
            Assert.That(batch.MarkedPageCount, Is.LessThan(8), "what the batch wrote since its last 128 writes, not all of it");
        }

        Assert.That(batch.Rollback(), Is.True);
    }

    /// <summary>The refresh's own cadence reads 8 entries at a time, so a modulo over it fired at their least common multiple: a quarter of a 1,000-slot cache, not
    /// a sixteenth. And an entry can touch far more pages than one: entries alone reach a whole small cache first.</summary>
    [TestCase(56, 0, 1_000, false)]
    [TestCase(64, 0, 1_000, true)]
    [TestCase(16, 0, 256, true)]
    [TestCase(8, 0, 256, false)]
    [TestCase(8, 63, 256, false)]
    [TestCase(8, 64, 256, true)]
    [TestCase(8, 0, 16, true)]
    public void ACommitGivesBack_ByEntriesOrByMarks(int steps, int marks, int cacheSlots, bool expected) =>
        Assert.That(Transaction.CommitStepsGiveBack(steps, marks, cacheSlots), Is.EqualTo(expected));

    /// <summary>
    /// Under no back-pressure a publish gave nothing back, however much of the cache its own marks had taken: an entry that then met a full cache could not
    /// step out to return them, and a publish waiting on its own marks stops the engine. It moves by its own count, on a cache that never fills.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [NonParallelizable]   // PagedMMF.PublishStepRefreshes is process-wide
    [Property("CacheSize", 16 * 1024 * 1024)]
    [VerifiesRule("EP-02")]
    public void APublishOfManyEntries_GivesBackItsMarks()
    {
        var dbe = Build(Storage.Versioned);
        var ids = new List<EntityId>();
        using (var read = dbe.CreateReadOnlyTransaction())
        {
            ids.AddRange(read.Query<WbVersionedArch>().Execute());
        }

        var refreshes = System.Threading.Interlocked.Read(ref PagedMMF.PublishStepRefreshes);
        using var tx = dbe.CreateQuickTransaction();
        foreach (var id in ids)
        {
            var row = tx.Open(id).Read(WbVersionedArch.Row);
            row.Key = -row.Key - 1;
            tx.OpenMut(id).Set(WbVersionedArch.Row, row);
        }

        Assert.That(tx.Commit(), Is.True);
        Assert.That(System.Threading.Interlocked.Read(ref PagedMMF.PublishStepRefreshes), Is.GreaterThan(refreshes), "the publish kept every mark it took");
    }

    /// <summary>
    /// A commit of more destroys than the cache holds pages is refused in PREPARE, under marks that fill the cache, and all it undoes and ends needs pages
    /// too: the undo ran out of slots with the whole cache under its own marks, out of Dispose, which replaced the refusal with a timeout. A commit that fits
    /// ends cleanly.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [Property("CacheSize", 2 * 1024 * 1024)]
    [VerifiesRule("EP-02")]
    public void ACommitOfMoreDestroysThanTheCacheHolds_EndsItsUnitOfWork()
    {
        var dbe = Build(Storage.Single);
        var ids = new List<EntityId>();
        using (var read = dbe.CreateReadOnlyTransaction())
        {
            ids.AddRange(read.Query<WbSingleArch>().Execute());
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var id in ids)
            {
                tx.Destroy(id);
            }

            var committed = false;
            try
            {
                committed = tx.Commit();
            }
            catch (TransactionExceedsPageCacheException)
            {
                // Refused: the caller's exception, and the undo below must not replace it.
            }

            Assert.DoesNotThrow(tx.Dispose, "ending it must not fail for want of a page its own marks hold");
            using var after = dbe.CreateReadOnlyTransaction();
            Assert.That(after.Query<WbSingleArch>().Count(), Is.EqualTo(committed ? 0 : Entities), "all of the commit, or none of it");
        }
    }

    /// <summary>
    /// A rollback frees a copy-on-write chunk per update, and each free reads its page. Its epoch never moved and its accessors' deferred releases were never
    /// drained, so 4M updates in one transaction ended in a back-pressure timeout out of Dispose, which replaced the commit's own refusal.
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    [CancelAfter(60_000)]
    [Property("CacheSize", 2 * 1024 * 1024)]
    [VerifiesRule("EP-02")]
    public void ARollbackOfMoreUpdatesThanTheCacheHolds_Completes(bool explicitRollback)
    {
        var dbe = Build(Storage.Versioned);
        var ids = new List<EntityId>();
        using (var read = dbe.CreateReadOnlyTransaction())
        {
            ids.AddRange(read.Query<WbVersionedArch>().Execute());
        }

        using var tx = dbe.CreateQuickTransaction();
        foreach (var id in ids)
        {
            var row = tx.Open(id).Read(WbVersionedArch.Row);
            row.Key = -row.Key - 1;
            tx.OpenMut(id).Set(WbVersionedArch.Row, row);
        }

        if (explicitRollback)
        {
            Assert.That(tx.Rollback(), Is.True);
        }
        else
        {
            Assert.Throws<TransactionExceedsPageCacheException>(() => tx.Commit(),
                "premise: the commit does not fit, and its own refusal is what the caller sees");
        }

        Assert.DoesNotThrow(tx.Dispose, "the rollback reached the end (the using disposes it again, a no-op)");

        using var after = dbe.CreateReadOnlyTransaction();
        var seen = 0;
        using var walk = after.EnumerateIndex<WbVersioned, int>(dbe.GetIndexRef<WbVersioned, int>(x => x.Key), 0, int.MaxValue);
        while (walk.MoveNext())
        {
            seen++;
        }

        Assert.That(seen, Is.EqualTo(Entities), "every row kept its committed key");
    }
}
