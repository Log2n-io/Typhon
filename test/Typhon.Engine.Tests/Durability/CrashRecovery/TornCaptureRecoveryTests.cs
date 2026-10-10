using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

    /// <summary>Writes the pages holding <paramref name="chunks"/> of <paramref name="segment"/> to the data file, and no other.</summary>
    private static unsafe void WriteChunkPages(DatabaseEngine dbe, ChunkBasedSegment<PersistentStore> segment, params int[] chunks)
    {
        var pages = new HashSet<int>();
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var a = segment.CreateChunkAccessor();
            try
            {
                foreach (var chunk in chunks)
                {
                    pages.Add(dbe.MMF.MemPageIndexOf(a.GetChunkAddress(chunk)));
                }
            }
            finally
            {
                a.Dispose();
            }
        }

        dbe.MMF.SavePages([.. pages]).GetAwaiter().GetResult();
    }

    /// <summary>Writes the file page <paramref name=filePageIndex/> to the data file, and no other.</summary>
    private static void WriteFilePage(DatabaseEngine dbe, int filePageIndex)
    {
        int memPageIndex;
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            dbe.MMF.RequestPageEpoch(filePageIndex, dbe.EpochManager.GlobalEpoch, out memPageIndex);
        }

        dbe.MMF.SavePages([memPageIndex]).GetAwaiter().GetResult();
    }

    private static int OverflowOf(DatabaseEngine dbe, int root)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var a = dbe.GetComponentTable<MkItem>().CompRevTableSegment.CreateChunkAccessor();
        try
        {
            return a.GetChunk<CompRevStorageHeader>(root).NextChunkId;
        }
        finally
        {
            a.Dispose();
        }
    }

    private static List<int> ChainChunks(DatabaseEngine dbe, int root)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var a = dbe.GetComponentTable<MkItem>().CompRevTableSegment.CreateChunkAccessor();
        try
        {
            var chunks = new List<int> { root };
            int length = a.GetChunk<CompRevStorageHeader>(root).ChainLength, next = a.GetChunk<CompRevStorageHeader>(root).NextChunkId;
            for (var i = 1; i < length && next != 0; i++)
            {
                chunks.Add(next);
                next = a.GetChunk<int>(next);
            }

            return chunks;
        }
        finally
        {
            a.Dispose();
        }
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
    /// The market's swapped records, at unit scale: an item's chain grows an overflow chunk, a checkpoint writes both; the chain is compacted, freeing the
    /// chunk, and another item's chain takes it; that chunk's page reaches the disk, the compacted root's does not. Recovered, the first item's root named
    /// the other item's elements, and the scrub kept the newest — the other item's value (#1251, step 2.5). With the free held until a checkpoint has
    /// written the compacted root, the chunk is not reused before the disk stops naming it. Without the hold
    /// (<see cref="DatabaseEngine.DisableFreeQuarantineForTest"/>) the second item's growth takes the chunk, and the first item recovers an old value,
    /// its logged update lost.
    /// </summary>
    [Test]
    [VerifiesRule("CK-17")]
    [CancelAfter(30_000)]
    public void AnOverflowChunkFreedByACompaction_IsNotReusedBeforeTheCompactionReachesTheDisk()
    {
        var (value, updates) = OverflowReuseScenario(held: true);
        Assert.That(updates, Is.EqualTo(OverflowReuseMaxUpdates), "the freed chunk, held: not reused before a checkpoint");
        Assert.That(value, Is.EqualTo(42), "the first item, its own value");
    }

    /// <summary>The scenario above without the hold: the second item's growth takes the freed chunk, and the first item recovers another value.</summary>
    [Test]
    [NonParallelizable]
    [RuleMutant("CK-17")]
    [CancelAfter(30_000)]
    public void Mutant_AFreedOverflowChunkReusedAtOnce_GivesTheOldChainAnotherValue()
    {
        DatabaseEngine.DisableFreeQuarantineForTest = true;
        try
        {
            var (value, updates) = OverflowReuseScenario(held: false);
            Assert.That(updates, Is.LessThan(OverflowReuseMaxUpdates), "without the hold, the second item's growth took the freed chunk");
            Assert.That(value, Is.Not.EqualTo(42), "and the first item recovered another value");
        }
        finally
        {
            DatabaseEngine.DisableFreeQuarantineForTest = false;
        }
    }

    private const int OverflowReuseMaxUpdates = 100;

    private (long FirstValue, int Updates) OverflowReuseScenario(bool held)
    {
        EntityId first, second;
        int lastSecond;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            using (var tx = dbe.CreateQuickTransaction())
            {
                first = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 1, OwnerNo = 1, LastTradeSeq = -1 }));
                second = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 2, OwnerNo = 2, LastTradeSeq = -2 }));

                // A page of other items' roots, so the first item's overflow chunk lands on another page than its root.
                for (var i = 0; i < revisions.ChunkCountRootPage + revisions.ChunkCountPerPage; i++)
                {
                    tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 100 + i, OwnerNo = 9, LastTradeSeq = -(100 + i) }));
                }

                Assert.That(tx.Commit(), Is.True, "the spawns");
            }

            // A reader keeps the first item's revisions: its chain grows an overflow chunk.
            var pin = dbe.CreateReadOnlyTransaction();
            for (var i = 1; i <= ComponentRevisionManager.CompRevCountInRoot; i++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.OpenMut(first).Set(MkItemArch.Item, new MkItem { ItemNo = 1, OwnerNo = 1, LastTradeSeq = i });
                Assert.That(tx.Commit(), Is.True, "an update of the first item");
            }

            var root = ItemRootOf(dbe, first);
            var overflow = OverflowOf(dbe, root);
            Assert.That(overflow, Is.Not.Zero, "premise: the first item's chain has an overflow chunk");
            Assert.That(revisions.GetChunkLocation(overflow).segmentIndex, Is.Not.EqualTo(revisions.GetChunkLocation(root).segmentIndex),
                "premise: the overflow chunk is on another page than the root");
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint: root and overflow on disk");

            // The reader leaves; the next update of the first item compacts its chain, freeing the overflow chunk.
            pin.Dispose();
            using (var tx = dbe.CreateQuickTransaction())
            {
                tx.OpenMut(first).Set(MkItemArch.Item, new MkItem { ItemNo = 1, OwnerNo = 1, LastTradeSeq = 42 });
                Assert.That(tx.Commit(), Is.True, "the update that compacts");
            }

            Assert.That(OverflowOf(dbe, root), Is.Zero, "premise: the first item's chain compacted to its root");

            // The compaction's frees wait for the readers of its time; none is left.
            dbe.DeferredCleanupManager.FlushChunkFrees(dbe.EpochManager);
            Assert.That(revisions.IsChunkAllocated(overflow), Is.EqualTo(held), "the freed overflow chunk: held, or free at once");

            // Another reader; the second item's chain grows until it takes the freed chunk — the allocator is next-fit, so it comes back to it
            // only once the chunks after it are taken.
            var pin2 = dbe.CreateReadOnlyTransaction();
            var secondRoot = ItemRootOf(dbe, second);
            var updates = 0;
            const int MaxUpdates = OverflowReuseMaxUpdates;
            while (updates < MaxUpdates && !ChainChunks(dbe, secondRoot).Contains(overflow))
            {
                updates++;
                using var tx = dbe.CreateQuickTransaction();
                tx.OpenMut(second).Set(MkItemArch.Item, new MkItem { ItemNo = 2, OwnerNo = 2, LastTradeSeq = 1000 + updates });
                Assert.That(tx.Commit(), Is.True, "an update of the second item");
            }



            // The chunk's page reaches the disk; the compacted root's does not.
            lastSecond = updates;
            WriteChunkPages(dbe, revisions, overflow);
            dbe.SimulateHardCrash();
            try
            {
                pin2.Dispose();
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
        if (held)
        {
            Assert.That(read.Open(second).Read(MkItemArch.Item).LastTradeSeq, Is.EqualTo(1000 + lastSecond), "the second item");
            AssertStorageHealthy(reopened);
        }

        return (read.Open(first).Read(MkItemArch.Item).LastTradeSeq, lastSecond);
    }

    /// <summary>
    /// The market's zeroed wallet, at unit scale: an item's chain grows twice under a reader; its root's page reaches the disk after the second growth, the
    /// first overflow chunk's as of before it — linking to nothing. The replay of the logged updates appended past the chain's last reachable chunk, into
    /// a chunk no walk reaches (or the reserved chunk 0), and the scrub kept a stale element: the item recovered an old value, or the replay's growth threw
    /// (#1297). Before the replay, a chain whose links end before its length is collapsed to its newest reachable committed revision.
    /// </summary>
    [Test]
    [VerifiesRule("AP-12")]
    [CancelAfter(30_000)]
    public void AChainWhoseLinkIsOlderThanItsRoot_KeepsTheReplayedUpdates()
    {
        EntityId id;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            using (var tx = dbe.CreateQuickTransaction())
            {
                id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 5, OwnerNo = 1, LastTradeSeq = -1 }));

                // A page of other items' roots, so the item's overflow chunks land on another page than its root.
                for (var i = 0; i < revisions.ChunkCountRootPage + revisions.ChunkCountPerPage; i++)
                {
                    tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 100 + i, OwnerNo = 9, LastTradeSeq = -(100 + i) }));
                }

                Assert.That(tx.Commit(), Is.True, "the spawns");
            }

            // A reader keeps every revision: the root's, then one overflow chunk.
            var pin = dbe.CreateReadOnlyTransaction();
            var seq = 0;
            for (var i = 0; i < ComponentRevisionManager.CompRevCountInRoot; i++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.OpenMut(id).Set(MkItemArch.Item, new MkItem { ItemNo = 5, OwnerNo = 1, LastTradeSeq = ++seq });
                Assert.That(tx.Commit(), Is.True, "an update");
            }

            var root = ItemRootOf(dbe, id);
            var first = OverflowOf(dbe, root);
            Assert.That(first, Is.Not.Zero, "premise: one overflow chunk");
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint: the chain of two on disk");

            // The chain grows again — logged, then only the root's page reaches the disk.
            using (var uow = dbe.CreateUnitOfWork())
            {
                for (var i = 0; i < ComponentRevisionManager.CompRevCountInNext + 1; i++)
                {
                    using var tx = uow.CreateTransaction(CommitDiscipline.Commit);
                    tx.OpenMut(id).Set(MkItemArch.Item, new MkItem { ItemNo = 5, OwnerNo = 1, LastTradeSeq = ++seq });
                    Assert.That(tx.Commit(), Is.True, "a logged update");
                }

                uow.Flush();
            }

            var chain = ChainChunks(dbe, root);
            Assert.That(chain, Has.Count.EqualTo(3), "premise: the chain grew a second overflow chunk");
            Assert.That(revisions.GetChunkLocation(first).segmentIndex, Is.Not.EqualTo(revisions.GetChunkLocation(root).segmentIndex),
                "premise: the first overflow chunk is on another page than the root");
            WriteChunkPages(dbe, revisions, root);
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
        Assert.That(reopened.ChainsCollapsedAtOpen, Is.EqualTo(1), "the torn chain, collapsed before the replay");
        Assert.That((reopened.MMF.QuarantinePeakBytes > 0, reopened.MMF.QuarantinedBytes), Is.EqualTo((true, 0L)),
            "recovery's frees — the collapsed chain's chunks the sweep gave back — held until the recovery's seal covered them (CK-17)");
        using (var read = reopened.CreateReadOnlyTransaction())
        {
            Assert.That(read.Open(id).Read(MkItemArch.Item).LastTradeSeq,
                Is.EqualTo(ComponentRevisionManager.CompRevCountInRoot + ComponentRevisionManager.CompRevCountInNext + 1), "the item, its last logged update");
        }

        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// A chain that grew after the last completed checkpoint, its root's page alone on the disk: the root links to a chunk the disk has as free — the
    /// allocation bit lives on the chunk's own page — whose bytes were never written. Walked as the chain's, the length matched and nothing was collapsed;
    /// the replay appended behind its zeroed elements — or, its page holding a previous owner's bytes, behind that owner's elements, naming its content. A link
    /// to a chunk the disk has as free is torn like one past the chain's end (#1297).
    /// </summary>
    [Test]
    [VerifiesRule("AP-12")]
    [CancelAfter(30_000)]
    public void AChainLinkingToAChunkTheDiskHasAsFree_IsCollapsed()
    {
        EntityId id;
        int seq;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            using (var tx = dbe.CreateQuickTransaction())
            {
                id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 5, OwnerNo = 1, LastTradeSeq = -1 }));
                for (var i = 0; i < revisions.ChunkCountRootPage + revisions.ChunkCountPerPage; i++)
                {
                    tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 100 + i, OwnerNo = 9, LastTradeSeq = -(100 + i) }));
                }

                Assert.That(tx.Commit(), Is.True, "the spawns");
            }

            // A reader keeps every revision: the root fills, no overflow yet.
            var pin = dbe.CreateReadOnlyTransaction();
            seq = 0;
            for (var i = 0; i < ComponentRevisionManager.CompRevCountInRoot - 1; i++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.OpenMut(id).Set(MkItemArch.Item, new MkItem { ItemNo = 5, OwnerNo = 1, LastTradeSeq = ++seq });
                Assert.That(tx.Commit(), Is.True, "an update");
            }

            var root = ItemRootOf(dbe, id);
            Assert.That(OverflowOf(dbe, root), Is.Zero, "premise: the root alone holds the chain");
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint: the full root on disk");

            using (var uow = dbe.CreateUnitOfWork())
            {
                for (var i = 0; i < 2; i++)
                {
                    using var tx = uow.CreateTransaction(CommitDiscipline.Commit);
                    tx.OpenMut(id).Set(MkItemArch.Item, new MkItem { ItemNo = 5, OwnerNo = 1, LastTradeSeq = ++seq });
                    Assert.That(tx.Commit(), Is.True, "a logged update");
                }

                uow.Flush();
            }

            var chain = ChainChunks(dbe, root);
            Assert.That(chain, Has.Count.EqualTo(2), "premise: the chain grew an overflow chunk after the checkpoint");
            Assert.That(revisions.GetChunkLocation(chain[1]).segmentIndex, Is.Not.EqualTo(revisions.GetChunkLocation(root).segmentIndex),
                "premise: the overflow chunk is on another page than the root");
            WriteChunkPages(dbe, revisions, root);
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
        Assert.That(reopened.ChainsCollapsedAtOpen, Is.EqualTo(1), "the chain linking to a free chunk, collapsed before the replay");
        using (var read = reopened.CreateReadOnlyTransaction())
        {
            Assert.That(read.Open(id).Read(MkItemArch.Item).LastTradeSeq, Is.EqualTo(seq), "the item, its last logged update");
        }

        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// A segment grow whose directory page reached the disk and whose old tail's page did not — the directory written first, as a checkpoint writes it
    /// (CK-16): the old tail links to nothing where the directory names the grow's pages. The load's integrity check refused the segment, at every open
    /// (#1296, the market under a page-write kill). A crash open completes the grow: the link patched, the grow's pages empty, the spawns on them replayed.
    /// </summary>
    [Test]
    [VerifiesRule("CS-08")]
    [CancelAfter(30_000)]
    public void ASegmentGrowWhoseOldTailMissedTheDisk_IsCompletedAtTheCrashOpen()
    {
        var items = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;

            // Up to a few chunks short of the segment's capacity, then a checkpoint: on disk, the old tail links to nothing.
            while (revisions.AllocatedChunkCount < revisions.ChunkCapacity - 40)
            {
                using var tx = dbe.CreateQuickTransaction();
                for (var i = 0; i < 20; i++)
                {
                    var no = items.Count;
                    items.Add(tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = no, OwnerNo = 1, LastTradeSeq = -1 - no })));
                }

                Assert.That(tx.Commit(), Is.True, "the spawns");
            }

            var length = revisions.Length;
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");

            // Logged spawns past the capacity: the segment grows.
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    for (var i = 0; i < 100; i++)
                    {
                        var no = items.Count;
                        items.Add(tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = no, OwnerNo = 2, LastTradeSeq = -1 - no })));
                    }

                    Assert.That(tx.Commit(), Is.True, "the logged spawns");
                }

                uow.Flush();
            }

            Assert.That(revisions.Length, Is.GreaterThan(length), "premise: the revision segment grew");

            // The directory page reaches the disk; the old tail's does not.
            WriteFilePage(dbe, revisions.RootPageIndex);
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        Assert.That(reopened.MMF.TornGrowsRepairedAtLoad, Is.GreaterThanOrEqualTo(1), "the torn grow, completed at load");
        Assert.That(reopened.MMF.PagesClaimedAtLoad, Is.GreaterThan(0), "the grow's pages, claimed before the open allocated");
        Assert.That(LogicalSegmentHeader.Size, Is.EqualTo(16), "the incarnation sits in free header bytes: the page geometry is unchanged");
        using (var read = reopened.CreateReadOnlyTransaction())
        {
            for (var no = 0; no < items.Count; no++)
            {
                var item = read.Open(items[no]).Read(MkItemArch.Item);
                Assert.That(item.ItemNo, Is.EqualTo(no), $"item {no}");
            }
        }

        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// The same torn grow, the old tail's page on the disk torn as well: its first sector — the header, the incarnation, the link to nothing — as of the
    /// checkpoint, the rest not. The completion took the suspect page for one the grow had not written and initialized it empty, clearing its suspicion:
    /// the items the checkpoint had written on it were gone, the open silent (review of #1296). A torn disagreeing page that is this segment's is not
    /// completed: the segment is refused, as before the completion existed.
    /// </summary>
    [Test]
    [VerifiesRule("CS-08")]
    [CancelAfter(30_000)]
    public void ATornGrowOverATornOldTail_FailsTheOpenLoudly_InsteadOfEmptyingTheTail()
    {
        int oldTail;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            var no = 0;
            while (revisions.AllocatedChunkCount < revisions.ChunkCapacity - 40)
            {
                using var tx = dbe.CreateQuickTransaction();
                for (var i = 0; i < 20; i++, no++)
                {
                    tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = no, OwnerNo = 1, LastTradeSeq = -1 - no }));
                }

                Assert.That(tx.Commit(), Is.True, "the spawns");
            }

            var length = revisions.Length;
            oldTail = revisions.Pages[length - 1];
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    for (var i = 0; i < 100; i++, no++)
                    {
                        tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = no, OwnerNo = 2, LastTradeSeq = -1 - no }));
                    }

                    Assert.That(tx.Commit(), Is.True, "the logged spawns");
                }

                uow.Flush();
            }

            Assert.That(revisions.Length, Is.GreaterThan(length), "premise: the revision segment grew");
            WriteFilePage(dbe, revisions.RootPageIndex);
            dbe.SimulateHardCrash();
        }

        var path = ServiceProvider.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value.BuildDatabasePathFileName();
        ChaosPageIO.DamagePageOnDisk(path, oldTail, PageDamageType.TornPage, PagedMMF.PageSize);

        using var scope2 = ServiceProvider.CreateScope();
        var thrown = Assert.Catch(() => RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>()),
            "the open over a torn page backing checkpointed chunks");
        var e = thrown;
        while (e is not InvalidOperationException && e?.InnerException != null)
        {
            e = e.InnerException;
        }

        Assert.That(e?.Message, Does.Contain("integrity check failed at load"), $"refused by the segment's load, not by {thrown.GetType().Name}: {thrown.Message}");
    }

    /// <summary>
    /// A grow's own page torn by the crash, its first sector — this segment's header — on the disk, the rest not, behind an old tail that missed the disk.
    /// The page holds only what came after the last completed checkpoint's barrier, which the replay brings back: initialized, not kept suspect — kept, the
    /// suspect resolution refused an open a single power cut had made (review of #1296).
    /// </summary>
    [Test]
    [VerifiesRule("CS-08")]
    [CancelAfter(30_000)]
    public void AGrowWhosePageIsTorn_IsCompleted_ThePageInitialized()
    {
        var items = new List<EntityId>();
        int length, grown, torn;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            length = GrowRevisionsAfterACheckpoint(dbe, items);
            grown = revisions.Length;
            torn = revisions.Pages[length];
            WriteFilePage(dbe, revisions.RootPageIndex);
            WriteFilePage(dbe, torn);
            dbe.SimulateHardCrash();
        }

        var path = ServiceProvider.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value.BuildDatabasePathFileName();
        ChaosPageIO.DamagePageOnDisk(path, torn, PageDamageType.TornPage, PagedMMF.PageSize);

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        var segment = reopened.GetComponentTable<MkItem>().CompRevTableSegment;
        Assert.That((reopened.MMF.TornGrowsRepairedAtLoad >= 1, segment.TornGrowPagesReinitialized), Is.EqualTo((true, grown - length)),
            "the torn grow completed, every page it added initialized — the torn one too");
        AssertItems(reopened, items);
        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// A grow whose last page alone missed the disk: every link agrees — the page before it links to it, and it, all zeros, to nothing — so the chain
    /// check saw no tear, and the page loaded as the disk held it, without the segment's header or incarnation (review of #1296). The crash path checks a
    /// directory's last page and initializes one the grow had not written.
    /// </summary>
    [Test]
    [VerifiesRule("CS-08")]
    [CancelAfter(30_000)]
    public void AGrowWhoseLastPageMissedTheDisk_InitializesIt()
    {
        var items = new List<EntityId>();
        int length, grown;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            length = GrowRevisionsAfterACheckpoint(dbe, items);
            grown = revisions.Length;

            // The directory, the old tail and every page the grow added but the last.
            WriteFilePage(dbe, revisions.RootPageIndex);
            for (var p = length - 1; p < grown - 1; p++)
            {
                WriteFilePage(dbe, revisions.Pages[p]);
            }

            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        var segment = reopened.GetComponentTable<MkItem>().CompRevTableSegment;
        Assert.That(segment.TornGrowPagesReinitialized, Is.EqualTo(1), "the last page, which the grow had not written, initialized");
        AssertItems(reopened, items);
        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// A completion that initializes the page holding chunk 0 — the null id, reserved at creation — reserves it again: initialized, its bit was cleared with
    /// the page's, and the replay could store a revision at "none" (review of #1296). Staged with the page zeroed on the disk, which the completion takes
    /// for one a grow had not written.
    /// </summary>
    [Test]
    [VerifiesRule("CS-08")]
    [CancelAfter(30_000)]
    public void ACompletionThatInitializesChunkZerosPage_ReservesItAgain()
    {
        int page;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; revisions.Length < 3; i++)
                {
                    tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = i, OwnerNo = 1, LastTradeSeq = -1 - i }));
                }

                Assert.That(tx.Commit(), Is.True, "the spawns");
            }

            page = revisions.GetChunkLocation(0).segmentIndex;
            Assert.That(page, Is.GreaterThan(0), "premise: chunk 0 is on a data page, not the root");
            page = revisions.Pages[page];
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            dbe.SimulateHardCrash();
        }

        var path = ServiceProvider.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value.BuildDatabasePathFileName();
        ChaosPageIO.DamagePageOnDisk(path, page, PageDamageType.ZeroPage, PagedMMF.PageSize);

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        var segment = reopened.GetComponentTable<MkItem>().CompRevTableSegment;
        Assert.That(segment.TornGrowRepaired, Is.True, "premise: the completion initialized the page");
        using var guard = EpochGuard.Enter(reopened.EpochManager);
        Assert.That(segment.IsChunkAllocated(0), Is.True, "chunk 0, reserved again");
    }

    // Item spawns up to a few chunks short of the revision segment's capacity, a checkpoint, then logged spawns past it: the segment grows after the
    // checkpoint's barrier. Returns the segment's length before the grow.
    private static int GrowRevisionsAfterACheckpoint(DatabaseEngine dbe, List<EntityId> items)
    {
        var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
        while (revisions.AllocatedChunkCount < revisions.ChunkCapacity - 40)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < 20; i++)
            {
                var no = items.Count;
                items.Add(tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = no, OwnerNo = 1, LastTradeSeq = -1 - no })));
            }

            Assert.That(tx.Commit(), Is.True, "the spawns");
        }

        var length = revisions.Length;
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
        using (var uow = dbe.CreateUnitOfWork())
        {
            using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
            {
                for (var i = 0; i < 100; i++)
                {
                    var no = items.Count;
                    items.Add(tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = no, OwnerNo = 2, LastTradeSeq = -1 - no })));
                }

                Assert.That(tx.Commit(), Is.True, "the logged spawns");
            }

            uow.Flush();
        }

        Assert.That(revisions.Length, Is.GreaterThan(length), "premise: the revision segment grew");
        return length;
    }

    private static void AssertItems(DatabaseEngine dbe, List<EntityId> items)
    {
        using var read = dbe.CreateReadOnlyTransaction();
        for (var no = 0; no < items.Count; no++)
        {
            Assert.That(read.Open(items[no]).Read(MkItemArch.Item).ItemNo, Is.EqualTo(no), $"item {no}");
        }
    }

    /// <summary>
    /// A link lost long before the crash — the data file's page of a segment's second-to-last page links to nothing, the pages after it written and
    /// checkpointed, the items on them in no log window. Completing the chain must keep those pages: their stamp and their links say they are this
    /// segment's. Initialized empty, the items on them were gone, with nothing to replay (#1296).
    /// </summary>
    [Test]
    [VerifiesRule("CS-08")]
    [CancelAfter(30_000)]
    public void AnOldLostLinkOverCheckpointedPages_KeepsThem()
    {
        var items = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;

            // Until the segment's last page holds item roots.
            bool LastPageHoldsRoots() => revisions.Length > 2
                && revisions.AllocatedChunkCount > revisions.ChunkCountRootPage + (revisions.Length - 2) * revisions.ChunkCountPerPage + 10;
            while (!LastPageHoldsRoots())
            {
                using var tx = dbe.CreateQuickTransaction();
                for (var i = 0; i < 20; i++)
                {
                    var no = items.Count;
                    items.Add(tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = no, OwnerNo = 1, LastTradeSeq = -1 - no })));
                }

                Assert.That(tx.Commit(), Is.True, "the spawns");
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint: every item on disk");

            // On disk, the second-to-last page's link lost.
            var damaged = revisions.Pages[^2];
            int memPageIndex;
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                dbe.MMF.RequestPageEpoch(damaged, dbe.EpochManager.GlobalEpoch, out memPageIndex);
                dbe.MMF.GetPage(memPageIndex).StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset).LogicalSegmentNextRawDataPBID = 0;
            }

            dbe.MMF.SavePages([memPageIndex]).GetAwaiter().GetResult();
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        Assert.That(reopened.MMF.TornGrowsRepairedAtLoad, Is.EqualTo(1), "the lost link, completed at load");
        using (var read = reopened.CreateReadOnlyTransaction())
        {
            for (var no = 0; no < items.Count; no++)
            {
                Assert.That(read.IsAlive(items[no]), Is.True, $"item {no}");
                Assert.That(read.Open(items[no]).Read(MkItemArch.Item).ItemNo, Is.EqualTo(no), $"item {no}");
            }
        }

        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// A chain scrubbed at a crash open that replayed nothing: the scrub frees its overflow chunk. Freed at once, the next transactions reused it while the
    /// disk's root as of before the scrub still named it — no seal ran, nothing replayed — and a second crash recovered the first item over the other
    /// item's elements (review of #1251). Held, the chunk waits for a checkpoint that writes the collapsed root.
    /// </summary>
    [Test]
    [VerifiesRule("CK-17")]
    [CancelAfter(30_000)]
    public void AChainScrubbedAtACrashOpen_KeepsItsFreedChunksUntilACheckpointWritesTheScrub()
    {
        EntityId first, second;
        int overflow;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            using (var tx = dbe.CreateQuickTransaction())
            {
                first = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 1, OwnerNo = 1, LastTradeSeq = -1 }));
                second = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 2, OwnerNo = 2, LastTradeSeq = -2 }));
                for (var i = 0; i < revisions.ChunkCountRootPage + revisions.ChunkCountPerPage; i++)
                {
                    tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 100 + i, OwnerNo = 9, LastTradeSeq = -(100 + i) }));
                }

                Assert.That(tx.Commit(), Is.True, "the spawns");
            }

            var pin = dbe.CreateReadOnlyTransaction();
            for (var i = 1; i <= ComponentRevisionManager.CompRevCountInRoot; i++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.OpenMut(first).Set(MkItemArch.Item, new MkItem { ItemNo = 1, OwnerNo = 1, LastTradeSeq = i });
                Assert.That(tx.Commit(), Is.True, "an update of the first item");
            }

            overflow = OverflowOf(dbe, ItemRootOf(dbe, first));
            Assert.That(overflow, Is.Not.Zero, "premise: the first item's chain has an overflow chunk");
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint: the chain on disk, no log window");
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

        int lastSecond;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            Assert.That(dbe.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
            Assert.That(OverflowOf(dbe, ItemRootOf(dbe, first)), Is.Zero, "premise: the scrub collapsed the first item's chain");

            // The second item's chain grows until it takes the chunk the scrub freed, if it can.
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            var pin = dbe.CreateReadOnlyTransaction();
            var secondRoot = ItemRootOf(dbe, second);
            lastSecond = 0;
            while (lastSecond < 100 && !ChainChunks(dbe, secondRoot).Contains(overflow))
            {
                lastSecond++;
                using var tx = dbe.CreateQuickTransaction();
                tx.OpenMut(second).Set(MkItemArch.Item, new MkItem { ItemNo = 2, OwnerNo = 2, LastTradeSeq = 1000 + lastSecond });
                Assert.That(tx.Commit(), Is.True, "an update of the second item");
            }

            // The chunk's page reaches the disk, and nothing else.
            WriteChunkPages(dbe, revisions, overflow);
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

        using var scope3 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope3.ServiceProvider.GetRequiredService<DatabaseEngine>());
        using (var read = reopened.CreateReadOnlyTransaction())
        {
            Assert.That(read.Open(first).Read(MkItemArch.Item).LastTradeSeq, Is.EqualTo(ComponentRevisionManager.CompRevCountInRoot),
                "the first item, its own last value");
        }

        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// A chain whose root, its page as of a previous life, links within its length into another live record's root, its item count spanning that chunk.
    /// The walk counted the root as the chain's overflow and read its header as elements — a committed tombstone with a TSN past every replayed one, which
    /// won the collapse and the scrub: the item was gone (review of #1251). A link into a live root is a tear, and the chain is collapsed before the replay.
    /// </summary>
    [Test]
    [VerifiesRule("AP-12")]
    [CancelAfter(30_000)]
    public unsafe void AChainLinkingIntoAnotherLiveRoot_IsCollapsed_AndBothItemsKeepTheirValues()
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
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    tx.OpenMut(first).Set(MkItemArch.Item, new MkItem { ItemNo = 1, OwnerNo = 7, LastTradeSeq = 43 });
                    Assert.That(tx.Commit(), Is.True, "the logged update");
                }

                uow.Flush();
            }

            // On disk, the first item's root as of a previous life: two chunks, its items past the root, its next the second item's root.
            int firstRoot = ItemRootOf(dbe, first), secondRoot = ItemRootOf(dbe, second);
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                var cs = dbe.MMF.CreateChangeSet();
                var a = revisions.CreateChunkAccessor(cs);
                ref var header = ref a.GetChunk<CompRevStorageHeader>(firstRoot, true);
                header.NextChunkId = secondRoot;
                header.ChainLength = 2;
                header.ItemCount = (short)(ComponentRevisionManager.CompRevCountInRoot + 1);
                a.Dispose();
                cs.ReleaseDirtyMarks();
            }

            WriteChunkPages(dbe, revisions, firstRoot);
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        Assert.That(reopened.ChainsCollapsedAtOpen, Is.EqualTo(1), "the chain linking into another root, collapsed");
        using (var read = reopened.CreateReadOnlyTransaction())
        {
            Assert.That(read.IsAlive(first), Is.True, "the first item");
            Assert.That(read.Open(first).Read(MkItemArch.Item).LastTradeSeq, Is.EqualTo(43), "the first item, its logged update");
            Assert.That(read.Open(second).Read(MkItemArch.Item).LastTradeSeq, Is.EqualTo(42), "the second item");
        }

        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// A segment grow whose first new page carries the bytes of another segment — a page that segment freed, initialized as its own, its link even the
    /// directory's next — and missed the disk, the old tail's link lost too. Its segment flag and link say "a segment page"; its incarnation says another
    /// segment's. Kept, its chunk bitmap was the other segment's. It is initialized empty, and every new page with it (#1296).
    /// </summary>
    [Test]
    [VerifiesRule("CS-08")]
    [CancelAfter(30_000)]
    public unsafe void AGrowOverAPageOfAnotherIncarnation_InitializesIt()
    {
        var items = new List<EntityId>();
        int grown;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            while (revisions.AllocatedChunkCount < revisions.ChunkCapacity - 40)
            {
                using var tx = dbe.CreateQuickTransaction();
                for (var i = 0; i < 20; i++)
                {
                    var no = items.Count;
                    items.Add(tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = no, OwnerNo = 1, LastTradeSeq = -1 - no })));
                }

                Assert.That(tx.Commit(), Is.True, "the spawns");
            }

            var length = revisions.Length;
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    for (var i = 0; i < 100; i++)
                    {
                        var no = items.Count;
                        items.Add(tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = no, OwnerNo = 2, LastTradeSeq = -1 - no })));
                    }

                    Assert.That(tx.Commit(), Is.True, "the logged spawns");
                }

                uow.Flush();
            }

            grown = revisions.Length - length;
            Assert.That(grown, Is.GreaterThan(0), "premise: the revision segment grew");

            // The first new page, as another segment's page: its incarnation changed in memory, then written with the directory.
            var firstNew = revisions.Pages[length];
            int memPageIndex;
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                dbe.MMF.RequestPageEpoch(firstNew, dbe.EpochManager.GlobalEpoch, out memPageIndex);
                var image = new Span<byte>(dbe.MMF.GetPage(memPageIndex).Address, PagedMMF.PageSize);
                SegmentGeometry.WriteIncarnation(image, SegmentGeometry.ReadIncarnation(image) + 1);
            }

            dbe.MMF.SavePages([memPageIndex]).GetAwaiter().GetResult();
            WriteFilePage(dbe, revisions.RootPageIndex);
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        var revisionsAfter = reopened.GetComponentTable<MkItem>().CompRevTableSegment;
        Assert.That(revisionsAfter.TornGrowPagesReinitialized, Is.EqualTo(grown), "every new page initialized empty, the other segment's with them");
        using (var read = reopened.CreateReadOnlyTransaction())
        {
            for (var no = 0; no < items.Count; no++)
            {
                Assert.That(read.Open(items[no]).Read(MkItemArch.Item).ItemNo, Is.EqualTo(no), $"item {no}");
            }
        }

        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// A segment grow whose old tail reached the disk with its new link and whose first new page did not: the tail links on, the page it links to holds
    /// no segment page at all, and links to nothing. The completion started at the old tail, patched a link into that uninitialized page and left it
    /// without its header (review of #1296). The page that does not link on is initialized when the grow had not written it.
    /// </summary>
    [Test]
    [VerifiesRule("CS-08")]
    [CancelAfter(30_000)]
    public void AGrowWhoseFirstNewPageMissedTheDisk_IsCompleted()
    {
        var items = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            var revisions = dbe.GetComponentTable<MkItem>().CompRevTableSegment;
            while (revisions.AllocatedChunkCount < revisions.ChunkCapacity - 40)
            {
                using var tx = dbe.CreateQuickTransaction();
                for (var i = 0; i < 20; i++)
                {
                    var no = items.Count;
                    items.Add(tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = no, OwnerNo = 1, LastTradeSeq = -1 - no })));
                }

                Assert.That(tx.Commit(), Is.True, "the spawns");
            }

            var length = revisions.Length;
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    for (var i = 0; i < 100; i++)
                    {
                        var no = items.Count;
                        items.Add(tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = no, OwnerNo = 2, LastTradeSeq = -1 - no })));
                    }

                    Assert.That(tx.Commit(), Is.True, "the logged spawns");
                }

                uow.Flush();
            }

            Assert.That(revisions.Length, Is.GreaterThan(length), "premise: the revision segment grew");

            // The directory and the old tail reach the disk, the new pages do not.
            WriteFilePage(dbe, revisions.RootPageIndex);
            WriteFilePage(dbe, revisions.Pages[length - 1]);
            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.MMF.TornGrowsRepairedAtLoad, Is.GreaterThanOrEqualTo(1), "the torn grow, completed at load");
        using (var read = reopened.CreateReadOnlyTransaction())
        {
            for (var no = 0; no < items.Count; no++)
            {
                Assert.That(read.Open(items[no]).Read(MkItemArch.Item).ItemNo, Is.EqualTo(no), $"item {no}");
            }
        }

        AssertStorageHealthy(reopened);
    }

    /// <summary>
    /// A free held at a clean close is freed in the data file: the close releases every held free before its final checkpoint. Held after it, the chunk
    /// stayed allocated for good — a clean open sweeps nothing (CK-17).
    /// </summary>
    [Test]
    [VerifiesRule("CK-17")]
    [CancelAfter(30_000)]
    public void AFreeHeldAtACleanClose_IsFreedInTheFile()
    {
        EntityId id;
        int overflow;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = RegisterMarket(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            using (var tx = dbe.CreateQuickTransaction())
            {
                id = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(new MkItem { ItemNo = 1, OwnerNo = 1, LastTradeSeq = -1 }));
                Assert.That(tx.Commit(), Is.True, "the spawn");
            }

            var pin = dbe.CreateReadOnlyTransaction();
            for (var i = 1; i <= ComponentRevisionManager.CompRevCountInRoot; i++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.OpenMut(id).Set(MkItemArch.Item, new MkItem { ItemNo = 1, OwnerNo = 1, LastTradeSeq = i });
                Assert.That(tx.Commit(), Is.True, "an update");
            }

            overflow = OverflowOf(dbe, ItemRootOf(dbe, id));
            Assert.That(overflow, Is.Not.Zero, "premise: an overflow chunk");
            pin.Dispose();
            using (var tx = dbe.CreateQuickTransaction())
            {
                tx.OpenMut(id).Set(MkItemArch.Item, new MkItem { ItemNo = 1, OwnerNo = 1, LastTradeSeq = 42 });
                Assert.That(tx.Commit(), Is.True, "the update that compacts");
            }

            dbe.DeferredCleanupManager.FlushChunkFrees(dbe.EpochManager);
            Assert.That(dbe.GetComponentTable<MkItem>().CompRevTableSegment.IsChunkHeld(overflow), Is.True, "premise: the freed chunk, held");
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = RegisterMarket(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.False, "premise: a clean open");
        using (EpochGuard.Enter(reopened.EpochManager))
        {
            Assert.That(reopened.GetComponentTable<MkItem>().CompRevTableSegment.IsChunkAllocated(overflow), Is.False, "the held chunk, freed in the file");
        }
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
