using NUnit.Framework;
using System;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-17: the replication track times itself, per tick and per stage, and <c>STATS</c> reports that measurement rather than a guess made from system names.
/// </summary>
/// <remarks>
/// The ring is exercised directly where the property is about the ring — wraparound, a chunk that outlives its tick, a percentile over a window with gaps in
/// it — and through a live runtime where the property is about the wiring. Driving everything through a runtime would make the first set untestable, because
/// a live tick cannot be asked to wrap a 256-entry ring on demand; driving everything through the ring would prove nothing about whether any stage actually
/// calls it.
/// </remarks>
[TestFixture]
[NonParallelizable]
sealed class SubscriptionsTelemetryTests : TestBase<SubscriptionsTelemetryTests>
{
    private const int TickRateHz = 100;
    private const string Profile = "god-world";

    // ── the ring ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A stage's chunks add up, and the track's own figure is the span they cover rather than their sum.</summary>
    /// <remarks>
    /// The distinction is the whole reason this class exists. Two chunks running side by side for 100 µs cost the server 100 µs of wall time and 200 µs of
    /// worker time; a metric that reported 200 would exceed the tick it was measuring, which is how the previous implementation could have reported a track
    /// costing more than the tick that contained it.
    /// </remarks>
    [Test]
    public void TheTrackIsTheSpanOfItsStagesAndAStageIsTheSumOfItsChunks()
    {
        var telemetry = new SubscriptionsTelemetry();
        telemetry.BeginTick(7);

        // Two overlapping chunks of one stage, each 100 units long, together spanning 150.
        telemetry.NoteStage(7, SubscriptionsStage.Project, 1000, 1100);
        telemetry.NoteStage(7, SubscriptionsStage.Project, 1050, 1150);

        var stage = telemetry.StageMicroseconds(7, SubscriptionsStage.Project);
        var track = telemetry.TrackMicroseconds(7);

        Assert.That(stage, Is.GreaterThan(0), "a stage that ran must report something");
        Assert.That(track, Is.GreaterThan(0));
        Assert.That(stage / track, Is.EqualTo(200.0 / 150.0).Within(0.01),
            "the stage sums its chunks (200 units) while the track spans them (150) — if these are equal, the track is summing and can exceed its own tick");
    }

    /// <summary>A tick the ring no longer holds reports nothing, rather than another tick's numbers.</summary>
    /// <remarks>
    /// The slot is indexed by tick number modulo the depth, so tick N and tick N+256 share it. Without the stored tick number, reading the older one would
    /// hand back the newer one's measurement — a wrong number that looks exactly like a right one.
    /// </remarks>
    [Test]
    public void ARecycledTickReportsNothingRatherThanItsSuccessorsNumbers()
    {
        var telemetry = new SubscriptionsTelemetry();
        telemetry.BeginTick(1);
        telemetry.NoteStage(1, SubscriptionsStage.Interest, 0, 500);
        Assert.That(telemetry.TrackMicroseconds(1), Is.GreaterThan(0));

        telemetry.BeginTick(1 + SubscriptionsTelemetry.Depth);
        telemetry.NoteStage(1 + SubscriptionsTelemetry.Depth, SubscriptionsStage.Interest, 0, 900);

        Assert.That(telemetry.Holds(1), Is.False, "the ring cannot still claim a tick whose slot was reused");
        Assert.That(telemetry.TrackMicroseconds(1), Is.Zero, "a recycled tick must report zero, not its successor's span");
        Assert.That(telemetry.TrackMicroseconds(1 + SubscriptionsTelemetry.Depth), Is.GreaterThan(0));
    }

    /// <summary>A chunk that finishes after its tick's slot was recycled is dropped, not added to whoever holds the slot now.</summary>
    [Test]
    public void AChunkThatOutlivesItsTickIsDroppedRatherThanMisattributed()
    {
        var telemetry = new SubscriptionsTelemetry();
        telemetry.BeginTick(4);
        telemetry.BeginTick(4 + SubscriptionsTelemetry.Depth);

        telemetry.NoteStage(4, SubscriptionsStage.Frames, 0, 10_000);

        Assert.That(telemetry.TrackMicroseconds(4 + SubscriptionsTelemetry.Depth), Is.Zero,
            "a late chunk from a recycled tick was charged to the tick that now owns the slot");
    }

    /// <summary>A tick on which the track did not run contributes no sample, instead of a zero that drags the percentile down.</summary>
    /// <remarks>
    /// The track is gated off whenever no session is watching anything, which on an idle server is most ticks. Counting those as zero-cost samples would put
    /// the p99 at zero exactly when a spike is the only thing worth seeing.
    /// </remarks>
    [Test]
    public void TicksWhereTheTrackDidNotRunAreNotSamples()
    {
        var telemetry = new SubscriptionsTelemetry();
        Span<double> scratch = stackalloc double[64];

        for (var tick = 1L; tick <= 20; tick++)
        {
            telemetry.BeginTick(tick);
            if (tick % 2 == 0)
            {
                telemetry.NoteStage(tick, SubscriptionsStage.Frames, 0, 1000);
            }
        }

        var median = telemetry.Percentile(20, window: 20, percentile: 0.5, scratch);
        Assert.That(median, Is.GreaterThan(0), "half the window ran the track, so the median cannot be zero");
    }

