using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>
/// A connected Typhon session: the handshake, the receive loop, the ping the server's lag skip depends on, and the store the frames are applied to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decode is strictly in order and synchronous.</b> Frames are applied one at a time on the receive loop, in arrival order, and nothing asynchronous sits
/// between the socket and the store. Updates do not commute — an update applied before the enter it depends on is a different world, not a late one — and
/// this is not a hypothetical: SpacetimeDB's TypeScript SDK diverged silently when async decompression reordered frames ([05 § 1](../../../claude/design/Subscriptions/05-sdks.md)).
/// </para>
/// <para>
/// <b>PING is mandatory, not a keepalive.</b> It carries this client's newest applied tick, which is what the server's lag skip reads to decide whether
/// producing for this session is still worth doing; a session that stops pinging is closed 4001. The rate comes from the catalog when the server named one,
/// because the server is the side that knows what it expects.
/// </para>
/// <para>
/// <b>Reconnect is a decision about the close code, taken by <see cref="ReconnectPolicy"/>.</b> The client reconnects only where the code says the condition
/// can change; a protocol error or a rejected credential would produce the identical failure again, and retrying it is a hot loop wearing a network problem's
/// clothes.
/// </para>
/// </remarks>
public sealed class TyphonClient : IAsyncDisposable
{
    private readonly ClientOptions _options;
    private readonly Func<IClientTransport> _transportFactory;
    private readonly ReconnectPolicy _policy;
    private readonly CancellationTokenSource _stopping = new();

    private IClientTransport _transport;
    private FrameApplier _applier;
    private ushort _commandSeq;

    /// <summary>
    /// The code of the last <c>KICK</c> this connection received, or zero.
    /// </summary>
    /// <remarks>
    /// <b>TCP carries no close code, so without this every disconnect reads as 1001.</b> A FIN says the peer has gone and not why, which is why the protocol
    /// sends a <c>KICK</c> ahead of the close carrying the real code (03 § 3). Reading only the transport's code collapses "you stopped acknowledging" (4001,
    /// reconnect now), "the server is overloaded" (1013, back off) and "you spoke the protocol wrongly" (1002, do not come back) into one answer — and it is
    /// the reconnect policy that then decides wrongly.
    /// </remarks>
    private ushort _kickCode;
    private Task _receiveLoop;
    private Task _pingLoop;

    /// <summary>The managed id of the thread the receive loop is running a handler on, or zero when it is not running one.</summary>
    /// <remarks>
    /// Lets <see cref="DisposeAsync"/> tell a call from inside a handler the loop raised — <see cref="FrameApplied"/>, <see cref="Fault"/>,
    /// <see cref="Disconnected"/>, an event — from one made anywhere else: the first must not wait for the loop, because the loop is waiting for it. Only
    /// the loop's own thread can ever match it.
    /// </remarks>
    private int _dispatchThread;

    /// <summary>Builds a client for the options' endpoint.</summary>
    /// <param name="options">Where to connect and how to behave.</param>
    public TyphonClient(ClientOptions options)
        : this(options, null)
    {
    }

    /// <summary>Builds a client over a transport of the caller's choosing.</summary>
    /// <param name="options">Where to connect and how to behave.</param>
    /// <param name="transportFactory">
    /// Makes a fresh transport per attempt, or <see langword="null"/> to derive one from the endpoint's scheme. A factory rather than an instance because a
    /// reconnect needs a new one: a closed socket cannot be reopened, and reusing the object would hide that.
    /// </param>
    public TyphonClient(ClientOptions options, Func<IClientTransport> transportFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _transportFactory = transportFactory ?? (() => ForEndpoint(options));
        _policy = new ReconnectPolicy(options);
    }

    /// <summary>Raised when a session opens, with the catalog the server sent.</summary>
    public event Action<WelcomeMessage> Connected;

    /// <summary>Raised when a session ends, with the close code and whether the client will try again.</summary>
    public event Action<ushort, bool> Disconnected;

    /// <summary>Raised when the server sends a <c>KICK</c>, before the transport closes.</summary>
    public event Action<KickMessage> Kicked;

    /// <summary>
    /// Raised for a message the client could not apply — the session continues and the frame is lost — or for an exception a <see cref="FrameApplied"/>
    /// handler threw, whose frame did apply whole.
    /// </summary>
    public event Action<Exception> Fault;

