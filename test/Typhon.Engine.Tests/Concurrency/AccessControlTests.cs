using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for AccessControl with WaitContext API.
/// Focuses on WaitContext-specific behavior: NullRef pattern, timeout via Deadline,
/// cancellation via Token, and combined scenarios.
/// </summary>
[TestFixture]
public class AccessControlTests
{
    #region NullRef Pattern Tests (Infinite Wait)

    [Test]
    public void EnterExclusiveAccess_WithNullRef_InfiniteWait_Succeeds()
    {
        var control = new AccessControl();

        // Using WaitContext.Null should work for infinite wait
        var result = control.EnterExclusiveAccess(ref WaitContext.Null);

        Assert.That(result, Is.True);
        Assert.That(control.IsLockedByCurrentThread, Is.True);

        control.ExitExclusiveAccess();
    }

    [Test]
    public void EnterSharedAccess_WithNullRef_InfiniteWait_Succeeds()
    {
        var control = new AccessControl();

        var result = control.EnterSharedAccess(ref WaitContext.Null);

        Assert.That(result, Is.True);
        Assert.That(control.SharedUsedCounter, Is.EqualTo(1));

        control.ExitSharedAccess();
    }

    [Test]
    public void TryPromoteToExclusiveAccess_WithNullRef_InfiniteWait_Succeeds()
    {
        var control = new AccessControl();

        // First enter shared
        control.EnterSharedAccess(ref WaitContext.Null);

        // Then promote with NullRef (infinite wait)
        var result = control.TryPromoteToExclusiveAccess(ref WaitContext.Null);

        Assert.That(result, Is.True);
        Assert.That(control.IsLockedByCurrentThread, Is.True);

        control.DemoteFromExclusiveAccess();
        control.ExitSharedAccess();
    }

    #endregion

    #region Default WaitContext Tests (Immediate Failure)

