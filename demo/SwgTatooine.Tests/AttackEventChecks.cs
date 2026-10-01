using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// SWG-09 — one blow landed is declared, emitted and encoded, and comes off the wire decodable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a wire check and not a counter check.</b> <c>TatooineReplication.Strike</c> increments a counter, and a
/// test that read the counter would be green in the same build as an event that never travels — the exact failure C4
/// found for the aggregate grid, where blocks arrived carrying nothing. So the assertions are made on bytes the engine
/// sent, decoded through the protocol's own reader against the catalog the client negotiated.
/// </para>
/// <para>
/// <b>What this does NOT prove, deliberately: the routing.</b> The only session it opens is a god session, which knows
/// every entity on the planet — so replacing <c>RouteToKnown</c> with <c>Broadcast</c> would leave both cases green.
/// Routing is the ENGINE's property and the engine owns its proof:
/// <c>EventDeliveryTests.EveryEventReachesExactlyItsSessionsAtEverySkipRate</c> declares its event with
/// <c>RouteToKnown(d =&gt; d.A, d =&gt; d.B)</c> and asserts that every session receives exactly the events routed to it
/// and no others, at four skip rates, under rules SUB-03 and SUB-21. Re-proving that here would test the engine through a
/// demo. What is the demo's to prove is that it DECLARED the event, with the fields a client draws from, and that a blow
/// actually reaches the wire — which is what these two cases are.
/// </para>
/// <para>
/// <b>The fight is staged, and the first version of this — which ran the world and waited for one — was flaky.</b> The
/// demo's own combat starts when a simulated player is rolled into <c>Combat</c> and has walked the one to two kilometres
/// to its mission, and with four workers a tick's ordering is not reproducible: the same 600 ticks produced a blow in one
/// run and none in another. A case whose subject is "the event reaches the wire" must not also depend on whether the
/// world felt like fighting, so a player is parked on a creature's toes in <c>Combat</c> — the shape
/// <c>CausalityChecks.DriveByCombatIsGoneTests</c> uses, and its exact inverse: that one proves a player NOT in combat
/// does nothing, this one that a player in combat is heard about.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class AttackEventChecks
{
    private string _dir;
    private SessionHarness _harness;

    [SetUp]
    public void SetUp()
    {
        _dir = Worlds.NewDirectory();
        TatooineReplication.ResetSessionAccounting();

        // Both, because they cover different statics: the emission counters and `_declared`/`_pushCommands` live in
        // ResetIntentAccounting, so a fixture that reset only the session ones would inherit the previous world's
        // registry.
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

    /// <summary>
    /// A world whose tick is fast enough that a bounded wait costs seconds rather than a minute, and single-worker so the
    /// order within a tick is the same on every run.
    /// </summary>
    /// <remarks>
    /// <b>10 Hz, and raising it makes this SLOWER rather than faster.</b> The serve path is paced by the runtime, so a
    /// tick costs its period in wall clock; but every interval in the simulation is stated in seconds and converted at the
    /// configured rate (S0-3), so a cooldown of one second is 10 ticks at 10 Hz and 100 at 100 Hz. Raising the rate buys
    /// no simulated time at all. What makes this case cheap is that the fight is staged down to the weapon's cooldown, so
    /// it needs a handful of ticks rather than a stretch of simulated time.
    /// </remarks>
    private SimConfig Config()
    {
        var config = Worlds.Small(_dir);
        config.Unpaced = false;
        config.TickRateHz = 10;
        config.WorkerCount = 1;
        return config;
    }

    /// <summary>Parks a simulated player on a creature's toes, in <c>Combat</c>, so the next ticks land blows.</summary>
    /// <param name="sim">An initialized simulation that is not yet ticking.</param>
    /// <returns>The creature being shot at.</returns>
    private static EntityId StageAFight(TatooineSim sim)
    {
        var rows = Causality.Creatures(sim);
        Assert.That(rows, Is.Not.Empty, "precondition: the world has creatures");

        var chosen = default((EntityId Id, float X, float Z, int Mode, EntityId Target, int Health));
        foreach (var row in rows)
        {
            if (row.Mode != AiMode.Dead && row.Health > 0)
            {
                chosen = row;
                break;
            }
        }

        Assert.That(chosen.Id.IsNull, Is.False, "precondition: the world has a living creature");

        using var tx = sim.Dbe.CreateQuickTransaction();
        var id = sim.Index.Players[0];
        var player = tx.OpenMut(id);

        // Combat, and for longer than the window: the activity mix re-rolls, and a player that wandered out of Combat half
        // way through would fail this for a reason that is not the event's.
        ref var state = ref player.Write(Player.State);
        state.Activity = PlayerActivity.Combat;
        state.ActivityTicks = int.MaxValue / 2;
        ref var move = ref player.Write(Player.Move);
        move.VelX = 0f;
        move.VelZ = 0f;

        // The target and the weapon, set outright. Left to the simulation, the player would acquire a target on the
        // weapon's own scan cadence and then wait out `MinAttackDelaySec` — seconds of simulated time, which on the paced
        // serve path is seconds of wall clock, for a case whose subject is one event's trip to the wire. `PlayerCombatTick`
        // still does the deciding: it re-checks the target's range and kind, and refuses a blow it does not like.
        ref var session = ref player.Write(Player.Session);
        session.Target = chosen.Id;
        session.TargetKind = CombatTargetKind.Creature;
        ref var vitals = ref player.Write(Player.Vitals);
        vitals.AttackCooldown = 0;

        // Two metres: inside both the 75 m weapon and the 6 m melee reach.
        var at = default(PlayerPlacement);
        at.SetAt(chosen.X + 2f, chosen.Z, 0f, player.Read(Player.Bounds).HalfExtent);
        tx.Teleport(id, Player.Bounds, RealmId.Default, in at);
        tx.Commit();
        return chosen.Id;
    }

    /// <summary>Collects every <c>Attack</c> a frame carries, with its decoded fields.</summary>
    private struct AttackSink : ITickSink
    {
        public MessagePlan Attack;
        public List<(uint Attacker, uint Target, uint Amount)> Attacks;

        private bool _inAttack;
        private uint _attacker;
        private uint _target;
        private uint _amount;

        public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
        {
            if (!_inAttack || components.Length == 0)
            {
                return;
            }

            var value = (uint)components[0];
            switch (field.Name)
            {
                case "attacker":
                    _attacker = value;
                    break;
                case "target":
                    _target = value;
                    break;
                case "amount":
                    _amount = value;
                    break;
                default:
                    break;
            }
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

        public void BeginTick(uint tick, TickFlags flags, uint periodUs) => Flush();

        public void Realm(RealmFrame frame)
        {
        }

        public void BeginEntities(ArchetypePlan archetype) => Flush();

        public void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch) => Flush();

        public void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch) => Flush();

        public void State(uint netId, byte groupMask) => Flush();

        public void Leave(uint netId) => Flush();

        public void Event(MessagePlan type)
        {
            Flush();
            _inAttack = type == Attack;
        }

        public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask) => Flush();

        public void Ack(ushort seq, byte reason) => Flush();

        public void Source(ushort requestId, byte status, ushort code) => Flush();

        public void BeginAggregate(CatalogGrid grid, bool reset) => Flush();

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

        public void UnknownBlock(byte blockType) => Flush();

        public void EndTick() => Flush();

        /// <summary>Closes the record the field members were filling: fields arrive between one record call and the next.</summary>
        private void Flush()
        {
            if (!_inAttack)
            {
                return;
            }

            _inAttack = false;
            Attacks.Add((_attacker, _target, _amount));
            _attacker = 0;
            _target = 0;
            _amount = 0;
        }
    }

    [Test]
    public void TheCatalogDeclaresAttackWithTheFieldsAClientDrawsALineFrom()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var link = _harness.Connect(TatooineReplication.GodKind);
        SessionHarness.Until(() => link.Received(MessageTypes.Welcome), "WELCOME");

        // From the WIRE catalog: a declaration the client was never sent is a declaration that does not exist.
        var attack = link.Plan.EventByName("Attack");
        Assert.That(attack, Is.Not.Null, "the catalog carries no Attack event, so no client can draw an attack line");

        var names = new List<string>();
        foreach (var field in attack.Body.Fields)
        {
            names.Add(field.Name);
        }

        Assert.That(names, Is.EquivalentTo(new[] { "attacker", "target", "amount" }));
    }

    [Test]
    public void ALandedBlowIsEmittedAndComesOffTheWireDecodable()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        var creature = StageAFight(sim);
        _harness = new SessionHarness(sim);
        var link = _harness.Connect(TatooineReplication.GodKind);
        SessionHarness.Until(() => link.Received(MessageTypes.Welcome), "WELCOME");

        var plan = link.Plan;
        var attack = plan.EventByName("Attack");
        var sink = new AttackSink { Attack = attack, Attacks = [] };
        var read = 0;

        // Held ACROSS messages, because that is what a session holds: a REALM block arrives once and every positioned
        // value in every later frame decodes over it. Re-declaring it per message made the reader refuse the first
        // ENTITIES block of the second frame, which is the refusal a real client would get for losing its frame.
        RealmFrame frame = null;

        // A staged fight lands its blow on the first tick that runs the combat phase; 40 ticks is four seconds of head
        // room. A bound rather than a wait, so a build where combat stopped resolving fails with a sentence, not a hang.
        for (var round = 0; round < 8 && sink.Attacks.Count == 0; round++)
        {
            _harness.Ticks(5);
            var sent = link.Sent;
            for (; read < sent.Length; read++)
            {
                var message = sent[read];
                if (message.Length > 0 && message[0] == MessageTypes.Tick)
                {
                    TickReader.Read(message, plan, ref frame, ref sink);
                }
            }
        }

        Assert.That(sink.Attacks, Is.Not.Empty, "no Attack was emitted in 40 ticks of a staged fight");
        Assert.That(creature.IsNull, Is.False, "precondition: a creature was staged to be shot at");

        var first = sink.Attacks[0];
        Assert.Multiple(() =>
        {
            Assert.That(first.Attacker, Is.Not.Zero, "an attack with no attacker is half a line");
            Assert.That(first.Target, Is.Not.Zero, "an attack with no target is one a client cannot draw");
            Assert.That(first.Amount, Is.Not.Zero, "a blow that took nothing off is not a landed blow");
        });
    }
}
