using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>A reliable event every session hears.</summary>
public struct RelNotice
{
    public int Seq;
}

/// <summary>A reliable event heard by the session controlling its target.</summary>
public struct RelHit
{
    public EntityId Target;
    public int Seq;
}

/// <summary>A reliable event heard by the sessions near its point.</summary>
public struct RelBoom
{
    public float X;
    public float Y;
    public int Seq;
}

/// <summary>A reliable event heard by the sessions that know its entity.</summary>
public struct RelDuel
{
    public EntityId A;
    public int Seq;
}

/// <summary>A reliable event with a long text: a burst of them is larger than a frame.</summary>
public struct RelLetter
{
    public Utf8Text256 Text;
    public int Seq;
}

/// <summary>A reliable event with an enum field: a value outside the enum's names is one its codec cannot carry.</summary>
internal struct RelLevel
{
    public ProjAiMode Mode;
    public int Seq;
}

/// <summary>A reliable event addressed to one session.</summary>
public struct RelWhisper
{
    public int Seq;
}

/// <summary>The best-effort twin of <see cref="RelLetter"/>: the same burst, cut at half a frame and counted lost.</summary>
public struct BestLetter
{
    public Utf8Text256 Text;
    public int Seq;
}

/// <summary>
/// Reliable events (14 § 7, SUB-35, step 4.5): while a session lives it receives every reliable event routed to it, once, in emission order — across
/// skipped frames, a RESET and a burst larger than a frame — and a session that cannot be kept so is closed with 1013. An emit past the budget is refused.
/// </summary>
[TestFixture]
[NonParallelizable]
sealed class ReliableEventTests : TestBase<ReliableEventTests>
{
    private const double Radius = 45d;
    private const int FillTicks = 6;
    private const int FrameBytes = 8 * 1024;

    private static readonly Utf8Text256 Long = Utf8Text256.From(new string('x', 240));

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile("near", p => p.Sphere(Radius).Of<ProjCreature>());
        subs.Event<RelNotice>(e => e.Broadcast().Reliable());
        subs.Event<RelHit>(e => e.RouteToOwner(h => h.Target).Reliable());
        subs.Event<RelBoom>(e => e.RouteNear(b => new Vector3D(b.X, b.Y, 0d)).Reliable());
        subs.Event<RelDuel>(e => e.RouteToKnown(d => d.A).Reliable());
        subs.Event<RelLetter>(e => e.Broadcast().Reliable().Field(l => l.Text, Codec.Str(Utf8Text256.Capacity), "text"));
        subs.Event<BestLetter>(e => e.Broadcast().Field(l => l.Text, Codec.Str(Utf8Text256.Capacity), "text"));
        subs.Event<RelWhisper>(e => e.RouteToSession().Reliable());

