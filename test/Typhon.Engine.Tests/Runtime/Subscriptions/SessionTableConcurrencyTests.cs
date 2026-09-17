using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-04 — the session table under two threads: a transport thread on its allow-list while the tick recycles slots underneath it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every bug this fixture exists for is silent.</b> None of them throws, none corrupts anything a debugger would show, and all of them are invisible in a
/// single-threaded test: a send counter that goes negative, a close that lands on the next occupant, an identity leased and never given back. What they have in
/// common is a check and a write that are two steps, with a recycle able to run in between — so the fixture's job is to make that interleaving happen often
/// enough to be caught, then to assert the property the interleaving would break.
/// </para>
/// <para>
/// <b>The transport loops run free; they are not lined up on a barrier.</b> A barrier looked like the way to narrow the window, and it is the opposite: the
/// thread that arrives last is the one that proceeds first, and the tick side always arrives last because it does the setup. Every "race" then resolved the
/// same way and the coverage counters came back zero. A loop spinning on a published identity while the tick churns the slot hits both orders within a few
/// iterations, which is why each case carries a counter asserting the window was actually entered — a stress test that silently stops reaching its race is
/// worse than no test, because it still reports green.
/// </para>
/// <para>
/// <b>Where the assertion is a liveness one, that is deliberate.</b> The send-counter bug does not produce a wrong value anybody reads; it produces a slot that
/// can never be recycled again. So the case asserts that the slot keeps coming back, iteration after iteration, on a table with exactly one of them: a counter
/// stuck below zero fails the very next admission and names the iteration it happened on.
/// </para>
/// <para>
/// Each case is budgeted well under 300 ms: threads are started once and reused, and the tick side is a few microseconds an iteration.
/// </para>
/// </remarks>
[TestFixture]
class SessionTableConcurrencyTests
{
    private const int Iterations = 1500;

    /// <summary>How long a slot is given to come back before the test calls it lost. A correct table needs one tick; this is margin.</summary>
    private const int RecycleSecondsBudget = 2;

    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;
    private SubscriptionsSessions _sessions;

    /// <summary>The identity the transport loop is currently hammering, published as a packed value. Zero means "nothing to do".</summary>
    private uint _target;

