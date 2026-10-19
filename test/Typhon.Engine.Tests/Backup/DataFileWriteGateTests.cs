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

    /// <summary>BKP-12, the freezer's half: an acquire that cannot drain gives up within its budget and leaves the gate open for writers.</summary>
    [Test]
    [CancelAfter(5_000)]
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
