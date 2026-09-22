using NUnit.Framework;
using System;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-04 — session requests: recorded per worker with no synchronization, applied single-threaded in a fixed order, and reset per tick without allocating.
/// </summary>
/// <remarks>
/// <para>
/// The determinism is the point. Systems run in parallel and several of them may steer one session in the same tick; writing the row from each would need a
/// lock per session and would still give whichever thread was quicker. Recording per worker and applying in worker-then-record order makes "last writer wins
/// per field" a property of the order rather than of the scheduler, which is what makes a replay of the same tick produce the same sessions.
/// </para>
/// <para>
/// The allocation case is not a micro-benchmark: this runs once per tick for the life of the process, so a per-tick allocation is a per-tick collection
/// pressure that shows up as tick-time jitter long before it shows up as memory.
/// </para>
/// </remarks>
[TestFixture]
class SessionRequestLogTests
{
    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;
    private SubscriptionsSessions _sessions;
    private SessionTable _table;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "SessionRequestLogTests" });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "SessionRequestLogTestAllocator" });
        _sessions = new SubscriptionsSessions();
        _table = new SessionTable("Sessions", _registry.Runtime, _allocator, new SubscriptionsOptions { MaxSessions = 8 }, _sessions.SessionEvents);
    }

    [TearDown]
    public void TearDown()
    {
        _table?.Dispose();
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    /// <summary>Admits a session and advances one tick, so it is open and walkable.</summary>
    private SessionId OpenSession()
    {
        Assert.That(_table.TryAdmit(_sessions, new AdmissionRequest(string.Empty, null, 0, ReadOnlySpan<byte>.Empty, null, null, null, "fake"),
            out var session, out _, out _), Is.True);
        _table.BeginTick();
        return session;
    }

    // ── what Phase 1 applies ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ProfileControlAndBudgetTakeEffectWhenTheLogIsApplied()
    {
        var session = OpenSession();
        var entity = EntityId.FromRaw(0x0000_1234_5678_0007L);
        var log = new SessionRequestLog(2);

        log.Request(0, session).Profile("player").Control(entity).SetBudget(48_000);

        Assert.That(_table.ProfileName(session), Is.Null, "a request is not a write — nothing happens until the session pass applies it");
        Assert.That(log.Apply(_table), Is.EqualTo(3));

        var controlled = _table.Row(session).Controlled;
        var budget = _table.Row(session).BytesPerSecond;

        Assert.Multiple(() =>
        {
            Assert.That(_table.ProfileName(session), Is.EqualTo("player"));
            Assert.That(controlled, Is.EqualTo(entity));
            Assert.That(budget, Is.EqualTo(48_000));
        });
    }

    [Test]
    public void KickClosesTheSessionWhenTheLogIsApplied()
    {
        var session = OpenSession();
        var log = new SessionRequestLog(1);

        log.Request(0, session).Kick(4200, "afk");

        Assert.That(_table.IsOpen(session), Is.True);
        Assert.That(log.Apply(_table), Is.EqualTo(1));
        Assert.That(_table.IsOpen(session), Is.False);

        _table.BeginTick();
        var batch = _table.Events.AsSpan();
        var e = batch[0];

        Assert.Multiple(() =>
        {
            Assert.That(e.Kind, Is.EqualTo(SessionEventKind.Closed));
            Assert.That(e.Reason, Is.EqualTo(SessionCloseReason.Kicked));
            Assert.That(e.CloseCode, Is.EqualTo(4200));
        });
    }

    [Test]
    [TestCase((ushort)1000)]
    [TestCase((ushort)1013)]
    [TestCase((ushort)4003)]
    [TestCase((ushort)4099)]
    [TestCase((ushort)5000)]
    public void KickOutsideTheApplicationRangeThrowsAtTheCallSite(ushort code)
    {
        var session = OpenSession();
        var log = new SessionRequestLog(1);

        Assert.That(() => log.Request(0, session).Kick(code), Throws.InstanceOf<ArgumentOutOfRangeException>(),
            "a lower code already means something to every SDK, and decides whether it comes back");
        Assert.That(log.Count, Is.Zero, "and nothing is recorded, so the refusal cannot be discovered a tick later");
    }

    // ── ordering ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void LastWriterWinsPerFieldInWorkerThenRecordOrder()
    {
        var session = OpenSession();
        var log = new SessionRequestLog(3);

        // Worker 2 records first in wall-clock terms; worker 0 records last. The apply order is the worker index, so worker 2's values must win regardless.
        log.Request(2, session).Profile("late-worker").SetBudget(3);
        log.Request(0, session).Profile("early-worker").SetBudget(1);
        log.Request(1, session).SetBudget(2);

        Assert.That(log.Apply(_table), Is.EqualTo(5));

        var budget = _table.Row(session).BytesPerSecond;

        Assert.Multiple(() =>
        {
            Assert.That(_table.ProfileName(session), Is.EqualTo("late-worker"), "the highest worker index applies last");
            Assert.That(budget, Is.EqualTo(3));
        });
    }

    [Test]
    public void WithinOneSegmentTheLastRecordWins()
    {
        var session = OpenSession();
        var log = new SessionRequestLog(1);

        log.Request(0, session).SetBudget(10);
        log.Request(0, session).SetBudget(20);
        log.Request(0, session).SetBudget(30);

        Assert.That(log.Apply(_table), Is.EqualTo(3));
        Assert.That(_table.Row(session).BytesPerSecond, Is.EqualTo(30));
    }

    [Test]
    public void ARequestForASessionThatHasGoneIsDroppedRatherThanThrowing()
    {
        var session = OpenSession();
        var log = new SessionRequestLog(1);

        log.Request(0, session).Profile("player");
        _table.Close(session, SessionCloseReason.ClientLeft, CloseCodes.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(log.Apply(_table), Is.Zero, "a client disconnecting mid-tick is normal, not an error");
            Assert.That(_table.ProfileName(session), Is.Null);
        });
    }

    // ── what a later phase builds ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The refusal belongs on the caller's stack. Recording the verb and throwing a tick later puts the exception inside the session pass, where nothing names
    /// the system that asked for it and the system's own tick has already finished — so the one piece of information the exception exists to carry is gone.
    /// </summary>
    [Test]
    public void ObserveAndUnobserveThrowAtTheCallSiteNamingPhase2()
    {
        var session = OpenSession();
        var log = new SessionRequestLog(1);

        Assert.Multiple(() =>
        {
            Assert.That(() => log.Request(0, session).Observe(NewWorldObserver(), 0),
                Throws.InstanceOf<NotSupportedException>().With.Message.Contains("Phase 2"));
            Assert.That(() => log.Request(0, session).Unobserve(1), Throws.InstanceOf<NotSupportedException>().With.Message.Contains("Phase 2"));
            Assert.That(log.Count, Is.Zero, "and nothing is recorded, so the refusal cannot be discovered a tick later");
        });

        Assert.That(log.Apply(_table), Is.Zero);
    }

    [Test]
    public void SetSourcesThrowsAtTheCallSiteNamingPhase4()
    {
        var session = OpenSession();
        var log = new SessionRequestLog(1);

        Assert.That(() => log.Request(0, session).SetSources(), Throws.InstanceOf<NotSupportedException>().With.Message.Contains("Phase 4"));

        Assert.Multiple(() =>
        {
            Assert.That(log.Count, Is.Zero);
            Assert.That(log.Apply(_table), Is.Zero);
        });
    }

    /// <summary>
    /// The apply pass empties its segments in a <see langword="finally"/>. Without it the first record that throws stays in the log for ever: every later tick
    /// replays every earlier record, throws at the same one, and the session pass never applies anything again. One bad request wedges the subsystem.
    /// </summary>
    [Test]
    public void ARecordThatThrowsStillLeavesTheLogEmpty()
    {
        var session = OpenSession();
        var log = new SessionRequestLog(2);

        log.Request(0, session).SetBudget(1234);

        // Past SessionRequest, which refuses this at the call site: the backstop inside Apply is what this is about, and it must not keep the records.
        log.Segment(1).Add(session, SessionRequestKind.SetSources, 0, Array.Empty<SourceDeclaration>());

        Assert.That(() => log.Apply(_table), Throws.InstanceOf<NotSupportedException>());

        Assert.Multiple(() =>
        {
            Assert.That(log.Count, Is.Zero, "a throw must not leave the tick's records behind for the next tick to replay");
            Assert.That(log.Segment(1).ObjectAt(0), Is.Null, "and the reset that drops the managed payloads has to have run too");
        });

        Assert.That(log.Apply(_table), Is.Zero, "so the next tick applies an empty log rather than throwing again");
    }

    // ── per-tick reset ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ApplyEmptiesTheLog()
    {
        var session = OpenSession();
        var log = new SessionRequestLog(2);

        log.Request(0, session).Profile("player");
        log.Request(1, session).SetBudget(7);

        Assert.That(log.Count, Is.EqualTo(2));
        log.Apply(_table);

        Assert.Multiple(() =>
        {
            Assert.That(log.Count, Is.Zero);
            Assert.That(log.Apply(_table), Is.Zero, "a second apply in the same tick must not replay last tick's requests");
        });
    }

    [Test]
    public void AResetSegmentDropsItsReferencesButKeepsItsCapacity()
    {
        var session = OpenSession();
        var log = new SessionRequestLog(1);
        var segment = log.Segment(0);

        for (var i = 0; i < 40; i++)
        {
            log.Request(0, session).Profile("player");
        }

        var recordCapacity = segment.RecordCapacity;
        var objectCapacity = segment.ObjectCapacity;
        log.Apply(_table);

        Assert.Multiple(() =>
        {
            Assert.That(segment.Count, Is.Zero);
            Assert.That(segment.RecordCapacity, Is.EqualTo(recordCapacity), "capacity is what makes the next tick free");
            Assert.That(segment.ObjectCapacity, Is.EqualTo(objectCapacity));
            Assert.That(segment.ObjectAt(0), Is.Null, "the managed payloads are dropped, or the busiest tick's names live for ever");
        });
    }

    /// <summary>
    /// Recording and applying a tick's requests must cost nothing on the GC heap once the segments have reached their high-water mark. This runs every tick
    /// of the process, so a single per-tick allocation is per-tick collection pressure.
    /// </summary>
    [Test]
    public void SteadyStateRecordingAndApplyAllocateNothing()
    {
        var session = OpenSession();
        var entity = EntityId.FromRaw(0x0000_0000_00AB_0003L);
        var log = new SessionRequestLog(4);

        for (var i = 0; i < 64; i++)
        {
            RecordOneTick(log, session, entity);
            log.Apply(_table);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 512; i++)
        {
            RecordOneTick(log, session, entity);
            log.Apply(_table);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.Zero, $"{allocated} bytes allocated over 512 ticks of session requests");
    }

    private static void RecordOneTick(SessionRequestLog log, SessionId session, EntityId entity)
    {
        log.Request(0, session).Profile("player");
        log.Request(1, session).Control(entity);
        log.Request(2, session).SetBudget(4096);
        log.Request(3, session).Profile("god");
    }

    private static ObserverDeclaration NewWorldObserver()
    {
        var profile = new ProfileDeclaration("scratch");
        new ProfileBuilder(profile).World();
        return profile.Observers[0];
    }
}
