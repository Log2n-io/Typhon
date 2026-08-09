using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The DEBUG concurrent-mutation guard on <see cref="ChangeSet"/> (#705 T5 / #400).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not an owner-thread assert.</b> #705 asks for "a Debug owner-thread assert on ChangeSet mutation", but in <c>Deferred</c> and
/// <c>GroupCommit</c> the UnitOfWork deliberately SHARES one ChangeSet across every transaction it creates (<c>UnitOfWork.cs:64-66</c>), and those
/// transactions run on different threads — that is the production default. An owner-thread assert would fire on correct code. The guard therefore detects what
/// is actually illegal: two threads inside a mutating method AT THE SAME TIME. <c>_marksByPage</c> is a plain <c>Dictionary</c> and <c>_deferredEvictions</c> a
/// plain <c>List</c>, so concurrent mutation loses marks or corrupts the map — #400's mechanism, silent in 36 of 40 runs.
/// </para>
/// <para>
/// Both directions are pinned. A guard that fired on sequential hand-off would be worse than no guard: it would alarm on the engine's normal operation, and
/// the first response to a false alarm is to delete the detector.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
internal sealed class ChangeSetMutationGuardTests : TestBase<ChangeSetMutationGuardTests>
{
    private ChangeSet NewChangeSet()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        return new ChangeSet(dbe.MMF);
    }

    // DeferEviction is the mutating method to drive these through: it appends to `_deferredEvictions` and touches NOTHING else. AddByMemPageIndex and
    // RegisterReDirty call PagedMMF.IncrementDirty, which indexes the real page-state array — feeding them synthetic page indices tears down the test host
    // rather than testing the guard, which is exactly what the first version of this fixture did.

#if DEBUG
    /// <summary>
    /// Two threads mutating one ChangeSet concurrently must be caught, and the report must name the threads.
    /// </summary>
    /// <remarks>
    /// <b>The overlap is produced by contention, not constructed</b> — there is no injection point inside the guarded region to park a thread in. With two
    /// threads each performing 200,000 dictionary inserts on the same instance the windows overlap essentially immediately, and the test carries its own
    /// evidence either way: if the guard does not fire, the raw <c>Dictionary</c> corruption it exists to pre-empt usually does, and that is reported as a
    /// distinct (and worse) outcome rather than passing quietly.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    public void ConcurrentMutation_IsDetected()
    {
        var cs = NewChangeSet();
        using var start = new Barrier(2);
        Exception guardReport = null;
        Exception corruption = null;

        void Hammer(int keyBase)
        {
            start.SignalAndWait(TimeSpan.FromSeconds(10));
            for (var i = 0; i < 200_000; i++)
            {
                try
                {
                    cs.DeferEviction(keyBase + i);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("concurrent mutation", StringComparison.Ordinal))
                {
                    Interlocked.CompareExchange(ref guardReport, ex, null);
                    return;
                }
                catch (Exception ex)
                {
                    // A Dictionary mutated from two threads throws its own way (IndexOutOfRange, NullReference, or an InvalidOperationException about
                    // concurrent operations). Recorded separately: it proves the window was real, but it means the guard did not get there first.
                    Interlocked.CompareExchange(ref corruption, ex, null);
                    return;
                }
            }
        }

        var a = new Thread(() => Hammer(0));
        var b = new Thread(() => Hammer(1_000_000));
        a.Start();
        b.Start();
        Assert.That(a.Join(TimeSpan.FromSeconds(15)) && b.Join(TimeSpan.FromSeconds(15)), Is.True, "both hammer threads must finish");

        Assert.That(
            guardReport,
            Is.Not.Null,
            corruption == null
                ? "two threads mutated one ChangeSet concurrently and nothing objected — the guard cannot see #400's mechanism"
                : $"the raw collection corrupted before the guard reported it ({corruption.GetType().Name}: {corruption.Message}) — the guard is too narrow");
        Assert.That(guardReport.Message, Does.Contain("thread"), "the report must name the threads, or it cannot be acted on");
    }
#endif

    /// <summary>
    /// A ChangeSet handed BETWEEN threads sequentially must not trip the guard — that is the engine's normal Deferred-mode operation.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void SequentialHandoffBetweenThreads_IsAllowed()
    {
        var cs = NewChangeSet();

        for (var round = 0; round < 8; round++)
        {
            var r = round;
            var t = new Thread(() =>
            {
                for (var i = 0; i < 50; i++)
                {
                    cs.DeferEviction((r * 100) + i);
                }
            });

            t.Start();
            Assert.That(t.Join(TimeSpan.FromSeconds(5)), Is.True, "each round must finish before the next starts — this test is about hand-off, not overlap");
        }
    }

    /// <summary>
    /// Repeated mutation from the SAME thread must not trip the guard — every enter must be balanced by its exit.
    /// </summary>
    /// <remarks>
    /// The failure this catches is an unbalanced <c>ExitMutation</c>: residency left set after the first call would make the SECOND call from the same thread
    /// look like a re-entrant one forever, and worse, a genuinely concurrent call from another thread would then be compared against a stale owner.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void RepeatedMutationOnOneThread_IsAllowed()
    {
        var cs = NewChangeSet();
        Assert.DoesNotThrow(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                cs.DeferEviction(i);
            }
        });
    }
}
