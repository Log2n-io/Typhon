using System;
using System.Collections.Generic;
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
    /// <summary>How long a refusal's <c>BYE</c> may take: the same bound the WebSocket transport gives its close handshake.</summary>
    private static readonly TimeSpan ByeTimeout = TimeSpan.FromSeconds(2);

    private readonly ClientOptions _options;
    private readonly Func<IClientTransport> _transportFactory;
    private readonly ReconnectPolicy _policy;
    private readonly CancellationTokenSource _stopping = new();

    private IClientTransport _transport;
    private FrameApplier _applier;
    private ushort _commandSeq;

    // One flush on the wire at a time: commands are queued with their seq under _outboxLock, and a flush swaps the whole queue out under the gate, so seqs
    // reach the wire in the order they were taken whichever threads queued and flushed.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // The command queue (Enqueue<T>, SendCommandAsync, SubscribeRequest), double-buffered so a flush sends one while callers fill the other.
#if NET9_0_OR_GREATER
    private readonly Lock _outboxLock = new();
#else
    private readonly object _outboxLock = new();
#endif
    private CommandOutbox _outbox = new();
    private CommandOutbox _flushing = new();
    private byte[] _sendBuffer = [];

    // Typed command bindings by CommandTypeSlot<T>.Index, valid for _typedPlan only: a new catalog rebinds every type on its next enqueue.
    private object[] _typed = new object[8];
    private CatalogPlan _typedPlan;

    // varu cmdTypeIdx (≤ 5 B) | u16 seq.
    private const int CommandHeaderMaxBytes = 7;

    // SubscribeRequest (14 § 4): the requests, their bucket and their callers, under one lock — called from the application, pumped from the receive and
    // ping loops.
#if NET9_0_OR_GREATER
    private readonly Lock _subscribeLock = new();
#else
    private readonly object _subscribeLock = new();
