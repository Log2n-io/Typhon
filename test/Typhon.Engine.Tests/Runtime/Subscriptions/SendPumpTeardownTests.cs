using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Both cases' quiesce, in milliseconds: each needs it to time out, and the production 5 s would only make that slower.</summary>
    private const int QuiesceTimeoutMs = 100;

    /// <summary>
    /// A pump still sending when the quiesce deadline passes leaves the assembler's memory allocated, and the pump's late completion is then harmless.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-04")]
    [VerifiesRule("SUB-38")]
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
            var held = link.StalledMessage;
            var expected = held.ToArray();

            var subs = runtime.SubscriptionsContextForTest.Subscriptions;
            var pump = subs.SendPumpForTest;

            // Bounded quiesce, then the teardown must choose: free what the pump holds, or leak it. Shortened from 5 s: it times out either way.
            pump.SetQuiesceTimeoutForTest(QuiesceTimeoutMs);
            runtime.Dispose();

            Assert.That(pump.PumpsStillRunningAtDispose, Is.GreaterThan(0),
                "the quiesce must have timed out with a pump inside a send, or this case is not exercising the hazard at all");

            // The bytes that pump is sending live in a pool slab. Freeing it under the transport is a read of freed memory, so the slabs are kept.
            Assert.That(subs.Frames.Pool.SlabsKeptForOutstandingSends, Is.True,
                "a pump outlived the quiesce, so the frame pool must leave its slabs allocated rather than free them under a send");

            // And they really are still allocated. The flag alone proved nothing while the scheduler's cascade had already freed the slabs before it was set
            // (#1508): the bytes below then read back intact only because a freed slab usually stays mapped.
            var slabs = NativeBlocks(subs.Frames.Pool);
            Assert.That(slabs, Is.Not.Empty, "the frame pool kept no slab, so the bytes the parked send is reading were freed under it");
            Assert.That(slabs.Where(b => b.IsDisposed).Select(b => b.Id), Is.Empty, "a slab the parked send may be reading was freed under it");

            // And the view the link was handed is still the frame: the link owns those bytes until its task ends (ISubscriptionLink.SendAsync), so a kept slab
            // behind a released view is a send that faults mid-write rather than one that finishes.
            Assert.That(StillReadable(held), Is.EqualTo(expected),
                "the parked send's memory was taken away under it: the pump released its view while the send it lent it to was still running");

            // Faulting the send resumes that pump into its catch path, where it completes the frame through the assembler's send state. That used to be a
            // write through a freed pointer; the states are a managed array the assembler still references, so it is a write to a live slot nobody reads
            // again. The pump leaving, with the failure counted, is the proof it ran its catch path to the end instead of taking the process down with it —
            // and waiting for it here keeps a regression in this fixture rather than in whichever one runs next.
            var failuresBefore = pump.SendFailures;
            link.FaultStalledSends();
            Assert.That(WaitFor(() => pump.ActivePumps == 0, TimeSpan.FromSeconds(10)), Is.True,
                "the resumed pump must complete its catch path — a crash here is #1006 back");
            Assert.That(pump.SendFailures, Is.GreaterThan(failuresBefore), "the pump left without its catch path, so the late completion never ran");
        }
        finally
        {
            runtime.Dispose();
        }
    }

    /// <summary>
    /// A <c>PONG</c> still on a stalled link when the quiesce deadline passes keeps its buffer: the pump's own native allocation, which a release would free
    /// under the socket.
    /// </summary>
    /// <remarks>
    /// The frame half of #1006 is the pool's, and the case above covers it; this is the other memory a pump lends a link. No tick runs, so no frame is ever
    /// claimable and the pump's only send is the <c>PONG</c> — the stall lands there by construction rather than by timing.
    /// </remarks>
    [Test]
    public void APongStillSendingAtDispose_KeepsItsBuffer()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using var harness = FrameHarness.Create(dbe, DeclareForHarness, nameof(SendPumpTeardownTests));
        var session = harness.OpenSessions(1, Profile)[0];
        var pump = harness.Subscriptions.SendPump;
        pump.SetQuiesceTimeoutForTest(QuiesceTimeoutMs);

        var link = new InProcessLink();
        link.StallSends();
        pump.AttachLink(session, link);

        Assert.That(pump.RequestPong(session, 7), Is.True, "the pump refused the PING");
        Assert.That(link.WaitForStalledSend(TimeSpan.FromSeconds(5)), Is.True, "no send was parked, so no pump is held across the teardown");
        var held = link.StalledMessage;
        var expected = held.ToArray();
        Assert.That(expected[0], Is.EqualTo(MessageTypes.Pong), "the parked send must be the PONG, or this case is not about the PONG's buffer");

        try
        {
            harness.Subscriptions.Dispose();

            Assert.That(pump.PumpsStillRunningAtDispose, Is.EqualTo(1),
                "the quiesce must have timed out with the pump inside the PONG's send, or this case is not exercising the hazard at all");
            Assert.That(StillReadable(held), Is.EqualTo(expected),
                "the PONG's buffer was released under a send that still owned it: on a socket, that is the kernel reading freed memory");
        }
        finally
        {
            // Resumed into its catch path, the pump must finish rather than leave a pool thread behind for the next fixture.
            link.FaultStalledSends();
            Assert.That(WaitFor(() => pump.ActivePumps == 0, TimeSpan.FromSeconds(5)), Is.True, "the resumed pump never finished");
        }
    }

    /// <summary>
    /// A teardown whose quiesce succeeds still releases the pump's buffers: the leak is for a pump that outlived the wait, not for every shutdown.
    /// </summary>
    [Test]
    public void ACleanDispose_StillReleasesThePongBuffer()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using var harness = FrameHarness.Create(dbe, DeclareForHarness, nameof(SendPumpTeardownTests));
        var session = harness.OpenSessions(1, Profile)[0];
        var pump = harness.Subscriptions.SendPump;

        var link = new InProcessLink();
        link.StallSends();
        pump.AttachLink(session, link);

        // The PONG's send is parked only to capture the memory it was lent, then ended, so the pump is gone before the teardown starts.
        Assert.That(pump.RequestPong(session, 7), Is.True, "the pump refused the PING");
        Assert.That(link.WaitForStalledSend(TimeSpan.FromSeconds(5)), Is.True, "no send was parked, so the PONG's memory was never captured");
        var held = link.StalledMessage;
        link.FaultStalledSends();
        Assert.That(WaitFor(() => pump.ActivePumps == 0, TimeSpan.FromSeconds(5)), Is.True, "the pump never finished");
        Assert.That(StillReadable(held), Is.Not.Null, "the buffer is the pump's until the teardown, so it must still be there now");

        harness.Subscriptions.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(pump.PumpsStillRunningAtDispose, Is.Zero, "no pump was running, so the quiesce cannot have given up");
            Assert.That(StillReadable(held), Is.Null, "a teardown with no pump left must release the PONG's buffer, not leak it");
        });
    }

    /// <summary>
    /// #1508 — the scheduler's disposal frees none of the native memory a send pump or a transport thread still reads: the subscriptions runtime frees it,
    /// after it has quiesced them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TyphonRuntime.Dispose"/> disposes the scheduler first, and the scheduler was the resource parent of the session table, the frame pool and
    /// the ingress rings. Its cascade freed all three while the table's caller gate was still open, so a pump the last tick had queued — run late by a busy
    /// pool — read a freed row: an access violation in <c>SessionTable.OwnerIdAt</c> that no handler catches, and the test host gone with it.
    /// </para>
    /// <para>
    /// <b>Sized so a regression cannot hide.</b> At the default 8 192 slots the table's block sits just under the heap's own-mapping threshold, so a freed
    /// one usually stays mapped and the pump reads stale bytes that still look like rows — which is why the SWG suite crashed one run in several hundred
    /// tests. At 65 535 the block is mapped on its own and unmapped when freed, and the same read faults every time. The ownership is asserted before the
    /// pump is woken, so a regression fails here as an assertion and never reaches the read.
    /// </para>
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-38")]
    public void TheSchedulersDisposal_FreesNothingAPumpStillReads()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        var runtime = CreateRuntime(dbe,
            new SubscriptionsOptions { ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0), MaxSessions = SessionTable.MaxSlots });
        var link = new InProcessLink();
        try
        {
            Declare(runtime.Subscriptions);
            runtime.Start();
            Connect(StartTransport(runtime), link);
            Assert.That(WaitFor(() => link.PendingCount > 0, TimeSpan.FromSeconds(5)), Is.True, "the session never received a frame, so nothing was sending");

            var subs = runtime.SubscriptionsContextForTest.Subscriptions;
            var pump = subs.SendPumpForTest;
            var session = subs.Sessions.IdAt(0);
            Assert.That(session.IsValid, Is.True, "the one connected session must hold slot 0");

            Assert.That(WaitFor(() => NativeBlocks(subs.Frames.Pool).Count > 0, TimeSpan.FromSeconds(5)), Is.True,
                "no frame was ever encoded, so the frame pool holds no slab and this case would cover the table alone");

            // A ring held the way a session's transport thread holds one. This fixture declares no commands, so no session leases a ring of its own.
            var rings = subs.IngressRingsForTest;
            Assert.That(rings.TryAcquire(out var lease), Is.True, "the ingress pool lent no ring");

            var blocks = NativeBlocks(subs.ResourcesForTest);
            var ingress = subs.ResourcesForTest.Children.Single(c => c.Id == "Subscriptions.IngressRings");
            Assert.Multiple(() =>
            {
                // One per reader the cascade used to free under: the pumps read the rows, a parked send reads a frame slab, a transport thread writes a ring.
                Assert.That(blocks.Select(b => b.Id), Does.Contain("Subscriptions.Sessions"),
                    "the session table's rows must be among the blocks the runtime owns");
                Assert.That(NativeBlocks(subs.Frames.Pool), Is.Not.Empty, "the frame pool's slabs must be among them");
                Assert.That(NativeBlocks(ingress), Is.Not.Empty, "the ingress ring's slab must be among them");
            });

            // The window TyphonRuntime.Dispose used to open by disposing the scheduler first: the scheduler is gone, the subscriptions runtime is not.
            runtime.Scheduler.Dispose();

            Assert.That(subs.Sessions.IsDisposed, Is.False, "the table's own teardown has not run, so its gate is still open to every pump");
            AssertNoneFreed(blocks);

            // A pump that runs now — as one the last tick queued does when the pool is busy — reads the table through its open gate.
            var pongs = pump.PongsSent;
            Assert.That(pump.RequestPong(session, 1), Is.True, "the pump refused the PING, so no pump ran inside the window");
            Assert.That(WaitFor(() => pump.ActivePumps == 0 && pump.PongsSent > pongs, TimeSpan.FromSeconds(5)), Is.True,
                "the pump woken inside the window never finished");

            // And a transport thread writing a client's record into its ring does so into memory that is still the ring's.
            Assert.That(lease.Ring.TryWrite([1, 2, 3]), Is.True, "the ring refused a record inside the window");
            Assert.That(lease.Ring.IsEmpty, Is.False, "the record written inside the window is not in the ring");
            rings.Release(lease);

            runtime.Dispose();

            // One owner, not none: the fix must move the free, not drop it.
            Assert.Multiple(() =>
            {
                Assert.That(blocks.Where(b => !b.IsDisposed).Select(b => b.Id), Is.Empty, "the runtime's teardown must free every block it owns");
                Assert.That(subs.ResourcesForTest.Children, Is.Empty, "a freed block must also leave the resource tree");
            });
        }
        finally
        {
            runtime.Dispose();
        }
    }

    /// <summary>
    /// <see cref="TyphonRuntime.Dispose"/> quiesces the subscriptions runtime before the scheduler's disposal frees what is registered under the scheduler:
    /// belt and braces with the runtime's own node, for a block a later change registers there (#1508).
    /// </summary>
    /// <remarks>
    /// A probe node under the scheduler records, at the moment the scheduler's cascade reaches it, whether the session table's gate has already latched —
    /// which happens in the subscriptions runtime's Dispose, after the send pump is torn down and before the frame pool is.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-38")]
    public void TheRuntimesDisposal_QuiescesSubscriptionsBeforeTheSchedulersCascade()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        var runtime = CreateRuntime(dbe);
        var link = new InProcessLink();
        try
        {
            Declare(runtime.Subscriptions);
            runtime.Start();
            Connect(StartTransport(runtime), link);
            Assert.That(WaitFor(() => link.PendingCount > 0, TimeSpan.FromSeconds(5)), Is.True, "the session never received a frame, so nothing was sending");

            var subs = runtime.SubscriptionsContextForTest.Subscriptions;
            bool? latchedAtCascade = null;
            _ = new DisposeProbe(runtime.Scheduler, () => latchedAtCascade = subs.Sessions.IsDisposed);
            bool? tickingAtTeardown = null;
            bool? workersAtTeardown = null;
            subs.DisposingForTest = () =>
            {
                tickingAtTeardown = runtime.Scheduler.IsRunning;
                workersAtTeardown = runtime.Scheduler.AnyWorkerAliveForTest;
            };

            runtime.Dispose();

            Assert.Multiple(() =>
            {
                // The other side of the order: the subscriptions runtime frees the replication state a tick reaches, so nothing may tick when it begins.
                Assert.That(tickingAtTeardown, Is.False, "the subscriptions runtime began its teardown with the timer thread still running ticks");
                Assert.That(workersAtTeardown, Is.False, "the subscriptions runtime began its teardown with a worker thread still alive");
                Assert.That(latchedAtCascade, Is.True,
                    "the scheduler's cascade ran while the subscriptions runtime was still live — its pumps and transport threads not yet quiesced");
            });
        }
        finally
        {
            runtime.Dispose();
        }
    }

    /// <summary>
    /// A start the configuration refuses part way leaves nothing of the subscriptions runtime under the scheduler — neither a block nor the node that owned
    /// them (#1508).
    /// </summary>
    /// <remarks>
    /// The scheduler outlives a refused start, and the runtime's own node is skipped by its cascade by design, so the runtime has to take the node off the tree
    /// itself. Refused here by the ingress rails, which are checked after the session table, the frame pool and the replication state are all allocated.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-38")]
    public void ARefusedStart_LeavesNothingUnderTheScheduler()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var runtime = CreateRuntime(dbe,
            new SubscriptionsOptions { ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0), AbuseWindow = TimeSpan.Zero });
        try
        {
            Declare(runtime.Subscriptions);
            Assert.That(runtime.Start, Throws.InvalidOperationException.With.Message.Contains("AbuseWindow"),
                "the start must be refused by the ingress rails, after the runtime's memory is allocated, or this case does not exercise the unwind");

            Assert.Multiple(() =>
            {
                Assert.That(runtime.Scheduler.Children.Select(c => c.Id), Does.Not.Contain("Subscriptions"),
                    "a refused start left the subscriptions runtime's node registered under a scheduler that will not dispose it");
                // Asked of the allocator, not the tree: a node already taken off the tree hides whatever it still holds from any walk of the tree.
                Assert.That(((MemoryAllocator)dbe.MemoryAllocator).LiveBlocksForTest.Where(b => HasAncestor(b, "Subscriptions")).Select(b => b.Id),
                    Is.Empty, "a refused start left a subscriptions block allocated");
            });
        }
        finally
        {
            runtime.Dispose();
        }
    }

    /// <summary>
    /// SUB-38's verifier rejects a block the scheduler's cascade frees: one registered under the scheduler directly, which is where the runtime's blocks
    /// were before #1508.
    /// </summary>
    [Test]
    [RuleMutant("SUB-38")]
    public void Mutant_ABlockRegisteredUnderTheScheduler_IsFreedByItsCascade()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var runtime = CreateRuntime(dbe);
        try
        {
            var block = dbe.MemoryAllocator.AllocatePinned("Subscriptions.Mutant", runtime.Scheduler, 4096, zeroed: true, alignment: 64);
            runtime.Scheduler.Dispose();
            RuleMutants.AssertDetects("SUB-38", FreedByCascade, () => AssertNoneFreed([block]));
        }
        finally
        {
            runtime.Dispose();
        }
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A copy of what a link still holds, or <see langword="null"/> when the view behind it has been released under it.</summary>
    /// <param name="held">The memory a parked send was handed.</param>
    /// <returns>The bytes.</returns>
    /// <remarks>A released view drops to zero length before its memory is freed, so this never reads freed bytes: the bounds check throws first.</remarks>
    private static byte[] StillReadable(ReadOnlyMemory<byte> held)
    {
        try
        {
            return held.ToArray();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private const string FreedByCascade = "the scheduler's cascade freed memory the subscriptions runtime still owns";

    /// <summary>SUB-38's verifier: none of the runtime's blocks has been freed, while the runtime itself has not been disposed.</summary>
    /// <param name="blocks">The blocks, captured before the scheduler was disposed.</param>
    private static void AssertNoneFreed(List<PinnedMemoryBlock> blocks) =>
        Assert.That(blocks.Where(b => b.IsDisposed).Select(b => b.Id), Is.Empty,
            $"{FreedByCascade}, under threads its teardown has not quiesced yet");

    /// <summary>Whether a resource has an ancestor of this id.</summary>
    private static bool HasAncestor(IResource resource, string id)
    {
        for (var node = resource.Parent; node != null; node = node.Parent)
        {
            if (node.Id == id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A resource node that runs a callback when its parent's cascade disposes it.</summary>
    private sealed class DisposeProbe : ResourceNode
    {
        private readonly Action _onDispose;

        public DisposeProbe(IResource parent, Action onDispose) : base("Probe.1508", ResourceType.Node, parent) => _onDispose = onDispose;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _onDispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Every native block registered at or below a resource node.</summary>
    /// <param name="root">The node.</param>
    /// <returns>The blocks, captured now: a block that is freed later leaves the tree but keeps answering <c>IsDisposed</c>.</returns>
    private static List<PinnedMemoryBlock> NativeBlocks(IResource root)
    {
        var blocks = new List<PinnedMemoryBlock>();
        Collect(root);
        return blocks;

        void Collect(IResource node)
        {
            foreach (var child in node.Children)
            {
                if (child is PinnedMemoryBlock block)
                {
                    blocks.Add(block);
                }

                Collect(child);
            }
        }
    }

    private static void DeclareForHarness(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareRock(subs);
        subs.Profile(Profile, p => p.World().Of<ProjRock>());
    }

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

    private static TyphonRuntime CreateRuntime(DatabaseEngine dbe, SubscriptionsOptions subscriptions = null) => TyphonRuntime.Create(dbe, schedule =>
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
        Subscriptions = subscriptions ?? new SubscriptionsOptions { ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0) },
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
