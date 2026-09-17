using System;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// What the engine needs from the replication runtime to speak to one client: the catalog, the session table and where the tick is.
/// </summary>
/// <remarks>
/// <para>
/// <b>A seam, not a layer.</b> <c>SubscriptionsRuntime</c> (P1-03) is the single object that owns the compiled plan, the catalog bytes, the catalog hash, the
/// session table and the pools; it implements this interface and nothing else does in production. It is declared here rather than there so the connection and
/// its tests can be built and verified before that object exists, and so the connection's dependency on the runtime is exactly this and visibly so.
/// </para>
/// <para>
/// Everything here is read from a <b>transport thread</b> and must therefore be either immutable after <c>Start</c> (the catalog, its hash, the metric flag) or
/// published for cross-thread reading (the tick counters). None of it is a session row — writing those is the tick's, which is rule <c>SUB-05</c>.
/// </para>
/// </remarks>
internal interface ISubscriptionsHost
{
    /// <summary>The declared session kinds and the application's admission hook.</summary>
    SubscriptionsSessions Sessions { get; }

    /// <summary>Where a session lives once it is admitted.</summary>
    SessionTable SessionTable { get; }

    /// <summary>
    /// The canonical catalog JSON, built once at <c>Start</c> and never mutated, or <see langword="null"/> before a catalog exists.
    /// </summary>
    byte[] CatalogJson { get; }

    /// <summary>The catalog's FNV-1a 64 digest. Zero means "no digest", and a catalog that digests to zero is always sent (03 § 4).</summary>
    ulong CatalogHash { get; }

    /// <summary>Whether the catalog declares at least one metric, which is what the <c>STATS</c> capability is granted on (03 § 3).</summary>
    bool HasMetrics { get; }

    /// <summary>
    /// Whether replication is running and would admit a client at all. <see langword="false"/> on a runtime whose application declared no subscriptions, and
    /// on one that is shutting down.
    /// </summary>
    /// <remarks>
    /// It is what turns <see cref="ISubscriptionAcceptor.Accept"/>'s documented <see langword="null"/> — "replication is not running, or the runtime is
    /// stopping" — into something the acceptor can decide. Without it, a transport started against a declaration-free runtime would reach a null session table
    /// on the first <c>HELLO</c>: a <see cref="NullReferenceException"/> on a network thread, for a configuration mistake.
    /// </remarks>
    bool IsAccepting { get; }

    /// <summary>The tick the server is on, as <c>WELCOME</c> and <c>PONG</c> report it.</summary>
    uint CurrentTick { get; }

    /// <summary>The current tick period in microseconds, which a client turns into its tick-to-time map.</summary>
    uint TickPeriodUs { get; }

    /// <summary>
    /// How far into the current tick the server is, in microseconds. <c>PONG</c> carries it so a client can place a round trip inside the tick.
    /// </summary>
    uint MicrosecondsIntoTick { get; }

    /// <summary>
    /// Hands a validated <c>COMMANDS</c> message to ingress.
    /// </summary>
    /// <param name="session">Whose commands they are.</param>
    /// <param name="message">The whole message, type byte included. Borrowed; the ring copies what it keeps.</param>
    /// <remarks>
    /// <para>
    /// The host validates the whole message and frames each command it carries into that session's ingress ring, for the Engine-Pre drain to turn into the
    /// tick's typed buffers (P1-05). The bytes are not retained: what survives the call is what the ring copied.
    /// </para>
    /// <para>
    /// <b>It may throw <see cref="WireFormatException"/>, and that is the contract rather than a leak.</b> The decode is the protocol's, so the classification
    /// of a malformed message — 1002 for framing, 1007 for a payload — is the protocol's too, and <see cref="SubscriptionConnection.OnMessage"/> already turns
    /// exactly that exception into exactly that close code. A host that swallowed it would leave a malformed client connected and sending.
    /// </para>
    /// </remarks>
    void OnCommands(SessionId session, ReadOnlySpan<byte> message);
}

/// <summary>Where a connection is in the protocol.</summary>
internal enum SubscriptionConnectionState
{
    /// <summary>Accepted, nothing received. The only message that may arrive is <c>HELLO</c>, within five seconds.</summary>
    AwaitingHello = 0,

    /// <summary>Admitted and told so. <c>COMMANDS</c>, <c>PING</c> and <c>BYE</c> are the whole client vocabulary from here (03 § 3).</summary>
    Open = 1,

