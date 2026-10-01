using System;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// #1006 — teardown with a send pump still inside a send must not free the memory that pump is holding.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard is by construction, not by bad luck.</b> <see cref="SendPump.Dispose"/> cancels, closes every link and then spins for at most
/// <c>QuiesceTimeoutMs</c> (5 s) on its active-pump count — deliberately bounded, because a transport that never returns from a write must not hang the
/// runtime's teardown forever. Past that deadline the old code recorded <c>PumpsStillRunningAtDispose</c> and freed the pool anyway. The pump then resumed on
/// a faulted or cancelled send, took its catch path and called <c>Complete</c> → <c>SendStateOf(slot)->CompleteSend(...)</c>: a write through a pointer into
/// memory that had just been freed, which the process does not survive and no handler can catch.
/// </para>
/// <para>
/// <b>Why no existing fixture reached it.</b> Every other link either completes synchronously or parks on a <i>cancellable</i> <c>Task.Delay</c>, so
/// shutdown's cancel releases the pump at once and the deadline is never crossed. <see cref="InProcessLink.StallSends"/> exists for this case and ignores the
/// token, which is what a socket write to a peer that has stopped reading does too.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class SendPumpTeardownTests : TestBase<SendPumpTeardownTests>
{
    private const string Profile = "god-world";
    private const int TickRateHz = 200;

    /// <summary>
    /// A pump still sending when the quiesce deadline passes leaves the assembler's memory allocated, and the pump's late completion is then harmless.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-04")]
    public void APumpStillSendingAtDispose_NeverCompletesIntoTheFreedBlock()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        var runtime = CreateRuntime(dbe);
        FrameAssembler frames;
        var link = new InProcessLink();
        try
        {
            Declare(runtime.Subscriptions);
            runtime.Start();
            frames = runtime.SubscriptionsContextForTest.Subscriptions.Frames;

            var acceptor = StartTransport(runtime);
            Connect(acceptor, link);

            // One frame delivered normally first: the session has to be live and sending before the stall means anything.
            Assert.That(WaitFor(() => link.PendingCount > 0, TimeSpan.FromSeconds(5)), Is.True, "the session never received a frame, so nothing was sending");

            // From here every send parks, ignoring cancellation, and the pump that owns it is stuck inside TrySendOneAsync.
            link.StallSends();
            Assert.That(link.WaitForStalledSend(TimeSpan.FromSeconds(5)), Is.True, "no send was parked, so no pump is held across the teardown");

            var subs = runtime.SubscriptionsContextForTest.Subscriptions;
            var pump = subs.SendPumpForTest;

            // Bounded 5 s quiesce, then the teardown must choose: free what the pump holds, or leak it.
            runtime.Dispose();

            Assert.That(pump.PumpsStillRunningAtDispose, Is.GreaterThan(0),
                "the quiesce must have timed out with a pump inside a send, or this case is not exercising the hazard at all");

            // The bytes that pump is sending live in a pool slab. Freeing it under the transport is a read of freed memory, so the slabs are kept.
            Assert.That(subs.Frames.Pool.SlabsKeptForOutstandingSends, Is.True,
                "a pump outlived the quiesce, so the frame pool must leave its slabs allocated rather than free them under a send");

            // Faulting the send resumes that pump into its catch path, where it completes the frame through the assembler's send state. That used to be a
            // write through a freed pointer; the states are a managed array the assembler still references, so it is a write to a live slot nobody reads
            // again. The link closing is the observable proof the pump ran its catch path to the end instead of taking the process down with it.
            link.FaultStalledSends();
            Assert.That(WaitFor(() => link.CloseCount > 0, TimeSpan.FromSeconds(10)), Is.True,
                "the resumed pump must complete its catch path — a crash here is #1006 back");
        }
        finally
        {
            runtime.Dispose();
        }
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            System.Threading.Thread.Sleep(2);
        }

        return condition();
    }

    private static void Populate(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < 64; i++)
        {
            var x = -1000f + (i * 37f);
            var z = -1000f + (i * 53f);
            var bounds = new ProjBounds { Bounds = new AABB2F { MinX = x, MinY = z, MaxX = x, MaxY = z }, Speed = 0f };
            var ai = new ProjAi { Template = (byte)(i & 0x7F), Mode = ProjAiMode.Idle, Level = (ushort)i };
            tx.Spawn<ProjRock>(ProjRock.Bounds.Set(in bounds), ProjRock.Ai.Set(in ai));
        }

        Assert.That(tx.Commit(), Is.True);
    }

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
    }, new RuntimeOptions
    {
        WorkerCount = 2,
        BaseTickRate = TickRateHz,
        Subscriptions = new SubscriptionsOptions { ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0) },
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

    /// <summary>The transport this fixture starts, which exists only to hand back the acceptor a live runtime installs.</summary>
    private sealed class CapturingTransport : ISubscriptionTransport
    {
        public ISubscriptionAcceptor Acceptor { get; private set; }

        public void Start(ISubscriptionAcceptor acceptor) => Acceptor = acceptor;

        public ValueTask StopAsync() => ValueTask.CompletedTask;
    }

    private static void Connect(ISubscriptionAcceptor acceptor, InProcessLink link)
    {
        var info = new LinkInfo { Transport = "fake", SubProtocol = ProtocolConstants.WebSocketSubprotocol };
        var connection = (SubscriptionConnection)acceptor.Accept(link, info);
        Assert.That(connection, Is.Not.Null, "a live runtime refused a connection");
        link.Connection = connection;
        connection.OnMessage(ClientMessages.Hello("god", Capabilities.None));
    }
}
