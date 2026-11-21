using NUnit.Framework;
using System;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// A dead thread's slot is freed without a consumer when no profiling session is possible, and left for the consumer otherwise; a releaser whose claim a
/// reset wiped cannot touch the slot a live thread claimed since.
/// </summary>
[TestFixture]
[NonParallelizable] // ThreadSlotRegistry is static
public sealed class ThreadSlotReaperTests
{
    [SetUp]
    public void SetUp()
    {
        Assume.That(ThreadSlotRegistry.SessionPossible, Is.False, "the suite's configuration (profiler on, Trace none) expects no session");
    }

    /// <summary>Claims a slot on a thread that then exits, and runs that thread's releaser finalizer.</summary>
    private static int ClaimOnAThreadThatDies()
    {
        var index = -1;
        var t = new Thread(() => index = ThreadSlotRegistry.GetOrAssignSlot());
        t.Start();
        t.Join();
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return index;
    }

    [Test]
    public void ADeadThreadsSlot_IsFreed_WhenNoSessionIsPossible()
    {
        var index = ClaimOnAThreadThatDies();
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        Assert.That(ThreadSlotRegistry.GetSlotState(index), Is.EqualTo((int)SlotState.Free), "nothing will ever drain it: the finalizer frees it");
        Assert.That(ThreadSlotRegistry.GetSlot(index).OwnerManagedThreadId, Is.Zero);
    }

    [Test]
    public void WithAConsumerAttached_TheSlotIsLeftRetiring_AndFreedWhenItDetaches()
    {
        ThreadSlotRegistry.ConsumerAttached();
        int index;
        try
        {
            index = ClaimOnAThreadThatDies();
            Assert.That(ThreadSlotRegistry.GetSlotState(index), Is.EqualTo((int)SlotState.Retiring), "the consumer drains and frees it");
        }
        finally
        {
            ThreadSlotRegistry.ConsumerDetached();
        }

        Assert.That(ThreadSlotRegistry.GetSlotState(index), Is.EqualTo((int)SlotState.Free), "the last detach frees what retired meanwhile");
    }

    [Test]
    public void AReleaserOfAWipedClaim_DoesNotRetireTheSlotALiveThreadClaimedSince()
    {
        var index = -1;
        var staleGeneration = 0;
        var claimed = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var live = new Thread(() =>
        {
            index = ThreadSlotRegistry.GetOrAssignSlot();
            staleGeneration = ThreadSlotRegistry.GetSlot(index).ClaimGeneration - 1;
            claimed.Set();
            release.Wait();
            ThreadSlotRegistry.ReleaseCurrentThreadForTests();
        });
        live.Start();
        claimed.Wait();
        try
        {
            // A finalizer of the slot's previous claim (wiped by a reset) runs now: it must not retire, let alone free, the live thread's slot.
            ThreadSlotRegistry.MarkRetiring(index, staleGeneration);
            Assert.That(ThreadSlotRegistry.GetSlotState(index), Is.EqualTo((int)SlotState.Active));
        }
        finally
        {
            release.Set();
            live.Join();
        }
    }
}
