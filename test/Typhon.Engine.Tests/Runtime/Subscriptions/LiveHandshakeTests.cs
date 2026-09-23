using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-03 + P1-06 — step 1 of <c>archive/Subscriptions/09-phase1-build-plan.md § 5</c>: an in-process client completes the handshake against a
/// <b>live <see cref="TyphonRuntime"/></b>, and the <c>WELCOME</c> it receives carries the real catalog built from the real compiled plan.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this fixture adds over <see cref="HandshakeTests"/>.</b> That one drives every branch of the state machine against
/// <see cref="FakeSubscriptionsHost"/> — a catalog a test chose and a tick a test set — which is the right way to reach a close code, and cannot show that the
/// two halves were ever connected. Everything here comes from a runtime that is ticking: the catalog is the one <c>Start</c> compiled, the session is a row in
/// its table, and the tick and <c>usIntoTick</c> in a <c>PONG</c> are the ones the tick driver published microseconds earlier.
/// </para>
/// <para>
/// <b>The transport is a seam, not a socket.</b> <see cref="CapturingTransport"/> does what every real transport does — it takes the acceptor the runtime hands
/// it — and <see cref="InProcessLink"/> is the link, so the bytes asserted on are the bytes a WebSocket would carry
/// (<c>design/Subscriptions/04-transport.md § 2</c>).
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class LiveHandshakeTests : TestBase<LiveHandshakeTests>
{
    /// <summary>Slow enough that a tick boundary is observable from the test thread, fast enough that waiting for a few costs milliseconds.</summary>
    private const int TickRateHz = 100;

    /// <summary>The period the catalog declares and <c>WELCOME</c> reports, at multiplier 1.</summary>
    private const uint TickPeriodUs = 1_000_000 / TickRateHz;

    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    private static TyphonRuntime CreateRuntime(DatabaseEngine dbe) => TyphonRuntime.Create(dbe, schedule =>
    {
        schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
    }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = TickRateHz });

    /// <summary>The SWG-shaped declaration of <see cref="SubscriptionsRuntimeTests"/>: three archetypes, a world profile, one session kind.</summary>
    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("god");
        ProjectionTestSchema.DeclareCreature(subs);
        ProjectionTestSchema.DeclarePlayer(subs);
        ProjectionTestSchema.DeclareRock(subs);
        subs.Profile("god-world", p => p.World().Of<ProjCreature>().Of<ProjPlayer>().Of<ProjRock>());
    }

    private static SubscriptionsRuntime SubscriptionsOf(TyphonRuntime runtime) => runtime.SubscriptionsContextForTest.Subscriptions;

    /// <summary>Starts a transport the way a host would, and hands back the acceptor the runtime gave it.</summary>
    private static ISubscriptionAcceptor StartTransport(TyphonRuntime runtime)
    {
        var transport = new CapturingTransport();
        runtime.StartSubscriptionTransport(transport);

        Assert.That(transport.Starts, Is.EqualTo(1), "a transport is started exactly once, with the acceptor");
        Assert.That(transport.Acceptor, Is.Not.Null);
        return transport.Acceptor;
    }

    /// <summary>Accepts one link through the acceptor, wiring the link's close-back as a real transport does.</summary>
    private static SubscriptionConnection Connect(ISubscriptionAcceptor acceptor, InProcessLink link)
    {
        var info = new LinkInfo { Transport = "fake", SubProtocol = ProtocolConstants.WebSocketSubprotocol };
        var connection = (SubscriptionConnection)acceptor.Accept(link, info);

        Assert.That(connection, Is.Not.Null, "a live runtime refused a connection");
        link.Connection = connection;
        return connection;
    }

    /// <summary>Waits until the runtime has ticked at least <paramref name="count"/> times past where it is now.</summary>
    private static void WaitForTicks(TyphonRuntime runtime, int count)
    {
        var target = runtime.CurrentTickNumber + count;
        Assert.That(SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= target, TimeSpan.FromSeconds(5)), Is.True, "the runtime did not tick");
    }

    // ── the end-to-end case § 5 step 1 promises ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// HELLO → WELCOME with the real catalog → PING → PONG from the running tick → BYE, against a runtime that is ticking.
    /// </summary>
    [Test]
    public void AClientCompletesTheHandshakeAgainstALiveRuntime()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var subscriptions = SubscriptionsOf(runtime);
        var acceptor = StartTransport(runtime);
        WaitForTicks(runtime, 2);

        var link = new InProcessLink();
        var connection = Connect(acceptor, link);

        // The band is read around the whole exchange, and its lower bound is one tick wide on purpose: the scheduler increments its tick number as the LAST
        // statement of a tick and the next tick's start is what publishes it, so between the two — most of the inter-tick gap — the scheduler already names
        // N+1 while the last tick that actually ran is N. A client is told the tick that happened, which is the honest answer, not the one about to.
        var beforeWelcome = (uint)runtime.CurrentTickNumber - 1;
        connection.OnMessage(ClientMessages.Hello("god", Capabilities.Stats));
        var welcome = WelcomeMessage.Parse(link.Take());
        var afterWelcome = (uint)runtime.CurrentTickNumber;

        // A row is Admitted until a tick promotes it, which is the table's own recycling invariant: its Opened event has to be delivered first. Nothing in
        // the engine calls BeginTick yet — the stage that will is P1-12 — so the test stands in for the tick here, exactly as HandshakeTests does.
        var live = subscriptions.Sessions.IsLive(connection.Session);
        subscriptions.Sessions.BeginTick();

        Assert.Multiple(() =>
        {
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Open));
            Assert.That(welcome.SessionId, Is.EqualTo(connection.Session.Value));
            Assert.That(live, Is.True, "the session is a row in the LIVE table, not one the fixture made");
            Assert.That(subscriptions.Sessions.IsOpen(connection.Session), Is.True);
            Assert.That(subscriptions.Sessions.OpenCount, Is.EqualTo(1));
            Assert.That(welcome.TickPeriodUs, Is.EqualTo(TickPeriodUs), "the period the tick published, which is the one the catalog declares");
            Assert.That(welcome.Tick, Is.InRange(beforeWelcome, afterWelcome), "WELCOME names the tick the server was on when it answered");
            Assert.That(welcome.CapsGranted, Is.EqualTo(Capabilities.Stats), "the catalog carries the built-in metrics, so STATS is grantable");
        });

        // PING → PONG, under the same band rule.
        var beforePing = (uint)runtime.CurrentTickNumber - 1;
        connection.OnMessage(ClientMessages.Ping(0x0BADF00D, welcome.Tick));
        var pong = PongMessage.Parse(link.Take());
        var afterPing = (uint)runtime.CurrentTickNumber;

        Assert.Multiple(() =>
        {
            Assert.That(pong.ClientMs, Is.EqualTo(0x0BADF00Du), "opaque to the server, which echoes it");
            Assert.That(pong.Tick, Is.InRange(beforePing, afterPing));
            Assert.That(pong.Tick, Is.GreaterThan(0u), "a PONG carrying tick zero would mean nothing published the tick at all");
            Assert.That(pong.UsIntoTick, Is.LessThan(TickPeriodUs * 100), "measured from this tick's origin, not from the process's");
            Assert.That(connection.LastAppliedTick, Is.EqualTo(welcome.Tick), "what the lag skip is computed from");
        });

        // BYE — a clean leave: no KICK, code 1000, and the tick is asked to close the row.
        connection.OnMessage(ClientMessages.Bye());
        var closed = subscriptions.Sessions.ApplyPendingCloses();

        Assert.Multiple(() =>
        {
            Assert.That(link.PendingCount, Is.Zero, "answering a goodbye with a KICK races the client's own close");
            Assert.That(link.CloseCode, Is.EqualTo(CloseCodes.Normal));
            Assert.That(closed, Is.EqualTo(1));
            Assert.That(subscriptions.Sessions.IsOpen(connection.Session), Is.False);
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Closed));
        });

        runtime.Shutdown();
    }

    /// <summary>
    /// The catalog in <c>WELCOME</c> is the runtime's own export, byte for byte — and a client can parse it.
    /// </summary>
    /// <remarks>
    /// The point of the byte comparison rather than a structural one: the export is hashed, the hash is what a reconnecting client echoes, and a
    /// <c>WELCOME</c> carrying re-serialized bytes would hand a client a catalog whose digest does not match the one beside it.
    /// </remarks>
    [Test]
    public void WelcomeCarriesTheCatalogBuiltFromTheRealPlan()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var export = SubscriptionsOf(runtime).Catalog;
        var acceptor = StartTransport(runtime);

        var link = new InProcessLink();
        var connection = Connect(acceptor, link);
        connection.OnMessage(ClientMessages.Hello("god"));

        var welcome = WelcomeMessage.Parse(link.Take());
        var decoded = CatalogSerializer.FromUtf8(welcome.CatalogJson);

        Assert.Multiple(() =>
        {
            Assert.That(welcome.CatalogJson, Is.EqualTo(export.Utf8), "the bytes a client receives are the ones Start emitted");
            Assert.That(welcome.CatalogHash, Is.EqualTo(export.Hash));
            Assert.That(welcome.CatalogHash, Is.EqualTo(CatalogSerializer.HashBytes(welcome.CatalogJson)), "the digest describes the bytes beside it");
            Assert.That(connection.CatalogSent, Is.True);
            Assert.That(decoded.Archetypes, Has.Length.EqualTo(SubscriptionsOf(runtime).Plans.Length), "built from the compiled plan, not from a model");
            Assert.That(decoded.Tick.PeriodUs, Is.EqualTo((int)TickPeriodUs));
        });

        connection.Dispose();
        runtime.Shutdown();
    }

    /// <summary>
    /// A second client presenting the hash it already holds is sent a <c>WELCOME</c> with the catalog omitted — the reconnect saving of 03 § 10.
    /// </summary>
    [Test]
    public void ASecondClientPresentingTheMatchingHashIsSentNoCatalog()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var export = SubscriptionsOf(runtime).Catalog;
        var acceptor = StartTransport(runtime);

        var firstLink = new InProcessLink();
        var first = Connect(acceptor, firstLink);
        first.OnMessage(ClientMessages.Hello("god"));
        var carried = WelcomeMessage.Parse(firstLink.Take());

        // Exactly what an SDK does on reconnect: echo the hash it was given, having computed nothing itself (archive/Subscriptions/foundation/06 § 2).
        var secondLink = new InProcessLink();
        var second = Connect(acceptor, secondLink);
        second.OnMessage(ClientMessages.Hello("god", clientCatalogHash: carried.CatalogHash));
        var skipped = WelcomeMessage.Parse(secondLink.Take());

        // Both rows are Admitted until a tick delivers their Opened events; P1-12's stage is what will call this from the tick.
        SubscriptionsOf(runtime).Sessions.BeginTick();

        Assert.Multiple(() =>
        {
            Assert.That(carried.CatalogJson, Is.EqualTo(export.Utf8));
            Assert.That(skipped.CatalogJson, Is.Empty, "the client already holds these bytes");
            Assert.That(second.CatalogSent, Is.False);
            Assert.That(skipped.CatalogHash, Is.EqualTo(export.Hash), "the hash travels either way — it is what the client echoes next time");
            Assert.That(SubscriptionsOf(runtime).Sessions.OpenCount, Is.EqualTo(2), "both are real sessions; only the bytes differ");
        });

        first.Dispose();
        second.Dispose();
        runtime.Shutdown();
    }

    // ── the tick state the connection reads ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The three values a transport thread reads are published by the tick and advance with it: the number, the period, and an origin that resets every tick.
    /// </summary>
    /// <remarks>
    /// The <c>usIntoTick</c> half is asserted as a MINIMUM over several observed tick boundaries rather than as a single reading. A single one can be late by
    /// whatever the sampling thread was descheduled for; a minimum over boundaries cannot be small unless the origin genuinely moved with the tick, which is
    /// the property under test — a published constant, or a timestamp taken once at <c>Start</c>, both fail it.
    /// </remarks>
    [Test]
    public void TheTickPublishesItsNumberPeriodAndOrigin()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var host = (ISubscriptionsHost)SubscriptionsOf(runtime);
        WaitForTicks(runtime, 2);

        var minIntoTick = uint.MaxValue;
        var ticksSeen = 0;
        var previousTick = host.CurrentTick;
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;

        while (Stopwatch.GetTimestamp() < deadline && ticksSeen < 4)
        {
            var tick = host.CurrentTick;
            var intoTick = host.MicrosecondsIntoTick;
            if (tick != previousTick)
            {
                previousTick = tick;
                ticksSeen++;
                minIntoTick = Math.Min(minIntoTick, intoTick);
            }
        }

        var finalTick = host.CurrentTick;
        runtime.Shutdown();

        Assert.Multiple(() =>
        {
            Assert.That(ticksSeen, Is.GreaterThanOrEqualTo(2), "the runtime did not tick often enough to observe a boundary");
            Assert.That(finalTick, Is.GreaterThan(0u), "the tick number reached the transport side");
            Assert.That((long)finalTick, Is.LessThanOrEqualTo(runtime.CurrentTickNumber), "a published tick can lag the scheduler's, never lead it");
            Assert.That(host.TickPeriodUs, Is.EqualTo(TickPeriodUs), "the period at multiplier 1 is the nominal one");
            Assert.That(minIntoTick, Is.LessThan(TickPeriodUs), "the origin is re-published every tick, so just after a boundary the offset is small");
        });
    }

    // ── nothing declared: it starts, it accepts nothing, it costs nothing ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A runtime whose application declared no subscriptions starts and ticks normally, refuses to bind a transport, and publishes no tick state.
    /// </summary>
    /// <remarks>
    /// The refusal is loud rather than silent because the alternative is a listener that accepts connections and closes every one of them — which reads to an
    /// operator as a network fault rather than as the missing declaration it is.
    /// </remarks>
    [Test]
    public void ARuntimeWithNoSubscriptionsDeclaredStartsAcceptsNothingAndCostsNothing()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateRuntime(dbe);
        runtime.Start();

        var subscriptions = SubscriptionsOf(runtime);
        var transport = new CapturingTransport();
        var refusal = Assert.Throws<InvalidOperationException>(() => runtime.StartSubscriptionTransport(transport));

        WaitForTicks(runtime, 3);

        var host = (ISubscriptionsHost)subscriptions;
        var chunks = runtime.SubscriptionsContextForTest.ChunksExecuted;
        runtime.Shutdown();

        Assert.Multiple(() =>
        {
            Assert.That(refusal.Message, Does.Contain("declares no subscriptions"));
            Assert.That(transport.Starts, Is.Zero, "a transport that was never started has no socket to close");
            Assert.That(runtime.SubscriptionAcceptor, Is.Null);
            Assert.That(subscriptions.IsAccepting, Is.False);
            Assert.That(subscriptions.Acceptor, Is.Null);
            Assert.That(host.SessionTable, Is.Null, "no session table, which is half a megabyte of native memory at the default MaxSessions");
            Assert.That(host.CatalogJson, Is.Null);
            Assert.That(host.CurrentTick, Is.Zero, "the tick publishes nothing for a subsystem nobody asked for");
            Assert.That(host.MicrosecondsIntoTick, Is.Zero);
            Assert.That(runtime.CurrentTickNumber, Is.GreaterThan(2), "and the runtime itself ticked perfectly well");
            Assert.That(chunks, Is.Zero);
        });
    }

    /// <summary>A transport cannot be bound before <c>Start</c>, because the catalog it would negotiate against is compiled there.</summary>
    [Test]
    public void ATransportCannotBeStartedBeforeTheRuntimeIs()
    {
        using var dbe = SetupEngine();
        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);

        var transport = new CapturingTransport();
        var refusal = Assert.Throws<InvalidOperationException>(() => runtime.StartSubscriptionTransport(transport));

        Assert.Multiple(() =>
        {
            Assert.That(refusal.Message, Does.Contain("after TyphonRuntime.Start()"));
            Assert.That(runtime.SubscriptionAcceptor, Is.Null);
            Assert.That(transport.Starts, Is.Zero);
        });
    }

    /// <summary>
    /// An acceptor a transport is still holding when the runtime goes down declines every connection instead of reaching a disposed session table.
    /// </summary>
    /// <remarks>
    /// A real listener does not stop the instant a host disposes its runtime — the two race by construction — and
    /// <see cref="ISubscriptionAcceptor.Accept"/>'s contract already names this case: <see langword="null"/> when "replication is not running, or the runtime
    /// is stopping". Without the check it is a <see cref="NullReferenceException"/> on a network thread.
    /// </remarks>
    [Test]
    public void AnAcceptorOutlivingItsRuntimeDeclinesEveryConnection()
    {
        using var dbe = SetupEngine();
        var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var transport = new CapturingTransport();
        runtime.StartSubscriptionTransport(transport);
        var acceptor = transport.Acceptor;

        var link = new InProcessLink();
        var connection = Connect(acceptor, link);
        connection.OnMessage(ClientMessages.Hello("god"));
        link.Take();

        runtime.Shutdown();
        runtime.Dispose();

        var lateLink = new InProcessLink();
        var declined = acceptor.Accept(lateLink, new LinkInfo { Transport = "fake" });

        Assert.Multiple(() =>
        {
            Assert.That(declined, Is.Null, "the link is the transport's to close: nothing was admitted, so there is nothing to tell the client");
            Assert.That(lateLink.PendingCount, Is.Zero);
            Assert.That(SubscriptionsOf(runtime).IsAccepting, Is.False);
            Assert.That(transport.Stopped, Is.False,
                "and the runtime did NOT stop the listener: draining a transport is asynchronous, so who awaits it is P1-07 / P1-08's decision, not this " +
                "slice's. What the runtime owns meanwhile is refusing to admit anyone through it");
        });

        connection.Dispose();
    }

    /// <summary>
    /// A transport that owns no bytes: it records the acceptor the runtime hands it, which is the whole of what the seam obliges it to do here.
    /// </summary>
    private sealed class CapturingTransport : ISubscriptionTransport
    {
        public ISubscriptionAcceptor Acceptor { get; private set; }

        public int Starts { get; private set; }

        public bool Stopped { get; private set; }

        public void Start(ISubscriptionAcceptor acceptor)
        {
            Acceptor = acceptor;
            Starts++;
        }

        public ValueTask StopAsync()
        {
            Stopped = true;
            return ValueTask.CompletedTask;
        }
    }
}
