using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-06 — rule <c>SUB-05</c>: session state has one writer, the tick, and a transport thread's whole write set is the lease, the pre-publication row, and two
/// interlocked words.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a field diff and not a lock.</b> Every replication stage reads session rows with no synchronization at all, because the rule says nothing but the
/// tick writes them. That claim is not enforceable by a type — a row is a struct in native memory and any code holding the table can write any field of it — so
/// the verifier is an observation: drive the whole transport-side path with the tick quiescent, and require that the only fields that moved are the two the
/// rule allows. A violation is otherwise invisible: the write lands, nothing throws, and the stage that read the torn value replicates it.
/// </para>
/// <para>
/// <b>The concurrency half is the backstop.</b> <see cref="ReplicationThreadAffinity"/> is <c>[Conditional("DEBUG")]</c>, so the guarded tick-side members
/// detect an overlapping caller only in a Debug build. Running the two loops together therefore adds a check in Debug and costs nothing in Release, where the
/// field diff above is still the assertion that carries the rule — deliberately, so this fixture cannot be green in Debug and meaningless in Release.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class SessionWriterOwnershipTests
{
    private const int MaxSessions = 64;

    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;
    private SubscriptionsSessions _sessions;
    private SessionTable _table;
    private FakeSubscriptionsHost _host;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "SessionWriterOwnershipTests" });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "SessionWriterOwnershipAllocator" });
        _sessions = new SubscriptionsSessions();
        _table = new SessionTable("Sessions", _registry.Runtime, _allocator, new SubscriptionsOptions { MaxSessions = MaxSessions }, _sessions.SessionEvents);
        _host = new FakeSubscriptionsHost { Sessions = _sessions, SessionTable = _table };
    }

    [TearDown]
    public void TearDown()
    {
        _table?.Dispose();
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    [Test]
    [VerifiesRule("SUB-05")]
    public void TransportThreadTouchesOnlyItsAllowList()
    {
        var link = new InProcessLink();
        var connection = Accept(link);

        // Everything a transport thread does to the table, run from a thread that is not the tick's: lease a slot and publish a row (admission), then only
        // the two interlocked words. Every step is snapshotted, so a write to a tick-owned field is attributed to the call that made it.
        RunOffTick(() => connection.OnMessage(ClientMessages.Hello()));
        Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Open));

        var session = connection.Session;
        _table.BeginTick();

        var afterOpen = _table.Row(session);

        RunOffTick(() => connection.OnMessage(ClientMessages.Ping(1, 100)));
        AssertOnlyAllowListChanged(afterOpen, _table.Row(session), "PING");

        RunOffTick(() =>
        {
            Assert.That(_table.BeginSend(session), Is.True);
            _table.EndSend(session);
        });
        AssertOnlyAllowListChanged(afterOpen, _table.Row(session), "BeginSend/EndSend");

        RunOffTick(() => connection.OnMessage(ClientMessages.Bye()));
        var afterBye = _table.Row(session);
        AssertOnlyAllowListChanged(afterOpen, afterBye, "BYE");

        Assert.Multiple(() =>
        {
            Assert.That(afterBye.PendingClose, Is.Not.Zero, "the close is a REQUEST — one interlocked word — and the tick is what performs it");
            Assert.That(afterBye.State, Is.EqualTo((int)SessionSlotState.Open), "so the state is still the tick's to change");
            Assert.That(afterBye.CloseCode, Is.Zero);
        });

        Assert.That(_table.ApplyPendingCloses(), Is.EqualTo(1), "and the tick performs it");

        // The backstop: tick-side members and the transport path running at the same time. Nothing the transport does enters a guarded member, so the
        // Debug re-entrancy guard must never fire, and no session may end up in an impossible state.
        AssertTickAndTransportRunTogether();
    }

    [Test]
    [RuleMutant("SUB-05")]
    public void ARogueTransportThatWritesATickOwnedFieldIsDetected()
        => RuleMutants.AssertDetects("SUB-05", AllowListMarker, () =>
        {
            var link = new InProcessLink();
            var connection = Accept(link);
            connection.OnMessage(ClientMessages.Hello());
            var session = connection.Session;
            _table.BeginTick();

            var before = _table.Row(session);

            // A budget change is an ordinary, correct engine operation — from the TICK, out of the session request log. Reached from a transport thread it is
            // exactly the violation SUB-05 names, and it is silent: the write succeeds, and a stage mid-read sees half of it.
            RunOffTick(() => _table.SetBudget(session, 4242));

            AssertOnlyAllowListChanged(before, _table.Row(session), "a rogue SetBudget");
        });

    // ── the verifier's own assertion ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The marker the mutant looks for, so "the mutant failed" cannot be confused with "the mutant failed for the right reason".</summary>
    private const string AllowListMarker = "SUB-05: a transport thread wrote";

    /// <summary>
    /// Asserts that nothing outside the rule's allow-list moved.
    /// </summary>
    /// <param name="before">The row before the transport-side work.</param>
    /// <param name="after">The row after it.</param>
    /// <param name="what">What the transport did, for the message.</param>
    private static void AssertOnlyAllowListChanged(in SessionRowView before, in SessionRowView after, string what)
    {
        Check(before.IdValue, after.IdValue, nameof(SessionRow.IdValue), what);
        Check(before.State, after.State, nameof(SessionRow.State), what);
        Check(before.Role, after.Role, nameof(SessionRow.Role), what);
        Check(before.Flags, after.Flags, nameof(SessionRow.Flags), what);
        Check(before.CloseCode, after.CloseCode, nameof(SessionRow.CloseCode), what);
        Check(before.CloseReason, after.CloseReason, nameof(SessionRow.CloseReason), what);
        Check(before.Resumable, after.Resumable, nameof(SessionRow.Resumable), what);
        Check(before.BytesPerSecond, after.BytesPerSecond, nameof(SessionRow.BytesPerSecond), what);
        Check(before.MaxObservers, after.MaxObservers, nameof(SessionRow.MaxObservers), what);
        Check(before.MaxKnownEntities, after.MaxKnownEntities, nameof(SessionRow.MaxKnownEntities), what);
        Check(before.FrameBytes, after.FrameBytes, nameof(SessionRow.FrameBytes), what);
        Check(before.ClientMessageBytes, after.ClientMessageBytes, nameof(SessionRow.ClientMessageBytes), what);
        Check(before.Controlled.EntityKey, after.Controlled.EntityKey, nameof(SessionRow.Controlled), what);
    }

    private static void Check<T>(T before, T after, string field, string what)
    {
        if (!Equals(before, after))
        {
            Assert.Fail($"{AllowListMarker} {field} ({before} -> {after}) during {what}. A published session row belongs to the tick; a transport thread may "
                + $"touch only {nameof(SessionRow.SendsInFlight)} and {nameof(SessionRow.PendingClose)}, both interlocked. Every replication stage reads "
                + "these rows unsynchronized on the strength of that.");
        }
    }

    // ── the concurrency backstop ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void AssertTickAndTransportRunTogether()
    {
        const int Connections = 300;

        var failures = new ConcurrentQueue<Exception>();
        var stop = new ManualResetEventSlim(false);
        var admitted = 0;

        var tick = new Thread(() =>
        {
            try
            {
                while (!stop.IsSet)
                {
                    _table.BeginTick();
                    _table.ApplyPendingCloses();
                }

                // Two more passes so every close requested at the very end is applied and delivered.
                for (var i = 0; i < 4; i++)
                {
                    _table.BeginTick();
                    _table.ApplyPendingCloses();
                }
            }
            catch (Exception e)
            {
                failures.Enqueue(e);
            }
        })
        {
            IsBackground = true,
            Name = "subscriptions-tick",
        };

        var transport = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < Connections; i++)
                {
                    var link = new InProcessLink();
                    using var connection = Accept(link);
                    connection.OnMessage(ClientMessages.Hello());
                    if (connection.State != SubscriptionConnectionState.Open)
                    {
                        // 1013: the table was momentarily full because the tick had not recycled yet. A refusal is a correct outcome, not a race.
                        continue;
                    }

                    admitted++;
                    connection.OnMessage(ClientMessages.Ping((uint)i, (uint)i));
                    connection.OnMessage(ClientMessages.Bye());
                }
            }
            catch (Exception e)
            {
                failures.Enqueue(e);
            }
        })
        {
            IsBackground = true,
            Name = "subscriptions-transport",
        };

        tick.Start();
        transport.Start();

        Assert.That(transport.Join(TimeSpan.FromSeconds(15)), Is.True, "the transport loop did not finish");
        stop.Set();
        Assert.That(tick.Join(TimeSpan.FromSeconds(15)), Is.True, "the tick loop did not finish");

        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Empty,
                "nothing a transport thread does enters a guarded tick-side member, so the Debug re-entrancy guard must never fire");
            Assert.That(admitted, Is.GreaterThan(0), "the loop has to have admitted something for this to have tested anything");
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private SubscriptionConnection Accept(InProcessLink link)
    {
        var connection = new SubscriptionConnection(_host, link, new LinkInfo { Transport = "fake" }, Timeout.InfiniteTimeSpan);
        link.Connection = connection;
        return connection;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a thread that is not the one driving the tick, which is the whole point: a transport thread is never a worker.
    /// </summary>
    /// <param name="work">What the transport does.</param>
    private static void RunOffTick(Action work)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                failure = e;
            }
        })
        {
            IsBackground = true,
            Name = "subscriptions-transport",
        };

        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("the transport-side work did not finish");
        }

        if (failure != null)
        {
            throw failure;
        }
    }
}
