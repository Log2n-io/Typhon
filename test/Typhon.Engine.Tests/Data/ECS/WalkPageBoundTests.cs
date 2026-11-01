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
