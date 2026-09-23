using NUnit.Framework;
using System;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-04 — the session table: identity that survives a recycled slot, rows in native memory, and a lifecycle the tick can see.
/// </summary>
/// <remarks>
/// <para>
/// The properties worth pinning here are the ones a later slice would otherwise break silently. A slot that came back one tick early would let a stale
/// <see cref="SessionId"/> — written into a component by the system that possessed an avatar — address whichever client now occupies it, and nothing in the
/// type system would notice. A generation that failed to rise would do the same thing permanently.
/// </para>
/// <para>
/// These run against a real <see cref="ResourceRegistry"/> and <see cref="MemoryAllocator"/>, because "the rows are native" is only observable through the
/// allocator's own counters and through surviving a collection.
/// </para>
/// </remarks>
[TestFixture]
unsafe class SessionTableTests
{
    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;
    private SubscriptionsSessions _sessions;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "SessionTableTests" });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "SessionTableTestAllocator" });
        _sessions = new SubscriptionsSessions();
    }

    [TearDown]
    public void TearDown()
    {
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    private SessionTable NewTable(int maxSessions = 8, string id = "Sessions") =>
        new(id, _registry.Runtime, _allocator, new SubscriptionsOptions { MaxSessions = maxSessions }, _sessions.SessionEvents);

    private static AdmissionRequest Request(string kind = "") =>
        new(kind, "token", 0, ReadOnlySpan<byte>.Empty, null, null, null, "fake");

    private bool Admit(SessionTable table, out SessionId session, out ushort code) =>
        table.TryAdmit(_sessions, Request(), out session, out code, out _);

    // ── identity ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void SlotAndGenerationRoundTripThroughThePackedValue()
    {
        var id = new SessionId(40_000, 5_000);

        Assert.Multiple(() =>
        {
            Assert.That(id.Slot, Is.EqualTo(40_000));
            Assert.That(id.Generation, Is.EqualTo(5_000));
            Assert.That(SessionId.FromValue(id.Value), Is.EqualTo(id), "the packed value is the whole identity");
            Assert.That(id.Value, Is.EqualTo(40_000u | (5_000u << 16)), "slot low, generation high — a client reads the same u32 off the wire");
        });
    }

    [Test]
    public void TheTopOfBothHalvesSurvivesThePacking()
    {
        var id = new SessionId(ushort.MaxValue, ushort.MaxValue);

        Assert.Multiple(() =>
        {
            Assert.That(id.Slot, Is.EqualTo(ushort.MaxValue));
            Assert.That(id.Generation, Is.EqualTo(ushort.MaxValue));
            Assert.That(id.Value, Is.EqualTo(uint.MaxValue));
        });
    }

    [Test]
    public void ADefaultIdentityNamesNoSession()
    {
        Assert.Multiple(() =>
        {
            Assert.That(default(SessionId), Is.EqualTo(SessionId.None));
            Assert.That(SessionId.None.IsValid, Is.False, "a zero generation is never issued, which is what makes default mean 'none'");
            Assert.That(new SessionId(0, 1).IsValid, Is.True, "slot zero is a real slot");
            Assert.That(new SessionId(0, 1), Is.Not.EqualTo(SessionId.None));
            Assert.That(SessionId.None.ToString(), Is.EqualTo("session none"));
            Assert.That(new SessionId(7, 3).ToString(), Is.EqualTo("session 7.3"), "a log that shows only the slot cannot tell two occupants apart");
        });
    }

    // ── native memory ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ARowIsExactlyOneCacheLine()
    {
        Assert.Multiple(() =>
        {
            Assert.That(sizeof(SessionRow), Is.EqualTo(64), "a row spanning two lines would double the cost of every per-session walk");
            Assert.That(SessionTable.RowBytes, Is.EqualTo(64));
        });
    }

    [Test]
    public void TheTableTakesExactlyOneNativeAllocationAndNoGcArrayForItsRows()
    {
        using var table = NewTable(64);

        Assert.Multiple(() =>
        {
            Assert.That(_allocator.PinnedLiveBlocks, Is.EqualTo(1), "rows and the free stack come from one pinned native block");
            Assert.That(table.Capacity, Is.EqualTo(64));
            Assert.That(table.FreeCount, Is.EqualTo(64));
        });
    }

    /// <summary>
    /// The rows are native, so a collection must not disturb them. This is the property whose absence was the SWG x64 <c>0x80131506</c> crash: a pinned
    /// object-heap array stops the GC moving a buffer, not freeing it.
    /// </summary>
    [Test]
    public void RowContentsSurviveAGarbageCollection()
    {
        using var table = NewTable();
        _sessions.Admit = static (in AdmissionRequest _) => Admission.Accept(SessionRole.Player, new SessionLimits { BytesPerSecond = 4096 });

        Assert.That(Admit(table, out var session, out _), Is.True);

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        var row = table.Row(session);

        Assert.Multiple(() =>
        {
            Assert.That(row.IdValue, Is.EqualTo(session.Value), "the row must still be the one that was written — the memory is not GC memory");
            Assert.That(row.BytesPerSecond, Is.EqualTo(4096));
            Assert.That(row.Role, Is.EqualTo((byte)SessionRole.Player));
        });
    }

    // ── admission and capacity ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void TheFirstLeaseTakesSlotZeroAtGenerationOne()
    {
        using var table = NewTable();

        Assert.That(table.TryLease(out var leased), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(leased.Slot, Is.Zero);
            Assert.That(leased.Generation, Is.EqualTo(1));
            Assert.That(table.FreeCount, Is.EqualTo(7), "a lease takes a slot out of circulation before the row is written");
        });
    }

    [Test]
    public void MaxSessionsRefusesFurtherSessionsWithTryAgainLater()
    {
        using var table = NewTable(2);

        Assert.Multiple(() =>
        {
            Assert.That(Admit(table, out _, out _), Is.True);
            Assert.That(Admit(table, out _, out _), Is.True);
        });

        Assert.That(Admit(table, out var refused, out var code), Is.False);

        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(CloseCodes.TryAgainLater), "1013 tells the SDK to come back with backoff; 4003 would tell it never to");
            Assert.That(code, Is.EqualTo(1013));
            Assert.That(refused, Is.EqualTo(SessionId.None));
            Assert.That(table.FreeCount, Is.Zero);
        });
    }

    [Test]
    public void ARefusedAdmissionCostsNoSlot()
    {
        using var table = NewTable(2);
        _sessions.Kinds("player");
        _sessions.Admit = static (in AdmissionRequest _) => Admission.Reject(4321, "not today");

        Assert.That(table.TryAdmit(_sessions, new AdmissionRequest("player", null, 0, ReadOnlySpan<byte>.Empty, null, null, null, "fake"),
            out _, out var code, out _), Is.False);

        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(4321));
            Assert.That(table.FreeCount, Is.EqualTo(2), "capacity is checked after the hook, so a refusal never consumes a slot");
        });
    }

    // ── lifecycle ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void OpenedIsDeliveredInTheTickThatFollowsAdmission()
    {
        using var table = NewTable();
        _sessions.Admit = static (in AdmissionRequest _) => Admission.Accept(SessionRole.Spectator);

        Assert.That(Admit(table, out var session, out _), Is.True);
        Assert.That(table.OpenCount, Is.Zero, "a session is not part of the tick's work until its Opened event has been delivered");

        Assert.That(table.BeginTick(), Is.EqualTo(1));

        var length = table.Events.Count;
        var opened = table.Events.AsSpan()[0];

        Assert.Multiple(() =>
        {
            Assert.That(length, Is.EqualTo(1));
            Assert.That(opened.Kind, Is.EqualTo(SessionEventKind.Opened));
            Assert.That(opened.Session, Is.EqualTo(session));
            Assert.That(table.OpenCount, Is.EqualTo(1));
            Assert.That(table.IsOpen(session), Is.True);
        });
    }

    [Test]
    public void ASlotIsNotReusedBeforeItsClosedEventWasDelivered()
    {
        using var table = NewTable(1);

        Assert.That(Admit(table, out var session, out _), Is.True);
        table.BeginTick();

        Assert.That(table.Close(session, SessionCloseReason.ClientLeft, CloseCodes.Normal), Is.True);
        Assert.That(table.TryLease(out _), Is.False, "the slot is still taken while the Closed event is only queued");

        var delivered = table.BeginTick();
        var closed = table.Events.AsSpan()[0];

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.EqualTo(1));
            Assert.That(closed.Kind, Is.EqualTo(SessionEventKind.Closed));
            Assert.That(closed.Reason, Is.EqualTo(SessionCloseReason.ClientLeft));
            Assert.That(table.IsLive(session), Is.True, "an application reading the event this tick must still be able to look the session up");
            Assert.That(table.TryLease(out _), Is.False, "and the slot must not have come back underneath it");
            Assert.That(table.OpenCount, Is.Zero, "though it is out of the tick's work at once");
        });

        table.BeginTick();

        Assert.Multiple(() =>
        {
            Assert.That(table.TryLease(out _), Is.True, "the tick after delivery, the slot comes back");
            Assert.That(table.IsLive(session), Is.False, "and the old identity no longer names it");
        });
    }

    [Test]
    public void TheGenerationRisesWhenASlotIsHandedOutAgain()
    {
        using var table = NewTable(1);

        Assert.That(Admit(table, out var first, out _), Is.True);
        table.BeginTick();
        table.Close(first, SessionCloseReason.LinkLost, CloseCodes.GoingAway);
        table.BeginTick();
        table.BeginTick();

        Assert.That(Admit(table, out var second, out _), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(second.Slot, Is.EqualTo(first.Slot), "the same row");
            Assert.That(second.Generation, Is.EqualTo(first.Generation + 1), "a different occupant");
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(table.IsLive(first), Is.False, "a stale identity names nobody, rather than naming the new client");
        });
    }

    [Test]
    public void ASlotIsNotReusedWhileASendIsInFlight()
    {
        using var table = NewTable(1);

        Assert.That(Admit(table, out var session, out _), Is.True);
        table.BeginTick();

        Assert.That(table.BeginSend(session), Is.True);
        table.Close(session, SessionCloseReason.Lagging, CloseCodes.TryAgainLater);
        table.BeginTick();
        table.BeginTick();

        Assert.Multiple(() =>
        {
            Assert.That(table.SendsInFlight(session), Is.EqualTo(1));
            Assert.That(table.TryLease(out _), Is.False, "a recycled slot under a frame that is still on a socket is a use-after-free with extra steps");
        });

        table.EndSend(session);
        table.BeginTick();

        Assert.That(table.TryLease(out _), Is.True, "and it comes back on the first tick after the last send finished");
    }

    /// <summary>
    /// The send counter must never go below zero, whatever a caller does, because a counter below zero is a slot the table loses for ever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recycling tests the counter against zero. A counter at −1 never passes that test again: the slot is retried on every tick for the life of the process,
    /// no client is ever admitted to it, and nothing anywhere reports a fault — the table is simply one session smaller than the operator configured.
    /// </para>
    /// <para>
    /// An unmatched <c>EndSend</c> is the reachable version of that failure, and it is how the race in <c>BeginSend</c> used to end: a transport thread that
    /// had checked the identity, then incremented a counter belonging to the slot's next occupant, corrected itself with a decrement one instant after that
    /// occupant's own publication had reset it to zero. The interleaving is a few instructions wide and no stress test reproduces it on demand; this asserts
    /// the property it would have broken, which is reachable in one call.
    /// </para>
    /// </remarks>
    [Test]
    public void AnUnmatchedEndSendCannotTakeTheCounterBelowZero()
    {
        using var table = NewTable(1);

        Assert.That(Admit(table, out var session, out _), Is.True);
        table.BeginTick();

        table.EndSend(session);
        table.EndSend(session);

        Assert.That(table.SendsInFlight(session), Is.Zero, "a decrement with no send behind it is refused, not applied");

        table.Close(session, SessionCloseReason.ClientLeft, CloseCodes.Normal);
        table.BeginTick();
        table.BeginTick();

        Assert.That(table.TryLease(out _), Is.True, "a counter driven below zero is a slot no tick can ever recycle again");
    }

    /// <summary>A send is refused rather than wrapped at the ceiling: a wrapped counter reads as zero and recycles the slot under live frames.</summary>
    [Test]
    public void TheSendCounterIsRefusedAtItsCeilingRatherThanWrapped()
    {
        using var table = NewTable(1);

        Assert.That(Admit(table, out var session, out _), Is.True);
        table.BeginTick();

        for (var i = 0; i < SessionTable.MaxSendsInFlight; i++)
        {
            Assert.That(table.BeginSend(session), Is.True, $"send {i}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(table.SendsInFlight(session), Is.EqualTo(SessionTable.MaxSendsInFlight));
            Assert.That(table.BeginSend(session), Is.False, "the caller drops the frame rather than being handed a counter that wrapped to zero");
            Assert.That(table.SendsInFlight(session), Is.EqualTo(SessionTable.MaxSendsInFlight));
        });
    }

    [Test]
    public void ASecondCloseIsANoOpRatherThanASecondEvent()
    {
        using var table = NewTable();

        Assert.That(Admit(table, out var session, out _), Is.True);
        table.BeginTick();

        Assert.Multiple(() =>
        {
            Assert.That(table.Close(session, SessionCloseReason.Kicked, 4100), Is.True);
            Assert.That(table.Close(session, SessionCloseReason.LinkLost, CloseCodes.GoingAway), Is.False);
        });

        Assert.That(table.BeginTick(), Is.EqualTo(1), "one close, one event — an application that releases an avatar twice would double-free it");
    }

    [Test]
    public void ACloseAskedForOffTheTickIsAppliedByTheTick()
    {
        using var table = NewTable();

        Assert.That(Admit(table, out var session, out _), Is.True);
        table.BeginTick();

        Assert.Multiple(() =>
        {
            Assert.That(table.RequestClose(session, SessionCloseReason.ProtocolError, CloseCodes.ProtocolError), Is.True);
            Assert.That(table.RequestClose(session, SessionCloseReason.LinkLost, CloseCodes.GoingAway), Is.False, "one request per session");
            Assert.That(table.IsOpen(session), Is.True, "a transport thread asks; it does not write the row");
        });

        Assert.That(table.ApplyPendingCloses(), Is.EqualTo(1));
        table.BeginTick();

        var length = table.Events.Count;
        var closed = table.Events.AsSpan()[0];

        Assert.Multiple(() =>
        {
            Assert.That(length, Is.EqualTo(1));
            Assert.That(closed.Kind, Is.EqualTo(SessionEventKind.Closed));
            Assert.That(closed.Reason, Is.EqualTo(SessionCloseReason.ProtocolError));
            Assert.That(closed.CloseCode, Is.EqualTo(CloseCodes.ProtocolError));
        });
    }

    [Test]
    public void OpenSessionsAreWalkableByTheTick()
    {
        using var table = NewTable();

        Assert.That(Admit(table, out var a, out _), Is.True);
        Assert.That(Admit(table, out var b, out _), Is.True);
        Assert.That(Admit(table, out var c, out _), Is.True);
        table.BeginTick();

        table.Close(b, SessionCloseReason.ClientLeft, CloseCodes.Normal);
        table.BeginTick();

        var seen = 0;
        var sawA = false;
        var sawC = false;
        foreach (var session in table)
        {
            seen++;
            sawA |= session == a;
            sawC |= session == c;
        }

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.EqualTo(2));
            Assert.That(sawA, Is.True);
            Assert.That(sawC, Is.True);
            Assert.That(table.OpenCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void LimitsLeftAtZeroTakeTheOperatorValueAndAmbitiousOnesAreClamped()
    {
        using var table = new SessionTable("Sessions", _registry.Runtime, _allocator,
            new SubscriptionsOptions
            {
                MaxSessions = 4, FrameBytes = 64 * 1024, ClientMessageBytes = 1024, ObserversPerSession = 6,
            },
            _sessions.SessionEvents);

        _sessions.Admit = static (in AdmissionRequest _) => Admission.Accept(SessionRole.Player,
            new SessionLimits { FrameBytes = 8 * 1024, ClientMessageBytes = 1024 * 1024 });

        Assert.That(Admit(table, out var session, out _), Is.True);
        var row = table.Row(session);

        Assert.Multiple(() =>
        {
            Assert.That(row.FrameBytes, Is.EqualTo(8 * 1024), "a session may ask for less than the operator allows");
            Assert.That(row.ClientMessageBytes, Is.EqualTo(1024), "and never for more");
            Assert.That(row.MaxObservers, Is.EqualTo(6), "zero means the operator's number, as for every sibling limit");
        });
    }

    /// <summary>
    /// An observer is a spatial query per tick, so the count is the knob that bounds a session's query cost. It comes from an application hook deciding on
    /// untrusted input, which is exactly the shape of number that needs an operator rail above it — the same rail every other per-session limit already had.
    /// </summary>
    [Test]
    public void AnAmbitiousObserverCountIsClampedToTheOperatorsRail()
    {
        using var table = new SessionTable("Sessions", _registry.Runtime, _allocator,
            new SubscriptionsOptions { MaxSessions = 4, ObserversPerSession = 4 }, _sessions.SessionEvents);

        _sessions.Admit = static (in AdmissionRequest _) => Admission.Accept(SessionRole.Player, new SessionLimits { MaxObservers = 100_000 });

        Assert.That(Admit(table, out var session, out _), Is.True);
        Assert.That(table.Row(session).MaxObservers, Is.EqualTo(4), "a hook cannot buy itself a hundred thousand queries a tick");
    }

    [Test]
    public void AnObserverRailBelowOneIsRefusedAtConstruction()
        => Assert.That(() => new SessionTable("Sessions", _registry.Runtime, _allocator,
                new SubscriptionsOptions { MaxSessions = 4, ObserversPerSession = 0 }, _sessions.SessionEvents),
            Throws.InstanceOf<ArgumentOutOfRangeException>(), "a rail of zero would admit sessions that can never be served");

    // ── the lease ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A connection can fail between the lease and the open: a HELLO that does not decode, a link that drops, an allocation that throws while the payload is
    /// copied. Without a release the slot is gone for the life of the process — on no free stack, owned by no session, and never recycled because nothing ever
    /// closes it. At <c>MaxSessions</c> failed handshakes the server stops admitting anyone and nothing in it reports a fault.
    /// </summary>
    [Test]
    public void ALeaseThatNeverBecameASessionIsGivenBack()
    {
        using var table = NewTable(2);

        Assert.That(table.TryLease(out var leased), Is.True);
        Assert.That(table.FreeCount, Is.EqualTo(1));

        Assert.Multiple(() =>
        {
            Assert.That(table.IsLive(leased), Is.False, "a lease is not a session: no row is published for it");
            Assert.That(table.ReleaseLease(leased), Is.True);
            Assert.That(table.FreeCount, Is.EqualTo(2), "and the slot is back");
        });

        Assert.That(table.TryLease(out var again), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(again.Slot, Is.EqualTo(leased.Slot), "the same row");
            Assert.That(again.Generation, Is.EqualTo(leased.Generation + 1), "with a generation that was never handed out before");
            Assert.That(table.ReleaseLease(leased), Is.False, "and the identity that was released cannot be released a second time");
            Assert.That(table.FreeCount, Is.EqualTo(1), "so a double release cannot hand one slot to two connections");
        });
    }

    [Test]
    public void ReleasingAnOpenSessionsIdentityDoesNothing()
    {
        using var table = NewTable(2);

        Assert.That(Admit(table, out var session, out _), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(table.ReleaseLease(session), Is.False, "an opened session is closed, not un-leased");
            Assert.That(table.FreeCount, Is.EqualTo(1));
            Assert.That(table.IsLive(session), Is.True);
        });
    }

    [Test]
    public void OpeningAReleasedLeaseIsRefused()
    {
        using var table = NewTable(2);

        Assert.That(table.TryLease(out var leased), Is.True);
        Assert.That(table.ReleaseLease(leased), Is.True);

        Assert.That(() => table.Open(leased, Admission.Accept(SessionRole.Player), "player", ReadOnlySpan<byte>.Empty),
            Throws.InstanceOf<InvalidOperationException>(), "writing a row whose lease is gone would scribble over the next connection's session");
    }

    /// <summary>
    /// A leaked lease is invisible until the server stops admitting: the slot is on no free stack, owned by no session and never recycled, so the failure
    /// surfaces as 1013 for everyone, <c>MaxSessions</c> failed handshakes later. Releasing is what keeps a full table a temporary state.
    /// </summary>
    [Test]
    public void ReleasingTheLastLeaseLetsTheNextConnectionIn()
    {
        using var table = NewTable(1);

        Assert.That(table.TryLease(out var leased), Is.True);
        Assert.That(Admit(table, out _, out var refused), Is.False, "the one slot is out on a lease");
        Assert.That(refused, Is.EqualTo(CloseCodes.TryAgainLater));

        Assert.That(table.ReleaseLease(leased), Is.True);

        Assert.That(Admit(table, out var session, out _), Is.True, "and a failed handshake costs the server nothing permanent");
        Assert.That(session.Generation, Is.EqualTo(leased.Generation + 1));
    }

    // ── the generation ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Zero is never issued, so the wrap has to skip it — a generation of zero would make <see langword="default"/> name a live session. Reaching the wrap
    /// through the table would take 65 536 admissions of one slot, which is why the bump is a static this can call directly.
    /// </summary>
    [Test]
    [TestCase((ushort)1, (ushort)2)]
    [TestCase((ushort)41, (ushort)42)]
    [TestCase((ushort)65_534, (ushort)65_535)]
    [TestCase((ushort)65_535, (ushort)1)]
    public void TheGenerationWrapsPastZero(ushort generation, ushort expected)
        => Assert.That(SessionTable.NextGeneration(generation), Is.EqualTo(expected));

    // ── the row is a copy ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A <c>ref readonly</c> into the rows would stay a valid reference long after it stopped describing the session that asked for it: the memory is
    /// recycled, not freed, so the next client's limits appear in it with nothing marking the moment it changed meaning. The copy cannot do that.
    /// </summary>
    [Test]
    public void ARowReadSurvivesItsSessionBeingRecycled()
    {
        using var table = NewTable(1);
        _sessions.Admit = static (in AdmissionRequest _) => Admission.Accept(SessionRole.Player, new SessionLimits { BytesPerSecond = 111 });

        Assert.That(Admit(table, out var first, out _), Is.True);
        var taken = table.Row(first);

        table.BeginTick();
        table.Close(first, SessionCloseReason.ClientLeft, CloseCodes.Normal);
        table.BeginTick();
        table.BeginTick();

        _sessions.Admit = static (in AdmissionRequest _) => Admission.Accept(SessionRole.Spectator, new SessionLimits { BytesPerSecond = 222 });
        Assert.That(Admit(table, out var second, out _), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(taken.BytesPerSecond, Is.EqualTo(111), "the copy still describes the client it was taken for");
            Assert.That(taken.IdValue, Is.EqualTo(first.Value));
            Assert.That(table.Row(second).BytesPerSecond, Is.EqualTo(222));
            Assert.That(sizeof(SessionRowView), Is.LessThanOrEqualTo(64), "and it stays one cache line, so returning it costs one copy of one line");
        });
    }

    [Test]
    public void AStaleIdentityCannotReadTheNextOccupantsRow()
    {
        using var table = NewTable(1);

        Assert.That(Admit(table, out var first, out _), Is.True);
        table.BeginTick();
        table.Close(first, SessionCloseReason.ClientLeft, CloseCodes.Normal, "gone");
        table.BeginTick();
        table.BeginTick();

        Assert.That(Admit(table, out var second, out _), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(() => table.Row(first), Throws.InstanceOf<InvalidOperationException>());
            Assert.That(table.AppData(first), Is.Null, "the side arrays are keyed by SLOT, so they need the identity checked as much as the row does");
            Assert.That(table.SessionKind(first), Is.Null);
            Assert.That(table.ProfileName(first), Is.Null);
            Assert.That(table.CloseDetail(first), Is.Null);
            Assert.That(table.Row(second).IdValue, Is.EqualTo(second.Value));
        });
    }

    // ── disposal ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The transport-callable members run on threads this table does not own, so a flag read followed by a free is a real use-after-free on native memory.
    /// After disposal every one of them has to answer "gone" rather than dereference a freed block.
    /// </summary>
    [Test]
    public void EveryTransportCallableMemberAnswersGoneAfterDisposal()
    {
        var table = NewTable(2);
        Assert.That(Admit(table, out var session, out _), Is.True);
        table.BeginTick();

        table.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(table.TryLease(out _), Is.False);
            Assert.That(table.ReleaseLease(session), Is.False);
            Assert.That(table.BeginSend(session), Is.False);
            Assert.That(table.RequestClose(session, SessionCloseReason.LinkLost, CloseCodes.GoingAway), Is.False);
            Assert.That(table.SendsInFlight(session), Is.Zero);
            Assert.That(table.IsOpen(session), Is.False);
            Assert.That(table.IsLive(session), Is.False);
            Assert.That(table.AppData(session), Is.Null);
            Assert.That(table.FreeCount, Is.Zero);
            Assert.That(table.BeginTick(), Is.Zero);
            Assert.That(table.ApplyPendingCloses(), Is.Zero);
            Assert.That(() => table.EndSend(session), Throws.Nothing);
            Assert.That(() => table.Dispose(), Throws.Nothing, "a second dispose is a no-op, not a second free");
            Assert.That(() => table.Row(session), Throws.InstanceOf<ObjectDisposedException>());
        });
    }

    [Test]
    public void ATableWiderThanASlotNumberIsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => NewTable(0), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => NewTable(SessionTable.MaxSlots + 1), Throws.InstanceOf<ArgumentOutOfRangeException>());
        });
    }
}
