using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading.Tasks;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1253: a revision chain's lock word lives in its root chunk, in a data page. A page written while a reader held the lock carried the held lock to
/// disk; the release changed the page in memory only, so once the clean page was evicted and read back, the lock was held by no one, for good — and a
/// clean close does not rewrite a clean page, so the next process inherited it: every writer of that entity timed out on the chain's lock. A holder now
/// takes a capture ticket before it locks and, at its release, has the page rewritten if a capture overlapped the hold (PS-20,
/// <see cref="PagedMMF.CaptureTicket"/>); a crash, which leaves no time for the rewrite, is undone as the crash open loads the table.
/// </summary>
[TestFixture]
class ChainLockWordPersistenceTests : TestBase<ChainLockWordPersistenceTests>
{
    // The crash case reopens and must find the WAL it crashed with, or the reopen is not a crash recovery.
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    // No periodic checkpoint: the only captures are the ones a test stages, so the counts are exact. The log goes under the bundle, which the teardown
    // deletes, so no case replays another's.
    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
        o.Wal.WalDirectory = null;
    }

    private static DatabaseEngine Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<MkTrader>();
        dbe.RegisterComponentFromAccessor<MkWallet>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private DatabaseEngine SetupEngine() => Register(ServiceProvider.GetRequiredService<DatabaseEngine>());

    private static EntityId SpawnTrader(DatabaseEngine dbe, int no)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<MkTraderArch>(MkTraderArch.Trader.Set(new MkTrader { TraderNo = no }), MkTraderArch.Wallet.Set(new MkWallet { Credits = 100 }));
        Assert.That(tx.Commit(), Is.True);
        return id;
    }

    /// <summary>The root chunk of the entity's <see cref="MkWallet"/> revision chain, from its entity record.</summary>
    private static unsafe int WalletRootOf(DatabaseEngine dbe, EntityId entityId)
    {
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var ct = dbe.GetComponentTable<MkWallet>();
            var meta = dbe.GetMetaByRouting(entityId.ArchetypeId);
            var es = dbe._archetypeStates[meta.ArchetypeId];
            var slot = -1;
            for (var s = 0; s < meta.ComponentCount; s++)
            {
                if (ReferenceEquals(es.SlotToComponentTable[s], ct))
                {
                    slot = s;
                    break;
                }
            }

            var record = stackalloc byte[meta._entityRecordSize];
            var accessor = es.EntityMap.Segment.CreateChunkAccessor();
            var found = es.EntityMap.TryGet(entityId.EntityKey, record, ref accessor);
            accessor.Dispose();
            Assert.That(found, Is.True, "the entity's record");
            return meta.IsClusterEligible && es.ClusterState != null
                ? ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, es.ClusterState.Layout.SlotToVersionedIndex[slot])
                : EntityRecordAccessor.GetLocation(record, slot);
        }
        finally
        {
            dbe.EpochManager.ExitScope(depth);
        }
    }

    /// <summary>The chain's lock word: who holds it exclusively, and how many share it.</summary>
    private static (int Holder, int Shared) LockWord(DatabaseEngine dbe, int root)
    {
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var accessor = dbe.GetComponentTable<MkWallet>().CompRevTableSegment.CreateChunkAccessor();
            try
            {
                ref var header = ref accessor.GetChunk<CompRevStorageHeader>(root);
                return (header.Control.LockedByThreadId, header.Control.SharedUsedCounter);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            dbe.EpochManager.ExitScope(depth);
        }
    }

    /// <summary>The cache slot holding the chain's root chunk.</summary>
    private static unsafe int SlotOf(DatabaseEngine dbe, int root)
    {
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var accessor = dbe.GetComponentTable<MkWallet>().CompRevTableSegment.CreateChunkAccessor();
            try
            {
                return dbe.MMF.MemPageIndexOf(accessor.GetChunkAddress(root));
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            dbe.EpochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// Starts a capture of <paramref name="slot"/> that stays in flight until <see cref="EndStagedCapture"/>: every ticketed hold on the slot in between
    /// counts, and a hold that takes no ticket, or never ends, does not. Returns the count before.
    /// </summary>
    private static long BeginStagedCapture(DatabaseEngine dbe, int slot)
    {
        var before = dbe.MMF.LocksHeldThroughCapture;
        dbe.MMF.BeginCapture(slot);
        return before;
    }

    /// <summary>Ends the capture <see cref="BeginStagedCapture"/> started; returns the holds it overlapped.</summary>
    private static long EndStagedCapture(DatabaseEngine dbe, int slot, long before)
    {
        dbe.MMF.EndCapture(slot);
        return dbe.MMF.LocksHeldThroughCapture - before;
    }

    /// <summary>The page of <paramref name="chunk"/> in its segment.</summary>
    private static int PageOf(ChunkBasedSegment<PersistentStore> segment, int chunk) =>
        chunk < segment.ChunkCountRootPage ? 0 : 1 + (chunk - segment.ChunkCountRootPage) / segment.ChunkCountPerPage;

    /// <summary>
    /// A walk of <paramref name="root"/>'s chain, which holds its lock, around <paramref name="duringTheHold"/>: the engine's own lock site, ticket
    /// included. Returns how many holds the release found overlapped by a capture.
    /// </summary>
    private static long HoldDuring(DatabaseEngine dbe, int root, bool exclusive, Action duringTheHold)
    {
        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var accessor = dbe.GetComponentTable<MkWallet>().CompRevTableSegment.CreateChunkAccessor();
            try
            {
                var before = dbe.MMF.LocksHeldThroughCapture;
                var walk = new RevisionEnumerator(ref accessor, root, exclusive, true, WaitContext.FromTimeout(TimeSpan.FromSeconds(5)));
                try
                {
                    Assert.That(LockWord(dbe, root), Is.Not.EqualTo((0, 0)), "premise: the walk holds the chain's lock");
                    duringTheHold();
                }
                finally
                {
                    walk.Dispose();
                }

                return dbe.MMF.LocksHeldThroughCapture - before;
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            dbe.EpochManager.ExitScope(depth);
        }
    }

    /// <summary>A neighbour's spawn dirties the chain's page, and the checkpoint writes it: the capture the held lock is copied in.</summary>
    private static void DirtyThePageAndCheckpoint(DatabaseEngine dbe, int root, int neighbourNo)
    {
        var neighbour = SpawnTrader(dbe, neighbourNo);
        var segment = dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        Assert.That(PageOf(segment, WalletRootOf(dbe, neighbour)), Is.EqualTo(PageOf(segment, root)), "premise: both chains' roots are in one page");
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
    }

    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public void AChainLockHeldWhileItsPageIsWritten_IsFreeWhenThePageIsReadBack([Values] bool exclusive)
    {
        using var dbe = SetupEngine();
        var root = WalletRootOf(dbe, SpawnTrader(dbe, 1));

        var overlapped = HoldDuring(dbe, root, exclusive, () => DirtyThePageAndCheckpoint(dbe, root, 2));
        Assert.That(overlapped, Is.EqualTo(1), "the release found the capture that copied its hold");

        // The release left the page owed: the next checkpoint writes it with the lock free, and only then may it leave the cache.
        var slot = SlotOf(dbe, root);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
        Assert.That(dbe.MMF.EvictEvictablePagesForTest(), Is.GreaterThan(0), "premise: pages were evicted");
        Assert.That(dbe.MMF.GetPageState(slot), Is.EqualTo(PagedMMF.PageState.Free), "premise: the chain's page left the cache");
        Assert.That(LockWord(dbe, root), Is.EqualTo((0, 0)), "the chain's lock, read back from disk");
    }

    /// <summary>The page a hold was copied in is owed until it is rewritten: evicting it before would read the held lock back.</summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public void APageAHoldWasCopiedIn_StaysCachedUntilRewritten()
    {
        using var dbe = SetupEngine();
        var root = WalletRootOf(dbe, SpawnTrader(dbe, 1));

        Assert.That(HoldDuring(dbe, root, false, () => DirtyThePageAndCheckpoint(dbe, root, 2)), Is.EqualTo(1), "premise: the capture copied the hold");
        var slot = SlotOf(dbe, root);
        dbe.MMF.EvictEvictablePagesForTest();
        Assert.That(dbe.MMF.GetPageState(slot), Is.Not.EqualTo(PagedMMF.PageState.Free), "the page holding the only free copy of the lock was evicted");
        Assert.That(LockWord(dbe, root), Is.EqualTo((0, 0)));
    }

    /// <summary>
    /// A hold no capture overlapped costs no write: the page stays clean and leaves the cache. Rewriting every page a lock was taken in would turn every
    /// read of a multi-revision chain into a page write.
    /// </summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public void AHoldNoCaptureOverlapped_LeavesThePageClean()
    {
        using var dbe = SetupEngine();
        var root = WalletRootOf(dbe, SpawnTrader(dbe, 1));
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the page is written, so clean");

        Assert.That(HoldDuring(dbe, root, false, () => { }), Is.Zero, "a hold with no checkpoint at all");
        Assert.That(HoldDuring(dbe, root, true, () => Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True)),
            Is.Zero, "a hold across a checkpoint that did not write its page, clean");

        var slot = SlotOf(dbe, root);
        dbe.MMF.EvictEvictablePagesForTest();
        Assert.That(dbe.MMF.GetPageState(slot), Is.EqualTo(PagedMMF.PageState.Free), "the page was left owed by holds no capture overlapped");
    }

    /// <summary>
    /// The structural save writes pages from live memory, and the OS reads them until the writes complete: a hold that overlaps it has the page rewritten
    /// like one overlapping a checkpoint.
    /// </summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public void AHoldOverlappingAStructuralSave_HasThePageRewritten()
    {
        using var dbe = SetupEngine();
        var root = WalletRootOf(dbe, SpawnTrader(dbe, 1));
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "premise: the page is written, so clean");

        var overlapped = HoldDuring(dbe, root, false, () =>
        {
            var changeSet = dbe.MMF.CreateChangeSet();
            var accessor = dbe.GetComponentTable<MkWallet>().CompRevTableSegment.CreateChunkAccessor(changeSet);
            try
            {
                accessor.PreDirtyChunk(root);
            }
            finally
            {
                accessor.Dispose();
            }

            changeSet.SaveChanges();
        });
        Assert.That(overlapped, Is.EqualTo(1), "the release found the save that wrote its hold");

        var slot = SlotOf(dbe, root);
        dbe.MMF.EvictEvictablePagesForTest();
        Assert.That(dbe.MMF.GetPageState(slot), Is.Not.EqualTo(PagedMMF.PageState.Free), "the page the save wrote the hold in was evicted before rewrite");
        Assert.That(HoldDuring(dbe, root, false, () => { }), Is.Zero, "a hold after the save completed: the save's capture never ended");
    }

    /// <summary>
    /// A commit with a conflict handler holds the chain's lock from PREPARE to PUBLISH, across the WAL append: the ticket taken in PREPARE travels with
    /// the publish entry, and the release in PUBLISH ends it. A checkpoint cannot complete while a commit publishes, so the capture is staged on the fence.
    /// </summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public void ACommitsRetainedLock_EndsItsHoldAtThePublish()
    {
        using var dbe = SetupEngine();
        var id = SpawnTrader(dbe, 1);
        var root = WalletRootOf(dbe, id);

        // PUBLISH begins with the lock PREPARE retained still held: a capture of its page runs meanwhile. Asserted after the commit, as a publish may
        // not throw.
        var slot = SlotOf(dbe, root);
        var fired = false;
        var holderAtPublish = 0;
        dbe.PublishComponentProbe = (_, firstChunkId) =>
        {
            if (firstChunkId == root && !fired)
            {
                fired = true;
                holderAtPublish = LockWord(dbe, root).Holder;
                dbe.MMF.BeginCapture(slot);
                dbe.MMF.EndCapture(slot);
            }
        };
        var before = dbe.MMF.LocksHeldThroughCapture;
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            var e = tx.OpenMut(id);
            var wallet = e.Read(MkTraderArch.Wallet);
            wallet.Credits++;
            e.Set(MkTraderArch.Wallet, wallet);
            Assert.That(tx.Commit((ref ConcurrencyConflictSolver _) => { }), Is.True);
        }
        finally
        {
            dbe.PublishComponentProbe = null;
        }

        Assert.That(fired, Is.True, "premise: the commit published the chain");
        Assert.That(holderAtPublish, Is.Not.Zero, "premise: PUBLISH holds the lock PREPARE took");
        Assert.That(dbe.MMF.LocksHeldThroughCapture - before, Is.EqualTo(1), "the release found the capture that could have copied its hold");
        Assert.That(SlotOf(dbe, root), Is.EqualTo(slot), "premise: the page stayed in its slot");
    }

    /// <summary>A write appends its revision under the chain's lock (AddCompRev): that hold is ticketed.</summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public void AWritesRevisionAppend_EndsItsHold()
    {
        using var dbe = SetupEngine();
        var id = SpawnTrader(dbe, 1);
        var slot = SlotOf(dbe, WalletRootOf(dbe, id));

        using var tx = dbe.CreateQuickTransaction();
        var before = BeginStagedCapture(dbe, slot);
        var e = tx.OpenMut(id);
        var wallet = e.Read(MkTraderArch.Wallet);
        wallet.Credits++;
        e.Set(MkTraderArch.Wallet, wallet);
        Assert.That(EndStagedCapture(dbe, slot, before), Is.EqualTo(1), "the append's hold");
        Assert.That(tx.Commit(), Is.True);
    }

    /// <summary>
    /// A rollback voids its own pending entry under the chain's lock (VoidOwnPendingEntry), and its dispose — the tail's — runs the cleanup the spawn
    /// queued, which takes the lock too: both holds are ticketed.
    /// </summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public void ARollbacksVoid_EndsItsHold()
    {
        using var dbe = SetupEngine();
        var id = SpawnTrader(dbe, 1);
        var slot = SlotOf(dbe, WalletRootOf(dbe, id));

        var tx = dbe.CreateQuickTransaction();
        var e = tx.OpenMut(id);
        var wallet = e.Read(MkTraderArch.Wallet);
        wallet.Credits++;
        e.Set(MkTraderArch.Wallet, wallet);
        var before = BeginStagedCapture(dbe, slot);
        tx.Dispose();
        Assert.That(EndStagedCapture(dbe, slot, before), Is.EqualTo(2), "the void's hold and the cleanup's");
    }

    /// <summary>
    /// A destroy after a write in the same transaction turns the write into the tombstone under the chain's lock, as the commit prepares: that hold is
    /// ticketed. The staged capture spans the commit up to the publish.
    /// </summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public void ADestroyAfterAWrite_EndsItsTombstoneHold()
    {
        using var dbe = SetupEngine();
        var id = SpawnTrader(dbe, 1);
        var root = WalletRootOf(dbe, id);
        var slot = SlotOf(dbe, root);

        using var tx = dbe.CreateQuickTransaction();
        var e = tx.OpenMut(id);
        var wallet = e.Read(MkTraderArch.Wallet);
        wallet.Credits++;
        e.Set(MkTraderArch.Wallet, wallet);
        tx.Destroy(id);

        long overlapped = -1;
        var before = BeginStagedCapture(dbe, slot);
        dbe.PublishComponentProbe = (_, firstChunkId) =>
        {
            if (firstChunkId == root && overlapped < 0)
            {
                overlapped = EndStagedCapture(dbe, slot, before);
            }
        };
        try
        {
            Assert.That(tx.Commit(), Is.True);
        }
        finally
        {
            dbe.PublishComponentProbe = null;
            if (overlapped < 0)
            {
                EndStagedCapture(dbe, slot, before);
            }
        }

        Assert.That(overlapped, Is.EqualTo(1), "the tombstone's hold");
    }

    /// <summary>
    /// A last-writer-wins commit that finds another committed since its read resolves the conflict under the chain's lock: that hold is ticketed. The
    /// staged capture spans the resolution only, from just before its lock to the publish.
    /// </summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public void AConflictResolution_EndsItsHold()
    {
        using var dbe = SetupEngine();
        var id = SpawnTrader(dbe, 1);
        var root = WalletRootOf(dbe, id);
        var slot = SlotOf(dbe, root);

        // The loser writes first, so its entry precedes the winner's: last-writer-wins detects the conflict by index.
        using var first = dbe.CreateQuickTransaction();
        using var second = dbe.CreateQuickTransaction();
        foreach (var tx in new[] { second, first })
        {
            var e = tx.OpenMut(id);
            var wallet = e.Read(MkTraderArch.Wallet);
            wallet.Credits++;
            e.Set(MkTraderArch.Wallet, wallet);
        }

        Assert.That(first.Commit(), Is.True);
        long before = -1, overlapped = -1;
        dbe.ChainVoidProbe = (_, firstChunkId) =>
        {
            if (firstChunkId == root && before < 0)
            {
                before = BeginStagedCapture(dbe, slot);
            }
        };
        dbe.PublishComponentProbe = (_, firstChunkId) =>
        {
            if (firstChunkId == root && before >= 0 && overlapped < 0)
            {
                overlapped = EndStagedCapture(dbe, slot, before);
            }
        };
        try
        {
            Assert.That(second.Commit(), Is.True);
        }
        finally
        {
            dbe.ChainVoidProbe = null;
            dbe.PublishComponentProbe = null;
        }

        Assert.That(before, Is.Not.Negative, "premise: the commit resolved a conflict on the chain");
        Assert.That(overlapped, Is.EqualTo(1), "the resolution's hold");
    }

    /// <summary>
    /// A crash leaves no time for the rewrite: the hold copied by the checkpoint is what the data file keeps. The crash open frees every chain's lock as
    /// the table loads — before the HEAD rebuild walks the chain and the replay of an update after the capture adds a revision to it, both of which take
    /// the lock and would wait for a holder that died with the process. The hold is another thread's, so the reopen's thread cannot pass for its holder.
    /// </summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(30_000)]
    public void AHoldCopiedBeforeACrash_IsFreeAfterRecovery([Values] bool exclusive)
    {
        EntityId id;
        int root;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Register(scope.ServiceProvider.GetRequiredService<DatabaseEngine>());
            id = SpawnTrader(dbe, 1);
            root = WalletRootOf(dbe, id);
            var overlapped = Task.Run(() => HoldDuring(dbe, root, exclusive, () => DirtyThePageAndCheckpoint(dbe, root, 2))).GetAwaiter().GetResult();
            Assert.That(overlapped, Is.EqualTo(1), "premise: the capture copied the hold");

            // An update after the capture, durable in the log only: the recovery replays it into the chain.
            using (var uow = dbe.CreateUnitOfWork())
            {
                using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
                {
                    var e = tx.OpenMut(id);
                    var wallet = e.Read(MkTraderArch.Wallet);
                    wallet.Credits = 777;
                    e.Set(MkTraderArch.Wallet, wallet);
                    Assert.That(tx.Commit(), Is.True, "the update");
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = Register(scope2.ServiceProvider.GetRequiredService<DatabaseEngine>());
        Assert.That(reopened.CrashRecoveryAtOpen, Is.True, "premise: the reopen recovered from a crash");
        Assert.That(reopened.GetComponentTable<MkWallet>().ChainLocksResetAtOpen, Is.EqualTo(1), "premise: the data file held the chain's lock");
        Assert.That(WalletRootOf(reopened, id), Is.EqualTo(root), "premise: the chain's root kept its chunk");
        Assert.That(LockWord(reopened, root), Is.EqualTo((0, 0)), "the chain's lock after recovery");

        using var write = reopened.CreateQuickTransaction();
        var entity = write.OpenMut(id);
        var credits = entity.Read(MkTraderArch.Wallet);
        Assert.That(credits.Credits, Is.EqualTo(777), "the replayed update");
        credits.Credits++;
        entity.Set(MkTraderArch.Wallet, credits);
        Assert.That(write.Commit(), Is.True, "a write of the entity");
    }

    /// <summary>
    /// An element lookup past the root's entries walks to an overflow chunk under the chain's shared lock, when its caller holds none — the recovery
    /// replay's lookup does: that hold is ticketed. (GetRevisionLocation's own hold is never taken: its one caller, AddCompRev, holds the chain's lock.)
    /// </summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public void AnElementLookupPastTheRoot_EndsItsHold()
    {
        using var dbe = SetupEngine();
        var id = SpawnTrader(dbe, 1);
        var root = WalletRootOf(dbe, id);
        var slot = SlotOf(dbe, root);

        // A reader opened first keeps every later revision: the chain grows past the root's entries.
        using var pin = dbe.CreateReadOnlyTransaction();
        for (var k = 0; k < 4; k++)
        {
            using var tx = dbe.CreateQuickTransaction();
            var e = tx.OpenMut(id);
            var wallet = e.Read(MkTraderArch.Wallet);
            wallet.Credits++;
            e.Set(MkTraderArch.Wallet, wallet);
            Assert.That(tx.Commit(), Is.True);
        }

        var depth = dbe.EpochManager.EnterScope();
        try
        {
            var accessor = dbe.GetComponentTable<MkWallet>().CompRevTableSegment.CreateChunkAccessor();
            try
            {
                ref var header = ref accessor.GetChunk<CompRevStorageHeader>(root);
                var index = (short)(header.FirstItemIndex + header.ItemCount - 1);
                Assert.That(index, Is.GreaterThanOrEqualTo((short)ComponentRevisionManager.CompRevCountInRoot), "premise: the newest entry is past the root");

                var before = BeginStagedCapture(dbe, slot);
                _ = ComponentRevisionManager.GetRevisionElement(ref accessor, root, index);
                Assert.That(EndStagedCapture(dbe, slot, before), Is.EqualTo(1), "the lookup's hold");
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            dbe.EpochManager.ExitScope(depth);
        }
    }

    /// <summary>
    /// A conflict-handler commit that fails in PREPARE after another entity's PREPARE retained its chain's lock gives that lock back on its abort path
    /// (ReleaseRetainedPublishLocks), the failing entity's own in its PREPARE, and the terminal rollback voids both entries under their locks: every hold
    /// is ticketed.
    /// </summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public void AnAbortedCommitsRetainedLocks_EndTheirHolds()
    {
        using var dbe = SetupEngine();
        var a = SpawnTrader(dbe, 1);
        var b = SpawnTrader(dbe, 2);
        var slot = SlotOf(dbe, WalletRootOf(dbe, a));
        Assert.That(SlotOf(dbe, WalletRootOf(dbe, b)), Is.EqualTo(slot), "premise: both chains' roots are in one page");

        var prepared = 0;
        dbe.PrepareRetainedLockProbe = () =>
        {
            if (++prepared == 2)
            {
                throw new InvalidOperationException("probe");
            }
        };
        long overlapped;
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            foreach (var id in new[] { a, b })
            {
                var e = tx.OpenMut(id);
                var wallet = e.Read(MkTraderArch.Wallet);
                wallet.Credits++;
                e.Set(MkTraderArch.Wallet, wallet);
            }

            var before = BeginStagedCapture(dbe, slot);
            Assert.That(() => tx.Commit((ref ConcurrencyConflictSolver _) => { }), Throws.Exception, "premise: the second PREPARE throws");
            overlapped = EndStagedCapture(dbe, slot, before);
        }
        finally
        {
            dbe.PrepareRetainedLockProbe = null;
        }

        Assert.That(prepared, Is.EqualTo(2), "premise: both entities reached PREPARE");
        Assert.That(overlapped, Is.EqualTo(4), "the retained lock released on the abort path, the failing PREPARE's, and the terminal rollback's two voids");
        Assert.That(LockWord(dbe, WalletRootOf(dbe, a)), Is.EqualTo((0, 0)));
        Assert.That(LockWord(dbe, WalletRootOf(dbe, b)), Is.EqualTo((0, 0)));
    }

    /// <summary>
    /// A destroyed entity's chain is released, under its lock, by the cleanup that runs once no snapshot can read it — here the destroy's own commit:
    /// that hold is ticketed with the others of the commit.
    /// </summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public void ADestroyedChainsRelease_EndsItsHold()
    {
        using var dbe = SetupEngine();
        var id = SpawnTrader(dbe, 1);
        var slot = SlotOf(dbe, WalletRootOf(dbe, id));

        var released = dbe.DeferredCleanupManager.DestroyedChainsReleased;
        var before = BeginStagedCapture(dbe, slot);
        using (var destroy = dbe.CreateQuickTransaction())
        {
            destroy.Destroy(id);
            Assert.That(destroy.Commit(), Is.True);
        }

        var overlapped = EndStagedCapture(dbe, slot, before);
        Assert.That(dbe.DeferredCleanupManager.DestroyedChainsReleased - released, Is.GreaterThan(0), "premise: the commit released the chain");
        Assert.That(overlapped, Is.EqualTo(4), "the commit's holds on the chain, the release's among them");
    }

    /// <summary>
    /// The fence itself, case by case: a capture of the holder's slot that could have copied the hold — in flight at the ticket, or started since — has
    /// the page marked; one that ended before the ticket, or a capture of another slot, does not. A capture between the ticket and an acquire that had to
    /// wait marks too: a rewrite more than needed, never one missed.
    /// </summary>
    [Test]
    [VerifiesRule("PS-20")]
    [CancelAfter(20_000)]
    public unsafe void TheCaptureFence_MarksTheHoldsACaptureCouldHaveCopied()
    {
        using var dbe = SetupEngine();
        var root = WalletRootOf(dbe, SpawnTrader(dbe, 1));
        var mmf = dbe.MMF;

        var depth = dbe.EpochManager.EnterScope();
        var accessor = dbe.GetComponentTable<MkWallet>().CompRevTableSegment.CreateChunkAccessor();
        try
        {
            var lockAddress = accessor.GetChunkAddress(root);
            var slot = mmf.MemPageIndexOf(lockAddress);
            var otherSlot = slot == 0 ? 1 : slot - 1;

            long Hold(Action beforeTicket, Action duringHold, Action afterRelease)
            {
                var before = mmf.LocksHeldThroughCapture;
                beforeTicket();
                var ticket = mmf.TakeCaptureTicket(ref *lockAddress);
                duringHold();
                mmf.EndLockHold(in ticket);
                afterRelease();
                return mmf.LocksHeldThroughCapture - before;
            }

            void None() { }
            void Begin() => mmf.BeginCapture(slot);
            void End() => mmf.EndCapture(slot);
            void BeginEnd() { Begin(); End(); }

            Assert.Multiple(() =>
            {
                Assert.That(Hold(None, None, None), Is.Zero, "no capture");
                Assert.That(Hold(BeginEnd, None, None), Is.Zero, "a capture that ended before the ticket");
                Assert.That(Hold(Begin, End, None), Is.EqualTo(1), "a capture in flight at the ticket, ended during the hold");
                Assert.That(Hold(Begin, None, End), Is.EqualTo(1), "a capture in flight for the whole hold");
                Assert.That(Hold(None, BeginEnd, None), Is.EqualTo(1), "a capture begun and ended during the hold");
                Assert.That(Hold(None, Begin, End), Is.EqualTo(1), "a capture begun during the hold, in flight at the release");
                Assert.That(Hold(None, () => { mmf.BeginCapture(otherSlot); mmf.EndCapture(otherSlot); }, None), Is.Zero, "a capture of another slot");
            });
        }
        finally
        {
            accessor.Dispose();
            dbe.EpochManager.ExitScope(depth);
        }
    }
}
