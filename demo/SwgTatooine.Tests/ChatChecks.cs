using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// SWG-09 — spatial chat: heard within 50 m, and only in the speaker's own realm.
/// </summary>
/// <remarks>
/// <para>
/// <b>The realm clause is the one worth the fixture.</b> "Heard within 50 m" is distance arithmetic and would pass with
/// the realm ignored entirely; two players at the SAME local coordinates in two different interiors are 0 m apart by that
/// arithmetic and must hear nothing of each other. That is what separates a spatial index that knows about realms from
/// one that happens to be given disjoint coordinates, and it is the acceptance criterion the scope document singles out.
/// </para>
/// <para>
/// Asserted on the bytes a session actually received, decoded through the protocol's own reader — not on a counter. A
/// counter would be green in the same build as an event that reaches everybody.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class ChatChecks
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
        config.TickRateHz = 10;
        config.WorkerCount = 1;
        return config;
    }

    /// <summary>Collects every <c>Chat</c> a session's frames carried, with the text decoded.</summary>
    private struct ChatSink : ITickSink
    {
        public MessagePlan Chat;
        public List<(uint Speaker, string Text)> Heard;

        private bool _inChat;
        private uint _speaker;
        private string _text;

        public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
        {
            if (_inChat && field.Name == "speaker" && components.Length > 0)
            {
                _speaker = (uint)components[0];
            }
        }

        public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
        {
            if (_inChat && field.Name == "text")
            {
                _text = System.Text.Encoding.UTF8.GetString(utf8);
            }
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
            _inChat = type == Chat;
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

        private void Flush()
        {
            if (!_inChat)
            {
                return;
            }

            _inChat = false;
            Heard.Add((_speaker, _text ?? string.Empty));
            _speaker = 0;
            _text = null;
        }
    }

    /// <summary>A listening player session parked at a place, with everything it has heard.</summary>
    private sealed class Listener
    {
        public Listener(SessionHarness harness, FakeLink link)
        {
            Harness = harness;
            Link = link;
            Sink = new ChatSink { Chat = link.Plan.EventByName("Chat"), Heard = [] };
        }

        public SessionHarness Harness { get; }

        public FakeLink Link { get; }

        public ChatSink Sink;

        private int _read;
        private RealmFrame _frame;

        /// <summary>Decodes every frame that arrived since the last call.</summary>
        public List<(uint Speaker, string Text)> Drain()
        {
            var sent = Link.Sent;
            for (; _read < sent.Length; _read++)
            {
                var message = sent[_read];
                if (message.Length > 0 && message[0] == MessageTypes.Tick)
                {
                    TickReader.Read(message, Link.Plan, ref _frame, ref Sink);
                }
            }

            return Sink.Heard;
        }
    }

    /// <summary>Puts the player a session controls at a place in a realm, and returns it.</summary>
    private static EntityId Park(TatooineSim sim, SessionId session, float x, float z, ushort realm)
    {
        var entity = TatooineReplication.ControlledBy(sim.Dbe, session);
        Assert.That(entity, Is.Not.EqualTo(EntityId.Null), "the session controls nothing to speak with");

        using var tx = sim.Dbe.CreateQuickTransaction();
        var player = tx.OpenMut(entity);

        // Parked and not deciding anything: a player that wandered off mid-case would move the distance under test.
        ref var state = ref player.Write(Player.State);
        state.Activity = PlayerActivity.Idle;
        state.ActivityTicks = int.MaxValue / 2;
        ref var move = ref player.Write(Player.Move);
        move.VelX = 0f;
        move.VelZ = 0f;

        var at = default(PlayerPlacement);
        at.SetAt(x, z, 0f, player.Read(Player.Bounds).HalfExtent);
        tx.Teleport(entity, Player.Bounds, new RealmId(realm), in at);
        tx.Commit();
        return entity;
    }

    private Listener Connect(TatooineSim sim)
    {
        var link = _harness.Connect(TatooineReplication.PlayerKind);
        SessionHarness.Until(() => link.Received(MessageTypes.Welcome), "WELCOME");
        return new Listener(_harness, link);
    }

    /// <summary>
    /// 40 m apart they hear each other; 60 m apart they do not; at the same coordinates in two different interiors they do not.
    /// </summary>
    /// <remarks>
    /// It speaks by sending the real <c>Say</c> COMMAND from the speaker's own client, so the whole path is under test —
    /// the rate limit, the drain, the lookup of the speaker's place and realm, the routing and the encode. Calling the
    /// emitter directly would skip the half of it that decides WHERE a voice comes from, which is the half a client must
    /// not be able to choose.
    /// </remarks>
    [Test]
    public void ChatIsHeardWithinFiftyMetresAndOnlyInTheSpeakersRealm()
    {
        var config = Config();
        config.Interiors = true;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        _harness = new SessionHarness(sim);

        var speaker = Connect(sim);
        var listener = Connect(sim);
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 2, "both sessions to possess a player");
        _harness.Ticks(2);

        // A line nothing in the world says by itself. The town talks now (SWG-09's own ambient chatter), so counting
        // EVERY chat a listener hears would count the locals — which is what made the first version of this fail in
        // Release and pass in Debug, on timing alone.
        const string Spoken = "Utinni! ça va, señor? — test utterance 7f3a";
        var ground = RealmId.Default.Value;

        // ── 40 m apart, same realm: heard ──────────────────────────────────────────────────────────────────
        Park(sim, speaker.Link.Session, 0f, 0f, ground);
        Park(sim, listener.Link.Session, 40f, 0f, ground);
        Settle(speaker, listener);

        speaker.Link.SendCommand("Say", new RecordValues { ["text"] = FieldValue.Of(Spoken) });
        _harness.Ticks(6);

        listener.Drain();
        Assert.That(Utterances(listener, Spoken), Is.EqualTo(1), "40 m apart in one realm, and it was not heard");

        // ── 60 m apart, same realm: not heard ──────────────────────────────────────────────────────────────
        Park(sim, listener.Link.Session, 60f, 0f, ground);
        Settle(speaker, listener);

        var atSixty = Utterances(listener, Spoken);
        speaker.Link.SendCommand("Say", new RecordValues { ["text"] = FieldValue.Of(Spoken) });
        _harness.Ticks(6);
        listener.Drain();
        Assert.That(Utterances(listener, Spoken), Is.EqualTo(atSixty), "60 m apart and it was heard: the range is not 50 m");

        // ── the same coordinates, two different interiors: not heard ───────────────────────────────────────
        //
        // THE case. Zero metres apart by arithmetic, and they must hear nothing of each other.
        Assert.That(sim.InteriorsPerPlanet, Is.GreaterThan(1), "precondition: the world has two interiors to stand in");
        var firstInterior = (ushort)config.Planets;
        var secondInterior = (ushort)(firstInterior + 1);

        Park(sim, speaker.Link.Session, 3f, 3f, firstInterior);
        Park(sim, listener.Link.Session, 3f, 3f, secondInterior);
        Settle(speaker, listener);

        var inside = Utterances(listener, Spoken);
        speaker.Link.SendCommand("Say", new RecordValues { ["text"] = FieldValue.Of(Spoken) });
        _harness.Ticks(6);
        listener.Drain();
        Assert.That(
            Utterances(listener, Spoken),
            Is.EqualTo(inside),
            "two players at identical coordinates in DIFFERENT interiors heard each other: the routing is distance arithmetic, not realms");

        // And the speaker was really speaking throughout, or all three negatives above are vacuously true.
        //
        // Drained first: `Drain` is what pumps a session's sent frames into its sink, and the speaker's is only drained by
        // `Settle` — which runs BEFORE each send, not after the last one. Without this the count is 2 of 3, which is how
        // the guard announced that it is measuring something real.
        speaker.Drain();

        // Asserted on the SPEAKER'S OWN ear, which is zero metres from itself and must have heard the line three times.
        // The previous guard read TatooineReplication.Chatter.Heard, a global that the town's own ambient chatter also
        // increments (SimBridge.Behaviour.cs) — at one line per NPC per eight seconds it cleared any small threshold on its
        // own, so it was green whatever the three Say commands did. A guard against vacuity that is itself vacuous is
        // worse than none: it reads like coverage.
        Assert.That(
            Utterances(speaker, Spoken),
            Is.EqualTo(3),
            "the speaker did not hear its own three lines: the Say commands never became Chat events");
    }

    /// <summary>How many times the listener has heard exactly this line — never the town's own chatter.</summary>
    private static int Utterances(Listener listener, string text)
    {
        var count = 0;
        foreach (var (_, heard) in listener.Sink.Heard)
        {
            if (string.Equals(heard, text, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Lets the parks and the views land, and drains both sessions' queues.</summary>
    /// <remarks>
    /// It does NOT clear what has been heard: <see cref="Listener.Drain"/> returns the accumulated sink and leaves it. Every
    /// case here therefore compares against a baseline taken just before it, and a new case that assumes a zero count will
    /// be wrong.
    /// It also spaces the utterances: Say is one a second, and three in quick succession would be rate-limited.
    /// </remarks>
    private void Settle(Listener speaker, Listener listener)
    {
        _harness.Ticks(14);
        speaker.Drain();
        listener.Drain();
    }
}
