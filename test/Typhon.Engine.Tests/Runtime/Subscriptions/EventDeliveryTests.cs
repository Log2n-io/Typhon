using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>An event routed to the session controlling its target, carrying the target as a netId.</summary>
public struct ProjHit
{
    public EntityId Target;
    public int Seq;
    public ushort Damage;
}

/// <summary>An event heard where it happens: by the sessions that see its point.</summary>
public struct ProjBoom
{
    public float X;
    public float Y;
    public int Seq;
}

/// <summary>An event heard by the sessions that know either of its entities.</summary>
public struct ProjDuel
{
    public EntityId A;
    public EntityId B;
    public int Seq;
}

/// <summary>An event addressed to one session.</summary>
public struct ProjWhisper
{
    public int Seq;
}

/// <summary>An event every session hears.</summary>
public struct ProjNotice
{
    public int Seq;
    public byte Kind;
}

/// <summary>Every codec shape an event field can take: packed flags, quantized and angular values, a signed varint, an entity with no replication.</summary>
public struct ProjRich
{
    public byte Flag;
    public byte Level;
    public float Pitch;
    public float Turn;
    public short Delta;
    public EntityId Who;
    public double Big;
}

/// <summary>A bool field: not blittable, so it cannot be read by offset.</summary>
public struct ProjBoolEvent
{
    public bool Flag;
}