#endif
    private readonly Dictionary<ushort, TaskCompletionSource<SubscribeResult>> _subscribeWaiters = [];
    private GroupSubscriptions _subscriptions;
    private CommandBucket _subscribeBucket;

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
    private int _disposed;

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

        // A client restarted from persisted state starts holding the token its previous process was given, so its first HELLO resumes rather than cold-logs.
        // Null for every other caller, which is every caller that has not asked.
        ResumeToken = options.ResumeToken;
    }

    /// <summary>Raised when a session opens, with the catalog the server sent.</summary>
    public event Action<WelcomeMessage> Connected;

    /// <summary>Raised when a session ends, with the close code and whether the client will try again.</summary>
    public event Action<ushort, bool> Disconnected;

    /// <summary>
    /// Raised when the server sends a <c>KICK</c>, before the transport closes. A handler that throws refuses the stream, as it would for any message.
    /// </summary>
    public event Action<KickMessage> Kicked;

    /// <summary>
    /// Raised for a failure the client reports rather than throws: a message it could not apply — a malformed one, or one the event handler threw on —
    /// after which it refuses the stream and the session ends (<see cref="Disconnected"/> follows, with 1002, 1007 or 1009); an exception a
    /// <see cref="FrameApplied"/> handler threw, whose frame did apply whole, after which the session continues; or a receive or reconnect attempt that
    /// failed.
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
    /// <remarks>
    /// Seeded from <see cref="ClientOptions.ResumeToken"/> when one was given, so a client restarted from persisted state presents it on its first
    /// <c>HELLO</c> exactly as a reconnect within one process would.
    /// </remarks>
    public byte[] ResumeToken { get; private set; }

    /// <summary>The digest of the catalog this session holds, as the server reported it.</summary>
    public ulong CatalogHash { get; private set; }

    /// <summary>The newest tick this client has applied, which is what its <c>PING</c> carries.</summary>
    public uint LastAppliedTick => Store == null || Store.Tick < 0 ? 0 : (uint)Store.Tick;

    /// <summary>
    /// The round trip measured from this session's <c>PONG</c>s (05-sdks § 1) — the same estimator the TypeScript and native SDKs keep. Reset by every
    /// <c>WELCOME</c>; half its <see cref="RoundTripEstimator.RttMs"/> is the usual one-way delay for <see cref="Clock.SubTickPhase"/>.
    /// </summary>
    public RoundTripEstimator RoundTrip { get; } = new();

    /// <summary>Whether a session is open right now.</summary>
    public bool IsConnected => _transport is { IsConnected: true } && Plan != null;

    /// <summary>
    /// What the transport knows about an abnormal close beyond its code, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Surfaced because a close code of 1006 says only that no close frame arrived. A caller counting abrupt disconnects — a load
    /// generator, say — needs the local failure behind them to tell a reset by the peer from an abort or a timeout.
    /// </remarks>
    public string CloseDetail => _transport?.CloseDetail;

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
            Dispatch(message, live: false);
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
            await transport.SendAsync(Encode(new PingMessage(RoundTripEstimator.Stamp(PreciseNowMs()), LastAppliedTick).Write), ct).ConfigureAwait(false);
            await PumpSubscriptionsAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Asks the server for one key of an explicit group set (14 § 4, P13): its members are delivered, then the request is answered.</summary>
    /// <param name="set">The explicit set's catalog name.</param>
    /// <param name="key">The key, never 0.</param>
    /// <param name="ct">Stops waiting for the answer; the request itself goes on.</param>
    /// <returns>
    /// The answer: <see cref="SubscribeOutcome.Applied"/> once the key is complete, <see cref="SubscribeOutcome.Refused"/> with the server's code,
    /// <see cref="SubscribeOutcome.Rejected"/> with an <c>ACK</c> reason, or <see cref="SubscribeOutcome.Dropped"/> when the session ended first.
    /// </returns>
    /// <remarks>
    /// Requests leave in call order, paced by the command's catalog rate — 5 a second past a burst of the server's <c>MaxGroupsPerSession</c> — and one
    /// the server still refuses for its rate is sent again by itself. Waiting continues across frames; nothing is sent while no session is open.
    /// </remarks>
    public Task<SubscribeResult> SubscribeAsync(string set, long key, CancellationToken ct = default) =>
        RequestGroupAsync(set, key, BuiltInCommands.SubscribeOp, ct);

    /// <summary>Gives back one key of an explicit group set: its members leave, then the request is answered.</summary>
    /// <param name="set">The explicit set's catalog name.</param>
    /// <param name="key">The key, never 0.</param>
    /// <param name="ct">Stops waiting for the answer; the request itself goes on.</param>
    /// <returns>The answer, as <see cref="SubscribeAsync"/>'s.</returns>
    public Task<SubscribeResult> UnsubscribeAsync(string set, long key, CancellationToken ct = default) =>
        RequestGroupAsync(set, key, BuiltInCommands.UnsubscribeOp, ct);

    private async Task<SubscribeResult> RequestGroupAsync(string set, long key, byte op, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(set);
        var waiter = new TaskCompletionSource<SubscribeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_subscribeLock)
        {
            var plan = Plan ?? throw new InvalidOperationException("no catalog yet: connect first");
            var index = plan.GroupSetIndex(set);
            if (index < 0)
            {
                throw new ArgumentException($"the catalog this session negotiated declares no group set named '{set}'", nameof(set));
            }

            var id = _subscriptions.Request((uint)index, key, op);
            _subscribeWaiters[id] = waiter;
        }

        await PumpSubscriptionsAsync().ConfigureAwait(false);
        using (ct.Register(() => waiter.TrySetCanceled(ct)))
        {
            return await waiter.Task.ConfigureAwait(false);
        }
    }

    // Sends what the bucket allows, one command a message, in order; nothing while no session is open.
    private async Task PumpSubscriptionsAsync()
    {
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                var transport = _transport;
                var queued = false;
                lock (_subscribeLock)
                {
                    if (_subscriptions == null || _subscriptions.QueuedCount == 0 || transport is not { IsConnected: true })
                    {
                        return;
                    }

                    // Through the command queue, behind whatever the application queued before it: one seq space, in order on the wire.
                    _subscriptions.Pump((command, values) =>
                    {
                        if (!_subscribeBucket.Take(NowMs()))
                        {
                            return -1;
                        }

                        lock (_outboxLock)
                        {
                            // Not between a reconnect's transport and its WELCOME: the request waits, as it would for its rate.
                            if (!_sessionOpen)
                            {
                                return -1;
                            }

                            queued = true;
                            return EnqueueValues(command, values);
                        }
                    }, max: 1);
                }

                if (!queued)
                {
                    return;
                }

                await FlushUnderGateAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // The transport is going; the receive loop notices and the close drops what is still waiting.
            Fault?.Invoke(exception);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // After an applied frame: its ACKS and SOURCES answer what was sent, and a request refused for the rate goes again.
    private void ObserveSubscriptions()
    {
        bool queued;
        lock (_subscribeLock)
        {
            if (_subscriptions == null || (_subscriptions.SentCount == 0 && _subscriptions.QueuedCount == 0))
            {
                return;
            }

            _subscriptions.Observe(Store.Acks, Store.Sources);
            queued = _subscriptions.QueuedCount > 0;
        }

        if (queued)
        {
            _ = PumpSubscriptionsAsync();
        }
    }

    /// <summary>Commands queued and not yet flushed (tests).</summary>
    internal int QueuedCommandCount
    {
        get
        {
            lock (_outboxLock)
            {
                return _outbox.Count;
            }
        }
    }

    // A session ended or a new one began: what was queued belongs to the old one — its seqs will never be acknowledged and its type indices name the old
    // catalog's commands — so it is dropped, never sent on the next session.
    private void DropQueuedCommands(bool open = false)
    {
        lock (_outboxLock)
        {
            _outbox.Clear();
            _sessionOpen = open;
        }
    }

    // Under _outboxLock: whether a session's WELCOME was applied and it has not ended. Commands are queued and flushed only then — never between a
    // reconnect's transport and its WELCOME, where the old catalog's indices and seqs would reach the new session, or before its HELLO.
    private bool _sessionOpen;

    // The session is over: every request still waiting ends Dropped. A new catalog starts a fresh set of requests.
    private void ResetSubscriptions(CatalogPlan plan)
    {
        DropQueuedCommands();
        lock (_subscribeLock)
        {
            _subscriptions?.Reset();
            if (plan != null)
            {
                _subscriptions = new GroupSubscriptions(plan);
                _subscriptions.Answered += OnSubscribeAnswered;
                var rate = Array.Find(plan.Catalog.Commands ?? [], c => c.Name == BuiltInCommands.SubscribeRequest)?.Rate;
                _subscribeBucket = new CommandBucket(rate);
            }
        }
    }

    // Under _subscribeLock; the waiter's continuations run elsewhere (RunContinuationsAsynchronously).
    private void OnSubscribeAnswered(SubscribeResult result)
    {
        if (_subscribeWaiters.Remove(result.RequestId, out var waiter))
        {
            waiter.TrySetResult(result);
        }
    }

    /// <summary>
    /// Queues one command for the next <see cref="FlushCommandsAsync"/>, encoded now from the struct itself: the typed, allocation-free send path.
    /// </summary>
    /// <typeparam name="T">
    /// A <see cref="ReplicatedMessageAttribute"/> struct — the one the server declared the command from, out of the shared contracts assembly. Its catalog
    /// name is its type name.
    /// </typeparam>
    /// <param name="command">The command.</param>
    /// <returns>
    /// The sequence it will travel under; <see cref="CommandRefused.RateLimited"/> when the local bucket for its type is empty, or
    /// <see cref="CommandRefused.NotConnected"/> when no session is open.
    /// </returns>
    /// <exception cref="InvalidOperationException">No session has negotiated a catalog yet, or the catalog declares no command named after <typeparamref name="T"/>.</exception>
    /// <exception cref="MessageBindingException">
    /// The struct and the catalog's command disagree — a field renamed, ignored or retyped by the server's declaration — listing every disagreement. Thrown on
    /// the first enqueue of the type per session, before anything is sent: a contract drift is a loud failure at connect, never a field silently zero.
    /// </exception>
    /// <exception cref="ArgumentException">A value outside its codec's range, as the name-keyed path refuses it.</exception>
    /// <remarks>
    /// <para>
    /// <b>Batch a frame, then flush once.</b> Every command queued between two flushes leaves in one <c>COMMANDS</c> message (split only at the catalog's
    /// <c>limits.clientMessageBytes</c>), stamped with the newest applied tick at flush time (§ 10). Steady state allocates nothing: the struct is read
    /// through its generated accessors, its type's binding is found in an array, and its bytes land in a buffer reused across frames.
    /// </para>
    /// <para>
    /// <b>Delivery.</b> A command the catalog delivers <c>latest</c> replaces the pending one of its type. The catalog's <c>rate</c> is pre-checked locally
    /// per type, so the client drops its own excess instead of having the server count a drop; the server's bucket stays the authority.
    /// </para>
    /// <para>
    /// Thread-safe. A session that ends, or a new <c>WELCOME</c>, drops what was queued and not yet flushed: its seqs would never be acknowledged and its type
    /// indices name the old catalog's commands.
    /// </para>
    /// </remarks>
    public int Enqueue<T>(in T command) where T : unmanaged, IReplicatedMessage
    {
        lock (_outboxLock)
        {
            var slot = TypedCommand<T>();
            if (!_sessionOpen || _transport is not { IsConnected: true })
            {
                return CommandRefused.NotConnected;
            }

            // Encoded and checked first, the token taken last: a command refused for its value or its size spends no rate.
            var seq = unchecked((ushort)(_commandSeq + 1));
            var w = new WireWriter(_outbox.Reserve(CommandHeaderMaxBytes + slot.Binding.MaxBytes));
            CommandsMessage.WriteCommand(ref w, slot.Binding, seq, command, Store?.Realm?.ForCommands);
            RefuseOversized(slot.Binding.Plan, w.Written.Length);
            if (!slot.Bucket.Take(NowMs()))
            {
                return CommandRefused.RateLimited;
            }

            CommitCommand(slot.Binding.Plan, w.Written.Length, slot.Latest);
            _commandSeq = seq;
            return seq;
        }
    }

    /// <summary>
    /// Sends every queued command, in queue order, as few <c>COMMANDS</c> messages as the catalog's message limit allows.
    /// </summary>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>The messages sent; 0 when nothing was queued or no session is open (what was queued is then dropped).</returns>
    /// <remarks>Call once per client frame, after its commands were queued. Allocates nothing when the transport completes the send synchronously.</remarks>
#if NET6_0_OR_GREATER
    [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    public async ValueTask<int> FlushCommandsAsync(CancellationToken ct = default)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await FlushUnderGateAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Queues one typed command and flushes at once: <see cref="Enqueue{T}"/> then <see cref="FlushCommandsAsync"/>, for a caller with one command.</summary>
    /// <typeparam name="T">A <see cref="ReplicatedMessageAttribute"/> struct.</typeparam>
    /// <param name="command">The command.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>The sequence it travelled under, or a <see cref="CommandRefused"/> value.</returns>
    /// <remarks>
    /// A client sending several commands a frame should queue them all and flush once: each flush is a message. A cancellation while waiting for the send
    /// leaves the command queued: the next flush sends it.
    /// </remarks>
#if NET6_0_OR_GREATER
    [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    public async ValueTask<int> SendCommandAsync<T>(T command, CancellationToken ct = default) where T : unmanaged, IReplicatedMessage
    {
        var seq = Enqueue(in command);
        if (seq >= 0)
        {
            await FlushCommandsAsync(ct).ConfigureAwait(false);
        }

        return seq;
    }

    /// <summary>
    /// Sends one command to the server, by catalog name — the untyped path, for a built-in command or one with no contracts struct on this side.
    /// </summary>
    /// <param name="name">The command's catalog name — a built-in such as <c>ClientRegion</c>, or one the application declared.</param>
    /// <param name="values">The command's fields, by name.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>The sequence the command was sent under, which an <c>ACKS</c> rejection would name; 0 when no session is open.</returns>
    /// <remarks>
    /// Goes out through the same queue as <see cref="Enqueue{T}"/>, flushing whatever was queued before it, so seqs reach the wire in order. Prefer the
    /// typed path where a contracts struct exists: this one finds fields by name at run time, so a misspelled one is found only when it is sent, and it
    /// allocates per call. The catalog's rate is not pre-checked here. A cancellation while waiting for the send leaves the command queued: the next flush
    /// sends it.
    /// </remarks>
    public async Task<ushort> SendCommandAsync(string name, RecordValues values, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(values);

        ushort seq;
        lock (_outboxLock)
        {
            // Resolved under the lock: a WELCOME applies its catalog behind it, so the plan and the open session are the same session's.
            var plan = Plan?.CommandByName(name)
                ?? throw new InvalidOperationException($"the catalog this session negotiated declares no command named '{name}'");
            if (!_sessionOpen || _transport is not { IsConnected: true })
            {
                return 0;
            }

            seq = EnqueueValues(plan, values);
        }

        await FlushCommandsAsync(ct).ConfigureAwait(false);
        return seq;
    }

    // Under _outboxLock. The seq is spent only once the command encodes: one that cannot (a realm-framed field while the session holds no realm) leaves no gap.
    private ushort EnqueueValues(MessagePlan plan, RecordValues values)
    {
        var seq = unchecked((ushort)(_commandSeq + 1));
        var w = new WireWriter(_outbox.Reserve(ClientMessageBytes));
        try
        {
            CommandsMessage.WriteCommand(ref w, plan, seq, values, Store?.Realm?.ForCommands);
        }
        catch (InvalidOperationException) when (w.Written.Length >= ClientMessageBytes - CommandHeaderMaxBytes)
        {
            // The reservation is the message limit: a command that overruns it is the caller's, as RefuseOversized says, not a broken buffer.
            throw new ArgumentException($"command '{plan.Name}' does not fit the catalog's {ClientMessageBytes} B message limit");
        }

        RefuseOversized(plan, w.Written.Length);
        CommitCommand(plan, w.Written.Length, IsLatest(plan));
        _commandSeq = seq;
        return seq;
    }

    // Refuses a command no message could carry.
    private void RefuseOversized(MessagePlan plan, int length)
    {
        if (CommandOutbox.HeaderBytes(1) + length > ClientMessageBytes)
        {
            throw new ArgumentException($"command '{plan.Name}' needs {length} B, above the catalog's {ClientMessageBytes} B message limit");
        }
    }

    // Under _outboxLock: keeps the command just written, after RefuseOversized.
    private void CommitCommand(MessagePlan plan, int length, bool latest)
    {
        if (_outbox.Commit(plan.Idx, length, latest))
        {
            CommandsCoalesced++;
        }
    }

    // Under _sendGate: swaps the queue out under its lock, so commands queued meanwhile wait for the next flush, and sends it in order.
    private async ValueTask<int> FlushUnderGateAsync(CancellationToken ct)
    {
        IClientTransport transport;
        lock (_outboxLock)
        {
            (_outbox, _flushing) = (_flushing, _outbox);

            // Read with the swap: what was swapped out belongs to the session open now, or to none.
            transport = _sessionOpen ? _transport : null;
        }

        var pending = _flushing;
        try
        {
            if (pending.Count == 0 || transport is not { IsConnected: true })
            {
                return 0;
            }

            var limit = ClientMessageBytes;
            if (_sendBuffer.Length < limit)
            {
                _sendBuffer = new byte[limit];
            }

            var tick = LastAppliedTick;
            var messages = 0;
            var from = 0;
            while (from < pending.Count)
            {
                var length = pending.WriteMessage(ref from, tick, _sendBuffer.AsSpan(0, limit));
                await transport.SendAsync(_sendBuffer.AsMemory(0, length), ct).ConfigureAwait(false);
                messages++;
            }

            return messages;
        }
        finally
        {
            // Emptied however the flush ends: a send that throws midway must not leave the batch to go out a second time, with seqs the server already ran.
            pending.Clear();
        }
    }

    // Under _outboxLock: the type's binding, bucket and delivery mode for the current catalog, made on first use per session.
    private TypedCommandSlot<T> TypedCommand<T>() where T : unmanaged, IReplicatedMessage
    {
        var plan = Plan ?? throw new InvalidOperationException("no catalog yet: connect first");
        if (!ReferenceEquals(_typedPlan, plan))
        {
            Array.Clear(_typed, 0, _typed.Length);
            _typedPlan = plan;
        }

        var index = CommandTypeSlot<T>.Index;
        return index < _typed.Length && _typed[index] is TypedCommandSlot<T> slot ? slot : BindTypedCommand<T>(plan, index);
    }

    // The cold path, kept out of TypedCommand: nothing here may run per send. (A lambda capturing a local here once allocated its closure on every call,
    // cache hit included — C# creates a display class when the scope that declares the captured local is entered.)
    private TypedCommandSlot<T> BindTypedCommand<T>(CatalogPlan plan, int index) where T : unmanaged, IReplicatedMessage
    {
        var command = plan.CommandByName(typeof(T).Name)
            ?? throw new InvalidOperationException($"the catalog this session negotiated declares no command named '{typeof(T).Name}'");
        var slot = new TypedCommandSlot<T>(MessageBinding<T>.Bind(command), new CommandBucket(Declared(command)?.Rate), IsLatest(command));
        if (index >= _typed.Length)
        {
            Array.Resize(ref _typed, Math.Max(index + 1, _typed.Length * 2));
        }

        _typed[index] = slot;
        return slot;
    }

    private CatalogCommand Declared(MessagePlan command)
    {
        foreach (var c in Plan.Catalog.Commands ?? [])
        {
            if (c.Idx == command.Idx)
            {
                return c;
            }
        }

        return null;
    }

    private bool IsLatest(MessagePlan command) => Declared(command)?.Delivery == CatalogCommand.LatestDelivery;

    private int ClientMessageBytes => Plan.Catalog.Limits.ClientMessageBytes;

    /// <summary>Queued commands a newer one of the same <c>latest</c> type replaced before they were sent.</summary>
    public long CommandsCoalesced { get; private set; }

    private sealed class TypedCommandSlot<T>(MessageBinding<T> binding, CommandBucket bucket, bool latest) where T : unmanaged, IReplicatedMessage
    {
        public MessageBinding<T> Binding { get; } = binding;

        public CommandBucket Bucket { get; } = bucket;

        public bool Latest { get; } = latest;
    }

    /// <summary>Sends a <c>BYE</c> and closes.</summary>
    /// <param name="code">1000, or a code in 4000-4999.</param>
    /// <returns>The close.</returns>
    public async Task CloseAsync(ushort code = CloseCodes.Normal)
    {
        // Unflushed commands go with the session the caller is ending; flush first to send them.
        DropQueuedCommands();
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

    // Monotonic milliseconds at the stopwatch's resolution, for the round trip: Environment.TickCount64 moves in 15.6 ms steps on Windows, which is
    // the size of a LAN round trip.
    private static double PreciseNowMs() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    // Monotonic milliseconds, for the subscribe bucket. netstandard2.1 has no Environment.TickCount64; the stopwatch's clock is the same monotonic one.
    private static long NowMs() =>
#if NET
        Environment.TickCount64;
#else
        System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
#endif

    /// <inheritdoc />
    /// <remarks>Idempotent: a second call returns at once.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

#if NET8_0_OR_GREATER
        await _stopping.CancelAsync().ConfigureAwait(false);
#else
        _stopping.Cancel();
#endif

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

        ResetSubscriptions(null);
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
        // No command is queued or flushed until this session's WELCOME is applied (ApplyWelcome below opens it).
        DropQueuedCommands();
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

        // The session is open: commands queue and flush against the catalog the WELCOME just applied.
        DropQueuedCommands(open: true);
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
        RoundTrip.Reset();
        DropQueuedCommands();
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
            ResetSubscriptions(Plan);
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
            ushort refusal = 0;
            try
            {
                message = await transport.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (WireFormatException exception)
            {
                // A message above the cap, refused by the transport before its body was read: the stream is refused like any other message this client
                // cannot take, BYE 4004 included (03 § 3).
                RaiseOnLoop(Fault, exception);
                refusal = RefusalCode(exception);
            }
            catch (Exception exception)
            {
                RaiseOnLoop(Fault, exception);
            }

            if (refusal != 0)
            {
                if (!await RefuseAsync(transport, refusal, ct).ConfigureAwait(false))
                {
                    return;
                }

                continue;
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
                Dispatch(message, live: true);
                continue;
            }
            catch (Exception exception)
            {
                // A message that could not be applied ends the session (03 § 3, FrameApplier.Apply). The store may hold part of a frame — its leaves, among
                // others, apply last — and no later frame repairs it: state records carry only the groups that changed and a leave is sent once, so carrying
                // on left entities in the store that the server had removed. A throw from the event handler is refused like a malformed payload, as the
                // TypeScript and native SDKs do: the same handler would throw again on the same world, so a reconnect would only repeat it.
                Fault?.Invoke(exception);
                refusal = RefusalCode(exception);
            }
            finally
            {
                _dispatchThread = 0;
            }

            if (!await RefuseAsync(transport, refusal, ct).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// The code a refusal closes with: a wire error's own when it is one a client refuses a stream with, else 1007. A <see cref="WireFormatException"/> the
    /// application threw from a handler could carry anything, and a retryable code there would reconnect onto the same failure.
    /// </summary>
    private static ushort RefusalCode(Exception exception) =>
        exception is WireFormatException { CloseCode: CloseCodes.ProtocolError or CloseCodes.MalformedPayload or CloseCodes.MessageTooBig } wire
            ? wire.CloseCode
            : CloseCodes.MalformedPayload;

    /// <summary>Refuses the stream, then lets the reconnect policy decide what follows.</summary>
    /// <param name="transport">The session's transport.</param>
    /// <param name="code">1002, 1007 or 1009.</param>
    /// <param name="ct">Stops the client.</param>
    /// <returns>Whether the receive loop goes on.</returns>
    private async Task<bool> RefuseAsync(IClientTransport transport, ushort code, CancellationToken ct)
    {
        await RefuseStreamAsync(transport, code).ConfigureAwait(false);
        return await HandleCloseAsync(code, ct).ConfigureAwait(false);
    }

    /// <summary>Closes a stream this client cannot go on applying, telling the server why in the only form both transports carry.</summary>
    /// <param name="transport">The session's transport.</param>
    /// <param name="code">The real code: 1002, 1007 or 1009.</param>
    /// <returns>The close.</returns>
    /// <remarks>
    /// A <c>BYE</c> carries only 1000 or 4000-4999, so the refusal travels as 4004 ("the client refused the stream") and the transport closes with the real
    /// code where it has a place for one — a WebSocket close frame; TCP's FIN carries nothing (03 § 3). Neither step may throw or hang: a refusal that
    /// failed must still end in <see cref="Disconnected"/>, and a server that has stopped reading would otherwise hold the BYE — and a dispose awaiting
    /// this loop — forever.
    /// </remarks>
    private static async Task RefuseStreamAsync(IClientTransport transport, ushort code)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ByeTimeout);
            await transport.SendAsync(Encode(new ByeMessage(CloseCodes.ClientRefusedTheStream).Write), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The link may already be going; the close below is the refusal either way.
        }

        try
        {
            await transport.CloseAsync(code, "client refused the stream").ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A transport that cannot close is dropped by HandleCloseAsync all the same.
        }
    }

    /// <summary>Applies one message the server sent.</summary>
    /// <param name="message">The message, type byte included.</param>
    /// <param name="live">
    /// Whether it arrived on an open session. A live <c>WELCOME</c> is out of state, since the handshake consumed the only one; a recording holds one per
    /// session it spans, each opening the next.
    /// </param>
    /// <exception cref="WireFormatException">The message is unknown or out of state (1002), or malformed.</exception>
    private void Dispatch(byte[] message, bool live)
    {
        if (message.Length == 0)
        {
            // No type byte, so no message: a framing error, as the native and TypeScript clients refuse it.
            throw new WireFormatException(CloseCodes.ProtocolError, "an empty message");
        }

        switch (message[0])
        {
            case MessageTypes.Tick:
                if (_applier != null)
                {
                    _applier.Apply(message);
                    if (live)
                    {
                        ObserveSubscriptions();
                    }

                    RaiseFrameApplied(live);
                }

                break;
            case MessageTypes.Welcome when live:
                // Applying it would recompile the catalog and replace the store under the application, mid-session.
                throw new WireFormatException(CloseCodes.ProtocolError, "a WELCOME on a session that is already open");
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
                var pong = PongMessage.Parse(message);
                RoundTrip.OnPong(pong.ClientMs, PreciseNowMs());
                break;
            default:
                // 1002, as 03 § 3 has it. A newer server cannot send this client a type it does not know: the lower minor wins the handshake and the server
                // emits nothing the session did not ask for, so what extends the protocol is a TICK block, which IS skipped by its length (03 § 10).
                throw new WireFormatException(CloseCodes.ProtocolError, $"message type 0x{message[0]:X2} is unknown or out of state");
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
        // Whatever the policy decides, this session is over: what it was asked is unanswered, and a new one holds none of it.
        ResetSubscriptions(null);
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

    // The frame applied whole, so a FrameApplied handler that throws leaves nothing to refuse: on the receive loop it is reported and the session goes on,
    // under Replay it propagates to the caller. Only a throw from inside the apply leaves a partial store.
    private void RaiseFrameApplied(bool live)
    {
        if (!live)
        {
            FrameApplied?.Invoke(Store);
            return;
        }

        try
        {
            FrameApplied?.Invoke(Store);
        }
        catch (Exception exception)
        {
            Fault?.Invoke(exception);
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
                    // The round trip's clock, the one OnPong subtracts: a TickCount stamp against a Stopwatch receive time measured two clocks'
                    // offset (~23 ms on one box) plus TickCount's 15.6 ms steps, and a wrap of four billion ms whenever the offset went negative.
                    var ping = new PingMessage(RoundTripEstimator.Stamp(PreciseNowMs()), LastAppliedTick);
                    await transport.SendAsync(Encode(ping.Write), ct).ConfigureAwait(false);

                    // A request the bucket held back, with no frame arriving to pump it: the ping's cadence sends it once a token is back.
                    await PumpSubscriptionsAsync().ConfigureAwait(false);
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
