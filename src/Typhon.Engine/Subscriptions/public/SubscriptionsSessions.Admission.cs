using JetBrains.Annotations;
using System;
using System.Security.Claims;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine;

/// <summary>
/// Admission and the tick-visible session lifecycle. The declaration half — <see cref="Kinds"/> — lives beside the rest of the registration surface.
/// </summary>
public sealed partial class SubscriptionsSessions
{
    /// <summary>
    /// The application's admission hook, run once per connecting client on the transport thread that decoded its <c>HELLO</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An undeclared kind never reaches it.</b> The kind is checked against <see cref="DeclaredKinds"/> first and refused with
    /// <see cref="CloseCodes.AuthenticationRejected"/>, so a hook only ever sees names the application itself declared and does not have to defend against
    /// arbitrary strings from the network.
    /// </para>
    /// <para>
    /// <b>Leaving it unset admits everyone as a <see cref="SessionRole.Spectator"/></b>, with the operator's limits. That is the least a session can do, so a
    /// server that forgets to write a hook is permissive about who connects and restrictive about what they may do — the opposite default would let a
    /// forgotten line of configuration hand world-changing commands to anyone who found the port.
    /// </para>
    /// <para>
    /// <b>Unlike a declaration, it stays settable after <c>Start</c>.</b> The registry freezes because its declarations are compiled once into a catalog
    /// clients negotiate against; this is read fresh on every connection and compiles into nothing, so swapping it — a maintenance mode that refuses new
    /// players, a hot-reloaded policy — is correct rather than a late declaration that would silently not apply.
    /// </para>
    /// <para>
    /// <b>Published, because that swap is the documented use.</b> It is written on whatever thread decides to change policy and read on every transport thread
    /// that decodes a <c>HELLO</c>. A plain auto-property would let a reader keep the old delegate indefinitely on arm64 — where a store is not ordered against
    /// another core's loads without a release — so a maintenance mode could go on admitting players after it was switched on. The pair of accessors below is
    /// the release/acquire that makes the swap take effect for the next connection.
    /// </para>
    /// </remarks>
    public AdmitHandler Admit
    {
        get => Volatile.Read(ref _admit);
        set => Volatile.Write(ref _admit, value);
    }

    private AdmitHandler _admit;

    /// <summary>
    /// This tick's session lifecycle, read by application systems in Engine-Pre.
    /// </summary>
    /// <remarks>
    /// Sessions open and close on transport threads, at moments that have nothing to do with the tick. This is where that becomes something an application
    /// can act on with an ordinary transaction: the events are collected as they happen and handed to the tick as one immutable batch, so possessing an avatar
    /// on <see cref="SessionEventKind.Opened"/> and releasing it on <see cref="SessionEventKind.Closed"/> is normal system code rather than a callback on
    /// somebody else's thread.
    /// </remarks>
    public SessionEvents SessionEvents { get; } = new();

