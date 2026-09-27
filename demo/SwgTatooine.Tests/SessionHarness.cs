using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// A client connected to the demo's replication, in process, with no socket and no web host.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why in-process rather than a real WebSocket.</b> A transport is a seam the engine defines
/// (<c>design/Subscriptions/04-transport.md § 2</c>): the bytes captured here are the bytes a WebSocket would carry, and nothing in the engine can tell the
/// difference. Going through Kestrel instead would bind a port, add a handshake nobody is asking about, and make a check that is about a <c>KICK</c> fail for
/// reasons that have nothing to do with one — on a shared build box, intermittently.
/// </para>
/// <para>
/// <b>It speaks the protocol through the protocol's own writers.</b> Never a hand-spelt byte array: a test that writes the wire itself is green in the same
/// build as a broken encoder.
/// </para>
/// </remarks>
internal sealed class FakeLink : ISubscriptionLink
{
    private readonly List<byte[]> _sent = [];

    /// <summary>The connection the acceptor handed back, so a close can be reported to it as a real transport does.</summary>
    public ISubscriptionConnection Connection { get; set; }

    /// <summary>The close code and reason the engine asked for, or null while the link is open.</summary>
    public (ushort Code, string Reason)? Closed { get; private set; }

    /// <inheritdoc/>
    public bool SupportsUnreliable => false;

    /// <summary>Every server-to-client message, in order. Copied out of the engine's buffer before the send completes.</summary>
    public byte[][] Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToArray();
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        // Copied, not kept: the memory is engine-owned and recycled the moment this task completes.
        lock (_sent)
        {
            _sent.Add(message.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public bool TrySendUnreliable(ReadOnlySpan<byte> datagram) => false;

    /// <inheritdoc/>
    public void Close(ushort code, string reason)
    {
        if (Closed != null)
        {
            return;
        }

        Closed = (code, reason);
        Connection?.OnClosed(code, null);
    }

    /// <summary>The first message of a type, decoded, or null when none arrived.</summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="type">Its <see cref="MessageTypes"/> byte.</param>
    /// <param name="parse">The message's own parser.</param>
    /// <returns>The message, or null.</returns>
    public T? FirstOf<T>(byte type, Func<byte[], T> parse) where T : struct
    {
        foreach (var message in Sent)
        {
            if (message.Length > 0 && message[0] == type)
            {
                return parse(message);
            }
        }

        return null;
    }

    /// <summary>Whether any message of a type arrived, for the types whose contents this does not need to read.</summary>
    /// <param name="type">Its <see cref="MessageTypes"/> byte.</param>
    /// <returns>Whether one arrived.</returns>
    public bool Received(byte type)
    {
        foreach (var message in Sent)
        {
            if (message.Length > 0 && message[0] == type)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>A transport that does the one thing every transport does — take the acceptor — and hands it to the test.</summary>
internal sealed class FakeTransport : ISubscriptionTransport
{
    /// <summary>The acceptor the runtime handed over on <see cref="Start"/>.</summary>
    public ISubscriptionAcceptor Acceptor { get; private set; }

    /// <inheritdoc/>
    public void Start(ISubscriptionAcceptor acceptor) => Acceptor = acceptor;

    /// <inheritdoc/>
    public ValueTask StopAsync() => ValueTask.CompletedTask;
}

/// <summary>Builds the client-to-server messages these checks send, through <c>Typhon.Protocol</c>'s own writers.</summary>
internal static class ClientSays
{
    /// <summary>Encodes a <c>HELLO</c>.</summary>
    /// <param name="kind">The session kind the client names.</param>
    /// <returns>The message.</returns>
    public static byte[] Hello(string kind)
    {
        var hello = new HelloMessage
        {
            Major = ProtocolConstants.Major,
            Minor = ProtocolConstants.Minor,
            Caps = Capabilities.None,
            Kind = kind,
            Token = "opaque",
            ClientCatalogHash = 0,
            HelloPayload = [],
        };

        var scratch = new byte[ProtocolConstants.HelloMaxBytes];
        var writer = new WireWriter(scratch);
        hello.Write(ref writer);
        return writer.Written.ToArray();
    }
}

/// <summary>Connects fake clients to a started <see cref="TatooineSim"/>'s replication and waits on its ticks.</summary>
internal sealed class SessionHarness
{
    private readonly TatooineSim _sim;
    private readonly ISubscriptionAcceptor _acceptor;

    /// <summary>Starts replication on a simulation and a transport against it.</summary>
    /// <param name="sim">An initialised simulation; this starts its runtime.</param>
    public SessionHarness(TatooineSim sim)
    {
        _sim = sim;
        sim.StartReplication();

        var transport = new FakeTransport();
        sim.Runtime.StartSubscriptionTransport(transport);
        Assert.That(transport.Acceptor, Is.Not.Null, "the runtime did not hand the transport an acceptor, so replication is not running");
        _acceptor = transport.Acceptor;
    }

    /// <summary>Connects one client and completes its handshake.</summary>
    /// <param name="kind">The session kind: <c>god</c> or <c>player</c>.</param>
    /// <returns>The link, whose <see cref="FakeLink.Sent"/> is what the client received.</returns>
    public FakeLink Connect(string kind)
    {
        var link = new FakeLink();
        var info = new LinkInfo { Transport = "fake", SubProtocol = ProtocolConstants.WebSocketSubprotocol };
        var connection = _acceptor.Accept(link, info);
        Assert.That(connection, Is.Not.Null, "the runtime declined the link outright");
        link.Connection = connection;
        connection.OnMessage(ClientSays.Hello(kind));
        return link;
    }

    /// <summary>Waits until the runtime has ticked past where it is now.</summary>
    /// <param name="count">How many ticks.</param>
    public void Ticks(int count)
    {
        var target = _sim.Runtime.CurrentTickNumber + count;
        Assert.That(SpinWait.SpinUntil(() => _sim.Runtime.CurrentTickNumber >= target, TimeSpan.FromSeconds(10)), Is.True, "the runtime stopped ticking");
    }

    /// <summary>Waits for a condition the tick or the send pump makes true, or fails.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="what">What was being waited for, for the failure message.</param>
    public static void Until(Func<bool> condition, string what)
        => Assert.That(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)), Is.True, $"timed out waiting for {what}");
}
