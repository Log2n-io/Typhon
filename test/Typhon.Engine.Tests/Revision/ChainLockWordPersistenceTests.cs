using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1253: a revision chain's lock word lives in its root chunk, in a data page. A page written while a reader holds the lock carries the held lock to
/// disk; the release changes the page in memory only, so once the clean page is evicted and read back, the lock is held by no one, for good. A clean
/// close does not rewrite a clean page, so the next process inherits it: every writer of that entity then times out on the chain's lock.
/// </summary>
[TestFixture]
class ChainLockWordPersistenceTests : TestBase<ChainLockWordPersistenceTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<MkTrader>();
        dbe.RegisterComponentFromAccessor<MkWallet>();
        dbe.InitializeArchetypes();
        return dbe;
    }

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

    /// <summary>What the chain's lock word holds, read through a fresh accessor (a page evicted is read back from disk).</summary>
    private static (int Holder, int Shared) LockWord(DatabaseEngine dbe, int root)
    {
        var depth = dbe.EpochManager.EnterScope();
        var accessor = dbe.GetComponentTable<MkWallet>().CompRevTableSegment.CreateChunkAccessor();
        try
        {
            ref var header = ref accessor.GetChunk<CompRevStorageHeader>(root);
            return (header.Control.LockedByThreadId, header.Control.SharedUsedCounter);
        }
        finally
        {
            accessor.Dispose();
            dbe.EpochManager.ExitScope(depth);
        }
    }

    /// <summary>The page of <paramref name="chunk"/> in its segment.</summary>
    private static int PageOf(ChunkBasedSegment<PersistentStore> segment, int chunk) =>
        chunk < segment.ChunkCountRootPage ? 0 : 1 + (chunk - segment.ChunkCountRootPage) / segment.ChunkCountPerPage;

    [Test]
    [Category("Quarantine")]   // #1253: red until a lock word held while its page is written no longer reaches the disk
    [CancelAfter(20_000)]
    public void AChainLockHeldWhileItsPageIsWritten_IsFreeWhenThePageIsReadBack()
    {
        using var dbe = SetupEngine();
        var a = SpawnTrader(dbe, 1);
        var root = WalletRootOf(dbe, a);

        var depth = dbe.EpochManager.EnterScope();
        var accessor = dbe.GetComponentTable<MkWallet>().CompRevTableSegment.CreateChunkAccessor();
        try
        {
            // A reader inside a walk of the chain.
            ref var header = ref accessor.GetChunk<CompRevStorageHeader>(root);
            var wc = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
            Assert.That(header.Control.EnterSharedAccess(ref wc), Is.True);

            // Meanwhile a neighbour's spawn dirties the page, and the checkpoint writes it.
            var b = SpawnTrader(dbe, 2);
            var segment = dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
            Assert.That(PageOf(segment, WalletRootOf(dbe, b)), Is.EqualTo(PageOf(segment, root)), "premise: both chains' roots are in one page");
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");

            // The reader's walk ends.
            header = ref accessor.GetChunk<CompRevStorageHeader>(root);
            header.Control.ExitSharedAccess();
        }
        finally
        {
            accessor.Dispose();
            dbe.EpochManager.ExitScope(depth);
        }

        Assert.That(LockWord(dbe, root), Is.EqualTo((0, 0)), "premise: the lock is free in memory");
        Assert.That(dbe.MMF.EvictEvictablePagesForTest(), Is.GreaterThan(0), "premise: pages were evicted");
        Assert.That(LockWord(dbe, root), Is.EqualTo((0, 0)), "the chain's lock, read back from disk");
    }
}