        // Served one tick in two: a rate class beside a degrade class.
        subs.Profile("near-every-2", p => p.Every(2).Sphere(Radius).Of<ProjCreature>());
    }

    private static SubscriptionsOptions Options(long reliableBytes = 4 << 20, TimeSpan? closeAfter = null) => new()
    {
        IngressBytesPerSecond = TestIngress.Budget,
        MaxSessions = 16,
        ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
        FrameBytes = FrameBytes,
        ReliableEventBytes = reliableBytes,
        CloseStalledAfter = closeAfter ?? TimeSpan.FromSeconds(3),
    };

    private static ProjBounds PointAt(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    private static EntityId[] Spawn(DatabaseEngine dbe, int count, float spacing)
    {
        var ids = new EntityId[count];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            ids[i] = tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(i * spacing, 0f)));
        }

        tx.Commit();
        return ids;
    }

    private FrameHarness Harness(DatabaseEngine dbe, string name, SubscriptionsOptions options = null)
    {
        var harness = FrameHarness.Create(dbe, Declare, name, options ?? Options());
        harness.RunFence = true;
        return harness;
    }

    private static List<int> Seqs(FrameHarness harness, SessionId session, string name = null) =>
        harness.Replica(session).Events.Received.Where(e => name == null || e.Name == name).Select(e => (int)e.Fields["Seq"]).ToList();

    /// <summary>
    /// The oracle: random reliable broadcasts, owner-routed, near and known events every tick, sessions skipping frames at a given rate — far enough, at
    /// 90 %, to fall past the event log and be reset. Every session receives exactly the events routed to it, each once, in emission order, and loses none.
    /// </summary>
    /// <param name="skipPercent">The percentage of ticks on which a session's frames are left undrained.</param>
    [Test]
    [VerifiesRule("SUB-35")]
    public void EveryReliableEventReachesEveryLiveSessionOnceInOrder([Values(0, 30, 60, 90)] int skipPercent)
    {
        const int SessionCount = 6;
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var creatures = Spawn(dbe, SessionCount + 1, 20f);
        // A close bound past any skip run the seeds reach: every session lives, so every one is owed everything (a closed session is the 1013 case's).
        using var harness = Harness(dbe, nameof(EveryReliableEventReachesEveryLiveSessionOnceInOrder) + skipPercent,
            Options(closeAfter: TimeSpan.FromSeconds(20)));
        var sessions = harness.OpenSessions(SessionCount, "near");
        for (var i = 0; i < SessionCount; i++)
        {
            Assert.That(harness.Sessions.SetViewpoint(sessions[i], new Vector3D(i * 20d, 0d, 0d)), Is.True);
            if (i < SessionCount - 1)
            {
                Assert.That(harness.Sessions.SetControlled(sessions[i], creatures[i]), Is.True);
            }
        }

        var tick = 0;
        for (; tick < FillTicks; tick++)
        {
            harness.RunTick(tick + 1);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }
        }

        var rng = new Random(9300 + skipPercent);
        var expected = new List<int>[SessionCount];
        for (var i = 0; i < SessionCount; i++)
        {
            expected[i] = [];
        }

        var commands = harness.Subscriptions.Commands;
        var resetsBefore = harness.Subscriptions.Push.Resets;
        var seq = 0;
        var what = new Dictionary<int, string>();
        for (var t = 0; t < 150; t++, tick++)
        {
            var count = rng.Next(0, 4);
            for (var k = 0; k < count; k++)
            {
                seq++;
                var kind = rng.Next(5);
                what[seq] = $"{(kind >= 3 ? kind == 3 ? "near" : "known" : kind == 0 ? "broadcast" : "owner")}@{tick + 1}";
                if (kind >= 3)
                {
                    // Sessions stand 20 m apart with R = 45 m: the sessions that see creature c are those within two steps of it.
                    var c = rng.Next(creatures.Length);
                    Assert.That(kind == 3
                            ? commands.Emit(new RelBoom { X = c * 20f, Y = 0f, Seq = seq })
                            : commands.Emit(new RelDuel { A = creatures[c], Seq = seq }),
                        Is.True);
                    for (var s = 0; s < SessionCount; s++)
                    {
                        if (Math.Abs(s - c) <= 2)
                        {
                            expected[s].Add(seq);
                        }
                    }
                }
                else if (kind == 0)
                {
                    Assert.That(commands.Emit(new RelNotice { Seq = seq }), Is.True);
                    foreach (var list in expected)
                    {
                        list.Add(seq);
                    }
                }
                else
                {
                    var target = rng.Next(creatures.Length);
                    Assert.That(commands.Emit(new RelHit { Target = creatures[target], Seq = seq }), Is.True);
                    if (target < SessionCount - 1)
                    {
                        expected[target].Add(seq);
                    }
                }
            }

            harness.RunTick(tick + 1);
            foreach (var session in sessions)
            {
                if (rng.Next(100) >= skipPercent)
                {
                    harness.Deliver(session);
                }
            }
        }

        for (var q = 0; q < 4; q++, tick++)
        {
            harness.RunTick(tick + 1);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }
        }

        Assert.Multiple(() =>
        {
            for (var i = 0; i < SessionCount; i++)
            {
                Assert.That(harness.Sessions.IsOpen(sessions[i]), Is.True, $"session {i} lives");
                var got = Seqs(harness, sessions[i]);
                var missing = string.Join(", ", expected[i].Except(got).Take(12).Select(m => $"{m}:{what[m]}"));
                var extra = string.Join(", ", got.Except(expected[i]).Take(12).Select(m => $"{m}:{what[m]}"));
                Assert.That(got, Is.EqualTo(expected[i]), $"session {i}: every routed event, once, in order — missing [{missing}], extra [{extra}]");
                Assert.That(harness.Replica(sessions[i]).Events.Lost, Is.Zero, $"session {i}: a reliable event is never counted lost");
            }

            Assert.That(harness.Subscriptions.Events.ReliableRefused, Is.Zero);
            Assert.That(harness.Assembler.SessionsClosedLagging + harness.Assembler.SessionsClosedReliable, Is.Zero,
                $"no session closed (close bound {harness.Assembler.CloseBoundTicks} ticks, reliable ring {harness.Subscriptions.Events.ReliableDepth})");
            if (skipPercent == 90)
            {
                Assert.That(harness.Subscriptions.Push.Resets, Is.GreaterThan(resetsBefore), "a session fell past the log: its RESET carried its events");
            }
        });
    }

    /// <summary>
    /// A session not drained for longer than the event log holds: its next frame is a RESET, and it carries every reliable event of the gap, while the
    /// best-effort ones of the same gap are counted lost.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-35")]
    public void AResetCarriesTheReliableEventsOfTheGap()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = Harness(dbe, nameof(AResetCarriesTheReliableEventsOfTheGap));
        var session = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0L;
        for (; tick < FillTicks;)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        var commands = harness.Subscriptions.Commands;
        for (var t = 0; t < PushReplication.LogDepth + 4; t++)
        {
            commands.Emit(new RelNotice { Seq = t });
            commands.Emit(new BestLetter { Text = Long, Seq = t });
            harness.RunTick(++tick);
        }

        // What the ring held when the session stopped draining, then the frame that catches it up.
        var logs = harness.DeliverLogged(session);
        harness.RunTick(++tick);
        logs.AddRange(harness.DeliverLogged(session));
        Assert.Multiple(() =>
        {
            Assert.That(logs, Is.Not.Empty);
            Assert.That(logs.Any(l => (l.Flags & TickFlags.Reset) != 0), Is.True, "the gap is past the log: a RESET");
            Assert.That(Seqs(harness, session, nameof(RelNotice)), Is.EqualTo(Enumerable.Range(0, PushReplication.LogDepth + 4)),
                "every reliable one, in order");
            Assert.That(harness.Replica(session).Events.Lost, Is.GreaterThan(0), "the best-effort ones past the log are counted lost");
        });
    }

    /// <summary>
    /// A burst of reliable events larger than a frame goes over the next frames, whole and in order — where the same burst of best-effort events is cut at
    /// half a frame and counted lost.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-35")]
    public void ABurstLargerThanAFrameGoesOverTheNextFramesUncut()
    {
        const int Burst = 100;   // ~250 B each: three frames' worth
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = Harness(dbe, nameof(ABurstLargerThanAFrameGoesOverTheNextFramesUncut));
        var session = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0L;
        for (; tick < FillTicks;)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        var commands = harness.Subscriptions.Commands;
        for (var i = 0; i < Burst; i++)
        {
            Assert.That(commands.Emit(new RelLetter { Text = Long, Seq = i }), Is.True);
            commands.Emit(new BestLetter { Text = Long, Seq = i });
        }

        var frames = 0;
        for (var t = 0; t < 12; t++)
        {
            harness.RunTick(++tick);
            frames += harness.Deliver(session) > 0 ? 1 : 0;
        }

        var reliable = Seqs(harness, session, nameof(RelLetter));
        var best = Seqs(harness, session, nameof(BestLetter));
        Assert.Multiple(() =>
        {
            Assert.That(reliable, Is.EqualTo(Enumerable.Range(0, Burst)), "every one, once, in order");
            Assert.That(frames, Is.GreaterThanOrEqualTo(3), "over several frames: three frames' worth");
            Assert.That(best.Count + harness.Replica(session).Events.Lost, Is.EqualTo(Burst), "the best-effort burst: received or counted lost");
            Assert.That(harness.Replica(session).Events.Lost, Is.GreaterThan(0), "and it was cut");
            Assert.That(harness.Sessions.IsOpen(session), Is.True);
        });
    }

    /// <summary>
    /// A session whose frames publish but can never catch up — more reliable bytes emitted every tick than half a frame carries — is closed with 1013 once
    /// its backlog is older than the close bound, rather than kept on and starved.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-35")]
    public void ASessionThatCannotBeKeptReliableIsClosedWith1013()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = Harness(dbe, nameof(ASessionThatCannotBeKeptReliableIsClosedWith1013), Options(closeAfter: TimeSpan.FromMilliseconds(1)));
        var session = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0L;
        for (; tick < FillTicks;)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        var commands = harness.Subscriptions.Commands;
        var bound = harness.Assembler.CloseBoundTicks;
        var state = harness.StateOf(session);
        var age = 0L;
        for (var t = 0; t < (bound * 4) && harness.Assembler.SessionsClosedReliable == 0; t++)
        {
            // The age the next tick's sweep judges: how old the oldest owed event is.
            var oldest = EventHub.OldestOwed(state);
            age = oldest == 0 ? 0 : tick + 1 - oldest;
            for (var i = 0; i < 30; i++)
            {
                commands.Emit(new RelLetter { Text = Long, Seq = i });
            }

            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        // The close is latched, applied at the next ingress drain: its code is the row's pending one.
        var code = (ushort)(harness.Sessions.Row(session).PendingClose & 0xFFFF);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Assembler.SessionsClosedReliable, Is.EqualTo(1), "closed once");
            Assert.That(age, Is.EqualTo(bound + 1), "by the sweep, the tick the owed backlog passed the bound — not by the ring's backstop");
            Assert.That(age, Is.LessThan(harness.Subscriptions.Events.ReliableDepth), "while the ring still held what it was owed");
            Assert.That(code, Is.EqualTo(CloseCodes.TryAgainLater), "1013: the SDK reconnects");
            Assert.That(harness.Assembler.SessionsClosedLagging, Is.Zero, "its frames were drained: not the skip rule");
        });
    }

    /// <summary>
    /// The same unkeepable session while the metronome catches up after a stall — ticks 1 µs apart: its backlog does not age in ticks (SUB-15), so the
    /// close bound does not fire; the ring's own depth still does, the tick before it would reuse the slot the oldest owed event sits in (SUB-35).
    /// </summary>
    [Test]
    [VerifiesRule("SUB-15")]
    [VerifiesRule("SUB-35")]
    public void ACatchUpDoesNotAgeAReliableBacklogButTheRingStillForcesTheClose()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = Harness(dbe, nameof(ACatchUpDoesNotAgeAReliableBacklogButTheRingStillForcesTheClose),
            Options(closeAfter: TimeSpan.FromMilliseconds(1)));
        var session = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0L;
        for (; tick < FillTicks;)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        harness.TickSpacingUs = 1;
        var commands = harness.Subscriptions.Commands;
        var bound = harness.Assembler.CloseBoundTicks;
        var depth = harness.Subscriptions.Events.ReliableDepth;
        var state = harness.StateOf(session);
        var age = 0L;
        // The backlog ages about half a tick per tick here (a frame carries half of what each tick emits), so four ring depths reach the backstop.
        for (var t = 0; t < (depth * 4) && harness.Assembler.SessionsClosedReliable == 0; t++)
        {
            var oldest = EventHub.OldestOwed(state);
            age = oldest == 0 ? 0 : tick + 1 - oldest;
            for (var i = 0; i < 30; i++)
            {
                commands.Emit(new RelLetter { Text = Long, Seq = i });
            }

            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Assembler.SessionsClosedReliable, Is.EqualTo(1), "closed once");
            Assert.That(age, Is.GreaterThan(bound + 1), "not by the close bound: the catch-up's ticks took microseconds between them");
            Assert.That(age, Is.EqualTo(depth - 1), "by the ring's backstop, the tick before the oldest owed event's slot would be reused");
        });
    }

    /// <summary>
    /// The burst's check against a client that loses one of the frames carrying it — the violation SUB-35 forbids, made on the test side: the check must
    /// reject the run.
    /// </summary>
    [Test]
    [RuleMutant("SUB-35")]
    public void AFrameLostOnTheWayIsDetected()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = Harness(dbe, nameof(AFrameLostOnTheWayIsDetected));
        var session = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0L;
        for (; tick < FillTicks;)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        for (var i = 0; i < 100; i++)
        {
            harness.Subscriptions.Commands.Emit(new RelLetter { Text = Long, Seq = i });
        }

        var dropped = false;
        for (var t = 0; t < 12; t++)
        {
            harness.RunTick(++tick);
            foreach (var frame in harness.Collect(session))
            {
                if (!dropped && t == 1)
                {
                    dropped = true;
                    continue;
                }

                harness.Replica(session).Apply(frame);
            }
        }

        RuleMutants.AssertDetects("SUB-35", "reliable oracle:", () =>
            Assert.That(Seqs(harness, session, nameof(RelLetter)), Is.EqualTo(Enumerable.Range(0, 100)), "reliable oracle: every one, once, in order"));
    }

    /// <summary>
    /// Events still take at most half a frame: while a reliable backlog is carried, the best-effort events beside it are what is shed, so no frame grows
    /// past half for them.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-35")]
    [VerifiesRule("SUB-21")]
    public void ACarriedBacklogLeavesTheBestEffortEventsWhatIsLeftOfHalfAFrame()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = Harness(dbe, nameof(ACarriedBacklogLeavesTheBestEffortEventsWhatIsLeftOfHalfAFrame));
        var session = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0L;
        for (; tick < FillTicks;)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        var commands = harness.Subscriptions.Commands;
        for (var i = 0; i < 100; i++)
        {
            commands.Emit(new RelLetter { Text = Long, Seq = i });
        }

        var largest = 0;
        var shared = 0;
        var events = harness.Replica(session).Events;
        for (var t = 0; t < 12; t++)
        {
            // Nearly half a frame of best-effort letters every tick: alone they fit, beside the backlog only what it leaves of the half does.
            for (var i = 0; i < 16; i++)
            {
                commands.Emit(new BestLetter { Text = Long, Seq = i });
            }

            harness.RunTick(++tick);
            foreach (var frame in harness.Collect(session))
            {
                largest = Math.Max(largest, frame.Length);
                var reliableBefore = events.Received.Count(e => e.Name == nameof(RelLetter));
                var bestBefore = events.Received.Count(e => e.Name == nameof(BestLetter));
                harness.Replica(session).Apply(frame);
                var reliableNow = events.Received.Count(e => e.Name == nameof(RelLetter));
                var bestNow = events.Received.Count(e => e.Name == nameof(BestLetter));
                shared += reliableNow > reliableBefore && bestNow > bestBefore ? 1 : 0;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(Seqs(harness, session, nameof(RelLetter)), Is.EqualTo(Enumerable.Range(0, 100)));
            Assert.That(largest, Is.LessThanOrEqualTo((FrameBytes / 2) + 512), "the events of a frame stay within half of it");
            Assert.That(harness.Replica(session).Events.Lost, Is.GreaterThan(0), "the best-effort ones beside the backlog were counted lost");
            Assert.That(shared, Is.GreaterThan(0), "a frame carrying the backlog also carried the best-effort letters that fit what it left of the half");
        });
    }

    /// <summary>
    /// A session owed nothing for longer than the reliable ring holds — every reliable event routed to someone else — is not closed: an idle frame moves its
    /// cursor too.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-35")]
    public void ASessionOwedNothingIsNeverClosedForReliability()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var creatures = Spawn(dbe, 2, 500f);
        using var harness = Harness(dbe, nameof(ASessionOwedNothingIsNeverClosedForReliability), Options(closeAfter: TimeSpan.FromMilliseconds(1)));
        harness.RunIngress = true;
        var sessions = harness.OpenSessions(2, "near");
        for (var i = 0; i < 2; i++)
        {
            Assert.That(harness.Sessions.SetViewpoint(sessions[i], new Vector3D(i * 500d, 0d, 0d)), Is.True);
            Assert.That(harness.Sessions.SetControlled(sessions[i], creatures[i]), Is.True);
        }

        var tick = 0L;
        var commands = harness.Subscriptions.Commands;
        for (var t = 0; t < harness.Subscriptions.Events.ReliableDepth * 3; t++)
        {
            commands.Emit(new RelHit { Target = creatures[0], Seq = t });
            harness.RunTick(++tick);
            harness.Deliver(sessions[0]);
            harness.Deliver(sessions[1]);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Sessions.IsOpen(sessions[1]), Is.True, "owed nothing, so never behind");
            Assert.That(harness.Assembler.SessionsClosedReliable, Is.Zero);
            Assert.That(Seqs(harness, sessions[0]), Has.Count.EqualTo(harness.Subscriptions.Events.ReliableDepth * 3));
        });
    }

    /// <summary>
    /// A session bound to no profile — frames of events alone — is kept reliable too: a gap past the log, then every broadcast of it, in order.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-35")]
    public void ASessionWithNoProfileIsKeptReliableAcrossAGap()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = Harness(dbe, nameof(ASessionWithNoProfileIsKeptReliableAcrossAGap));
        var session = harness.OpenSessions(1, null)[0];
        var tick = 0L;
        harness.RunTick(++tick);
        harness.Deliver(session);

        var commands = harness.Subscriptions.Commands;
        for (var t = 0; t < PushReplication.LogDepth + 4; t++)
        {
            commands.Emit(new RelNotice { Seq = t });
            harness.RunTick(++tick);
        }

        harness.Deliver(session);
        harness.RunTick(++tick);
        harness.Deliver(session);
        Assert.Multiple(() =>
        {
            Assert.That(Seqs(harness, session, nameof(RelNotice)), Is.EqualTo(Enumerable.Range(0, PushReplication.LogDepth + 4)));
            Assert.That(harness.Replica(session).Events.Lost, Is.Zero);
        });
    }

    /// <summary>
    /// The budget is the ring's bytes: a slot's events leave it when the ring reuses the slot, so a steady flow far larger than the budget over the ring's
    /// life is accepted whole, while one tick's burst past it is refused.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-35")]
    public void TheBudgetIsGivenBackAsTheRingMovesOn()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = Harness(dbe, nameof(TheBudgetIsGivenBackAsTheRingMovesOn), Options(reliableBytes: 16 * 1024));
        var session = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0L;
        var commands = harness.Subscriptions.Commands;
        var depth = harness.Subscriptions.Events.ReliableDepth;
        var accepted = 0;
        for (var t = 0; t < depth * 3; t++)
        {
            accepted += commands.Emit(new RelLetter { Text = Long, Seq = t }) ? 1 : 0;
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        var refused = 0;
        for (var i = 0; i < 80; i++)
        {
            refused += commands.Emit(new RelLetter { Text = Long, Seq = i }) ? 0 : 1;
        }

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.EqualTo(depth * 3), "three rings' worth, a ring's at a time under the budget");
            Assert.That(refused, Is.GreaterThan(0), "a burst past the budget");
            Assert.That(harness.Subscriptions.Events.ReliableRetained, Is.LessThanOrEqualTo(16 * 1024));
        });
    }

    /// <summary>
    /// Ticks no encode reached — no frame stage ran — leave the ring too: what a slot held a ring ago is released at the next encode, not when the ring next
    /// reaches that slot.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-35")]
    public void TicksNoEncodeReachedGiveTheirBytesBack()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = Harness(dbe, nameof(TicksNoEncodeReachedGiveTheirBytesBack), Options(reliableBytes: 4 * 1024));
        var session = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0L;
        harness.RunTick(++tick);
        harness.Deliver(session);

        var commands = harness.Subscriptions.Commands;
        var first = 0;
        for (var i = 0; i < 14; i++)
        {
            first += commands.Emit(new RelLetter { Text = Long, Seq = i }) ? 1 : 0;
        }

        harness.RunTick(++tick);
        harness.Deliver(session);
        Assert.That(harness.Subscriptions.Events.ReliableRetained, Is.GreaterThan(3 * 1024));

        for (var t = 0; t < harness.Subscriptions.Events.ReliableDepth; t++)
        {
            harness.SkipTick(++tick);
        }

        harness.RunTick(++tick);
        var second = 0;
        for (var i = 0; i < 14; i++)
        {
            second += commands.Emit(new RelLetter { Text = Long, Seq = i }) ? 1 : 0;
        }

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(14));
            Assert.That(harness.Subscriptions.Events.ReliableRetained, Is.Zero, "a ring of ticks with no encode: nothing is held");
            Assert.That(second, Is.EqualTo(14), "so the budget is whole again");
        });
    }

    /// <summary>
    /// A ring at its smallest — the close bound's floor — wrapping many times under a session that skips up to seven ticks at a time: every event once, in
    /// order, and the session lives.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-35")]
    public void TheRingWrapsUnderASkippingSessionAndLosesNothing()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = Harness(dbe, nameof(TheRingWrapsUnderASkippingSessionAndLosesNothing), Options(closeAfter: TimeSpan.FromMilliseconds(1)));
        var session = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0L;
        var commands = harness.Subscriptions.Commands;
        const int Ticks = 160;
        for (var t = 0; t < Ticks; t++)
        {
            commands.Emit(new RelNotice { Seq = t });
            harness.RunTick(++tick);

            // Drained after runs of 0 to 6 skipped ticks: never long enough to be closed.
            if (t % 7 == t / 7 % 7)
            {
                harness.Deliver(session);
            }
        }

        for (var q = 0; q < 4; q++)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        Assert.Multiple(() =>
        {
            Assert.That(Ticks, Is.GreaterThan(harness.Subscriptions.Events.ReliableDepth * 4), "the ring wrapped");
            Assert.That(Seqs(harness, session, nameof(RelNotice)), Is.EqualTo(Enumerable.Range(0, Ticks)));
            Assert.That(harness.Assembler.SessionsClosedLagging + harness.Assembler.SessionsClosedReliable, Is.Zero);
        });
    }

    /// <summary>A reliable event addressed to one session reaches it across a gap, and an emit past the budget is refused like any.</summary>
    [Test]
    [VerifiesRule("SUB-35")]
    public void AReliableEventToOneSessionReachesItAcrossAGap()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = Harness(dbe, nameof(AReliableEventToOneSessionReachesItAcrossAGap), Options(reliableBytes: 512));
        var sessions = harness.OpenSessions(2, "near");
        foreach (var session in sessions)
        {
            Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        }

        var tick = 0L;
        harness.RunTick(++tick);
        var commands = harness.Subscriptions.Commands;
        var sent = new List<int>();
        for (var t = 0; t < PushReplication.LogDepth + 4; t++)
        {
            if (commands.EmitTo(sessions[0], new RelWhisper { Seq = t }))
            {
                sent.Add(t);
            }

            harness.RunTick(++tick);
        }

        harness.Deliver(sessions[0]);
        harness.RunTick(++tick);
        harness.Deliver(sessions[0]);
        harness.Deliver(sessions[1]);
        Assert.Multiple(() =>
        {
            Assert.That(sent, Has.Count.EqualTo(PushReplication.LogDepth + 4));
            Assert.That(Seqs(harness, sessions[0], nameof(RelWhisper)), Is.EqualTo(sent));
            Assert.That(Seqs(harness, sessions[1], nameof(RelWhisper)), Is.Empty);
        });

        var refused = 0;
        for (var i = 0; i < 200; i++)
        {
            refused += commands.EmitTo(sessions[0], new RelWhisper { Seq = i }) ? 0 : 1;
        }

        Assert.That(refused, Is.GreaterThan(0), "EmitTo is bounded by the same budget");
    }

    /// <summary>
    /// A session served one tick in two and degraded to every other tick still produces frames, and is kept reliable: its two classes run on one staggered
    /// tick (the odd slot's frames were never built when the degrade class read the bare tick).
    /// </summary>
    [Test]
    [VerifiesRule("SUB-35")]
    public void ARateClassedDegradedSessionStillProducesAndStaysReliable()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = Harness(dbe, nameof(ARateClassedDegradedSessionStillProducesAndStaysReliable));
        var sessions = harness.OpenSessions(2, "near-every-2");
        var odd = sessions.First(s => s.Slot % 2 == 1);
        Assert.That(harness.Sessions.SetViewpoint(odd, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0L;
        harness.RunTick(++tick);
        harness.Deliver(odd);
        harness.StateOf(odd).DegradeLevel = 1;

        var commands = harness.Subscriptions.Commands;
        var frames = 0;
        for (var t = 0; t < 16; t++)
        {
            commands.Emit(new RelNotice { Seq = t });
            harness.RunTick(++tick);
            frames += harness.Deliver(odd) > 0 ? 1 : 0;
        }

        Assert.Multiple(() =>
        {
            Assert.That(frames, Is.GreaterThanOrEqualTo(3), "the session is served");
            Assert.That(Seqs(harness, odd, nameof(RelNotice)).Count, Is.GreaterThanOrEqualTo(12), "and its reliable events reach it, in order");
            Assert.That(Seqs(harness, odd, nameof(RelNotice)), Is.Ordered.Ascending.And.Unique);
        });
    }

    /// <summary>An emit that would take the kept bytes past ReliableEventBytes is refused at the emitter and counted; the accepted ones all arrive.</summary>
    [Test]
    [VerifiesRule("SUB-35")]
    public void AnEmitPastTheBudgetIsRefusedAtTheEmitter()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = Harness(dbe, nameof(AnEmitPastTheBudgetIsRefusedAtTheEmitter), Options(reliableBytes: 4 * 1024));
        var session = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0L;
        for (; tick < FillTicks;)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        var commands = harness.Subscriptions.Commands;
        var accepted = new List<int>();
        for (var i = 0; i < 40; i++)
        {
            if (commands.Emit(new RelLetter { Text = Long, Seq = i }))
            {
                accepted.Add(i);
            }
        }

        for (var t = 0; t < 4; t++)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.Not.Empty.And.Count.LessThan(40));
            Assert.That(harness.Subscriptions.Events.ReliableRefused, Is.EqualTo(40 - accepted.Count));
            Assert.That(Seqs(harness, session, nameof(RelLetter)), Is.EqualTo(accepted), "what was accepted arrives, all of it");
        });
    }

    /// <summary>
    /// A reliable event is tried at the emitter: a value its codec cannot carry — an enum value outside its names — is refused there and counted, never
    /// accepted and then dropped by the encode; a value that fits travels.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-35")]
    public void AReliableValueItsCodecCannotCarryIsRefusedAtTheEmitter()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            Declare(subs);
            subs.Event<RelLevel>(e => e.Broadcast().Reliable().Field(l => l.Mode, Codec.Enum<ProjAiMode>(bits: 3)));
        }, nameof(AReliableValueItsCodecCannotCarryIsRefusedAtTheEmitter), Options());
        harness.RunFence = true;
        var session = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0L;
        harness.RunTick(++tick);
        harness.Deliver(session);

        var commands = harness.Subscriptions.Commands;
        Assert.Multiple(() =>
        {
            Assert.That(commands.Emit(new RelLevel { Mode = (ProjAiMode)200, Seq = 1 }), Is.False, "200 is not one of the enum's names");
            Assert.That(commands.Emit(new RelLevel { Mode = ProjAiMode.Wander, Seq = 2 }), Is.True);
        });

        harness.RunTick(++tick);
        harness.Deliver(session);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Subscriptions.Events.ReliableInvalid, Is.EqualTo(1));
            Assert.That(harness.Subscriptions.Events.Rejected, Is.Zero, "nothing accepted was rejected by the encode");
            Assert.That(Seqs(harness, session, nameof(RelLevel)), Is.EqualTo(new[] { 2 }));
        });
    }

    /// <summary>A reliable event with no ReliableEventBytes: the budget is load-bearing, so Start refuses rather than guess one.</summary>
    [Test]
    [VerifiesRule("SUB-35")]
    public void AReliableEventWithoutItsBudgetIsRefusedAtStart()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Assert.That(() => FrameHarness.Create(dbe, Declare, nameof(AReliableEventWithoutItsBudgetIsRefusedAtStart), Options(reliableBytes: 0)),
            Throws.InvalidOperationException.With.Message.Contains("ReliableEventBytes is not set"));
    }
}
