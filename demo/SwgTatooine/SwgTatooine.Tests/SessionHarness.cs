using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SwgTatooine.Accounts;
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
[Category("Integration")]
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

    /// <summary>The resume token the <c>WELCOME</c> carried, or <see langword="null"/> when the server issues none (A13).</summary>
    /// <remarks>
    /// Off the wire, for the same reason <see cref="Session"/> is: this is the whole of what a client keeps in order to come back, so a test that read the
    /// server's own record instead could pass while the client held something else.
    /// </remarks>
    public byte[] ResumeToken
    {
        get
        {
            foreach (var message in Sent)
            {
                if (message.Length > 0 && message[0] == MessageTypes.Welcome)
                {
                    var token = WelcomeMessage.Parse(message).ResumeToken;
                    foreach (var b in token)
                    {
                        if (b != 0)
                        {
                            return token;
                        }
                    }

                    return null;   // all-zero is how the wire says there is no resume
                }
            }

            return null;
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

    /// <summary>
    /// How many messages had been sent when the link was closed, or -1 while it is open.
    /// </summary>
    /// <remarks>
    /// <b>The ordinal is what makes "KICK then close" assertable.</b> A case that checks only that a <c>KICK</c> arrived and that the close code matches is
    /// satisfied by a transport that wrote the close first — which is the opposite of what the protocol requires (<c>03 § 3</c>: <c>KICK</c>, then close).
    /// Recording the count at the moment of the close turns the order into a number.
    /// </remarks>
    public int SentWhenClosed { get; private set; } = -1;

    /// <summary>The index of the first message of a type, or -1.</summary>
    /// <param name="type">Its <see cref="MessageTypes"/> byte.</param>
    /// <returns>The index.</returns>
    public int IndexOf(byte type)
    {
        lock (_sent)
        {
            for (var i = 0; i < _sent.Count; i++)
            {
                if (_sent[i].Length > 0 && _sent[i][0] == type)
                {
                    return i;
                }
            }
        }

        return -1;
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
            SentWhenClosed = _sent.Count;
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
    /// <param name="changePhase">
    /// How far into the tick the intent changed, in 255ths — <c>MoveDir.ChangePhase</c>. Defaults to 0, which is what a client that does not measure it
    /// sends and what every case written before A14b means.
    /// </param>
    /// <returns>The values.</returns>
    public static RecordValues MoveDir(float heading, byte speedClass, byte changePhase = 0) => new()
    {
        ["heading"] = FieldValue.Of(heading),
        ["speedClass"] = FieldValue.Of(speedClass),
        ["changePhase"] = FieldValue.Of(changePhase),
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
    /// <param name="token">
    /// The credential presented. A <c>player</c> needs a real one since A3 — <see cref="SessionHarness.Connect"/> mints it — and a
    /// <c>god</c> session is anonymous, so anything at all does there.
    /// </param>
    /// <param name="resumeToken">The resume token a reconnecting client presents (A13), or <see langword="null"/> for a fresh connection.</param>
    /// <returns>The message.</returns>
    public static byte[] Hello(string kind, string token = "opaque", byte[] resumeToken = null)
    {
        var hello = new HelloMessage
        {
            Major = ProtocolConstants.Major,
            Minor = ProtocolConstants.Minor,
            Caps = Capabilities.None,
            Kind = kind,
            Token = token,
            ResumeToken = resumeToken,
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

    /// <summary>
    /// Hands out a fresh synthetic account id, so two <c>player</c> links are two different people.
    /// </summary>
    /// <returns>An account id no other link in this process has used.</returns>
    /// <remarks>
    /// <para>
    /// <b>DISTINCT per link since A5, and sharing one was a real failure.</b> Every player session used to be minted a token for
    /// account 1, which was harmless while a session was handed a pooled player — and became visible the moment a login spawned an
    /// avatar, because one account may only be in the world once (<c>AccountAlreadyPlayingCloseCode</c>). Two sessions sharing an
    /// account meant the second was refused, and a case asking for two players waited for ever.
    /// </para>
    /// <para>
    /// <b>The account does not have to exist, and that is a property of A3 rather than a shortcut.</b> <c>Admit</c> validates the
    /// token's signature and expiry and carries the account id through as <c>AppData</c>; it never resolves the entity, because the
    /// hook runs on the transport thread with no transaction to resolve it in. A5's bind reads the account's CHARACTER through an
    /// index and creates one when there is none, which works for an account id nothing else has heard of. So a case that only needs
    /// a session gets one without also needing a signup — and a case that wants the real thing passes its own account.
    /// </para>
    /// <para>
    /// Process-wide rather than per harness, so two fixtures in one process cannot collide on an id either.
    /// </para>
    /// </remarks>
    private static EntityId NextSyntheticAccount()
        => EntityId.FromRawValue((ulong)Interlocked.Increment(ref _syntheticAccounts));

    private static long _syntheticAccounts;

    /// <summary>Connects one client and completes its handshake.</summary>
    /// <param name="kind">The session kind: <c>god</c> or <c>player</c>.</param>
    /// <param name="account">
    /// The account a <c>player</c>'s token names; a fresh one from <see cref="NextSyntheticAccount"/> when not given. Ignored for
    /// <c>god</c>, which is anonymous by design.
    /// </param>
    /// <returns>The link, whose <see cref="FakeLink.Sent"/> is what the client received.</returns>
    public FakeLink Connect(string kind, EntityId? account = null)
    {
        var token = string.Equals(kind, TatooineReplication.PlayerKind, StringComparison.Ordinal)
            ? AuthToken.Mint(account ?? NextSyntheticAccount())
            : "opaque";

        return ConnectWithToken(kind, token);
    }

    /// <summary>
    /// Connects one client presenting a token this harness did not mint — a rubbish one, an expired one, or none.
    /// </summary>
    /// <param name="kind">The session kind.</param>
    /// <param name="token">Exactly what the client presents.</param>
    /// <returns>The link. For a refused admission it carries the CLOSE the server sent, which is what the case asserts on.</returns>
    public FakeLink ConnectWithToken(string kind, string token) => ConnectWithToken(kind, token, null);

    /// <summary>
    /// Connects one client that presents a resume token as well as its credential — a reconnect, as the SDK performs it (A13).
    /// </summary>
    /// <param name="kind">The session kind.</param>
    /// <param name="account">The account the credential names; a fresh one when not given.</param>
    /// <param name="resumeToken">The token the previous session's <c>WELCOME</c> carried (<see cref="FakeLink.ResumeToken"/>).</param>
    /// <returns>The link.</returns>
    /// <remarks>
    /// <b>The credential is minted again, and that is what a real reconnect does too.</b> A resume token names a session, never an account: the engine
    /// admits the client through the ordinary hook first and only then looks at the token, so a reconnect proves who it is exactly as a fresh login does.
    /// </remarks>
    public FakeLink ConnectResuming(string kind, EntityId account, byte[] resumeToken)
        => ConnectWithToken(kind, AuthToken.Mint(account), resumeToken);

    /// <summary>Connects one client with a credential and, optionally, a resume token.</summary>
    /// <param name="kind">The session kind.</param>
    /// <param name="token">Exactly what the client presents as its credential.</param>
    /// <param name="resumeToken">The resume token, or <see langword="null"/>.</param>
    /// <returns>The link.</returns>
    public FakeLink ConnectWithToken(string kind, string token, byte[] resumeToken)
    {
        var link = new FakeLink();
        var info = new LinkInfo { Transport = "fake", SubProtocol = ProtocolConstants.WebSocketSubprotocol };
        var connection = _acceptor.Accept(link, info);
        Assert.That(connection, Is.Not.Null, "the runtime declined the link outright");
        link.Connection = connection;
        connection.OnMessage(ClientSays.Hello(kind, token, resumeToken));
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
    /// <remarks>
    /// Idempotent, and it only clears the static when this harness is the one holding it — so an out-of-order teardown cannot unhook a live one.
    /// </remarks>
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
                ThrowIfTickAborted(_sim);
                Keepalive();
                return _sim.Runtime.CurrentTickNumber >= target;
            },
            TimeSpan.FromSeconds(10)), Is.True, "the runtime stopped ticking");
    }

    /// <summary>
    /// Throws the fault of an aborted tick, so a wait fails with the defect rather than with its own timeout.
    /// </summary>
    /// <param name="sim">The simulation being waited on.</param>
    /// <remarks>
    /// <b>Because a tick that aborts makes every wait in this file meaningless, and says so in the least useful way.</b> <c>AbortTickAndStop</c> is terminal:
    /// the engine stops committing, so no session possesses a player, no interior is released and no chat is delivered — and each of those cases fails ten
    /// seconds later reporting the thing it was waiting for. The exception that caused it reaches only the console, where a filtered test run never shows it.
    /// <para>
    /// Measured against the case that prompted this: an <c>[Index]</c> field on <c>Structure</c> made the engine refuse <c>EconomyTick</c>'s writable span
    /// (#1187) and aborted tick 0. The suite reported "timed out waiting for both sessions to possess a player" and nothing else, and the diagnosis took an
    /// hour. With this, the first wait throws the engine's own message, which names the rule, the component slot and the three APIs that would have been
    /// correct.
    /// </para>
    /// <para>Checked inside the spin rather than after it, so the failure is immediate instead of ten seconds late.</para>
    /// </remarks>
    private static void ThrowIfTickAborted(TatooineSim sim)
    {
        var abort = sim.TickAbort;
        if (abort == null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"the engine aborted tick {abort.Tick} in system '{abort.SystemName}' ({abort.Reason}), so it has stopped committing and this wait can never "
            + "finish. The abort is the failure; what this case was waiting for is a consequence of it.",
            abort.Fault);
    }

    /// <summary>How long one link waits between PINGs: the engine's default ping period, <c>1000 / SubscriptionsOptions.PingHz</c>.</summary>
    /// <remarks>
    /// <b>A keepalive that pings as fast as the wait spins is a flood, and the engine closes one.</b> It answers at most <c>4 x PingHz</c> PINGs a second
    /// and counts the refused ones towards SUB-27's abuse rule (#1025), so pinging on every <c>SpinWait.SpinUntil</c> predicate — which evaluates orders of
    /// magnitude faster than that — ends with 1008, the player handed back to the simulation, and the case failing on state <c>PlayerThink</c> has resumed
    /// writing. Exactly the failure the keepalive exists to prevent, arrived at from the other side.
    /// <para>
    /// One ping per this period is 4 a second against an answer cap of 16, and three inside the silence bound of <c>3 s / PingHz</c> = 750 ms, which is the
    /// deadline the ping is actually for. Both bounds move with <c>PingHz</c>, which the demo leaves at the engine's default of 4.
    /// </para>
    /// </remarks>
    private const long KeepalivePeriodMs = 250;

    private readonly Stopwatch _sinceStart = Stopwatch.StartNew();
    private long _nextKeepaliveMs;

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
        // Rate-limited, not sent on every call: see KeepalivePeriodMs. Wall clock rather than the tick number, because the cap the engine applies is per
        // second of real time and a paused or slow-ticking runtime is exactly when a case waits longest.
        var nowMs = _sinceStart.ElapsedMilliseconds;
        if (nowMs < _nextKeepaliveMs)
        {
            return;
        }

        _nextKeepaliveMs = nowMs + KeepalivePeriodMs;

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
                    if (_current != null)
                    {
                        ThrowIfTickAborted(_current._sim);
                        _current.Keepalive();
                    }

                    return condition();
                },
                TimeSpan.FromSeconds(10)),
            Is.True,
            $"timed out waiting for {what}");
}