    [Test]
    [CancelAfter(5000)]
    public void EnterExclusiveAccess_WithDefaultContext_FailsImmediately_WhenContended()
    {
        var control = new AccessControl();
        var barrier = new Barrier(2);
        var canRelease = new ManualResetEventSlim(false);

        // Thread 1 holds exclusive
        var t1 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            barrier.SignalAndWait();
            canRelease.Wait();
            control.ExitExclusiveAccess();
        });

        barrier.SignalAndWait();

        // default(WaitContext) has Deadline.Zero (already expired) - should fail immediately
        var ctx = default(WaitContext);
        var result = control.EnterExclusiveAccess(ref ctx);

        Assert.That(result, Is.False, "default(WaitContext) should fail immediately when lock is contended");

        canRelease.Set();
        t1.Wait();
    }

    [Test]
    public void EnterExclusiveAccess_WithDefaultContext_SucceedsWhenUncontended()
    {
        var control = new AccessControl();

        // Even default(WaitContext) should succeed if there's no contention
        // because the fast path doesn't check timeout before the first CAS attempt
        var ctx = default(WaitContext);
        var result = control.EnterExclusiveAccess(ref ctx);

        Assert.That(result, Is.True, "Should succeed on uncontended fast path");

        control.ExitExclusiveAccess();
    }

    #endregion

    #region Timeout via Deadline Tests

    [Test]
    [CancelAfter(5000)]
    public void EnterExclusiveAccess_WithTimeout_FailsAfterDeadline()
    {
        var control = new AccessControl();
        var barrier = new Barrier(2);
        var canRelease = new ManualResetEventSlim(false);

        // Thread 1 holds exclusive for longer than timeout
        var t1 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            barrier.SignalAndWait();
            canRelease.Wait();
            control.ExitExclusiveAccess();
        });

        barrier.SignalAndWait();

        // Create context with 50ms timeout
        var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(50));
        var result = control.EnterExclusiveAccess(ref ctx);

        Assert.That(result, Is.False, "Should timeout after deadline");

        canRelease.Set();
        t1.Wait();
    }

    [Test]
    [CancelAfter(5000)]
    public void EnterSharedAccess_WithTimeout_FailsAfterDeadline()
    {
        var control = new AccessControl();
        var barrier = new Barrier(2);
        var canRelease = new ManualResetEventSlim(false);

        // Thread 1 holds exclusive for longer than timeout
        var t1 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            barrier.SignalAndWait();
            canRelease.Wait();
            control.ExitExclusiveAccess();
        });

        barrier.SignalAndWait();

        var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(50));
        var result = control.EnterSharedAccess(ref ctx);

        Assert.That(result, Is.False, "Shared access should timeout when exclusive is held");

        canRelease.Set();
        t1.Wait();
    }

    [Test]
    [CancelAfter(5000)]
    public void EnterExclusiveAccess_WithSufficientTimeout_Succeeds()
    {
        var control = new AccessControl();
        var barrier = new Barrier(2);

        // Thread 1 holds exclusive briefly
        var t1 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            barrier.SignalAndWait();
            Thread.Sleep(30);  // Hold lock briefly
            control.ExitExclusiveAccess();
        });

        barrier.SignalAndWait();

        // Long timeout should succeed
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
        var result = control.EnterExclusiveAccess(ref ctx);

        Assert.That(result, Is.True, "Should succeed with sufficient timeout");

        control.ExitExclusiveAccess();
        t1.Wait();
    }

    #endregion

    #region Cancellation via Token Tests

    [Test]
    [CancelAfter(5000)]
    public void EnterExclusiveAccess_WithCancellation_FailsWhenCanceled()
    {
        var control = new AccessControl();
        var barrier = new Barrier(2);
        var canRelease = new ManualResetEventSlim(false);
        var cts = new CancellationTokenSource();

        // Thread 1 holds exclusive
        var t1 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            barrier.SignalAndWait();
            canRelease.Wait();
            control.ExitExclusiveAccess();
        });

        barrier.SignalAndWait();

        // Cancel after a short delay
        Task.Run(() =>
        {
            Thread.Sleep(50);
            cts.Cancel();
        });

        var ctx = WaitContext.FromToken(cts.Token);
        var result = control.EnterExclusiveAccess(ref ctx);

        Assert.That(result, Is.False, "Should fail when cancellation is requested");

        canRelease.Set();
        t1.Wait();
    }

    [Test]
    public void EnterExclusiveAccess_WithAlreadyCanceledToken_FailsImmediately()
    {
        var control = new AccessControl();

        // Pre-canceled token
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var ctx = WaitContext.FromToken(cts.Token);

        // Even uncontended, pre-canceled token should be checked
        // Note: This depends on implementation - if fast path doesn't check,
        // it may succeed on uncontended lock. Let's test contended case.
        control.EnterExclusiveAccess(ref TestWaitContext.Default);  // Hold lock

        var t = Task.Run(() =>
        {
            var result = control.EnterExclusiveAccess(ref ctx);
            return result;
        });

        var taskResult = t.Result;
        Assert.That(taskResult, Is.False, "Pre-canceled token should fail immediately");

        control.ExitExclusiveAccess();
    }

    #endregion

    #region Combined Timeout + Cancellation Tests

    [Test]
    [CancelAfter(5000)]
    public void EnterExclusiveAccess_WithTimeoutAndCancellation_TimeoutWins()
    {
        var control = new AccessControl();
        var barrier = new Barrier(2);
        var canRelease = new ManualResetEventSlim(false);
        var cts = new CancellationTokenSource();

        // Thread 1 holds exclusive until signaled
        var t1 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            barrier.SignalAndWait();
            canRelease.Wait();
            control.ExitExclusiveAccess();
        });

        barrier.SignalAndWait();

        // Timeout is short, cancellation would happen later (but won't be triggered)
        var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(50), cts.Token);
        var result = control.EnterExclusiveAccess(ref ctx);

        Assert.That(result, Is.False, "Should fail due to timeout");
        Assert.That(cts.IsCancellationRequested, Is.False, "Cancellation should not have been triggered");

        canRelease.Set();
        t1.Wait();
    }

    [Test]
    [CancelAfter(5000)]
    public void EnterExclusiveAccess_WithTimeoutAndCancellation_CancellationWins()
    {
        var control = new AccessControl();
        var barrier = new Barrier(2);
        var canRelease = new ManualResetEventSlim(false);
        var cts = new CancellationTokenSource();

        // Thread 1 holds exclusive until signaled
        var t1 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            barrier.SignalAndWait();
            canRelease.Wait();
            control.ExitExclusiveAccess();
        });

        barrier.SignalAndWait();

        // Cancel quickly, timeout is long
        Task.Run(() =>
        {
            Thread.Sleep(30);
            cts.Cancel();
        });

        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5), cts.Token);
        var result = control.EnterExclusiveAccess(ref ctx);

        Assert.That(result, Is.False, "Should fail due to cancellation");
        Assert.That(cts.IsCancellationRequested, Is.True, "Cancellation should have been triggered");

        canRelease.Set();
        t1.Wait();
    }

    #endregion

    #region Shared WaitContext (Deadline Not Accumulated) Tests

    [Test]
    [CancelAfter(5000)]
    public void NestedCalls_ShareWaitContext_DeadlineNotAccumulated()
    {
        var control1 = new AccessControl();
        var control2 = new AccessControl();

        // Create single WaitContext with 500ms timeout
        var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(500));

        // First lock
        var r1 = control1.EnterExclusiveAccess(ref ctx);
        Assert.That(r1, Is.True);

        // Simulate some work
        Thread.Sleep(100);

        // Second lock using SAME context - deadline is shared, not reset
        var r2 = control2.EnterExclusiveAccess(ref ctx);
        Assert.That(r2, Is.True);

        // The total time used is ~100ms, remaining should be ~400ms
        Assert.That(ctx.Remaining.TotalMilliseconds, Is.GreaterThan(300), "Remaining time should not restart");

        control2.ExitExclusiveAccess();
        control1.ExitExclusiveAccess();
    }

    #endregion

    #region Basic Functionality Tests

    [Test]
    public void MultipleSharedAccess_Works()
    {
        var control = new AccessControl();

        control.EnterSharedAccess(ref WaitContext.Null);
        control.EnterSharedAccess(ref WaitContext.Null);
        control.EnterSharedAccess(ref WaitContext.Null);

        Assert.That(control.SharedUsedCounter, Is.EqualTo(3));

        control.ExitSharedAccess();
        control.ExitSharedAccess();
        control.ExitSharedAccess();

        Assert.That(control.SharedUsedCounter, Is.EqualTo(0));
    }

    [Test]
    public void TryEnterExclusiveAccess_WhenIdle_Succeeds()
    {
        var control = new AccessControl();

        var result = control.TryEnterExclusiveAccess();

        Assert.That(result, Is.True);
        Assert.That(control.IsLockedByCurrentThread, Is.True);

        control.ExitExclusiveAccess();
    }

    [Test]
    public void TryEnterExclusiveAccess_WhenShared_Fails()
    {
        var control = new AccessControl();

        control.EnterSharedAccess(ref WaitContext.Null);

        var result = control.TryEnterExclusiveAccess();

        Assert.That(result, Is.False, "TryEnter should fail when shared access is held");

        control.ExitSharedAccess();
    }

    [Test]
    public void TryEnterExclusiveAccess_WhenExclusive_Fails()
    {
        var control = new AccessControl();

        control.EnterExclusiveAccess(ref TestWaitContext.Default);

        var t = Task.Run(() => control.TryEnterExclusiveAccess());
        var result = t.Result;

        Assert.That(result, Is.False, "TryEnter should fail when exclusive access is held by another thread");

        control.ExitExclusiveAccess();
    }

    [Test]
    public void PromoteAndDemote_Works()
    {
        var control = new AccessControl();

        // Enter shared
        control.EnterSharedAccess(ref WaitContext.Null);
        Assert.That(control.SharedUsedCounter, Is.EqualTo(1));

        // Promote to exclusive
        var promoted = control.TryPromoteToExclusiveAccess(ref WaitContext.Null);
        Assert.That(promoted, Is.True);
        Assert.That(control.IsLockedByCurrentThread, Is.True);

        // Demote back to shared
        control.DemoteFromExclusiveAccess();
        Assert.That(control.IsLockedByCurrentThread, Is.False);

        // Exit shared
        control.ExitSharedAccess();
    }

    [Test]
    public void Reset_ClearsLockState()
    {
        var control = new AccessControl();

        control.EnterExclusiveAccess(ref WaitContext.Null);
        Assert.That(control.IsLockedByCurrentThread, Is.True);

        control.Reset();

        Assert.That(control.IsLockedByCurrentThread, Is.False);
        Assert.That(control.SharedUsedCounter, Is.EqualTo(0));
    }

    #endregion

    #region Contention Flag Tests

    [Test]
    public void WasContended_InitiallyFalse()
    {
        var control = new AccessControl();
        Assert.That(control.WasContended, Is.False);
    }

    [Test]
    public void WasContended_FalseAfterUncontendedAcquisition()
    {
        var control = new AccessControl();

        control.EnterExclusiveAccess(ref WaitContext.Null);
        control.ExitExclusiveAccess();

        Assert.That(control.WasContended, Is.False, "Uncontended acquisition should not set flag");
    }

    [Test]
    public void WasContended_FalseAfterUncontendedSharedAcquisition()
    {
        var control = new AccessControl();

        control.EnterSharedAccess(ref WaitContext.Null);
        control.ExitSharedAccess();

        Assert.That(control.WasContended, Is.False, "Uncontended shared acquisition should not set flag");
    }

    [Test]
    [CancelAfter(5000)]
    public void WasContended_TrueAfterExclusiveContention()
    {
        var control = new AccessControl();
        var barrier = new Barrier(2);
        var canRelease = new ManualResetEventSlim(false);

        // Thread 1 holds exclusive
        var t1 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            barrier.SignalAndWait();
            canRelease.Wait();
            control.ExitExclusiveAccess();
        });

        barrier.SignalAndWait();

        // Thread 2 tries to acquire - will contend
        var t2 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            control.ExitExclusiveAccess();
        });

        // Wait until contention is actually observed before releasing
        SpinWait.SpinUntil(() => control.WasContended, 2000);
        canRelease.Set();

        Task.WaitAll(t1, t2);

        Assert.That(control.WasContended, Is.True, "Exclusive contention should set flag");
    }

    [Test]
    [CancelAfter(5000)]
    public void WasContended_TrueAfterSharedBlockedByExclusive()
    {
        var control = new AccessControl();
        var barrier = new Barrier(2);
        var canRelease = new ManualResetEventSlim(false);

        // Thread 1 holds exclusive
        var t1 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            barrier.SignalAndWait();
            canRelease.Wait();
            control.ExitExclusiveAccess();
        });

        barrier.SignalAndWait();

        // Thread 2 tries shared access - will be blocked by exclusive
        var t2 = Task.Run(() =>
        {
            control.EnterSharedAccess(ref TestWaitContext.Default);
            control.ExitSharedAccess();
        });

        // Wait until contention is actually observed before releasing
        SpinWait.SpinUntil(() => control.WasContended, 2000);
        canRelease.Set();

        Task.WaitAll(t1, t2);

        Assert.That(control.WasContended, Is.True, "Shared blocked by exclusive should set flag");
    }

    [Test]
    [CancelAfter(5000)]
    public void WasContended_PersistsAfterRelease()
    {
        var control = new AccessControl();
        var barrier = new Barrier(2);
        var canRelease = new ManualResetEventSlim(false);

        // Create contention
        var t1 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            barrier.SignalAndWait();
            canRelease.Wait();
            control.ExitExclusiveAccess();
        });

        barrier.SignalAndWait();

        var t2 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            control.ExitExclusiveAccess();
        });

        // Wait until contention is actually observed before releasing
        SpinWait.SpinUntil(() => control.WasContended, 2000);
        canRelease.Set();

        Task.WaitAll(t1, t2);

        // Flag should persist after all locks released
        Assert.That(control.WasContended, Is.True, "Flag should persist after release");

        // More operations shouldn't change it
        control.EnterSharedAccess(ref WaitContext.Null);
        control.ExitSharedAccess();
        Assert.That(control.WasContended, Is.True, "Flag should persist through subsequent operations");
    }

    [Test]
    [CancelAfter(5000)]
    public void WasContended_ClearedByReset()
    {
        var control = new AccessControl();
        var barrier = new Barrier(2);
        var canRelease = new ManualResetEventSlim(false);

        // Create contention
        var t1 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            barrier.SignalAndWait();
            canRelease.Wait();
            control.ExitExclusiveAccess();
        });

        barrier.SignalAndWait();

        var t2 = Task.Run(() =>
        {
            control.EnterExclusiveAccess(ref TestWaitContext.Default);
            control.ExitExclusiveAccess();
        });

        // Wait until contention is actually observed before releasing
        SpinWait.SpinUntil(() => control.WasContended, 2000);
        canRelease.Set();

        Task.WaitAll(t1, t2);

        Assert.That(control.WasContended, Is.True);

        control.Reset();

        Assert.That(control.WasContended, Is.False, "Reset should clear contention flag");
    }

    #endregion

    #region WaitContext Property Tests

    [Test]
    public void WaitContext_Null_IsNullRef()
    {
        // Verify that WaitContext.Null is actually a null reference
        ref var nullCtx = ref WaitContext.Null;
        Assert.That(System.Runtime.CompilerServices.Unsafe.IsNullRef(ref nullCtx), Is.True);
    }

    [Test]
    public void WaitContext_FromTimeout_ShouldStopAfterExpiry()
    {
        var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(50));

        Assert.That(ctx.ShouldStop, Is.False, "Should not stop immediately after creation");

        Thread.Sleep(100);

        Assert.That(ctx.ShouldStop, Is.True, "Should stop after timeout expires");
    }

    [Test]
    public void WaitContext_FromToken_ShouldStopWhenCanceled()
    {
        var cts = new CancellationTokenSource();
        var ctx = WaitContext.FromToken(cts.Token);

        Assert.That(ctx.ShouldStop, Is.False, "Should not stop before cancellation");

        cts.Cancel();

        Assert.That(ctx.ShouldStop, Is.True, "Should stop after cancellation");
    }

    [Test]
    public void WaitContext_Default_IsAlreadyExpired()
    {
        var ctx = default(WaitContext);

        Assert.That(ctx.ShouldStop, Is.True, "default(WaitContext) should be already expired");
        Assert.That(ctx.Deadline.IsExpired, Is.True);
    }

    #endregion
}
