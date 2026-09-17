using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-14b — the send side: the thresholds of <see cref="SkipPolicy"/>, and the end-to-end path
/// <c>design/Subscriptions/09-phase1-build-plan.md § 5</c> calls step 2, where a client receives <c>ENTITIES</c> from a live runtime.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the thresholds are tested apart from the pipeline.</b> A lag bound computed wrongly is invisible end to end — every session in a test is fast, so
/// nothing is ever skipped — and it is exactly the kind of arithmetic that is wrong at one tick rate and right at another. The pure cases sweep the rates; the
/// live case proves the bytes actually leave.
/// </para>
/// <para>
/// <b>The live case is the one that would have caught a missing wire.</b> Everything below the handshake — the interest pass, the projection, the frame
/// assembler, the hand-off, the gate, the pump — is exercised by asserting on one thing a test cannot fake: a <c>TICK</c> message arriving on the link with an
/// entity in it.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class SendPumpTests : TestBase<SendPumpTests>
{
    private const int TickRateHz = 100;
    private const int CreatureCount = 8;

    private const string FirstProfile = "god-world";
    private const string SecondProfile = "god-world-again";

    /// <summary>The session the bind system alternates profiles for, as a packed <see cref="SessionId"/>, or zero for none.</summary>
    /// <remarks>
    /// Static because the system is declared before any session exists and the fixture is <c>[NonParallelizable]</c>, so exactly one test is ever setting it.
    /// Written and read with <see cref="Volatile"/>: the setter is the test thread and the reader is a worker.
    /// </remarks>
    private static uint _alternatingSession;

    [SetUp]
    public void ClearAlternatingSession() => Volatile.Write(ref _alternatingSession, 0);

    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    private static ProjBounds PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    // ── the thresholds ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The lag bound is the round-trip allowance and a ping period expressed in ticks, and never below its floor.</summary>
    [TestCase(1_000_000, ExpectedResult = SkipPolicy.MinimumLagTicks, TestName = "LagBound_AtOneHertz_IsTheFloor")]
    [TestCase(50_000, ExpectedResult = 12, TestName = "LagBound_AtTwentyHertz")]
    [TestCase(16_666, ExpectedResult = 33, TestName = "LagBound_AtSixtyHertz")]
    public int TheLagBoundIsTheAllowanceInTicks(int tickPeriodUs)
        => SkipPolicy.LagBoundTicks(new SubscriptionsOptions(), (uint)tickPeriodUs);

    /// <summary>A client that has acknowledged nothing is not lagging: it has had nothing to acknowledge.</summary>
    [Test]
    public void AnUnacknowledgedSessionIsNotLagging()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SkipPolicy.AcknowledgementLag(500, 0), Is.Zero, "a session that has never acknowledged is new, not behind");
            Assert.That(SkipPolicy.AcknowledgementLag(500, 500), Is.Zero);
            Assert.That(SkipPolicy.AcknowledgementLag(500, 490), Is.EqualTo(10));
            Assert.That(SkipPolicy.AcknowledgementLag(490, 500), Is.Zero, "an acknowledgement ahead of what was produced is not negative lag");
        });
    }

    /// <summary>A degraded session produces on a power-of-two cadence, and every session at one level lands on the same ticks.</summary>
    [Test]
    public void ADegradedSessionProducesOnItsRateClass()
    {
        Assert.Multiple(() =>
        {
            for (var tick = 0; tick < 8; tick++)
            {
                Assert.That(SkipPolicy.ProducesOnTick(0, tick), Is.True, "an undegraded session produces every tick");
                Assert.That(SkipPolicy.ProducesOnTick(1, tick), Is.EqualTo(tick % 2 == 0));
                Assert.That(SkipPolicy.ProducesOnTick(2, tick), Is.EqualTo(tick % 4 == 0));
                Assert.That(SkipPolicy.ProducesOnTick(9, tick), Is.EqualTo(tick % 4 == 0), "a level above the cap is the cap");
            }
        });
    }

    /// <summary>Twenty skips drop one class, forty drop a second, and the ladder stops at two.</summary>
    [Test]
    public void TheDegradeLadderIsClimbedOneClassAtATime()
    {
        var options = new SubscriptionsOptions();

        Assert.Multiple(() =>
        {
            Assert.That(SkipPolicy.ShouldDegrade(19, 0, options), Is.False);
            Assert.That(SkipPolicy.ShouldDegrade(20, 0, options), Is.True);
            Assert.That(SkipPolicy.ShouldDegrade(20, 1, options), Is.False, "a second class costs another 20 skips, not the same 20");
            Assert.That(SkipPolicy.ShouldDegrade(40, 1, options), Is.True);
            Assert.That(SkipPolicy.ShouldDegrade(1000, SkipPolicy.MaxDegradeLevel, options), Is.False, "the ladder has a top");
        });
    }

    /// <summary>Fifty skips close the session, and anything below it is a skip rather than a close.</summary>
    [Test]
    public void FiftySkipsCloseTheSession()
    {
        var options = new SubscriptionsOptions();

        Assert.Multiple(() =>
        {
            Assert.That(SkipPolicy.Evaluate(0, options), Is.EqualTo(SkipVerdict.Produce));
            Assert.That(SkipPolicy.Evaluate(49, options), Is.EqualTo(SkipVerdict.Skip));
            Assert.That(SkipPolicy.Evaluate(50, options), Is.EqualTo(SkipVerdict.Close));
        });
    }

    /// <summary>Silence is three ping periods, and never tighter than the lag bound, whatever the tick rate.</summary>
    [Test]
    public void SilenceIsNeverTighterThanLag()
    {
        var options = new SubscriptionsOptions();

        Assert.Multiple(() =>
        {
            foreach (var periodUs in new uint[] { 1_000_000, 100_000, 50_000, 16_666, 1_000 })
            {
                Assert.That(
                    SkipPolicy.SilenceBoundTicks(options, periodUs),
                    Is.GreaterThanOrEqualTo(SkipPolicy.LagBoundTicks(options, periodUs)),
                    $"at {periodUs} µs a healthy but distant client would be closed for silence before it was ever skipped for lag");
            }

            Assert.That(SkipPolicy.SilenceBoundTicks(options, 50_000), Is.EqualTo(15), "three quarters of a second at 20 Hz");
        });
    }

    // ── the live path ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// § 5 step 2, end to end: a client connects to a ticking runtime with a populated world and receives <c>TICK</c> frames carrying entities.
    /// </summary>
    [Test]
    public void AConnectedClientReceivesFramesCarryingEntities()
    {
        using var dbe = SetupEngine();
        Populate(dbe);

        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var subscriptions = runtime.SubscriptionsContextForTest.Subscriptions;
        var acceptor = StartTransport(runtime);
        var link = new InProcessLink();
        var connection = Connect(acceptor, link);

        connection.OnMessage(ClientMessages.Hello("god", Capabilities.None));
        var welcome = WelcomeMessage.Parse(link.Take());

        // Enough ticks for the session to be promoted, gathered, projected, assembled, published and pumped.
        var entities = 0;
        var frames = 0;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && entities == 0)
        {
            if (!link.TryTake(out var message, 200) || message.Length == 0 || message[0] != MessageTypes.Tick)
            {
                continue;
            }

            frames++;
            entities += CountEntityRecords(message);
        }

        Assert.Multiple(() =>
        {
            Assert.That(welcome.SessionId, Is.EqualTo(connection.Session.Value));
            Assert.That(frames, Is.GreaterThan(0),
                $"no TICK reached the link. produced={subscriptions.Frames.FramesProduced} skipped={subscriptions.Frames.FramesSkipped} "
                + $"sent={subscriptions.SendPump.FramesSent} failures={subscriptions.SendPump.SendFailures} open={subscriptions.Sessions.OpenCount} "
                + $"tickSessions={subscriptions.Interest.TickSessionCount} committed={subscriptions.Frames.Gate.CommittedTick} "
                + $"watched={State(subscriptions).WatchedClusterCount} projectedBlocks={State(subscriptions).BlocksProjected} "
                + $"slots={State(subscriptions).SlotsProjected} records={State(subscriptions).RecordsProduced}");
            Assert.That(entities, Is.GreaterThan(0), "TICK frames arrived but carried no entity: the world was never projected into them");
            Assert.That(subscriptions.SendPump.FramesSent, Is.GreaterThan(0));
            Assert.That(subscriptions.SendPump.SendFailures, Is.Zero);
            Assert.That(subscriptions.Frames.FramesProduced, Is.GreaterThanOrEqualTo(subscriptions.SendPump.FramesSent),
                "more frames left than were ever produced");
        });
    }

    /// <summary>
    /// A frame is never sent before the tick that produced it has flushed: every frame's tick is at or below the published committed tick.
    /// </summary>
    /// <remarks>
    /// SUB-02's durability clause, asserted where it is observable — on the wire. The committed tick is read after the frames have been taken, so a frame
    /// whose tick beat it could only have been released by a pump that ignored the gate.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-02")]
    public void NoFrameIsSentBeforeItsTickIsCommitted()
    {
        using var dbe = SetupEngine();
        Populate(dbe);

        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var subscriptions = runtime.SubscriptionsContextForTest.Subscriptions;
        var acceptor = StartTransport(runtime);
        var link = new InProcessLink();
        var connection = Connect(acceptor, link);
        connection.OnMessage(ClientMessages.Hello("god", Capabilities.None));
        link.Take();

        var ticks = new List<uint>();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && ticks.Count < 3)
        {
            if (link.TryTake(out var message, 200) && message.Length > 0 && message[0] == MessageTypes.Tick)
            {
                ticks.Add(BitConverter.ToUInt32(message, 1));
            }
        }

        var committed = subscriptions.Frames.Gate.CommittedTick;

        Assert.Multiple(() =>
        {
            Assert.That(ticks, Is.Not.Empty, "nothing arrived to check the gate against");
            Assert.That(ticks, Is.Ordered, "frames reach a link in the order they were produced");
            foreach (var tick in ticks)
            {
                Assert.That(tick, Is.LessThanOrEqualTo((uint)committed), "a frame was sent for a tick that had not flushed");
            }
        });
    }

    /// <summary>
    /// A session whose link is detached while a committed frame is still in its slot stops pumping instead of spinning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shape of the defect this pins.</b> The pump's loop clears its in-progress flag when it cannot send, then re-checks whether anything is
    /// claimable so a frame published in that window is not left until the next tick. If the re-check tests only "is a frame claimable" while the send path
    /// also stops on "is there a link", the two disagree: a session whose link went away with a committed frame still in its slot clears, re-checks, re-arms,
    /// and does that for as long as the runtime lives — one thread-pool thread at full tilt, with no error anywhere.
    /// </para>
    /// <para>
    /// The trigger is ordinary rather than exotic: a client disconnects on a tick that produced a frame for it, which is what every disconnect under load
    /// looks like.
    /// </para>
    /// </remarks>
    [Test]
    public void ASessionWhoseLinkWentAwayStopsPumpingInsteadOfSpinning()
    {
        using var dbe = SetupEngine();
        Populate(dbe);

        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var subscriptions = runtime.SubscriptionsContextForTest.Subscriptions;
        var acceptor = StartTransport(runtime);
        var link = new InProcessLink();
        var connection = Connect(acceptor, link);
        connection.OnMessage(ClientMessages.Hello("god", Capabilities.None));
        link.Take();

        // Wait until the session is really being served, so the detach below lands on a slot that has frames moving through it.
        Assert.That(SpinWait.SpinUntil(() => subscriptions.SendPump.FramesSent > 0, TimeSpan.FromSeconds(5)), Is.True, "the session never received a frame");

        // Keep frames coming for this session for the rest of the test. A static world goes quiet after its first frames — nothing changes, so nothing is
        // produced — and a pump with nothing to claim stops for the right reason, which would make this case pass against the defect it exists to catch.
        Volatile.Write(ref _alternatingSession, connection.Session.Value);

        // Exactly what a disconnect does: the connection unbinds the link while the tick keeps producing.
        subscriptions.SendPump.DetachLink(connection.Session);

        // Several ticks of production with no link, so the assertion is about the pump's own loop rather than about there being nothing left to do.
        var target = runtime.CurrentTickNumber + 10;
        Assert.That(SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= target, TimeSpan.FromSeconds(5)), Is.True, "the runtime did not tick");
        Assert.That(subscriptions.Frames.FramesProduced, Is.GreaterThan(subscriptions.SendPump.FramesSent),
            "frames must be piling up unsent, or the case under test never arises");

        Assert.That(
            SpinWait.SpinUntil(() => subscriptions.SendPump.ActivePumps == 0, TimeSpan.FromSeconds(5)),
            Is.True,
            "a pump is still running for a session with no link: the loop's re-check disagrees with the send path about when to stop, and it will spin a "
            + "thread-pool thread until the runtime is disposed");
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A runtime whose one application system does what a host does: bind every session that opens to the declared profile.
    /// </summary>
    /// <remarks>
    /// This is the real binding path, not a test shortcut — <c>SubscriptionsCommands.Session(…).Profile(…)</c> staged on a worker and applied by the next
    /// tick's prologue. A session bound to no profile is in no tick's session set and receives nothing, so a fixture that reached into the table instead would
    /// pass against an engine in which the public path did not work at all.
    /// </remarks>
    private static TyphonRuntime CreateRuntime(DatabaseEngine dbe) => TyphonRuntime.Create(dbe, schedule =>
    {
        schedule.PublicTrack.DeclareDag("Test").CallbackSystem("BindProfiles", ctx =>
        {
            var subs = ctx.Subscriptions;
            if (subs == null)
            {
                return;
            }

            foreach (ref readonly var e in subs.SessionEvents)
            {
                if (e.Kind == SessionEventKind.Opened)
                {
                    subs.Session(e.Session).Profile(FirstProfile);
                }
            }

            // A frame generator for the one test that needs frames to keep being produced after it has taken the link away. The two profiles are identical in
            // what they select, so alternating them changes nothing a client would see — but a profile switch is a RESET, and a RESET is always a frame.
            var alternating = Volatile.Read(ref _alternatingSession);
            if (alternating != 0)
            {
                subs.Session(SessionId.FromValue(alternating)).Profile((ctx.TickNumber & 1) == 0 ? FirstProfile : SecondProfile);
            }
        });
    }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = TickRateHz });

    private static ArchetypeReplicationState State(SubscriptionsRuntime subs) => subs.StateOf(subs.Plans[0].ArchetypeCatalogId);

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("god");
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(FirstProfile, p => p.World().Of<ProjCreature>());

        // Identical to the first by construction. It exists so one test can make a session produce a frame every tick without a moving world: switching
        // profiles is a RESET, and the two select the same entities, so nothing about what a client sees depends on which one is bound.
        subs.Profile(SecondProfile, p => p.World().Of<ProjCreature>());
    }

    private static void Populate(DatabaseEngine dbe)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < CreatureCount; i++)
            {
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(10f + i, 10f + i)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    private static ISubscriptionAcceptor StartTransport(TyphonRuntime runtime)
    {
        var transport = new CapturingSendTransport();
        runtime.StartSubscriptionTransport(transport);
        return transport.Acceptor;
    }

    private static SubscriptionConnection Connect(ISubscriptionAcceptor acceptor, InProcessLink link)
    {
        var info = new LinkInfo { Transport = "fake", SubProtocol = ProtocolConstants.WebSocketSubprotocol };
        var connection = (SubscriptionConnection)acceptor.Accept(link, info);
        Assert.That(connection, Is.Not.Null, "a live runtime refused a connection");
        link.Connection = connection;
        return connection;
    }

    /// <summary>
    /// Counts the entity records a <c>TICK</c> carries, by walking its blocks far enough to read each one's record count.
    /// </summary>
    /// <param name="message">The whole message, type byte included.</param>
    /// <returns>Records across every <c>ENTITIES</c> block.</returns>
    /// <remarks>
    /// Deliberately shallow: this fixture is about whether frames leave, not about what is in them — <c>EntitiesEncodingTests</c> and the golden vectors pin
    /// the layout. All that is needed here is a number that is zero for a header-only keepalive and positive for a frame that describes a world.
    /// </remarks>
    private static int CountEntityRecords(byte[] message)
    {
        var reader = new WireReader(message);
        reader.ReadU8();
        reader.ReadU32();
        var flags = (TickFlags)reader.ReadU8();
        if ((flags & TickFlags.Period) != 0)
        {
            reader.ReadU32();
        }

        var records = 0;
        while (!reader.IsAtEnd)
        {
            var type = reader.ReadU8();
            var length = reader.ReadVaruAtMost(reader.Remaining, "block length");
            var block = reader.Slice(length);
            if (type != BlockTypes.Entities)
            {
                continue;
            }

            block.ReadVaru();
            records += (int)block.ReadVaru();
        }

        return records;
    }

    private sealed class CapturingSendTransport : ISubscriptionTransport
    {
        public ISubscriptionAcceptor Acceptor { get; private set; }

        public void Start(ISubscriptionAcceptor acceptor) => Acceptor = acceptor;

        public ValueTask StopAsync() => ValueTask.CompletedTask;
    }
}