/// <summary>
/// Server events to clients (09 § 11, step 2.5a): emitted by systems, encoded once, routed per session — to every session, or to the one controlling the
/// entity named — and caught up from the event log by a session that missed frames, or counted into <c>EventsLost</c> past it.
/// </summary>
[TestFixture]
[NonParallelizable]
sealed class EventDeliveryTests : TestBase<EventDeliveryTests>
{
    private const double Radius = 45d;
    private const int FillTicks = 6;

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile("near", p => p.Sphere(Radius).Of<ProjCreature>());
        subs.Event<ProjHit>(e => e.RouteToOwner(h => h.Target));
        subs.Event<ProjNotice>(e => e.Broadcast());
        subs.Event<ProjBoom>(e => e.RouteNear(b => new Vector3D(b.X, b.Y, 0d)));
        subs.Event<ProjDuel>(e => e.RouteToKnown(d => d.A, d => d.B));
        subs.Event<ProjWhisper>(e => e.RouteToSession());
    }

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

    private static FrameHarness Harness(DatabaseEngine dbe, string name) =>
        FrameHarness.Create(dbe, Declare, name, replicationCellM: ProjectionTestSchema.ReplicationCellFor(Radius));

    /// <summary>
    /// A broadcast reaches every session and an owner-routed event only the session controlling its target, in emission order, with every field's value —
    /// the target as the netId the session knows it by.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-21")]
    public void AnEventReachesItsSessionsWithItsValues()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var creatures = Spawn(dbe, 2, 500f);
        using var harness = Harness(dbe, nameof(AnEventReachesItsSessionsWithItsValues));
        harness.RunFence = true;
        var sessions = harness.OpenSessions(2, "near");
        for (var i = 0; i < 2; i++)
        {
            Assert.That(harness.Sessions.SetViewpoint(sessions[i], new Vector3D(i * 500d, 0d, 0d)), Is.True);
            Assert.That(harness.Sessions.SetControlled(sessions[i], creatures[i]), Is.True);
        }

        for (var tick = 1; tick <= FillTicks; tick++)
        {
            harness.RunTick(tick);
            harness.Deliver(sessions[0]);
            harness.Deliver(sessions[1]);
        }

        var creature = harness.CatalogPlan.ArchetypeByName(nameof(ProjCreature)).Idx;
        var held = harness.Replica(sessions[0]).NetIds(creature);
        Assert.That(held, Has.Length.EqualTo(1), "each session holds its own creature only");

        var commands = harness.Subscriptions.Commands;
        commands.Emit(new ProjHit { Target = creatures[0], Seq = 7, Damage = 42 });
        commands.Emit(new ProjNotice { Seq = 9, Kind = 3 });
        harness.RunTick(FillTicks + 1);
        harness.Deliver(sessions[0]);
        harness.Deliver(sessions[1]);

        var a = harness.Replica(sessions[0]).Events.Received;
        var b = harness.Replica(sessions[1]).Events.Received;
        Assert.Multiple(() =>
        {
            Assert.That(a, Has.Count.EqualTo(2));
            Assert.That(a[0].Name, Is.EqualTo(nameof(ProjHit)));
            Assert.That(a[0].Fields["Target"], Is.EqualTo(held[0]), "the target travels as the netId the session holds it by");
            Assert.That(a[0].Fields["Seq"], Is.EqualTo(7));
            Assert.That(a[0].Fields["Damage"], Is.EqualTo(42));
            Assert.That(a[1].Name, Is.EqualTo(nameof(ProjNotice)));
            Assert.That((a[1].Fields["Seq"], a[1].Fields["Kind"]), Is.EqualTo((9d, 3d)));
            Assert.That(b, Has.Count.EqualTo(1), "the other session hears the broadcast only");
            Assert.That(b[0].Name, Is.EqualTo(nameof(ProjNotice)));
            Assert.That(harness.Subscriptions.Events.Rejected, Is.Zero);
        });
    }

    /// <summary>
    /// The routing oracle (SUB-21): random broadcasts, owner-routed, near and known events every tick, sessions skipping frames at a given rate. Every session receives
    /// exactly the events routed to it — each once, in emission order — less those the log no longer held, which its <c>EventsLost</c> counts exactly.
    /// </summary>
    /// <param name="skipPercent">The percentage of ticks on which a session's frames are left undrained.</param>
    [Test]
    [VerifiesRule("SUB-21")]
    [VerifiesRule("SUB-03")]
    public void EveryEventReachesExactlyItsSessionsAtEverySkipRate([Values(0, 30, 60, 90)] int skipPercent)
    {
        const int SessionCount = 6;
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var creatures = Spawn(dbe, SessionCount + 1, 20f);
        using var harness = Harness(dbe, nameof(EveryEventReachesExactlyItsSessionsAtEverySkipRate) + skipPercent);
        harness.RunFence = true;
        var sessions = harness.OpenSessions(SessionCount, "near");
        for (var i = 0; i < SessionCount; i++)
        {
            Assert.That(harness.Sessions.SetViewpoint(sessions[i], new Vector3D(i * 20d, 0d, 0d)), Is.True);

            // The last session controls nothing: it hears the broadcasts only.
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

        var rng = new Random(9100 + skipPercent);
        var expected = new List<int>[SessionCount];
        for (var i = 0; i < SessionCount; i++)
        {
            expected[i] = [];
        }

        var commands = harness.Subscriptions.Commands;
        var seq = 0;
        for (var t = 0; t < 150; t++, tick++)
        {
            var count = rng.Next(0, 4);
            for (var k = 0; k < count; k++)
            {
                seq++;
                var kind = rng.Next(5);
                if (kind >= 3)
                {
                    // Geometric: at a creature's point (Near), or naming a creature (ToKnown). Sessions stand 20 m apart with R = 45 m, so the sessions
                    // that see creature c are exactly those within two steps of it — 5 m inside the radius, 15 m outside.
                    var c = rng.Next(creatures.Length);
                    if (kind == 3)
                    {
                        commands.Emit(new ProjBoom { X = c * 20f, Y = 0f, Seq = seq });
                    }
                    else
                    {
                        commands.Emit(new ProjDuel { A = creatures[c], B = creatures[c], Seq = seq });
                    }

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
                    commands.Emit(new ProjNotice { Seq = seq, Kind = 1 });
                    foreach (var list in expected)
                    {
                        list.Add(seq);
                    }
                }
                else
                {
                    // Any creature, the uncontrolled one included: its events reach nobody.
                    var target = rng.Next(creatures.Length);
                    commands.Emit(new ProjHit { Target = creatures[target], Seq = seq, Damage = 1 });
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

        // Quiet ticks, every session drained: whatever is owed arrives.
        for (var q = 0; q < 4; q++, tick++)
        {
            harness.RunTick(tick + 1);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }
        }

        var lostSeen = 0L;
        Assert.Multiple(() =>
        {
            for (var i = 0; i < SessionCount; i++)
            {
                var recorder = harness.Replica(sessions[i]).Events;
                var got = new List<int>();
                foreach (var (_, fields) in recorder.Received)
                {
                    got.Add((int)fields["Seq"]);
                }

                var routed = new HashSet<int>(expected[i]);
                Assert.That(got, Is.Ordered.Ascending.And.Unique, $"session {i}: once each, in emission order");
                Assert.That(got.TrueForAll(routed.Contains), Is.True, $"session {i}: an event not routed to it");
                Assert.That(got.Count + recorder.Lost, Is.EqualTo(expected[i].Count), $"session {i}: received + lost = routed");
                Assert.That(recorder.LostOutOfPlace, Is.Zero, $"session {i}: EventsLost opens its frame's block");
                lostSeen += recorder.Lost;
            }
        });

        if (skipPercent == 0)
        {
            Assert.That(lostSeen, Is.Zero, "a session drained every tick loses nothing");
        }
        else if (skipPercent == 90)
        {
            Assert.That(lostSeen, Is.GreaterThan(0), "no session was skipped past the log: the loss count did not run");
        }
    }

    /// <summary>
    /// The compiled encoder against the client's decoder, across codec shapes — packed bool and bits, quant, angle, varint, an entity with no replication,
    /// f32 — each value arriving as its codec rounds it; a value its codec cannot carry drops that event, counted, and the tick goes on.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-21")]
    public void EveryCodecShapeRoundTripsAndABadValueDropsOnlyItsEvent()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("near", p => p.Sphere(Radius).Of<ProjCreature>());
            subs.Event<ProjRich>(e => e.Broadcast()
                .Field(r => r.Flag, Codec.Bool)
                .Field(r => r.Level, Codec.Bits(5))
                .Field(r => r.Pitch, Codec.Quant(-10, 10, 16))
                .Field(r => r.Turn, Codec.Angle(16))
                .Field(r => r.Delta, Codec.VarInt)
                .Field(r => r.Big, Codec.F32));
        }, nameof(EveryCodecShapeRoundTripsAndABadValueDropsOnlyItsEvent), replicationCellM: ProjectionTestSchema.ReplicationCellFor(Radius));
        harness.RunFence = true;
        var session = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        harness.RunTick(1);
        harness.Deliver(session);

        var commands = harness.Subscriptions.Commands;
        commands.Emit(new ProjRich { Flag = 1, Level = 40, Pitch = 1f, Turn = 1f, Delta = 1, Big = 1d });
        EntityId player;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var vitals = new ProjVitals { Health = 1, MaxHealth = 1 };
            player = tx.Spawn<ProjPlayer>(ProjPlayer.Bounds.Set(PointAt(1f, 1f)), ProjPlayer.Vitals.Set(in vitals));
            tx.Commit();
        }

        // A live entity of an archetype no profile replicates: it has no netId, so it travels as 0.
        commands.Emit(new ProjRich { Flag = 1, Level = 21, Pitch = 3.3f, Turn = 1.2f, Delta = -300, Who = player, Big = 1234.5d });
        harness.RunTick(2);
        harness.Deliver(session);

        var got = harness.Replica(session).Events.Received;
        Assert.Multiple(() =>
        {
            Assert.That(harness.Subscriptions.Events.Rejected, Is.EqualTo(1), "a level of 40 does not fit five bits");
            Assert.That(got, Has.Count.EqualTo(1), "the bad event is dropped, the good one travels");
            var f = got[0].Fields;
            Assert.That(f["Flag"], Is.EqualTo(1));
            Assert.That(f["Level"], Is.EqualTo(21));
            Assert.That(f["Pitch"], Is.EqualTo(3.3).Within(20.0 / 65535));
            Assert.That(f["Turn"], Is.EqualTo(1.2).Within(2 * Math.PI / 65536));
            Assert.That(f["Delta"], Is.EqualTo(-300));
            Assert.That(f["Who"], Is.Zero, "a live entity with no replication is netId 0, unknown");
            Assert.That(f["Big"], Is.EqualTo(1234.5));
        });
    }

    /// <summary>An emission of an undeclared type is refused where it is made.</summary>
    [Test]
    public void AnUndeclaredEventIsRefusedWhereItIsEmitted()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using var harness = Harness(dbe, nameof(AnUndeclaredEventIsRefusedWhereItIsEmitted));
        Assert.That(() => harness.Subscriptions.Commands.Emit(new ProjBoolEvent()), Throws.InvalidOperationException.With.Message.Contains("not a declared"));
    }

    /// <summary>A bool field, which cannot be read by offset, is refused at Start.</summary>
    [Test]
    public void ANonBlittableEventIsRefusedAtStart()
    {
        var dbe2 = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Assert.That(() => FrameHarness.Create(dbe2, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("near", p => p.Sphere(Radius).Of<ProjCreature>());
            subs.Event<ProjBoolEvent>(e => e.Broadcast().Field(b => b.Flag, Codec.Bool));
        }, nameof(ANonBlittableEventIsRefusedAtStart)).Dispose(), Throws.InvalidOperationException.With.Message.Contains("blittable"));
    }

    /// <summary>
    /// The geometric routes' edges: an event naming two entities a session knows reaches it once; a near event with a viewpoint radius reaches only the
    /// sessions within it; a session-routed event only its session; and an event naming an entity destroyed this tick still carries the netId the session
    /// knew it by, and reaches the sessions that knew it.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-21")]
    public void GeometricRoutesDedupeRespectTheirRadiusAndNameTheDestroyed()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var creatures = Spawn(dbe, 3, 20f);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            Declare(subs);
            subs.Event<ProjRich>(e => e.RouteNear(r => new Vector3D(r.Pitch, 0d, 0d), radiusM: 10d).Field(r => r.Big, Codec.F32));
        }, nameof(GeometricRoutesDedupeRespectTheirRadiusAndNameTheDestroyed), replicationCellM: ProjectionTestSchema.ReplicationCellFor(Radius));
        harness.RunFence = true;
        var sessions = harness.OpenSessions(3, "near");
        for (var i = 0; i < 3; i++)
        {
            Assert.That(harness.Sessions.SetViewpoint(sessions[i], new Vector3D(i * 20d, 0d, 0d)), Is.True);
        }

        for (var tick = 1; tick <= FillTicks; tick++)
        {
            harness.RunTick(tick);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }
        }

        var creature = harness.CatalogPlan.ArchetypeByName(nameof(ProjCreature)).Idx;
        var knownBy0 = new HashSet<uint>(harness.Replica(sessions[0]).NetIds(creature));
        Assert.That(knownBy0, Has.Count.EqualTo(3), "session 0 (at 0 m, R = 45 m) holds all three creatures");
        var third = 0u;
        foreach (var id in knownBy0)
        {
            if (Math.Abs(harness.Replica(sessions[0]).Position(creature, id)[0] - 40d) < 1d)
            {
                third = id;
            }
        }

        Assert.That(third, Is.Not.Zero, "session 0 holds the creature at 40 m");

        var commands = harness.Subscriptions.Commands;
        commands.Emit(new ProjDuel { A = creatures[0], B = creatures[1], Seq = 1 });
        commands.Emit(new ProjRich { Pitch = 0f });
        commands.EmitTo(sessions[2], new ProjWhisper { Seq = 3 });
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(creatures[2]);
            tx.Commit();
        }

        commands.Emit(new ProjDuel { A = creatures[2], B = EntityId.Null, Seq = 4 });
        harness.RunTick(FillTicks + 1);
        foreach (var session in sessions)
        {
            harness.Deliver(session);
        }

        List<(string Name, Dictionary<string, double> Fields)> Got(int i) => harness.Replica(sessions[i]).Events.Received;
        Assert.Multiple(() =>
        {
            var duels0 = Got(0).FindAll(e => e.Name == nameof(ProjDuel));
            Assert.That(duels0, Has.Count.EqualTo(2), "session 0 knows both duellists: the first duel once, and the destroyed one's");
            Assert.That(duels0[1].Fields["A"], Is.EqualTo(third), "a destroyed entity names the netId session 0 knew it by");
            for (var i = 1; i < 3; i++)
            {
                Assert.That(Got(i).Exists(e => e.Name == nameof(ProjDuel) && e.Fields["Seq"] == 4), Is.True,
                    $"session {i} knew the destroyed creature too, and hears of it");
            }
            Assert.That(Got(0).FindAll(e => e.Name == nameof(ProjRich)), Has.Count.EqualTo(1), "within 10 m of the point");
            Assert.That(Got(1).FindAll(e => e.Name == nameof(ProjRich)), Is.Empty, "20 m from the point: outside the event's radius");
            Assert.That(Got(2).FindAll(e => e.Name == nameof(ProjWhisper)), Has.Count.EqualTo(1));
            Assert.That(Got(0).FindAll(e => e.Name == nameof(ProjWhisper)), Is.Empty, "a whisper reaches its session only");
        });

        Assert.Multiple(() =>
        {
            Assert.That(() => commands.Emit(new ProjWhisper()), Throws.InvalidOperationException.With.Message.Contains("EmitTo"));
            Assert.That(() => commands.EmitTo(sessions[0], new ProjNotice()), Throws.InvalidOperationException.With.Message.Contains("RouteToSession"));
        });
    }

    /// <summary>
    /// 2.5's acceptance, measured: a steady tick with events allocates no more managed memory than the same tick without (the segments, arenas and routing
    /// lists only grow past their high-water marks), and the serial encode and route cost per event is reported.
    /// </summary>
    [Test]
    public void EventsAllocateNothingPerEventAndReportTheirCost()
    {
        const int SessionCount = 8;
        const int EventsPerTick = 120;
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var creatures = Spawn(dbe, SessionCount, 20f);
        using var harness = Harness(dbe, nameof(EventsAllocateNothingPerEventAndReportTheirCost));
        harness.RunFence = true;
        var sessions = harness.OpenSessions(SessionCount, "near");
        for (var i = 0; i < SessionCount; i++)
        {
            harness.Sessions.SetViewpoint(sessions[i], new Vector3D(i * 20d, 0d, 0d));
            harness.Sessions.SetControlled(sessions[i], creatures[i]);
        }

        var commands = harness.Subscriptions.Commands;
        var tick = 0;
        void Run(int ticks, int perTick)
        {
            for (var t = 0; t < ticks; t++)
            {
                for (var k = 0; k < perTick; k++)
                {
                    var c = k % SessionCount;
                    switch (k % 4)
                    {
                        case 0:
                            commands.Emit(new ProjNotice { Seq = k, Kind = 1 });
                            break;
                        case 1:
                            commands.Emit(new ProjHit { Target = creatures[c], Seq = k, Damage = 1 });
                            break;
                        case 2:
                            commands.Emit(new ProjBoom { X = c * 20f, Y = 0f, Seq = k });
                            break;
                        default:
                            commands.Emit(new ProjDuel { A = creatures[c], B = creatures[(c + 1) % SessionCount], Seq = k });
                            break;
                    }
                }

                harness.RunTick(++tick);
                foreach (var session in sessions)
                {
                    harness.Drain(session);
                }
            }
        }

        // Past the loss summary's depth: each of its slots grows its routing arrays the first time a tick lands in it.
        Run(EventHub.SummaryDepth + 8, EventsPerTick);

        // The track runs on this thread, so this thread's count is the track's alone. Both windows publish a frame to every session every tick; what differs
        // is four times the events. The smallest of three interleaved pairs, so a one-off (a tier-up, a lazy init) cannot decide it.
        var hub = harness.Subscriptions.Events;
        var perEvent = long.MaxValue;
        var fewer = 0L;
        var more = 0L;
        var encoded = 0L;
        var encodeTicks = 0L;
        for (var pair = 0; pair < 3; pair++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            Run(20, EventsPerTick / 4);
            var a = GC.GetAllocatedBytesForCurrentThread() - before;
            var encodedBefore = hub.Encoded;
            var ticksBefore = hub.EncodeTicks;
            before = GC.GetAllocatedBytesForCurrentThread();
            Run(20, EventsPerTick);
            var b = GC.GetAllocatedBytesForCurrentThread() - before;
            encoded += hub.Encoded - encodedBefore;
            encodeTicks += hub.EncodeTicks - ticksBefore;
            if (b - a < perEvent)
            {
                (perEvent, fewer, more) = (b - a, a, b);
            }
        }

        var perEventUs = encodeTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, encoded);
        TestContext.Out.WriteLine($"this thread's bytes over 20 ticks: {more} at {EventsPerTick} events/tick, {fewer} at {EventsPerTick / 4}; " +
            $"encode + route {perEventUs:F3} µs/event (serial)");
        Assert.That(perEvent, Is.LessThanOrEqualTo(0), "four times the events allocate more: something is allocated per event");
    }

    /// <summary>
    /// A World session hears the geometric routes of every cell it has delivered, whatever its distance; a session opened mid-run hears only what is emitted
    /// from its first frame on; and a tick's events travel in worker-slot order, then call order.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-21")]
    public void WorldSessionsLateSessionsAndWorkerOrder()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var creatures = Spawn(dbe, 2, 400f);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            Declare(subs);
            subs.Profile("world", p => p.World().Of<ProjCreature>());
        }, nameof(WorldSessionsLateSessionsAndWorkerOrder), replicationCellM: ProjectionTestSchema.ReplicationCellFor(Radius));
        harness.RunFence = true;
        var near = harness.OpenSessions(1, "near")[0];
        var world = harness.OpenSessions(1, "world")[0];
        Assert.That(harness.Sessions.SetViewpoint(near, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0;
        void Run(params SessionId[] deliver)
        {
            harness.RunTick(++tick);
            foreach (var session in deliver)
            {
                harness.Deliver(session);
            }
        }

        for (var i = 0; i < FillTicks; i++)
        {
            Run(near, world);
        }

        var commands = harness.Subscriptions.Commands;
        commands.Emit(new ProjBoom { X = 400f, Y = 0f, Seq = 1 });
        commands.Emit(new ProjDuel { A = creatures[1], B = EntityId.Null, Seq = 2 });
        Run(near, world);

        // A late session: opened after events were emitted, it hears only its own ticks'.
        var late = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(late, new Vector3D(0d, 0d, 0d)), Is.True);
        commands.Emit(new ProjNotice { Seq = 3, Kind = 1 });
        Run(near, world, late);

        // Worker slots: emitted slot 2, then 1, then 0; delivered slot 0 first.
        var hub = harness.Subscriptions.Events;
        hub.BindWorkerSlots(2);
        hub.Emit(2, new ProjNotice { Seq = 10, Kind = 1 });
        hub.Emit(1, new ProjNotice { Seq = 11, Kind = 1 });
        hub.Emit(0, new ProjNotice { Seq = 12, Kind = 1 });
        Run(near, world, late);

        List<int> Seqs(SessionId s)
        {
            var list = new List<int>();
            foreach (var (_, fields) in harness.Replica(s).Events.Received)
            {
                list.Add((int)fields["Seq"]);
            }

            return list;
        }

        Assert.Multiple(() =>
        {
            Assert.That(Seqs(world), Is.EqualTo(new[] { 1, 2, 3, 12, 11, 10 }), "the World session hears the far boom and duel, then the rest in slot order");
            Assert.That(Seqs(near), Is.EqualTo(new[] { 3, 12, 11, 10 }), "400 m away: the near session hears neither the boom nor the duel");
            Assert.That(Seqs(late), Is.EqualTo(new[] { 3, 12, 11, 10 }), "the late session hears nothing emitted before it existed");
        });
    }

    /// <summary>
    /// The tick path does not fail on events: a routing point that throws drops its event, counted, and the next tick's events travel; a burst larger than
    /// half a frame is counted into EventsLost rather than making the frame oversize; emissions of a tick no frame stage encoded are discarded, not
    /// delivered later.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-21")]
    public void AThrowingPointABurstAndAnUnencodedTickDoNotBreakTheTick()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, 1, 0f);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            Declare(subs);
            subs.Event<ProjRich>(e => e.RouteNear(r => r.Level == 99 ? throw new InvalidOperationException("boom") : new Vector3D(0d, 0d, 0d))
                .Field(r => r.Big, Codec.F32));
        }, nameof(AThrowingPointABurstAndAnUnencodedTickDoNotBreakTheTick), new SubscriptionsOptions
        {
            MaxSessions = 4,
            FrameBytes = 4096,
            StatePoolBudgetBytes = 64L * 1024 * 1024,
            FramePoolBudgetBytes = 64L * 1024 * 1024,
            ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
        });
        harness.RunFence = true;
        var session = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0;
        for (; tick < FillTicks;)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        var hub = harness.Subscriptions.Events;
        var commands = harness.Subscriptions.Commands;
        var recorder = harness.Replica(session).Events;

        // A point that throws: that event is dropped, the one after it travels.
        commands.Emit(new ProjRich { Level = 99 });
        commands.Emit(new ProjNotice { Seq = 1, Kind = 1 });
        harness.RunTick(++tick);
        harness.Deliver(session);
        Assert.Multiple(() =>
        {
            Assert.That(hub.Rejected, Is.EqualTo(1));
            Assert.That(recorder.Received.ConvertAll(e => e.Name), Is.EqualTo(new[] { nameof(ProjNotice) }));
        });

        // A burst: 1 000 notices, several kilobytes against a 4 KiB frame — counted, and the frame still goes.
        for (var i = 0; i < 1000; i++)
        {
            commands.Emit(new ProjNotice { Seq = 100 + i, Kind = 1 });
        }

        harness.RunTick(++tick);
        Assert.That(harness.Deliver(session), Is.EqualTo(1), "the frame was published, not refused as oversize");
        Assert.Multiple(() =>
        {
            Assert.That(recorder.Received, Has.Count.EqualTo(1), "none of the burst is sent");
            Assert.That(recorder.Lost, Is.EqualTo(1000), "all of it is counted");
        });

        // A tick no frame stage encoded: its emissions are discarded when the next tick starts, not delivered then.
        commands.Emit(new ProjNotice { Seq = 5000, Kind = 1 });
        hub.OnTickStart((uint)tick + 2);
        harness.RunTick(++tick);
        harness.Deliver(session);
        Assert.Multiple(() =>
        {
            Assert.That(hub.DiscardedBytes, Is.GreaterThan(0));
            Assert.That(recorder.Received, Has.Count.EqualTo(1), "the discarded notice never arrives");
        });
    }

    private static void MoveCreature(DatabaseEngine dbe, EntityId creature, float x)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (cluster.GetEntityId(slot).RawValue == creature.RawValue)
                    {
                        cluster.WriteSpatial(ProjCreature.Bounds, slot, PointAt(x, 0f));
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    /// <summary>
    /// RouteToKnown is was ∨ is (09 § 11): an entity that leaves a session's view in the event's own tick — "X killed Y" as Y walks out — still reaches the
    /// session that knew it where it was.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-21")]
    public void AKnownEventReachesASessionTheEntityLeftThisTick()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var creature = Spawn(dbe, 1, 0f)[0];
        MoveCreature(dbe, creature, 42f);
        using var harness = Harness(dbe, nameof(AKnownEventReachesASessionTheEntityLeftThisTick));
        harness.RunFence = true;
        var session = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var tick = 0;
        for (; tick < FillTicks;)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        var plan = harness.CatalogPlan.ArchetypeByName(nameof(ProjCreature)).Idx;
        Assert.That(harness.Replica(session).NetIds(plan), Has.Length.EqualTo(1), "the session holds the creature at 42 m");

        // In one tick: the creature steps to 60 m, out of the 45 m sphere and into another cell, and the event naming it is emitted.
        MoveCreature(dbe, creature, 60f);
        harness.Subscriptions.Commands.Emit(new ProjDuel { A = creature, B = EntityId.Null, Seq = 7 });
        harness.RunTick(++tick);
        harness.Deliver(session);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(session).NetIds(plan), Is.Empty, "the creature left the view");
            Assert.That(harness.Replica(session).Events.Received.Exists(e => e.Name == nameof(ProjDuel)), Is.True, "and the event about it arrived first");
        });
    }

    /// <summary>A session bound to no profile has no view, but broadcasts and its own EmitTo reach it, in frames of events alone; nothing else does.</summary>
    [Test]
    [VerifiesRule("SUB-21")]
    public void ASessionWithNoProfileHearsBroadcastsAndItsOwnEvents()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var creatures = Spawn(dbe, 1, 0f);
        using var harness = Harness(dbe, nameof(ASessionWithNoProfileHearsBroadcastsAndItsOwnEvents));
        harness.RunFence = true;
        var near = harness.OpenSessions(1, "near")[0];
        var lobby = harness.OpenSessions(1, "near")[0];
        Assert.That(harness.Sessions.SetViewpoint(near, new Vector3D(0d, 0d, 0d)), Is.True);
        Assert.That(harness.Sessions.SetProfile(lobby, null), Is.True);
        Assert.That(harness.Sessions.SetControlled(lobby, creatures[0]), Is.True);
        var tick = 0;
        for (; tick < FillTicks;)
        {
            harness.RunTick(++tick);
            harness.Deliver(near);
            harness.Deliver(lobby);
        }

        var commands = harness.Subscriptions.Commands;
        commands.Emit(new ProjNotice { Seq = 1, Kind = 1 });
        commands.EmitTo(lobby, new ProjWhisper { Seq = 2 });
        commands.Emit(new ProjBoom { X = 0f, Y = 0f, Seq = 3 });
        commands.Emit(new ProjDuel { A = creatures[0], B = EntityId.Null, Seq = 4 });
        harness.RunTick(++tick);
        harness.Deliver(near);
        harness.Deliver(lobby);

        // Missed frames catch up from the log, as any session's do.
        commands.Emit(new ProjNotice { Seq = 5, Kind = 1 });
        harness.RunTick(++tick);
        commands.Emit(new ProjNotice { Seq = 6, Kind = 1 });
        harness.RunTick(++tick);
        harness.Deliver(lobby);

        var seqs = harness.Replica(lobby).Events.Received.ConvertAll(e => (int)e.Fields["Seq"]);
        Assert.That(seqs, Is.EqualTo(new[] { 1, 2, 5, 6 }), "the broadcast and the whisper — no near, known or owner event: it has no view, and controls in none");
    }
}