    /// <summary>Over. Terminal: nothing is sent, nothing is decoded, and the session row has been asked to close.</summary>
    Closed = 2,
}

/// <summary>
/// The engine's half of one connection: the handshake, the message caps, the close codes and the session behind them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The state machine is three states and every edge carries a close code.</b>
/// </para>
/// <list type="table">
/// <listheader><term>From</term><description>On, and to</description></listheader>
/// <item><term><see cref="SubscriptionConnectionState.AwaitingHello"/></term><description>
/// a well-formed <c>HELLO</c> from a declared kind the hook admits, and a free slot → <c>WELCOME</c> → <see cref="SubscriptionConnectionState.Open"/>.
/// Five seconds of silence → 4002. A message over 16 KiB → 1009, <b>before</b> it is decoded. Any other message type → 1002. A malformed body → 1007. A
/// protocol-major mismatch → 1002 with <b>no</b> <c>KICK</c> (03 § 10: a major-N client cannot be assumed to parse major 2's framing). An undeclared kind →
/// 4003, decided before the application's hook runs. A hook refusal → its own 4100-4999 code. A full table → 1013.
/// </description></item>
/// <item><term><see cref="SubscriptionConnectionState.Open"/></term><description>
/// <c>PING</c> → <c>PONG</c>, staying open. <c>COMMANDS</c> → ingress, staying open. <c>BYE</c> → a clean close, including the <c>BYE 4004</c> a browser sends
/// when it must refuse the stream for a fault it cannot name in a close frame. A message over the session's <c>clientMessageBytes</c> → 1009 before decoding.
/// A second <c>HELLO</c>, or an unknown type → 1002. A malformed body → 1007.
/// </description></item>
/// <item><term>any</term><description>the link ending → <see cref="SubscriptionConnectionState.Closed"/>, and the session row is asked to close.</description>
/// </item>
/// </list>
/// <para>
/// <b>It writes no session state.</b> Everything this type does to the session table is on rule <c>SUB-05</c>'s allow-list: lease a slot, fill a row nothing
/// else can reach yet and publish it with one release store, and afterwards only the two interlocked words — the pending-close request and the in-flight send
/// counter. The tick owns every other field of every published row, which is what lets replication stages read them with no synchronization at all.
/// </para>
/// <para>
/// <b>One lock, and it is not for the hot path.</b> Messages arrive sequentially per connection by the transport's contract, but the <c>HELLO</c> deadline
/// fires on a timer thread and <see cref="OnClosed"/> arrives from whichever thread noticed the link end, so the three can genuinely race. The lock is taken
/// once per control message and once per close — a handful of times per connection per second — and never covers a wait: a send is <i>started</i> under it and
/// awaited outside.
/// </para>
/// </remarks>
internal sealed class SubscriptionConnection : ISubscriptionConnection, IDisposable
{
    /// <summary>Header bytes of a <c>WELCOME</c> before its catalog: the type byte, the fixed fields, and the catalog length's widest varint.</summary>
    private const int WelcomeHeaderBytes = 1 + 2 + 2 + 4 + 4 + 16 + 4 + 4 + 8 + 5;

    /// <summary>A <c>PONG</c>: the type byte and three <c>u32</c>.</summary>
    private const int PongBytes = 1 + 4 + 4 + 4;

    /// <summary>A <c>KICK</c>: the type byte, the code, and a reason with its length prefix.</summary>
    private const int KickBytes = 1 + 2 + 1 + ProtocolConstants.KickReasonMaxBytes;

    private readonly object _gate = new();
    private readonly ISubscriptionsHost _host;
    private readonly ISubscriptionLink _link;
    private readonly LinkInfo _info;

    private Timer _helloTimer;
    private SubscriptionConnectionState _state;
    private SessionId _session;
    private int _clientMessageBytes;
    private Capabilities _capsGranted;
    private ushort _closeCode;
    private ushort _clientByeCode;
    private uint _lastAppliedTick;
    private bool _catalogSent;
    private bool _disposed;

