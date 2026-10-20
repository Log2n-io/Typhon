using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// #1397 — the pending-connection cap (SUB-37), and the surfaces the Phase 1 TCP audit named untested (O-11): TLS, <c>Attach</c> racing a close,
/// <c>StopAsync</c>
/// in the middle of a handshake, and a length prefix nobody should send.
/// </summary>
/// <remarks>
/// A connection is <i>pending</i> from the moment it is accepted until its <c>HELLO</c> is admitted: through TLS, the preamble and the <c>HELLO</c> wait. The
/// fixture's <see cref="RecordingAcceptor"/> never runs a <c>HELLO</c>, so the tests that use it admit by hand, through the link's
/// <see cref="IAdmissionObserver"/> — the same call the engine's connection makes; the live-engine case proves the engine makes it.
/// </remarks>
partial class TcpTransportTests
{
    /// <summary>What SUB-37's verifier says when an admitted session still counts as pending; the mutant must produce exactly this.</summary>
    private const string PendingHeldMarker = "an admitted session still holds a pending slot";

    // ── the cap ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Past the cap a connection is refused the moment it is accepted — no preamble, no TLS, no slot — and counted; an admission frees a slot for the next one.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-37")]
    public void AConnectionPastThePendingCapIsRefusedAtAcceptAndCounted()
    {
        var acceptor = new RecordingAcceptor();
        var endpoint = StartTransport(acceptor, new TcpSubscriptionOptions { MaxPendingConnections = 2 });

        using var first = Connect(endpoint);
        using var second = Connect(endpoint);
        Assert.That(SpinUntil(() => _transport.PendingConnectionCount == 2), Is.True, "two silent connections are pending");

        using var third = Connect(endpoint);
        Assert.That(SpinUntil(() => _transport.PendingRefused == 1), Is.True, "the connection past the cap was not refused");
        Assert.Multiple(() =>
        {
            Assert.That(third.ReadWhatever(), Is.Empty, "a refused connection is told nothing: spending a preamble on it is what the cap avoids");
            Assert.That(third.EndedWith, Is.EqualTo(SocketError.ConnectionReset),
                "refused with a reset (linger 0), not a FIN that would leave the server holding TIME_WAIT for every refused connect");
            Assert.That(_transport.PendingConnectionCount, Is.EqualTo(2), "a refusal takes no slot");
            Assert.That(acceptor.Accepts, Is.Zero, "nothing reached the engine");
        });

        Handshake(first, acceptor);
        ((IAdmissionObserver)acceptor.LastLink).OnAdmitted();
        Assert.That(SpinUntil(() => _transport.PendingConnectionCount == 1), Is.True, "an admission did not give its slot back");

        using var fourth = Connect(endpoint);
        fourth.Send(ProtocolConstants.TcpPreamble);
        Assert.Multiple(() =>
        {
            Assert.That(fourth.ReadExactly(4), Is.EqualTo(ProtocolConstants.TcpPreamble.ToArray()), "the slot an admission freed serves the next connection");
            Assert.That(_transport.PendingRefused, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// Every way a connection ends gives its slot back exactly once: a silent one at the handshake deadline, one that closes after the preamble, and one that
    /// was admitted and then closed — whose second release, at the close, must not take the count below zero.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-37")]
    public void EveryWayOutReleasesThePendingSlotExactlyOnce()
    {
        var acceptor = new RecordingAcceptor();
        // A second, not 100 ms: the deadline also covers the two later connections' preambles, and the first observation of a pending slot must not race it on
        // a gate box running eight shards.
        var endpoint = StartTransport(acceptor, new TcpSubscriptionOptions { HandshakeTimeoutMs = 1000 });

        using (Connect(endpoint))
        {
            Assert.That(SpinUntil(() => _transport.PendingConnectionCount == 1), Is.True, "a silent connection is pending");
            Assert.That(SpinUntil(() => _transport.PendingConnectionCount == 0), Is.True, "the handshake deadline did not release a silent connection's slot");
        }

        var unadmitted = Connect(endpoint);
        var dropped = Handshake(unadmitted, acceptor);
        Assert.That(_transport.PendingConnectionCount, Is.EqualTo(1), "adopted and waiting for HELLO is still pending");
        unadmitted.Dispose();
        Assert.That(dropped.WaitForClose(TimeoutMs), Is.True);
        Assert.That(SpinUntil(() => _transport.PendingConnectionCount == 0), Is.True, "a connection closed before admission kept its slot");

        // Not through Handshake: the acceptor's event is already set by the first adoption, so its LastLink could still be the dropped connection's. The
        // second adoption is what this waits for.
        var admitted = Connect(endpoint);
        admitted.Send(ProtocolConstants.TcpPreamble);
        admitted.ReadExactly(4);
        Assert.That(SpinUntil(() => acceptor.Accepts == 2), Is.True, "the second connection was never adopted");
        var session = acceptor.LastConnection;
        ((IAdmissionObserver)acceptor.LastLink).OnAdmitted();
        Assert.That(_transport.PendingConnectionCount, Is.Zero);
        admitted.Dispose();
        Assert.That(session.WaitForClose(TimeoutMs), Is.True);
        Assert.That(SpinUntil(() => _transport.ServesEndedForTest == 3), Is.True, "the admitted connection's serve loop did not finish");
        Assert.That(_transport.PendingConnectionCount, Is.Zero, "the close released a slot the admission had already released");
    }

    /// <summary>Against the engine: a connection stops being pending at its <c>HELLO</c>, so a cap of one serves a second client while the first session
    /// lives.</summary>
    [Test]
    [VerifiesRule("SUB-37")]
    public void AnAdmittedSessionNoLongerHoldsAPendingSlot() => AssertAdmissionReleasesTheSlot(keepOnAdmission: false);

    /// <summary>The same scenario with the slot kept until the session ends: the verifier above must reject it.</summary>
    [Test]
    [RuleMutant("SUB-37")]
    public void ASlotKeptPastAdmissionIsDetected() =>
        RuleMutants.AssertDetects("SUB-37", PendingHeldMarker, () => AssertAdmissionReleasesTheSlot(keepOnAdmission: true));

    private void AssertAdmissionReleasesTheSlot(bool keepOnAdmission)
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var transport = new TcpSubscriptionTransport(new TcpSubscriptionOptions { MaxPendingConnections = 1 })
        {
            KeepPendingOnAdmissionForTest = keepOnAdmission,
        };
        _transport = transport;
        runtime.StartSubscriptionTransport(transport);

        try
        {
            using var client = Connect(transport.BoundEndPoint);
            client.Send(ProtocolConstants.TcpPreamble);
            Assert.That(client.ReadExactly(4), Is.EqualTo(ProtocolConstants.TcpPreamble.ToArray()));

            client.SendFramed(ClientMessages.Hello("god", Capabilities.None));
            var welcome = WelcomeMessage.Parse(client.ReadMessage());
            Assert.That(welcome.SessionId, Is.GreaterThan(0u), "admitted");

            // The slot goes back before WELCOME is written, so it is free by the time the client has read it.
            Assert.That(transport.PendingConnectionCount, Is.Zero, PendingHeldMarker);

            using var second = Connect(transport.BoundEndPoint);
            second.Send(ProtocolConstants.TcpPreamble);
            Assert.That(second.ReadExactly(4), Is.EqualTo(ProtocolConstants.TcpPreamble.ToArray()), "a cap of one served a second client");
        }
        finally
        {
            runtime.Shutdown();
        }
    }

    /// <summary>A cap that admits nothing is refused where the host can see it.</summary>
    [Test]
    public void APendingCapOfZeroIsRefused()
    {
        var refusal = Assert.Throws<ArgumentException>(() => new TcpSubscriptionTransport(new TcpSubscriptionOptions { MaxPendingConnections = 0 }));

        Assert.That(refusal.ParamName, Is.EqualTo(nameof(TcpSubscriptionOptions.MaxPendingConnections)));
    }

    // ── TLS ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Over TLS the preamble and the framing run inside the encrypted stream and a message arrives whole; a peer that connects and never starts TLS holds a
    /// pending slot until the handshake deadline drops it.
    /// </summary>
    [Test]
    public void ATlsClientIsServedAndOneThatNeverStartsTlsIsDroppedAtTheDeadline()
    {
        using var certificate = SelfSignedCertificate();
        var acceptor = new RecordingAcceptor();
        // 2 s: the deadline covers the served client's TLS too, and the first cold handshake loads a PKCS#12 key.
        var endpoint = StartTransport(acceptor, new TcpSubscriptionOptions { ServerCertificate = certificate, HandshakeTimeoutMs = 2000 });

        var stalled = Connect(endpoint);
        Assert.That(SpinUntil(() => _transport.PendingConnectionCount == 1), Is.True, "a connection in TLS is pending");

        using var tls = new TlsTestClient(endpoint);
        tls.Write(ProtocolConstants.TcpPreamble);
        Assert.That(tls.ReadExactly(4), Is.EqualTo(ProtocolConstants.TcpPreamble.ToArray()), "the preamble, inside TLS");
        Assert.That(acceptor.WaitForAccept(TimeoutMs), Is.True, "the TLS connection was never adopted");

        var hello = ClientMessages.Hello("god", Capabilities.None);
        tls.WriteFramed(hello);
        var connection = acceptor.LastConnection;
        Assert.That(connection.WaitForMessages(1, TimeoutMs), Is.True, "a framed message over TLS did not arrive");
        Assert.That(connection.Messages[0], Is.EqualTo(hello), "it arrived whole");

        Assert.That(stalled.ReadWhatever(), Is.Empty, "the stalled peer was told nothing");
        Assert.That(SpinUntil(() => _transport.PendingConnectionCount == 1), Is.True, "the deadline did not release the stalled peer's slot");
        stalled.Dispose();
    }

    /// <summary>
    /// <c>StopAsync</c> does not wait out a handshake: a peer stuck in TLS and one adopted but silent are both ended at once, and every slot comes back.
    /// </summary>
    [Test]
    public void StoppingMidHandshakeEndsEveryConnectionAndReleasesEverySlot()
    {
        using var certificate = SelfSignedCertificate();
        var acceptor = new RecordingAcceptor();
        var endpoint = StartTransport(acceptor, new TcpSubscriptionOptions { ServerCertificate = certificate });

        using var inTls = Connect(endpoint);
        using var waitingForHello = new TlsTestClient(endpoint);
        waitingForHello.Write(ProtocolConstants.TcpPreamble);
        waitingForHello.ReadExactly(4);
        Assert.That(acceptor.WaitForAccept(TimeoutMs), Is.True);
        var adopted = acceptor.LastConnection;
        Assert.That(SpinUntil(() => _transport.PendingConnectionCount == 2), Is.True);

        var transport = _transport;
        _transport = null;
        var clock = Stopwatch.StartNew();
        Assert.That(transport.StopAsync().AsTask().Wait(TimeoutMs), Is.True, "StopAsync did not return");

        Assert.Multiple(() =>
        {
            Assert.That(clock.ElapsedMilliseconds, Is.LessThan(ProtocolConstants.HelloTimeoutMs / 2), "StopAsync waited for a handshake deadline");
            Assert.That(adopted.Closes, Is.EqualTo(1), "the adopted connection was not told");
            Assert.That(adopted.CloseCode, Is.EqualTo(CloseCodes.GoingAway));
            Assert.That(transport.ConnectionCount, Is.Zero);
            Assert.That(inTls.ReadWhatever(), Is.Empty, "the peer in TLS was dropped, not served");
        });
        Assert.That(SpinUntil(() => transport.PendingConnectionCount == 0), Is.True, "a slot outlived the transport");
    }

    // ── the races and the framing the audit named ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The engine closes the link between <c>Accept</c> and <c>Attach</c> — what a <c>HELLO</c> deadline firing in that window does: the connection is told
    /// once, and the transport lets go of everything it held.
    /// </summary>
    [Test]
    public void ACloseBeforeAttachIsReportedOnceAndReleasesEverything()
    {
        var acceptor = new ClosingAcceptor();
        var endpoint = StartTransport(acceptor);

        using var client = Connect(endpoint);
        client.Send(ProtocolConstants.TcpPreamble);
        client.ReadExactly(4);

        Assert.That(acceptor.Accepted.Wait(TimeoutMs), Is.True, "never adopted");
        var connection = acceptor.Connection;
        Assert.That(connection.WaitForClose(TimeoutMs), Is.True, "a close before Attach reached nobody");
        Assert.Multiple(() =>
        {
            Assert.That(SpinForClosesAbove(connection, 1), Is.False, "told twice");
            Assert.That(connection.CloseCode, Is.EqualTo(CloseCodes.TryAgainLater));
            Assert.That(SpinUntil(() => _transport.ConnectionCount == 0 && _transport.PendingConnectionCount == 0), Is.True, "the transport kept the link");
            Assert.That(client.ReadWhatever(), Is.Empty);
        });
    }

    /// <summary>
    /// A length prefix at the top of the <c>u32</c> range, or with the bit an <c>int</c> would read as a sign, closes 1009 on the prefix alone — as the first
    /// message and after one.
    /// </summary>
    /// <param name="allOnes">0xFFFFFFFF when set, 0x80000000 otherwise (a bool, not the value: a test name holding <c>uint.MaxValue</c>'s dot is not a
    /// legal database name for the fixture's base class).</param>
    /// <param name="afterAMessage">Whether a first message went through before it.</param>
    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(true, true)]
    [TestCase(false, true)]
    public void APathologicalLengthPrefixClosesWith1009OnThePrefix(bool allOnes, bool afterAMessage)
    {
        var length = allOnes ? 0xFFFFFFFFu : 0x80000000u;
        var acceptor = new RecordingAcceptor();
        var endpoint = StartTransport(acceptor);

        using var client = Connect(endpoint);
        var connection = Handshake(client, acceptor);
        if (afterAMessage)
        {
            client.SendFramed(ClientMessages.Hello("god", Capabilities.None));
            Assert.That(connection.WaitForMessages(1, TimeoutMs), Is.True);
        }

        client.SendLength(length);

        Assert.That(connection.WaitForClose(TimeoutMs), Is.True, "the transport waited for a body it should have refused on the prefix");
        Assert.Multiple(() =>
        {
            Assert.That(connection.CloseCode, Is.EqualTo(CloseCodes.MessageTooBig));
            Assert.That(connection.Closes, Is.EqualTo(1));
            Assert.That(connection.Messages, Has.Count.EqualTo(afterAMessage ? 1 : 0));
        });
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Polls without sleeping: <c>SpinWait.SpinUntil</c> sleeps a whole timer quantum between checks once it stops spinning.</summary>
    private static bool SpinUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + TimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Yield();
        }

        return condition();
    }

    /// <summary>A throwaway server certificate for <c>localhost</c>.</summary>
    /// <remarks>
    /// Through PKCS#12 rather than straight from <see cref="CertificateRequest.CreateSelfSigned"/>: SChannel will not serve with a key that exists only in this
    /// process's memory, so the certificate is reloaded to get a key the operating system can reach.
    /// </remarks>
    private static X509Certificate2 SelfSignedCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    /// <summary>Closes every link it is handed before returning its connection — a <c>HELLO</c> deadline that fired between <c>Accept</c> and
    /// <c>Attach</c>.</summary>
    private sealed class ClosingAcceptor : ISubscriptionAcceptor
    {
        public ManualResetEventSlim Accepted { get; } = new(false);

        public RecordingConnection Connection { get; private set; }

        public ISubscriptionConnection Accept(ISubscriptionLink link, in LinkInfo info)
        {
            var connection = new RecordingConnection();
            Connection = connection;
            link.Close(CloseCodes.TryAgainLater, null);
            Accepted.Set();
            return connection;
        }
    }

    /// <summary>A TLS client that accepts any certificate and speaks the framing by hand, as <see cref="TestClient"/> does in plaintext.</summary>
    private sealed class TlsTestClient : IDisposable
    {
        private readonly SslStream _ssl;

        public TlsTestClient(IPEndPoint endpoint)
        {
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            socket.Connect(endpoint);
            _ssl = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false)
            {
                ReadTimeout = TimeoutMs, WriteTimeout = TimeoutMs,
            };
            _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                })
                .GetAwaiter()
                .GetResult();
        }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            _ssl.Write(bytes);
            _ssl.Flush();
        }

        public void WriteFramed(ReadOnlySpan<byte> message)
        {
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)message.Length);
            _ssl.Write(header);
            Write(message);
        }

        public byte[] ReadExactly(int count)
        {
            var buffer = new byte[count];
            _ssl.ReadExactly(buffer);
            return buffer;
        }

        public void Dispose() => _ssl.Dispose();
    }
}
