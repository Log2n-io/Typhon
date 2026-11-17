using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1653: a transaction's lifetime and thread, checked in every build — strict mode OFF here, the configuration the Release NuGet ships
/// (<c>[Category("ChecksOffGated")]</c>; this suite's json turns strict mode on, and its per-operation affinity check would otherwise mask these).
/// </summary>
[NonParallelizable]
[Category("ChecksOffGated")]
class TransactionLifetimeTests : TestBase<TransactionLifetimeTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static Exception OnAnotherThread(Action act)
    {
        Exception caught = null;
        var t = new Thread(() =>
        {
            try
            {
                act();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        t.Start();
        t.Join();
        return caught;
    }

    [Test]
    [VerifiesRule("EP-04")]
    public void ACrossThreadDispose_Throws_BeforeTouchingAnyEpochState()
    {
        using var dbe = SetupEngine();
        var tx = dbe.CreateQuickTransaction();
        var depthHere = dbe.EpochManager.CurrentThreadDepth;
        var otherDepths = (before: -1, after: -1);

        var ex = OnAnotherThread(() =>
        {
            otherDepths.before = dbe.EpochManager.CurrentThreadDepth;
            try
            {
                tx.Dispose();
            }
            finally
            {
                otherDepths.after = dbe.EpochManager.CurrentThreadDepth;
            }
        });

        Assert.That(ex, Is.TypeOf<InvalidOperationException>());
        Assert.That(ex.Message, Does.Contain("EP-04").And.Contain("await"));
        Assert.That(otherDepths, Is.EqualTo((0, 0)), "the other thread's epoch untouched: no underflow, no stray exit");
        Assert.That(OnAnotherThread(() => tx.Commit()), Is.TypeOf<InvalidOperationException>(), "nor may it commit");
        Assert.That(OnAnotherThread(() => tx.Rollback()), Is.TypeOf<InvalidOperationException>(), "nor roll back");

        Assert.That(dbe.EpochManager.CurrentThreadDepth, Is.EqualTo(depthHere), "the creator still holds its scope");
        Assert.That(tx.Commit(), Is.True);
        tx.Dispose();
        Assert.That(dbe.EpochManager.CurrentThreadDepth, Is.EqualTo(depthHere - 1), "and releases it on its own thread");
    }

    [Test]
    [VerifiesRule("EP-04")]
    public async Task ADisposeAfterAnAwait_OnAThreadPoolContinuation_Throws()
    {
        using var dbe = SetupEngine();
        using var created = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var handed = new TaskCompletionSource<Transaction>(TaskCreationOptions.RunContinuationsAsynchronously);
        var creator = new Thread(() =>
        {
            var tx = dbe.CreateQuickTransaction();
            handed.SetResult(tx);
            release.Wait();
            tx.Dispose();   // the clean end, on its own thread
            created.Set();
        });
        creator.Start();

        var tx = await handed.Task;   // resumes on a thread-pool thread, as an ASP.NET continuation does
        Assert.That(Environment.CurrentManagedThreadId, Is.Not.EqualTo(creator.ManagedThreadId));
        var ex = Assert.Throws<InvalidOperationException>(() => tx.Dispose());
        Assert.That(ex.Message, Does.Contain("await"));

        release.Set();
        Assert.That(created.Wait(TimeSpan.FromSeconds(10)), Is.True);
        creator.Join();
    }

    [Test]
    [VerifiesRule("TX-01")]
    public void AUseAfterDispose_Throws_InsteadOfReachingTheNextTransaction()
    {
        using var dbe = SetupEngine();

        // Pooled (the Release default): refused until the pool hands the object out again.
        var tx = dbe.CreateQuickTransaction();
        tx.Dispose();
        Assert.Throws<ObjectDisposedException>(() => tx.Commit());
        Assert.Throws<ObjectDisposedException>(() => tx.Rollback());
        Assert.Throws<ObjectDisposedException>(() => tx.Spawn<CompAArch>(CompAArch.A.Set(new CompA(1))));
        Assert.Throws<ObjectDisposedException>(() => tx.Open(EntityId.Null));
        Assert.Throws<ObjectDisposedException>(() => tx.OpenMut(EntityId.Null));
        Assert.Throws<ObjectDisposedException>(() => tx.Destroy(EntityId.Null));
        Assert.DoesNotThrow(() => tx.Dispose(), "a second Dispose is a no-op");

        // Poisoned (a Debug build's default): never handed out again, so the stale reference keeps throwing and the next caller's transaction is untouched.
        dbe.TransactionChain.PoolDisposedTransactions = false;
        try
        {
            var stale = dbe.CreateQuickTransaction();
            stale.Dispose();
            using var next = dbe.CreateQuickTransaction();
            Assert.That(next, Is.Not.SameAs(stale));
            Assert.Throws<ObjectDisposedException>(() => stale.Spawn<CompAArch>(CompAArch.A.Set(new CompA(2))));
            Assert.That(next.State, Is.EqualTo(Transaction.TransactionState.Created), "the next transaction saw nothing");

        }
        finally
        {
            dbe.TransactionChain.PoolDisposedTransactions = true;
        }
    }

    [Test]
    [VerifiesRule("PS-05a")]
    public void ASiblingTransaction_OnAnotherThread_IsRefused_WhileTheFirstLives()
    {
        using var dbe = SetupEngine();
        using var uow = dbe.CreateUnitOfWork(DurabilityMode.Deferred);
        var first = uow.CreateTransaction();

        var ex = OnAnotherThread(() => uow.CreateTransaction().Dispose());
        Assert.That(ex, Is.TypeOf<InvalidOperationException>());
        Assert.That(ex.Message, Does.Contain("PS-05a"));

        using (var sameThread = uow.CreateTransaction())   // a second one on the owning thread shares the set legally
        {
            Assert.That(sameThread, Is.Not.Null);
        }

        first.Dispose();
        Assert.That(OnAnotherThread(() => uow.CreateTransaction().Dispose()), Is.Null, "a hand-off once the first is disposed is legal");
        Assert.That(OnAnotherThread(() => dbe.CreateQuickTransaction().Dispose()), Is.Null, "a transaction of its own unit of work is never refused");
    }
}
