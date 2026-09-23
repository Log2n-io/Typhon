using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-07 — the built-in TCP transport: the <c>TYP2</c> preamble, <c>u32 len</c> framing, and the seam's promises held over a real socket.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every case here drives a real client socket.</b> A fake that calls <see cref="ISubscriptionConnection.OnMessage"/> with whole messages tests nothing this
/// slice adds: the framing, the order the preamble is exchanged in, and what is decided before a body is read are exactly the parts that only exist once bytes
/// travel. Ports are ephemeral, every wait has a deadline, and nothing sleeps.
/// </para>
/// <para>
/// <b>Messages are built through <c>Typhon.Protocol</c>'s own writers</b> (<see cref="ClientMessages"/>), never spelled out as byte arrays. The one thing this
/// fixture does write by hand is the length prefix — which is the framing under test, and a test that took it from the implementation would agree with it
/// whatever it said.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class TcpTransportTests : TestBase<TcpTransportTests>
{
    /// <summary>
    /// Every socket wait in this fixture. Long enough that a loaded machine does not fail it, short enough that a hang is a failure and not a hang.
    /// </summary>
    private const int TimeoutMs = 5000;

    private readonly List<IDisposable> _disposables = [];
    private TcpSubscriptionTransport _transport;

    [TearDown]
    public void TearDownTransport()
    {
        _transport?.StopAsync().AsTask().Wait(TimeoutMs);
        _transport = null;

        for (var i = _disposables.Count - 1; i >= 0; i--)
        {
            _disposables[i].Dispose();
        }

        _disposables.Clear();
    }

    // ── the preamble ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A client whose major is not this one gets this server's preamble and then a close — never a <c>KICK</c>, and never a session.
    /// </summary>
    /// <remarks>
    /// The reason it cannot be a <c>KICK</c> is not style: a major-N server answering a major-2 client with one would have to speak major 2's <c>KICK</c>
    /// framing forever (03 § 12 W31). Four bytes and a <c>FIN</c> are the whole of the vocabulary two majors are guaranteed to share.
    /// </remarks>
    [Test]
    public void APreambleMismatchIsAnsweredWithTheServersOwnAndThenAClose()
    {
        var acceptor = new RecordingAcceptor();
        var endpoint = StartTransport(acceptor);

        using var client = Connect(endpoint);
        client.Send("TYP1"u8);

        var serverPreamble = client.ReadExactly(4);
        var afterwards = client.ReadWhatever();

        Assert.Multiple(() =>
        {
            Assert.That(serverPreamble, Is.EqualTo(ProtocolConstants.TcpPreamble.ToArray()), "the server writes its own preamble whatever the client wrote");
            Assert.That(afterwards, Is.Empty, "a KICK after a major mismatch would be framed in a major the client may not speak");
            Assert.That(acceptor.Accepts, Is.Zero, "nothing was admitted: the preamble is settled before the engine hears about the connection");
        });
    }

    /// <summary>A client speaking this major is admitted, and the preamble it reads back is the shared constant rather than a literal.</summary>
    [Test]
    public void AMatchingPreambleIsAdmitted()
    {
        var acceptor = new RecordingAcceptor();
        var endpoint = StartTransport(acceptor);

        using var client = Connect(endpoint);
        var connection = Handshake(client, acceptor);

        Assert.Multiple(() =>
        {
            Assert.That(acceptor.Accepts, Is.EqualTo(1));
            Assert.That(connection.Closes, Is.Zero, "a good preamble closes nothing");
            Assert.That(acceptor.LastInfo.Transport, Is.EqualTo("tcp"));
            Assert.That(acceptor.LastInfo.Remote, Is.Not.Null, "an address the client cannot forge is what a transport knows and the protocol does not");
            Assert.That(acceptor.LastInfo.SubProtocol, Is.Null, "TCP has no subprotocol: the preamble is its version check");
        });
    }

    // ── inbound framing ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A declared length of zero is a framing error — 1002 — and no body is waited for.</summary>
    [Test]
    public void AZeroLengthFrameClosesWith1002()
    {
        var acceptor = new RecordingAcceptor();
        var endpoint = StartTransport(acceptor);

        using var client = Connect(endpoint);
        var connection = Handshake(client, acceptor);

        client.SendLength(0);

        Assert.That(connection.WaitForClose(TimeoutMs), Is.True, "the transport did not close a zero-length frame");
        Assert.Multiple(() =>
        {
            Assert.That(connection.CloseCode, Is.EqualTo(CloseCodes.ProtocolError));
            Assert.That(connection.CloseCode, Is.EqualTo(1002));
            Assert.That(connection.Closes, Is.EqualTo(1));
            Assert.That(connection.Messages, Is.Empty, "there was no message: zero is a framing error, not an empty one");
        });
    }

    /// <summary>
    /// A length above the cap closes 1009 <b>before the body is read</b> — which this test proves by never sending one.
    /// </summary>
    /// <remarks>
    /// The check exists because the bytes are the attack. A transport that read the body first to "see what it was" would let a client with a 4-byte prefix
    /// make the server wait on, and allocate for, whatever it named.
    /// </remarks>
    [Test]
    public void ALengthAboveTheCapClosesWith1009BeforeTheBodyIsRead()
    {
        var acceptor = new RecordingAcceptor();
        var endpoint = StartTransport(acceptor);

        using var client = Connect(endpoint);
        var connection = Handshake(client, acceptor);

        // The prefix and nothing else. If the transport waited for the body, this test would time out rather than pass.
        client.SendLength((uint)ProtocolConstants.HelloMaxBytes + 1);

        Assert.That(connection.WaitForClose(TimeoutMs), Is.True, "the transport waited for a body it had already decided to refuse");
        Assert.Multiple(() =>
        {
            Assert.That(connection.CloseCode, Is.EqualTo(CloseCodes.MessageTooBig));
            Assert.That(connection.CloseCode, Is.EqualTo(1009));
            Assert.That(connection.Closes, Is.EqualTo(1));
            Assert.That(connection.Messages, Is.Empty);
        });
    }

    /// <summary>The first message may be a whole <c>HELLO</c>; the ones after it are held to the transport's own ceiling.</summary>
    [Test]
    public void TheFirstMessageIsBoundedByTheHelloLimitAndTheRestByTheTransportsOwn()
    {
        var acceptor = new RecordingAcceptor();
        var endpoint = StartTransport(acceptor, new TcpSubscriptionOptions { MaxInboundMessageBytes = ProtocolConstants.HelloMaxBytes });

        using var client = Connect(endpoint);
        var connection = Handshake(client, acceptor);

        // Exactly the HELLO limit: legal as a first message, and the transport does not have to know it is a HELLO to let it through.
        var atTheLimit = new byte[ProtocolConstants.HelloMaxBytes];
        atTheLimit[0] = MessageTypes.Hello;
        client.SendFramed(atTheLimit);

        Assert.That(connection.WaitForMessages(1, TimeoutMs), Is.True, "a message at the HELLO limit was refused");

        client.SendLength((uint)ProtocolConstants.HelloMaxBytes + 1);

        Assert.That(connection.WaitForClose(TimeoutMs), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(connection.Messages[0], Has.Length.EqualTo(ProtocolConstants.HelloMaxBytes));
            Assert.That(connection.CloseCode, Is.EqualTo(CloseCodes.MessageTooBig));
        });
    }

    /// <summary>
    /// Messages arrive whole, one at a time and in order, whatever the peer's writes looked like.
    /// </summary>
    /// <remarks>
    /// Both halves matter and neither is free on a stream: three messages written in one <c>send</c> must not arrive as one, and one message written in two
    /// must not arrive as two — or a <c>HELLO</c> split by an MTU boundary would be a protocol error instead of a handshake.
    /// </remarks>
    [Test]
    public void MessagesArriveWholeAndInOrderHoweverThePeerWroteThem()
    {
        var acceptor = new RecordingAcceptor();
        var endpoint = StartTransport(acceptor);

        using var client = Connect(endpoint);
        var connection = Handshake(client, acceptor);

        var first = ClientMessages.Ping(1, 10);
        var second = ClientMessages.Ping(2, 20);
        var third = ClientMessages.Bye();

        // Three messages, one write: the boundaries are the prefixes, not the packets.
        var coalesced = new List<byte>();
        foreach (var message in new[] { first, second })
        {
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)message.Length);
            coalesced.AddRange(header);
            coalesced.AddRange(message);
        }

        client.Send(coalesced.ToArray());

        // And one message in two writes, the prefix arriving alone.
        var lastHeader = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lastHeader, (uint)third.Length);
        client.Send(lastHeader);
        client.Send(third);

        Assert.That(connection.WaitForMessages(3, TimeoutMs), Is.True, "the three messages did not arrive");
        Assert.Multiple(() =>
        {
            Assert.That(connection.Messages[0], Is.EqualTo(first));
            Assert.That(connection.Messages[1], Is.EqualTo(second));
            Assert.That(connection.Messages[2], Is.EqualTo(third));
            Assert.That(
                connection.Overlaps,
                Is.Zero,
                "OnMessage was called from two threads at once, which breaks the session's ordering and not only the link's");
        });
    }

    // ── outbound framing ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A frame living in engine-owned native memory reaches the wire with a little-endian prefix that excludes itself — and never touches the heap on the way.
    /// </summary>
    /// <remarks>
    /// The <see cref="NativeFrameMemoryManager"/> is the production path: the frame pool hands the send pump one of these views, and a link that copied it into
    /// a managed array to send it would double the bytes touched per frame for nothing. What the assertion can show from outside is that the bytes arrive
    /// unchanged; what the implementation guarantees is that the same <see cref="ReadOnlyMemory{T}"/> is what the socket was given.
    /// </remarks>
    [Test]
    public void ANativeFrameIsFramedLittleEndianAndSentWithoutACopy()
    {
        var acceptor = new RecordingAcceptor();
        var endpoint = StartTransport(acceptor);

        using var client = Connect(endpoint);
        Handshake(client, acceptor);

        const int frameBytes = 300;
        var buffer = NativeFrameMemoryManager.Allocate(frameBytes);
        try
        {
            var span = buffer.GetSpan();
            for (var i = 0; i < frameBytes; i++)
            {
                span[i] = (byte)(i * 7);
            }

            Assert.That(acceptor.LastLink.SendAsync(buffer.Memory[..frameBytes], CancellationToken.None).AsTask().Wait(TimeoutMs), Is.True, "the send hung");
        }
        finally
        {
            buffer.Release();
        }

        var header = client.ReadExactly(4);
        var body = client.ReadExactly(frameBytes);

        Assert.Multiple(() =>
        {
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(header), Is.EqualTo((uint)frameBytes), "little-endian, and excluding itself");
            Assert.That(body, Has.Length.EqualTo(frameBytes));
            for (var i = 0; i < frameBytes; i++)
            {
                Assert.That(body[i], Is.EqualTo((byte)(i * 7)), $"byte {i} of the frame changed on the way out");
            }
        });
    }

    /// <summary>A message queued before a close reaches the peer ahead of the <c>FIN</c>, which is what makes a <c>KICK</c>'s reason readable.</summary>
    [Test]
    public void AQueuedSendReachesThePeerBeforeTheClose()
    {
        var acceptor = new RecordingAcceptor();
        var endpoint = StartTransport(acceptor);

        using var client = Connect(endpoint);
        var connection = Handshake(client, acceptor);

        var kick = new KickMessage(CloseCodes.TryAgainLater, "come back later");
        var bytes = Encode(kick);
        var buffer = NativeFrameMemoryManager.Allocate(bytes.Length);
        try
        {
            bytes.CopyTo(buffer.GetSpan());
            var send = acceptor.LastLink.SendAsync(buffer.Memory[..bytes.Length], CancellationToken.None);
            acceptor.LastLink.Close(CloseCodes.TryAgainLater, "come back later");
            Assert.That(send.AsTask().Wait(TimeoutMs), Is.True, "the send never completed");
        }
        finally
        {
            buffer.Release();
        }

        var received = client.ReadMessage();
        var afterwards = client.ReadWhatever();

        Assert.That(connection.WaitForClose(TimeoutMs), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(KickMessage.Parse(received).Code, Is.EqualTo(CloseCodes.TryAgainLater), "the reason arrived before the FIN");
            Assert.That(afterwards, Is.Empty);
            Assert.That(connection.Closes, Is.EqualTo(1), "an engine-initiated close is still exactly one OnClosed");
            Assert.That(connection.CloseCode, Is.EqualTo(CloseCodes.TryAgainLater));
        });
    }

    // ── the connection's end ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A peer that vanishes half way through a message is closed exactly once, and the half message is never delivered.</summary>
    [Test]
    public void AClientThatDisappearsMidMessageIsClosedExactlyOnce()
    {
        var acceptor = new RecordingAcceptor();
        var endpoint = StartTransport(acceptor);

        var client = Connect(endpoint);
        var connection = Handshake(client, acceptor);

        client.SendLength(64);
        client.Send(new byte[16]);
        client.Dispose();

        Assert.That(connection.WaitForClose(TimeoutMs), Is.True, "a vanished peer left a receive loop running");
        Assert.Multiple(() =>
        {
            Assert.That(connection.Closes, Is.EqualTo(1), "exactly one, whoever ended it: a session row is released here and nowhere else");
            Assert.That(connection.Messages, Is.Empty, "half a message is not a message");
            Assert.That(connection.CloseError, Is.Not.Null, "a truncated stream is a link loss, not a goodbye");
        });

        // And the report stays at one after the teardown has fully unwound.
        Assert.That(SpinForClosesAbove(connection, 1), Is.False, "OnClosed was delivered twice");
    }

    /// <summary>A peer closing cleanly between messages is a link loss with nothing to report, still reported exactly once.</summary>
    [Test]
    public void APeerClosingBetweenMessagesIsClosedExactlyOnce()
    {
        var acceptor = new RecordingAcceptor();
        var endpoint = StartTransport(acceptor);

        var client = Connect(endpoint);
        var connection = Handshake(client, acceptor);

        client.Dispose();

        Assert.That(connection.WaitForClose(TimeoutMs), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(connection.Closes, Is.EqualTo(1));
            Assert.That(connection.CloseCode, Is.EqualTo(CloseCodes.Normal));
            Assert.That(connection.CloseError, Is.Null, "a FIN between messages is as orderly as TCP gets");
        });
    }

    /// <summary>Stopping the transport closes what is still connected, with the code the seam names, and tells every connection.</summary>
    [Test]
    public void StoppingTheTransportClosesLiveLinksWith1001()
    {
        var acceptor = new RecordingAcceptor();
        var endpoint = StartTransport(acceptor);

        using var client = Connect(endpoint);
        var connection = Handshake(client, acceptor);

        var transport = _transport;
        _transport = null;
        Assert.That(transport.StopAsync().AsTask().Wait(TimeoutMs), Is.True, "StopAsync did not drain");

        Assert.Multiple(() =>
        {
            Assert.That(connection.Closes, Is.EqualTo(1), "StopAsync returned before a connection had been told, which leaks its session row");
            Assert.That(connection.CloseCode, Is.EqualTo(CloseCodes.GoingAway));
            Assert.That(transport.ConnectionCount, Is.Zero);
            Assert.That(client.ReadWhatever(), Is.Empty, "the client sees a FIN, not bytes");
        });
    }

    // ── what it binds ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The listener binds loopback unless an address was named.
    /// </summary>
    /// <remarks>
    /// This transport authenticates nobody, so the default is the one decision that cannot be walked back after a sample has been copied: a listener on every
    /// interface with no authentication is what the previous implementation shipped (04 § 4).
    /// </remarks>
    [Test]
    public void TheListenerBindsLoopbackUnlessConfigured()
    {
        var acceptor = new RecordingAcceptor();
        var bound = StartTransport(acceptor);

        var explicitOptions = new TcpSubscriptionOptions { Address = IPAddress.Any };
        var second = new TcpSubscriptionTransport(explicitOptions);
        _disposables.Add(new TransportHandle(second));
        second.Start(new RecordingAcceptor());

        Assert.Multiple(() =>
        {
            Assert.That(TcpSubscriptionOptions.Default.Address, Is.EqualTo(IPAddress.Loopback), "the default is the documented one");
            Assert.That(bound.Address, Is.EqualTo(IPAddress.Loopback), "a transport built with no options bound beyond the machine");
            Assert.That(bound.Port, Is.GreaterThan(0), "an ephemeral port is resolved and published, which is how a test finds it");
            Assert.That(second.BoundEndPoint.Address, Is.EqualTo(IPAddress.Any), "an explicit address is honoured — it is opt-in, not unreachable");
        });
    }

    /// <summary>A configuration that cannot work is refused where the host can see it, not at the first connection.</summary>
    [Test]
    public void AnInboundCapBelowTheHelloLimitIsRefused()
    {
        var refusal = Assert.Throws<ArgumentException>(() => new TcpSubscriptionTransport(new TcpSubscriptionOptions { MaxInboundMessageBytes = 512 }));

        Assert.That(refusal.Message, Does.Contain("HELLO"));
    }

    // ── against the live engine ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// HELLO → WELCOME → PING → PONG → BYE over a real socket against a ticking <see cref="TyphonRuntime"/>.
    /// </summary>
    /// <remarks>
    /// The same exchange <see cref="LiveHandshakeTests"/> drives through an in-process link, with a length prefix and a kernel in between. What it adds is
    /// that the two halves work when the engine's thread and the transport's are not the same one.
    /// </remarks>
    [Test]
    public void AClientCompletesTheHandshakeOverARealSocket()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var transport = new TcpSubscriptionTransport();
        _transport = transport;
        runtime.StartSubscriptionTransport(transport);

        // A PONG names the tick the driver last published, so a client connecting before the first one would be answered with zero — honestly, and uselessly.
        var target = runtime.CurrentTickNumber + 2;
        Assert.That(SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= target, TimeSpan.FromSeconds(5)), Is.True, "the runtime did not tick");

        using var client = Connect(transport.BoundEndPoint);
        client.Send(ProtocolConstants.TcpPreamble);
        Assert.That(client.ReadExactly(4), Is.EqualTo(ProtocolConstants.TcpPreamble.ToArray()));

        client.SendFramed(ClientMessages.Hello("god", Capabilities.Stats));
        var welcome = WelcomeMessage.Parse(client.ReadMessage());

        client.SendFramed(ClientMessages.Ping(0x0BADF00D, welcome.Tick));
        var pong = PongMessage.Parse(client.ReadMessage());

        client.SendFramed(ClientMessages.Bye());
        var afterBye = client.ReadWhatever();

        Assert.Multiple(() =>
        {
            Assert.That(welcome.Major, Is.EqualTo(ProtocolConstants.Major));
            Assert.That(welcome.SessionId, Is.GreaterThan(0u), "a real row in the runtime's session table");
            Assert.That(welcome.CatalogJson, Is.EqualTo(runtime.SubscriptionsContextForTest.Subscriptions.Catalog.Utf8), "the catalog Start compiled");
            Assert.That(welcome.CapsGranted, Is.EqualTo(Capabilities.Stats));
            Assert.That(pong.ClientMs, Is.EqualTo(0x0BADF00Du));
            Assert.That(pong.Tick, Is.GreaterThan(0u));
            Assert.That(afterBye, Is.Empty, "a goodbye is answered with a close, never with a KICK");
        });

        runtime.Shutdown();
    }

    /// <summary>
    /// AC-15's first half: the same script over a real socket and over an in-process link produces byte-identical protocol messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a fake host rather than a live runtime.</b> A session id, a tick and a microsecond offset are what differ between two connections to one running
    /// server, and normalising them away would leave a comparison that could not fail for the reasons it exists to catch. Two identically configured hosts
    /// admit their first session under the same identity and publish the same tick, so the two byte streams are comparable as bytes — and the thing actually
    /// under test is that the transport contributes nothing to them but a length prefix.
    /// </para>
    /// <para>
    /// <c>Typhon.Client</c> has no TCP client yet (P1-20b), so the bot half is a raw socket driven with messages from <c>Typhon.Protocol</c>'s own writers —
    /// which is what that client will send when it exists.
    /// </para>
    /// </remarks>
    [Test]
    public void ASocketClientAndAnInProcessClientProduceIdenticalProtocolMessages()
    {
        var script = new[]
        {
            ClientMessages.Hello("god", Capabilities.Stats),
            ClientMessages.Ping(0x0BADF00D, 1234),
            ClientMessages.Bye(),
        };

        var inProcess = RunScriptInProcess(script);
        Assert.That(inProcess, Has.Count.GreaterThan(0), "the in-process oracle produced nothing to compare against");
        Assert.That(inProcess[0][0], Is.EqualTo(MessageTypes.Welcome), $"the oracle's first message is 0x{inProcess[0][0]:X2}");

        var overTcp = RunScriptOverTcp(script);

        Assert.That(overTcp, Has.Count.EqualTo(inProcess.Count), "the two arms produced a different number of messages");
        Assert.Multiple(() =>
        {
            for (var i = 0; i < overTcp.Count; i++)
            {
                Assert.That(
                    overTcp[i],
                    Is.EqualTo(inProcess[i]),
                    $"message {i} (type 0x{inProcess[i][0]:X2}) differs between the socket and the in-process link");
            }

            Assert.That(overTcp[0][0], Is.EqualTo(MessageTypes.Welcome));
            Assert.That(overTcp[1][0], Is.EqualTo(MessageTypes.Pong));
        });
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private const int TickRateHz = 100;

    private static TyphonRuntime CreateRuntime(DatabaseEngine dbe) => TyphonRuntime.Create(dbe, schedule =>
    {
        schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
    }, new RuntimeOptions
    {
        WorkerCount = 1, BaseTickRate = TickRateHz, Subscriptions = new SubscriptionsOptions { ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0) },
    });

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("god");
        ProjectionTestSchema.DeclareCreature(subs);
        ProjectionTestSchema.DeclarePlayer(subs);
        ProjectionTestSchema.DeclareRock(subs);
        subs.Profile("god-world", p => p.World().Of<ProjCreature>().Of<ProjPlayer>().Of<ProjRock>());
    }

    /// <summary>Starts the fixture's transport on an ephemeral loopback port.</summary>
    /// <param name="acceptor">Who adopts the connections.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>Where it bound.</returns>
    private IPEndPoint StartTransport(ISubscriptionAcceptor acceptor, TcpSubscriptionOptions options = null)
    {
        var transport = new TcpSubscriptionTransport(options);
        _transport = transport;
        transport.Start(acceptor);
        return transport.BoundEndPoint;
    }

    private TestClient Connect(IPEndPoint endpoint)
    {
        var client = new TestClient(endpoint, TimeoutMs);
        _disposables.Add(client);
        return client;
    }

    /// <summary>Exchanges the preamble and waits until the acceptor has adopted the connection.</summary>
    /// <param name="client">The client socket.</param>
    /// <param name="acceptor">The acceptor to read the connection from.</param>
    /// <returns>The connection the transport created.</returns>
    private static RecordingConnection Handshake(TestClient client, RecordingAcceptor acceptor)
    {
        client.Send(ProtocolConstants.TcpPreamble);
        Assert.That(client.ReadExactly(4), Is.EqualTo(ProtocolConstants.TcpPreamble.ToArray()), "the server's preamble");
        Assert.That(acceptor.WaitForAccept(TimeoutMs), Is.True, "the transport never adopted the connection");
        return acceptor.LastConnection;
    }

    /// <summary>Waits a moment to see whether a second close arrives, without a fixed sleep on the passing path.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="above">The count to exceed.</param>
    /// <returns><see langword="true"/> when it did, which is a failure.</returns>
    private static bool SpinForClosesAbove(RecordingConnection connection, int above)
    {
        var deadline = Environment.TickCount64 + 100;
        while (Environment.TickCount64 < deadline)
        {
            if (connection.Closes > above)
            {
                return true;
            }

            Thread.Yield();
        }

        return connection.Closes > above;
    }

    private static byte[] Encode(KickMessage kick)
    {
        Span<byte> scratch = stackalloc byte[1 + 2 + 1 + ProtocolConstants.KickReasonMaxBytes];
        var writer = new WireWriter(scratch);
        kick.Write(ref writer);
        return writer.Written.ToArray();
    }

    /// <summary>Drives the script over a real socket against a fresh fake host, and collects what the server sent back.</summary>
    /// <param name="script">The client-to-server messages, in order.</param>
    /// <returns>The server's messages, framing removed.</returns>
    private List<byte[]> RunScriptOverTcp(byte[][] script)
    {
        using var world = new FakeWorld();
        var endpoint = StartTransport(new SubscriptionAcceptor(world.Host));

        using var client = Connect(endpoint);
        client.Send(ProtocolConstants.TcpPreamble);
        client.ReadExactly(4);

        var received = new List<byte[]>();
        client.SendFramed(script[0]);
        received.Add(client.ReadMessage());
        client.SendFramed(script[1]);
        received.Add(client.ReadMessage());
        client.SendFramed(script[2]);

        Assert.That(client.ReadWhatever(), Is.Empty, "the socket arm was sent something after BYE");
        return received;
    }

    /// <summary>Drives the same script through an in-process link against an identically configured fake host.</summary>
    /// <param name="script">The client-to-server messages, in order.</param>
    /// <returns>The server's messages.</returns>
    private static List<byte[]> RunScriptInProcess(byte[][] script)
    {
        using var world = new FakeWorld();
        var acceptor = new SubscriptionAcceptor(world.Host);
        var link = new InProcessLink();
        var connection = acceptor.Accept(link, new LinkInfo { Transport = "fake" });
        link.Connection = connection;

        var received = new List<byte[]>();
        foreach (var message in script)
        {
            connection.OnMessage(message);
            while (link.TryTake(out var reply, 50))
            {
                received.Add(reply);
            }
        }

        return received;
    }

    /// <summary>A fake replication runtime and the resources its session table needs, disposed together.</summary>
    private sealed class FakeWorld : IDisposable
    {
        private readonly ResourceRegistry _registry;
        private readonly MemoryAllocator _allocator;
        private readonly SessionTable _table;

        public FakeWorld()
        {
            _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "TcpTransportTests" });
            _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "TcpTransportTestAllocator" });
            var sessions = new SubscriptionsSessions();
            sessions.Kinds("god");
            _table = new SessionTable("Sessions", _registry.Runtime, _allocator, new SubscriptionsOptions { MaxSessions = 4 }, sessions.SessionEvents);
            // Answers PONGs the way the runtime's send pump does, since this fixture compares what reaches the wire and has no pump of its own.
            Host = new FakeSubscriptionsHost { Sessions = sessions, SessionTable = _table, AnswerPongs = true };
        }

        public FakeSubscriptionsHost Host { get; }

        public void Dispose()
        {
            _table.Dispose();
            _allocator.Dispose();
            _registry.Dispose();
        }
    }

    /// <summary>Stops a transport from the fixture's disposable list.</summary>
    private sealed class TransportHandle(TcpSubscriptionTransport transport) : IDisposable
    {
        public void Dispose() => transport.StopAsync().AsTask().Wait(TimeoutMs);
    }

    /// <summary>An acceptor that adopts every connection and remembers it, so a test can assert on what the transport did to it.</summary>
    private sealed class RecordingAcceptor : ISubscriptionAcceptor
    {
        private readonly ManualResetEventSlim _accepted = new(false);
        private int _accepts;

        public int Accepts => Volatile.Read(ref _accepts);

        public RecordingConnection LastConnection { get; private set; }

        public ISubscriptionLink LastLink { get; private set; }

        public LinkInfo LastInfo { get; private set; }

        public ISubscriptionConnection Accept(ISubscriptionLink link, in LinkInfo info)
        {
            var connection = new RecordingConnection();
            LastConnection = connection;
            LastLink = link;
            LastInfo = info;
            Interlocked.Increment(ref _accepts);
            _accepted.Set();
            return connection;
        }

        public bool WaitForAccept(int timeoutMs) => _accepted.Wait(timeoutMs);
    }

    /// <summary>The engine's side of one connection, recording what the transport delivered and when it stopped.</summary>
    private sealed class RecordingConnection : ISubscriptionConnection
    {
        private readonly ManualResetEventSlim _closed = new(false);
        private readonly ManualResetEventSlim _arrived = new(false);
        private readonly List<byte[]> _messages = [];
        private int _inMessage;
        private int _closes;
        private int _overlaps;

        /// <summary>The messages delivered, in order.</summary>
        public IReadOnlyList<byte[]> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToArray();
                }
            }
        }

        /// <summary>How many times <see cref="OnClosed"/> was called. Anything but one is the bug this fixture exists for.</summary>
        public int Closes => Volatile.Read(ref _closes);

        /// <summary>How many times two deliveries overlapped, which the seam forbids.</summary>
        public int Overlaps => Volatile.Read(ref _overlaps);

        /// <summary>The first close's code.</summary>
        public ushort CloseCode { get; private set; }

        /// <summary>The first close's error.</summary>
        public Exception CloseError { get; private set; }

        public void OnMessage(ReadOnlySpan<byte> message)
        {
            if (Interlocked.Exchange(ref _inMessage, 1) != 0)
            {
                Interlocked.Increment(ref _overlaps);
            }

            lock (_messages)
            {
                _messages.Add(message.ToArray());
            }

            Volatile.Write(ref _inMessage, 0);
            _arrived.Set();
        }

        public void OnClosed(ushort code, Exception error)
        {
            if (Interlocked.Increment(ref _closes) == 1)
            {
                CloseCode = code;
                CloseError = error;
            }

            _closed.Set();
        }

        public bool WaitForClose(int timeoutMs) => _closed.Wait(timeoutMs);

        public bool WaitForMessages(int count, int timeoutMs)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                lock (_messages)
                {
                    if (_messages.Count >= count)
                    {
                        return true;
                    }
                }

                _arrived.Wait(20);
                _arrived.Reset();
            }

            lock (_messages)
            {
                return _messages.Count >= count;
            }
        }
    }

    /// <summary>
    /// A client socket that speaks the framing by hand, so the test is an independent peer rather than a second copy of the implementation.
    /// </summary>
    private sealed class TestClient : IDisposable
    {
        private readonly Socket _socket;

        public TestClient(IPEndPoint endpoint, int timeoutMs)
        {
            _socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
                ReceiveTimeout = timeoutMs,
                SendTimeout = timeoutMs,
            };

            _socket.Connect(endpoint);
        }

        public void Send(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                var sent = _socket.Send(bytes);
                bytes = bytes[sent..];
            }
        }

        /// <summary>Writes a length prefix and nothing else — how a test proves the body was never waited for.</summary>
        /// <param name="length">The length to declare.</param>
        public void SendLength(uint length)
        {
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, length);
            Send(header);
        }

        public void SendFramed(ReadOnlySpan<byte> message)
        {
            SendLength((uint)message.Length);
            Send(message);
        }

        /// <summary>Reads exactly <paramref name="count"/> bytes, failing the test when the peer stops first.</summary>
        /// <param name="count">How many.</param>
        /// <returns>The bytes.</returns>
        public byte[] ReadExactly(int count)
        {
            var buffer = new byte[count];
            var total = 0;
            while (total < count)
            {
                var read = _socket.Receive(buffer, total, count - total, SocketFlags.None);
                if (read <= 0)
                {
                    Assert.Fail($"the server closed after {total} of {count} expected bytes");
                }

                total += read;
            }

            return buffer;
        }

        /// <summary>Reads one framed message.</summary>
        /// <returns>The message, framing removed.</returns>
        public byte[] ReadMessage() => ReadExactly((int)BinaryPrimitives.ReadUInt32LittleEndian(ReadExactly(4)));

        /// <summary>
        /// Reads everything the server still has to say, until it closes. Empty means it closed with nothing more.
        /// </summary>
        /// <returns>The bytes.</returns>
        /// <remarks>
        /// It reads to the end rather than once, because one <c>Receive</c> returning nothing proves nothing on a stream: a message can arrive in two segments
        /// and an assertion written against a single read would be green whenever the second one was late.
        /// </remarks>
        public byte[] ReadWhatever()
        {
            var all = new List<byte>();
            var buffer = new byte[1024];
            try
            {
                while (true)
                {
                    var read = _socket.Receive(buffer);
                    if (read <= 0)
                    {
                        break;
                    }

                    all.AddRange(buffer[..read]);
                }
            }
            catch (SocketException)
            {
                // A reset rather than a FIN: whatever arrived before it is what the peer said.
            }

            return all.ToArray();
        }

        public void Dispose()
        {
            try
            {
                _socket.Dispose();
            }
            catch (SocketException)
            {
            }
        }
    }
}