    private bool _stop;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "SessionTableConcurrencyTests" });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "SessionTableConcurrencyAllocator" });
        _sessions = new SubscriptionsSessions();
        _target = 0;
        _stop = false;
    }

    [TearDown]
    public void TearDown()
    {
        Volatile.Write(ref _stop, true);
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    private SessionTable NewTable(int maxSessions) =>
        new("Sessions", _registry.Runtime, _allocator, new SubscriptionsOptions { MaxSessions = maxSessions }, _sessions.SessionEvents);

    private bool Admit(SessionTable table, out SessionId session) =>
        table.TryAdmit(_sessions, new AdmissionRequest(string.Empty, null, 0, ReadOnlySpan<byte>.Empty, null, null, null, "fake"), out session, out _, out _);

    /// <summary>
    /// Ticks until the one-slot table has its slot back, which is the property every send-counter bug destroys.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="iteration">Which iteration, for the message.</param>
    /// <param name="what">What was racing, for the message.</param>
    /// <remarks>
    /// The budget is wall time, not a tick count. A correct table frees the slot on the first tick after the last send finishes, and sixty-four ticks is a few
    /// microseconds — shorter than one deschedule of a transport thread that is between its <c>BeginSend</c> and its <c>EndSend</c>, which made a tick-counted
    /// budget fail about half the runs for a reason that had nothing to do with the table. A deadline distinguishes "slower than expected" from "never", and it
    /// is only the second that this is looking for.
    /// </remarks>
    private static void TickUntilFree(SessionTable table, int iteration, string what)
    {
        var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * RecycleSecondsBudget);
        var spin = new SpinWait();
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (table.FreeCount == 1)
            {
                return;
            }

            table.BeginTick();
            spin.SpinOnce();
        }

        Assert.Fail($"iteration {iteration}: the slot never came back within {RecycleSecondsBudget} s ({what}). A send counter that went below zero can never "
            + "reach the 'no send in flight' test again, so the slot is lost for the life of the process and the table is permanently one session smaller.");
    }

    /// <summary>
    /// Starts the transport-side loops: two of them, spinning on the published identity until the test stops them.
    /// </summary>
    /// <param name="name">A name for the threads, so a hung run names the loop.</param>
    /// <param name="work">What a transport thread does with the identity.</param>
    /// <param name="count">How many loops. Two unless the case's window is narrow enough to need more readers in flight.</param>
    /// <returns>The threads, for <see cref="StopLoops"/>.</returns>
    /// <remarks>
    /// Two rather than one, measured: with a single loop the odds of it being parked exactly inside the window while the tick recycles are low enough that a
    /// reintroduced bug survived half the runs. A second loop roughly doubles the chance that one of them is mid-call at the moment that matters, at no cost to
    /// the fixture's wall time.
    /// </remarks>
    private Thread[] StartLoops(string name, Action<SessionId> work, int count = 2)
    {
        var threads = new Thread[count];
        for (var t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() =>
            {
                while (!Volatile.Read(ref _stop))
                {
                    var packed = Volatile.Read(ref _target);
                    if (packed != 0)
                    {
                        work(SessionId.FromValue(packed));
                    }
                }
            })
            {
                IsBackground = true,
                Name = $"{name}-{t}",
            };

            threads[t].Start();
        }

        return threads;
    }

    private void StopLoops(Thread[] threads)
    {
        Volatile.Write(ref _stop, true);
        foreach (var thread in threads)
        {
            Assert.That(thread.Join(TimeSpan.FromSeconds(10)), Is.True, $"{thread.Name} did not finish");
        }
    }

    // ── the send counter ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A transport thread marking a frame while the tick recycles the slot under it.
    /// </summary>
    /// <remarks>
    /// The bug this guards is terminal for the slot, not transient. Check the identity, then increment a separate counter, and a recycle landing between the
    /// two puts the increment on the next occupant — whose own publication has already reset the counter, so the correcting decrement takes it to −1. Recycling
    /// tests the counter against zero, so it never passes again: the slot is retried every tick for the life of the process and the table is one session
    /// smaller for ever. The identity and the counter live in one word precisely so that interleaving cannot be expressed.
    /// </remarks>
    [Test]
    public void ASendThatRacesARecycleNeverLosesTheSlot()
    {
        using var table = NewTable(1);

        var accepted = 0;
        var threads = StartLoops("session-table-send", session =>
        {
            if (!table.BeginSend(session))
            {
                return;
            }

            Interlocked.Increment(ref accepted);
            table.EndSend(session);

            // A gap, and a necessary one: a send in flight legitimately defers a recycle, so two loops issuing sends without pause keep the counter non-zero
            // for ever and the slot starves. That is not a defect in the table — the send pump stops producing frames for a session the tick has closed, so
            // the real system never does it — but without the gap this case measures the starvation instead of the race it was written for.
            Thread.SpinWait(64);
        });

        try
        {
            for (var i = 0; i < Iterations; i++)
            {
                Assert.That(Admit(table, out var session), Is.True, $"iteration {i}: the slot was not available");
                table.BeginTick();

                Volatile.Write(ref _target, session.Value);

                table.Close(session, SessionCloseReason.ClientLeft, CloseCodes.Normal);
                table.BeginTick();

                // The recycle runs while the transport loops are inside BeginSend with this identity, and they keep hammering it across the re-admission that
                // follows — which is where the damage lands: a counter re-armed by the new occupant's publication between an increment and its correcting
                // decrement ends up below zero, and the slot is then unrecyclable for ever.
                table.BeginTick();
                TickUntilFree(table, i, "a send racing the recycle");

                Assert.That(table.SendsInFlight(session), Is.Zero, $"iteration {i}: a dead identity reports no sends");
            }
        }
        finally
        {
            StopLoops(threads);
        }

        Assert.Multiple(() =>
        {
            Assert.That(table.FreeCount, Is.EqualTo(1), "the table ends with the slot it started with");
            Assert.That(accepted, Is.GreaterThan(0), "no send was ever accepted, so the race window was never entered and this asserted nothing");
        });
    }

    /// <summary>
    /// A send accepted on another thread holds the slot: the recycle must lose, and the slot must come back on the first tick after the send finishes.
    /// </summary>
    /// <remarks>
    /// Phased on a barrier rather than raced, because this one is about the outcome being certain: the send is in flight before the tick runs, so a slot that
    /// came back anyway would be a frame still on a socket whose session had been handed to somebody else.
    /// </remarks>
    [Test]
    public void ARecycleWaitsForASendTakenOnAnotherThread()
    {
        const int Rounds = 200;

        using var table = NewTable(1);

        var barrier = new Barrier(2);
        var taken = 0;
        var session = SessionId.None;
        Exception failure = null;

        var transport = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < Rounds; i++)
                {
                    barrier.SignalAndWait();
                    Assert.That(table.BeginSend(session), Is.True, "the session is closed but not recycled, so a send is still legal");
                    Interlocked.Increment(ref taken);

                    barrier.SignalAndWait();
                    barrier.SignalAndWait();
                    table.EndSend(session);
                    barrier.SignalAndWait();
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
        })
        {
            IsBackground = true,
            Name = "session-table-hold",
        };

        transport.Start();

        for (var i = 0; i < Rounds; i++)
        {
            Assert.That(Admit(table, out session), Is.True, $"iteration {i}");
            table.BeginTick();
            table.Close(session, SessionCloseReason.Lagging, CloseCodes.TryAgainLater);
            table.BeginTick();

            // Round 1: the transport takes the send. This thread waits for it at the next barrier.
            barrier.SignalAndWait();
            barrier.SignalAndWait();

            table.BeginTick();
            Assert.That(table.FreeCount, Is.Zero, $"iteration {i}: a slot recycled under a frame that is still on a socket is a use-after-free");

            // Round 3: the transport finishes the send.
            barrier.SignalAndWait();
            barrier.SignalAndWait();

            table.BeginTick();
            Assert.That(table.FreeCount, Is.EqualTo(1), $"iteration {i}: and it comes back on the first tick after the last send finished");
        }

        Assert.That(transport.Join(TimeSpan.FromSeconds(10)), Is.True, "the transport loop did not finish");

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Null);
            Assert.That(taken, Is.EqualTo(Rounds));
        });
    }

    // ── the close request ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A stale identity asking for a close while its slot is handed to the next client.
    /// </summary>
    /// <remarks>
    /// Validating the generation and then compare-exchanging a <i>separate</i> word lets the recycle and the re-admission land in between, and the request then
    /// closes the new occupant with the previous client's code and reason. The new client is disconnected for something another connection did, and the close
    /// is indistinguishable from a legitimate one at every layer above. The identity travels inside the compare-exchange so the two cannot come apart.
    /// </remarks>
    [Test]
    public void AStaleCloseRequestCannotCloseTheNextOccupant()
    {
        using var table = NewTable(1);

        var accepted = 0;
        var attempts = 0;
        var threads = StartLoops("session-table-close", session =>
        {
            Interlocked.Increment(ref attempts);
            if (table.RequestClose(session, SessionCloseReason.ProtocolError, CloseCodes.ProtocolError))
            {
                Interlocked.Increment(ref accepted);
            }
        });

        try
        {
            for (var i = 0; i < Iterations; i++)
            {
                Assert.That(Admit(table, out var stale), Is.True, $"iteration {i}");
                table.BeginTick();

                // Published while the identity is still live, so the loop is hammering RequestClose across the whole close-deliver-recycle-readmit sequence
                // rather than being handed a window one line wide. The first call is accepted and the rest are refused until the slot is recycled; from then
                // on every one of them must be refused because the identity is dead, which is the property under test.
                Volatile.Write(ref _target, stale.Value);

                table.Close(stale, SessionCloseReason.ClientLeft, CloseCodes.Normal);
                table.BeginTick();
                table.BeginTick();
                TickUntilFree(table, i, "a close request racing the recycle");

                Assert.That(Admit(table, out var fresh), Is.True, $"iteration {i}: the slot came back");
                table.BeginTick();

                Assert.That(table.ApplyPendingCloses(), Is.Zero,
                    $"iteration {i}: the new occupant inherited the previous client's close — 1002 for something another connection did");
                Assert.That(table.IsOpen(fresh), Is.True, $"iteration {i}");

                Volatile.Write(ref _target, 0);

                table.Close(fresh, SessionCloseReason.ClientLeft, CloseCodes.Normal);
                table.BeginTick();
                table.BeginTick();
            }
        }
        finally
        {
            StopLoops(threads);
        }

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.GreaterThan(0), "no close request was ever accepted, so the race window was never entered");
            Assert.That(attempts, Is.GreaterThan(Iterations), "the loop has to have run across the recycle boundary, not only before it");
        });
    }

    // ── the side arrays ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A reader asking a stale identity for its application data while the slot is recycled and re-admitted.
    /// </summary>
    /// <remarks>
    /// The managed side arrays are keyed by SLOT, and a slot outlives its occupant. Checking the identity only before the array read hands the caller whatever
    /// the next client put there — which is how a system possessing an avatar ends up holding another player's state, with no error anywhere. The answer must
    /// be this session's object or nothing.
    /// </remarks>
    [Test]
    public void AStaleReadOfTheSideArraysNeverReturnsAnotherClientsObject()
    {
        using var table = NewTable(1);

        var reads = 0;
        var wrong = 0;

        // The two clients' objects are told apart by TYPE rather than by reference against a field the tick keeps moving. Comparing against "the object of the
        // iteration currently running" is a race in the test itself: the reader can be descheduled between its read and that comparison, and then fails for
        // having read the right object one iteration late. The type says what the property is — a stale identity never sees the NEXT client's data.
        var threads = StartLoops("session-table-appdata", session =>
        {
            var read = table.AppData(session);
            if (read == null)
            {
                return;
            }

            Interlocked.Increment(ref reads);
            if (read is TheNextClientsData)
            {
                Interlocked.Increment(ref wrong);
            }
        },
        count: 4);

        try
        {
            for (var i = 0; i < Iterations; i++)
            {
                var mine = new TheStaleClientsData();
                _sessions.Admit = (in AdmissionRequest _) => Admission.Accept(SessionRole.Player, null, mine);

                Assert.That(Admit(table, out var stale), Is.True, $"iteration {i}");
                table.BeginTick();

                // Published while the session is still open: the window the reader has to be caught in is the one that starts here and ends at the recycle,
                // and it is a few microseconds wide. Publishing it one line before the recycle leaves the reader no chance at all — measured, not assumed.
                Volatile.Write(ref _target, stale.Value);

                table.Close(stale, SessionCloseReason.ClientLeft, CloseCodes.Normal);
                table.BeginTick();

                var theirs = new TheNextClientsData();
                _sessions.Admit = (in AdmissionRequest _) => Admission.Accept(SessionRole.Player, null, theirs);

                table.BeginTick();
                TickUntilFree(table, i, "a side-array read racing the recycle");

                Assert.That(Admit(table, out var fresh), Is.True, $"iteration {i}");
                table.BeginTick();

                Assert.That(table.AppData(fresh), Is.SameAs(theirs), $"iteration {i}");

                Volatile.Write(ref _target, 0);

                table.Close(fresh, SessionCloseReason.ClientLeft, CloseCodes.Normal);
                table.BeginTick();
                table.BeginTick();
            }
        }
        finally
        {
            StopLoops(threads);
        }

        Assert.Multiple(() =>
        {
            Assert.That(wrong, Is.Zero, "a stale identity read the next client's application data");
            Assert.That(reads, Is.GreaterThan(0), "every read came back null, so the race window was never entered");
        });
    }

    // ── the free stack ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Threads racing for the last identity: exactly one may have it.
    /// </summary>
    /// <remarks>
    /// The pop is lock-free and stamped against ABA, which is what makes two connecting clients safe. The push is not: it writes the entry and then publishes
    /// the count, so two pushers racing would write the same index, lose one identity and publish the other twice — one slot handed to two connections, which
    /// is the worst outcome this structure has. <c>ReleaseLease</c> makes a transport thread a pusher, so both halves are exercised here.
    /// </remarks>
    [Test]
    public void ThreadsRacingForTheLastIdentityCannotBothWinIt()
    {
        const int Threads = 4;

        using var table = NewTable(1);

        var winners = 0;
        var collisions = 0;
        var threads = new Thread[Threads];
        var failures = new Exception[Threads];

        for (var t = 0; t < Threads; t++)
        {
            var index = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < Iterations; i++)
                    {
                        if (!table.TryLease(out var leased))
                        {
                            continue;
                        }

                        Interlocked.Increment(ref winners);

                        // A second winner of the same slot would have failed the exchange inside TryLease, which asserts in Debug.
                        if (leased.Slot != 0)
                        {
                            Interlocked.Increment(ref collisions);
                        }

                        Assert.That(table.ReleaseLease(leased), Is.True, "the thread that leased it is the one releasing it");
                    }
                }
                catch (Exception e)
                {
                    failures[index] = e;
                }
            })
            {
                IsBackground = true,
                Name = $"session-table-lease-{index}",
            };
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            Assert.That(thread.Join(TimeSpan.FromSeconds(10)), Is.True, "a lease loop did not finish");
        }

        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.All.Null, "a double lease of one slot fails the exchange inside TryLease");
            Assert.That(collisions, Is.Zero);
            Assert.That(winners, Is.GreaterThan(0), "nobody ever won the slot, so this asserted nothing");
            Assert.That(table.FreeCount, Is.EqualTo(1), "one slot, still one slot: no identity was lost and none was duplicated");
        });
    }

    /// <summary>
    /// Leases and releases from several threads against a table with room, asserting the free stack neither loses nor duplicates an identity.
    /// </summary>
    /// <remarks>
    /// A lost identity shrinks the table silently; a duplicated one hands one row to two clients, and every later read of that row belongs to whichever of them
    /// wrote last. Counting the free slots at the end catches the first; leasing every slot afterwards and comparing the set catches the second.
    /// </remarks>
    [Test]
    public void ConcurrentLeasesAndReleasesNeitherLoseNorDuplicateAnIdentity()
    {
        const int Threads = 4;
        const int Slots = 16;

        using var table = NewTable(Slots);

        var threads = new Thread[Threads];
        var failures = new Exception[Threads];

        for (var t = 0; t < Threads; t++)
        {
            var index = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < Iterations; i++)
                    {
                        if (table.TryLease(out var leased))
                        {
                            Assert.That(table.ReleaseLease(leased), Is.True);
                        }
                    }
                }
                catch (Exception e)
                {
                    failures[index] = e;
                }
            })
            {
                IsBackground = true,
                Name = $"session-table-churn-{index}",
            };
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            Assert.That(thread.Join(TimeSpan.FromSeconds(10)), Is.True, "a churn loop did not finish");
        }

        Assert.That(failures, Is.All.Null);
        Assert.That(table.FreeCount, Is.EqualTo(Slots), "every identity came back exactly once");

        var seen = new bool[Slots];
        for (var i = 0; i < Slots; i++)
        {
            Assert.That(table.TryLease(out var leased), Is.True, $"slot {i} of {Slots} is missing from the free stack");
            Assert.That(seen[leased.Slot], Is.False, "one slot was on the free stack twice");
            seen[leased.Slot] = true;
        }

        Assert.That(table.TryLease(out _), Is.False, "and there was nothing extra on it");
    }

    // ── disposal ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Disposal while transport threads are inside the table.
    /// </summary>
    /// <remarks>
    /// The rows are native memory and the transport members are documented as callable from any thread, so freeing the block on the strength of a plain flag
    /// read is a use-after-free with a race in front of it — the shape of the SWG x64 <c>0x80131506</c> crash, which is why this is a test and not a comment.
    /// The caller gate makes the free wait for the callers already inside, and turns every later call into "gone".
    /// </remarks>
    [Test]
    public void DisposingWhileTransportThreadsAreInsideIsSafe()
    {
        const int Threads = 4;

        var table = NewTable(32);
        Assert.That(Admit(table, out var session), Is.True);
        table.BeginTick();

        var running = new CountdownEvent(Threads);
        var threads = new Thread[Threads];
        var failures = new Exception[Threads];

        for (var t = 0; t < Threads; t++)
        {
            var index = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    running.Signal();
                    while (!Volatile.Read(ref _stop))
                    {
                        if (table.BeginSend(session))
                        {
                            table.EndSend(session);
                        }

                        table.RequestClose(session, SessionCloseReason.LinkLost, CloseCodes.GoingAway);
                        if (table.TryLease(out var leased))
                        {
                            table.ReleaseLease(leased);
                        }

                        _ = table.SendsInFlight(session);
                        _ = table.AppData(session);
                    }
                }
                catch (Exception e)
                {
                    failures[index] = e;
                }
            })
            {
                IsBackground = true,
                Name = $"session-table-dispose-{index}",
            };
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        Assert.That(running.Wait(TimeSpan.FromSeconds(5)), Is.True, "the transport loops did not start");
        Spin();

        table.Dispose();

        // The loops keep calling AFTER the free: every one of those calls has to be refused by the latch rather than reach a freed pointer.
        Spin();
        Volatile.Write(ref _stop, true);

        foreach (var thread in threads)
        {
            Assert.That(thread.Join(TimeSpan.FromSeconds(10)), Is.True, "a transport loop did not finish");
        }

        Assert.That(failures, Is.All.Null, "a call arriving after disposal answers 'gone'; it never dereferences the freed block");
    }

    /// <summary>What the admission hook attaches to the session that is about to be recycled.</summary>
    private sealed class TheStaleClientsData;

    /// <summary>What it attaches to the client that takes the slot next. A stale identity must never be handed one of these.</summary>
    private sealed class TheNextClientsData;

    /// <summary>A short busy wait, so the loops get a turn without a sleep whose granularity is 15 ms.</summary>
    private static void Spin()
    {
        for (var i = 0; i < 400; i++)
        {
            Thread.SpinWait(500);
        }
    }
}
