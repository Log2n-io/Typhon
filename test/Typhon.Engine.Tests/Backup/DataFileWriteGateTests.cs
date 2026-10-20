using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// <see cref="DataFileWriteGate"/>: shared holders exclude the freezer and the freezer excludes new holders, a pending freeze is not starved by a
/// stream of writers, a shared hold may be released by another thread, and an exclusive acquire that cannot drain gives up and reopens the gate.
/// </summary>
[TestFixture]
public class DataFileWriteGateTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(5);

    [Test]
    [CancelAfter(5_000)]
    public void Exclusive_WaitsForTheSharedHolder_AndBlocksNewOnes()
    {
        var gate = new DataFileWriteGate();
        var held = gate.EnterShared();

        Assert.That(gate.TryEnterExclusive(TimeSpan.FromMilliseconds(20)), Is.False, "a shared holder must keep the freezer out");
        Assert.That(gate.FreezePending, Is.False, "a timed-out acquire reopens the gate");

        gate.ExitShared(held);
        Assert.That(gate.TryEnterExclusive(Long), Is.True);

        using var entered = new ManualResetEventSlim();
        using var backedOff = new ManualResetEventSlim();
        gate.BackedOffProbe = () => backedOff.Set();
        var writer = Task.Run(() =>
        {
            var t = gate.EnterShared();
            entered.Set();
            gate.ExitShared(t);
        });

        Assert.That(backedOff.Wait(Long), Is.True, "premise: the writer reached the gate");
        Assert.That(entered.IsSet, Is.False, "no shared enter while the gate is held exclusive");
        gate.ExitExclusive();
        Assert.That(writer.Wait(Long), Is.True);
        Assert.That(entered.IsSet, Is.True);
    }

    /// <summary>
    /// The freeze must not starve: once it is requested, writers arriving afterwards wait, and it is granted as soon as the writes already in flight
    /// drain, even though four other threads want in.
    /// </summary>
    [Test]
    [CancelAfter(5_000)]
    public void PendingFreeze_IsPreferredOverArrivingWriters()
    {
        var gate = new DataFileWriteGate();
        var inFlight = gate.EnterShared();

        var granted = false;
        var freezer = Task.Factory.StartNew(() =>
        {
            var ok = gate.TryEnterExclusive(Long);
            if (ok)
            {
                Volatile.Write(ref granted, true);
                gate.ExitExclusive();
            }

            return ok;
        }, TaskCreationOptions.LongRunning);

        var spin = new SpinWait();
        while (!gate.FreezePending)
        {
            spin.SpinOnce(-1);
        }

        const int Writers = 4;
        var backOffs = 0;
        using var allBackedOff = new ManualResetEventSlim();
        gate.BackedOffProbe = () =>
        {
            if (Interlocked.Increment(ref backOffs) >= Writers)
            {
                allBackedOff.Set();
            }
        };
        var enteredBeforeFreeze = 0;
        var writers = new Task[Writers];
        for (var i = 0; i < Writers; i++)
        {
            writers[i] = Task.Factory.StartNew(() =>
            {
                var t = gate.EnterShared();
                if (!Volatile.Read(ref granted))
                {
                    Interlocked.Increment(ref enteredBeforeFreeze);
                }

                gate.ExitShared(t);
            }, TaskCreationOptions.LongRunning);
        }

        Assert.That(allBackedOff.Wait(Long), Is.True, "premise: every arriving writer met the pending freeze");
        gate.ExitShared(inFlight);

        Assert.That(freezer.Wait(Long) && freezer.Result, Is.True, "the freeze is granted once the in-flight write drains");
        Assert.That(Task.WaitAll(writers, Long), Is.True);
        Assert.That(enteredBeforeFreeze, Is.Zero, "a writer that arrived after the freeze request entered ahead of it");
    }

    /// <summary>
    /// BKP-01 under load: two freezers take the gate exclusive thousands of times while four writers enter and leave shared without pause; no writer is
    /// ever inside while a freezer holds it, and a freezer never sees a shared holder. Each freeze waits for a writer entry since its previous one, so
    /// the writers run between the freezes by construction: on 2 vCPUs the freezers once finished every cycle while the writers had entered 383 times
    /// (#1468).
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("BKP-01")]
    public void Stress_NoWriterIsEverInsideAnExclusiveHold()
    {
        const int Cycles = 2_500;
        var gate = new DataFileWriteGate();
        var exclusive = 0;
        var inside = 0;   // writers between their EnterShared and ExitShared
        var violations = 0;
        var timeouts = 0;
        var stalls = 0;
        long entries = 0, entriesDuringFreezes = 0;
        using var stop = new CancellationTokenSource();
        var writers = new Task[4];
        using var started = new CountdownEvent(writers.Length);
        for (var i = 0; i < writers.Length; i++)
        {
            writers[i] = Task.Factory.StartNew(() =>
            {
                started.Signal();
                while (!stop.IsCancellationRequested)
                {
                    var t = gate.EnterShared();
                    Interlocked.Increment(ref entries);
                    Interlocked.Increment(ref inside);
                    if (Volatile.Read(ref exclusive) != 0)
                    {
                        Interlocked.Increment(ref violations);
                    }

                    Interlocked.Decrement(ref inside);
                    gate.ExitShared(t);
                }
            }, TaskCreationOptions.LongRunning);
        }

        var freezers = new Task[2];
        for (var i = 0; i < freezers.Length; i++)
        {
            freezers[i] = Task.Factory.StartNew(() =>
            {
                started.Wait();
                var first = Interlocked.Read(ref entries);
                var seen = first;
                for (var c = 0; c < Cycles; c++)
                {
                    // Yields, never sleeps (sleep1Threshold -1); bounded so a writer that can no longer enter fails the test instead of hanging it.
                    var spin = new SpinWait();
                    var deadline = Environment.TickCount64 + 2_000;
                    while (Interlocked.Read(ref entries) == seen && Environment.TickCount64 < deadline)
                    {
                        spin.SpinOnce(-1);
                    }

                    var now = Interlocked.Read(ref entries);
                    if (now == seen)
                    {
                        Interlocked.Increment(ref stalls);
                        break;
                    }

                    seen = now;
                    if (!gate.TryEnterExclusive(Long))
                    {
                        Interlocked.Increment(ref timeouts);
                        continue;
                    }

                    // Not SharedHolders(): a writer meeting the freeze counts on its stripe for an instant before it backs off.
                    if (Interlocked.Increment(ref exclusive) != 1 || Volatile.Read(ref inside) != 0)
                    {
                        Interlocked.Increment(ref violations);
                    }

                    Thread.SpinWait(20);
                    Interlocked.Decrement(ref exclusive);
                    gate.ExitExclusive();
                }

                Interlocked.Add(ref entriesDuringFreezes, Interlocked.Read(ref entries) - first);
            }, TaskCreationOptions.LongRunning);
        }

        var done = Task.WaitAll(freezers, 15_000);
        stop.Cancel();
        Assert.That(Task.WaitAll(writers, Long), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(done, Is.True, "the freezers did not finish");
            Assert.That(stalls, Is.Zero, "no writer entered for 2 s between two freezes");
            Assert.That(entriesDuringFreezes, Is.GreaterThanOrEqualTo(2 * Cycles), "premise: the writers ran between every two freezes");
            Assert.That(violations, Is.Zero, "BKP-01: a writer was inside an exclusive hold, or two freezers held it at once");
            Assert.That(timeouts, Is.Zero);
            Assert.That(gate.SharedHolders(), Is.Zero);
            Assert.That(gate.Waiting, Is.Zero);
        });
    }

    [Test]
    [CancelAfter(5_000)]
    public void SharedHold_CanBeReleasedByAnotherThread()
    {
        var gate = new DataFileWriteGate();
        var token = Task.Run(() => gate.EnterShared()).Result;

        Assert.That(gate.SharedHolders(), Is.EqualTo(1));
        Task.Run(() => gate.ExitShared(token)).Wait();
        Assert.That(gate.SharedHolders(), Is.Zero);
        Assert.That(gate.TryEnterExclusive(TimeSpan.FromMilliseconds(100)), Is.True);
        gate.ExitExclusive();
    }

    /// <summary>
    /// BKP-12 across retries: a writer held back by a failed exclusive attempt enters before the freezer's next attempt, however late it is scheduled
    /// (here, held in its back-off until the freezer is known to be waiting for it).
    /// </summary>
    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-12")]
    public void AWriterHeldBackByAFailedAttempt_EntersBeforeTheNextOne()
    {
        var gate = new DataFileWriteGate();
        var stuck = gate.EnterShared();
        using var backedOff = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        gate.BackedOffProbe = () =>
        {
            backedOff.Set();
            go.Wait();
        };

        long holdersAtRetry = -1;
        var freezer = Task.Factory.StartNew(() =>
        {
            Assert.That(gate.TryEnterExclusive(TimeSpan.FromMilliseconds(300)), Is.False, "premise: the stuck holder defeats the attempt");
            gate.AdmitWaiters(Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency);
            holdersAtRetry = gate.SharedHolders();
        }, TaskCreationOptions.LongRunning);

        var spin = new SpinWait();
        while (!gate.FreezePending)
        {
            spin.SpinOnce(-1);
        }

        DataFileWriteGate.Token writerToken = default;
        var writer = Task.Factory.StartNew(() => writerToken = gate.EnterShared(), TaskCreationOptions.LongRunning);
        Assert.That(backedOff.Wait(Long), Is.True, "premise: the writer met the attempt");
        Assert.That(gate.Waiting, Is.EqualTo(1));
        Assert.That(freezer.Wait(600), Is.False, "the freezer went on to its next attempt without waiting for the writer it held back");
        go.Set();
        Assert.That(freezer.Wait(Long) && writer.Wait(Long), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(holdersAtRetry, Is.EqualTo(2), "the held-back writer had not entered when the freezer retried");
            Assert.That(gate.Waiting, Is.Zero);
        });
        gate.ExitShared(writerToken);
        gate.ExitShared(stuck);
    }

    /// <summary>BKP-12, the freezer's half: an acquire that cannot drain gives up within its budget and leaves the gate open for writers.</summary>
    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("BKP-12")]
    public void ExclusiveTimeout_IsBounded_AndReopensTheGate()
    {
        var gate = new DataFileWriteGate();
        var stuck = gate.EnterShared();

        var sw = Stopwatch.StartNew();
        var acquired = gate.TryEnterExclusive(TimeSpan.FromMilliseconds(30));
        sw.Stop();

        var reentered = Task.Run(() =>
        {
            var t = gate.EnterShared();
            gate.ExitShared(t);
        }).Wait(Long);

        Assert.Multiple(() =>
        {
            Assert.That(acquired, Is.False);
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1_000), "the acquire must give up near its budget");
            Assert.That(reentered, Is.True, "a writer must get in once the freezer gave up");
        });
        gate.ExitShared(stuck);
    }
}
