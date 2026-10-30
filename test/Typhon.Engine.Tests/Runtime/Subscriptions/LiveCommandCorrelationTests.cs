using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Typhon.Engine.Tests.Profiler;
using Typhon.Profiler;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// Request correlation for client commands against a live runtime (#1517, steps 2-3): the real scheduler scopes a command to the system that reads it
/// and clears it when that system's body ends, and the real send pump records the session's frame.
/// </summary>
[TestFixture]
[NonParallelizable] // the profiler is process-global
[Category("TelemetryGated")] // Correlation is off suite-wide; shard.py's T pass turns it on
internal sealed class LiveCommandCorrelationTests : TestBase<LiveCommandCorrelationTests>
{
    private readonly ConcurrentQueue<(ulong Hi, ulong Lo)> _inLoop = new();
    private readonly ConcurrentQueue<(ulong Hi, ulong Lo)> _afterSystem = new();
    private int _afterRuns;

    [TearDown]
    public void TearDownProfiler()
    {
        try { TyphonProfiler.Stop(); } catch { /* already stopped */ }
        TyphonProfiler.ResetForTests();
    }

    [Test]
    [CancelAfter(20_000)]
    public void A_command_is_scoped_to_its_system_cleared_after_it_and_its_frame_is_recorded()
    {
        Assume.That(TelemetryConfig.ProfilerCorrelationActive, Is.True, "needs TYPHON__PROFILER__CORRELATION__ENABLED=true (shard.py T pass)");

        using var read = new ManualResetEventSlim();
        using var settled = new ManualResetEventSlim();
        using var observer = new TraceRingObserver(ResourceRegistry.Profiler, captureRawBytes: true);
        TyphonProfiler.AttachExporter(observer);
        TyphonProfiler.Start(ResourceRegistry.Profiler, new ProfilerSessionMetadata(systems: [], archetypes: [], componentTypes: [], workerCount: 0,
            baseTickRate: 100f, startTimestamp: Stopwatch.GetTimestamp(), stopwatchFrequency: Stopwatch.Frequency, startedUtc: DateTime.UtcNow));
        var from = Stopwatch.GetTimestamp();

        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test")
                .CallbackSystem("Read", ctx =>
                {
                    foreach (ref readonly var command in ctx.Subscriptions.Commands<SelfStep>())
                    {
                        using var span = TyphonEvent.BeginBTreeInsert();
                        _inLoop.Enqueue((span.Header.TraceIdHi, span.Header.TraceIdLo));
                        read.Set();
                        break; // leaves the command in scope: the scheduler's end-of-body clear is what this test is about
                    }
                })
                .CallbackSystem("After", _ =>
                {
                    using var span = TyphonEvent.BeginBTreeInsert();
                    _afterSystem.Enqueue((span.Header.TraceIdHi, span.Header.TraceIdLo));
                    // Three ticks past the read, so the send pump has framed the tick that answered it.
                    if (read.IsSet && Interlocked.Increment(ref _afterRuns) == 3)
                    {
                        settled.Set();
                    }
                }, after: "Read");
        }, new RuntimeOptions
        {
            WorkerCount = 1, BaseTickRate = 100,
            Subscriptions = new SubscriptionsOptions
            {
                IngressBytesPerSecond = TestIngress.Budget, ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0),
            },
        });

        runtime.Subscriptions.Sessions.Kinds("god");
        ProjectionTestSchema.DeclarePlayer(runtime.Subscriptions);
        runtime.Subscriptions.Profile("world", p => p.World().Of<ProjPlayer>());
        runtime.Subscriptions.Command<SelfStep>(c => c.Roles(SessionRole.Spectator, SessionRole.Player).Field(m => m.Step, Codec.VarUInt));
        runtime.Start();

        var subscriptions = runtime.SubscriptionsContextForTest.Subscriptions;
        var transport = new CapturingTransport();
        runtime.StartSubscriptionTransport(transport);
        var link = new InProcessLink();
        var connection = (SubscriptionConnection)transport.Acceptor.Accept(link,
            new LinkInfo { Transport = "fake", SubProtocol = ProtocolConstants.WebSocketSubprotocol });
        link.Connection = connection;
        connection.OnMessage(ClientMessages.Hello("god"));
        var session = connection.Session;

        var plan = CatalogPlan.Compile(subscriptions.Catalog.Canonical);
        var buffer = new byte[256];
        var writer = new WireWriter(buffer);
        CommandsMessage.Write(ref writer, 1, [(plan.CommandByName(nameof(SelfStep)), (ushort)77, new RecordValues { ["Step"] = FieldValue.Of(1u) })]);
        connection.OnMessage(writer.Written);

        Assert.That(read.Wait(TimeSpan.FromSeconds(5)), Is.True, "the command never reached the system");
        Assert.That(settled.Wait(TimeSpan.FromSeconds(5)), Is.True, "the runtime stopped ticking");

        // Ticks are not frames: under load the pump can be behind them, and an idle session gets none. The record of the frame that answered the command is
        // what the assertions below read, so it is what this waits for.
        List<CorrelationInstantData> Records() => observer.GetRecords()
            .Where(r => CorrelationInstantCodec.Covers(r.Kind))
            .Select(r => CorrelationInstantCodec.Decode(r.Bytes))
            .Where(r => r.Timestamp >= from && r.Session == session.Value)
            .ToList();

        Assert.That(SpinWait.SpinUntil(() =>
        {
            var seen = Records();
            var drained = seen.FirstOrDefault(r => r.Kind == TraceEventKind.IngressAdmitted);
            return drained.Kind == TraceEventKind.IngressAdmitted && seen.Any(r => r.Kind == TraceEventKind.EgressSent && r.Tick >= drained.Tick && r.Bytes > 0);
        }, TimeSpan.FromSeconds(5)), Is.True, "the pump sent no frame for the tick that answered the command");
        runtime.Shutdown();
        TyphonProfiler.Stop();

        var records = Records();
        var admitted = records.Single(r => r.Kind == TraceEventKind.IngressAdmitted);
        var expected = CorrelationInstantCodec.CommandCausalId(session.Value, admitted.Tick, 77);

        Assert.Multiple(() =>
        {
            Assert.That(_inLoop.Single(), Is.EqualTo(expected), "the span inside the loop carries the command's id, derived from its drain tick");
            Assert.That(_afterSystem, Is.Not.Empty);
            Assert.That(_afterSystem.All(s => s == (0UL, 0UL)), Is.True, "the scheduler cleared the id the break left behind before the next system ran");
            Assert.That(records.Single(r => r.Kind == TraceEventKind.CommandConsumed).SysIdx,
                Is.EqualTo((ushort)runtime.Scheduler.Systems.ToList().FindIndex(s => s.Name == "Read")), "consumption names the reading system");
            Assert.That(records.Any(r => r.Kind == TraceEventKind.EgressSent && r.Tick >= admitted.Tick && r.Bytes > 0), Is.True,
                "the send pump recorded the session's frame for the tick that answered the command");
        });
    }

    private sealed class CapturingTransport : ISubscriptionTransport
    {
        public ISubscriptionAcceptor Acceptor { get; private set; }

        public void Start(ISubscriptionAcceptor acceptor) => Acceptor = acceptor;

        public System.Threading.Tasks.ValueTask StopAsync() => System.Threading.Tasks.ValueTask.CompletedTask;
    }
}