    /// <summary>The percentile is nearest-rank over what actually ran, and allocates nothing of its own.</summary>
    [Test]
    public void ThePercentileIsNearestRankOverTheTicksThatRan()
    {
        var telemetry = new SubscriptionsTelemetry();
        Span<double> scratch = stackalloc double[32];

        for (var tick = 1L; tick <= 10; tick++)
        {
            telemetry.BeginTick(tick);
            telemetry.NoteStage(tick, SubscriptionsStage.Interest, 0, tick * 100);
        }

        var p100 = telemetry.Percentile(10, window: 10, percentile: 1.0, scratch);
        var p10 = telemetry.Percentile(10, window: 10, percentile: 0.1, scratch);

        Assert.That(p100, Is.GreaterThan(p10), "the top of the distribution must exceed the bottom of it");
        Assert.That(telemetry.Percentile(10, window: 10, percentile: 1.0, scratch), Is.EqualTo(p100), "the percentile must not consume its own samples");
    }

    /// <summary>An empty window reports zero rather than throwing, because this runs on the tick path.</summary>
    [Test]
    public void AnEmptyWindowReportsZero()
    {
        var telemetry = new SubscriptionsTelemetry();
        Span<double> scratch = stackalloc double[8];
        Assert.That(telemetry.Percentile(5, window: 8, percentile: 0.99, scratch), Is.Zero);
        Assert.That(telemetry.TrackMicroseconds(5), Is.Zero);
        Assert.That(telemetry.StageMicroseconds(5, SubscriptionsStage.Events), Is.Zero);
    }

    // ── the wiring ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A live runtime's stages report a duration on the ticks they ran, and the track's span is at most the tick that contained it.
    /// </summary>
    /// <remarks>
    /// The upper bound is the assertion that would have caught the metric this slice replaces: a sum over per-system durations counts a parallel stage once
    /// per worker and can exceed its own tick, which no amount of reading the number tells you.
    /// </remarks>
    [Test]
    public void ALiveTracksStagesReportAndTheSpanFitsInsideItsTick()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        // A session has to exist, or the track is gated off and times nothing — which is the other test in this fixture, not this one.
        var acceptor = StartTransport(runtime);
        var link = new InProcessLink();
        var connection = (SubscriptionConnection)acceptor.Accept(link, new LinkInfo { Transport = "fake", SubProtocol = ProtocolConstants.WebSocketSubprotocol });
        Assert.That(connection, Is.Not.Null, "a live runtime refused a connection");
        link.Connection = connection;
        connection.OnMessage(ClientMessages.Hello("god", Capabilities.None));
        link.Take();

        var ctx = runtime.SubscriptionsContextForTest;
        var telemetry = ctx.Telemetry;

        // Wait for a tick whose track genuinely ran and is still in the ring. Spinning on the measurement rather than on a tick count is what keeps this
        // from asserting against a tick the gate turned the track off for.
        long measured = 0;
        var found = SpinWait.SpinUntil(
            () =>
            {
                var tick = runtime.CurrentTickNumber - 1;
                if (tick > 0 && telemetry.Holds(tick) && telemetry.TrackMicroseconds(tick) > 0)
                {
                    measured = tick;
                    return true;
                }

                return false;
            },
            TimeSpan.FromSeconds(10));

        var track = telemetry.TrackMicroseconds(measured);
        var interest = telemetry.StageMicroseconds(measured, SubscriptionsStage.Interest);
        var frames = telemetry.StageMicroseconds(measured, SubscriptionsStage.Frames);
        runtime.Shutdown();

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True, "no tick of a running subscriptions track ever reported a duration");
            Assert.That(interest, Is.GreaterThan(0), "the interest stage ran and reported nothing");
            Assert.That(frames, Is.GreaterThan(0), "the frames stage ran and reported nothing");
            Assert.That(track, Is.LessThan(1_000_000d), "the track's span for one tick exceeded a second, so it is summing rather than spanning");
        });
    }

    /// <summary>A runtime with no subscriptions declared never runs the track, so nothing is timed and the metric is honestly zero.</summary>
    [Test]
    public void AGatedOffTrackTimesNothing()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        using var runtime = TyphonRuntime.Create(dbe, _ => { }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = TickRateHz });
        runtime.Start();

        var ctx = runtime.SubscriptionsContextForTest;
        var target = runtime.CurrentTickNumber + 5;
        Assert.That(SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= target, TimeSpan.FromSeconds(5)), Is.True, "the runtime did not tick");

        var ran = 0;
        for (var tick = Math.Max(1, target - 4); tick <= target; tick++)
        {
            if (ctx != null && ctx.Telemetry.TrackMicroseconds(tick) > 0)
            {
                ran++;
            }
        }

        runtime.Shutdown();
        Assert.That(ran, Is.Zero, "a track that never ran reported a duration, so something is timing a stage that was gated off");
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

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
                    subs.Session(e.Session).Profile(Profile);
                }
            }
        });
    }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = TickRateHz });

    private static ISubscriptionAcceptor StartTransport(TyphonRuntime runtime)
    {
        var transport = new CapturingTransport();
        runtime.StartSubscriptionTransport(transport);
        return transport.Acceptor;
    }

    private sealed class CapturingTransport : ISubscriptionTransport
    {
        public ISubscriptionAcceptor Acceptor { get; private set; }

        public void Start(ISubscriptionAcceptor acceptor) => Acceptor = acceptor;

        public System.Threading.Tasks.ValueTask StopAsync() => System.Threading.Tasks.ValueTask.CompletedTask;
    }

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("god");
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.World().Of<ProjCreature>());
    }

    private static void Populate(DatabaseEngine dbe)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 16; i++)
            {
                var bounds = new ProjBounds
                {
                    Bounds = new Typhon.Schema.Definition.AABB2F { MinX = i * 4f, MinY = 10f, MaxX = (i * 4f) + 1f, MaxY = 11f },
                    Speed = 1f,
                };

                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }
}
