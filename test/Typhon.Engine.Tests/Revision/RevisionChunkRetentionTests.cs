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
    /// #1268: a close that finds a transaction still alive skips its cleanup drain, and the revision chunks a trim had unlinked and queued for freeing
    /// were lost: still allocated, in no chain, so no later cleanup could find them, and a clean open does not sweep. The close now writes no
    /// clean-shutdown marker, and the next open's orphan sweep gives them back.
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
    }
}