    /// <summary>
    /// Adopts a link and starts the <c>HELLO</c> deadline.
    /// </summary>
    /// <param name="host">The replication runtime.</param>
    /// <param name="link">The transport's link.</param>
    /// <param name="info">What the transport knows about the peer.</param>
    /// <param name="helloTimeout">
    /// How long to wait for <c>HELLO</c>. Defaults to <see cref="ProtocolConstants.HelloTimeoutMs"/>;
    /// <see cref="Timeout.InfiniteTimeSpan"/> arms no timer, which is how a test drives <see cref="OnHelloDeadline"/> itself instead of waiting on a clock.
    /// </param>
    public SubscriptionConnection(ISubscriptionsHost host, ISubscriptionLink link, in LinkInfo info, TimeSpan? helloTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(link);

        _host = host;
        _link = link;
        _info = info;
        _state = SubscriptionConnectionState.AwaitingHello;
        _session = SessionId.None;
        _clientMessageBytes = ProtocolConstants.HelloMaxBytes;

        var timeout = helloTimeout ?? TimeSpan.FromMilliseconds(ProtocolConstants.HelloTimeoutMs);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            // The callback runs on a pool thread and takes the same lock every other entry point does, so a deadline that fires while a HELLO is being
            // decoded waits for it and then finds the state already past AwaitingHello.
            _helloTimer = new Timer(static s => ((SubscriptionConnection)s).OnHelloDeadline(), this, timeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Where the connection is in the protocol.</summary>
    public SubscriptionConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The session this connection was admitted as, or <see cref="SessionId.None"/> before <c>WELCOME</c>.</summary>
    public SessionId Session
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    /// <summary>The capabilities granted in <c>WELCOME</c>: requested ∩ known ∩ authorized (03 § 3).</summary>
    public Capabilities CapsGranted
    {
        get
        {
            lock (_gate)
            {
                return _capsGranted;
            }
        }
    }

    /// <summary>The code this connection was closed with, or zero while it is live.</summary>
    public ushort CloseCode
    {
        get
        {
            lock (_gate)
            {
                return _closeCode;
            }
        }
    }

    /// <summary>
    /// The code the client's own <c>BYE</c> carried, or zero when it sent none.
    /// </summary>
    /// <remarks>
    /// 1000 is an ordinary leave. <see cref="CloseCodes.ClientRefusedTheStream"/> (4004) is the one that carries information: a browser may not send 1002,
    /// 1007 or 1009 — they are reserved for an endpoint's own use — so a client that must refuse the stream for one of those says 4004 and closes with 1000
    /// (03 § 10). The session still ends cleanly; the code is what tells an operator the client found the stream unreadable rather than simply left.
    /// </remarks>
    public ushort ClientByeCode
    {
        get
        {
            lock (_gate)
            {
                return _clientByeCode;
            }
        }
    }

    /// <summary>Whether <c>WELCOME</c> carried the catalog, or skipped it because the client's hash already matched (03 § 10).</summary>
    public bool CatalogSent
    {
        get
        {
            lock (_gate)
            {
                return _catalogSent;
            }
        }
    }

    /// <summary>The newest tick the client has told us it applied, through <c>PING</c>. Zero until the first one arrives.</summary>
    /// <remarks>
    /// Kept on the connection rather than on the session row deliberately: it is written by a transport thread, and <c>SUB-05</c> allows a transport thread
    /// exactly two interlocked words of a published row. The lag skip (P1-14b) reads it from here.
    /// </remarks>
    public uint LastAppliedTick => Volatile.Read(ref _lastAppliedTick);

    /// <inheritdoc />
    public void OnMessage(ReadOnlySpan<byte> message)
    {
        lock (_gate)
        {
            if (_state == SubscriptionConnectionState.Closed)
            {
                return;
            }

            // The cap is checked against the FRAMED length, before a byte of the body is read: that is what 1009 means, and it is why the first message has a
            // protocol constant of its own — a client has to be able to send a token before it has been told any session's limit (03 § 12 W22, W24).
            if (message.Length > _clientMessageBytes)
            {
                Refuse(CloseCodes.MessageTooBig, "message above its limit");
                return;
            }

            if (message.IsEmpty)
            {
                Refuse(CloseCodes.ProtocolError, "empty message");
                return;
            }

            try
            {
                if (_state == SubscriptionConnectionState.AwaitingHello)
                {
                    HandleHello(message);
                }
                else
                {
                    HandleOpen(message);
                }
            }
            catch (WireFormatException e)
            {
                // The decoder already classified it: 1002 for framing, 1007 for a malformed payload. Re-deciding here would be a second opinion on the wire.
                Refuse(e.CloseCode, e.Message);
            }
        }
    }

    /// <inheritdoc />
    public void OnClosed(ushort code, Exception error)
    {
        lock (_gate)
        {
            StopHelloTimer();

            if (_state == SubscriptionConnectionState.Closed)
            {
                // An engine-initiated close already asked the row to close and recorded the code; the link merely confirming it must not overwrite either.
                return;
            }

            _state = SubscriptionConnectionState.Closed;
            _closeCode = code;
            RequestSessionClose(error != null || code != CloseCodes.Normal ? SessionCloseReason.LinkLost : SessionCloseReason.ClientLeft, code);
        }
    }

    /// <summary>
    /// Ends the session from the engine's side: a <c>KICK</c> carrying <paramref name="code"/>, then the link.
    /// </summary>
    /// <param name="code">The close code.</param>
    /// <param name="reason">Why, truncated by the encoder to <see cref="ProtocolConstants.KickReasonMaxBytes"/>.</param>
    /// <param name="sessionReason">What the tick records for the <c>Closed</c> event.</param>
    public void Kick(ushort code, string reason, SessionCloseReason sessionReason = SessionCloseReason.Kicked)
    {
        lock (_gate)
        {
            if (_state == SubscriptionConnectionState.Closed)
            {
                return;
            }

            CloseWithKick(code, reason, sessionReason);
        }
    }

    /// <summary>
    /// Fires the <c>HELLO</c> deadline. Internal so a test can reach it without waiting on a clock.
    /// </summary>
    public void OnHelloDeadline()
    {
        lock (_gate)
        {
            if (_state != SubscriptionConnectionState.AwaitingHello)
            {
                return;
            }

            CloseWithKick(CloseCodes.HelloTimeout, "no HELLO within the handshake deadline", SessionCloseReason.ProtocolError);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopHelloTimer();
            _state = SubscriptionConnectionState.Closed;
        }
    }

    // ── the handshake ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void HandleHello(ReadOnlySpan<byte> message)
    {
        if (message[0] != MessageTypes.Hello)
        {
            Refuse(CloseCodes.ProtocolError, "the first message must be HELLO");
            return;
        }

        var hello = HelloMessage.Parse(message);

        if (hello.Major != ProtocolConstants.Major)
        {
            // Never a KICK: a major-N client cannot be assumed to parse major 2's framing, so answering with one would oblige every future major to speak this
            // one's (03 § 10, § 12 W31). The transport's subprotocol or preamble is where a major is meant to be settled; this is the backstop.
            CloseWithoutKick(CloseCodes.ProtocolError, "protocol major mismatch");
            return;
        }

        StopHelloTimer();

        var request = new AdmissionRequest(hello.Kind, hello.Token, (uint)hello.Caps, hello.HelloPayload, null, null, _info.Remote, _info.Transport);
        if (!_host.SessionTable.TryAdmit(_host.Sessions, request, out var session, out var code, out var reason))
        {
            // TryAdmit checks the declared kind before the application's hook and capacity after it, so an unknown name never reaches application code and a
            // refusal costs no slot (01 § 3).
            CloseWithKick(code, reason, SessionCloseReason.Kicked);
            return;
        }

        _session = session;

        var row = _host.SessionTable.Row(session);
        _clientMessageBytes = row.ClientMessageBytes;
        _capsGranted = GrantCaps(hello.Caps, row.Flags);

        SendWelcome(hello, (ushort)Math.Min(hello.Minor, ProtocolConstants.Minor));
    }

    /// <summary>
    /// Narrows what the client asked for to what this server has and this session may have.
    /// </summary>
    /// <param name="requested">The bits in <c>HELLO</c>.</param>
    /// <param name="rowFlags">The admitted session's flags.</param>
    /// <returns>requested ∩ known ∩ authorized. Unknown bits are dropped rather than refused (03 § 3).</returns>
    /// <remarks>
    /// The subset is not a nicety: a granted bit the client did not request is a server bug the client closes with 1002, because it would mean the server is
    /// about to emit a block the client never agreed to decode.
    /// </remarks>
    private Capabilities GrantCaps(Capabilities requested, byte rowFlags)
    {
        const Capabilities known = Capabilities.Stats | Capabilities.Debug;

        var authorized = Capabilities.None;
        if (_host.HasMetrics)
        {
            authorized |= Capabilities.Stats;
        }

        if ((rowFlags & SessionRowFlags.AllowDebug) != 0)
        {
            authorized |= Capabilities.Debug;
        }

        return requested & known & authorized;
    }

    private void SendWelcome(HelloMessage hello, ushort minor)
    {
        var hash = _host.CatalogHash;
        byte[] catalog = _host.CatalogJson ?? [];

        // The skip is what keeps a churn storm from resending five kilobytes per reconnect (03 § 10). A catalog that digests to zero is always sent: zero is
        // also what a client with no catalog presents, so the two would be indistinguishable.
        var skip = hash != 0 && hello.ClientCatalogHash == hash;
        _catalogSent = !skip;
        if (skip)
        {
            catalog = [];
        }

        var welcome = new WelcomeMessage
        {
            Major = ProtocolConstants.Major,
            Minor = minor,
            CapsGranted = _capsGranted,
            SessionId = _session.Value,
            ResumeToken = null,
            Tick = _host.CurrentTick,
            TickPeriodUs = _host.TickPeriodUs,
            CatalogHash = hash,
            CatalogJson = catalog,
        };

        var capacity = WelcomeHeaderBytes + catalog.Length;
        if (capacity > ProtocolConstants.WelcomeMaxBytes)
        {
            // The one inbound message a client cannot bound from limits.frameBytes, because those limits arrive inside it (03 § 10). Over the cap it would be
            // refused by every conforming client, so the honest answer is a server error rather than bytes nobody can read.
            CloseWithKick(CloseCodes.InternalError, "the catalog does not fit a WELCOME", SessionCloseReason.InternalError);
            return;
        }

        var buffer = NativeFrameMemoryManager.Allocate(capacity);
        int length;
        try
        {
            var writer = new WireWriter(buffer.GetSpan());
            welcome.Write(ref writer);
            length = writer.Position;
        }
        catch
        {
            buffer.Release();
            throw;
        }

        _state = SubscriptionConnectionState.Open;
        Send(buffer, length);
    }

    // ── the open session ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void HandleOpen(ReadOnlySpan<byte> message)
    {
        switch (message[0])
        {
            case MessageTypes.Ping:
                var ping = PingMessage.Parse(message);
                Volatile.Write(ref _lastAppliedTick, ping.LastAppliedTick);
                SendPong(ping.ClientMs);
                break;

            case MessageTypes.Bye:
                var bye = ByeMessage.Parse(message);
                _clientByeCode = bye.Code;

                // Clean, whatever the code says: the client is leaving, and 4004 only tells us why it had to (03 § 10). Answering a goodbye with a KICK would
                // race the client's own close and log a protocol error where there was none.
                CloseWithoutKick(CloseCodes.Normal, null, SessionCloseReason.ClientLeft);
                break;

            case MessageTypes.Commands:
                _host.OnCommands(_session, message);
                break;

            default:
                // A second HELLO lands here too, which is right: it is a known type in the wrong state, and 1002 is what the protocol gives both (03 § 12 W24).
                Refuse(CloseCodes.ProtocolError, "unknown or out-of-state message type");
                break;
        }
    }

    private void SendPong(uint clientMs)
    {
        var pong = new PongMessage(clientMs, _host.CurrentTick, _host.MicrosecondsIntoTick);
        var buffer = NativeFrameMemoryManager.Allocate(PongBytes);
        int length;
        try
        {
            var writer = new WireWriter(buffer.GetSpan());
            pong.Write(ref writer);
            length = writer.Position;
        }
        catch
        {
            buffer.Release();
            throw;
        }

        Send(buffer, length);
    }

    // ── closing ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Refuses the message being decoded: the protocol's own close code, with a <c>KICK</c> so a client on TCP learns why.</summary>
    /// <param name="code">The close code.</param>
    /// <param name="reason">Why.</param>
    private void Refuse(ushort code, string reason) => CloseWithKick(code, reason, SessionCloseReason.ProtocolError);

    private void CloseWithKick(ushort code, string reason, SessionCloseReason sessionReason)
    {
        var kick = new KickMessage(code, reason ?? string.Empty);
        var buffer = NativeFrameMemoryManager.Allocate(KickBytes);
        var length = 0;
        try
        {
            var writer = new WireWriter(buffer.GetSpan());
            kick.Write(ref writer);
            length = writer.Position;
        }
        catch
        {
            buffer.Release();
            buffer = null;
        }

        Finish(code, sessionReason);

        if (buffer != null)
        {
            Send(buffer, length);
        }

        // After the KICK, so a link that orders its writes delivers the reason before the close (03 § 3: KICK, then close; on TCP, KICK then FIN).
        _link.Close(code, KickMessage.TruncateUtf8(reason ?? string.Empty, ProtocolConstants.KickReasonMaxBytes));
    }

    private void CloseWithoutKick(ushort code, string reason, SessionCloseReason sessionReason = SessionCloseReason.ProtocolError)
    {
        Finish(code, sessionReason);
        _link.Close(code, reason);
    }

    private void Finish(ushort code, SessionCloseReason sessionReason)
    {
        StopHelloTimer();
        _state = SubscriptionConnectionState.Closed;
        _closeCode = code;
        RequestSessionClose(sessionReason, code);
    }

    /// <summary>
    /// Asks the tick to close this connection's session.
    /// </summary>
    /// <param name="reason">Why.</param>
    /// <param name="code">The close code the client was given.</param>
    /// <remarks>
    /// One interlocked word, and nothing else — no reason string, no row field. That is <c>SUB-05</c>'s allow-list: a transport thread publishes a number
    /// atomically, and the tick is what turns it into a close and a <c>Closed</c> event an application can act on.
    /// </remarks>
    private void RequestSessionClose(SessionCloseReason reason, ushort code)
    {
        if (_session.IsValid)
        {
            _host.SessionTable.RequestClose(_session, reason, code);
        }
    }

    private void StopHelloTimer()
    {
        var timer = _helloTimer;
        _helloTimer = null;
        timer?.Dispose();
    }

    // ── sending ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hands one control message to the link and releases its buffer when the link is done with it.
    /// </summary>
    /// <param name="buffer">The message's native memory. Owned here, freed when the send completes.</param>
    /// <param name="length">How many bytes of it are the message.</param>
    /// <remarks>
    /// A buffer per control message, rather than one reused per connection: there are a handful of them in a session's life — one <c>WELCOME</c>, a
    /// <c>PONG</c> every quarter second, one <c>KICK</c> — and a fresh allocation makes the lifetime trivially correct against an asynchronous send, where a
    /// shared buffer would need a second one to write the next message into while the first is still on the wire. Frames, which are the part that matters for
    /// throughput, come from the frame pool instead and re-point a single view (P1-14a).
    /// </remarks>
    private void Send(NativeFrameMemoryManager buffer, int length)
    {
        ValueTask task;
        try
        {
            task = _link.SendAsync(buffer.Memory[..length], CancellationToken.None);
        }
        catch (Exception e)
        {
            buffer.Release();
            OnSendFailed(e);
            return;
        }

        if (task.IsCompletedSuccessfully)
        {
            buffer.Release();
            return;
        }

        _ = ObserveSendAsync(task, buffer);
    }

    private async Task ObserveSendAsync(ValueTask task, NativeFrameMemoryManager buffer)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            OnSendFailed(e);
        }
        finally
        {
            // Only here, never earlier: the link borrowed these bytes for exactly the task's lifetime, so freeing them before it completes is a read of freed
            // native memory on whichever thread the socket finishes on.
            buffer.Release();
        }
    }

