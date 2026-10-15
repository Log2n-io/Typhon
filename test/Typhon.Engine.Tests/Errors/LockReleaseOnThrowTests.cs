using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1278: a lock acquired where a throw can follow before the try that releases it leaks the lock; a lock acquired inside that try makes a failed
/// acquisition release a lock this thread never took.
/// </summary>
[TestFixture]
[NonParallelizable] // Mutates the static TimeoutOptions.Current, which every engine reads.
class LockReleaseOnThrowTests : TestBase<LockReleaseOnThrowTests>
{
    public enum BufferWrite
    {
        AddRef,
        Release,
        AddElement,
        AddElements,
        DeleteElement,
        DeleteBuffer,
    }

    private delegate T WithAccessor<out T>(ref ChunkAccessor<PersistentStore> accessor);

    private static T Run<T>(DatabaseEngine dbe, VariableSizedBufferSegment<int, PersistentStore> vsbs, WithAccessor<T> action)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var changeSet = dbe.MMF.CreateChangeSet();
        var accessor = vsbs.Segment.CreateChunkAccessor(changeSet);
        try
        {
            return action(ref accessor);
        }
        finally
        {
            accessor.Dispose();
            changeSet.ReleaseDirtyMarks();
        }
    }

    /// <summary>Sets <c>SegmentAllocationLockTimeout</c> for the test's duration; disposing restores every timeout.</summary>
    private static IDisposable ShortBufferLockTimeout()
    {
        var saved = TimeoutOptions.Current;
        TimeoutOptions.Current = new TimeoutOptions
        {
            DefaultLockTimeout = saved.DefaultLockTimeout,
            PageCacheLockTimeout = saved.PageCacheLockTimeout,
            BTreeLockTimeout = saved.BTreeLockTimeout,
            TransactionChainLockTimeout = saved.TransactionChainLockTimeout,
            RevisionChainLockTimeout = saved.RevisionChainLockTimeout,
            SegmentAllocationLockTimeout = TimeSpan.FromMilliseconds(50),
            DefaultCommitTimeout = saved.DefaultCommitTimeout,
            DefaultUowTimeout = saved.DefaultUowTimeout,
            PageCacheBackpressureTimeout = saved.PageCacheBackpressureTimeout,
            CommitPublishBackpressureTimeout = saved.CommitPublishBackpressureTimeout,
            WalBackPressureTimeout = saved.WalBackPressureTimeout,
        };
        return new Restore(saved);
    }

    private sealed class Restore(TimeoutOptions saved) : IDisposable
    {
        public void Dispose() => TimeoutOptions.Current = saved;
    }

    /// <summary>
    /// A buffer write that cannot get the buffer's lock fails on the lock's timeout, and leaves the lock to its holder. Acquired inside the try, the
    /// failed acquisition ran the finally's release on the other thread's hold: a corruption report replaced the timeout.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void ABufferWriteThatTimesOutOnTheLock_LeavesTheHoldersLock([Values] BufferWrite write)
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.InitializeArchetypes();
        var vsbs = dbe.GetComponentCollectionVSBS<int>();
        var buffer = Run(dbe, vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            var id = vsbs.AllocateBuffer(ref a);
            vsbs.AddElement(id, 1, ref a);
            return id;
        });
        var element = Run(dbe, vsbs, (ref ChunkAccessor<PersistentStore> a) => a.GetChunk<VariableSizedBufferRootHeader>(buffer).FirstStoredChunkId);

        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => Run(dbe, vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            a.GetChunk<VariableSizedBufferRootHeader>(buffer, true).EnterBufferLockForTest();
            holding.Set();
            release.Wait();
            a.GetChunk<VariableSizedBufferRootHeader>(buffer, true).ExitBufferLockForTest();
            return 0;
        }));
        Assert.That(holding.Wait(TimeSpan.FromSeconds(5)), Is.True, "premise: another thread holds the buffer's lock");
        try
        {
            using (ShortBufferLockTimeout())
            {
                Assert.That(() => Run(dbe, vsbs, (ref ChunkAccessor<PersistentStore> a) =>
                {
                    switch (write)
                    {
                        case BufferWrite.AddRef: vsbs.BufferAddRef(buffer, ref a); break;
                        case BufferWrite.Release: vsbs.BufferRelease(buffer, ref a); break;
                        case BufferWrite.AddElement: vsbs.AddElement(buffer, 2, ref a); break;
                        case BufferWrite.AddElements: vsbs.AddElements(buffer, new[] { 2, 3 }, ref a); break;
                        case BufferWrite.DeleteElement: vsbs.DeleteElement(buffer, element, 1, ref a); break;
                        case BufferWrite.DeleteBuffer: vsbs.DeleteBuffer(buffer, ref a); break;
                    }

                    return 0;
                }), Throws.TypeOf<LockTimeoutException>(), "the write fails on the lock's timeout");
            }

            Assert.That(vsbs.Segment.IsChunkAllocated(buffer), Is.True, "the failed write freed the buffer");
        }
        finally
        {
            release.Set();
            Assert.That(holder.Wait(TimeSpan.FromSeconds(5)), Is.True, "the holder released its lock");
        }
    }

    /// <summary>
    /// A commit with a conflict handler takes the chain's lock in PREPARE, then checks and relocates its entry before the try that releases the lock if
    /// PREPARE fails. A throw in that window left the chain locked for good: every later writer of the entity timed out.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    public void APrepareThatThrowsUnderTheRetainedLock_ReleasesIt()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<MkTrader>();
        dbe.RegisterComponentFromAccessor<MkWallet>();
        dbe.InitializeArchetypes();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<MkTraderArch>(MkTraderArch.Trader.Set(new MkTrader { TraderNo = 1 }), MkTraderArch.Wallet.Set(new MkWallet { Credits = 100 }));
            Assert.That(tx.Commit(), Is.True);
        }

        dbe.PrepareRetainedLockProbe = () => throw new InvalidOperationException("probe");
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            var e = tx.OpenMut(id);
            var wallet = e.Read(MkTraderArch.Wallet);
            wallet.Credits++;
            e.Set(MkTraderArch.Wallet, wallet);
            Assert.That(() => tx.Commit((ref ConcurrencyConflictSolver _) => { }), Throws.InvalidOperationException.With.Message.EqualTo("probe"),
                "premise: PREPARE throws under the lock, from the probe");
        }
        finally
        {
            dbe.PrepareRetainedLockProbe = null;
        }

        using var write = dbe.CreateQuickTransaction();
        var entity = write.OpenMut(id);
        var credits = entity.Read(MkTraderArch.Wallet);
        credits.Credits++;
        entity.Set(MkTraderArch.Wallet, credits);
        Assert.That(write.Commit(), Is.True, "a later write of the entity");
    }

    /// <summary>The root chunk of the entity's <see cref="MkWallet"/> revision chain, from its entity record.</summary>
    private static unsafe int WalletRootOf(DatabaseEngine dbe, EntityId entityId)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
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

    /// <summary>
    /// A buffer reader unlinking empty chunks fetches each next one with the buffer's lock promoted and the root's and predecessor's pages latched. A
    /// fetch that threw — a back-pressure timeout here — left both pages latched for good and the lock exclusive, so every later user of the buffer
    /// timed out (#1290 review).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public unsafe void AnUnlinkThatThrows_LeavesNoPageLatchedAndTheBufferFree()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.InitializeArchetypes();
        var vsbs = dbe.GetComponentCollectionVSBS<int>();
        var segment = vsbs.Segment;

        // Four full data chunks, a page each (fillers take the rest of each page), the middle two emptied.
        var chunks = new List<int>();
        var fillers = new List<int>();
        var buffer = Run(dbe, vsbs, (ref ChunkAccessor<PersistentStore> a) =>
        {
            var id = vsbs.AllocateBuffer(ref a);
            var value = 1;
            for (var c = 0; c < 4; c++)
            {
                for (var f = 0; f < segment.ChunkCountPerPage - 1; f++)
                {
                    fillers.Add(segment.AllocateChunk(false, a.ChangeSet));
                }

                for (var i = 0; i < vsbs.ElementCountPerChunk; i++)
                {
                    var chunk = vsbs.AddElement(id, value++, ref a);
                    if (chunk != id && !chunks.Contains(chunk))
                    {
                        chunks.Add(chunk);
                    }
                }
            }

            foreach (var emptied in new[] { chunks[1], chunks[2] })
            {
                var header = (VariableSizedBufferChunkHeader*)a.GetChunkAddress(emptied);
                var elements = (int*)((byte*)header + sizeof(VariableSizedBufferChunkHeader));
                var values = new int[header->ElementCount];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = elements[i];
                }

                foreach (var v in values)
                {
                    vsbs.DeleteElement(id, emptied, v, ref a);
                }
            }

            return id;
        });
        foreach (var filler in fillers)
        {
            segment.FreeChunk(filler);
        }

        Assert.That(chunks, Has.Count.EqualTo(4), "premise: four data chunks");
        Assert.That(PageOf(segment, chunks[2]), Is.Not.EqualTo(PageOf(segment, chunks[1])), "premise: the second empty chunk on a page of its own");
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");

        // The fillers' frees were held (CK-17, #1300): released here, not by the worker racing the eviction below, and their release on disk too.
        dbe.MMF.DrainReleases();
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the release's checkpoint");

        var saved = TimeoutOptions.Current;
        var timeouts = saved.Clone();
        timeouts.PageCacheBackpressureTimeout = TimeSpan.FromMilliseconds(50);
        TimeoutOptions.Current = timeouts;
        try
        {
            Exception thrown = null;
            int rootSlot, prevSlot;
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                // The root, the first chunk and the first empty one tagged under this scope; every other clean page evicted — the second empty one's too.
                var touch = segment.CreateChunkAccessor();
                rootSlot = dbe.MMF.MemPageIndexOf(touch.GetChunkAddress(buffer));
                prevSlot = dbe.MMF.MemPageIndexOf(touch.GetChunkAddress(chunks[0]));
                touch.GetChunkAddress(chunks[1]);
                touch.Dispose();
                dbe.MMF.EvictEvictablePagesForTest();

                var reader = vsbs.GetReadOnlyAccessor(buffer);
                try
                {
                    var walker = Environment.CurrentManagedThreadId;
                    dbe.MMF.ForceBackpressureForTest = () => Environment.CurrentManagedThreadId == walker;
                    try
                    {
                        reader.NextChunk();
                    }
                    catch (Exception e)
                    {
                        thrown = e;
                    }
                    finally
                    {
                        dbe.MMF.ForceBackpressureForTest = null;
                    }
                }
                finally
                {
                    reader.Dispose();
                }
            }

            Assert.That(thrown, Is.InstanceOf<PageCacheBackpressureTimeoutException>(), "premise: the unlink threw, fetching the second empty chunk");
            Assert.That(dbe.MMF.GetPageState(rootSlot), Is.Not.EqualTo(PagedMMF.PageState.Exclusive), "the root's page");
            Assert.That(dbe.MMF.GetPageState(prevSlot), Is.Not.EqualTo(PagedMMF.PageState.Exclusive), "the predecessor's page");
            var (owner, shared) = Run(dbe, vsbs, (ref ChunkAccessor<PersistentStore> a) =>
            {
                ref var rh = ref a.GetChunk<VariableSizedBufferRootHeader>(buffer);
                return (rh.Lock.LockedByThreadId, rh.Lock.SharedUsedCounter);
            });
            Assert.That(owner, Is.Zero, "the buffer's lock, exclusively held by nobody");
            Assert.That(shared, Is.Zero, "the buffer's shared holds");
        }
        finally
        {
            TimeoutOptions.Current = saved;
        }
    }

    private static int PageOf(ChunkBasedSegment<PersistentStore> segment, int chunk) =>
        chunk < segment.ChunkCountRootPage ? 0 : 1 + (chunk - segment.ChunkCountRootPage) / segment.ChunkCountPerPage;

    private static void AddCredit(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateQuickTransaction();
        var e = tx.OpenMut(id);
        var wallet = e.Read(MkTraderArch.Wallet);
        wallet.Credits++;
        e.Set(MkTraderArch.Wallet, wallet);
        Assert.That(tx.Commit(), Is.True, "the update");
    }

    /// <summary>
    /// <see cref="ComponentRevisionManager.GetRevisionElement"/> walks a chain's overflow chunks under the chain's shared lock when its caller holds none —
    /// PREPARE's first read of its own entry does. A walk that threw, here a page-cache back-pressure timeout on an overflow chunk's page, left the shared
    /// hold behind (#1278): the next writer of the entity waited out its lock timeout.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AChainWalkThatThrows_ReleasesTheChainLock()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<MkTrader>();
        dbe.RegisterComponentFromAccessor<MkWallet>();
        dbe.InitializeArchetypes();
        var segment = dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<MkTraderArch>(MkTraderArch.Trader.Set(new MkTrader { TraderNo = 1 }), MkTraderArch.Wallet.Set(new MkWallet { Credits = 100 }));

            // The rest of the root's page, taken by other chains: the overflow chunk the updates add opens a later page.
            for (var i = 0; i < segment.ChunkCountPerPage; i++)
            {
                tx.Spawn<MkTraderArch>(MkTraderArch.Trader.Set(new MkTrader { TraderNo = 2 + i }), MkTraderArch.Wallet.Set(new MkWallet { Credits = 1 }));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        var root = WalletRootOf(dbe, id);
        using (var pin = dbe.CreateReadOnlyTransaction())
        {
            // A reader keeps every revision: the root's entries, then an overflow chunk's.
            for (var i = 0; i < ComponentRevisionManager.CompRevCountInRoot + 1; i++)
            {
                AddCredit(dbe, id);
            }

            // The reader's epoch moved on, as a long reader's does every 128 opens (EP-03): the pages its updates touched are evictable again, once clean.
            dbe.EpochManager.RefreshScope();
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            dbe.MMF.EvictEvictablePagesForTest();

            var saved = TimeoutOptions.Current;
            var timeouts = saved.Clone();
            timeouts.PageCacheBackpressureTimeout = TimeSpan.FromMilliseconds(50);
            timeouts.RevisionChainLockTimeout = TimeSpan.FromMilliseconds(200);
            TimeoutOptions.Current = timeouts;
            try
            {
                Exception thrown = null;
                int sharedHolds;
                using (EpochGuard.Enter(dbe.EpochManager))
                {
                    var accessor = segment.CreateChunkAccessor();
                    try
                    {
                        ref var header = ref accessor.GetChunk<CompRevStorageHeader>(root);
                        Assert.That(header.ChainLength, Is.GreaterThan(1), "premise: the chain has an overflow chunk");
                        Assert.That(PageOf(segment, header.NextChunkId), Is.Not.EqualTo(PageOf(segment, root)),
                            "premise: the overflow chunk is on another page than the root");

                        // The root in the window: only the walk's fetch of the overflow chunk allocates, and every allocation of this thread fails.
                        var walker = Environment.CurrentManagedThreadId;
                        dbe.MMF.ForceBackpressureForTest = () => Environment.CurrentManagedThreadId == walker;
                        try
                        {
                            ComponentRevisionManager.GetRevisionElement(ref accessor, root, (short)ComponentRevisionManager.CompRevCountInRoot);
                        }
                        catch (Exception e)
                        {
                            thrown = e;
                        }
                        finally
                        {
                            dbe.MMF.ForceBackpressureForTest = null;
                        }

                        sharedHolds = accessor.GetChunk<CompRevStorageHeader>(root).Control.SharedUsedCounter;
                    }
                    finally
                    {
                        accessor.Dispose();
                    }
                }

                Assert.That(thrown, Is.InstanceOf<PageCacheBackpressureTimeoutException>(), "premise: the walk threw, fetching the overflow chunk");
                Assert.That(sharedHolds, Is.Zero, "the chain's shared holds, after the walk threw");
            }
            finally
            {
                TimeoutOptions.Current = saved;
            }
        }

        AddCredit(dbe, id);
    }

    /// <summary>
    /// A <see cref="RevisionEnumerator"/> takes the chain's lock, then steps to the chain's first live item, which in a circular chain can sit in an overflow
    /// chunk: the step fetches a page and can throw, a back-pressure timeout, or an entity open's restart after which the transaction goes on (#1230). The
    /// constructor never returned, so the caller's <c>using</c> disposed nothing, and the shared hold stayed: the open's retry took another over it.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AnEnumeratorWhoseFirstStepThrows_ReleasesTheChainLock()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<MkTrader>();
        dbe.RegisterComponentFromAccessor<MkWallet>();
        dbe.InitializeArchetypes();
        var segment = dbe.GetComponentTable<MkWallet>().CompRevTableSegment;
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<MkTraderArch>(MkTraderArch.Trader.Set(new MkTrader { TraderNo = 1 }), MkTraderArch.Wallet.Set(new MkWallet { Credits = 100 }));
            for (var i = 0; i < segment.ChunkCountPerPage; i++)
            {
                tx.Spawn<MkTraderArch>(MkTraderArch.Trader.Set(new MkTrader { TraderNo = 2 + i }), MkTraderArch.Wallet.Set(new MkWallet { Credits = 1 }));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        var root = WalletRootOf(dbe, id);
        using (var pin = dbe.CreateReadOnlyTransaction())
        {
            for (var i = 0; i < ComponentRevisionManager.CompRevCountInRoot + 1; i++)
            {
                AddCredit(dbe, id);
            }

            // The chain's first live item in its overflow chunk, as once the root's revisions are trimmed: the enumerator's first step fetches that chunk.
            short firstItem;
            using (EpochGuard.Enter(dbe.EpochManager))
            {
                var writer = segment.CreateChunkAccessor();
                ref var header = ref writer.GetChunk<CompRevStorageHeader>(root, true);
                firstItem = header.FirstItemIndex;
                header.FirstItemIndex = (short)ComponentRevisionManager.CompRevCountInRoot;
                writer.Dispose();
            }

            dbe.EpochManager.RefreshScope();
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint");
            dbe.MMF.EvictEvictablePagesForTest();

            var saved = TimeoutOptions.Current;
            var timeouts = saved.Clone();
            timeouts.PageCacheBackpressureTimeout = TimeSpan.FromMilliseconds(50);
            timeouts.RevisionChainLockTimeout = TimeSpan.FromMilliseconds(200);
            TimeoutOptions.Current = timeouts;
            try
            {
                Exception thrown = null;
                int sharedHolds;
                using (EpochGuard.Enter(dbe.EpochManager))
                {
                    var accessor = segment.CreateChunkAccessor();
                    try
                    {
                        ref var header = ref accessor.GetChunk<CompRevStorageHeader>(root);
                        Assert.That(PageOf(segment, header.NextChunkId), Is.Not.EqualTo(PageOf(segment, root)),
                            "premise: the overflow chunk is on another page than the root");

                        var walker = Environment.CurrentManagedThreadId;
                        dbe.MMF.ForceBackpressureForTest = () => Environment.CurrentManagedThreadId == walker;
                        try
                        {
                            using var enumerator = new RevisionEnumerator(ref accessor, root, false, true,
                                WaitContext.FromTimeout(TimeoutOptions.Current.RevisionChainLockTimeout));
                        }
                        catch (Exception e)
                        {
                            thrown = e;
                        }
                        finally
                        {
                            dbe.MMF.ForceBackpressureForTest = null;
                        }

                        sharedHolds = accessor.GetChunk<CompRevStorageHeader>(root).Control.SharedUsedCounter;
                        accessor.GetChunk<CompRevStorageHeader>(root, true).FirstItemIndex = firstItem;
                    }
                    finally
                    {
                        accessor.Dispose();
                    }
                }

                Assert.That(thrown, Is.InstanceOf<PageCacheBackpressureTimeoutException>(), "premise: the first step threw, fetching the overflow chunk");
                Assert.That(sharedHolds, Is.Zero, "the chain's shared holds, after the constructor threw");
            }
            finally
            {
                TimeoutOptions.Current = saved;
            }
        }

        AddCredit(dbe, id);
    }
}