    /// <summary>
    /// Raised after each <c>TICK</c> has been applied whole — its records, its events, then its leaves — and before the next one begins: the one point at
    /// which <see cref="Store"/> holds a complete frame, its per-frame change lists included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Synchronous, on the thread that applies frames</b> — the receive loop, or the caller of <see cref="Replay"/> — and the next frame waits for it.
    /// This is the window to copy out what another thread needs: <see cref="ArchetypeStore.Entered"/>, <see cref="ArchetypeStore.Updated"/> and
    /// <see cref="ArchetypeStore.Left"/> are cleared by the next frame, and every column under them is rewritten in place. No reference into the store
    /// may outlive the handler.
    /// </para>
    /// <para>
    /// <b>Copy, do not compute.</b> While the handler runs nothing is received and the <c>PING</c>s carry an older tick, which the server's lag skip reads
    /// as a client falling behind. Meshing, physics rebuilds and the like belong on the thread the copy is handed to.
    /// </para>
    /// <para>
    /// The argument is the store the frame was applied to: a <c>WELCOME</c> that carries a new catalog replaces <see cref="Store"/>, so a held reference
    /// can go stale. A frame whose apply throws raises <see cref="Fault"/> instead, never this.
    /// </para>
    /// <para>
    /// <b>A handler may end the session</b> with <see cref="CloseAsync"/> or <see cref="DisposeAsync"/>, blocking on either: no later frame is applied. An
    /// exception it throws is reported through <see cref="Fault"/> on the receive loop, and the session continues; under <see cref="Replay"/> it
    /// propagates to the caller.
    /// </para>
    /// </remarks>
    public event Action<WorldStore> FrameApplied;

    /// <summary>The decoded world, or <see langword="null"/> until the first <c>WELCOME</c> has been applied.</summary>
    public WorldStore Store { get; private set; }

    /// <summary>The compiled catalog this session negotiated.</summary>
    public CatalogPlan Plan { get; private set; }

    /// <summary>The optional work the server granted, which may be less than was asked for.</summary>
    public Capabilities CapsGranted { get; private set; }

    /// <summary>The session identifier the server assigned.</summary>
    public uint SessionId { get; private set; }

    /// <summary>The resume token from the last <c>WELCOME</c>, or <see langword="null"/>.</summary>
    public byte[] ResumeToken { get; private set; }

    /// <summary>The digest of the catalog this session holds, as the server reported it.</summary>
    public ulong CatalogHash { get; private set; }

    /// <summary>The newest tick this client has applied, which is what its <c>PING</c> carries.</summary>
    public uint LastAppliedTick => Store == null || Store.Tick < 0 ? 0 : (uint)Store.Tick;

    /// <summary>Whether a session is open right now.</summary>
    public bool IsConnected => _transport is { IsConnected: true } && Plan != null;

    /// <summary>How many messages have been received across every attempt.</summary>
    public long MessagesReceived { get; private set; }

    /// <summary>How many bytes those messages carried, as they came off the transport — the wire cost of this session.</summary>
    public long BytesReceived { get; private set; }

    /// <summary>How many times a session has been opened, including reconnects.</summary>
    public int Connects { get; private set; }

