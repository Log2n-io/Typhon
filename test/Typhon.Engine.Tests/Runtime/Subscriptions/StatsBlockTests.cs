using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Client;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-16 — the <c>STATS</c> block: the eleven built-ins and whatever the application declared, dense, in catalog index order, once a second, to the sessions
/// that asked for statistics.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven through a live runtime and decoded with <c>Typhon.Client</c>, never with hand-written bytes.</b> The block's whole content is server state —
/// tick percentiles, entity counts, the open session count — so a fixture that encoded its own values would be asserting on what it just wrote. What is
/// worth pinning is that the engine's numbers reach a client's store in the order the catalog promised, which takes a real tick loop at one end and a real
/// decoder at the other.
/// </para>
/// <para>
/// <b>The emission period is overridden to one tick.</b> The production cadence is a second, and a fixture that waited for one would be a two-second test
/// per case. <see cref="StatsEncoder.EmissionPeriodTicksForTest"/> changes when a block is emitted and nothing about what is in it, which is precisely the
/// part these cases are about.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class StatsBlockTests : TestBase<StatsBlockTests>
{
    private const int TickRateHz = 100;
    private const int CreatureCount = 6;
    private const string Profile = "god-world";
    private const string AppMetric = "test.spawns";

    /// <summary>The value the application metric's source returns, so a test can assert its own number came back off the wire.</summary>
    private const double AppMetricValue = 17;

    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    /// <summary>
    /// <c>typhon.sessions</c> counts every open session, at a session count where a burst of handshakes overlaps the tick.
    /// </summary>
    /// <remarks>
    /// The first AC-1 run had 110 sessions connected, served and granted statistics, and every block any of them received said the server held about 29. A
    /// count that saturates is worse than one that is missing: an operator reads it as a real number and concludes the population is small.
    /// </remarks>
    [Test]
    public void TheSessionCountIsEverySessionThatIsOpen()
    {
        const int Sessions = 110;

        using var world = new StatsWorld(SetupEngine(), workerCount: 4);
        var clients = new StatsClient[Sessions];
        for (var i = 0; i < Sessions; i++)
        {
            clients[i] = world.Connect(Capabilities.Stats);
        }

        // The last-connected session is the one whose Opened event is most likely to still be in flight, so it is the one worth reading.
        var last = clients[Sessions - 1];
        Assert.That(last.AwaitBlock(), Is.True, "the last session to connect never received a STATS block");
        Assert.That(last.AwaitBlock(), Is.True, "the last session received one block and then no more");

        Assert.That(Scalar(last, "typhon.sessions"), Is.EqualTo(Sessions),
            "the server reported a session count that is not the number of sessions it has open");
    }

    /// <summary>
    /// Every session that asked for statistics receives the block, at a session count above the worker count.
    /// </summary>
    /// <remarks>
    /// Found by the first AC-1 run and not by any fixture: 110 sessions against the demo each received exactly one <c>STATS</c> block, during the connect
    /// ramp, and then none for the next eighty seconds, while frames kept flowing. Every fixture here ran one session against one worker, which is the one
    /// shape the defect cannot appear in. A block that reaches a client once and then stops is worse than one that never arrives, because a statistics HUD
    /// goes on displaying the first second of the run as though it were the current one.
    /// </remarks>
    [Test]
    public void EverySessionReceivesTheBlockWhenSessionsOutnumberWorkers()
    {
        const int Sessions = 40;

        using var world = new StatsWorld(SetupEngine(), workerCount: 4);
        var clients = new StatsClient[Sessions];
        for (var i = 0; i < Sessions; i++)
        {
            clients[i] = world.Connect(Capabilities.Stats);
        }

        // Two blocks, not one: the first can ride on the frame that completes the view, and what the AC-1 run showed is a session receiving that one and
        // never another.
        foreach (var client in clients)
        {
            Assert.That(client.AwaitBlock(), Is.True, "a session never received its first STATS block");
        }

        var missing = 0;
        foreach (var client in clients)
        {
            if (!client.AwaitBlock())
            {
                missing++;
            }
        }

        Assert.That(missing, Is.Zero, $"{missing} of {Sessions} sessions received one STATS block and then no more");
    }

    /// <summary>Values arrive in catalog index order, and a labelled metric contributes one value per label.</summary>
    [Test]
    public void ValuesArriveInCatalogIndexOrder()
    {
        using var world = new StatsWorld(SetupEngine());
        var client = world.Connect(Capabilities.Stats);

        Assert.That(client.AwaitBlock(), Is.True, "no frame carrying a STATS block reached the client");

        var plan = client.Store.Plan;
        var systemMean = Row(client, BuiltInMetrics.SystemMean);
        var archetypes = Row(client, BuiltInMetrics.ArchetypeEntities);

        Assert.Multiple(() =>
        {
            for (var i = 0; i < plan.ServerMetrics.Length; i++)
            {
                Assert.That(client.Store.ServerMetricValues[i], Has.Length.EqualTo(plan.ServerMetrics[i].ValueCount),
                    $"server metric '{plan.ServerMetrics[i].Name}' is laid out for the wrong number of values");
                Assert.That(plan.ServerMetrics[i].Offset, Is.EqualTo(i), "the plan's offset must be the row the applier indexes with");
            }

            Assert.That(plan.ServerMetrics, Is.Ordered.By(nameof(MetricPlan.Idx)), "a segment is laid out in catalog index order");
            Assert.That(plan.SessionMetrics, Is.Ordered.By(nameof(MetricPlan.Idx)));

            Assert.That(systemMean, Has.Length.EqualTo(Metric(client, BuiltInMetrics.SystemMean).Metric.Labels.Length),
                "a labelled metric contributes exactly |labels| values");
            Assert.That(archetypes, Has.Length.EqualTo(Metric(client, BuiltInMetrics.ArchetypeEntities).Metric.Labels.Length));

            Assert.That(archetypes[0], Is.EqualTo(CreatureCount), "the archetype's live entity count is what the block carries");
            Assert.That(Scalar(client, "typhon.sessions"), Is.EqualTo(1), "one session is connected");
            Assert.That(Scalar(client, AppMetric), Is.EqualTo(AppMetricValue), "an application metric's source is read and its value travels");
            Assert.That(Scalar(client, "typhon.tick.p50"), Is.GreaterThan(0), "a ticking runtime has a tick duration");
            Assert.That(Scalar(client, "typhon.tick.p99"), Is.GreaterThanOrEqualTo(Scalar(client, "typhon.tick.p50")));
            Assert.That(Scalar(client, "typhon.durability.wait.p99"), Is.Zero,
                "the runtime times its flush only through the profiler's phase wrapper, so this built-in is unsourced and must say zero rather than invent");
        });
    }

    /// <summary>
    /// No capability, no block — asserted against a session of the same runtime that did ask, so the absence is the capability's doing and not a quiet tick.
    /// </summary>
    [Test]
    public void TheBlockIsAbsentWithoutTheCapability()
    {
        using var world = new StatsWorld(SetupEngine());
        var without = world.Connect(Capabilities.None);
        var with = world.Connect(Capabilities.Stats);

        Assert.That(with.AwaitBlock(), Is.True, "the session that asked never received a block, so the comparison has no control");
        var frames = without.DrainAvailable();

        Assert.Multiple(() =>
        {
            Assert.That(frames, Is.GreaterThan(0), "no frame reached the session that did not ask, so the absence of a block proves nothing");
            Assert.That(without.StatsBlocks, Is.Zero, "a STATS block was emitted to a session that never requested the capability");
            Assert.That(world.Subscriptions.Stats.ServerSegmentEncodes, Is.GreaterThan(0),
                "the server segment is collected whatever any one session asked for; only the copy into a frame is gated");
        });
    }

    /// <summary>
    /// The server segment is encoded once per emission and copied into each session's frame — W25's encode-once, asserted on the counters rather than
    /// inferred from a timing.
    /// </summary>
    [Test]
    public void TheServerSegmentIsEncodedOnceForEverySession()
    {
        using var world = new StatsWorld(SetupEngine());
        var clients = new[] { world.Connect(Capabilities.Stats), world.Connect(Capabilities.Stats), world.Connect(Capabilities.Stats) };

        foreach (var client in clients)
        {
            Assert.That(client.AwaitBlock(), Is.True, "a session never received its first block, so nothing was shared with it");
        }

        var stats = world.Subscriptions.Stats;
        var encodes = stats.ServerSegmentEncodes;
        var copies = stats.ServerSegmentCopies;

        foreach (var client in clients)
        {
            Assert.That(client.AwaitBlock(), Is.True);
        }

        var deltaEncodes = stats.ServerSegmentEncodes - encodes;
        var deltaCopies = stats.ServerSegmentCopies - copies;

        Assert.Multiple(() =>
        {
            Assert.That(deltaCopies, Is.GreaterThan(0), "no block was written at all over the window");
            Assert.That(deltaCopies, Is.GreaterThan(deltaEncodes),
                $"{deltaCopies} block(s) cost {deltaEncodes} encode(s): the server segment is being re-encoded per session instead of copied");
        });
    }

    /// <summary>
    /// A tick rate whose period does not divide a second still emits: the byte-rate built-ins are <c>varu</c> fed by a division, and an integer codec is
    /// handed an integer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rate is the test.</b> <c>FieldCodec.ToUnsignedInteger</c> throws on anything with a fractional part, and the window a byte rate is divided by
    /// is exactly one second only when the period divides a second exactly — 100 Hz does, which is why every other case here is blind to this. 60 Hz gives a
    /// 16 667 µs period and a window of 1.00002 s, so <c>bytes / seconds</c> is fractional and the encode threw, on the frame stage, on every emission.
    /// </para>
    /// <para>
    /// The throw leaves the stage, faults the track and suppresses publication for the tick, so before the fix this fails by receiving no block at all
    /// rather than by receiving a wrong one. 144 Hz is included because its window (0.999936 s) falls just BELOW a second where 60 Hz's falls just above,
    /// so the two exercise the rounding from either side.
    /// </para>
    /// </remarks>
    [TestCase(60)]
    [TestCase(144)]
    public void ARateWhosePeriodDoesNotDivideASecondStillEmits(int tickRateHz)
    {
        using var world = new StatsWorld(SetupEngine(), tickRateHz);
        var client = world.Connect(Capabilities.Stats);

        // Twice: the first emission can see a zero byte total, which divides to an integer whatever the window is and would not exercise the codec.
        Assert.That(client.AwaitBlock(), Is.True, $"no STATS block arrived at {tickRateHz} Hz — the encode threw on the tick path and faulted the track");
        Assert.That(client.AwaitBlock(), Is.True, $"STATS stopped after the first block at {tickRateHz} Hz");

        Assert.Multiple(() =>
        {
            Assert.That(Scalar(client, "typhon.net.outBytesPerSec"), Is.GreaterThan(0), "bytes left the server, so the rate cannot be zero");
            Assert.That(Scalar(client, "typhon.session.outBytesPerSec", session: true), Is.GreaterThan(0));
            Assert.That(world.Subscriptions.Stats.ServerSegmentEncodes, Is.GreaterThan(1), "the server segment must survive more than one emission");
        });
    }

    /// <summary>
    /// A session that was skipped between two blocks loses nothing: its counter is cumulative, so the second block carries every skip the first did not.
    /// </summary>
    [Test]
    public void ASkippedSessionLosesNothing()
    {
        using var world = new StatsWorld(SetupEngine());
        var client = world.Connect(Capabilities.Stats);

        Assert.That(client.AwaitBlock(), Is.True);
        var before = Scalar(client, "typhon.session.skippedFrames", session: true);

        // A rate class the session did not earn, which is what the producer's skip path reads: three ticks in four are now skipped for it, and every one of
        // them has to appear in the counter the next block it does receive carries.
        var state = world.Subscriptions.Frames.StateOf(client.Session);
        state.DegradeLevel = 2;

        Assert.That(client.AwaitBlock(), Is.True, "a degraded session still receives blocks, on its own cadence");
        Assert.That(client.AwaitBlock(), Is.True);

        var after = Scalar(client, "typhon.session.skippedFrames", session: true);

        Assert.Multiple(() =>
        {
            Assert.That(after, Is.GreaterThan(before), "the skipped-frame counter did not carry the skips across the blocks that were missed");
            Assert.That(Scalar(client, "typhon.sessions"), Is.EqualTo(1), "a gauge is absolute: the skips change nothing about what it reports");
            Assert.That(Scalar(client, "typhon.session.outBytesPerSec", session: true), Is.GreaterThanOrEqualTo(0),
                "the per-session rate is a window, never a cumulative total");
        });
    }

    // ── reading the store ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static MetricPlan Metric(StatsClient client, string name, bool session = false)
    {
        foreach (var metric in session ? client.Store.Plan.SessionMetrics : client.Store.Plan.ServerMetrics)
        {
            if (metric.Name == name)
            {
                return metric;
            }
        }

        Assert.Fail($"the catalog declares no metric named '{name}'");
        return null;
    }

    private static double[] Row(StatsClient client, string name, bool session = false)
    {
        var metric = Metric(client, name, session);
        return (session ? client.Store.SessionMetricValues : client.Store.ServerMetricValues)[metric.Offset];
    }

    private static double Scalar(StatsClient client, string name, bool session = false) => Row(client, name, session)[0];

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A ticking runtime with a populated world, one profile, one application metric, and statistics emitted every tick.</summary>
    private sealed class StatsWorld : IDisposable
    {
        private readonly DatabaseEngine _engine;
        private readonly TyphonRuntime _runtime;
        private readonly ISubscriptionAcceptor _acceptor;

        public StatsWorld(DatabaseEngine engine, int tickRateHz = TickRateHz, int workerCount = 1, bool overridePeriod = true)
        {
            _engine = engine;
            Populate(engine);

            _runtime = TyphonRuntime.Create(engine, schedule =>
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
                            subs.Session(e.Session).Profile(Profile);
                        }
                    }
                });
            }, new RuntimeOptions { WorkerCount = workerCount, BaseTickRate = tickRateHz });

            _runtime.Subscriptions.Sessions.Kinds("god");
            ProjectionTestSchema.DeclareCreature(_runtime.Subscriptions);
            _runtime.Subscriptions.Profile(Profile, p => p.World().Of<ProjCreature>());
            _runtime.Subscriptions.Metric(AppMetric, "count", Codec.VarUInt, () => AppMetricValue);
            _runtime.Start();

            Subscriptions = _runtime.SubscriptionsContextForTest.Subscriptions;
            if (overridePeriod)
            {
                Subscriptions.Stats.EmissionPeriodTicksForTest = 1;
            }

            var transport = new CapturingTransport();
            _runtime.StartSubscriptionTransport(transport);
            _acceptor = transport.Acceptor;
        }

        public SubscriptionsRuntime Subscriptions { get; }

        public StatsClient Connect(Capabilities caps)
        {
            var link = new InProcessLink();
            var info = new LinkInfo { Transport = "fake", SubProtocol = ProtocolConstants.WebSocketSubprotocol };
            var connection = (SubscriptionConnection)_acceptor.Accept(link, info);
            Assert.That(connection, Is.Not.Null, "a live runtime refused a connection");
            link.Connection = connection;

            connection.OnMessage(ClientMessages.Hello("god", caps));
            var welcome = WelcomeMessage.Parse(link.Take());
            var catalog = CatalogPlan.Compile(Subscriptions.Catalog.Canonical);
            Assert.That(welcome.CapsGranted, Is.EqualTo(caps & Capabilities.Stats), "the grant is what the producer keys the block off");

            return new StatsClient(link, connection.Session, catalog);
        }

        public void Dispose()
        {
            _runtime.Dispose();
            _engine.Dispose();
        }

        private static void Populate(DatabaseEngine dbe)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < CreatureCount; i++)
                {
                    tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(new ProjBounds
                    {
                        Bounds = new AABB2F { MinX = 10f + i, MinY = 10f + i, MaxX = 10f + i, MaxY = 10f + i },
                        Speed = 1f,
                    }));
                }

                tx.Commit();
            }

            dbe.WriteTickFence(1);
        }

        private sealed class CapturingTransport : ISubscriptionTransport
        {
            public ISubscriptionAcceptor Acceptor { get; private set; }

            public void Start(ISubscriptionAcceptor acceptor) => Acceptor = acceptor;

            public ValueTask StopAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>One connected client, applying the frames it receives into a real <see cref="WorldStore"/>.</summary>
    private sealed class StatsClient
    {
        private readonly InProcessLink _link;
        private readonly FrameApplier _applier;

        public StatsClient(InProcessLink link, SessionId session, CatalogPlan plan)
        {
            _link = link;
            Session = session;
            Store = new WorldStore(plan);
            _applier = new FrameApplier(Store);
        }

        public SessionId Session { get; }

        public WorldStore Store { get; }

        /// <summary>Frames carrying a <c>STATS</c> block that this client has applied.</summary>
        public int StatsBlocks { get; private set; }

        /// <summary>Takes frames until one carries a <c>STATS</c> block, or the deadline passes.</summary>
        public bool AwaitBlock()
        {
            var target = StatsBlocks + 1;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && StatsBlocks < target)
            {
                Apply(200);
            }

            return StatsBlocks >= target;
        }

        /// <summary>Applies every frame already queued, and reports how many arrived. Stops at the first short wait that yields nothing.</summary>
        public int DrainAvailable()
        {
            var frames = 0;
            while (Apply(150))
            {
                frames++;
            }

            return frames;
        }

        private bool Apply(int timeoutMs)
        {
            if (!_link.TryTake(out var message, timeoutMs) || message.Length == 0 || message[0] != MessageTypes.Tick)
            {
                return false;
            }

            if (HasStats(message))
            {
                StatsBlocks++;
            }

            _applier.Apply(message);
            return true;
        }

        /// <summary>
        /// Whether a frame carries a <c>STATS</c> block, by walking its block headers.
        /// </summary>
        /// <remarks>
        /// The applier cannot answer this: it writes the values into the store whether or not a block arrived, so "the row is still zero" would be
        /// indistinguishable from "a metric read zero". Counting the block type on the wire is the only statement about presence.
        /// </remarks>
        private static bool HasStats(byte[] message)
        {
            var reader = new WireReader(message);
            reader.ReadU8();
            reader.ReadU32();
            var flags = (TickFlags)reader.ReadU8();
            if ((flags & TickFlags.Period) != 0)
            {
                reader.ReadU32();
            }

            while (!reader.IsAtEnd)
            {
                var type = reader.ReadU8();
                reader.Slice(reader.ReadVaruAtMost(reader.Remaining, "block length"));
                if (type == BlockTypes.Stats)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
