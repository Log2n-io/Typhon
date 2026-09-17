using NUnit.Framework;
using System;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-06 — the connection state machine: what a client may send, when, and the close code for everything else.
/// </summary>
/// <remarks>
/// <para>
/// Every case here is a close code the SDKs branch on (<c>design/Subscriptions/03-wire-protocol.md § 3</c>): 1002 and 1007 tell a client never to reconnect,
/// 1009 the same, 4002 and 1013 tell it to come back with backoff, 4003 not to. Getting one wrong does not fail anywhere — it silently changes whether a fleet
/// of clients retries a server that is refusing them, which is why each is asserted against the constant AND the number.
/// </para>
/// <para>
/// The link is <see cref="InProcessLink"/> with no delay, so a send completes synchronously and a message is queued before the close that follows it. That is
/// what lets these tests read the <c>KICK</c> the engine sent rather than only the code it closed with.
/// </para>
/// </remarks>
[TestFixture]
class HandshakeTests
{
    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;
    private SubscriptionsSessions _sessions;
    private SessionTable _table;
    private FakeSubscriptionsHost _host;
    private InProcessLink _link;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "HandshakeTests" });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "HandshakeTestAllocator" });
        _sessions = new SubscriptionsSessions();
        _table = new SessionTable("Sessions", _registry.Runtime, _allocator, new SubscriptionsOptions { MaxSessions = 4 }, _sessions.SessionEvents);
        _host = new FakeSubscriptionsHost { Sessions = _sessions, SessionTable = _table };
        _link = new InProcessLink();
    }

    [TearDown]
    public void TearDown()
    {
        _table?.Dispose();
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    /// <summary>Accepts a connection over <see cref="_link"/>, with the <c>HELLO</c> deadline under the test's control rather than a clock's.</summary>
    /// <returns>The connection.</returns>
    private SubscriptionConnection Accept()
    {
        var info = new LinkInfo { Transport = "fake", SubProtocol = ProtocolConstants.WebSocketSubprotocol };
        var connection = new SubscriptionConnection(_host, _link, info, Timeout.InfiniteTimeSpan);
        _link.Connection = connection;
        return connection;
    }

    private static KickMessage Kick(byte[] message) => KickMessage.Parse(message);

    // ── the HELLO deadline ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void NoHelloWithinTheDeadlineClosesWith4002()
    {
        var connection = Accept();

        connection.OnHelloDeadline();

        var kick = Kick(_link.Take());

        Assert.Multiple(() =>
        {
            Assert.That(kick.Code, Is.EqualTo(CloseCodes.HelloTimeout));
            Assert.That(kick.Code, Is.EqualTo(4002));
            Assert.That(_link.CloseCode, Is.EqualTo(4002));
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Closed));
            Assert.That(_table.FreeCount, Is.EqualTo(4), "a client that never said HELLO never took a slot");
        });
    }

    [Test]
    public void TheDeadlineIsTheProtocolsFiveSecondsAndItIsArmedOnAccept()
    {
        var fired = new ManualResetEventSlim(false);
        var link = new ClosingLink(fired);
        using var connection = new SubscriptionConnection(_host, link, new LinkInfo { Transport = "fake" }, TimeSpan.FromMilliseconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(fired.Wait(TimeSpan.FromSeconds(5)), Is.True, "the connection arms its own deadline; no transport has to run one");
            Assert.That(link.Code, Is.EqualTo(CloseCodes.HelloTimeout));
            Assert.That(ProtocolConstants.HelloTimeoutMs, Is.EqualTo(5000), "and the production value is the protocol's, not a number this test chose");
        });
    }

    [Test]
    public void ADeadlineAfterTheHandshakeIsANoOp()
    {
        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello());
        _link.Take();

        connection.OnHelloDeadline();

        Assert.Multiple(() =>
        {
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Open));
            Assert.That(_link.IsClosed, Is.False);
        });
    }

    // ── admission ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AnUnknownKindClosesWith4003AndTheHookNeverRuns()
    {
        var hookRan = false;
        _sessions.Kinds("god", "player");
        _sessions.Admit = (in AdmissionRequest _) =>
        {
            hookRan = true;
            return Admission.Accept(SessionRole.Player);
        };

        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello("wookiee"));

        var kick = Kick(_link.Take());

        Assert.Multiple(() =>
        {
            Assert.That(hookRan, Is.False, "an application hook must never see a name the application did not declare");
            Assert.That(kick.Code, Is.EqualTo(CloseCodes.AuthenticationRejected));
            Assert.That(kick.Code, Is.EqualTo(4003));
            Assert.That(_link.CloseCode, Is.EqualTo(4003));
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Closed));
            Assert.That(connection.Session, Is.EqualTo(SessionId.None));
            Assert.That(_table.FreeCount, Is.EqualTo(4), "and a refusal costs no slot");
        });
    }

    [Test]
    public void AHookRefusalCarriesItsOwnApplicationCode()
    {
        _sessions.Admit = (in AdmissionRequest _) => Admission.Reject(4321, "banned");

        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello());

        var kick = Kick(_link.Take());

        Assert.Multiple(() =>
        {
            Assert.That(kick.Code, Is.EqualTo(4321));
            Assert.That(kick.Reason, Is.EqualTo("banned"));
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Closed));
        });
    }

    [Test]
    public void AFullSessionTableClosesWith1013()
    {
        var live = new SubscriptionConnection[4];
        for (var i = 0; i < live.Length; i++)
        {
            var link = new InProcessLink();
            live[i] = new SubscriptionConnection(_host, link, new LinkInfo { Transport = "fake" }, Timeout.InfiniteTimeSpan);
            live[i].OnMessage(ClientMessages.Hello());
            Assert.That(live[i].State, Is.EqualTo(SubscriptionConnectionState.Open));
        }

        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello());

        var kick = Kick(_link.Take());

        Assert.Multiple(() =>
        {
            Assert.That(kick.Code, Is.EqualTo(CloseCodes.TryAgainLater));
            Assert.That(kick.Code, Is.EqualTo(1013), "'how many fit' is the operator's answer, not the application's");
        });

        foreach (var open in live)
        {
            open.Dispose();
        }
    }

    // ── message caps, checked before decoding ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AMessageAboveTheHelloCapClosesWith1009BeforeItIsDecoded()
    {
        var connection = Accept();

        // A body that is not a HELLO at all: were the cap checked after decoding, this would answer 1007 or 1002 instead, which tells an SDK something else.
        var oversized = new byte[ProtocolConstants.HelloMaxBytes + 1];
        oversized[0] = MessageTypes.Hello;

        connection.OnMessage(oversized);

        var kick = Kick(_link.Take());

        Assert.Multiple(() =>
        {
            Assert.That(kick.Code, Is.EqualTo(CloseCodes.MessageTooBig));
            Assert.That(kick.Code, Is.EqualTo(1009));
            Assert.That(_table.FreeCount, Is.EqualTo(4), "nothing was decoded, so nothing was admitted");
        });
    }

    [Test]
    public void AfterTheHandshakeTheCapIsTheSessionsClientMessageBytes()
    {
        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello());
        _link.Take();

        var limit = _table.Row(connection.Session).ClientMessageBytes;

        Assert.That(limit, Is.EqualTo(1024), "the operator's default, exported in the catalog so an SDK batches under it");

        connection.OnMessage(ClientMessages.Commands(limit + 1));

        var kick = Kick(_link.Take());

        Assert.Multiple(() =>
        {
            Assert.That(kick.Code, Is.EqualTo(CloseCodes.MessageTooBig));
            Assert.That(_host.CommandMessages, Is.Zero, "the body never reached ingress");
        });
    }

    [Test]
    public void AMessageInsideTheCapReachesIngress()
    {
        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello());
        _link.Take();

        connection.OnMessage(ClientMessages.Commands(64));

        Assert.Multiple(() =>
        {
            Assert.That(_host.CommandMessages, Is.EqualTo(1));
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Open), "a well-formed, in-state message is not a reason to disconnect");
        });
    }

    // ── out of state ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void APingBeforeHelloClosesWith1002()
    {
        var connection = Accept();

        connection.OnMessage(ClientMessages.Ping(7, 0));

        var kick = Kick(_link.Take());

        Assert.Multiple(() =>
        {
            Assert.That(kick.Code, Is.EqualTo(CloseCodes.ProtocolError));
            Assert.That(kick.Code, Is.EqualTo(1002));
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Closed));
        });
    }

    [Test]
    public void ASecondHelloClosesWith1002()
    {
        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello());
        _link.Take();

        connection.OnMessage(ClientMessages.Hello());

        Assert.That(Kick(_link.Take()).Code, Is.EqualTo(CloseCodes.ProtocolError), "a known type in the wrong state is 1002, exactly like an unknown one");
    }

    [Test]
    public void AnUnknownMessageTypeClosesWith1002()
    {
        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello());
        _link.Take();

        connection.OnMessage([0x7F, 0x00]);

        Assert.That(Kick(_link.Take()).Code, Is.EqualTo(CloseCodes.ProtocolError));
    }

    [Test]
    public void AMalformedHelloBodyClosesWith1007()
    {
        var connection = Accept();

        // The type byte is right and the message is inside its cap; the body simply runs out, which is a payload fault, not a framing one.
        connection.OnMessage([MessageTypes.Hello, 0x02, 0x00]);

        var kick = Kick(_link.Take());

        Assert.Multiple(() =>
        {
            Assert.That(kick.Code, Is.EqualTo(CloseCodes.MalformedPayload));
            Assert.That(kick.Code, Is.EqualTo(1007));
        });
    }

    [Test]
    public void AProtocolMajorMismatchClosesWithoutAKick()
    {
        var connection = Accept();

        connection.OnMessage(ClientMessages.Hello(major: ProtocolConstants.Major + 1));

        Assert.Multiple(() =>
        {
            Assert.That(_link.PendingCount, Is.Zero,
                "a KICK would oblige every future major to speak major 2's framing; the subprotocol and the preamble are where a major is settled");
            Assert.That(_link.CloseCode, Is.EqualTo(CloseCodes.ProtocolError));
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Closed));
        });
    }

    // ── WELCOME ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void WelcomeCarriesTheSessionTheTickAndThePeriod()
    {
        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello());

        var welcome = WelcomeMessage.Parse(_link.Take());

        Assert.Multiple(() =>
        {
            Assert.That(welcome.Major, Is.EqualTo(ProtocolConstants.Major));
            Assert.That(welcome.Minor, Is.EqualTo(ProtocolConstants.Minor), "the lower minor of the two sides wins");
            Assert.That(welcome.SessionId, Is.EqualTo(connection.Session.Value));
            Assert.That(welcome.Tick, Is.EqualTo(_host.CurrentTick));
            Assert.That(welcome.TickPeriodUs, Is.EqualTo(_host.TickPeriodUs));
            Assert.That(welcome.CatalogHash, Is.EqualTo(_host.CatalogHash));
            Assert.That(welcome.ResumeToken, Is.All.Zero, "resume is not built yet, and all zeros is how the wire says so");
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Open));
        });
    }

    [Test]
    public void AMatchingCatalogHashOmitsTheCatalogAndAMismatchingOneIncludesIt()
    {
        var matching = Accept();
        matching.OnMessage(ClientMessages.Hello(clientCatalogHash: _host.CatalogHash));
        var skipped = WelcomeMessage.Parse(_link.Take());

        var otherLink = new InProcessLink();
        using var mismatching = new SubscriptionConnection(_host, otherLink, new LinkInfo { Transport = "fake" }, Timeout.InfiniteTimeSpan);
        mismatching.OnMessage(ClientMessages.Hello(clientCatalogHash: _host.CatalogHash ^ 1));
        var carried = WelcomeMessage.Parse(otherLink.Take());

        Assert.Multiple(() =>
        {
            Assert.That(skipped.CatalogJson, Is.Empty, "≈ 5 KB saved per reconnect, and 25 MB across a 5 000-bot churn storm");
            Assert.That(matching.CatalogSent, Is.False);
            Assert.That(carried.CatalogJson, Is.EqualTo(_host.CatalogJson));
            Assert.That(mismatching.CatalogSent, Is.True);
            Assert.That(skipped.CatalogHash, Is.EqualTo(carried.CatalogHash), "the hash travels either way — it is what the client echoes next time");
        });
    }

    [Test]
    public void ACatalogThatDigestsToZeroIsAlwaysSent()
    {
        _host.CatalogHash = 0;

        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello(clientCatalogHash: 0));

        var welcome = WelcomeMessage.Parse(_link.Take());

        Assert.Multiple(() =>
        {
            Assert.That(welcome.CatalogJson, Is.EqualTo(_host.CatalogJson),
                "zero is also what a client with no catalog presents, so the two would otherwise be indistinguishable");
            Assert.That(connection.CatalogSent, Is.True);
        });
    }

    // ── capabilities ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void CapsGrantedAreASubsetOfRequestedKnownAndAuthorized()
    {
        _sessions.Admit = (in AdmissionRequest _) => Admission.Accept(SessionRole.Spectator, SessionLimits.God);
        _host.HasMetrics = true;

        var connection = Accept();

        // Every known bit, plus a reserved one the client has no business asking for.
        var requested = Capabilities.Stats | Capabilities.Debug | (Capabilities)0x8000_0000;
        connection.OnMessage(ClientMessages.Hello(caps: requested));

        var welcome = WelcomeMessage.Parse(_link.Take());

        Assert.Multiple(() =>
        {
            Assert.That(welcome.CapsGranted, Is.EqualTo(Capabilities.Stats | Capabilities.Debug));
            Assert.That(welcome.CapsGranted & ~requested, Is.EqualTo(Capabilities.None),
                "a granted bit the client did not request is a server bug it closes with 1002");
            Assert.That(connection.CapsGranted, Is.EqualTo(welcome.CapsGranted));
        });
    }

    [Test]
    public void AnUnrequestedCapabilityIsNeverGranted()
    {
        _sessions.Admit = (in AdmissionRequest _) => Admission.Accept(SessionRole.Spectator, SessionLimits.God);

        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello(caps: Capabilities.Stats));

        Assert.That(WelcomeMessage.Parse(_link.Take()).CapsGranted, Is.EqualTo(Capabilities.Stats));
        Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Open));
    }

    [Test]
    public void StatsIsRefusedWithoutMetricsAndDebugWithoutAuthorization()
    {
        _host.HasMetrics = false;

        // The default limits carry AllowDebug = false, so neither bit is authorized.
        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello(caps: Capabilities.Stats | Capabilities.Debug));

        Assert.Multiple(() =>
        {
            Assert.That(WelcomeMessage.Parse(_link.Take()).CapsGranted, Is.EqualTo(Capabilities.None));
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Open), "a capability refused is not a connection refused");
        });
    }

    // ── the open session ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void PingIsAnsweredWithPongAndRecordsTheClientsAppliedTick()
    {
        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello());
        _link.Take();

        connection.OnMessage(ClientMessages.Ping(0xDEADBEEF, 1200));

        var pong = PongMessage.Parse(_link.Take());

        Assert.Multiple(() =>
        {
            Assert.That(pong.ClientMs, Is.EqualTo(0xDEADBEEF), "opaque to the server, which echoes it: the client picks the epoch");
            Assert.That(pong.Tick, Is.EqualTo(_host.CurrentTick));
            Assert.That(pong.UsIntoTick, Is.EqualTo(99_999u), "a u32, because a 10 Hz tick is 100 000 µs and more under dilation");
            Assert.That(connection.LastAppliedTick, Is.EqualTo(1200u), "this is what the lag skip is computed from");
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Open));
        });
    }

    [Test]
    public void ByeIsACleanCloseAndAsksTheTickToCloseTheSession()
    {
        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello());
        _link.Take();
        var session = connection.Session;

        // The Opened event has to be delivered before the row is one the tick iterates, which is the table's own recycling invariant.
        _table.BeginTick();

        connection.OnMessage(ClientMessages.Bye());

        var closed = _table.ApplyPendingCloses();

        Assert.Multiple(() =>
        {
            Assert.That(_link.PendingCount, Is.Zero, "answering a goodbye with a KICK races the client's own close");
            Assert.That(_link.CloseCode, Is.EqualTo(CloseCodes.Normal));
            Assert.That(connection.ClientByeCode, Is.EqualTo(CloseCodes.Normal));
            Assert.That(closed, Is.EqualTo(1));
            Assert.That(_table.IsOpen(session), Is.False);
        });
    }

    [Test]
    public void AClientThatRefusedTheStreamArrivesAsBye4004AndIsStillACleanClose()
    {
        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello());
        _link.Take();

        // A browser may not send 1002, 1007 or 1009 — they are reserved for an endpoint's own use — so it says 4004 and closes with 1000 (03 § 10).
        connection.OnMessage(ClientMessages.Bye(CloseCodes.ClientRefusedTheStream));

        Assert.Multiple(() =>
        {
            Assert.That(connection.ClientByeCode, Is.EqualTo(4004));
            Assert.That(connection.ClientByeCode, Is.EqualTo(CloseCodes.ClientRefusedTheStream));
            Assert.That(_link.PendingCount, Is.Zero, "still a goodbye: the code says why it had to leave, not that the engine did anything wrong");
            Assert.That(_link.CloseCode, Is.EqualTo(CloseCodes.Normal));
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Closed));
        });
    }

    [Test]
    public void AByeCodeOutsideTheClientRangeClosesWith1007()
    {
        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello());
        _link.Take();

        connection.OnMessage([MessageTypes.Bye, 0xEA, 0x03]);

        Assert.That(Kick(_link.Take()).Code, Is.EqualTo(CloseCodes.MalformedPayload), "1002 is the endpoint's own code; a client may not claim it in BYE");
    }

    [Test]
    public void TheLinkEndingClosesTheSessionExactlyOnce()
    {
        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello());
        _link.Take();
        var session = connection.Session;
        _table.BeginTick();

        connection.OnClosed(CloseCodes.GoingAway, new InvalidOperationException("socket reset"));
        connection.OnClosed(CloseCodes.GoingAway, null);

        var closed = _table.ApplyPendingCloses();

        Assert.Multiple(() =>
        {
            Assert.That(closed, Is.EqualTo(1), "a second OnClosed must not queue a second close");
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Closed));
            Assert.That(connection.CloseCode, Is.EqualTo(CloseCodes.GoingAway));
            Assert.That(_table.IsLive(session), Is.True, "the row survives until its Closed event has been delivered");
            Assert.That(_table.IsOpen(session), Is.False);
        });
    }

    [Test]
    public void MessagesAfterACloseAreIgnored()
    {
        var connection = Accept();
        connection.OnMessage(ClientMessages.Hello());
        _link.Take();
        connection.OnMessage(ClientMessages.Bye());

        connection.OnMessage(ClientMessages.Ping(1, 1));

        Assert.Multiple(() =>
        {
            Assert.That(_link.PendingCount, Is.Zero);
            Assert.That(_link.CloseCount, Is.EqualTo(1));
        });
    }

    /// <summary>A link that only records the close, for the one case that has to observe a real timer firing.</summary>
    private sealed class ClosingLink : ISubscriptionLink
    {
        private readonly ManualResetEventSlim _closed;

        public ClosingLink(ManualResetEventSlim closed) => _closed = closed;

        public ushort Code { get; private set; }

        public bool SupportsUnreliable => false;

        public System.Threading.Tasks.ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
            => System.Threading.Tasks.ValueTask.CompletedTask;

        public bool TrySendUnreliable(ReadOnlySpan<byte> datagram) => false;

        public void Close(ushort code, string reason)
        {
            Code = code;
            _closed.Set();
        }
    }
}
