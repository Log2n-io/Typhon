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

    /// <summary>The session this link's <c>HELLO</c> opened, read from the <c>WELCOME</c> the server answered with.</summary>
    /// <remarks>
    /// Taken from the wire rather than from a session table, because that is what a client has: a test that reached into the engine for it could pass while
    /// the identity the client was actually told was a different one.
    /// </remarks>
    public SessionId Session
    {
        get
        {
            foreach (var message in Sent)
            {
                if (message.Length > 0 && message[0] == MessageTypes.Welcome)
                {
                    return SessionId.FromValue(WelcomeMessage.Parse(message).SessionId);
                }
            }

            return SessionId.None;
        }
    }

    /// <summary>Delivers a client-to-server message, as a transport does when bytes arrive.</summary>
    /// <param name="message">The encoded message.</param>
    public void Send(byte[] message) => Connection.OnMessage(message);

    /// <summary>
    /// The compiled catalog this client negotiated, built the way a real client builds one: from the JSON <c>WELCOME</c> carried.
    /// </summary>
    /// <remarks>
    /// <b>From the wire, not from the registry.</b> A test that compiled the server's own export would encode against a catalog the client was never sent, so
    /// a command index that disagreed between the two would pass here and fail against every real client.
    /// </remarks>
    public CatalogPlan Plan
    {
        get
        {
            if (_plan != null)
            {
                return _plan;
            }

            foreach (var message in Sent)
            {
                if (message.Length > 0 && message[0] == MessageTypes.Welcome)
                {
                    _plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(WelcomeMessage.Parse(message).CatalogJson));
                    return _plan;
                }
            }

            throw new InvalidOperationException("no WELCOME arrived, so this client has no catalog to encode against");
        }
    }

    private CatalogPlan _plan;

    /// <summary>Sends one command, encoded through the protocol's own writer against this client's negotiated catalog.</summary>
    /// <param name="name">The command's declared name.</param>
    /// <param name="values">Its field values, by field name.</param>
    /// <remarks>
    /// <b>Never a hand-spelt byte array.</b> A test that writes the wire itself is green in the same build as a broken encoder, and — worse here — would pin
    /// a command index that the catalog is free to renumber when a declaration is added.
    /// </remarks>
    public void SendCommand(string name, RecordValues values)
    {
        var commands = new List<(MessagePlan, ushort, RecordValues)> { (Plan.CommandByName(name), _seq++, values) };
        var buffer = new byte[1024];
        var writer = new WireWriter(buffer);
        CommandsMessage.Write(ref writer, clientTick: 1, commands);
        Send(writer.Written.ToArray());
    }

    private ushort _seq = 1;

    /// <summary>
    /// The close code and reason the engine asked for, or null while the link is open.
    /// </summary>
    /// <remarks>
    /// <b>Published under the same lock as <see cref="Sent"/>, because the writer and the readers are different threads.</b> <c>Close</c> runs on the send
    /// pump's pool thread; every reader is the test thread, and it reads twice — once to see that a close happened and once for the code. A
    /// <c>Nullable&lt;(ushort, string)&gt;</c> is several words, so an unsynchronized publication can be seen half-written, and on arm64 the reads can be
    /// reordered against the writes that set it. The lock is free here and the alternative is a flake nobody would diagnose.
    /// </remarks>
    public (ushort Code, string Reason)? Closed
    {
        get
        {
            lock (_sent)
            {
                return _closed;
            }
        }
    }

    private (ushort Code, string Reason)? _closed;

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
        lock (_sent)
        {
            if (_closed != null)
            {
                return;
            }

            _closed = (code, reason);
        }

        // Outside the lock: OnClosed reaches into the engine, and holding a lock a send could also want across that call is how a deadlock is built.
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
    /// <remarks>
    /// <b>Under the lock rather than over <see cref="Sent"/>, because this is called from inside a spin loop.</b> <see cref="Sent"/> copies the whole list,
    /// so every poll of a wait allocated one array per link — in a predicate that runs hundreds of times per case.
    /// </remarks>
    public bool Received(byte type)
    {
        lock (_sent)
        {
            for (var i = 0; i < _sent.Count; i++)
            {
                if (_sent[i].Length > 0 && _sent[i][0] == type)
                {
                    return true;
                }
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
    /// <summary>Encodes a <c>PING</c>, which is how a client stays connected.</summary>
    /// <param name="clientMs">The client's own clock reading, echoed in the <c>PONG</c>.</param>
    /// <param name="lastAppliedTick">The newest tick the client has applied.</param>
    /// <returns>The message.</returns>
    public static byte[] Ping(uint clientMs, uint lastAppliedTick)
    {
        var scratch = new byte[16];
        var writer = new WireWriter(scratch);
        new PingMessage(clientMs, lastAppliedTick).Write(ref writer);
        return writer.Written.ToArray();
    }

    /// <summary>The field values of a <c>MoveTo</c>.</summary>
    /// <param name="x">Where to walk.</param>
    /// <param name="z">Where to walk.</param>
    /// <returns>The values, keyed by the names the declaration gave.</returns>
    public static RecordValues MoveTo(float x, float z) => new()
    {
        ["x"] = FieldValue.Of(x),
        ["z"] = FieldValue.Of(z),
    };

    /// <summary>The field values of a <c>MoveDir</c>.</summary>
    /// <param name="heading">Radians, 0 along +X.</param>
    /// <param name="speedClass">See <c>SpeedClasses</c>.</param>
    /// <returns>The values.</returns>
    public static RecordValues MoveDir(float heading, byte speedClass) => new()
    {
        ["heading"] = FieldValue.Of(heading),
        ["speedClass"] = FieldValue.Of(speedClass),
    };

    /// <summary>The field values of a <c>SetTarget</c>.</summary>
    /// <param name="netId">The target's network identity, or 0 to clear.</param>
    /// <returns>The values.</returns>
    public static RecordValues SetTarget(uint netId) => new()
    {
        ["netId"] = FieldValue.Of(netId),
    };

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
        _current = this;
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
        _links.Add(link);
        return link;
    }

    private readonly List<FakeLink> _links = [];

    /// <summary>
    /// The harness whose clients <see cref="Until"/> keeps alive, or null between fixtures.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A static because <see cref="Until"/> is one: a wait for a condition reads nicely as a free function and every wait in every check has to keep the
    /// clients alive, so making it an instance method would mean every call site carrying the harness for that one reason.
    /// </para>
    /// <para>
    /// <b>Set in the constructor and cleared by <see cref="Release"/>, which every fixture calls from its teardown.</b> It used to be assigned in
    /// <c>Connect</c> and never cleared, which worked only because no case happened to call <see cref="Until"/> before its own <c>Connect</c>. The first one
    /// that did would have kept alive a DISPOSED simulation from the previous fixture, and the exception would have surfaced from inside a
    /// <c>SpinWait.SpinUntil</c> predicate as a failure of whatever that case was actually waiting for.
    /// </para>
    /// </remarks>
    private static SessionHarness _current;

    /// <summary>
    /// Stops <see cref="Until"/> keeping this harness's clients alive. Every fixture that builds one calls this from its teardown.
    /// </summary>
    /// <remarks>Idempotent, and it only clears the static when this harness is the one holding it — so an out-of-order teardown cannot unhook a live one.</remarks>
    public void Release()
    {
        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }

    /// <summary>Waits until the runtime has ticked past where it is now.</summary>
    /// <param name="count">How many ticks.</param>
    public void Ticks(int count)
    {
        var target = _sim.Runtime.CurrentTickNumber + count;
        Assert.That(SpinWait.SpinUntil(
            () =>
            {
                Keepalive();
                return _sim.Runtime.CurrentTickNumber >= target;
            },
            TimeSpan.FromSeconds(10)), Is.True, "the runtime stopped ticking");
    }

    /// <summary>
    /// Sends a <c>PING</c> on every connected link, which is what stops the server closing them.
    /// </summary>
    /// <remarks>
    /// <b>A real client pings; a test that does not gets a correct 4001 and looks like a bug in what it was testing.</b> The frame assembler closes a session
    /// it has not heard from for <c>_silenceBoundTicks</c> with <c>NoAcknowledgement</c> — "a client that has stopped talking is gone" — so an otherwise
    /// passing case that waits more than about a second sees its session released, its player handed back to the simulation, and its assertions fail against
    /// state that <c>PlayerThink</c> has resumed writing. That cost an afternoon's worth of the wrong hypothesis once, so the keepalive is inside the waits
    /// rather than left to each case to remember.
    /// </remarks>
    public void Keepalive()
    {
        var tick = (uint)Math.Max(0, _sim.Runtime.CurrentTickNumber);
        for (var i = 0; i < _links.Count; i++)
        {
            var link = _links[i];
            if (link.Closed == null)
            {
                link.Send(ClientSays.Ping(tick, tick));
            }
        }
    }

    /// <summary>Waits for a condition the tick or the send pump makes true, or fails.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="what">What was being waited for, for the failure message.</param>
    public static void Until(Func<bool> condition, string what)
        => Assert.That(
            SpinWait.SpinUntil(
                () =>
                {
                    _current?.Keepalive();
                    return condition();
                },
                TimeSpan.FromSeconds(10)),
            Is.True,
            $"timed out waiting for {what}");
}