    /// <summary>
    /// Whether <paramref name="kind"/> is one this application declared.
    /// </summary>
    /// <remarks>
    /// <b>Declaring no kinds means clients name none.</b> An application that never called <see cref="Kinds"/> has no vocabulary, so a client presenting a
    /// name is presenting one that does not exist and is refused — while a client that names nothing is admitted and the hook decides. The alternative,
    /// treating an empty declaration list as "anything goes", would make the refusal silently depend on whether one unrelated line of configuration was
    /// written.
    /// </remarks>
    /// <param name="kind">The name the client presented.</param>
    /// <returns><see langword="true"/> when the hook may run for it.</returns>
    internal bool IsDeclaredKind(string kind)
    {
        if (DeclaredKinds.Count == 0)
        {
            return string.IsNullOrEmpty(kind);
        }

        for (var i = 0; i < DeclaredKinds.Count; i++)
        {
            if (string.Equals(DeclaredKinds[i], kind, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decides one connection: the kind check, then the application's hook.
    /// </summary>
    /// <param name="request">What the client presented.</param>
    /// <returns>The acceptance or refusal. The session table still has to have room for an acceptance to become a session.</returns>
    internal Admission Decide(in AdmissionRequest request)
    {
        if (!IsDeclaredKind(request.Kind))
        {
            return Admission.RefuseInternal(CloseCodes.AuthenticationRejected,
                DeclaredKinds.Count == 0 ? "no session kinds are declared" : "unknown session kind");
        }

        var admit = Admit;
        return admit == null ? Admission.Accept(SessionRole.Spectator) : admit(request);
    }
}

/// <summary>
/// What happened to a session.
/// </summary>
[PublicAPI]
public enum SessionEventKind : byte
{
    /// <summary>A client was admitted. The role, limits and application data admission handed back travel with it.</summary>
    Opened = 1,

    /// <summary>A session ended, for the reason it carries.</summary>
    Closed = 2,

    /// <summary>A resumable session's grace period elapsed without the client coming back. Built with resume, in a later phase.</summary>
    ResumeExpired = 3,

    /// <summary>A session was served less — a rate class dropped, a near radius shrunk — rather than closed. Built with the send pump.</summary>
    Degraded = 4,
}

/// <summary>
/// Why a session ended.
/// </summary>
[PublicAPI]
public enum SessionCloseReason : byte
{
    /// <summary>The client left cleanly, with <c>BYE</c>.</summary>
    ClientLeft = 0,

    /// <summary>The application asked for it, through <see cref="SessionRequest.Kick"/>.</summary>
    Kicked = 1,

    /// <summary>The link went away without a goodbye.</summary>
    LinkLost = 2,

    /// <summary>The session fell too far behind to be worth serving.</summary>
    Lagging = 3,

    /// <summary>The client stopped acknowledging.</summary>
    Unacknowledged = 4,

    /// <summary>The client broke the protocol, or sent something the engine could not decode.</summary>
    ProtocolError = 5,

    /// <summary>The server is shutting down.</summary>
    ServerShutdown = 6,

    /// <summary>The engine faulted while serving this session.</summary>
    InternalError = 7,
}

/// <summary>
/// One thing that happened to one session, as an application system reads it in the tick.
/// </summary>
/// <remarks>
/// A single struct rather than one type per kind: the batch is a flat span an application walks with a <c>switch</c>, which keeps the read allocation-free and
/// keeps the ordering between an <see cref="SessionEventKind.Opened"/> and a <see cref="SessionEventKind.Closed"/> visible — a session that opens and closes
/// inside one tick produces both, in that order, and an application that only handles one of them would otherwise leak whatever it built.
/// </remarks>
[PublicAPI]
public readonly struct SessionEvent
{
    internal SessionEvent(SessionEventKind kind, SessionId session, SessionRole role, SessionLimits limits, object appData, string sessionKind,
        byte[] helloPayload, ClaimsPrincipal principal, SessionId resumedFrom, SessionCloseReason reason, ushort closeCode, bool resumable)
    {
        Kind = kind;
        Session = session;
        Role = role;
        Limits = limits;
        AppData = appData;
        SessionKind = sessionKind;
        HelloPayload = helloPayload;
        Principal = principal;
        ResumedFrom = resumedFrom;
        Reason = reason;
        CloseCode = closeCode;
        Resumable = resumable;
    }

    /// <summary>What happened.</summary>
    public SessionEventKind Kind { get; }

    /// <summary>Which session it happened to.</summary>
    public SessionId Session { get; }

    /// <summary>The role admission gave it. Meaningful on <see cref="SessionEventKind.Opened"/>.</summary>
    public SessionRole Role { get; }

    /// <summary>The limits admission asked for — the object the hook handed back, not the resolved numbers.</summary>
    public SessionLimits Limits { get; }

    /// <summary>Whatever the admission hook attached to this session.</summary>
    public object AppData { get; }

    /// <summary>The application-declared kind the client presented.</summary>
    public string SessionKind { get; }

    /// <summary>The client's <c>HELLO</c> payload, copied out of the transport's buffer. Empty when it sent none.</summary>
    public ReadOnlyMemory<byte> HelloPayload { get; }

    /// <summary>The authenticated principal, or <see langword="null"/> — always <see langword="null"/> until the security work lands.</summary>
    public ClaimsPrincipal Principal { get; }

    /// <summary>The session this one resumed, or <see cref="SessionId.None"/>. Filled when resume is built.</summary>
    public SessionId ResumedFrom { get; }

    /// <summary>Why it ended. Meaningful on <see cref="SessionEventKind.Closed"/>.</summary>
    public SessionCloseReason Reason { get; }

    /// <summary>The close code the client was given.</summary>
    public ushort CloseCode { get; }

    /// <summary>Whether the client may come back to this session within the grace period.</summary>
    public bool Resumable { get; }

    /// <inheritdoc />
    public override string ToString() => Kind == SessionEventKind.Closed
        ? $"{Kind} {Session} ({Reason}, {CloseCode})"
        : $"{Kind} {Session}";
}

/// <summary>
/// The tick's session lifecycle: everything that happened to sessions since the last tick, as one batch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from anywhere, read only by the tick.</b> Admission runs on a transport thread and closing runs on the tick, so production is many-to-one and
/// takes a lock — once per connection and once per disconnection, which is nowhere near any hot path. Consumption is a bare span walk with no synchronization
/// at all, because the buffer an application reads is not the buffer producers are appending to.
/// </para>
/// <para>
/// <b>Two buffers, swapped once per tick.</b> That is what makes "delivered in the tick they happened" true without holding a lock across application code:
/// <see cref="BeginTick"/> takes what accumulated and hands it over whole. An event produced while the tick is running lands in the other buffer and is
/// delivered next tick — never dropped, never delivered twice.
/// </para>
/// <para>
/// <b>Steady state allocates nothing.</b> Both buffers keep their high-water capacity and are reused; the only allocations are the growth steps of the first
/// ticks that reach a new peak.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class SessionEvents
{
    private const int InitialCapacity = 16;

    private readonly object _gate = new();

    private SessionEvent[] _pending = new SessionEvent[InitialCapacity];
    private SessionEvent[] _current = new SessionEvent[InitialCapacity];
    private int _pendingCount;
    private int _currentCount;

    internal SessionEvents()
    {
    }

    /// <summary>How many events this tick carries.</summary>
    public int Count => _currentCount;

    /// <summary>Events waiting for the next tick. Diagnostics only — an application reads <see cref="AsSpan"/>.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pendingCount;
            }
        }
    }

    /// <summary>This tick's events, in the order they were produced.</summary>
    /// <returns>The batch. It is valid until the next tick begins.</returns>
    public ReadOnlySpan<SessionEvent> AsSpan() => new(_current, 0, _currentCount);

    /// <summary>Walks this tick's events.</summary>
    /// <returns>An enumerator over <see cref="AsSpan"/>.</returns>
    public ReadOnlySpan<SessionEvent>.Enumerator GetEnumerator() => AsSpan().GetEnumerator();

    /// <summary>Appends an event. Called from a transport thread on admission and from the tick on close.</summary>
    /// <param name="sessionEvent">The event.</param>
    internal void Append(in SessionEvent sessionEvent)
    {
        lock (_gate)
        {
            if (_pendingCount == _pending.Length)
            {
                Array.Resize(ref _pending, _pending.Length * 2);
            }

            _pending[_pendingCount++] = sessionEvent;
        }
    }

    /// <summary>
    /// Hands the accumulated events to the tick and starts a fresh batch. Called once per tick, from the tick side, before any system reads them.
    /// </summary>
    /// <returns>How many events this tick carries.</returns>
    internal int BeginTick()
    {
        lock (_gate)
        {
            // The outgoing buffer is cleared before it becomes the pending one, so an event's AppData and payload stop being reachable through this type the
            // tick after they were delivered. Without it a batch of disconnections would hold whatever the application attached to those sessions alive until
            // the same peak was reached again, which is a leak that only shows up under churn.
            Array.Clear(_current, 0, _currentCount);

            (_current, _pending) = (_pending, _current);
            _currentCount = _pendingCount;
            _pendingCount = 0;
            return _currentCount;
        }
    }

    /// <summary>Drops everything, delivered and pending. Called when the runtime stops.</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_current, 0, _currentCount);
            Array.Clear(_pending, 0, _pendingCount);
            _currentCount = 0;
            _pendingCount = 0;
        }
    }
}
