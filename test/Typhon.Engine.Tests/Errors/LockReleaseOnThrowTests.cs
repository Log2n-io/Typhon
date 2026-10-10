using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
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
}
