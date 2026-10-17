using System;
using System.Threading;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The back-pressure wait's wake-ups (#1382). A shared event was waited on and reset by each waiter it woke, so a page made evictable between one
/// waiter's wake and its reset was swallowed for the waiters not yet back in their wait, which slept out the round. A generation counter ends every
/// waiter's round at the first signal after the round began.
/// </summary>
[TestFixture]
class WaitForIOStrategyTests
{
    private static Thread StartWaiter(WaitForIOStrategy strategy, Action<bool> done)
    {
        var thread = new Thread(() =>
        {
            var ctx = new BackpressureContext("test", TimeSpan.FromSeconds(30));
            done(strategy.OnPressure(ref ctx, 0, 0));
        });
        thread.Start();
        return thread;
    }

    private static void WaitForWaiters(WaitForIOStrategy strategy, int count)
    {
        var spin = new SpinWait();
        var until = Environment.TickCount64 + 5_000;
        while (strategy.Waiters < count)
        {
            Assert.That(Environment.TickCount64, Is.LessThan(until), $"premise: {count} threads waiting in a round");
            spin.SpinOnce();
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public void OneSignal_EndsTheRoundOfEveryWaiter()
    {
        using var strategy = new WaitForIOStrategy();
        strategy.RoundMilliseconds = 60_000;   // only a signal can end a round
        var returned = 0;
        var a = StartWaiter(strategy, _ => Interlocked.Increment(ref returned));
        var b = StartWaiter(strategy, _ => Interlocked.Increment(ref returned));
        WaitForWaiters(strategy, 2);

        strategy.SignalPageAvailable();

        Assert.That(a.Join(5_000) && b.Join(5_000), Is.True, "both waiters' rounds ended on the one signal");
        Assert.That(returned, Is.EqualTo(2));
        Assert.That(strategy.Waiters, Is.Zero);
    }

    [Test]
    [CancelAfter(10_000)]
    public void ASignalBeforeTheRound_DoesNotEndIt()
    {
        using var strategy = new WaitForIOStrategy();
        strategy.SignalPageAvailable();   // no waiter: nothing carried into a later round
        strategy.RoundMilliseconds = 100;
        var started = Environment.TickCount64;
        var ctx = new BackpressureContext("test", TimeSpan.FromSeconds(30));

        Assert.That(strategy.OnPressure(ref ctx, 0, 0), Is.True);
        Assert.That(Environment.TickCount64 - started, Is.GreaterThanOrEqualTo(90), "the round ran out: the earlier signal was not its");
    }

    [Test]
    [CancelAfter(10_000)]
    public void AContextPastItsDeadline_GivesUpWithoutWaiting()
    {
        using var strategy = new WaitForIOStrategy();
        strategy.RoundMilliseconds = 60_000;
        var ctx = new BackpressureContext("test", TimeSpan.Zero);

        Assert.That(strategy.OnPressure(ref ctx, 0, 0), Is.False);
    }
}
