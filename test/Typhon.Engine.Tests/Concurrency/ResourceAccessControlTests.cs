using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable CS0219 // Variable is assigned but never used - intentional for race condition testing

namespace Typhon.Engine.Tests;

[TestFixture]
public class ResourceAccessControlTests
{
    // ========================================
    // Basic ACCESSING Tests
    // ========================================

    [Test]
    [CancelAfter(1000)]
    public void TryEnterAccessing_OnIdle_Succeeds()
    {
        var control = new ResourceAccessControl();

        var result = control.TryEnterAccessing();

        Assert.That(result, Is.True);
        Assert.That(control.AccessingCount, Is.EqualTo(1));
        Assert.That(control.ModifyHolderThreadId, Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(1000)]
    public void EnterAccessing_OnIdle_Succeeds()
    {
        var control = new ResourceAccessControl();

        var result = control.EnterAccessing(ref WaitContext.Null);

        Assert.That(result, Is.True);
        Assert.That(control.AccessingCount, Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(1000)]
    public void ExitAccessing_AfterEnter_ReturnsToIdle()
    {
        var control = new ResourceAccessControl();
        control.EnterAccessing(ref WaitContext.Null);

        control.ExitAccessing();

        Assert.That(control.AccessingCount, Is.EqualTo(0));
        Assert.That(control.ModifyHolderThreadId, Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(1000)]
    public void EnterAccessing_MultipleTimes_IncrementsCounter()
    {
        var control = new ResourceAccessControl();

        control.EnterAccessing(ref WaitContext.Null);
        control.EnterAccessing(ref WaitContext.Null);
        control.EnterAccessing(ref WaitContext.Null);

        Assert.That(control.AccessingCount, Is.EqualTo(3));

        control.ExitAccessing();
        Assert.That(control.AccessingCount, Is.EqualTo(2));

        control.ExitAccessing();
        Assert.That(control.AccessingCount, Is.EqualTo(1));

        control.ExitAccessing();
        Assert.That(control.AccessingCount, Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(1000)]
    public void ExitAccessing_WithoutEnter_ThrowsException()
    {
        var control = new ResourceAccessControl();

        Assert.Throws<InvalidOperationException>(() => control.ExitAccessing());
    }

    [Test]
    [CancelAfter(1000)]
    public void EnterAccessing_MultipleThreads_AllSucceedConcurrently()
    {
        var control = new ResourceAccessControl();
        var allInside = new ManualResetEventSlim(false);
        var canExit = new ManualResetEventSlim(false);
        var insideCount = 0;

        var tasks = new Task[5];
        for (int i = 0; i < 5; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                control.EnterAccessing(ref TestWaitContext.Default);
                var count = Interlocked.Increment(ref insideCount);
                if (count == 5)
                {
                    allInside.Set();
                }
                canExit.Wait();
                control.ExitAccessing();
            });
        }

        Assert.That(allInside.Wait(1000), Is.True, "All threads should acquire ACCESSING concurrently");
        Assert.That(control.AccessingCount, Is.EqualTo(5));

        canExit.Set();
        Task.WaitAll(tasks);

        Assert.That(control.AccessingCount, Is.EqualTo(0));
    }

    // ========================================
    // Basic MODIFY Tests
    // ========================================

    [Test]
    [CancelAfter(1000)]
    public void TryEnterModify_OnIdle_Succeeds()
    {
        var control = new ResourceAccessControl();

        var result = control.TryEnterModify();

        Assert.That(result, Is.True);
        Assert.That(control.IsModifyHeldByCurrentThread, Is.True);
        Assert.That(control.AccessingCount, Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(1000)]
    public void EnterModify_OnIdle_Succeeds()
    {
        var control = new ResourceAccessControl();

        var result = control.EnterModify(ref WaitContext.Null);

        Assert.That(result, Is.True);
        Assert.That(control.IsModifyHeldByCurrentThread, Is.True);
    }

    [Test]
    [CancelAfter(1000)]
    public void ExitModify_AfterEnter_ReturnsToIdle()
    {
        var control = new ResourceAccessControl();
        control.EnterModify(ref WaitContext.Null);

        control.ExitModify();

        Assert.That(control.ModifyHolderThreadId, Is.EqualTo(0));
        Assert.That(control.AccessingCount, Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(1000)]
    public void ExitModify_FromDifferentThread_ThrowsException()
    {
        var control = new ResourceAccessControl();
        control.EnterModify(ref TestWaitContext.Default);
        Exception caughtException = null;

        var task = Task.Run(() =>
        {
            try
            {
                control.ExitModify();
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }
        });
        task.Wait();

        Assert.That(caughtException, Is.Not.Null);
        Assert.That(caughtException, Is.TypeOf<InvalidOperationException>());

        // Clean up - exit from the owning thread
        control.ExitModify();
    }

    [Test]
    [CancelAfter(1000)]
    public void TryEnterModify_WhenAccessingHeld_ReturnsFalse()
    {
        var control = new ResourceAccessControl();
        control.EnterAccessing(ref WaitContext.Null);

        var result = control.TryEnterModify();

        Assert.That(result, Is.False);

        control.ExitAccessing();
    }

    [Test]
    [CancelAfter(1000)]
    public void TryEnterModify_WhenModifyHeld_ReturnsFalse()
    {
        var control = new ResourceAccessControl();
        control.EnterModify(ref TestWaitContext.Default);

        bool otherResult = false;
        var task = Task.Run(() =>
        {
            otherResult = control.TryEnterModify();
        });
        task.Wait();

        Assert.That(otherResult, Is.False);

        control.ExitModify();
    }

    // ========================================
    // ACCESSING + MODIFY Compatibility Tests (KEY DESIGN FEATURE)
    // ========================================

    [Test]
    [CancelAfter(2000)]
    [Description("CRITICAL: Verifies that MODIFY can be held while ACCESSING is active - the key design difference from RW locks")]
    public void Modify_CompatibleWithAccessing_CanHoldBoth()
    {
        var control = new ResourceAccessControl();
        var modifyHeld = new ManualResetEventSlim(false);
        var canExit = new ManualResetEventSlim(false);

        // Thread 1: Hold MODIFY
        var modifyTask = Task.Run(() =>
        {
            control.EnterModify(ref TestWaitContext.Default);
            modifyHeld.Set();
            canExit.Wait();
            control.ExitModify();
        });

        modifyHeld.Wait();

        // While MODIFY is held, ACCESSING should still work (once MODIFY allows it via MODIFY_PENDING clear)
        // Note: TryEnterAccessing should return false because EnterModify might set MODIFY_PENDING
        // But after MODIFY is acquired, new ACCESSING attempts should work if no MODIFY_PENDING

        // Actually, the design says MODIFY is compatible with ACCESSING - accessors aren't blocked.
        // But the current implementation sets MODIFY_PENDING when waiting. Once acquired, it clears MODIFY_PENDING.
        // So after MODIFY is acquired and MODIFY_PENDING is cleared, ACCESSING should work.

        Assert.That(control.IsModifyPending, Is.False, "MODIFY_PENDING should be cleared after MODIFY acquired");

        // Now ACCESSING should succeed
        var accessResult = control.TryEnterAccessing();
        Assert.That(accessResult, Is.True, "ACCESSING should succeed while MODIFY is held (no MODIFY_PENDING)");
        Assert.That(control.AccessingCount, Is.EqualTo(1));

        // Cleanup
        control.ExitAccessing();
        canExit.Set();
        modifyTask.Wait();
    }

    [Test]
    [CancelAfter(2000)]
    public void EnterModify_WhileAccessingHeld_WaitsForDrain()
    {
        var control = new ResourceAccessControl();
        var accessingHeld = new ManualResetEventSlim(false);
        var modifyAttempted = new ManualResetEventSlim(false);
        var canReleaseAccessing = new ManualResetEventSlim(false);
        var modifyAcquired = false;

        // Thread 1: Hold ACCESSING until signaled
        var accessTask = Task.Run(() =>
        {
            control.EnterAccessing(ref TestWaitContext.Default);
            accessingHeld.Set();
            modifyAttempted.Wait();
            canReleaseAccessing.Wait();
            control.ExitAccessing();
        });

        accessingHeld.Wait();

        // Thread 2: Try to acquire MODIFY - should wait for ACCESSING to drain
        var aboutToEnterModify = new ManualResetEventSlim(false);
        var modifyTask = Task.Run(() =>
        {
            modifyAttempted.Set();
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            aboutToEnterModify.Set();
            modifyAcquired = control.EnterModify(ref ctx);
            if (modifyAcquired)
            {
                control.ExitModify();
            }
        });

        aboutToEnterModify.Wait();
        // Wait until EnterModify has set MODIFY_PENDING (proves it's blocking on drain)
        SpinWait.SpinUntil(() => control.IsModifyPending, TimeSpan.FromSeconds(1));

        // Now release ACCESSING so MODIFY can drain and acquire
        canReleaseAccessing.Set();

        Task.WaitAll(accessTask, modifyTask);

        Assert.That(modifyAcquired, Is.True, "MODIFY should succeed after ACCESSING drains");
    }

    // ========================================
    // MODIFY_PENDING Fairness Tests
    // ========================================

    [Test]
    [CancelAfter(2000)]
    public void ModifyPending_BlocksNewAccessing()
    {
        var control = new ResourceAccessControl();
        var firstAccessHeld = new ManualResetEventSlim(false);
        var canRelease = new ManualResetEventSlim(false);
        var secondAccessResult = false;

        // Thread 1: Hold ACCESSING
        var firstAccessTask = Task.Run(() =>
        {
            control.EnterAccessing(ref TestWaitContext.Default);
            firstAccessHeld.Set();
            canRelease.Wait();
            control.ExitAccessing();
        });

        firstAccessHeld.Wait();

        // Thread 2: Try to acquire MODIFY - will set MODIFY_PENDING
        var aboutToEnterModify = new ManualResetEventSlim(false);
        var modifyTask = Task.Run(() =>
        {
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            aboutToEnterModify.Set();
            var acquired = control.EnterModify(ref ctx); // blocks because ACCESSING is held; sets MODIFY_PENDING first
            if (acquired)
            {
                control.ExitModify();
            }
        });

        aboutToEnterModify.Wait();
        // Spin until MODIFY_PENDING is actually set by the EnterModify spin loop
        SpinWait.SpinUntil(() => control.IsModifyPending, TimeSpan.FromSeconds(1));

        // Thread 3: Try to acquire ACCESSING - should be blocked by MODIFY_PENDING
        var secondAccessTask = Task.Run(() =>
        {
            secondAccessResult = control.TryEnterAccessing();
        });

        secondAccessTask.Wait();

        Assert.That(control.IsModifyPending, Is.True, "MODIFY_PENDING should be set");
        Assert.That(secondAccessResult, Is.False, "New ACCESSING should be blocked by MODIFY_PENDING");

        canRelease.Set();
        Task.WaitAll(firstAccessTask, modifyTask);
    }

    [Test]
    [CancelAfter(2000)]
    public void ModifyPending_DrainsThenGrantsModify()
    {
        var control = new ResourceAccessControl();
        var accessorsReady = new CountdownEvent(3);
        var canRelease = new ManualResetEventSlim(false);
        var modifyAcquired = false;

        // Start 3 ACCESSING holders
        var accessTasks = new Task[3];
        for (int i = 0; i < 3; i++)
        {
            accessTasks[i] = Task.Run(() =>
            {
                control.EnterAccessing(ref TestWaitContext.Default);
                accessorsReady.Signal();
                canRelease.Wait();
                control.ExitAccessing();
            });
        }

        accessorsReady.Wait();
        Assert.That(control.AccessingCount, Is.EqualTo(3));

        // Start MODIFY waiter
        var aboutToEnterModify = new ManualResetEventSlim(false);
        var modifyTask = Task.Run(() =>
        {
            aboutToEnterModify.Set();
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            modifyAcquired = control.EnterModify(ref ctx);
            if (modifyAcquired)
            {
                control.ExitModify();
            }
        });

        aboutToEnterModify.Wait();
        SpinWait.SpinUntil(() => control.IsModifyPending, TimeSpan.FromSeconds(1));

        Assert.That(control.IsModifyPending, Is.True, "MODIFY_PENDING should be set");
        Assert.That(modifyAcquired, Is.False, "MODIFY should not be acquired yet");

        canRelease.Set();
        Task.WaitAll(accessTasks);
        modifyTask.Wait();

        Assert.That(modifyAcquired, Is.True, "MODIFY should be acquired after ACCESSING drains");
    }

    // ========================================
    // Promotion/Demotion Tests
    // ========================================

    [Test]
    [CancelAfter(1000)]
    public void TryPromoteToModify_WhenOnlyAccessingHolder_Succeeds()
    {
        var control = new ResourceAccessControl();
        control.EnterAccessing(ref WaitContext.Null);

        var result = control.TryPromoteToModify(ref WaitContext.Null);

        Assert.That(result, Is.True);
        Assert.That(control.IsModifyHeldByCurrentThread, Is.True);
        Assert.That(control.AccessingCount, Is.EqualTo(0), "ACCESSING count should be 0 after promotion");

        control.ExitModify();
    }

    [Test]
    [CancelAfter(1000)]
    public void TryPromoteToModify_WhenMultipleAccessingHolders_WaitsForDrain()
    {
        var control = new ResourceAccessControl();
        var canRelease = new ManualResetEventSlim(false);
        var otherAcquired = new ManualResetEventSlim(false);

        // Another thread holds ACCESSING
        var otherTask = Task.Run(() =>
        {
            control.EnterAccessing(ref TestWaitContext.Default);
            otherAcquired.Set();
            canRelease.Wait();
            control.ExitAccessing();
        });

        otherAcquired.Wait();
        control.EnterAccessing(ref TestWaitContext.Default);

        // Now we have 2 ACCESSING holders
        Assert.That(control.AccessingCount, Is.EqualTo(2));

        // Try to promote with short timeout - should fail because other holder exists
        var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(30));
        var result = control.TryPromoteToModify(ref ctx);

        Assert.That(result, Is.False, "Promotion should timeout when other ACCESSING holders exist");
        Assert.That(control.AccessingCount, Is.EqualTo(2), "ACCESSING count should remain 2");

        // Clean up
        control.ExitAccessing();
        canRelease.Set();
        otherTask.Wait();
    }

    [Test]
    [CancelAfter(1000)]
    public void TryPromoteToModify_WhenIdle_ThrowsException()
    {
        var control = new ResourceAccessControl();

        Assert.Throws<InvalidOperationException>(() => control.TryPromoteToModify(ref WaitContext.Null));
    }

    [Test]
    [CancelAfter(1000)]
    public void DemoteFromModify_ToAccessing_Works()
    {
        var control = new ResourceAccessControl();
        control.EnterModify(ref WaitContext.Null);

        control.DemoteFromModify();

        Assert.That(control.ModifyHolderThreadId, Is.EqualTo(0), "MODIFY should be released");
        Assert.That(control.AccessingCount, Is.EqualTo(1), "ACCESSING count should be 1");

        control.ExitAccessing();
        Assert.That(control.AccessingCount, Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(1000)]
    public void DemoteFromModify_FromDifferentThread_ThrowsException()
    {
        var control = new ResourceAccessControl();
        control.EnterModify(ref TestWaitContext.Default);
        Exception caughtException = null;

        var task = Task.Run(() =>
        {
            try
            {
                control.DemoteFromModify();
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }
        });
        task.Wait();

        Assert.That(caughtException, Is.Not.Null);
        Assert.That(caughtException, Is.TypeOf<InvalidOperationException>());

        control.ExitModify();
    }

    // ========================================
    // DESTROY Tests
    // ========================================

    [Test]
    [CancelAfter(1000)]
    public void EnterDestroy_OnIdle_Succeeds()
    {
        var control = new ResourceAccessControl();

        var result = control.EnterDestroy(ref WaitContext.Null);

        Assert.That(result, Is.True);
        Assert.That(control.IsDestroyed, Is.True);
    }

    [Test]
    [CancelAfter(1000)]
    public void IsDestroyed_AfterDestroy_IsTrue()
    {
        var control = new ResourceAccessControl();
        control.EnterDestroy(ref WaitContext.Null);

        Assert.That(control.IsDestroyed, Is.True);
    }

    [Test]
    [CancelAfter(1000)]
    public void TryEnterAccessing_AfterDestroy_ReturnsFalse()
    {
        var control = new ResourceAccessControl();
        control.EnterDestroy(ref WaitContext.Null);

        var result = control.TryEnterAccessing();

        Assert.That(result, Is.False);
    }

    [Test]
    [CancelAfter(1000)]
    public void TryEnterModify_AfterDestroy_ReturnsFalse()
    {
        var control = new ResourceAccessControl();
        control.EnterDestroy(ref WaitContext.Null);

        var result = control.TryEnterModify();

        Assert.That(result, Is.False);
    }

    [Test]
    [CancelAfter(2000)]
    public void EnterDestroy_WaitsForAllToDrain()
    {
        var control = new ResourceAccessControl();
        var accessingHeld = new ManualResetEventSlim(false);
        var canReleaseAccessing = new ManualResetEventSlim(false);
        var destroyCompleted = false;

        // Hold ACCESSING until signaled
        var accessTask = Task.Run(() =>
        {
            control.EnterAccessing(ref TestWaitContext.Default);
            accessingHeld.Set();
            canReleaseAccessing.Wait();
            control.ExitAccessing();
        });

        accessingHeld.Wait();

        // Try to destroy - should wait for ACCESSING to drain
        var aboutToDestroy = new ManualResetEventSlim(false);
        var destroyTask = Task.Run(() =>
        {
            aboutToDestroy.Set();
            destroyCompleted = control.EnterDestroy(ref TestWaitContext.Default);
        });

        aboutToDestroy.Wait();
        // Wait until DESTROY flag is set (Phase 1 of EnterDestroy), proving it's waiting for drain
        SpinWait.SpinUntil(() => control.IsDestroyed, TimeSpan.FromSeconds(1));

        // Now release ACCESSING so destroy can complete
        canReleaseAccessing.Set();

        Task.WaitAll(accessTask, destroyTask);

        Assert.That(destroyCompleted, Is.True);
        Assert.That(control.IsDestroyed, Is.True);
    }

    [Test]
    [CancelAfter(2000)]
    public void EnterDestroy_WaitsForModifyToDrain()
    {
        var control = new ResourceAccessControl();
        var modifyHeld = new ManualResetEventSlim(false);
        var canReleaseModify = new ManualResetEventSlim(false);
        var destroyCompleted = false;

        // Hold MODIFY until signaled
        var modifyTask = Task.Run(() =>
        {
            control.EnterModify(ref TestWaitContext.Default);
            modifyHeld.Set();
            canReleaseModify.Wait();
            control.ExitModify();
        });

        modifyHeld.Wait();

        // Try to destroy - should wait for MODIFY to drain
        var aboutToDestroy = new ManualResetEventSlim(false);
        var destroyTask = Task.Run(() =>
        {
            aboutToDestroy.Set();
            destroyCompleted = control.EnterDestroy(ref TestWaitContext.Default);
        });

        aboutToDestroy.Wait();
        // Wait until DESTROY flag is set (Phase 1 of EnterDestroy), proving it's waiting for drain
        SpinWait.SpinUntil(() => control.IsDestroyed, TimeSpan.FromSeconds(1));

        // Now release MODIFY so destroy can complete
        canReleaseModify.Set();

        Task.WaitAll(modifyTask, destroyTask);

        Assert.That(destroyCompleted, Is.True);
        Assert.That(control.IsDestroyed, Is.True);
    }

    [Test]
    [CancelAfter(2000)]
    public void EnterDestroy_DestroyFlagRemains_AfterTimeout()
    {
        var control = new ResourceAccessControl();
        var canRelease = new ManualResetEventSlim(false);
        var accessHeld = new ManualResetEventSlim(false);

        // Hold ACCESSING indefinitely
        var accessTask = Task.Run(() =>
        {
            control.EnterAccessing(ref TestWaitContext.Default);
            accessHeld.Set();
            canRelease.Wait();
            control.ExitAccessing();
        });

        accessHeld.Wait();

        // Try to destroy with timeout
        var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(30));
        var result = control.EnterDestroy(ref ctx);

        Assert.That(result, Is.False, "Destroy should timeout");
        Assert.That(control.IsDestroyed, Is.True, "DESTROY flag should remain set after timeout");

        // ACCESSING should now fail because DESTROY is set
        var accessResult = control.TryEnterAccessing();
        Assert.That(accessResult, Is.False, "New ACCESSING should fail because DESTROY is set");

        canRelease.Set();
        accessTask.Wait();
    }

    // ========================================
    // Timeout/Cancellation Tests
    // ========================================

    [Test]
    [CancelAfter(2000)]
    public void EnterAccessing_WithTimeout_WhenModifyPending_TimesOut()
    {
        var control = new ResourceAccessControl();
        var firstAccessHeld = new ManualResetEventSlim(false);
        var canRelease = new ManualResetEventSlim(false);

        // Hold ACCESSING to cause MODIFY to set MODIFY_PENDING
        var firstAccessTask = Task.Run(() =>
        {
            control.EnterAccessing(ref TestWaitContext.Default);
            firstAccessHeld.Set();
            canRelease.Wait();
            control.ExitAccessing();
        });

        firstAccessHeld.Wait();

        // Start MODIFY waiter to set MODIFY_PENDING
        var aboutToEnterModify = new ManualResetEventSlim(false);
        var modifyTask = Task.Run(() =>
        {
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            aboutToEnterModify.Set();
            control.EnterModify(ref ctx); // blocks because ACCESSING is held; sets MODIFY_PENDING first
            control.ExitModify();
        });

        aboutToEnterModify.Wait();
        SpinWait.SpinUntil(() => control.IsModifyPending, TimeSpan.FromSeconds(1));

        // Now try ACCESSING with timeout - should fail due to MODIFY_PENDING
        var ctx2 = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(30));
        var result = control.EnterAccessing(ref ctx2);

        Assert.That(result, Is.False, "Should timeout when MODIFY_PENDING is set");

        canRelease.Set();
        Task.WaitAll(firstAccessTask, modifyTask);
    }

    [Test]
    [CancelAfter(2000)]
    public void EnterModify_WithTimeout_WhenAccessingHeld_TimesOut()
    {
        var control = new ResourceAccessControl();
        var canRelease = new ManualResetEventSlim(false);
        var accessingHeld = new ManualResetEventSlim(false);

        var accessTask = Task.Run(() =>
        {
            control.EnterAccessing(ref TestWaitContext.Default);
            accessingHeld.Set();
            canRelease.Wait();
            control.ExitAccessing();
        });

        accessingHeld.Wait();

        var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(30));
        var result = control.EnterModify(ref ctx);

        Assert.That(result, Is.False, "Should timeout when ACCESSING is held");

        canRelease.Set();
        accessTask.Wait();
    }

    [Test]
    [CancelAfter(2000)]
    public void EnterAccessing_WithCancellation_WhenCanceled_ReturnsFalse()
    {
        var control = new ResourceAccessControl();

        // Set up a condition where ACCESSING would block
        var firstAccessHeld = new ManualResetEventSlim(false);
        var canRelease = new ManualResetEventSlim(false);

        var firstAccessTask = Task.Run(() =>
        {
            control.EnterAccessing(ref TestWaitContext.Default);
            firstAccessHeld.Set();
            canRelease.Wait();
            control.ExitAccessing();
        });

        firstAccessHeld.Wait();

        // Start MODIFY waiter to set MODIFY_PENDING
        var aboutToEnterModify = new ManualResetEventSlim(false);
        var modifyTask = Task.Run(() =>
        {
            aboutToEnterModify.Set();
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
            control.EnterModify(ref ctx); // blocks because ACCESSING is held; sets MODIFY_PENDING first
            control.ExitModify();
        });

        aboutToEnterModify.Wait();
        SpinWait.SpinUntil(() => control.IsModifyPending, TimeSpan.FromSeconds(1));

        using var cts = new CancellationTokenSource();
        var accessingTask = Task.Run(() =>
        {
            var ctx = WaitContext.FromToken(cts.Token);
            return control.EnterAccessing(ref ctx);
        });

        Thread.Sleep(50);
        cts.Cancel();

        var result = accessingTask.Result;
        Assert.That(result, Is.False, "Should return false when canceled");

        canRelease.Set();
        Task.WaitAll(firstAccessTask, modifyTask);
    }

    // ========================================
    // Scoped Guard Tests
    // ========================================

    [Test]
    [CancelAfter(1000)]
    public void EnterAccessingScoped_AutomaticallyExits()
    {
        var control = new ResourceAccessControl();

        using (var guard = control.EnterAccessingScoped(ref WaitContext.Null))
        {
            Assert.That(control.AccessingCount, Is.EqualTo(1));
        }

        Assert.That(control.AccessingCount, Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(1000)]
    public void EnterModifyScoped_AutomaticallyExits()
    {
        var control = new ResourceAccessControl();

        using (var guard = control.EnterModifyScoped(ref WaitContext.Null))
        {
            Assert.That(control.IsModifyHeldByCurrentThread, Is.True);
        }

        Assert.That(control.ModifyHolderThreadId, Is.EqualTo(0));
    }

    private sealed class ControlHolder
    {
        public ResourceAccessControl Control;
    }

    /// <summary>
    /// A control held in a class field, moved by a compacting collection while a guard is open: the guard must release the state where it now lives.
    /// </summary>
    /// <remarks>
    /// The guards used to capture an <c>int*</c> taken under <c>fixed</c> and keep it past the pin; after a move they released the old address.
    /// </remarks>
    [Test]
    [CancelAfter(1000)]
    public void ScopedGuards_ControlMovedByACompactingGc_ReleaseTheMovedState()
    {
        // Garbage allocated just before the holder leaves a gap below it for the compaction to close, so the holder moves.
        var garbage = new byte[4096][];
        for (int i = 0; i < garbage.Length; i++)
        {
            garbage[i] = new byte[64];
        }
        var holder = new ControlHolder();
        garbage = null;

        using (holder.Control.EnterAccessingScoped(ref WaitContext.Null))
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Assert.That(holder.Control.AccessingCount, Is.EqualTo(1));
        }
        Assert.That(holder.Control.AccessingCount, Is.Zero);

        using (holder.Control.EnterModifyScoped(ref WaitContext.Null))
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Assert.That(holder.Control.IsModifyHeldByCurrentThread, Is.True);
        }
        Assert.That(holder.Control.ModifyHolderThreadId, Is.Zero);
    }

