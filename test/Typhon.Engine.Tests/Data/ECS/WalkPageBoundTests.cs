using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
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
        Assert.That(refreshes, Is.GreaterThanOrEqualTo(Entities / EntityAccessor.EpochRefreshInterval - 1), $"premise: the walk refreshed (pressure {pressureBefore})");
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
    [VerifiesRule("EP-02")]
    public void AClusterWalkOverAWholeArchetype_HoldsABoundedSetOfPages(ClusterWalk how)
    {
        var dbe = Build(Storage.Single);
        var rows = 0;
        if (how == ClusterWalk.PointInTimeLease)
        {
            using var pta = PointInTimeAccessor.Create(dbe);
            using var lease = pta.Enter(0);
            using var clusters = lease.Accessor.GetClusterEnumerator<WbSingleArch>();
            while (clusters.MoveNext())
            {
                rows += System.Numerics.BitOperations.PopCount(clusters.Current.OccupancyBits);
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
    /// A transaction's long cluster walk moves its epoch at its yield points (#1364): held, it keeps every page another thread tags meanwhile. A write through a
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

        dbe.ForceCheckpoint();
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

    /// <summary>A spatial query over the whole world walks every cluster in a window that tagged each page: it died before its first row.</summary>
    [TestCase(false)]
    [TestCase(true)]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void ASpatialQueryOverTheWholeWorld_HoldsABoundedSetOfPages(bool count)
    {
        const float extent = 2_000f;
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<WbSpatial>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new System.Numerics.Vector2(-extent, -extent), new System.Numerics.Vector2(extent, extent), cellSize: 100));
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

        using var tx = dbe.CreateReadOnlyTransaction();
        var query = tx.Query<WbSpatialArch>().WhereInAABB<WbSpatial>(-extent, -extent, -1, extent, extent, 2);
        var rows = count ? query.Count() : query.Execute().Count;

        Assert.That(rows, Is.EqualTo(Entities));
        Assert.That(Held(dbe).EpochProtected, Is.LessThan(HeldPagesBound), "the query kept the clusters it read epoch-held for the transaction");
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
    /// A commit of more destroys than the cache holds pages is refused in PREPARE, under marks that fill the cache, and all it undoes and ends needs pages too: the
    /// undo ran out of slots with the whole cache under its own marks, out of Dispose, which replaced the refusal with a timeout. A commit that fits ends cleanly.
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
            Assert.Throws<TransactionExceedsPageCacheException>(() => tx.Commit(), "premise: the commit does not fit, and its own refusal is what the caller sees");
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