    /// <summary>
    /// Connects, completes the handshake and starts the receive and ping loops.
    /// </summary>
    /// <param name="ct">Cancels the connect.</param>
    /// <returns>The <c>WELCOME</c> the server answered with.</returns>
    public async Task<WelcomeMessage> ConnectAsync(CancellationToken ct = default)
    {
        var welcome = await HandshakeAsync(ct, onReceiveLoop: false).ConfigureAwait(false);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_stopping.Token), CancellationToken.None);

        // No loop when the host drives the cadence: see ClientOptions.PingHz. A thousand bots in one process would otherwise be a thousand timers.
        if (_options.PingHz > 0)
        {
            _pingLoop = Task.Run(() => PingLoopAsync(_stopping.Token), CancellationToken.None);
        }

        return welcome;
    }

    /// <summary>Applies a recorded session to this client's store, in order, with no socket involved.</summary>
    /// <param name="recording">The recording, whose first message must be the <c>WELCOME</c> that carries the catalog.</param>
    /// <returns>The store the recording produced.</returns>
    /// <remarks>
    /// The point of replay is that it uses the same decoder over the same bytes, so a recorded stream and a live one cannot disagree unless the decoder is
    /// non-deterministic. That is what makes "both replay a recorded stream to an identical store" a meaningful acceptance criterion rather than a tautology.
    /// </remarks>
    public WorldStore Replay(Recorder recording)
    {
        ArgumentNullException.ThrowIfNull(recording);

        foreach (var message in recording.Messages)
        {
            Dispatch(message);
        }

        return Store;
    }

    /// <summary>
    /// Sends one <c>PING</c> carrying this client's newest applied tick.
    /// </summary>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>The send.</returns>
    /// <remarks>
    /// Public so a host running many clients can drive the cadence from one timer rather than from one per client. A client with a non-zero
    /// <see cref="ClientOptions.PingHz"/> is already doing this on its own and does not need the call.
    /// </remarks>
    public async Task SendPingAsync(CancellationToken ct = default)
    {
        var transport = _transport;
        if (transport is { IsConnected: true })
        {
            await transport.SendAsync(Encode(new PingMessage((uint)Environment.TickCount, LastAppliedTick).Write), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends one command to the server.
    /// </summary>
    /// <param name="name">The command's catalog name — a built-in such as <c>ClientRegion</c>, or one the application declared.</param>
    /// <param name="values">The command's fields, by name.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>The sequence the command was sent under, which an <c>ACKS</c> rejection would name.</returns>
    /// <remarks>
    /// One command per message today. Batching several into one <c>COMMANDS</c> is what the wire is shaped for and what a real client should do per frame;
    /// nothing in Phase 1 sends enough commands for the difference to be measurable, and a batching API nobody exercises is a worse bet than an obvious one.
    /// </remarks>
    public async Task<ushort> SendCommandAsync(string name, RecordValues values, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(values);

        var plan = Plan?.CommandByName(name)
            ?? throw new InvalidOperationException($"the catalog this session negotiated declares no command named '{name}'");

        var transport = _transport;
        if (transport is not { IsConnected: true })
        {
            return 0;
        }

        // The seq is spent only once the command encodes: one that cannot (a realm-framed field while the session holds no realm) leaves no gap.
        var seq = unchecked((ushort)(_commandSeq + 1));
        var tick = LastAppliedTick;
        var frame = Store?.Realm;
        var message = Encode((ref WireWriter w) => CommandsMessage.Write(ref w, tick, [(plan, seq, values)], frame));
        _commandSeq = seq;
        await transport.SendAsync(message, ct).ConfigureAwait(false);
        return seq;
    }

    /// <summary>Sends a <c>BYE</c> and closes.</summary>
    /// <param name="code">1000, or a code in 4000-4999.</param>
    /// <returns>The close.</returns>
    public async Task CloseAsync(ushort code = CloseCodes.Normal)
    {
        var transport = _transport;
        if (transport != null)
        {
            try
            {
                await transport.SendAsync(Encode(new ByeMessage(code).Write), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A BYE that cannot be sent is not worth failing a close over: the FIN says the same thing, less politely.
            }

            await transport.CloseAsync(code, "client closing").ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        // Awaited rather than abandoned: the loops touch the transport, and disposing it under them turns an orderly shutdown into an ObjectDisposedException
        // on a background task nobody is watching. Except from a handler the receive loop is running: that loop cannot finish until this returns, and a
        // handler blocking on it would wait forever. Skipping it is safe — the loop's next step, once the handler returns, is to see the cancellation and
        // leave without touching the transport.
        var insideReceiveLoop = _dispatchThread == Environment.CurrentManagedThreadId;
        foreach (var loop in new[] { insideReceiveLoop ? null : _receiveLoop, _pingLoop })
        {
            if (loop != null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Shutting down; a loop that threw on the way out has nowhere to report it.
                }
            }
        }

        if (_transport != null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }

        _stopping.Dispose();
    }

    private static IClientTransport ForEndpoint(ClientOptions options)
    {
        var scheme = options.Endpoint.Scheme;
        if (string.Equals(scheme, "ws", StringComparison.OrdinalIgnoreCase) || string.Equals(scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            return new WebSocketClientTransport(options.Endpoint);
        }

        if (string.Equals(scheme, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            return new TcpClientTransport(options.Endpoint.Host, options.Endpoint.Port);
        }

        throw new ArgumentException($"'{scheme}' is not a Typhon endpoint scheme; use ws, wss or tcp", nameof(options));
    }

    private async Task<WelcomeMessage> HandshakeAsync(CancellationToken ct, bool onReceiveLoop)
    {
        var transport = _transportFactory();
        await transport.ConnectAsync(ct).ConfigureAwait(false);
        _transport = transport;

        var hello = new HelloMessage
        {
            Caps = _options.Caps,
            Kind = _options.Kind ?? "",
            Token = _options.Token ?? "",
            HelloPayload = _options.HelloPayload ?? [],
            ResumeToken = ResumeToken,

            // The catalog is skipped when the hash matches, which is the whole point of carrying it: a reconnect to the same server re-sends nothing.
            // The hash is the one the WELCOME gave, not one recomputed here — recomputing would make a client that canonicalizes differently from its own
            // server silently re-download the catalog on every reconnect, and nothing would report it.
            ClientCatalogHash = CatalogHash,
        };

        await transport.SendAsync(Encode(hello.Write), ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.HandshakeTimeout);

        var message = await transport.ReceiveAsync(timeout.Token).ConfigureAwait(false);
        if (message == null || message.Length == 0)
        {
            throw new InvalidOperationException($"the server closed during the handshake with code {transport.CloseCode}");
        }

        if (message[0] == MessageTypes.Kick)
        {
            var kick = KickMessage.Parse(message);
            Kicked?.Invoke(kick);
            throw new InvalidOperationException($"the server refused the session with {kick.Code}: {kick.Reason}");
        }

        var welcome = WelcomeMessage.Parse(message);
        ApplyWelcome(welcome);
        _options.Recorder?.Record(message);
        _policy.NoteConnected();

        // A new connection has said nothing yet, so the previous one's close code must not decide this one's.
        _kickCode = 0;
        Connects++;
        if (onReceiveLoop)
        {
            RaiseOnLoop(Connected, welcome);
        }
        else
        {
            Connected?.Invoke(welcome);
        }

        return welcome;
    }

    private void ApplyWelcome(WelcomeMessage welcome)
    {
        CapsGranted = welcome.CapsGranted;
        SessionId = welcome.SessionId;
        ResumeToken = welcome.ResumeToken;
        CatalogHash = welcome.CatalogHash;

        if (welcome.CatalogJson is { Length: > 0 })
        {
            // A catalog arrived, so this is a new world: recompile and start from an empty store. Keeping the old store would carry entities the new catalog
            // may not even be able to describe.
            Plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(welcome.CatalogJson));
            Store = new WorldStore(Plan, _options.SegmentHistory);
            _applier = new FrameApplier(Store, _options.Events);
            return;
        }

        if (Plan == null)
        {
            throw new InvalidOperationException(
                "the server skipped the catalog but this client holds none — it sent a catalog hash it could not have obtained from this session");
        }

        // The catalog was skipped because the hash matched. The store is kept; the server will send whatever the session needs to be told.
        _applier ??= new FrameApplier(Store, _options.Events);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Read ONCE into a local. The retry path drops the transport before it re-handshakes, and a handshake that throws leaves the loop running
            // with nothing to receive from: dereferencing the field here raised a NullReferenceException on every backoff cycle, which the catch below
            // swallowed into a spurious Fault, and the null message below then manufactured a Disconnected with a close code no server ever sent. A host
            // that counts those events — which is exactly what a load generator does — was handed a fabricated fault and disconnect per retry.
            var transport = _transport;
            if (transport == null)
            {
                if (!await HandleCloseAsync(CloseCodes.GoingAway, ct).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            byte[] message = null;
            try
            {
                message = await transport.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                RaiseOnLoop(Fault, exception);
            }

            if (message == null)
            {
                // A KICK outranks the transport's own code. The two agree where the transport has one (a WebSocket close frame carries the same number), and
                // where it has none the KICK is the whole of what the server said.
                var closeCode = _kickCode != 0 ? _kickCode : _transport?.CloseCode ?? CloseCodes.GoingAway;
                if (!await HandleCloseAsync(closeCode, ct).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            MessagesReceived++;
            BytesReceived += message.Length;
            _options.Recorder?.Record(message);

            try
            {
                _dispatchThread = Environment.CurrentManagedThreadId;
                Dispatch(message);
            }
            catch (Exception exception)
            {
                // One frame the decoder could not apply does not end the session: the next frame is absolute and will describe the world again. A decode
                // fault that IS fatal — a length mismatch, an unknown archetype — closes the transport from inside the decoder instead.
                Fault?.Invoke(exception);
            }
            finally
            {
                _dispatchThread = 0;
            }
        }
    }

    private void Dispatch(byte[] message)
    {
        if (message.Length == 0)
        {
            return;
        }

        switch (message[0])
        {
            case MessageTypes.Tick:
                if (_applier != null)
                {
                    _applier.Apply(message);
                    FrameApplied?.Invoke(Store);
                }

                break;
            case MessageTypes.Welcome:
                ApplyWelcome(WelcomeMessage.Parse(message));
                break;
            case MessageTypes.Kick:
                var kick = KickMessage.Parse(message);

                // Kept for the close that is about to follow it. The server sends the KICK and then closes the link; this is the only place the real code is
                // ever visible on a transport that has none of its own.
                _kickCode = kick.Code;
                Kicked?.Invoke(kick);
                break;
            case MessageTypes.Pong:
                break;
            default:
                // An unknown message type is skipped, not fatal: 03 § 10's versioning rule is that a minor revision may add message types, and a client that
                // closed on one could never talk to a newer server.
                break;
        }
    }

    /// <summary>Closes and forgets the transport, so nothing reports a session that has ended as open.</summary>
    /// <returns>The disposal.</returns>
    private async Task DropTransportAsync()
    {
        var transport = _transport;
        _transport = null;
        if (transport != null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> HandleCloseAsync(ushort code, CancellationToken ct)
    {
        var decision = _policy.Decide(code);
        RaiseOnLoop(Disconnected, code, decision == ReconnectDecision.Retry);

        if (decision != ReconnectDecision.Retry)
        {
            // The session is over and nothing will reopen it, so the socket goes with it. Leaving it in place made IsConnected say a session was open for the
            // rest of the process's life: Socket.Connected reports what the last I/O saw, and a peer's FIN does not change it — so a client that had been
            // kicked, and knew it, still answered "connected" to everything that asked.
            await DropTransportAsync().ConfigureAwait(false);
            return false;
        }

        await DropTransportAsync().ConfigureAwait(false);

        try
        {
            await Task.Delay(_policy.NextDelay(), ct).ConfigureAwait(false);
            await HandshakeAsync(ct, onReceiveLoop: true).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            RaiseOnLoop(Fault, exception);

            // The attempt failed rather than the session ending; loop round and let the policy decide again with a longer backoff.
            return !ct.IsCancellationRequested;
        }
    }

    // A handler raised from the receive loop runs marked as such, so a DisposeAsync it makes does not wait for the loop that is running it.
    private void RaiseOnLoop<T>(Action<T> handler, T arg)
    {
        if (handler == null)
        {
            return;
        }

        var outer = _dispatchThread;
        _dispatchThread = Environment.CurrentManagedThreadId;
        try
        {
            handler(arg);
        }
        finally
        {
            _dispatchThread = outer;
        }
    }

    private void RaiseOnLoop<T1, T2>(Action<T1, T2> handler, T1 arg1, T2 arg2)
    {
        if (handler == null)
        {
            return;
        }

        var outer = _dispatchThread;
        _dispatchThread = Environment.CurrentManagedThreadId;
        try
        {
            handler(arg1, arg2);
        }
        finally
        {
            _dispatchThread = outer;
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        var hz = Plan?.Catalog?.Tick?.PingHz is > 0 ? Plan.Catalog.Tick.PingHz : _options.PingHz;
        var period = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, hz));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(period, ct).ConfigureAwait(false);
                var transport = _transport;
                if (transport is { IsConnected: true })
                {
                    var ping = new PingMessage((uint)Environment.TickCount, LastAppliedTick);
                    await transport.SendAsync(Encode(ping.Write), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A ping that could not be sent means the transport is going; the receive loop is what notices and decides, and racing it here would give
                // the reconnect policy two opinions about one close.
            }
        }
    }

    private delegate void Writer(ref WireWriter writer);

    private static byte[] Encode(Writer write)
    {
        Span<byte> scratch = stackalloc byte[ProtocolConstants.HelloMaxBytes];
        var writer = new WireWriter(scratch);
        write(ref writer);
        return writer.Written.ToArray();
    }
}
