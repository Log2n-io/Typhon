using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace SwgTatooine.Tests;

/// <summary>
/// SWG-08 — #1060's crossing and routing checks, on the demo and on the wire: a portal round trip keeps the identity, the switch is one
/// <c>RESET|REALM</c>, and a planet's announcement reaches its own buildings and nobody else's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asserted on the frames the client received, never on server state.</b> Each of these is a claim about what a client is told, and the engine's own
/// analogues (<c>RealmSessionTests</c>, <c>RealmEventTests</c>) already make the same claims at unit scale against internal surfaces. What is untested is the
/// whole stack at SWG scale — a two-planet galaxy with a realm per building and the demo's own replication declaration in between — which is exactly what
/// #1053 exists for.
/// </para>
/// <para>
/// <b>The destination realm is pinned before anything is moved into it, and that is not a convenience.</b> An interior nobody is in is dormant, and the fence
/// does not index into a dormant realm — a player teleported there is in neither realm's spatial index until something wakes it. The demo never produces that
/// state: its portal path arrives with the player's own session, and its dungeons pin the realm before filling it. <c>Realms.Observe</c> is the public pin, and
/// its own documentation names this use ("for an application with no session in the realm — a headless server, a benchmark, a test").
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class RealmCrossingChecks
{
    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Worlds.NewDirectory();
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
    }

    [TearDown]
    public void TearDown()
    {
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        Worlds.Delete(_dir);
    }

    private SimConfig Config()
    {
        var config = Worlds.Small(_dir);
        config.Unpaced = false;
        config.TickRateHz = 40;
        config.WorkerCount = 1;
        config.Planets = 2;
        config.Interiors = true;
        config.Shuttles = false;

        // <b>No dungeons, and the announcement case depends on it.</b> A dungeon opening or closing emits a RealmNews on planet 0's subtree too, which would
        // satisfy that case's wait whether or not GmAnnounce worked at all. Zero is SimConfig's default and this states it rather than relying on it — the
        // sibling fixtures push DungeonIntervalS out to an hour for the same reason, and setting it here later would hollow the claim out silently.
        config.Dungeons = 0;
        return config;
    }

    /// <summary>What these cases read of a client's frames: its realm, its resets, its own netId and the events it heard.</summary>
    private struct CrossingSink : ITickSink
    {
        /// <summary>Every realm the session was told it was in, in order; <see cref="RealmFrame.NoRealm"/> for <c>REALM(NONE)</c>.</summary>
        public List<int> Realms;

        /// <summary>How many <c>RESET</c> frames arrived.</summary>
        public int Resets;

        /// <summary>The netId the last <c>SELF</c> named — how a client knows which entity is its own.</summary>
        public uint SelfNetId;

        /// <summary>Every <c>SELF</c> netId seen, so a change can be named rather than merely detected.</summary>
        public List<uint> SelfNetIds;

        /// <summary>Events delivered, by declared name.</summary>
        public List<string> Events;

        public void BeginTick(uint tick, TickFlags flags, uint periodUs)
        {
            if ((flags & TickFlags.Reset) != 0)
            {
                Resets++;
            }
        }

        public void Realm(RealmFrame frame) => Realms.Add(frame?.RealmId ?? RealmFrame.NoRealm);

        public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask)
        {
            if (netId != 0)
            {
                SelfNetId = netId;
                SelfNetIds.Add(netId);
            }
        }

        public void Event(MessagePlan type) => Events.Add(type.Name);

        public void BeginEntities(ArchetypePlan archetype)
        {
        }

        public void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
        {
        }

        public void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
        {
        }

        public void State(uint netId, byte groupMask)
        {
        }

        public void Leave(uint netId)
        {
        }

        public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
        {
        }

        public void Integer64(FieldPlan field, scoped ReadOnlySpan<ulong> components)
        {
        }

        public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
        {
        }

        public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
        {
        }

        public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
        {
        }

        public void Ack(ushort seq, byte reason)
        {
        }

        public void Source(ushort requestId, byte status, ushort code)
        {
        }

        public void BeginAggregate(CatalogGrid grid, bool reset)
        {
        }

        public void AggregateCell(uint cell, scoped ReadOnlySpan<uint> counts)
        {
        }

        public void Metric(MetricPlan metric, int valueIndex, double value)
        {
        }

        public void Debug(byte subType, scoped ReadOnlySpan<byte> payload)
        {
        }

        public void Ext(uint appTypeId, scoped ReadOnlySpan<byte> payload)
        {
        }

        public void UnknownBlock(byte blockType)
        {
        }

        public void EndTick()
        {
        }
    }

    /// <summary>A connected client whose frames are decoded on demand.</summary>
    private sealed class Viewer
    {
        private readonly FakeLink _link;
        private int _read;
        private RealmFrame _frame;
        private CrossingSink _sink = new() { Realms = [], SelfNetIds = [], Events = [] };

        public Viewer(SessionHarness harness, string kind)
        {
            _link = harness.Connect(kind);
            SessionHarness.Until(() => _link.Received(MessageTypes.Welcome), $"the {kind} client's WELCOME");
        }

        public FakeLink Link => _link;

        /// <summary>Decodes everything that arrived since the last call.</summary>
        public CrossingSink Drain()
        {
            var sent = _link.Sent;
            for (; _read < sent.Length; _read++)
            {
                var message = sent[_read];
                if (message.Length > 0 && message[0] == MessageTypes.Tick)
                {
                    TickReader.Read(message, _link.Plan, ref _frame, ref _sink);
                }
            }

            return _sink;
        }

        /// <summary>The realm this client is in now, as its own frames say.</summary>
        /// <remarks>
        /// One <c>Drain</c>, not two: each one copies the link's whole sent-message list under a lock, and this is read from inside spin predicates — so two
        /// per evaluation is quadratic in the frames the case has produced.
        /// </remarks>
        public int Realm
        {
            get
            {
                var sink = Drain();
                return sink.Realms.Count > 0 ? sink.Realms[^1] : RealmFrame.NoRealm;
            }
        }

        public void Send(string command, RecordValues values) => _link.SendCommand(command, values);
    }

    /// <summary>
    /// Moves a player into a realm and tells the simulation to leave it there, the way a dungeon's party is moved.
    /// </summary>
    private static void MoveTo(TatooineSim sim, EntityId id, ushort realm, float x, float z)
    {
        using var tx = sim.Dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit);
        Assert.That(tx.TryOpenMut(id, out var player), Is.True, $"player {id} is not there to move");
        ref var state = ref player.Write(Player.State);

        // Inside with a counter no run reaches, as SimBridge.Open sets it for a dungeon party: PlayerThink would otherwise walk the player straight back out
        // through the door and take the case's precondition with it.
        state.Activity = PlayerActivity.Inside;
        state.ActivityTicks = int.MaxValue / 2;
        ref var motion = ref player.Write(Player.Move);
        motion.VelX = 0f;
        motion.VelZ = 0f;
        var at = default(PlayerPlacement);
        at.SetAt(x, z, 0f, player.Read(Player.Bounds).HalfExtent);
        tx.Teleport(id, Player.Bounds, new RealmId(realm), in at);
        Assert.That(tx.Commit(), Is.True);
    }

    /// <summary>
    /// <c>Switch_IsOneResetRealmFrame</c> — each leg of a portal round trip is exactly one <c>RESET</c> whose first block is the realm arrived in.
    /// </summary>
    /// <remarks>
    /// <b>Exactly one <c>RESET</c> per crossing, not at least one.</b> Two would mean the realm switch and the profile-variant re-resolve were not folded into
    /// the same reset, which is a wasted full re-send of a whole world — and it is the specific thing 12-realms § 1.4 promises when it refuses app-driven
    /// profile switches as the mechanism for realm variants. The engine's analogue is
    /// <c>RealmSessionTests.PlacingIntoAnotherRealmIsOneResetRealmFrame</c>; what this adds is the demo's own declaration, its own profile variants and its own
    /// portal geometry in between.
    /// </remarks>
    [Test]
    public void Switch_IsOneResetRealmFrame()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        var harness = new SessionHarness(sim);
        try
        {
            var trip = RoundTrip(sim, harness);
            Assert.Multiple(() =>
            {
                Assert.That(trip.ResetsEntering, Is.EqualTo(1), "one crossing in, one RESET");
                Assert.That(trip.RealmEntering, Is.EqualTo(trip.Interior), "and the realm it names is the one arrived in");
                Assert.That(trip.ResetsLeaving, Is.EqualTo(1), "one crossing back, one RESET");
                Assert.That(trip.RealmLeaving, Is.Zero, "and it names the planet");
            });
        }
        finally
        {
            harness.Release();
        }
    }

    /// <summary>
    /// <c>Portal_RoundTrip_NetIdStable</c> — a player's own client keeps calling it by the same name across a door. <b>It does not (#1081).</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Quarantined, asserting the contract rather than the behaviour.</b> #1060 names this check as a done-when; it fails, deterministically, 5 runs out of
    /// 5, and at engine scale as well as here. A realm switch is a <c>RESET</c>, so the session's view is emptied, the entity leaves the only known-set holding
    /// it, its global identity is released to the quarantine and the re-entry allocates a fresh one. That is right for an entity the session has really
    /// lost and wrong for the entity it is ANCHORED TO, which the switch guarantees will be there on the other side.
    /// </para>
    /// <para>
    /// Written as the check #1060 asks for and left red against #1081, rather than weakened to assert what happens today: a test that asserted the
    /// re-identification would make the defect a specification, and a check left unwritten would leave the done-when looking unexamined instead of examined and
    /// failed. <c>[Category("Quarantine")]</c> keeps it out of the merge gate — which runs this project with <c>TestCategory!=Quarantine</c> — and it runs
    /// <b>locally only</b>: the nightly filters the category out too (<c>nightly-suppressed.yml</c>), so nothing runs this until #1081 is picked up.
    /// <c>QUARANTINE.md</c> carries its row, which is what makes it a register rather than a red test somebody has to remember.
    /// </para>
    /// <para>
    /// <b>What it costs a client:</b> everything keyed on netId is lost per doorway — interpolation, the camera's target, the selection, per-entity UI. Its own
    /// entity is recoverable from the <c>SELF</c> block, which is why this is survivable; anything else it was tracking is not.
    /// </para>
    /// </remarks>
    [Test]
    [Category("Quarantine")]
    public void Portal_RoundTrip_NetIdStable()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        var harness = new SessionHarness(sim);
        try
        {
            var trip = RoundTrip(sim, harness);
            Assert.Multiple(() =>
            {
                Assert.That(trip.NetIdEntering, Is.EqualTo(trip.NetIdOnPlanet),
                    $"the client's own name for its player changed across the door: {trip.NetIdOnPlanet} became {trip.NetIdEntering}");
                Assert.That(trip.NetIdLeaving, Is.EqualTo(trip.NetIdOnPlanet), "and the round trip ends with the name it started with");
                Assert.That(trip.NetIds, Has.Exactly(1).Items,
                    $"the client was given more than one name for its own player over the round trip: {string.Join(", ", trip.NetIds)}");
            });
        }
        finally
        {
            harness.Release();
        }
    }

    /// <summary>What one portal round trip looked like from the crossing client's own frames.</summary>
    private readonly record struct Trip(
        ushort Interior,
        uint NetIdOnPlanet,
        int ResetsEntering,
        int RealmEntering,
        uint NetIdEntering,
        int ResetsLeaving,
        int RealmLeaving,
        uint NetIdLeaving,
        uint[] NetIds);

    /// <summary>
    /// Walks a possessed player into portal 0 of planet 0 and back out, and reports what its own client was told.
    /// </summary>
    /// <remarks>
    /// One helper for both cases above because they are two claims about one trip, and running the trip twice would double a three-second fixture to say the
    /// same thing. The destination is pinned for the duration — see the fixture's remarks on why a dormant realm cannot be crossed into.
    /// </remarks>
    private static Trip RoundTrip(TatooineSim sim, SessionHarness harness)
    {
        var viewer = new Viewer(harness, TatooineReplication.PlayerKind);
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the player client to be given a player");
        harness.Ticks(2);
        var player = TatooineReplication.ControlledBy(sim.Dbe, viewer.Link.Session);
        Assert.That(player, Is.Not.EqualTo(EntityId.Null), "precondition: the client possesses a player");

        // Its own name for itself, as SELF gives it. Waited for rather than assumed: SELF arrives on the first frame after the control lands.
        SessionHarness.Until(
            () =>
            {
                harness.Ticks(1);
                return viewer.Drain().SelfNetId != 0;
            },
            "the client to be told which entity is its own");

        var netIdOnPlanet = viewer.Drain().SelfNetId;
        Assert.That(viewer.Realm, Is.Zero, "precondition: it starts on planet 0");

        var interior = (ushort)sim.Config.Planets;
        using var pin = sim.Dbe.Realms.Observe(new RealmId(interior));
        var edge = WorldBuilder.InteriorEdgeM * 0.5f;

        // <b>Counted from here, one statement before the crossing.</b> The world ticks on its own thread throughout, so every read above — the realm check, the
        // pin, the commit inside MoveTo — is a window in which an unrelated RESET could arrive and be charged to the crossing. "Exactly one" is only a claim
        // about the crossing if the count starts at the crossing.
        var resetsOnPlanet = viewer.Drain().Resets;
        MoveTo(sim, player, interior, edge, edge);
        SessionHarness.Until(
            () =>
            {
                harness.Ticks(1);
                return viewer.Realm == interior;
            },
            "the client to be told it is in the interior its player walked into");

        // Read out NOW, not at the end: the sink's lists are shared with the viewer, so Realms[^1] would answer for wherever the player has got to by the time
        // the record is built — which is back on the planet. The scalar counters are copied by the struct return; the lists are not.
        var inside = viewer.Drain();
        var resetsEntering = inside.Resets - resetsOnPlanet;
        var realmEntering = inside.Realms[^1];
        var netIdEntering = inside.SelfNetId;

        // Back out through the door, to a step outside it. The return leg's count starts here, for the reason the outbound one did.
        var door = sim.Index.Portals[0];
        var resetsInside = viewer.Drain().Resets;
        MoveTo(sim, player, 0, door.X + 8f, door.Z);
        SessionHarness.Until(
            () =>
            {
                harness.Ticks(1);
                return viewer.Realm == 0;
            },
            "the client to be told it is back on the planet");

        var back = viewer.Drain();
        return new Trip(
            interior,
            netIdOnPlanet,
            resetsEntering,
            realmEntering,
            netIdEntering,
            back.Resets - resetsInside,
            back.Realms[^1],
            back.SelfNetId,
            back.SelfNetIds.Distinct().ToArray());
    }

    /// <summary>
    /// <c>Announcement_ReachesInteriorsOfItsPlanetOnly</c> — a planet's announcement is heard inside its own buildings and not inside another planet's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two listeners are in interiors, not on planets, and that is the whole point.</b> An announcement routed to a realm reaches the realms UNDER it
    /// too (Realms G3, <c>RouteToRealm(subtree: true)</c>), and the demo makes each interior a child of its planet — so "heard inside planet 0's cantina" is
    /// the subtree walk working, and "not heard inside planet 1's" is it stopping where it should. Two listeners on two planets' surfaces would prove neither.
    /// </para>
    /// <para>
    /// <b>The producer is a GM command added for this, and that is a deliberate borrow from SWG-09.</b> The demo's only source of <see cref="RealmNews"/> was a
    /// dungeon opening or closing on the simulation's own schedule, so this check could only ever have been written around whenever that happened to fire. A
    /// check whose subject is the routing must be able to make the thing it is routing.
    /// </para>
    /// </remarks>
    [Test]
    public void Announcement_ReachesInteriorsOfItsPlanetOnly()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        var harness = new SessionHarness(sim);
        try
        {
            var here = new Viewer(harness, TatooineReplication.GodKind);
            var elsewhere = new Viewer(harness, TatooineReplication.GodKind);
            var gm = new Viewer(harness, TatooineReplication.GodKind);
            harness.Ticks(3);

            // An interior of planet 0 and an interior of planet 1: portal 0 of each, which is Planets + planet·N.
            var onPlanet0 = (ushort)sim.Config.Planets;
            var onPlanet1 = (ushort)(sim.Config.Planets + sim.InteriorsPerPlanet);
            Assert.That(sim.InteriorsPerPlanet, Is.GreaterThan(0), "precondition: --interiors built some");

            here.Send(nameof(ViewRealm), new RecordValues { ["realm"] = FieldValue.Of((uint)onPlanet0) });
            elsewhere.Send(nameof(ViewRealm), new RecordValues { ["realm"] = FieldValue.Of((uint)onPlanet1) });
            SessionHarness.Until(
                () =>
                {
                    harness.Ticks(1);
                    return here.Realm == onPlanet0 && elsewhere.Realm == onPlanet1;
                },
                "both cameras to arrive in the interiors they asked for");

            var heardBefore = here.Drain().Events.Count(e => e == nameof(RealmNews));
            var elsewhereBefore = elsewhere.Drain().Events.Count(e => e == nameof(RealmNews));

            gm.Send(nameof(GmAnnounce), new RecordValues { ["realm"] = FieldValue.Of(0u) });
            SessionHarness.Until(
                () =>
                {
                    harness.Ticks(1);
                    return here.Drain().Events.Count(e => e == nameof(RealmNews)) > heardBefore;
                },
                "planet 0's announcement to reach the camera inside planet 0's building");

            // Given time to arrive if it were going to: the claim is that it never does, and a claim of absence needs the ticks in which it would have shown.
            harness.Ticks(20);
            Assert.Multiple(() =>
            {
                Assert.That(here.Drain().Events.Count(e => e == nameof(RealmNews)), Is.GreaterThan(heardBefore),
                    "planet 0's news did not reach a session inside one of planet 0's buildings");
                Assert.That(elsewhere.Drain().Events.Count(e => e == nameof(RealmNews)), Is.EqualTo(elsewhereBefore),
                    "planet 0's news reached a session inside one of PLANET 1's buildings: the subtree walk does not stop at the planet");
            });
        }
        finally
        {
            harness.Release();
        }
    }
}
