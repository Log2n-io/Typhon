using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

/// <summary>
/// Plan 06 step 1.7: the revision chunks a long reader makes the writers' chains grow by are given back — once the reader ends, and across a clean close.
/// Found by <c>MarketHardeningTests</c>: after a long-reader storm and a clean reopen, one component's chains held ~71 % more chunks than its entities,
/// and a storm without readers did not bring them back.
/// </summary>
[TestFixture]
class RevisionChunkRetentionTests : TestBase<RevisionChunkRetentionTests>
{
    private const int Traders = 500;
    private const int Transfers = Traders * 6;

    // A WAL on disk, as an engine always has: a close that writes no clean-shutdown marker makes the next open a crash open only when it finds one.
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Wal.WalDirectory = null;
    }

    private static void Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<MkTrader>();
        dbe.RegisterComponentFromAccessor<MkWallet>();
        dbe.InitializeArchetypes();
    }

    // The frees, held until a checkpoint (CK-17), counted as freed.
    private static int TraderChunks(DatabaseEngine dbe)
    {
        dbe.ReleaseHeldFreesForTest();
        return dbe.GetComponentTable<MkTrader>().CompRevTableSegment.AllocatedChunkCount;
    }

    private static int WalletChunks(DatabaseEngine dbe)
    {
        dbe.ReleaseHeldFreesForTest();
        return dbe.GetComponentTable<MkWallet>().CompRevTableSegment.AllocatedChunkCount;
    }

    private static EntityId[] SpawnTraders(DatabaseEngine dbe)
    {
        var ids = new EntityId[Traders];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < Traders; i++)
        {
            ids[i] = tx.Spawn<MkTraderArch>(MkTraderArch.Trader.Set(new MkTrader { TraderNo = i }), MkTraderArch.Wallet.Set(new MkWallet { Credits = 1000 }));
        }

        tx.Commit();
        return ids;
    }

    /// <summary>A market transfer's shape: one entity's wallet, another's trader, each read, changed and set back.</summary>
    private static void Transfer(DatabaseEngine dbe, EntityId[] ids, Random rng)
    {
        using var tx = dbe.CreateQuickTransaction();
        var payer = tx.OpenMut(ids[rng.Next(ids.Length)]);
        var wallet = payer.Read(MkTraderArch.Wallet);
        wallet.Credits--;
        payer.Set(MkTraderArch.Wallet, wallet);
        var owner = tx.OpenMut(ids[rng.Next(ids.Length)]);
        var trader = owner.Read(MkTraderArch.Trader);
        trader.ItemsOwned++;
        owner.Set(MkTraderArch.Trader, trader);
        Assert.That(tx.Commit(), Is.True);
    }

    /// <summary>Grows every chain past its root chunk under a reader, ends the reader, and lets one writer's cleanup trim the chains.</summary>
    private static void GrowUnderAReaderThenTrim(DatabaseEngine dbe, EntityId[] ids, Random rng, int traderBaseline, int walletBaseline)
    {
        using (dbe.CreateReadOnlyTransaction())
        {
            for (var k = 0; k < Transfers; k++)
            {
                Transfer(dbe, ids, rng);
            }

            Assert.That(TraderChunks(dbe), Is.GreaterThan(traderBaseline + Traders / 2), "the premise: the reader made the trader chains grow");
            Assert.That(WalletChunks(dbe), Is.GreaterThan(walletBaseline + Traders / 2), "the premise: the reader made the wallet chains grow");
        }

        // The reader's dispose does not run the revision cleanup; this writer's does, and queues the chunks the trim unlinked.
        Transfer(dbe, ids, rng);
        Assert.That(dbe.DeferredCleanupManager.ChunkFreeQueueSize, Is.GreaterThan(Traders / 2), "the premise: the unlinked chunks are queued");
    }

    /// <summary>
    /// Under a reader every revision is kept, and the chains grow past their root chunk. Once it ends, the next writer's cleanup trims them and the one
    /// after frees the chunks the trim unlinked: each chain is its root again.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void ChainsGrownUnderAReader_AreGivenBackOnceItEnds()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        Register(dbe);
        var ids = SpawnTraders(dbe);
        var traderBaseline = TraderChunks(dbe);
        var walletBaseline = WalletChunks(dbe);
        var rng = new Random(1);
        GrowUnderAReaderThenTrim(dbe, ids, rng, traderBaseline, walletBaseline);

        Transfer(dbe, ids, rng);
        Assert.That(TraderChunks(dbe), Is.LessThanOrEqualTo(traderBaseline + 2), "trader chains kept what the reader needed after it ended");
        Assert.That(WalletChunks(dbe), Is.LessThanOrEqualTo(walletBaseline + 2), "wallet chains kept what the reader needed after it ended");
    }

    /// <summary>The chunks still queued for freeing when the engine closes are freed by the close (DrainCleanupsAtClose): none is left after the reopen.</summary>
    [Test]
    [CancelAfter(10_000)]
    public void ChunksQueuedForFreeingAtACleanClose_AreFreedByIt()
    {
        int traderBaseline, walletBaseline;
        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>())
        {
            Register(dbe);
            var ids = SpawnTraders(dbe);
            traderBaseline = TraderChunks(dbe);
            walletBaseline = WalletChunks(dbe);
            GrowUnderAReaderThenTrim(dbe, ids, new Random(1), traderBaseline, walletBaseline);
        }

        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>())
        {
            Register(dbe);
            Assert.That(TraderChunks(dbe), Is.EqualTo(traderBaseline), "trader revision chunks after a clean reopen");
            Assert.That(WalletChunks(dbe), Is.EqualTo(walletBaseline), "wallet revision chunks after a clean reopen");
        }
    }

    /// <summary>
    /// #1268: a close that found a transaction still alive skipped its cleanup drain, and the revision chunks a trim had unlinked and queued for freeing
    /// were lost: still allocated, in no chain, so no later cleanup could find them, and a clean open does not sweep. The close now drains regardless — no
    /// transaction outlives the engine's disposal — and marks itself clean.
    /// </summary>
    [Test]
    [VerifiesRule("REAP-02")]
    [CancelAfter(10_000)]
    public void ChunksQueuedForFreeingAtACloseWithALiveTransaction_AreNotLost()
    {
        int traderBaseline, walletBaseline;
        EntityId[] ids;
        var rng = new Random(1);
        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>())
        {
            Register(dbe);
            ids = SpawnTraders(dbe);
            traderBaseline = TraderChunks(dbe);
            walletBaseline = WalletChunks(dbe);
            GrowUnderAReaderThenTrim(dbe, ids, rng, traderBaseline, walletBaseline);

            // A reader opened now keeps the queued chunks queued through the close.
#pragma warning disable TYPHON004 // the point: a transaction still alive when the engine closes
            _ = dbe.CreateReadOnlyTransaction();
#pragma warning restore TYPHON004
        }

        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>())
        {
            Register(dbe);
            Assert.That(dbe.LastOpenRanCrashRecovery, Is.False, "the close drained what was queued, and marked itself clean");
            for (var k = 0; k < Transfers; k++)
            {
                Transfer(dbe, ids, rng);
            }

            dbe.FlushDeferredCleanups();
            dbe.FlushDeferredCleanups();
            Assert.That(TraderChunks(dbe), Is.LessThanOrEqualTo(traderBaseline + 2), "trader revision chunks lost at the close");
            Assert.That(WalletChunks(dbe), Is.LessThanOrEqualTo(walletBaseline + 2), "wallet revision chunks lost at the close");
        }
    }

    /// <summary>
    /// #1230: the tail drains every transaction's queued cleanups, and as the tail its epoch is the oldest pinned — a back-pressure wait in the drain held
    /// every page any thread had touched since the tail began, and on a 64 MiB market all of them timed out together. The drain starts at a call
    /// boundary, so the tail's epoch is refreshed before it — and between its entries, since a drain of thousands outlives any one refresh.
    /// </summary>
    [Test]
    [VerifiesRule("EP-02")]
    [CancelAfter(10_000)]
    [NonParallelizable] // the refresh interval is process-wide
    public void TheTailsDrain_RunsUnderARefreshedEpoch_NotTheOneItBeganWith([Values] bool betweenEntries)
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        Register(dbe);
        var ids = SpawnTraders(dbe);
        var tail = dbe.CreateQuickTransaction();
        var pinnedAtBegin = dbe.EpochManager.CurrentThreadPinnedEpoch;

        // Other threads' transactions: their revisions wait for the tail, and their scopes move the global epoch past its pin.
        System.Threading.Tasks.Task.Run(() =>
        {
            var rng = new Random(1);
            for (var k = 0; k < Traders; k++)
            {
                Transfer(dbe, ids, rng);
            }
        }).Wait();
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.GreaterThan(0), "premise: cleanups wait for the tail");

        long pinnedInDrain = 0, pinnedAtLast = 0;
        var entries = 0;
        dbe.DeferredCleanupManager.EntityCleanupProbeForTest = _ =>
        {
            entries++;
            pinnedAtLast = dbe.EpochManager.CurrentThreadPinnedEpoch;
            if (pinnedInDrain == 0)
            {
                pinnedInDrain = pinnedAtLast;
            }
        };
        // Never between entries: only the refresh before the drain can move the pin. At every entry: the pin moves as the drain goes.
        var interval = EntityAccessor.ScanEpochRefreshInterval;
        EntityAccessor.ScanEpochRefreshInterval = betweenEntries ? TimeSpan.Zero : TimeSpan.MaxValue;
        try
        {
            tail.Dispose();
        }
        finally
        {
            EntityAccessor.ScanEpochRefreshInterval = interval;
            dbe.DeferredCleanupManager.EntityCleanupProbeForTest = null;
        }

        Assert.That(entries, Is.GreaterThan(2), "premise: the tail drained several entries");
        Assert.That(pinnedInDrain, Is.GreaterThan(pinnedAtBegin + Traders / 2), "the drain's pin: refreshed before the drain, not the tail's first");
        if (betweenEntries)
        {
            Assert.That(pinnedAtLast, Is.GreaterThan(pinnedInDrain), "the drain's pin moved between its entries");
        }
        else
        {
            Assert.That(pinnedAtLast, Is.EqualTo(pinnedInDrain), "premise: nothing refreshed between entries");
        }
    }

    /// <summary>
    /// #1230, the read-only tail: its dispose drains the ECS cleanup queue — the destroyed entities other threads queued behind it — under a pin refreshed
    /// first, then between entries.
    /// </summary>
    [Test]
    [VerifiesRule("EP-02")]
    [CancelAfter(10_000)]
    [NonParallelizable] // the refresh interval is process-wide
    public void AReadOnlyTailsEcsDrain_RunsUnderARefreshedEpoch([Values] bool betweenEntries)
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        Register(dbe);
        var ids = SpawnTraders(dbe);
        var tail = dbe.CreateReadOnlyTransaction();
        var pinnedAtBegin = dbe.EpochManager.CurrentThreadPinnedEpoch;
        System.Threading.Tasks.Task.Run(() =>
        {
            for (var i = 0; i < Traders / 2; i++)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.Destroy(ids[i]);
                tx.Commit();
            }
        }).Wait();
        Assert.That(dbe.EcsCleanupQueueSize, Is.GreaterThan(0), "premise: destroyed entities wait for the tail");

        long pinnedInDrain = 0, pinnedAtLast = 0;
        var entries = 0;
        dbe.EcsCleanupProbeForTest = _ =>
        {
            entries++;
            pinnedAtLast = dbe.EpochManager.CurrentThreadPinnedEpoch;
            if (pinnedInDrain == 0)
            {
                pinnedInDrain = pinnedAtLast;
            }
        };
        var interval = EntityAccessor.ScanEpochRefreshInterval;
        EntityAccessor.ScanEpochRefreshInterval = betweenEntries ? TimeSpan.Zero : TimeSpan.MaxValue;
        try
        {
            tail.Dispose();
        }
        finally
        {
            EntityAccessor.ScanEpochRefreshInterval = interval;
            dbe.EcsCleanupProbeForTest = null;
        }

        Assert.That(entries, Is.GreaterThan(2), "premise: the tail drained several entries");
        Assert.That(pinnedInDrain, Is.GreaterThan(pinnedAtBegin + Traders / 4), "the drain's pin: refreshed before the drain, not the tail's first");
        if (betweenEntries)
        {
            Assert.That(pinnedAtLast, Is.GreaterThan(pinnedInDrain), "the drain's pin moved between its entries");
        }
    }

    /// <summary>
    /// #1268: a cleanup batch that throws midway — a back-pressure timeout on a small cache — dropped the frees it had collected from the entities it
    /// compacted before: their chains named those chunks no more, and nothing else did. They are queued even so.
    /// </summary>
    [Test]
    [VerifiesRule("REAP-02")]
    [CancelAfter(10_000)]
    public void AFailedCleanupBatch_QueuesTheFreesItCollectedBefore()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        Register(dbe);
        var ids = SpawnTraders(dbe);
        var rng = new Random(1);
        using (dbe.CreateReadOnlyTransaction())
        {
            for (var k = 0; k < Transfers; k++)
            {
                Transfer(dbe, ids, rng);
            }
        }

        var queuedBefore = dbe.DeferredCleanupManager.ChunkFreeQueueSize;
        dbe.DeferredCleanupManager.EntityCleanupProbeForTest = i =>
        {
            if (i == Traders / 4)
            {
                throw new InvalidOperationException("staged: the cleanup batch fails midway");
            }
        };
        Assert.Throws<InvalidOperationException>(() => dbe.FlushDeferredCleanups(), "premise: the batch failed");
        dbe.DeferredCleanupManager.EntityCleanupProbeForTest = null;
        Assert.That(dbe.DeferredCleanupManager.ChunkFreeQueueSize - queuedBefore, Is.GreaterThan(Traders / 8),
            "the frees of the entities compacted before the throw, queued");
        Assert.That(dbe.DeferredCleanupManager.FreesLost, Is.True, "the entity that threw: its frees dropped, the sweep owed");
    }

    /// <summary>
    /// #1268, review: a close whose session lost chunk frees marks the sweep owed. Written without the clean flag, the next open recovered with torn
    /// segment loads tolerated — over a data file the close had flushed whole, with nothing in the WAL to refill a segment that failed to load (CS-04). The
    /// failed-open marker goes with it: the next open recovers, sweeps, and loads its segments strictly.
    /// </summary>
    [Test]
    [VerifiesRule("REAP-02")]
    [VerifiesRule("CS-04")]
    [CancelAfter(10_000)]
    public void ACloseAfterACleanupLostFrees_MarksTheSweepOwed_AndTheNextOpenSweepsStrictly()
    {
        int traderBaseline;
        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>())
        {
            Register(dbe);
            var ids = SpawnTraders(dbe);
            traderBaseline = TraderChunks(dbe);
            var rng = new Random(1);
            using (dbe.CreateReadOnlyTransaction())
            {
                for (var k = 0; k < Transfers; k++)
                {
                    Transfer(dbe, ids, rng);
                }
            }

            dbe.DeferredCleanupManager.EntityCleanupProbeForTest = i =>
            {
                if (i == Traders / 4)
                {
                    throw new InvalidOperationException("staged: the cleanup batch fails midway");
                }
            };
            Assert.Throws<InvalidOperationException>(() => dbe.FlushDeferredCleanups(), "premise: the batch failed");
            dbe.DeferredCleanupManager.EntityCleanupProbeForTest = null;
            Assert.That(dbe.DeferredCleanupManager.FreesLost, Is.True, "premise: frees lost");
        }

        using (var scope = ServiceProvider.CreateScope())
        using (var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>())
        {
            Register(dbe);
            Assert.That((dbe.LastOpenRanCrashRecovery, dbe.TolerateTornSegmentsAtOpen), Is.EqualTo((true, false)),
                "the open after the close: a recovery, its segment loads strict");
            Assert.That(TraderChunks(dbe), Is.LessThanOrEqualTo(traderBaseline + 2), "trader revision chunks, swept back");
        }
    }

    /// <summary>
    /// #1268, review: the destroyed-entity cleanup had the shape the revision cleanup was fixed for. One that threw midway dropped what the entities before it
    /// had released and the entities not reached yet. Now the former are queued, the entity that threw goes back in the queue with those after it, and the
    /// sweep is owed.
    /// </summary>
    [Test]
    [VerifiesRule("REAP-02")]
    [CancelAfter(10_000)]
    public void AFailedDestroyCleanup_QueuesWhatItReleased_AndRequeuesTheRest()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        Register(dbe);
        var ids = SpawnTraders(dbe);
        const int destroyed = 20;

        // A reader older than the destroys keeps them queued: the destroying commit's own cleanup cannot release them past its snapshot.
        using (dbe.CreateReadOnlyTransaction())
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < destroyed; i++)
            {
                tx.Destroy(ids[i]);
            }

            Assert.That(tx.Commit(), Is.True, "the destroys");
        }

        Assert.That(dbe.EcsCleanupQueueSize, Is.EqualTo(destroyed), "premise: the destroyed entities are queued");

        // Their chains' revision cleanups first: a chain one still names is put off, not released.
        var revisionCleanups = dbe.MMF.CreateChangeSet();
        dbe.DeferredCleanupManager.ProcessDeferredCleanups(long.MaxValue, dbe.TransactionChain.NextFreeId + 1, dbe, revisionCleanups);
        revisionCleanups.SaveChanges();
        var seen = 0;
        dbe.EcsCleanupProbeForTest = _ =>
        {
            if (++seen == 10)
            {
                throw new InvalidOperationException("staged: the destroy cleanup fails midway");
            }
        };
        var queuedBefore = dbe.DeferredCleanupManager.ChunkFreeQueueSize;
        var cs = dbe.MMF.CreateChangeSet();
        try
        {
            Assert.Throws<InvalidOperationException>(() => dbe.ProcessEcsCleanups(dbe.TransactionChain.NextFreeId + 1, cs), "premise: the cleanup failed");
        }
        finally
        {
            dbe.EcsCleanupProbeForTest = null;
            cs.ReleaseDirtyMarks();
        }

        Assert.That((dbe.EcsCleanupQueueSize, dbe.DeferredCleanupManager.ChunkFreeQueueSize > queuedBefore, dbe.DeferredCleanupManager.FreesLost),
            Is.EqualTo((destroyed - 9, true, true)), "the nine released queued, the one that threw and those after it requeued, the sweep owed");
        dbe.FlushDeferredCleanups();
        dbe.FlushDeferredCleanups();
        Assert.That(dbe.EcsCleanupQueueSize, Is.Zero, "the requeued entities, released by the next drain");
    }
}
