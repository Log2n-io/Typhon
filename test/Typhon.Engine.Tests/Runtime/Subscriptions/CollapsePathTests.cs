using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-19 — the collapse path: below <see cref="SubscriptionsOptions.CollapseBelowWorkUnits"/> the replication pipeline runs as ONE dispatched system with no
/// internal barriers, and it must be indistinguishable from the staged pipeline in everything but its dispatch count.
/// </summary>
/// <remarks>
/// <para>
/// <b>The differential is the whole point.</b> The collapsed shape exists to remove two of the critical path's three worker wake/barrier cycles
/// (<c>archive/Subscriptions/foundation/03-subscriptions-track.md § 4</c>). An optimization that also changed a byte of what a client receives would not be an
/// optimization; it would be a second replication implementation with half the test coverage. So the assertion is the strongest one available: the two shapes
/// produce the same frames, message for message, byte for byte.
/// </para>
/// <para>
/// <b>Two runtimes against ONE open engine, not two worlds built alike.</b> The world is spawned once and both shapes run over it in turn. Building it twice
/// would have compared two pipelines AND two spawn sequences, so a difference would not have said which had moved.
/// </para>
/// <para>
/// <b>What is normalized, and why each is not the pipeline.</b> Two runtimes start their clocks independently, so a frame's absolute tick number differs
/// between them for reasons that have nothing to do with the shape; so does the <c>PERIOD</c> field, which reports the runtime's current tick period and
/// therefore the machine's load. Both are stripped. Everything else — the flags, the block types, the record counts, every encoded field and every netId — is
/// compared exactly. <b>Empty frames are dropped</b> for the same reason: whether a tick with nothing to say emits a header-only keepalive depends on how many
/// ticks elapsed while the handshake completed.
/// </para>
/// <para>
/// <b>One session, and the projection is <see cref="ProjRock"/>, which is static. Both are limitations worth stating.</b> The session count is explained on
/// <see cref="SessionCount"/> and is the sharper of the two. As for the projection: a motion-carrying one writes the segment's ABSOLUTE start tick into the
/// record (<c>MotionTracker</c>, <c>GroupTicks[0]</c>, low 16 bits on the wire), so two independently-clocked runtimes cannot agree on it byte for byte
/// however correct both are. The static archetype removes the only tick-bearing payload field and leaves everything the collapse could plausibly break — the
/// interest partition, the netId lease per chunk index, the record sort, the per-chunk arenas and the frame assembly — in the comparison.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class CollapsePathTests : TestBase<CollapsePathTests>
{
    private const int TickRateHz = 200;
    private const int RockCount = 96;

    /// <summary>
    /// <b>One, and that is a finding rather than a convenience.</b> With several sessions, which worker gathers which session — and so the order in which
    /// frames reach the pool — follows timing. Nothing there is wrong: netIds are opaque, every frame is still sorted ascending by netId, and each frame is
    /// internally consistent. But a byte-level differential over two runs needs a run that is determined, and with one session the frame side is.
    /// <see cref="TheHarnessIsDeterministic_TwoStagedRunsAgree"/> is what proves the whole run is, rather than this comment. The projection stage still runs
    /// on as many chunks as there are workers, which is where the collapse could plausibly break something.
    /// </summary>
    private const int SessionCount = 1;
    private const string Profile = "god-world";

    /// <summary>Collapse whatever the load: the switch the A/B moves, and what the demo's <c>--subs-pipeline collapsed</c> sets.</summary>
    private const int AlwaysCollapse = int.MaxValue;

    /// <summary>The engine default: four dispatched stages, whatever the load.</summary>
    private const int NeverCollapse = 0;

    /// <summary>What one run observed: the substantive frames each session received, and the dispatch count the track reported.</summary>
    private sealed class Run
    {
        public List<byte[]>[] Frames;
        public int MaxChunksOnAComputingTick;
    }

    // ── the differential ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// For the same world, both shapes produce byte-identical frames for every session — and the collapsed one does it in a single dispatch.
    /// </summary>
    [TestCase(1, TestName = "BothShapesProduceTheSameFrames_OneWorker")]
    [TestCase(4, TestName = "BothShapesProduceTheSameFrames_FourWorkers")]
    public void BothShapesProduceTheSameFrames(int workerCount)
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        var staged = Capture(dbe, workerCount, NeverCollapse);
        var collapsed = Capture(dbe, workerCount, AlwaysCollapse);

        Assert.Multiple(() =>
        {
            AssertSameFrames(staged, collapsed, "staged", "collapsed");

            Assert.That(collapsed.MaxChunksOnAComputingTick, Is.EqualTo(1),
                "the collapsed shape must be ONE dispatched chunk — more than one means the staged stages also ran, and the two shapes shared a tick");
            Assert.That(staged.MaxChunksOnAComputingTick, Is.GreaterThan(1),
                "the staged shape must dispatch more than one chunk, or the differential is comparing the collapsed path against itself");
        });
    }

    /// <summary>
    /// The control: two runs of the SAME shape also agree, so a passing differential is evidence about the shapes rather than about the harness.
    /// </summary>
    /// <remarks>
    /// Without this, a comparison that normalized away too much would pass for both shapes and prove nothing. It fails for the same reasons the differential
    /// would, which is what makes it worth its runtime.
    /// </remarks>
    [Test]
    public void TheHarnessIsDeterministic_TwoStagedRunsAgree()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        var first = Capture(dbe, workerCount: 4, collapseWorkUnits: NeverCollapse);
        var second = Capture(dbe, workerCount: 4, collapseWorkUnits: NeverCollapse);

        AssertSameFrames(first, second, "first staged run", "second staged run");
    }

    // ── the negative cases ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The collapsed shape publishes under the same gate: no frame leaves for a tick that has not flushed.</summary>
    /// <remarks>
    /// SUB-02's durability clause, asserted on the collapsed path specifically. The gate lives outside the track — publication is a call after
    /// <c>UnitOfWork.Flush</c> returns — so collapsing the compute half must not reach it at all, and this is what says so rather than assuming it.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-02")]
    public void CollapsedPath_SendsNoFrameBeforeItsTickIsCommitted()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        using var runtime = CreateRuntime(dbe, workerCount: 4, collapseWorkUnits: AlwaysCollapse);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var subscriptions = runtime.SubscriptionsContextForTest.Subscriptions;
        var acceptor = StartTransport(runtime);
        var link = new InProcessLink();
        Connect(acceptor, link);

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
        runtime.Shutdown();

        Assert.Multiple(() =>
        {
            Assert.That(ticks, Is.Not.Empty, "the collapsed path produced nothing to check the gate against");
            Assert.That(ticks, Is.Ordered, "frames reach a link in the order they were produced");
            foreach (var tick in ticks)
            {
                Assert.That(tick, Is.LessThanOrEqualTo((uint)committed), "a frame was sent for a tick that had not flushed");
            }
        });
    }

    /// <summary>
    /// A fault on the collapsed path suppresses publication and leaves the database running, exactly as it does on the staged one.
    /// </summary>
    /// <remarks>
    /// <b>What this can and cannot reach.</b> The only injectable fault is <c>SubscriptionsContext.FaultGateForTest</c>, which throws from the gate every
    /// member of the track shares — so it reaches whichever shape is selected, and it is checked BEFORE the shape gate precisely so the collapsed path has a
    /// failure case at all. What it therefore proves is that collapsing changes nothing about how a replication failure is handled: the fault is stamped,
    /// compute and publication are suppressed together (SUB-02), and the engine keeps ticking because replication's failure is not the fence's. A fault
    /// raised from inside a stage BODY has no injection point without a new seam in the context, which is out of this slice's scope.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-02")]
    public void CollapsedPath_AFaultSuppressesPublicationAndDoesNotStopTheEngine()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        using var runtime = CreateRuntime(dbe, workerCount: 4, collapseWorkUnits: AlwaysCollapse);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var ctx = runtime.SubscriptionsContextForTest;
        var acceptor = StartTransport(runtime);
        var link = new InProcessLink();
        Connect(acceptor, link);

        // Wait for a session to be open and the track to be genuinely computing, so the fault below lands on a collapsed tick that had work to do rather
        // than on a gate that was returning false anyway.
        Assert.That(SpinWait.SpinUntil(() => ctx.SessionCount > 0, TimeSpan.FromSeconds(5)), Is.True, "no session ever opened");

        ctx.FaultGateForTest = true;

        var target = runtime.CurrentTickNumber + 5;
        var keptTicking = SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= target, TimeSpan.FromSeconds(5));

        // Waited for, not sampled once. The journal is reset at the top of every tick and stamped as that tick proceeds, so reading it after shutdown asks
        // "did the LAST tick fault", which is a different question and is answered false whenever shutdown lands on a tick that had not reached the gate
        // yet — a flake that only appears under load. Observing `true` at any point is never spurious in the other direction: nothing sets the flag but the
        // fault itself.
        var observedFault = SpinWait.SpinUntil(() => ctx.Faulted, TimeSpan.FromSeconds(5));

        runtime.Shutdown();

        Assert.Multiple(() =>
        {
            Assert.That(keptTicking, Is.True, "a throwing collapsed pipeline must not stop the engine any more than a throwing stage does");
            Assert.That(runtime.Scheduler.IsFenceFailed, Is.False, "a replication failure is not a fence failure, whichever shape raised it");
            Assert.That(runtime.Scheduler.IsTickAborted, Is.False);
            Assert.That(observedFault, Is.True, "the collapsed system's throw must be recorded");
            Assert.That(ctx.ComputeSeq, Is.Zero, "the gate threw before stamping, so no compute completed");
            Assert.That(ctx.PublishSeq, Is.Zero, "SUB-02: compute and publish are skippable together and only together");
        });
    }

    /// <summary>The option's default is the staged pipeline: nothing collapses until an operator names a threshold.</summary>
    /// <remarks>
    /// The number the threshold should hold is a measurement nobody has taken (the build plan's Q-M1), so shipping one would be an engine default derived
    /// from nothing. This pins the decision rather than the code: a later commit that quietly gives it a value has to change this line and say why.
    /// </remarks>
    [Test]
    public void TheCollapseThresholdDefaultsToStaged() => Assert.That(new SubscriptionsOptions().CollapseBelowWorkUnits, Is.Zero);

    // ── comparison ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void AssertSameFrames(Run left, Run right, string leftName, string rightName)
    {
        for (var s = 0; s < SessionCount; s++)
        {
            var a = left.Frames[s];
            var b = right.Frames[s];

            Assert.That(a, Is.Not.Empty, $"session {s} received no substantive frame from the {leftName}, so there is nothing to compare");
            Assert.That(b.Count, Is.EqualTo(a.Count),
                $"session {s} received {a.Count} substantive frames from the {leftName} and {b.Count} from the {rightName}");

            for (var f = 0; f < a.Count; f++)
            {
                Assert.That(b[f], Is.EqualTo(a[f]).AsCollection,
                    $"session {s}, frame {f}: the {leftName} and the {rightName} disagree byte for byte. Either the collapsed path changed what is "
                    + $"produced, or a record sort is under-specified and the staged path's chunk order is leaking into the wire. {Diff(a[f], b[f])}");
            }
        }
    }

    /// <summary>
    /// Where two frames of equal length diverge — how many bytes, and which. A one-byte divergence says something very different from a hundred.
    /// </summary>
    private static string Diff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return $"lengths {a.Length} vs {b.Length}";
        }

        var differing = new List<int>();
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i] && differing.Count < 16)
            {
                differing.Add(i);
            }
        }

        return $"{differing.Count} of {a.Length} bytes differ, at [{string.Join(", ", differing)}]";
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Spawns the world both shapes below are run against.</summary>
    private static void Populate(DatabaseEngine dbe)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            // Spread over the grid so the rocks land in many clusters: one block per watched cluster is what the project stage partitions over, and a world
            // that fitted in one cluster would give the staged shape a single chunk and nothing for the differential to disagree about.
            for (var i = 0; i < RockCount; i++)
            {
                var x = -4000f + (i * 83f);
                var z = -4000f + (i * 137f);
                var bounds = new ProjBounds { Bounds = new AABB2F { MinX = x, MinY = z, MaxX = x, MaxY = z }, Speed = 0f };
                var ai = new ProjAi { Template = (byte)(i & 0x7F), Mode = ProjAiMode.Idle, Alerted = 0, Level = (ushort)i, ThinkCooldown = 0 };
                tx.Spawn<ProjRock>(ProjRock.Bounds.Set(in bounds), ProjRock.Ai.Set(in ai));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
    }

    /// <summary>
    /// Runs one shape against <paramref name="dbe"/> and returns what every session received.
    /// </summary>
    /// <remarks>
    /// <b>A second runtime over the SAME open engine, not a reopened database.</b> Two runs against one live engine is the strongest form of "the same
    /// world" there is — nothing is written back and re-read, so no cluster layout, no entity id and no page can differ between the arms. Closing and
    /// reopening the database between the arms was the first shape of this and it was worse twice over: it made the world round-trip through the checkpoint
    /// for no reason, and opening the same bundle three times inside one test raced the previous scope's handle release often enough to fail about one run
    /// in ten with a lock error that had nothing to do with replication.
    /// </remarks>
    private static Run Capture(DatabaseEngine dbe, int workerCount, int collapseWorkUnits)
    {
        using var runtime = CreateRuntime(dbe, workerCount, collapseWorkUnits);
        Declare(runtime.Subscriptions);

        var maxChunks = 0;
        runtime.SubscriptionsJournalObserver = ctx =>
        {
            // Only a tick that actually computed: a gated-off tick reports zero chunks for both shapes and would hide the difference this measures.
            if (ctx.ComputeSeq > 0)
            {
                var chunks = ctx.ChunksExecuted;
                if (chunks > Volatile.Read(ref maxChunks))
                {
                    Volatile.Write(ref maxChunks, chunks);
                }
            }
        };

        runtime.Start();

        var acceptor = StartTransport(runtime);
        var links = new InProcessLink[SessionCount];
        for (var i = 0; i < SessionCount; i++)
        {
            links[i] = new InProcessLink();
            Connect(acceptor, links[i]);
        }

        var frames = Drain(links);

        runtime.Shutdown();
        runtime.SubscriptionsJournalObserver = null;

        return new Run { Frames = frames, MaxChunksOnAComputingTick = Volatile.Read(ref maxChunks) };
    }

    /// <summary>
    /// Collects every session's substantive frames until the world has been sent and the stream has gone quiet.
    /// </summary>
    /// <remarks>
    /// Quiet, not a fixed count: the world is static, so after the enter frames there is nothing to say and a fixed count would wait out the deadline. The
    /// run is finished when every session has received something and a full poll round produced nothing new.
    /// </remarks>
    private static List<byte[]>[] Drain(InProcessLink[] links)
    {
        var frames = new List<byte[]>[links.Length];
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = [];
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var arrived = false;
            var everyoneServed = true;

            for (var i = 0; i < links.Length; i++)
            {
                while (links[i].TryTake(out var message, 50))
                {
                    arrived = true;
                    var substance = Substance(message);
                    if (substance != null)
                    {
                        frames[i].Add(substance);
                    }
                }

                everyoneServed &= frames[i].Count > 0;
            }

            if (!arrived && everyoneServed)
            {
                break;
            }
        }

        return frames;
    }

    /// <summary>
    /// A frame's comparable substance: its flags and its blocks, with the absolute tick number and the <c>PERIOD</c> field removed.
    /// </summary>
    /// <returns>The normalized bytes, or <see langword="null"/> for anything that is not a <c>TICK</c> carrying at least one block.</returns>
    private static byte[] Substance(byte[] message)
    {
        if (message == null || message.Length == 0 || message[0] != MessageTypes.Tick)
        {
            return null;
        }

        var reader = new WireReader(message);
        reader.ReadU8();
        reader.ReadU32();
        var flags = (TickFlags)reader.ReadU8();
        if ((flags & TickFlags.Period) != 0)
        {
            reader.ReadU32();
        }

        var remaining = reader.Remaining;
        if (remaining == 0)
        {
            return null;
        }

        var substance = new byte[1 + remaining];
        substance[0] = (byte)(flags & ~TickFlags.Period);
        Array.Copy(message, message.Length - remaining, substance, 1, remaining);
        return substance;
    }

    /// <summary>
    /// A runtime whose one application system binds every session that opens to the declared profile — the real path, as <c>SendPumpTests</c> uses.
    /// </summary>
    private static TyphonRuntime CreateRuntime(DatabaseEngine dbe, int workerCount, int collapseWorkUnits) => TyphonRuntime.Create(dbe, schedule =>
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
    }, new RuntimeOptions
    {
        WorkerCount = workerCount,
        BaseTickRate = TickRateHz,
        // Byte-exact comparisons across runs and shapes: the projection must map blocks to chunks the same way every time.
        Subscriptions = new SubscriptionsOptions { CollapseBelowWorkUnits = collapseWorkUnits, DeterministicProjection = true },
    });

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("god");
        ProjectionTestSchema.DeclareRock(subs);
        subs.Profile(Profile, p => p.World().Of<ProjRock>());
    }

    private static ISubscriptionAcceptor StartTransport(TyphonRuntime runtime)
    {
        var transport = new CapturingTransport();
        runtime.StartSubscriptionTransport(transport);
        return transport.Acceptor;
    }

    private static SubscriptionConnection Connect(ISubscriptionAcceptor acceptor, InProcessLink link)
    {
        var info = new LinkInfo { Transport = "fake", SubProtocol = ProtocolConstants.WebSocketSubprotocol };
        var connection = (SubscriptionConnection)acceptor.Accept(link, info);
        Assert.That(connection, Is.Not.Null, "a live runtime refused a connection");
        link.Connection = connection;
        connection.OnMessage(ClientMessages.Hello("god", Capabilities.None));

        // The WELCOME, taken here so it never reaches the frame comparison.
        link.Take();
        return connection;
    }

    private sealed class CapturingTransport : ISubscriptionTransport
    {
        public ISubscriptionAcceptor Acceptor { get; private set; }

        public void Start(ISubscriptionAcceptor acceptor) => Acceptor = acceptor;

        public ValueTask StopAsync() => ValueTask.CompletedTask;
    }
}
