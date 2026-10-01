using System.Numerics;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// SWG-10 — a session whose realm is torn down under it: the engine says so with <c>SessionEventKind.RealmClosed</c>, and this application acts on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaces.</b> A dungeon closing announced itself with a <c>RealmNews</c> on planet 0's subtree — which a session that is INSIDE the dungeon
/// does not hear, because it is not on the planet any more — and then simply stopped being served, with nothing telling the application that one of its
/// sessions had been left pointing at a realm that no longer existed. The engine had no event for it (<c>12-realms § 1.6</c> Q7 specified one; nothing built
/// it), so the demo's answer was to forbid the situation: <c>ViewRealm</c> refuses every realm above the permanent ones, and the release path replaced any
/// such realm with planet 0 whether or not it was still there. Both of those are workarounds for a missing notification.
/// </para>
/// <para>
/// <b>The precondition is staged, not waited for.</b> A real dungeon opens and closes on the simulation's own schedule, so a case that waited for one would
/// be a case whose timing decides whether it tests anything. A realm is registered into one of the reserved dungeon slots here, the viewer is sent into it,
/// and it is then unregistered — which is exactly the sequence <c>SimBridge.Close</c> performs, minus the party.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class RealmClosedChecks
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

        // Restored, because this case WIDENS it and neither Reset method covers it. It happens to be re-set by the next TatooineSim's constructor, so
        // leaving it would be invisible until some later fixture read it without building a sim — which is exactly the kind of cross-fixture leak that costs
        // an afternoon to find, from a symptom in code that did not change.
        TatooineReplication.ViewableRealms = 1;
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        Worlds.Delete(_dir);
    }

    /// <summary>What this case needs to see of a client's frames: which realm it holds, and how many times it was reset.</summary>
    private struct RealmSink : ITickSink
    {
        /// <summary>The realm of the last <c>REALM</c> block, or <see cref="RealmFrame.NoRealm"/> before one arrives.</summary>
        public int RealmId;

        /// <summary>Every realm the session was told it was in, in order — <see cref="RealmFrame.NoRealm"/> for <c>REALM(NONE)</c>.</summary>
        public System.Collections.Generic.List<int> Realms;

        public void BeginTick(uint tick, TickFlags flags, uint periodUs)
        {
        }

        public void Realm(RealmFrame frame)
        {
            RealmId = frame?.RealmId ?? RealmFrame.NoRealm;
            Realms.Add(RealmId);
        }

        public void BeginEntities(ArchetypePlan archetype)
        {
        }

        public void Enter(uint netId, scoped System.ReadOnlySpan<double> position, scoped System.ReadOnlySpan<double> velocity, uint t0, byte epoch)
        {
        }

        public void Segment(uint netId, scoped System.ReadOnlySpan<double> position, scoped System.ReadOnlySpan<double> velocity, uint t0, byte epoch)
        {
        }

        public void State(uint netId, byte groupMask)
        {
        }

        public void Leave(uint netId)
        {
        }

        public void Number(FieldPlan field, scoped System.ReadOnlySpan<double> components)
        {
        }

        public void Integer64(FieldPlan field, scoped System.ReadOnlySpan<ulong> components)
        {
        }

        public void Text(FieldPlan field, scoped System.ReadOnlySpan<byte> utf8)
        {
        }

        public void Bytes(FieldPlan field, scoped System.ReadOnlySpan<byte> bytes)
        {
        }

        public void List(FieldPlan field, int count, scoped System.ReadOnlySpan<double> components)
        {
        }

        public void Event(MessagePlan type)
        {
        }

        public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask)
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

        public void AggregateCell(uint cell, scoped System.ReadOnlySpan<uint> counts)
        {
        }

        public void Metric(MetricPlan metric, int valueIndex, double value)
        {
        }

        public void Debug(byte subType, scoped System.ReadOnlySpan<byte> payload)
        {
        }

        public void Ext(uint appTypeId, scoped System.ReadOnlySpan<byte> payload)
        {
        }

        public void UnknownBlock(byte blockType)
        {
        }

        public void EndTick()
        {
        }
    }

    private SimConfig Config()
    {
        var config = Worlds.Small(_dir);
        config.Unpaced = false;

        // 40 Hz for the same reason SpectateChecks uses it: every Ticks(1) here is a real wall-clock wait. One dungeon slot, reserved and left unregistered,
        // is what this case registers into.
        config.TickRateHz = 40;
        config.WorkerCount = 1;
        // TWO slots, and this case uses the second. The simulation opens one on its very first tick (the next-dungeon tick starts at 0), so slot 0 is never
        // free to a test; the interval and the stay are pushed past any run of this fixture so that the one it does open neither closes nor is followed by
        // another while the case is running — its churn would be harmless but its log lines would not be.
        config.Dungeons = 2;
        config.DungeonIntervalS = 3600f;
        config.DungeonStayS = 3600f;
        config.DungeonParty = 0;
        config.DungeonMobs = 0;
        return config;
    }

    /// <summary>The realm the client is being told it is in, decoded from its frames.</summary>
    private static RealmSink Drain(FakeLink link, ref int read, ref RealmFrame frame, ref RealmSink sink)
    {
        var sent = link.Sent;
        for (; read < sent.Length; read++)
        {
            var message = sent[read];
            if (message.Length > 0 && message[0] == MessageTypes.Tick)
            {
                TickReader.Read(message, link.Plan, ref frame, ref sink);
            }
        }

        return sink;
    }

    /// <summary>
    /// A god camera watching a realm that is then unregistered is told it holds nothing and is put back on planet 0, without being closed.
    /// </summary>
    /// <remarks>
    /// <b>Three claims, and each of them is the failure mode of a different shortcut.</b> The <c>REALM(NONE)</c> is the engine's half — a client that was not
    /// reset would keep drawing a world that no longer exists. Landing on realm 0 is the application's half, and it is what the <c>RealmClosed</c> event is
    /// for: without the event the session would sit in no realm for the rest of its life, served events only, with nothing to tell it otherwise. Still being
    /// open is the third: a realm ending is not the viewer's fault, and closing the link would be the easy answer that loses the client.
    /// </remarks>
    [Test]
    public void ASessionInARealmThatIsTornDownIsPutBackOnThePlanetAndStaysOpen()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);

        var link = _harness.Connect(TatooineReplication.GodKind);
        SessionHarness.Until(() => link.Received(MessageTypes.Welcome), "WELCOME");
        _harness.Ticks(5);

        // The staged dungeon: one of the slots TatooineSim reserved and left unregistered, configured exactly as SimBridge.Open configures one.
        var realm = (ushort)(sim.FirstDungeonRealm + 1);
        var edge = WorldBuilder.InteriorEdgeM;
        sim.Dbe.Realms.Register(new RealmId(realm), new RealmConfig
        {
            Grid = SpatialGridConfig.Flat(Vector2.Zero, new Vector2(edge, edge), edge),
            WhenUnobserved = RealmUnobserved.Simulate,
            UnobservedTickDivisor = 1,
            Replication = TatooineSim.DungeonReplication(1),
        });

        // A god camera may only ask for a realm below ViewableRealms, which stops at the dungeon slots precisely because a dungeon's id is true when the
        // client reads the directory and false when it clicks. Widened here because this case IS the click that arrives too late.
        TatooineReplication.ViewableRealms = realm + 1;

        var read = 0;
        var frame = default(RealmFrame);
        var sink = new RealmSink { RealmId = RealmFrame.NoRealm, Realms = [] };

        link.SendCommand(nameof(ViewRealm), new RecordValues { ["realm"] = FieldValue.Of((uint)realm) });
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return Drain(link, ref read, ref frame, ref sink).RealmId == realm;
            },
            "the viewer to arrive in the realm about to be torn down");

        // The teardown, as SimBridge.Close does it: Closing from here, and the first fence that finds it empty removes it. It never held anything.
        sim.Dbe.Realms.Unregister(new RealmId(realm));

        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return Drain(link, ref read, ref frame, ref sink).RealmId == 0;
            },
            "the viewer to be sent home to planet 0 after its realm closed");

        Assert.Multiple(() =>
        {
            Assert.That(sim.Dbe.Realms.IsRegistered(new RealmId(realm)), Is.False, "the realm really was removed, not merely marked Closing");
            Assert.That(sink.Realms, Does.Contain(RealmFrame.NoRealm),
                "the client was never told it holds nothing: it would have gone straight from the dead realm to planet 0 and kept whatever it had of neither");
            Assert.That(sink.Realms[^1], Is.Zero, "and it ends up on planet 0");
            Assert.That(link.Closed, Is.Null, "a realm ending is not the viewer's fault, and the session was closed for it");
        });
    }
}