    /// <summary>
    /// A send that failed took the link with it; there is nothing left to tell the client, so the session is asked to close and the link is closed.
    /// </summary>
    /// <param name="error">What went wrong.</param>
    private void OnSendFailed(Exception error)
    {
        lock (_gate)
        {
            if (_state == SubscriptionConnectionState.Closed)
            {
                return;
            }

            Finish(CloseCodes.Normal, SessionCloseReason.LinkLost);
        }

        _link.Close(CloseCodes.Normal, error?.GetType().Name);
    }
}

/// <summary>
/// The engine's <see cref="ISubscriptionAcceptor"/>: every transport reaches replication through this one object.
/// </summary>
/// <remarks>
/// It holds nothing but the host, so a transport can be started before any client exists and a second transport — TCP beside WebSocket — shares the same
/// acceptor rather than building a second idea of what a connection is.
/// </remarks>
internal sealed class SubscriptionAcceptor : ISubscriptionAcceptor
{
    private readonly ISubscriptionsHost _host;

    /// <summary>Creates the acceptor.</summary>
    /// <param name="host">The replication runtime every accepted connection speaks to.</param>
    public SubscriptionAcceptor(ISubscriptionsHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The one thing decided here rather than inside the connection: a host that is not accepting has no session table to admit anyone to, so there is nothing
    /// to tell the client and the link is the transport's to close.
    /// </remarks>
    public ISubscriptionConnection Accept(ISubscriptionLink link, in LinkInfo info)
        => _host.IsAccepting ? new SubscriptionConnection(_host, link, info) : null;
}
