using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// The session-lifecycle test seam: <see cref="SessionEvent.Create"/>, <see cref="ISessionRequests"/> and <see cref="RecordingSessionRequests"/>. An
/// application's lifecycle code — spawn and control on open, hold the body on a resumable close, rebind on resume, retire on expiry — runs in a plain unit test
/// against the fake, with no engine, socket or dropped link.
/// </summary>
[TestFixture]
class RecordingSessionRequestsTests
{
    // An application's lifecycle code, as the MMORPG shard writes it: everything it needs is ISessionRequests.
    private sealed class Lifecycle
    {
        private long _next = 1;
        public readonly Dictionary<SessionId, EntityId> Held = [];
        public readonly List<EntityId> Retired = [];

        public void Run(ISessionRequests subs)
        {
            foreach (var e in subs.SessionEvents)
            {
                switch (e.Kind)
                {
                    case SessionEventKind.Opened when e.ResumedFrom.IsValid && Held.Remove(e.ResumedFrom, out var body):
                        subs.Session(e.Session).Profile("player").Control(body);
                        break;
                    case SessionEventKind.Opened:
                        subs.Session(e.Session).Profile("player").Control(EntityId.FromParts(_next++, 1));
                        break;
                    case SessionEventKind.Closed when e.Resumable:
                        Held[e.Session] = subs.ControlledOf(e.Session);
                        break;
                    case SessionEventKind.ResumeExpired when Held.Remove(e.Session, out var expired):
                        Retired.Add(expired);
                        break;
                }
            }
        }
    }

    [Test]
    public void ALifecycleIsDrivenThroughOpenResumableCloseAndResume_WithoutAnEngine()
    {
        var subs = new RecordingSessionRequests();
        var app = new Lifecycle();
        var first = new SessionId(3, 1);
        var second = new SessionId(4, 1);

        subs.Raise(SessionEvent.Create(SessionEventKind.Opened, first));
        app.Run(subs);
        var body = subs.ControlledOf(first);
        Assert.That(body, Is.Not.EqualTo(EntityId.Null));
        Assert.That(subs.ProfileOf(first), Is.EqualTo("player"));

        subs.NextTick();
        subs.Raise(SessionEvent.Create(SessionEventKind.Closed, first, resumable: true, reason: SessionCloseReason.LinkLost));
        app.Run(subs);
        Assert.That(app.Held[first], Is.EqualTo(body), "a resumable close holds the body the session controlled");

        subs.NextTick();
        subs.Raise(SessionEvent.Create(SessionEventKind.Opened, second, resumedFrom: first));
        app.Run(subs);
        Assert.That(subs.ControlledOf(second), Is.EqualTo(body), "the resumed session is rebound to the held body, not given a new one");
        Assert.That(app.Held, Is.Empty);
        Assert.That(subs.Requests, Has.Count.EqualTo(4), "Profile + Control on each open, in call order");
        Assert.That(subs.Requests[3].ToString(), Does.Contain("Control"));
    }

    [Test]
    public void AnExpiredResumeRetiresTheHeldBody()
    {
        var subs = new RecordingSessionRequests();
        var app = new Lifecycle();
        var s = new SessionId(1, 7);
        subs.Raise(SessionEvent.Create(SessionEventKind.Opened, s));
        subs.Raise(SessionEvent.Create(SessionEventKind.Closed, s, resumable: true));
        app.Run(subs);
        var body = app.Held[s];

        subs.NextTick();
        Assert.That(subs.SessionEvents.Length, Is.Zero, "the next tick delivers only what was raised after it began");
        subs.Raise(SessionEvent.Create(SessionEventKind.ResumeExpired, s));
        app.Run(subs);
        Assert.That(app.Retired, Is.EqualTo(new[] { body }));
    }

    [Test]
    public void EveryRequestKindIsRecordedWithItsPayload_AndTheBuildersChecksStillApply()
    {
        var subs = new RecordingSessionRequests("party");
        var s = new SessionId(2, 2);
        var e = EntityId.FromParts(42, 3);
        subs.Session(s).Follow(e).SetBudget(4096).SetVisibility(0b101).SetSources("party", 7, 9).Kick(4100, "bye");

        var r = subs.RequestsOf(s);
        Assert.That(r, Has.Count.EqualTo(5));
        Assert.That(r[0].Kind, Is.EqualTo(SessionRequestKind.Follow));
        Assert.That(r[0].Entity, Is.EqualTo(e));
        Assert.That(r[1].Value, Is.EqualTo(4096));
        Assert.That(r[2].Value, Is.EqualTo(0b101));
        Assert.That(r[3].SourceSet, Is.EqualTo("party"));
        Assert.That(r[3].SourceKeys.ToArray(), Is.EqualTo(new long[] { 7, 9 }));
        Assert.That(r[4].Code, Is.EqualTo(4100));
        Assert.That(r[4].KickReason, Is.EqualTo("bye"));
        Assert.That(subs.WasKicked(s));
        Assert.That(subs.ControlledOf(s), Is.EqualTo(EntityId.Null), "Follow is not Control");

        Assert.Throws<ArgumentOutOfRangeException>(() => subs.Session(s).Kick(1000), "the engine's own argument checks run against the fake");
        Assert.Throws<ArgumentException>(() => subs.Session(s).SetSources("guild", 1), "an undeclared explicit set is refused, as the engine refuses it");

        subs.ClearRequests();
        Assert.That(subs.Requests, Is.Empty);
    }

    [Test]
    public void CreatedEventsCarryWhatTheEngineWouldAndNothingElse()
    {
        var s = new SessionId(5, 1);
        var data = new object();
        var closed = SessionEvent.Create(SessionEventKind.Closed, s, data, resumable: true, reason: SessionCloseReason.Lagging, closeCode: 4001);
        Assert.That(closed.AppData, Is.SameAs(data));
        Assert.That(closed.Resumable);
        Assert.That(closed.Reason, Is.EqualTo(SessionCloseReason.Lagging));
        Assert.That(closed.CloseCode, Is.EqualTo(4001));
        Assert.That(closed.HelloPayload.IsEmpty);

        // As the engine's events: a realm is only a RealmClosed's, a level only a Degraded's.
        var opened = SessionEvent.Create(SessionEventKind.Opened, s, realm: new RealmId(3), degradeLevel: 2);
        Assert.That(opened.Realm, Is.EqualTo(RealmId.None));
        Assert.That(opened.DegradeLevel, Is.Zero);
    }

    [Test]
    public void SubscriptionsCommandsIsTheProductionImplementation() =>
        Assert.That(typeof(ISessionRequests).IsAssignableFrom(typeof(SubscriptionsCommands)));
}