    [Test]
    [CancelAfter(1000)]
    public void EnterAccessingScoped_OnTimeout_ThrowsLockTimeoutException()
    {
        var control = new ResourceAccessControl();
        control.EnterDestroy(ref WaitContext.Null); // Block all access

        var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(10));
        Assert.Throws<LockTimeoutException>(() =>
        {
            control.EnterAccessingScoped(ref ctx);
        });
    }

    [Test]
    [CancelAfter(1000)]
    public void EnterModifyScoped_OnTimeout_ThrowsLockTimeoutException()
    {
        var control = new ResourceAccessControl();
        control.EnterDestroy(ref WaitContext.Null); // Block all access

        var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(10));
        Assert.Throws<LockTimeoutException>(() =>
        {
            control.EnterModifyScoped(ref ctx);
        });
    }

    // ========================================
    // Reset Tests
    // ========================================

    [Test]
    [CancelAfter(1000)]
    public void Reset_FromIdle_RemainsIdle()
    {
        var control = new ResourceAccessControl();

        control.Reset();

        Assert.That(control.AccessingCount, Is.EqualTo(0));
        Assert.That(control.ModifyHolderThreadId, Is.EqualTo(0));
        Assert.That(control.IsDestroyed, Is.False);
    }

    [Test]
    [CancelAfter(1000)]
    public void Reset_ClearsDestroyedState()
    {
        var control = new ResourceAccessControl();
        control.EnterDestroy(ref WaitContext.Null);

        control.Reset();

        Assert.That(control.IsDestroyed, Is.False);
        Assert.That(control.TryEnterAccessing(), Is.True);
        control.ExitAccessing();
    }

    [Test]
    [CancelAfter(1000)]
    public void Reset_AllowsNewAccess()
    {
        var control = new ResourceAccessControl();
        control.EnterAccessing(ref WaitContext.Null);
        control.EnterAccessing(ref WaitContext.Null);
        control.Reset();

        control.EnterAccessing(ref WaitContext.Null);
        Assert.That(control.AccessingCount, Is.EqualTo(1));
        control.ExitAccessing();

        control.EnterModify(ref WaitContext.Null);
        Assert.That(control.IsModifyHeldByCurrentThread, Is.True);
        control.ExitModify();
    }

    // ========================================
    // Diagnostic State Tests
    // ========================================

    [Test]
    [CancelAfter(1000)]
    public void GetDiagnosticState_ReturnsCorrectValues()
    {
        var control = new ResourceAccessControl();
        control.EnterAccessing(ref WaitContext.Null);
        control.EnterAccessing(ref WaitContext.Null);

        var state = control.GetDiagnosticState();

        Assert.That(state.AccessingCount, Is.EqualTo(2));
        Assert.That(state.ModifyHolderThreadId, Is.EqualTo(0));
        Assert.That(state.ModifyPending, Is.False);
        Assert.That(state.Destroyed, Is.False);

        control.ExitAccessing();
        control.ExitAccessing();
    }

    [Test]
    [CancelAfter(1000)]
    public void IsModifyHeldByCurrentThread_WhenHeld_ReturnsTrue()
    {
        var control = new ResourceAccessControl();
        control.EnterModify(ref WaitContext.Null);

        Assert.That(control.IsModifyHeldByCurrentThread, Is.True);

        control.ExitModify();
    }

    [Test]
    [CancelAfter(1000)]
    public void IsModifyHeldByCurrentThread_WhenHeldByOther_ReturnsFalse()
    {
        var control = new ResourceAccessControl();
        var canRelease = new ManualResetEventSlim(false);
        var modifyHeld = new ManualResetEventSlim(false);

        var task = Task.Run(() =>
        {
            control.EnterModify(ref TestWaitContext.Default);
            modifyHeld.Set();
            canRelease.Wait();
            control.ExitModify();
        });

        modifyHeld.Wait();

        Assert.That(control.IsModifyHeldByCurrentThread, Is.False);

        canRelease.Set();
        task.Wait();
    }

    // ========================================
    // Stress Tests
    // ========================================

    [Test]
    [CancelAfter(10000)]
    public void StressTest_AccessingOnly_NoConcurrencyIssues()
    {
        var control = new ResourceAccessControl();
        var operationCount = 0;
        var errorCount = 0;

        Parallel.For(0, 20, i =>
        {
            for (int j = 0; j < 500; j++)
            {
                try
                {
                    control.EnterAccessing(ref TestWaitContext.Default);
                    Thread.SpinWait(5);
                    control.ExitAccessing();
                    Interlocked.Increment(ref operationCount);
                }
                catch
                {
                    Interlocked.Increment(ref errorCount);
                }
            }
        });

        Assert.That(errorCount, Is.EqualTo(0));
        Assert.That(operationCount, Is.EqualTo(10000));
        Assert.That(control.AccessingCount, Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(10000)]
    public void StressTest_MixedAccessingAndModify_NoDeadlock()
    {
        var control = new ResourceAccessControl();
        var operationCount = 0;
        var errorCount = 0;

        Parallel.For(0, 10, i =>
        {
            for (int j = 0; j < 200; j++)
            {
                try
                {
                    if (i % 3 == 0)
                    {
                        // MODIFY
                        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));
                        if (control.EnterModify(ref ctx))
                        {
                            Thread.SpinWait(5);
                            control.ExitModify();
                        }
                    }
                    else
                    {
                        // ACCESSING
                        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));
                        if (control.EnterAccessing(ref ctx))
                        {
                            Thread.SpinWait(5);
                            control.ExitAccessing();
                        }
                    }

                    Interlocked.Increment(ref operationCount);
                }
                catch
                {
                    Interlocked.Increment(ref errorCount);
                }
            }
        });

        Assert.That(errorCount, Is.EqualTo(0));
        Assert.That(operationCount, Is.EqualTo(2000));
    }

    [Test]
    [CancelAfter(1000)]
    public void StressTest_AccessingNotBlockedByModify_VerifyDesign()
    {
        // This test verifies the key design property: when MODIFY is held (without MODIFY_PENDING),
        // ACCESSING can still be acquired
        var control = new ResourceAccessControl();
        var modifyHeld = new ManualResetEventSlim(false);
        var testComplete = new ManualResetEventSlim(false);
        var accessAcquiredDuringModify = 0;
        var totalAccessAttempts = 0;

        // Thread holding MODIFY
        var modifyTask = Task.Run(() =>
        {
            for (int i = 0; i < 10; i++)
            {
                control.EnterModify(ref TestWaitContext.Default);
                modifyHeld.Set();
                Thread.Sleep(10);
                control.ExitModify();
                modifyHeld.Reset();
                Thread.SpinWait(100);
            }
            testComplete.Set();
        });

        // Multiple threads trying ACCESSING
        var accessTasks = new Task[5];
        for (int t = 0; t < 5; t++)
        {
            accessTasks[t] = Task.Run(() =>
            {
                while (!testComplete.IsSet)
                {
                    if (modifyHeld.IsSet)
                    {
                        Interlocked.Increment(ref totalAccessAttempts);
                        if (control.TryEnterAccessing())
                        {
                            Interlocked.Increment(ref accessAcquiredDuringModify);
                            control.ExitAccessing();
                        }
                    }
                    Thread.SpinWait(10);
                }
            });
        }

        modifyTask.Wait();
        Task.WaitAll(accessTasks);

        // Some ACCESSING acquisitions should succeed while MODIFY is held
        // (as long as MODIFY_PENDING is not set)
        Console.WriteLine($"ACCESSING acquired during MODIFY: {accessAcquiredDuringModify} / {totalAccessAttempts}");
        Assert.That(accessAcquiredDuringModify, Is.GreaterThan(0),
            "Some ACCESSING should succeed while MODIFY is held (key design property)");
    }

    [Test]
    [CancelAfter(1000)]
    public void StressTest_PromotionUnderContention()
    {
        var control = new ResourceAccessControl();
        var successfulPromotions = 0;
        var failedPromotions = 0;

        Parallel.For(0, 10, i =>
        {
            for (int j = 0; j < 10; j++)
            {
                control.EnterAccessing(ref TestWaitContext.Default);

                var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(10));
                if (control.TryPromoteToModify(ref ctx))
                {
                    Interlocked.Increment(ref successfulPromotions);
                    Thread.SpinWait(5);
                    control.ExitModify();
                }
                else
                {
                    Interlocked.Increment(ref failedPromotions);
                    control.ExitAccessing();
                }
            }
        });

        Console.WriteLine($"Successful promotions: {successfulPromotions}, Failed: {failedPromotions}");
        Assert.That(successfulPromotions + failedPromotions, Is.EqualTo(100));
        Assert.That(successfulPromotions, Is.GreaterThan(0), "Some promotions should succeed");
    }

    [Test]
    [CancelAfter(10000)]
    public void StressTest_DemotionCycles()
    {
        var control = new ResourceAccessControl();
        var cycleCount = 0;

        Parallel.For(0, 5, i =>
        {
            for (int j = 0; j < 100; j++)
            {
                var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));
                if (control.EnterModify(ref ctx))
                {
                    Thread.SpinWait(5);
                    control.DemoteFromModify();
                    Thread.SpinWait(5);
                    control.ExitAccessing();
                    Interlocked.Increment(ref cycleCount);
                }
            }
        });

        Console.WriteLine($"Completed cycles: {cycleCount}");
        Assert.That(cycleCount, Is.GreaterThan(0));
        Assert.That(control.AccessingCount, Is.EqualTo(0));
        Assert.That(control.ModifyHolderThreadId, Is.EqualTo(0));
    }

    // ========================================
    // Contention Flag Tests
    // ========================================

    [Test]
    [CancelAfter(1000)]
    public void WasContended_InitiallyFalse()
    {
        var control = new ResourceAccessControl();
        Assert.That(control.WasContended, Is.False);
    }

    [Test]
    [CancelAfter(1000)]
    public void WasContended_FalseAfterUncontendedAccessingAcquisition()
    {
        var control = new ResourceAccessControl();

        control.EnterAccessing(ref WaitContext.Null);
        control.ExitAccessing();

        Assert.That(control.WasContended, Is.False, "Uncontended ACCESSING should not set flag");
    }

    [Test]
    [CancelAfter(1000)]
    public void WasContended_FalseAfterUncontendedModifyAcquisition()
    {
        var control = new ResourceAccessControl();

        control.EnterModify(ref WaitContext.Null);
        control.ExitModify();

        Assert.That(control.WasContended, Is.False, "Uncontended MODIFY should not set flag");
    }

    [Test]
    [CancelAfter(5000)]
    public void WasContended_TrueAfterModifyContention()
    {
        var control = new ResourceAccessControl();
        var modifyHeld = new ManualResetEventSlim(false);
        var canRelease = new ManualResetEventSlim(false);

        // Thread 1 holds MODIFY until signaled
        var t1 = Task.Run(() =>
        {
            control.EnterModify(ref TestWaitContext.Default);
            modifyHeld.Set();
            canRelease.Wait();
            control.ExitModify();
        });

        modifyHeld.Wait();

        // Thread 2 tries MODIFY - will contend
        var aboutToEnterModify = new ManualResetEventSlim(false);
        var t2 = Task.Run(() =>
        {
            aboutToEnterModify.Set();
            control.EnterModify(ref TestWaitContext.Default);
            control.ExitModify();
        });

        aboutToEnterModify.Wait();
        // Wait until contention is actually recorded (EnterModify sets CONTENTION_FLAG when it can't acquire)
        SpinWait.SpinUntil(() => control.WasContended, TimeSpan.FromSeconds(1));

        // Allow t1 to release so t2 can acquire
        canRelease.Set();

        Task.WaitAll(t1, t2);

        Assert.That(control.WasContended, Is.True, "MODIFY contention should set flag");
    }

    [Test]
    [CancelAfter(5000)]
    public void WasContended_TrueAfterAccessingBlockedByModifyPending()
    {
        var control = new ResourceAccessControl();
        var firstAccessHeld = new ManualResetEventSlim(false);
        var canRelease = new ManualResetEventSlim(false);

        // Thread 1 holds ACCESSING
        var firstAccessTask = Task.Run(() =>
        {
            control.EnterAccessing(ref TestWaitContext.Default);
            firstAccessHeld.Set();
            canRelease.Wait();
            control.ExitAccessing();
        });

        firstAccessHeld.Wait();

        // Thread 2 tries MODIFY - sets MODIFY_PENDING
        var aboutToEnterModify = new ManualResetEventSlim(false);
        var modifyTask = Task.Run(() =>
        {
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            aboutToEnterModify.Set();
            control.EnterModify(ref ctx); // blocks because ACCESSING is held; sets MODIFY_PENDING first
            control.ExitModify();
        });

        aboutToEnterModify.Wait();
        SpinWait.SpinUntil(() => control.IsModifyPending, TimeSpan.FromSeconds(1));

        // Thread 3 tries ACCESSING - blocked by MODIFY_PENDING
        var secondAccessTask = Task.Run(() =>
        {
            var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(30));
            control.EnterAccessing(ref ctx);
        });

        secondAccessTask.Wait();

        // Release everything
        canRelease.Set();
        Task.WaitAll(firstAccessTask, modifyTask);

        Assert.That(control.WasContended, Is.True, "ACCESSING blocked by MODIFY_PENDING should set flag");
    }

    [Test]
    [CancelAfter(5000)]
    public void WasContended_TrueAfterModifyWaitsForAccessing()
    {
        var control = new ResourceAccessControl();
        var accessingHeld = new ManualResetEventSlim(false);
        var modifyAttempted = new ManualResetEventSlim(false);

        // Thread 1 holds ACCESSING
        var accessTask = Task.Run(() =>
        {
            control.EnterAccessing(ref TestWaitContext.Default);
            accessingHeld.Set();
            modifyAttempted.Wait();
            Thread.Sleep(100);
            control.ExitAccessing();
        });

        accessingHeld.Wait();

        // Thread 2 tries MODIFY - will wait
        var modifyTask = Task.Run(() =>
        {
            modifyAttempted.Set();
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            control.EnterModify(ref ctx);
            control.ExitModify();
        });

        Task.WaitAll(accessTask, modifyTask);

        Assert.That(control.WasContended, Is.True, "MODIFY waiting for ACCESSING should set flag");
    }

    [Test]
    [CancelAfter(5000)]
    public void WasContended_PersistsAfterRelease()
    {
        var control = new ResourceAccessControl();
        var modifyHeld = new ManualResetEventSlim(false);
        var canRelease = new ManualResetEventSlim(false);

        // Create contention: t1 holds MODIFY, t2 contends
        var t1 = Task.Run(() =>
        {
            control.EnterModify(ref TestWaitContext.Default);
            modifyHeld.Set();
            canRelease.Wait();
            control.ExitModify();
        });

        modifyHeld.Wait();

        var aboutToEnterModify = new ManualResetEventSlim(false);
        var t2 = Task.Run(() =>
        {
            aboutToEnterModify.Set();
            control.EnterModify(ref TestWaitContext.Default);
            control.ExitModify();
        });

        aboutToEnterModify.Wait();
        SpinWait.SpinUntil(() => control.WasContended, TimeSpan.FromSeconds(1));

        canRelease.Set();

        Task.WaitAll(t1, t2);

        // Flag should persist after all locks released
        Assert.That(control.WasContended, Is.True, "Flag should persist after release");

        // More operations shouldn't change it
        control.EnterAccessing(ref WaitContext.Null);
        control.ExitAccessing();
        Assert.That(control.WasContended, Is.True, "Flag should persist through subsequent operations");
    }

    [Test]
    [CancelAfter(1000)]
    public void WasContended_ClearedByReset()
    {
        var control = new ResourceAccessControl();
        var modifyHeld = new ManualResetEventSlim(false);
        var canRelease = new ManualResetEventSlim(false);

        // Create contention: t1 holds MODIFY, t2 contends
        var t1 = Task.Run(() =>
        {
            control.EnterModify(ref TestWaitContext.Default);
            modifyHeld.Set();
            canRelease.Wait();
            control.ExitModify();
        });

        modifyHeld.Wait();

        var aboutToEnterModify = new ManualResetEventSlim(false);
        var t2 = Task.Run(() =>
        {
            aboutToEnterModify.Set();
            control.EnterModify(ref TestWaitContext.Default);
            control.ExitModify();
        });

        aboutToEnterModify.Wait();
        SpinWait.SpinUntil(() => control.WasContended, TimeSpan.FromSeconds(1));

        canRelease.Set();

        Task.WaitAll(t1, t2);

        Assert.That(control.WasContended, Is.True);

        control.Reset();

        Assert.That(control.WasContended, Is.False, "Reset should clear contention flag");
    }

    [Test]
    [CancelAfter(5000)]
    public void WasContended_TrueAfterDestroyWaitsForAccessing()
    {
        var control = new ResourceAccessControl();
        var accessingHeld = new ManualResetEventSlim(false);
        var destroyStarted = new ManualResetEventSlim(false);

        // Thread holds ACCESSING
        var accessTask = Task.Run(() =>
        {
            control.EnterAccessing(ref TestWaitContext.Default);
            accessingHeld.Set();
            destroyStarted.Wait();
            Thread.Sleep(100);
            control.ExitAccessing();
        });

        accessingHeld.Wait();

        // Try to destroy - will wait
        var destroyTask = Task.Run(() =>
        {
            destroyStarted.Set();
            control.EnterDestroy(ref TestWaitContext.Default);
        });

        Task.WaitAll(accessTask, destroyTask);

        Assert.That(control.WasContended, Is.True, "DESTROY waiting for ACCESSING should set flag");
    }

    // ========================================
    // Race Condition Tests
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public void RaceCondition_ModifyProtectsData()
    {
        var control = new ResourceAccessControl();
        var counter = 0;
        var errors = 0;

        Parallel.For(0, 10, i =>
        {
            for (int j = 0; j < 100; j++)
            {
                control.EnterModify(ref TestWaitContext.Default);

                // Non-atomic increment - should be safe under MODIFY lock
                var temp = counter;
                Thread.SpinWait(5);
                counter = temp + 1;

                if (counter != temp + 1)
                {
                    Interlocked.Increment(ref errors);
                }

                control.ExitModify();
            }
        });

        Assert.That(errors, Is.EqualTo(0));
        Assert.That(counter, Is.EqualTo(1000));
    }

    [Test]
    [CancelAfter(5000)]
    public void RaceCondition_AccessingAllowsConcurrentReads()
    {
        var control = new ResourceAccessControl();
        var sharedValue = 42;
        var readValues = new ConcurrentBag<int>();
        var allInsideAccessing = new CountdownEvent(10);
        var canExit = new ManualResetEventSlim(false);

        var tasks = new Task[10];
        for (int i = 0; i < 10; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                control.EnterAccessing(ref TestWaitContext.Default);
                allInsideAccessing.Signal();
                allInsideAccessing.Wait();

                readValues.Add(sharedValue);

                canExit.Wait();
                control.ExitAccessing();
            });
        }

        allInsideAccessing.Wait();
        Assert.That(control.AccessingCount, Is.EqualTo(10));

        canExit.Set();
        Task.WaitAll(tasks);

        Assert.That(readValues.Count, Is.EqualTo(10));
        Assert.That(readValues, Is.All.EqualTo(42));
    }

    [Test]
    [CancelAfter(5000)]
    [Repeat(10)]
    public void RaceCondition_DestroyBlocksAllNewAcquisitions()
    {
        var control = new ResourceAccessControl();
        var attemptResults = new ConcurrentBag<bool>();

        // Start destroy
        var aboutToDestroy = new ManualResetEventSlim(false);
        var destroyTask = Task.Run(() =>
        {
            aboutToDestroy.Set();
            control.EnterDestroy(ref TestWaitContext.Default);
        });

        aboutToDestroy.Wait();
        SpinWait.SpinUntil(() => control.IsDestroyed, TimeSpan.FromSeconds(1));

        // Multiple threads try to acquire locks
        var attemptTasks = new Task[10];
        for (int i = 0; i < 10; i++)
        {
            int index = i;
            attemptTasks[i] = Task.Run(() =>
            {
                var result = index % 2 == 0
                    ? control.TryEnterAccessing()
                    : control.TryEnterModify();
                attemptResults.Add(result);
            });
        }

        Task.WaitAll(attemptTasks);
        destroyTask.Wait();

        // All attempts should fail because DESTROY is set
        Assert.That(attemptResults, Is.All.False,
            "All lock acquisitions should fail after DESTROY flag is set");
    }
}
