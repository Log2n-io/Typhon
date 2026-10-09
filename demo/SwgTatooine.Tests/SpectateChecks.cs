using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// CLI3D-10 rung 2 — <see cref="Spectate"/>: the SESSION rides an entity, not just the camera.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asserted on the bytes the session received, never on a server-side map.</b> Whether the anchor took hold is exactly the question "does this client's
/// view now follow that entity", and the only honest answer to it is the frames. <c>Spectators</c> would be green in the same build as an anchor that was
/// never applied.
/// </para>
/// <para>
/// <b>The client re-finds its subject through <c>SELF</c>, not through the netId it clicked.</b> Anchoring re-sends the whole view; netIds are global per
/// database and stay with the entity — across clusters and, since #1081, across realms — so the subject keeps its id, but the client's store is rebuilt by
/// the RESET and <c>SELF</c> (what <c>Control</c> is for) is the one block that names which entity is the subject. These cases assert on <c>SELF</c>'s
/// archetype and non-zero id. (This paragraph used to say netIds were allocated densely per view, which was never the mechanism.)
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class SpectateChecks
{
    private string _dir;
    private SessionHarness _harness;

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
        _harness?.Release();
        _harness = null;
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        Worlds.Delete(_dir);
    }

    private SimConfig Config()
    {
        var config = Worlds.Small(_dir);
        config.Unpaced = false;

        // 40 Hz rather than 10: every `Ticks(1)` in here is a real wall-clock wait, and these cases step a dozen ticks
        // at a time. Nothing under test depends on the period.
        config.TickRateHz = 40;
        config.WorkerCount = 1;
        return config;
    }

    /// <summary>What a spectating client can see of its own session: who it is riding, what it holds, and what was refused.</summary>
    private struct RideSink : ITickSink
    {
        /// <summary>Every netId that entered, by archetype name, so a case can pick a real subject the way a viewer clicks one.</summary>
        public Dictionary<string, List<uint>> Entered;

        /// <summary>The archetype named by the last <c>SELF</c>, or <see langword="null"/> when the session controls nothing.</summary>
        public string SelfArchetype;

        /// <summary>The netId named by the last <c>SELF</c>; 0 for none.</summary>
        public uint SelfNetId;

        /// <summary>Every refusal, in order.</summary>
        public List<byte> Refusals;

        /// <summary>How many frames carried <c>RESET</c>: an accepted ride is one, because the session's profile changed.</summary>
        public int Resets;

        /// <summary>The realm the last <c>REALM</c> block put this session in, or <see cref="RealmFrame.NoRealm"/> before the first.</summary>
        public ushort RealmId;

        private string _archetype;

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

        public void BeginTick(uint tick, TickFlags flags, uint periodUs)
        {
            if ((flags & TickFlags.Reset) != 0)
            {
                Resets++;
                Entered.Clear();
            }
        }

        public void Realm(RealmFrame frame) => RealmId = frame?.RealmId ?? RealmFrame.NoRealm;

        public void BeginEntities(ArchetypePlan archetype) => _archetype = archetype.Name;

        public void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
        {
            if (_archetype == null)
            {
                return;
            }

            if (!Entered.TryGetValue(_archetype, out var ids))
            {
                ids = [];
                Entered[_archetype] = ids;
            }

            ids.Add(netId);
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

        public void Event(MessagePlan type)
        {
        }

        public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask)
        {
            SelfArchetype = archetype?.Name;
            SelfNetId = netId;
        }

        public void Ack(ushort seq, byte reason) => Refusals.Add(reason);

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
        private RideSink _sink = new() { Entered = [], Refusals = [], RealmId = RealmFrame.NoRealm };

        public Viewer(SessionHarness harness, string kind)
        {
            Harness = harness;
            _link = harness.Connect(kind);
            SessionHarness.Until(() => _link.Received(MessageTypes.Welcome), "WELCOME");
        }

        public SessionHarness Harness { get; }

        public FakeLink Link => _link;

        /// <summary>Decodes every frame that arrived since the last call and returns what the session knows about itself.</summary>
        public RideSink Drain()
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

        public void Ride(uint netId) => _link.SendCommand(nameof(Spectate), new RecordValues { ["netId"] = FieldValue.Of(netId) });

        public void AskRealm(uint realm) => _link.SendCommand(nameof(ViewRealm), new RecordValues { ["realm"] = FieldValue.Of(realm) });

        /// <summary>A netId of <paramref name="archetype"/> this session has been shown, which is the only kind a client may name (SUB-26).</summary>
        public uint AnEntityOf(string archetype)
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var sink = Drain();
                if (sink.Entered.TryGetValue(archetype, out var ids) && ids.Count > 0)
                {
                    return ids[0];
                }

                Harness.Ticks(5);
            }

            Assert.Fail($"the session was never shown a {archetype} to ride");
            return 0;
        }
    }

    /// <summary>
    /// A god camera that rides a bot is anchored on it by the server: the view is re-sent and <c>SELF</c> names the subject.
    /// </summary>
    /// <remarks>
    /// The <c>RESET</c> is asserted as well as the <c>SELF</c>, because they are two different claims. <c>SELF</c> alone would pass if the engine had
    /// acknowledged the control without changing the profile — the session would then be told what it controls while still being served a camera's region,
    /// which is precisely the state rung 1 was already in.
    /// </remarks>
    [Test]
    public void RidingABotAnchorsTheSessionOnIt()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var viewer = new Viewer(_harness, TatooineReplication.GodKind);
        _harness.Ticks(10);

        var resetsBefore = viewer.Drain().Resets;
        viewer.Ride(viewer.AnEntityOf(nameof(Player)));
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return viewer.Drain().SelfNetId != 0;
            },
            "the session to be told what it is riding");

        var sink = viewer.Drain();
        Assert.Multiple(() =>
        {
            Assert.That(sink.SelfArchetype, Is.EqualTo(nameof(Player)), "SELF named something other than the bot that was picked");
            Assert.That(sink.Resets, Is.GreaterThan(resetsBefore), "the session's profile did not change: the anchor was acknowledged but never applied");
            Assert.That(sink.Refusals, Is.Empty, "a ride of an entity the session was shown was refused");
        });
    }

    /// <summary>
    /// Asking for a realm while riding ENDS the ride and goes there, and the server survives the ask.
    /// </summary>
    /// <remarks>
    /// <b>This case is about a throw, not a misbehaviour.</b> <c>Enter</c> on an entity-anchored session raises at the call site (12-realms § 1.3), and the
    /// realm dropdown stays on screen while a viewer rides — so a first version REFUSED the ask, which needed the application to predict the engine's
    /// two-phase apply and got it wrong in the tick between asking for the god profile and the prologue applying it. Deferring instead makes the throw
    /// unreachable rather than guarded: the ask joins the release that was already a tick out, and lands from the drain.
    ///
    /// <para>So the assertion is the opposite of what it was: the viewer arrives. The world still ticking afterwards is the other half.</para>
    /// </remarks>
    [Test]
    public void AskingForARealmWhileRidingEndsTheRideAndGoesThere()
    {
        var config = Config();
        config.Planets = 2;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var viewer = new Viewer(_harness, TatooineReplication.GodKind);
        _harness.Ticks(10);

        viewer.Ride(viewer.AnEntityOf(nameof(Player)));
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return viewer.Drain().SelfNetId != 0;
            },
            "the ride to take hold");

        var tickBefore = sim.Runtime.CurrentTickNumber;
        viewer.AskRealm(1u);
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return viewer.Drain().RealmId == 1;
            },
            "the viewer to arrive in the realm asked for while riding");

        _harness.Ticks(10);
        var sink = viewer.Drain();
        Assert.Multiple(() =>
        {
            Assert.That(sink.SelfNetId, Is.Zero, "the ride outlived the realm change");
            Assert.That(sim.Runtime.CurrentTickNumber, Is.GreaterThan(tickBefore), "the runtime stopped after a realm ask from a riding session");
            Assert.That(viewer.Link.Closed, Is.Null, "the session was closed by its own realm ask");
        });
    }

    /// <summary>How many entities, of every archetype, the session holds in the view it is being served now.</summary>
    private static int Held(RideSink sink)
    {
        var held = 0;
        foreach (var ids in sink.Entered.Values)
        {
            held += ids.Count;
        }

        return held;
    }

    /// <summary>
    /// Releasing gives the session its own camera back: it is served a world again, and the realm control works.
    /// </summary>
    /// <remarks>
    /// <b>"It is served a world again" is the assertion that has teeth, and the first version of this case did not make it.</b> Dropping the anchor returns
    /// the session to a placed profile, and a placed session that was never placed is NOWHERE — it holds nothing and is sent nothing. That is what the
    /// deferred <c>Enter</c> is for, and without it the only symptom is an empty view: <c>SELF</c> still goes to zero and the realm ask is still not refused,
    /// so a case built on those two alone passed with the whole release drain removed. Measured that way, not reasoned.
    /// </remarks>
    [Test]
    public void ReleasingGivesTheSessionItsOwnCameraBack()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var viewer = new Viewer(_harness, TatooineReplication.GodKind);
        _harness.Ticks(10);

        viewer.Ride(viewer.AnEntityOf(nameof(Player)));
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return viewer.Drain().SelfNetId != 0;
            },
            "the ride to take hold");

        viewer.Ride(0u);
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return viewer.Drain().SelfNetId == 0;
            },
            "the release to drop the control");

        // Two ticks at least: the god profile is applied by the next prologue, and the Enter that puts the session back in a realm waits for it. The world
        // it is then served arrives as a fresh RESET, which clears the sink's Entered map — so what is counted is what this session holds NOW, not what it
        // held while riding.
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return Held(viewer.Drain()) > 0;
            },
            "the released session to be served a world again");

        // Realm 0 is where it already is, so what this proves is that the ask was ACCEPTED rather than deferred by an anchor that should be gone.
        viewer.AskRealm(0u);
        _harness.Ticks(10);

        Assert.Multiple(() =>
        {
            Assert.That(viewer.Drain().RealmId, Is.Zero, "the released session is not in the realm it asked for");
            Assert.That(viewer.Drain().SelfNetId, Is.Zero, "the anchor outlived the release");
            Assert.That(viewer.Link.Closed, Is.Null, "the session was closed while being released");
        });
    }

    /// <summary>
    /// A viewer who rode a bot into a building and then stopped is standing in that building, not back on planet 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the only case that can see the deferred <c>Enter</c> at all, and finding that out cost a mutation.</b> With one realm the release is
    /// indistinguishable either way: the session's PLACED realm is still the planet 0 it was entered into when it opened, so removing the whole release
    /// drain changed nothing any single-realm case could assert. The anchor's realm lives in the frame state and not in the session's placement —
    /// <c>AnchorRealm</c> against <c>PlacedRealm</c> in <c>FrameAssembler.Push</c> — so dropping the anchor without putting the session somewhere sends it
    /// back to wherever the application last placed it, which is a teleport the viewer did not ask for.
    /// </para>
    /// <para>
    /// The subject is TELEPORTED into an interior rather than walked there: a bot decides for itself when to use a door, and a case that waited for one
    /// would be waiting on a random walk.
    /// </para>
    /// </remarks>
    [Test]
    public void ReleasingLeavesTheViewerWhereTheSubjectWas()
    {
        var config = Config();
        config.Interiors = true;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var viewer = new Viewer(_harness, TatooineReplication.GodKind);
        _harness.Ticks(10);

        viewer.Ride(viewer.AnEntityOf(nameof(Player)));
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return viewer.Drain().SelfNetId != 0;
            },
            "the ride to take hold");

        // Interior 0 of planet 0: interiors are allocated above the planets, so the first of them is Planets itself.
        var interior = (ushort)TatooineReplication.Planets;
        Assert.That(sim.InteriorsPerPlanet, Is.GreaterThan(0), "this run registered no interiors, so there is no door to walk through");

        // The server's own record of what it anchored, used to STAGE the crossing rather than to assert it: what is asserted is that the CLIENT's realm
        // follows. A wrong entry here teleports the wrong bot and the client's realm does not move, so the assertion still holds the claim.
        var subject = TatooineReplication.SubjectOf(viewer.Link.Session);
        Assert.That(subject, Is.Not.EqualTo(EntityId.Null), "the session is riding nothing, so there is nothing to put through a door");
        Park(sim, subject, interior);

        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return viewer.Drain().RealmId == interior;
            },
            "the session to follow its subject through the door");

        viewer.Ride(0u);
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return viewer.Drain().SelfNetId == 0;
            },
            "the release to drop the control");

        _harness.Ticks(20);
        Assert.That(viewer.Drain().RealmId, Is.EqualTo(interior), "stopping a ride threw the viewer back to the realm their session was opened in");
    }

    /// <summary>Puts a player in a realm, parked, the way a portal would.</summary>
    private static void Park(TatooineSim sim, EntityId entity, ushort realm)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        var player = tx.OpenMut(entity);
        var state = player.Read(Player.State);
        state.Activity = PlayerActivity.Idle;
        state.ActivityTicks = int.MaxValue / 2;
        player.Set(Player.State, state);
        var move = player.Read(Player.Move);
        move.VelX = 0f;
        move.VelZ = 0f;
        player.Set(Player.Move, move);

        var at = default(PlayerPlacement);
        at.SetAt(32f, 32f, 0f, player.Read(Player.Bounds).HalfExtent);
        tx.Teleport(entity, Player.Bounds, new RealmId(realm), in at);
        tx.Commit();
    }

    /// <summary>
    /// Stopping a ride and asking for a realm in the SAME tick lands the viewer in that realm, and never throws.
    /// </summary>
    /// <remarks>
    /// <b>The window the other cases step around.</b> Every one of them waits for the release to be visible before
    /// touching the realm control, which is exactly the state in which the refusal is easy. The hard state is the tick
    /// in between: <c>StopSpectating</c> drops the <c>Spectators</c> entry the moment it is asked for, and the god
    /// profile that makes <c>Enter</c> legal is applied only by the NEXT tick's prologue. The two commands hold
    /// independent rate buckets, so a viewer pressing Stop and then picking a realm within one tick delivers both into
    /// that window — and a guard that consulted <c>Spectators</c> alone let the second one through to a throw. Deferring removed the guard and the window
    /// with it: neither command reaches <c>Enter</c>, and the pending target is simply overwritten by the second.
    ///
    /// <para>Sent without a tick between them. The viewer arriving is the assertion; the world still running is the other half.</para>
    /// </remarks>
    [Test]
    public void StoppingAndThenAskingForARealmInOneTickLandsThere_AndNeverThrows()
    {
        var config = Config();
        config.Planets = 2;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var viewer = new Viewer(_harness, TatooineReplication.GodKind);
        _harness.Ticks(10);

        viewer.Ride(viewer.AnEntityOf(nameof(Player)));
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return viewer.Drain().SelfNetId != 0;
            },
            "the ride to take hold");

        var tickBefore = sim.Runtime.CurrentTickNumber;
        viewer.Ride(0u);
        viewer.AskRealm(1u);
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return viewer.Drain().RealmId == 1;
            },
            "the viewer to land in the realm asked for in the same tick as the stop");

        _harness.Ticks(10);
        Assert.Multiple(() =>
        {
            Assert.That(sim.Runtime.CurrentTickNumber, Is.GreaterThan(tickBefore + 10), "the runtime stopped: the realm ask reached an anchored session");
            Assert.That(viewer.Link.Closed, Is.Null, "the session was closed by a stop followed by a realm ask");
            Assert.That(Held(viewer.Drain()), Is.GreaterThan(0), "the session was left holding nothing after the pair");
        });
    }

    /// <summary>
    /// A subject that dies ends the ride on the SERVER, without the client having to notice.
    /// </summary>
    /// <remarks>
    /// The engine will not do it: a destroyed subject leaves the session anchored, holding its realm and last point
    /// (<c>BoundLost</c>, 12-realms § 1.3). Left alone, the server refuses that session's realm control for the rest of
    /// its life: the anchor never goes away, so every realm it picks is routed through the deferred release path for
    /// ever. The realm ask at the end is what proves the anchor is really gone rather than merely unreported.
    /// </remarks>
    [Test]
    public void ASubjectThatDiesEndsTheRide_WithoutTheClientAskingForIt()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var viewer = new Viewer(_harness, TatooineReplication.GodKind);
        _harness.Ticks(10);

        viewer.Ride(viewer.AnEntityOf(nameof(Player)));
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return viewer.Drain().SelfNetId != 0;
            },
            "the ride to take hold");

        var subject = TatooineReplication.SubjectOf(viewer.Link.Session);
        Assert.That(subject, Is.Not.EqualTo(EntityId.Null), "the session is riding nothing, so there is nothing to kill");

        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            tx.Destroy(subject);
            tx.Commit();
        }

        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return TatooineReplication.SubjectOf(viewer.Link.Session) == EntityId.Null;
            },
            "the server to end a ride whose subject died");

        // Not merely forgotten: the anchor is gone, so the realm control works again.
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return Held(viewer.Drain()) > 0;
            },
            "the session to be served a world again");

        viewer.AskRealm(0u);
        _harness.Ticks(10);
        Assert.Multiple(() =>
        {
            Assert.That(viewer.Drain().RealmId, Is.Zero, "the session is not where it asked to be after its subject died");
            Assert.That(viewer.Drain().SelfNetId, Is.Zero, "the dead subject's anchor outlived it");
        });
    }


    /// <summary>
    /// A possessed player's session may not ride, and it is the ENGINE that says so.
    /// </summary>
    /// <remarks>
    /// <b>This case is why the application's own check for it was deleted.</b> It first asserted a demo reason code and timed out: <c>Spectate</c> declares
    /// <c>Roles(SessionRole.Spectator)</c>, so the engine answers <see cref="AckReasons.Forbidden"/> and the command is never offered to a system. The
    /// application check was therefore a constant that nothing could send and a branch nothing could reach. Asserting <c>Forbidden</c> tests the gate that
    /// actually holds, and <c>SELF</c> staying on the session's OWN player — never the bot it named — is what the gate is for.
    /// </remarks>
    [Test]
    public void APlayersSessionMayNotRide_AndTheEngineIsWhatRefusesIt()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var viewer = new Viewer(_harness, TatooineReplication.PlayerKind);
        _harness.Ticks(10);

        // Its own player, before the ask: a possessed session controls one from the moment it is bound, so "SELF did not change" is only meaningful against
        // what it already was.
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return viewer.Drain().SelfNetId != 0;
            },
            "the player session to be given a player");
        var own = viewer.Drain().SelfNetId;

        viewer.Ride(viewer.AnEntityOf(nameof(Player)));
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return viewer.Drain().Refusals.Contains(AckReasons.Forbidden);
            },
            "the ride to be refused for a player");

        _harness.Ticks(5);
        Assert.That(viewer.Drain().SelfNetId, Is.EqualTo(own), "a player's session was re-anchored onto the entity it named");
    }

    /// <summary>
    /// A subject the session was never shown is refused, and the refusal is told apart from a release.
    /// </summary>
    /// <remarks>
    /// <b>Both are sent, because either alone proves nothing.</b> A session riding nothing is already riding nothing, so a case that sent only the bogus id
    /// and asserted "not riding" would pass with the whole resolution deleted. What is falsifiable is the difference: an identity the session was never shown
    /// produces a refusal and a release produces none, from the same client.
    /// </remarks>
    [Test]
    public void ASubjectTheSessionWasNeverShownIsRefused_AndIsToldApartFromARelease()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var viewer = new Viewer(_harness, TatooineReplication.GodKind);
        _harness.Ticks(10);

        viewer.Ride(0xFFFF_FF00u);
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return viewer.Drain().Refusals.Contains(TatooineReplication.SpectateNoSuchEntity);
            },
            "the unknown identity to be refused");

        var refusalsAfterBogus = viewer.Drain().Refusals.Count;
        viewer.Ride(0u);
        _harness.Ticks(10);

        Assert.Multiple(() =>
        {
            Assert.That(viewer.Drain().Refusals, Has.Count.EqualTo(refusalsAfterBogus), "a release was answered as a refusal");
            Assert.That(viewer.Drain().SelfNetId, Is.Zero, "an identity the session was never shown was ridden");
        });
    }
}
