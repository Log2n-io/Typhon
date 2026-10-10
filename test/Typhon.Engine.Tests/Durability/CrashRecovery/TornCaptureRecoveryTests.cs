using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// A checkpoint copies a transaction's pages at different moments: the data file can hold one page after the transaction wrote it and another from
/// before. Recovery replays the transaction over that (#1251): it must end with the transaction's effects whatever subset of its pages reached the disk.
/// </summary>
[TestFixture]
internal sealed class TornCaptureRecoveryTests : TestBase<TornCaptureRecoveryTests>
{
    // The reopen must find the WAL it crashed with; no periodic checkpoint, the test writes what reaches the data file.
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
        o.Wal.WalDirectory = null;
    }

    [Flags]
    public enum Written
    {
        Map = 1,
        Cluster = 2,
        MapAndCluster = 3,
    }

    private static DatabaseEngine Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<MkTrader>();
        dbe.RegisterComponentFromAccessor<MkWallet>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Writes every page of <paramref name="segment"/> to the data file, as a checkpoint that copied them after the last write would have.</summary>
    private static unsafe void WriteSegmentPages(DatabaseEngine dbe, ChunkBasedSegment<PersistentStore> segment)
    {
        var pages = new HashSet<int>();
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var a = segment.CreateChunkAccessor();
            try
            {
                for (var p = 0; p < segment.Length; p++)
                {
                    var chunk = p == 0 ? 0 : segment.ChunkCountRootPage + (p - 1) * segment.ChunkCountPerPage;
                    if (chunk < segment.ChunkCapacity)
                    {
                        pages.Add(dbe.MMF.MemPageIndexOf(a.GetChunkAddress(chunk)));
                    }
                }
            }
            finally
            {
                a.Dispose();
            }
        }

        dbe.MMF.SavePages([.. pages]).GetAwaiter().GetResult();
    }

    private static DatabaseEngine RegisterMarket(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<MkItem>();
        dbe.RegisterComponentFromAccessor<MkLore>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static MkLore Lore(long seed)
    {
        var lore = new MkLore();
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<MkLore, long>(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref lore, 1));
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = seed * 1_000 + i;
        }

        return lore;
    }

    /// <summary>
    /// The market's craft: an item destroyed and spawned anew in one transaction, its lore enabled on the new entity — durable in the log, then a crash
    /// with any subset of the archetype's map, cluster, revision and content pages on disk as of after the transaction. The recovered market holds the
    /// item once, with its values and its lore.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void ACraftWhosePagesReachedTheDiskInPart_Recovers([Range(0, 15)] int written)
    {
        EntityId newId;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < 8; i++)
                {
                    var spawned = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = i, OwnerNo = 1, Price = 10, LastTradeSeq = -1 - i }));
                    tx.OpenMut(spawned).Enable(MkItemArch.Lore, Lore(i));
                }

                Assert.That(tx.Commit(), Is.True, "the build");
            }

            var oldId = default(EntityId);
            using (var tx = dbe.CreateReadOnlyTransaction())
            {
                foreach (var id in tx.Query<MkItemArch>().Execute())
                {
                    if (tx.Open(id).Read(MkItemArch.Item).ItemNo == 5)
                    {
                        oldId = id;
                    }
                }
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    tx.Destroy(oldId);
                    newId = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 5, OwnerNo = 7, Price = 99, TradeCount = 1, LastTradeSeq = 42 }));
                    tx.OpenMut(newId).Enable(MkItemArch.Lore, Lore(5));
                    Assert.That(tx.Commit(), Is.True, "the craft");
                }

                uow.Flush();
            }

            var es = dbe._archetypeStates[dbe.GetMetaByRouting(newId.ArchetypeId).ArchetypeId];
            var itemTable = dbe.GetComponentTable<MkItem>();
            if ((written & 1) != 0)
            {
                WriteSegmentPages(dbe, es.EntityMap.Segment);
            }

            if ((written & 2) != 0)
            {
                WriteSegmentPages(dbe, es.ClusterState.ClusterSegment);
            }

            if ((written & 4) != 0)
            {
                WriteSegmentPages(dbe, itemTable.CompRevTableSegment);
            }

            if ((written & 8) != 0)
            {
                WriteSegmentPages(dbe, itemTable.ComponentSegment);
            }

            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");

        // Spawns after the recovery take the chunks it left free: none may be one the crafted item holds.
        using (var tx = reopened.CreateQuickTransaction())
        {
            for (var i = 0; i < 8; i++)
            {
                tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 100 + i, OwnerNo = 1, LastTradeSeq = 100 + i }));
            }

            Assert.That(tx.Commit(), Is.True, "the spawns after the recovery");
        }

        using var read = reopened.CreateReadOnlyTransaction();
        var holders = new List<(EntityId Id, MkItem Item)>();
        foreach (var id in read.Query<MkItemArch>().Execute())
        {
            var item = read.Open(id).Read(MkItemArch.Item);
            if (item.ItemNo == 5 || item.ItemNo == 0 && item.OwnerNo == 0)
            {
                holders.Add((id, item));
            }
        }

        Assert.That(holders.ConvertAll(h => h.Id), Is.EqualTo(new[] { newId }), "the live entities holding item 5, or nothing");
        Assert.That(holders[0].Item.OwnerNo, Is.EqualTo(7), "the crafted item's owner");
        Assert.That(holders[0].Item.LastTradeSeq, Is.EqualTo(42), "the crafted item's last trade");
        Assert.That(read.Open(newId).Read(MkItemArch.Lore).W03, Is.EqualTo(5_003), "the crafted item's lore");
        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// An update committed and durable in the log, then a crash with any subset of the archetype's cluster, revision and content pages on disk as of
    /// after it. With the revision chain on disk and not the content, the chain held the update's committed element over a zeroed content chunk; the
    /// replay appended the logged value at the same TSN, and the scrub kept the first of the two — the entity read all zeros (#1251).
    /// </summary>
    [Test]
    [VerifiesRule("AP-12")]
    [CancelAfter(30_000)]
    public void AnUpdateWhosePagesReachedTheDiskInPart_Recovers([Range(0, 7)] int written)
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction())
            {
                id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 5, OwnerNo = 1, Price = 10, LastTradeSeq = -1 }));
                Assert.That(tx.Commit(), Is.True, "the build");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    tx.OpenMut(id).Set(MkItemArch.Item, new MkItem { ItemNo = 5, OwnerNo = 7, Price = 99, TradeCount = 1, LastTradeSeq = 42 });
                    Assert.That(tx.Commit(), Is.True, "the update");
                }

                uow.Flush();
            }

            var es = dbe._archetypeStates[dbe.GetMetaByRouting(id.ArchetypeId).ArchetypeId];
            var itemTable = dbe.GetComponentTable<MkItem>();
            if ((written & 1) != 0)
            {
                WriteSegmentPages(dbe, es.ClusterState.ClusterSegment);
            }

            if ((written & 2) != 0)
            {
                WriteSegmentPages(dbe, itemTable.CompRevTableSegment);
            }

            if ((written & 4) != 0)
            {
                WriteSegmentPages(dbe, itemTable.ComponentSegment);
            }

            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");

        // Spawns after the recovery take whatever chunks it left free: none may be one the item's head holds — the torn element and the replay's
        // twin shared a chunk, and freeing the dropped element freed the head's content too.
        using (var tx = reopened.CreateQuickTransaction())
        {
            for (var i = 0; i < 8; i++)
            {
                tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 100 + i, OwnerNo = 1, LastTradeSeq = 100 + i }));
            }

            Assert.That(tx.Commit(), Is.True, "the spawns after the recovery");
        }

        using var read = reopened.CreateReadOnlyTransaction();
        var item = read.Open(id).Read(MkItemArch.Item);
        Assert.That((item.ItemNo, item.OwnerNo, item.LastTradeSeq), Is.EqualTo((5L, 7L, 42L)), "the updated item");
    }

    /// <summary>
    /// Updates of several items, committed in an order their replay does not follow, then a crash with the revision chains on disk as of after them and
    /// the content as of before. Each chain held its update's committed element over a content chunk the disk had as free; the replay, appending the
    /// logged values, took those chunks for other items — and the scrub, keeping the first element of a TSN, gave each item another's content, or none.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void UpdatesReplayedInAnotherOrder_OverTornChains_KeepTheirOwnValues()
    {
        var ids = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < 6; i++)
                {
                    ids.Add(tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = i, OwnerNo = 1, Price = 10, LastTradeSeq = -1 - i })));
                }

                Assert.That(tx.Commit(), Is.True, "the build");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            using (var uow = dbe.CreateUnitOfWork())
            {
                for (var i = ids.Count - 1; i >= 0; i--)
                {
                    using var tx = uow.CreateTransaction(CommitDiscipline.Commit);
                    tx.OpenMut(ids[i]).Set(MkItemArch.Item, new MkItem { ItemNo = i, OwnerNo = 100 + i, Price = 99, TradeCount = 1, LastTradeSeq = 1_000 + i });
                    Assert.That(tx.Commit(), Is.True, "the update");
                }

                uow.Flush();
            }

            WriteSegmentPages(dbe, dbe.GetComponentTable<MkItem>().CompRevTableSegment);
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        using var read = reopened.CreateReadOnlyTransaction();
        var got = new List<(long, long, long)>();
        var expected = new List<(long, long, long)>();
        for (var i = 0; i < ids.Count; i++)
        {
            var item = read.Open(ids[i]).Read(MkItemArch.Item);
            got.Add((item.ItemNo, item.OwnerNo, item.LastTradeSeq));
            expected.Add((i, 100 + i, 1_000 + i));
        }

        Assert.That(got, Is.EqualTo(expected), "each item's update");
    }

    /// <summary>The root chunk of the entity's <see cref="MkItem"/> revision chain, from its entity record.</summary>
    private static unsafe int ItemRootOf(DatabaseEngine dbe, EntityId id)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var ct = dbe.GetComponentTable<MkItem>();
        var meta = dbe.GetMetaByRouting(id.ArchetypeId);
        var es = dbe._archetypeStates[meta.ArchetypeId];
        var slot = 0;
        for (var s = 0; s < meta.ComponentCount; s++)
        {
            if (ReferenceEquals(es.SlotToComponentTable[s], ct))
            {
                slot = s;
            }
        }

        var record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        var a = es.EntityMap.Segment.CreateChunkAccessor();
        Assert.That(es.EntityMap.TryGet(id.EntityKey, record, ref a), Is.True, "the entity's record");
        a.Dispose();
        return ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, es.ClusterState.Layout.SlotToVersionedIndex[slot]);
    }

    /// <summary>
    /// A chunk of the revision segment allocated in the data file and naming a live chain's root as its next — what a chain's overflow chunk looks like
    /// on disk when its page was written before a compaction freed it and the root's after the engine reused it. The crash path takes a chain root to be
    /// a chunk no other chunk names: the live entity lost its chain — its record rebuilt with no root, the chain swept as an orphan (#1251).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public unsafe void ALiveRootNamedByAStaleChunk_KeepsItsChain([Values(0, 2)] short staleLength)
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction())
            {
                id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 5, OwnerNo = 7, Price = 99, LastTradeSeq = 42 }));
                Assert.That(tx.Commit(), Is.True, "the spawn");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            var root = ItemRootOf(dbe, id);
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                var cs = dbe.MMF.CreateChangeSet();
                var a = revisions.CreateChunkAccessor(cs);
                var stale = revisions.AllocateChunk(cs, ref a);
                *(int*)a.GetChunkAddress(stale, true) = root;

                // Its bytes at the chain length's offset: a previous life's root of two chunks, or an overflow chunk's first element's TSN. Walked from
                // it, the live root was a chunk its chain reached — not a root, and its entity lost its chain (review of #1251).
                a.GetChunk<CompRevStorageHeader>(stale, true).ChainLength = staleLength;
                a.Dispose();
                cs.ReleaseDirtyMarks();
            }

            WriteSegmentPages(dbe, revisions);
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        using (var read = reopened.CreateReadOnlyTransaction())
        {
            var item = read.Open(id).Read(MkItemArch.Item);
            Assert.That((item.ItemNo, item.OwnerNo, item.LastTradeSeq), Is.EqualTo((5L, 7L, 42L)), "the item");
        }

        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// A destroy durable in the log only, then a crash: the replay tombstoned the entity and left its cluster slot occupied, so the index rebuild indexed
    /// it — an index lookup returned the destroyed entity — and its slot and chain were never reclaimed (#1251).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void ADestroyReplayed_LeavesNothingOfTheEntity()
    {
        EntityId gone, kept;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction())
            {
                gone = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 1, OwnerNo = 7, LastTradeSeq = 1 }));
                kept = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 2, OwnerNo = 7, LastTradeSeq = 2 }));
                Assert.That(tx.Commit(), Is.True, "the spawns");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    tx.Destroy(gone);
                    Assert.That(tx.Commit(), Is.True, "the destroy");
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        var ownerIndex = reopened.GetIndexRef<MkItem, long>(x => x.OwnerNo);
        var underOwner = new List<long>();
        using (var read = reopened.CreateReadOnlyTransaction())
        {
            Assert.That(read.IsAlive(gone), Is.False, "premise: the destroyed entity is dead");
            using var e = read.EnumerateIndex<MkItem, long>(ownerIndex, 7, 7);
            while (e.MoveNext())
            {
                underOwner.Add(e.CurrentEntityPK);
            }
        }

        Assert.That(underOwner, Is.EqualTo(new[] { (long)kept.RawValue }), "the owner index's entries under the owner");
        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// A full chain whose overflow chunk the data file has as free — the chain's pages written after the chunk was taken, the chunk's own before — then
    /// an update durable in the log only. The replay grew the chain and took a revision chunk: the free one, its own overflow, which it cleared and linked
    /// after itself. The chain lost its elements; on the market it ended early and the next growth threw, failing the crash open (#1251). With its root's
    /// lock word captured held: the crash reset of lock words took for a root only a chunk whose links run through allocated chunks — not this one, and
    /// every walk of the chain waited on the dead hold (review of #1251).
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public unsafe void AReplayGrowingAChainWhoseOverflowIsFreeOnDisk_KeepsTheChain([Values] bool heldLock)
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction())
            {
                id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 5, OwnerNo = 1, LastTradeSeq = 0 }));
                Assert.That(tx.Commit(), Is.True, "the spawn");
            }

            // A reader keeps every revision: the root's and a full overflow chunk's.
            var pin = dbe.CreateReadOnlyTransaction();
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            for (var i = 1; i < ComponentRevisionManager.CompRevCountInRoot + ComponentRevisionManager.CompRevCountInNext; i++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.OpenMut(id).Set(MkItemArch.Item, new MkItem { ItemNo = 5, OwnerNo = 1, LastTradeSeq = i });
                Assert.That(tx.Commit(), Is.True, "an update");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");

            // The full chain on disk with its overflow chunk free in the bitmap; allocated again in memory, so the live engine goes on as before.
            var root = ItemRootOf(dbe, id);
            int overflow;
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                var a = revisions.CreateChunkAccessor();
                ref var header = ref a.GetChunk<CompRevStorageHeader>(root);
                var full = ComponentRevisionManager.CompRevCountInRoot + ComponentRevisionManager.CompRevCountInNext;
                Assert.That((header.ChainLength, header.ItemCount), Is.EqualTo((2, full)),
                    "premise: a full chain of the root and one overflow chunk");
                overflow = header.NextChunkId;
                a.Dispose();
            }

            if (heldLock)
            {
                // Held by another thread, gone by the reopen; freed in memory once on disk, so the live engine goes on.
                System.Threading.Tasks.Task.Run(() =>
                {
                    using var g = EpochGuard.Enter(dbe.EpochManager);
                    var cs = dbe.MMF.CreateChangeSet();
                    var a = revisions.CreateChunkAccessor(cs);
                    a.GetChunk<CompRevStorageHeader>(root, true).EnterControlLockForTest();
                    a.Dispose();
                    cs.ReleaseDirtyMarks();
                }).GetAwaiter().GetResult();
            }

            revisions.FreeChunk(overflow);
            WriteSegmentPages(dbe, revisions);
            revisions.ReserveChunk(overflow);
            if (heldLock)
            {
                using var g = EpochGuard.Enter(dbe.EpochManager);
                var cs = dbe.MMF.CreateChangeSet();
                var a = revisions.CreateChunkAccessor(cs);
                *(int*)((byte*)a.GetChunkAddress(root, true) + sizeof(int)) = 0;
                a.Dispose();
                cs.ReleaseDirtyMarks();
            }

            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    tx.OpenMut(id).Set(MkItemArch.Item, new MkItem { ItemNo = 5, OwnerNo = 7, LastTradeSeq = 42 });
                    Assert.That(tx.Commit(), Is.True, "the logged update");
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
            try
            {
                pin.Dispose();
            }
            catch (Exception)
            {
                // The engine is gone under it.
            }
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        using (var read = reopened.CreateReadOnlyTransaction())
        {
            var item = read.Open(id).Read(MkItemArch.Item);
            Assert.That((item.ItemNo, item.OwnerNo, item.LastTradeSeq), Is.EqualTo((5L, 7L, 42L)), "the item, its logged update replayed");
        }

        AssertStorageHealthy(reopened);
    }

    private static void SpawnItemsAfterRecovery(DatabaseEngine dbe, int count = 8)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 100 + i, OwnerNo = 1, LastTradeSeq = 100 + i }));
        }

        Assert.That(tx.Commit(), Is.True, "the spawns after the recovery");
    }

    private static void AssertStorageHealthy(DatabaseEngine dbe)
    {
        var report = dbe.RunStorageIntegrityCheck();
        Assert.That(report.Issues, Is.Empty, "the storage integrity check, after the recovery");
    }

    /// <summary>
    /// A craft whose cluster and revision pages reached the disk after it, then destroys that left holes below its chain's root — durable in the log
    /// only. The replay re-applied the spawn on a fresh root in a hole, below the old one; the scrub, keeping one root per entity, kept the old and swept
    /// the fresh — the one the record names. After the next allocations, the item read another's chain (review of #1251).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void ACraftReappliedIntoAHoleBelowItsOldRoot_KeepsItsChain()
    {
        EntityId newId;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            var ids = new List<EntityId>();
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < 8; i++)
                {
                    ids.Add(tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = i, OwnerNo = 1, LastTradeSeq = -1 - i })));
                }

                Assert.That(tx.Commit(), Is.True, "the build");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    tx.Destroy(ids[5]);
                    newId = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 5, OwnerNo = 7, Price = 99, TradeCount = 1, LastTradeSeq = 42 }));
                    Assert.That(tx.Commit(), Is.True, "the craft");
                }

                // Holes below the craft's chain: the first items destroyed, their chains freed.
                for (var i = 0; i < 4; i++)
                {
                    using var tx = uow.CreateTransaction(CommitDiscipline.Commit);
                    tx.Destroy(ids[i]);
                    Assert.That(tx.Commit(), Is.True, "a destroy");
                }

                uow.Flush();
            }

            dbe.FlushDeferredCleanups();
            var es = dbe._archetypeStates[dbe.GetMetaByRouting(newId.ArchetypeId).ArchetypeId];
            WriteSegmentPages(dbe, es.ClusterState.ClusterSegment);
            WriteSegmentPages(dbe, dbe.GetComponentTable<MkItem>().CompRevTableSegment);
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        SpawnItemsAfterRecovery(reopened);
        using (var read = reopened.CreateReadOnlyTransaction())
        {
            var item = read.Open(newId).Read(MkItemArch.Item);
            Assert.That((item.ItemNo, item.OwnerNo, item.LastTradeSeq), Is.EqualTo((5L, 7L, 42L)), "the crafted item");
        }

        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// Two archetypes holding one Versioned component share its table, their updates durable in the log only. The scrub ran per archetype and swept the
    /// table each time with that archetype's roots: the other's chains were freed under their records (review of #1251).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void TwoArchetypesSharingAVersionedComponent_KeepTheirChains()
    {
        EntityId a, ae;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompE_Eng>();
            RegisterComponents(dbe);
            dbe.InitializeArchetypes();
            using (var tx = dbe.CreateQuickTransaction())
            {
                a = tx.Spawn<CompAArch>(CompAArch.A.Set(new CompA(1)));
                ae = tx.Spawn<CompAEArch>(CompAEArch.A.Set(new CompA(2)), CompAEArch.E.Set(new CompE_Eng(3)));
                Assert.That(tx.Commit(), Is.True, "the spawns");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    tx.OpenMut(a).Set(CompAArch.A, new CompA(10));
                    tx.OpenMut(ae).Set(CompAEArch.A, new CompA(20));
                    Assert.That(tx.Commit(), Is.True, "the updates");
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        reopened.RegisterComponentFromAccessor<CompE_Eng>();
        RegisterComponents(reopened);
        reopened.InitializeArchetypes();
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        using (var tx = reopened.CreateQuickTransaction())
        {
            for (var i = 0; i < 8; i++)
            {
                tx.Spawn<CompAArch>(CompAArch.A.Set(new CompA(100 + i)));
                tx.Spawn<CompAEArch>(CompAEArch.A.Set(new CompA(200 + i)), CompAEArch.E.Set(new CompE_Eng(300 + i)));
            }

            Assert.That(tx.Commit(), Is.True, "the spawns after the recovery");
        }

        using (var read = reopened.CreateReadOnlyTransaction())
        {
            Assert.That(read.Open(a).Read(CompAArch.A).A, Is.EqualTo(10), "the first archetype's entity");
            Assert.That(read.Open(ae).Read(CompAEArch.A).A, Is.EqualTo(20), "the second archetype's entity");
        }

        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// A spawn and a destroy of one entity in the window, the cluster page on disk as of between the two. The replay skipped the pair as net
    /// not-alive, but the map, rebuilt from the clusters, had the entity: it came back alive (review of #1251).
    /// </summary>
    [Test]
    [VerifiesRule("AP-12")]
    [CancelAfter(30_000)]
    public void ASpawnAndItsDestroyInTheWindow_LeaveTheEntityDead()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            EntityId built;
            using (var tx = dbe.CreateQuickTransaction())
            {
                built = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 1, OwnerNo = 1, LastTradeSeq = 1 }));
                Assert.That(tx.Commit(), Is.True, "the build");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            var es = dbe._archetypeStates[dbe.GetMetaByRouting(built.ArchetypeId).ArchetypeId];
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 2, OwnerNo = 7, LastTradeSeq = 2 }));
                    Assert.That(tx.Commit(), Is.True, "the spawn");
                }

                uow.Flush();
            }

            WriteSegmentPages(dbe, es.ClusterState.ClusterSegment);
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    tx.Destroy(id);
                    Assert.That(tx.Commit(), Is.True, "the destroy");
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        using var read = reopened.CreateReadOnlyTransaction();
        Assert.That(read.IsAlive(id), Is.False, "the destroyed entity");
        var live = 0;
        foreach (var _ in read.Query<MkItemArch>().Execute())
        {
            live++;
        }

        Assert.That(live, Is.EqualTo(1), "the archetype's live entities");
    }

    /// <summary>
    /// A chain root named by a stale chunk, its lock word captured held. The chain-head scan takes it as its entity's root, but the crash reset of
    /// lock words skipped named chunks: every walk of the chain the open made waited on the dead hold, and the open failed (review of #1251).
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public unsafe void ANamedRootWithAHeldLock_IsFreedAtTheOpen()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction())
            {
                id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 5, OwnerNo = 7, Price = 99, LastTradeSeq = 42 }));
                Assert.That(tx.Commit(), Is.True, "the spawn");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            var root = ItemRootOf(dbe, id);
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                var cs = dbe.MMF.CreateChangeSet();
                var a = revisions.CreateChunkAccessor(cs);
                var stale = revisions.AllocateChunk(cs, ref a);
                *(int*)a.GetChunkAddress(stale, true) = root;
                a.Dispose();
                cs.ReleaseDirtyMarks();
            }

            // Held by another thread, gone by the reopen: the lock word names a holder no thread of the reopen is.
            System.Threading.Tasks.Task.Run(() =>
            {
                using var g = EpochGuard.Enter(dbe.EpochManager);
                var cs = dbe.MMF.CreateChangeSet();
                var a = revisions.CreateChunkAccessor(cs);
                a.GetChunk<CompRevStorageHeader>(root, true).EnterControlLockForTest();
                a.Dispose();
                cs.ReleaseDirtyMarks();
            }).GetAwaiter().GetResult();

            WriteSegmentPages(dbe, revisions);
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        using var read = reopened.CreateReadOnlyTransaction();
        Assert.That(read.Open(id).Read(MkItemArch.Item).LastTradeSeq, Is.EqualTo(42), "the item");
    }

    /// <summary>
    /// A chain root whose links run past its length — its next chunk linking on to another. The scrub collected the overflow chunks into a span of
    /// the chain's length and walked a few more links as a cycle guard: the extra link overran the span, and the open failed (review of #1251).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public unsafe void AChainLinkedPastItsLength_DoesNotFailTheOpen()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction())
            {
                id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 5, OwnerNo = 7, LastTradeSeq = 42 }));
                Assert.That(tx.Commit(), Is.True, "the spawn");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            var root = ItemRootOf(dbe, id);
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                var cs = dbe.MMF.CreateChangeSet();
                var a = revisions.CreateChunkAccessor(cs);
                int first = revisions.AllocateChunk(cs, ref a), second = revisions.AllocateChunk(cs, ref a);
                *(int*)a.GetChunkAddress(first, true) = second;
                *(int*)a.GetChunkAddress(second, true) = first;
                a.GetChunk<CompRevStorageHeader>(root, true).NextChunkId = first;
                a.Dispose();
                cs.ReleaseDirtyMarks();
            }

            WriteSegmentPages(dbe, revisions);
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        using var read = reopened.CreateReadOnlyTransaction();
        Assert.That(read.Open(id).Read(MkItemArch.Item).LastTradeSeq, Is.EqualTo(42), "the item");
    }

    /// <summary>
    /// A chain whose root names, past its length, another entity's live root — its root page as of after a growth, its tail as of a previous life.
    /// The scrub took ChainLength overflow chunks where a chain has ChainLength - 1, and freed the other entity's root under its record (review of #1251).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public unsafe void AChainLinkingPastItsLengthIntoAnotherRoot_LeavesThatRootAlive([Values(1, 2)] short length)
    {
        EntityId first, second;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction())
            {
                first = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 1, OwnerNo = 7, LastTradeSeq = 41 }));
                second = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 2, OwnerNo = 8, LastTradeSeq = 42 }));
                Assert.That(tx.Commit(), Is.True, "the spawns");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            int firstRoot = ItemRootOf(dbe, first), secondRoot = ItemRootOf(dbe, second);
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                var cs = dbe.MMF.CreateChangeSet();
                var a = revisions.CreateChunkAccessor(cs);
                Assert.That(a.GetChunk<CompRevStorageHeader>(firstRoot).ChainLength, Is.EqualTo(1), "premise: a chain of its root alone");
                a.GetChunk<CompRevStorageHeader>(firstRoot, true).NextChunkId = secondRoot;

                // Of two: its root page as of before a compaction, naming as its overflow the chunk since reused as the other root (review of #1251).
                a.GetChunk<CompRevStorageHeader>(firstRoot, true).ChainLength = length;
                a.Dispose();
                cs.ReleaseDirtyMarks();
            }

            WriteSegmentPages(dbe, revisions);
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        SpawnItemsAfterRecovery(reopened);
        using (var read = reopened.CreateReadOnlyTransaction())
        {
            Assert.That(read.Open(first).Read(MkItemArch.Item).LastTradeSeq, Is.EqualTo(41), "the first item");
            Assert.That(read.Open(second).Read(MkItemArch.Item).LastTradeSeq, Is.EqualTo(42), "the second item, whose root was named");
        }

        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// An allocated chunk nothing names, its bytes an overflow chunk's whose predecessor's page is as of before its growth: its first element's TSN modulo
    /// 2^16 — read as the chain length — above 2^15, so negative; its second element void, an EntityPK of 0. The scrub took every unnamed chunk of an
    /// archetype the open had not loaded as a root, routing 0 included, and sized its spans from that length: the stack overflowed, at every open of the
    /// file (review of #1251).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public unsafe void AnOrphanReadingAsAChainOfNegativeLength_DoesNotFailTheOpen()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction())
            {
                id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 5, OwnerNo = 7, LastTradeSeq = 42 }));
                Assert.That(tx.Commit(), Is.True, "the spawn");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                var cs = dbe.MMF.CreateChangeSet();
                var a = revisions.CreateChunkAccessor(cs);
                var orphan = revisions.AllocateChunk(cs, ref a);
                var bytes = (byte*)a.GetChunkAddress(orphan, true);
                new Span<byte>(bytes, revisions.Stride).Clear();
                *(int*)(bytes + 4) = 7;
                *(uint*)(bytes + 8) = 1;
                *(ushort*)(bytes + 12) = 0x8001;
                Assert.That(a.GetChunk<CompRevStorageHeader>(orphan).ChainLength, Is.Negative, "premise: read as a header, a negative chain length");
                a.Dispose();
                cs.ReleaseDirtyMarks();
            }

            WriteSegmentPages(dbe, revisions);
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        using (var read = reopened.CreateReadOnlyTransaction())
        {
            Assert.That(read.Open(id).Read(MkItemArch.Item).LastTradeSeq, Is.EqualTo(42), "the item");
        }

        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// A live overflow chunk whose bytes read as a root's header: its first element's TSN ≡ 1 modulo 2^16 is a chain length of 1, its second element an
    /// EntityPK. The crash reset of lock words took it for a root and cleared its bytes 4..7 — the first element's content chunk id: the head became a
    /// tombstone (review of #1251).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public unsafe void AnOverflowChunkThatReadsAsARoot_KeepsItsElements()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction())
            {
                id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 5, OwnerNo = 1, LastTradeSeq = 0 }));
                Assert.That(tx.Commit(), Is.True, "the spawn");
            }

            // A reader keeps every revision: the root's three, then two in an overflow chunk.
            var pin = dbe.CreateReadOnlyTransaction();
            for (var i = 1; i <= ComponentRevisionManager.CompRevCountInRoot + 1; i++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.OpenMut(id).Set(MkItemArch.Item, new MkItem { ItemNo = 5, OwnerNo = 1, LastTradeSeq = i });
                Assert.That(tx.Commit(), Is.True, "an update");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            var root = ItemRootOf(dbe, id);
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                var cs = dbe.MMF.CreateChangeSet();
                var a = revisions.CreateChunkAccessor(cs);
                var overflow = a.GetChunk<CompRevStorageHeader>(root).NextChunkId;
                Assert.That(overflow, Is.Not.Zero, "premise: an overflow chunk");

                // Its first element — the newest revision but one — gets TSN 2^16 + 1 (the head, the highest TSN): read as a header, a chain of one.
                var element = (byte*)a.GetChunkAddress(overflow, true) + sizeof(int);
                *(uint*)(element + 4) = 1;
                *(ushort*)(element + 8) = 1;
                ref var asHeader = ref a.GetChunk<CompRevStorageHeader>(overflow);
                Assert.That((asHeader.ChainLength, asHeader.Control.SharedUsedCounter != 0 || asHeader.Control.LockedByThreadId != 0 || *(int*)(element) != 0),
                    Is.EqualTo(((short)1, true)), "premise: read as a header, a chain of one with a lock word set");
                a.Dispose();
                cs.ReleaseDirtyMarks();
            }

            WriteSegmentPages(dbe, revisions);
            dbe.SimulateHardCrash();
            try
            {
                pin.Dispose();
            }
            catch (Exception)
            {
                // The engine is gone under it.
            }
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        using var read = reopened.CreateReadOnlyTransaction();
        Assert.That(read.IsAlive(id), Is.True, "the item");
        Assert.That(read.Open(id).Read(MkItemArch.Item).LastTradeSeq, Is.EqualTo(ComponentRevisionManager.CompRevCountInRoot), "the head, the element made newest");
    }

    /// <summary>
    /// A spawn committed and durable in the log, then a crash with the archetype's map, its cluster or both on disk as of after the spawn. With the
    /// cluster, the crash path's map — rebuilt from the clusters — had the entity: the replay took the spawn as applied, and the entity recovered with
    /// its components zeroed. The map alone is harmless (it is rebuilt), and stays as the control.
    /// </summary>
    [Test]
    [VerifiesRule("AP-12")]
    [CancelAfter(30_000)]
    public void ASpawnWhosePagesReachedTheDiskInPart_Recovers([Values] Written written)
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Register(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction())
            {
                tx.Spawn<MkTraderArch>(MkTraderArch.Trader.Set(new MkTrader { TraderNo = 1 }), MkTraderArch.Wallet.Set(new MkWallet { Credits = 1 }));
                Assert.That(tx.Commit(), Is.True, "the first spawn");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    id = tx.Spawn<MkTraderArch>(MkTraderArch.Trader.Set(new MkTrader { TraderNo = 2 }),
                        MkTraderArch.Wallet.Set(new MkWallet { Credits = 777 }));
                    Assert.That(tx.Commit(), Is.True, "the spawn");
                }

                uow.Flush();
            }

            var es = dbe._archetypeStates[dbe.GetMetaByRouting(id.ArchetypeId).ArchetypeId];
            if ((written & Written.Map) != 0)
            {
                WriteSegmentPages(dbe, es.EntityMap.Segment);
            }

            if ((written & Written.Cluster) != 0)
            {
                Assert.That(es.ClusterState?.ClusterSegment, Is.Not.Null, "premise: a cluster archetype");
                WriteSegmentPages(dbe, es.ClusterState.ClusterSegment);
            }
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = Register(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        using var read = reopened.CreateQuickTransaction();
        Assert.That(read.Open(id).Read(MkTraderArch.Trader).TraderNo, Is.EqualTo(2), "the spawned trader");
        Assert.That(read.Open(id).Read(MkTraderArch.Wallet).Credits, Is.EqualTo(777), "the spawned wallet");
    }
}
