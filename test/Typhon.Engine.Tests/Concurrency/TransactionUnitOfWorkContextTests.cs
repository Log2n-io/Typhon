using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests;

[TestFixture]
[NonParallelizable] // Mutates static TimeoutOptions.Current — would race with any parallel DatabaseEngine ctor.
class TransactionUnitOfWorkContextTests : TestBase<TransactionUnitOfWorkContextTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    // ========================================
    // Backward Compatibility
    // ========================================

    [Test]
    public void Commit_NoContext_UsesDefaultTimeout()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(42);
        t.Spawn<CompAArch>(CompAArch.A.Set(in a));

        var res = t.Commit();
        Assert.That(res, Is.True);
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Committed));
    }

    [Test]
    public void Commit_EmptyTransaction_NoContext_ReturnsTrue()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var t = dbe.CreateQuickTransaction();

        var res = t.Commit();
        Assert.That(res, Is.True);
    }

    [Test]
    public void Rollback_NoContext_Succeeds()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(42);
        t.Spawn<CompAArch>(CompAArch.A.Set(in a));

        var res = t.Rollback();
        Assert.That(res, Is.True);
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Rollbacked));
    }

    [Test]
    public void Rollback_EmptyTransaction_NoContext_ReturnsTrue()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var t = dbe.CreateQuickTransaction();

        var res = t.Rollback();
        Assert.That(res, Is.True);
    }

    // ========================================
    // Yield Point at Commit Entry
    // ========================================

    [Test]
    public void Commit_ExpiredDeadline_ThrowsTyphonTimeoutException()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(42);
        t.Spawn<CompAArch>(CompAArch.A.Set(in a));

        var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.Zero);
        Thread.Sleep(1); // Ensure deadline expires

        Assert.Throws<TyphonTimeoutException>(() => t.Commit(ref ctx));
        // Transaction was not committed — state should still be InProgress
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.InProgress));
    }

    [Test]
    public void Commit_CancelledToken_ThrowsOperationCanceledException()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(42);
        t.Spawn<CompAArch>(CompAArch.A.Set(in a));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ctx = new UnitOfWorkContext(Deadline.Infinite, cts.Token);

        Assert.Throws<OperationCanceledException>(() => t.Commit(ref ctx));
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.InProgress));
    }

    [Test]
    public void Commit_InfiniteDeadline_CommitsNormally()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(42);
        t.Spawn<CompAArch>(CompAArch.A.Set(in a));

        var ctx = UnitOfWorkContext.None;
        var res = t.Commit(ref ctx);

        Assert.That(res, Is.True);
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Committed));
    }

    [Test]
    public void Commit_ValidDeadline_CommitsNormally()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(42);
        t.Spawn<CompAArch>(CompAArch.A.Set(in a));

        var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(10));
        var res = t.Commit(ref ctx);

        Assert.That(res, Is.True);
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Committed));
    }

    // ========================================
    // Holdoff Protection
    // ========================================

    [Test]
    public void Commit_ExpiredDuringHoldoff_CommitsSuccessfully()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(42);
        t.Spawn<CompAArch>(CompAArch.A.Set(in a));

        // Use a very short timeout that will still be valid when Commit is called
        // but the holdoff inside Commit should protect the commit loop
        var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromMilliseconds(100));
        var res = t.Commit(ref ctx);

        // Commit should succeed regardless of deadline expiring during holdoff
        Assert.That(res, Is.True);
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Committed));
    }

    [Test]
    public void Rollback_ExpiredDeadline_StillCompletes()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(42);
        t.Spawn<CompAArch>(CompAArch.A.Set(in a));

        // Expired deadline — rollback must still complete (no yield point)
        var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.Zero);
        Thread.Sleep(1);

        var res = t.Rollback(ref ctx);

        Assert.That(res, Is.True);
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Rollbacked));
    }

    [Test]
    public void Rollback_CancelledToken_StillCompletes()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(42);
        t.Spawn<CompAArch>(CompAArch.A.Set(in a));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ctx = new UnitOfWorkContext(Deadline.Infinite, cts.Token);

        var res = t.Rollback(ref ctx);

        Assert.That(res, Is.True);
        Assert.That(t.State, Is.EqualTo(Transaction.TransactionState.Rollbacked));
    }

    [Test]
    public void Commit_HoldoffNesting_CounterCorrect()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(42);
        t.Spawn<CompAArch>(CompAArch.A.Set(in a));

        var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(10));

        // Before commit, no holdoff
        Assert.That(ctx.IsInHoldoff, Is.False);

        // After commit, holdoff should be exited (scope guard disposed)
        var res = t.Commit(ref ctx);
        Assert.That(res, Is.True);
        Assert.That(ctx.IsInHoldoff, Is.False);
    }

    // ========================================
    // CommitContext Carries Ctx
    // ========================================

    [Test]
    public void Commit_WithContext_ComponentsCommittedCorrectly()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(42);
            entityId = t.Spawn<CompAArch>(CompAArch.A.Set(in a));

            var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(10));
            t.Commit(ref ctx);
        }

        // Verify the committed data is readable
        {
            using var t2 = dbe.CreateQuickTransaction();
            var readA = t2.Open(entityId).Read(CompAArch.A);
            Assert.That(readA.A, Is.EqualTo(42));
        }
    }

    [Test]
    public void Rollback_WithContext_ComponentsRolledBackCorrectly()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(42);
            entityId = t.Spawn<CompAArch>(CompAArch.A.Set(in a));

            var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(10));
            t.Rollback(ref ctx);
        }

        // Verify the entity does not exist after rollback
        {
            using var t2 = dbe.CreateQuickTransaction();
            Assert.That(t2.IsAlive(entityId), Is.False);
        }
    }

    // ========================================
    // DefaultCommitTimeout
    // ========================================

    [Test]
    public void DefaultCommitTimeout_DefaultIs30Seconds()
    {
        Assert.That(new TimeoutOptions().DefaultCommitTimeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public void DefaultCommitTimeout_CustomValue_Used()
    {
        var original = TimeoutOptions.Current.DefaultCommitTimeout;
        try
        {
            TimeoutOptions.Current.DefaultCommitTimeout = TimeSpan.FromSeconds(5);
            Assert.That(TimeoutOptions.Current.DefaultCommitTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));

            // The wrapper Commit() should still work with the custom timeout
            using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterComponents(dbe);
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(42);
            t.Spawn<CompAArch>(CompAArch.A.Set(in a));

            var res = t.Commit();
            Assert.That(res, Is.True);
        }
        finally
        {
            TimeoutOptions.Current.DefaultCommitTimeout = original;
        }
    }

    // ========================================
    // Deadline Composition
    // ========================================

    [Test]
    public void ComposeWaitContext_UowTighter_UsesUowDeadline()
    {
        // 500ms UoW vs 10s subsystem → UoW wins (tighter)
        var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromMilliseconds(500));
        var subsystemTimeout = TimeSpan.FromSeconds(10);

        var wc = WaitContext.FromDeadline(
            Deadline.Min(ctx.WaitContext.Deadline, Deadline.FromTimeout(subsystemTimeout)));

        // The composed deadline's remaining time should be close to 500ms, not 10s
        Assert.That(wc.Remaining.TotalMilliseconds, Is.LessThan(1000));
    }

    [Test]
    public void ComposeWaitContext_SubsystemTighter_UsesSubsystemTimeout()
    {
        // 30s UoW vs 1s subsystem → subsystem wins (tighter)
        var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(30));
        var subsystemTimeout = TimeSpan.FromSeconds(1);

        var wc = WaitContext.FromDeadline(
            Deadline.Min(ctx.WaitContext.Deadline, Deadline.FromTimeout(subsystemTimeout)));

        // The composed deadline's remaining time should be close to 1s, not 30s
        Assert.That(wc.Remaining.TotalMilliseconds, Is.LessThan(2000));
        Assert.That(wc.Remaining.TotalMilliseconds, Is.GreaterThan(0));
    }

    [Test]
    public void ComposeWaitContext_InfiniteUow_UsesSubsystemTimeout()
    {
        // Infinite UoW vs 5s subsystem → subsystem wins
        var ctx = UnitOfWorkContext.None;
        var subsystemTimeout = TimeSpan.FromSeconds(5);

        var wc = WaitContext.FromDeadline(
            Deadline.Min(ctx.WaitContext.Deadline, Deadline.FromTimeout(subsystemTimeout)));

        Assert.That(wc.Deadline.IsInfinite, Is.False);
        Assert.That(wc.Remaining.TotalSeconds, Is.LessThan(6));
    }

    // ========================================
    // State Transitions
    // ========================================

    [Test]
    public void Commit_AlreadyCommitted_ReturnsFalse()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(42);
        t.Spawn<CompAArch>(CompAArch.A.Set(in a));

        var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(10));
        var res1 = t.Commit(ref ctx);
        Assert.That(res1, Is.True);

        // Second commit should return false
        var ctx2 = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(10));
        var res2 = t.Commit(ref ctx2);
        Assert.That(res2, Is.False);
    }

    [Test]
    public void Rollback_AlreadyRolledBack_ReturnsFalse()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var t = dbe.CreateQuickTransaction();
        var a = new CompA(42);
        t.Spawn<CompAArch>(CompAArch.A.Set(in a));

        var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(10));
        var res1 = t.Rollback(ref ctx);
        Assert.That(res1, Is.True);

        // Second rollback should return false
        var ctx2 = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(10));
        var res2 = t.Rollback(ref ctx2);
        Assert.That(res2, Is.False);
    }
}
