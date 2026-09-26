using JetBrains.Annotations;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine;

/// <summary>
/// One command as a system reads it: who sent it, which of that client's it is, the client frame it belongs to, and its value.
/// </summary>
/// <typeparam name="T">The command's struct, as the application declared it.</typeparam>
[PublicAPI]
public readonly struct ClientCommand<T> where T : unmanaged
{
    internal ClientCommand(SessionId session, ushort seq, uint clientTick, in T value, RealmId realm = default)
    {
        Session = session;
        Seq = seq;
        ClientTick = clientTick;
        Value = value;
        Realm = realm;
    }

    /// <summary>
    /// The realm the client held when it built the command (12-realms § 2.5) — the session's realm at <see cref="ClientTick"/>. A command whose realm-framed
    /// field was built in a realm the session has left never arrives (<c>ACK REALM_CHANGED</c>); one without such a field arrives with the realm it was
    /// built in, for the application to judge.
    /// </summary>
    public RealmId Realm { get; }

    /// <summary>Which session sent it.</summary>
    public SessionId Session { get; }

    /// <summary>
    /// The client's sequence number. One space per session across every command type; it wraps, and is compared with RFC 1982 serial arithmetic.
    /// </summary>
    public ushort Seq { get; }

    /// <summary>The client's tick when it produced the batch this command arrived in. Shared by every command of that batch.</summary>
    public uint ClientTick { get; }

    /// <summary>The decoded command.</summary>
    public T Value { get; }
}

/// <summary>
/// This tick's commands of one type, in per-session arrival order.
/// </summary>
/// <typeparam name="T">The command's struct.</typeparam>
/// <remarks>
/// <para>
/// <b>Order within a session is the contract; order between sessions is not.</b> A session is drained by exactly one worker, which appends its records to that
/// worker's own segment in arrival order, so walking the segments preserves each client's order without preserving any order between clients — which nothing
/// needs (SUB-08, archive/Subscriptions/foundation/05 § 4.2).
/// </para>
/// <para>
/// <b>Nothing here allocates.</b> The batch is a view over the drain's buffers; the enumerator is a struct whose <c>Current</c> is a reference to its own
/// field, refreshed per step, so <c>foreach (ref readonly var c in …)</c> costs one copy of the command and no heap traffic at all.
/// </para>
/// </remarks>
[PublicAPI]
public readonly struct CommandBatch<T> where T : unmanaged
{
    private readonly CommandTypeBuffer _buffer;
    private readonly int _segment;
    private readonly int _start;
    private readonly int _count;

    internal CommandBatch(CommandTypeBuffer buffer)
    {
        _buffer = buffer;
        _segment = -1;
        _start = 0;
        _count = 0;
    }

    internal CommandBatch(CommandTypeBuffer buffer, int segment, int start, int count)
    {
        _buffer = buffer;
        _segment = segment;
        _start = start;
        _count = count;
    }

    /// <summary>How many commands this batch carries.</summary>
    public int Count
    {
        get
        {
            if (_buffer == null)
            {
                return 0;
            }

            if (_segment >= 0)
            {
                return _count;
            }

            var total = 0;
            for (var i = 0; i < _buffer.SegmentCount; i++)
            {
                total += _buffer.CountIn(i);
            }

            return total;
        }
    }

    /// <summary>Walks the batch.</summary>
    /// <returns>The enumerator.</returns>
    public Enumerator GetEnumerator() => new(_buffer, _segment, _start, _count);

    /// <summary>
    /// The newest command this session sent this tick, for a type declared <see cref="CommandCoalesce.LatestPerSession"/>.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="command">The command.</param>
    /// <returns><see langword="false"/> when that session sent none this tick.</returns>
    /// <remarks>
    /// O(1): the drain overwrites the session's single record in place rather than appending, and keeps the index of it. That is also why a coalesced type's
    /// batch carries exactly one record per session — the older ones never existed as records at all.
    /// </remarks>
    public bool TryGetLatest(SessionId session, out ClientCommand<T> command)
    {
        command = default;
        if (_buffer == null || !_buffer.TryGetSessionRange(session, out var segment, out var start, out var count) || count == 0)
        {
            return false;
        }

        // The newest is the LAST of the session's run, which for a coalesced type is its only one.
        command = Read(_buffer, segment, start + count - 1);
        return true;
    }

    /// <summary>
    /// Narrows the batch to one session's commands, in the order it sent them.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>The narrowed batch, empty when that session sent none this tick.</returns>
    /// <remarks>
    /// O(1), and it is what lets a parallel system apply each client's commands on the worker that already owns that client's entities, instead of one system
    /// walking the whole tick's batch (01-model § 7).
    /// </remarks>
    public CommandBatch<T> ForSession(SessionId session)
    {
        if (_buffer == null || !_buffer.TryGetSessionRange(session, out var segment, out var start, out var count))
        {
            return new CommandBatch<T>(_buffer, 0, 0, 0);
        }

        return new CommandBatch<T>(_buffer, segment, start, count);
    }

    internal static ClientCommand<T> Read(CommandTypeBuffer buffer, int segment, int index)
    {
        var header = buffer.HeadersIn(segment)[index];
        var payload = buffer.PayloadsIn(segment).Slice(index * buffer.Stride, buffer.Stride);
        return new ClientCommand<T>(SessionId.FromValue(header.Session), header.Seq, header.ClientTick, in MemoryMarshal.AsRef<T>(payload),
            new RealmId(header.Realm));
    }

    /// <summary>Walks a <see cref="CommandBatch{T}"/>.</summary>
    [PublicAPI]
    public struct Enumerator
    {
        private readonly CommandTypeBuffer _buffer;
        private readonly int _onlySegment;
        private readonly int _start;
        private readonly int _count;

        private int _segment;
        private int _index;
        private int _remaining;
        private ClientCommand<T> _current;

        internal Enumerator(CommandTypeBuffer buffer, int segment, int start, int count)
        {
            _buffer = buffer;
            _onlySegment = segment;
            _start = start;
            _count = count;
            _segment = segment >= 0 ? segment : 0;
            _index = segment >= 0 ? start - 1 : -1;
            _remaining = segment >= 0 ? count : -1;
            _current = default;
        }

        /// <summary>
        /// The command at the cursor.
        /// </summary>
        /// <remarks>
        /// A reference into the enumerator itself, so <c>foreach (ref readonly var c in …)</c> binds without a copy at the call site and stays valid exactly as
        /// long as <c>foreach</c> keeps it — until the next step.
        /// </remarks>
        [UnscopedRef]
        public readonly ref readonly ClientCommand<T> Current => ref _current;

        /// <summary>Advances the cursor.</summary>
        /// <returns><see langword="false"/> at the end of the batch.</returns>
        public bool MoveNext()
        {
            if (_buffer == null)
            {
                return false;
            }

            if (_onlySegment >= 0)
            {
                if (_remaining <= 0)
                {
                    return false;
                }

                _remaining--;
                _index++;
                _current = Read(_buffer, _onlySegment, _index);
                return true;
            }

            while (true)
            {
                _index++;
                if (_index < _buffer.CountIn(_segment))
                {
                    _current = Read(_buffer, _segment, _index);
                    return true;
                }

                _segment++;
                _index = -1;
                if (_segment >= _buffer.SegmentCount)
                {
                    return false;
                }
            }
        }
    }
}

/// <summary>
/// What an application system reads commands through, and answers them with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here describes exactly one tick.</b> The Engine-Pre drain fills the buffers before the application's track runs, so a command is visible for
/// the tick it was drained into and for no other — never zero ticks, never two (SUB-08). A system that wants a command to outlive its tick copies it into
/// state, which is what state is for.
/// </para>
/// <para>
/// <b>Semantic validation belongs to the caller.</b> What reaches here has passed the wire's own checks, the declaration's role list, its rate limit and its
/// stateless pre-check. Range against the current world, cooldowns and ownership are the system's, because the state they must agree with is the system's
/// (01-model § 7).
/// </para>
/// </remarks>
[PublicAPI]
public sealed class SubscriptionsCommands
{
    private readonly SubscriptionsIngress _ingress;
    private readonly EventHub _events;
    private readonly int _slot;

    internal SubscriptionsCommands(SubscriptionsIngress ingress, EventHub events = null, int slot = 0)
    {
        ArgumentNullException.ThrowIfNull(ingress);
        _ingress = ingress;
        _events = events;
        _slot = slot;
    }

    /// <summary>
    /// Sends an event to the clients its declaration routes it to (09 § 11). Encoded once, after this tick's projection, and the same bytes reach every
    /// session it matches; a session that misses frames receives it with its next one while the event log holds the tick, and is told how many it lost after.
    /// </summary>
    /// <typeparam name="T">A type declared with <see cref="SubscriptionsRegistry.Event{T}"/>.</typeparam>
    /// <param name="evt">The event; copied.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not a declared event.</exception>
    /// <remarks>
    /// <para>
    /// Each worker records into its own buffer; call it on the thread the <c>ctx</c> was handed to. A view kept past its tick or shared with other threads
    /// stays correct — each buffer is gated — but its events land in another worker's order. A tick's events travel in worker order, then call order, which
    /// is not the order the systems ran in across workers.
    /// </para>
    /// <para>
    /// An <see cref="EntityId"/> field travels as the entity's netId — 0, "unknown", for an entity the client could not know. A session bound to no profile
    /// hears broadcasts and its own <see cref="EmitTo{T}"/>, in frames of events alone; the routes that need a view never reach it.
    /// </para>
    /// </remarks>
    public void Emit<T>(in T evt) where T : unmanaged
    {
        if (_events == null)
        {
            throw new InvalidOperationException($"'{typeof(T).Name}' is not a declared event: declare it with Subscriptions.Event<{typeof(T).Name}>(…).");
        }

        _events.Emit(_slot, in evt);
    }

    /// <summary>
    /// Sends an event to one session: an event type declared with <see cref="EventBuilder{T}.RouteToSession"/> (09 § 11). To a session that is closed or
    /// unknown, it reaches nobody.
    /// </summary>
    /// <typeparam name="T">A type declared with <see cref="SubscriptionsRegistry.Event{T}"/> and routed to a session.</typeparam>
    /// <param name="session">The session.</param>
    /// <param name="evt">The event; copied.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not a declared event routed to a session.</exception>
    public void EmitTo<T>(SessionId session, in T evt) where T : unmanaged
    {
        if (_events == null)
        {
            throw new InvalidOperationException($"'{typeof(T).Name}' is not a declared event: declare it with Subscriptions.Event<{typeof(T).Name}>(…).");
        }

        _events.EmitTo(_slot, session, in evt);
    }

    /// <summary>
    /// This tick's session lifecycle events — <c>Opened</c>, <c>Closed</c> — delivered in Engine-Pre, so a system reacts to one with an ordinary transaction in
    /// the same tick.
    /// </summary>
    public ReadOnlySpan<SessionEvent> SessionEvents => _ingress.Sessions.Events.AsSpan();

    /// <summary>
    /// Asks something of a session: the profile it is bound to, the observers it carries, the entity it controls, its budget, or a kick.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="worker">
    /// Which worker's segment to stage on: the tick context's worker index inside a chunked parallel system, and 0 on the tick driver or in a serial one.
    /// </param>
    /// <returns>A builder whose calls are recorded and applied by the next tick's prologue.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is how a session gets a profile</b>, and without one it receives nothing: interest is gathered per profile, so a session bound to none is not
    /// in any tick's session set. The natural place to call it is the <see cref="SessionEvents"/> loop, on the <c>Opened</c> event.
    /// </para>
    /// <para>
    /// <b>Nothing happens immediately.</b> A session row belongs to the tick (SUB-05); the calls are appended to a per-worker segment and applied
    /// single-threaded at the start of the next tick, before interest is gathered. That is what makes it safe to call from any system on any worker.
    /// </para>
    /// <para>
    /// <b>One worker per segment, and an out-of-range index is refused rather than clamped.</b> A segment's append is unsynchronized — that is what makes it
    /// free — so two threads sharing one loses records, duplicates them, or throws out of an <c>Array.Resize</c>. Clamping a too-large index onto the last
    /// segment would silently arrange exactly that, so it throws instead: a system that passes the wrong index learns immediately rather than corrupting a
    /// neighbour's list under load.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="worker"/> is negative, or at or above the worker count this tick dispatched.
    /// </exception>
    public SessionRequest Session(SessionId session, int worker = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(worker);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(worker, _ingress.Requests.WorkerCount);
        return _ingress.Requests.Request(worker, session);
    }

    /// <summary>
    /// Places a session's spatial observers for THIS tick.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="position">Where it is looking from, in world space.</param>
    /// <returns><see langword="false"/> when the session is closing or gone.</returns>
    /// <remarks>
    /// <para>
    /// <b>It applies now, unlike everything on <see cref="Session"/>.</b> A profile, a budget or the entity a session controls are configuration, and the
    /// request log stages them so they take effect from a known boundary. A viewpoint is not configuration — it is this tick's position — and a tick of
    /// latency on it means every session resolves its interest around where it was rather than where it is.
    /// </para>
    /// <para>
    /// <b>Call it from an application system, once per session per tick, before the replication track runs.</b> A session with a <c>Sphere</c> observer that
    /// has never been placed sees nothing at all: an unplaced session is nowhere, not at the origin, because a default position is a legal world position and
    /// silently giving everyone a sphere around it is worse than giving them nothing.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The engine holds several realms and the session is in none: say which, with <see cref="Place(SessionId, RealmId, Vector3D)"/> (12-realms § 1.3).
    /// </exception>
    public bool Place(SessionId session, Vector3D position)
    {
        if (_ingress.MultiRealm && _ingress.Sessions.RealmOf(session) is < 0 or RealmId.NoneValue)
        {
            throw new InvalidOperationException(
                $"{session} is in no realm, and this engine holds several: place it with Place(session, realm, position) (12-realms § 1.3).");
        }

        return _ingress.Sessions.SetViewpoint(session, position);
    }

    /// <summary>
    /// Places a session in <paramref name="realm"/>, looking from <paramref name="position"/>, for THIS tick (R4.3, 12-realms § 2.2). A realm change is one
    /// <c>RESET</c> frame carrying the new realm's <c>REALM</c> block, published this tick or retried until it is (SUB-29).
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="realm">A registered realm, not closing.</param>
    /// <param name="position">Where it is looking from, in the realm's space.</param>
    /// <returns><see langword="false"/> when the session is closing or gone.</returns>
    /// <exception cref="ArgumentException">The realm is not registered, or is <see cref="RealmId.None"/> — use <see cref="Leave"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The realm is closing, or the session's profile follows an entity — its realm is that entity's, and two writers of one field is a bug.
    /// </exception>
    public bool Place(SessionId session, RealmId realm, Vector3D position)
    {
        CheckRealmTarget(session, realm);
        return _ingress.Sessions.SetRealm(session, realm.Value, placed: true, position);
    }

    /// <summary>
    /// Puts a session in <paramref name="realm"/> with no viewpoint (12-realms § 2.2): what a World, ClientRegion or Aggregate-only session needs; a Sphere sees
    /// nothing there until placed. Applied this tick, as <see cref="Place(SessionId, RealmId, Vector3D)"/>, with the same refusals.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="realm">A registered realm, not closing.</param>
    /// <returns><see langword="false"/> when the session is closing or gone.</returns>
    public bool Enter(SessionId session, RealmId realm)
    {
        CheckRealmTarget(session, realm);
        return _ingress.Sessions.SetRealm(session, realm.Value, placed: false, default);
    }

    /// <summary>
    /// Takes a session out of every realm (12-realms § 1.6): its client is told with a <c>RESET</c> carrying <c>REALM(NONE)</c>, and it hears only the events
    /// addressed to it. Applied this tick.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns><see langword="false"/> when the session is closing or gone.</returns>
    /// <exception cref="InvalidOperationException">The session's profile follows an entity, whose realm is its own.</exception>
    public bool Leave(SessionId session)
    {
        CheckNotAnchored(session);
        return _ingress.Sessions.SetRealm(session, RealmId.NoneValue, placed: false, default);
    }

    /// <summary>
    /// The realm a session's client holds: the realm of its last published <c>RESET</c> (12-realms § 2.2), <see cref="RealmId.None"/> before its first and
    /// while it is in none.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>The committed realm.</returns>
    public RealmId RealmOf(SessionId session)
    {
        var state = _ingress.Frames?.StateOf(session);
        var realm = state != null && state.Generation == session.Generation ? state.CommittedRealm : -1;
        return realm < 0 ? RealmId.None : new RealmId((ushort)realm);
    }

    private void CheckRealmTarget(SessionId session, RealmId realm)
    {
        if (realm.IsNone)
        {
            throw new ArgumentException("RealmId.None is no realm to be in: take the session out of every realm with Leave(session).", nameof(realm));
        }

        var table = _ingress.Realms;
        if (table != null || realm.Value != RealmId.Default.Value)
        {
            var target = table?.TryGet(realm.Value)
                         ?? throw new ArgumentException($"{realm} is not registered: register it with Realms.Register first.", nameof(realm));
            if (target.Closing)
            {
                throw new InvalidOperationException($"{realm} is closing: nothing enters a realm being unregistered (RLM-06).");
            }
        }

        CheckNotAnchored(session);
    }

    private void CheckNotAnchored(SessionId session)
    {
        var profiles = _ingress.Profiles;
        var profile = _ingress.Sessions.ProfileIndex(session);
        if (profiles != null && profile >= 0 && profiles.SourceOf(profile) is ViewpointSource.Bound or ViewpointSource.Controlled)
        {
            throw new InvalidOperationException(
                $"{session} follows an entity (profile '{_ingress.Sessions.ProfileName(session)}'): its realm is that entity's, and moves with it (12-realms § 1.3).");
        }
    }

    /// <summary>
    /// Changes a session's Sphere radius from this tick on, within the range its profile declares — <c>Sphere(r, max: m)</c> (09 § 4).
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="radius">
    /// The radius, in metres: between the profile's own (its band's midpoint when it declares a leave radius) and its declared maximum. 0 returns to the
    /// profile's own.
    /// </param>
    /// <returns><see langword="false"/> when the session is closing or gone.</returns>
    /// <exception cref="InvalidOperationException">No profile is applied to the session yet, or its profile is not a Sphere.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The radius is outside the profile's declared range.</exception>
    /// <remarks>
    /// <para>
    /// <b>A player boarding an aircraft.</b> The change is geometry like an anchor move: the next frame sweeps the shell between the two radii — enters when
    /// it grows, leaves when it shrinks — with no reset, so the client keeps everything it already holds.
    /// </para>
    /// <para>
    /// <b>It applies now, like <see cref="Place(SessionId, Vector3D)"/>,</b> and holds until the session's profile changes, which returns it to the new profile's own radius.
    /// It is checked against the profile applied NOW: a profile requested through <see cref="Session"/> is applied by the next tick's prologue, so a
    /// radius for that profile is set from the next tick on.
    /// </para>
    /// <para>
    /// <b>The radius is the one sessions test</b> — the band's midpoint <c>(R + L) / 2</c> when the profile declares a leave radius — the same space as
    /// <c>max:</c>.
    /// </para>
    /// </remarks>
    public bool SetRadius(SessionId session, double radius)
    {
        var sessions = _ingress.Sessions;
        if (!sessions.IsOpen(session))
        {
            return false;
        }

        var profiles = _ingress.Frames?.Profiles;
        var profile = sessions.ProfileIndex(session);
        if (profiles == null || profile < 0)
        {
            throw new InvalidOperationException(
                "SetRadius needs the session's profile applied first; a profile requested this tick is applied by the next tick's prologue.");
        }

        if (profiles.RadiusOf(profile) <= 0)
        {
            throw new InvalidOperationException($"SetRadius applies to a Sphere profile; the session's profile '{profiles.NameOf(profile)}' is not one.");
        }

        var own = profiles.RadiusOf(profile);
        var max = profiles.MaxRadiusOf(profile);
        if (radius != 0 && (!double.IsFinite(radius) || radius < own || radius > max))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius,
                $"Profile '{profiles.NameOf(profile)}' lets a session's radius range over [{own}, {max}] m; declare Sphere(..., max: ...) to widen it.");
        }

        return _ingress.Sessions.SetRadius(session, radius == own ? 0d : radius);
    }

    /// <summary>
    /// Tells the engine that the entity in <paramref name="slot"/> of <paramref name="cluster"/> changed something a client sees (ADR-067: replication is
    /// explicit).
    /// </summary>
    /// <param name="cluster">The cluster the system is iterating.</param>
    /// <param name="slot">The entity's slot.</param>
    /// <remarks>
    /// <para>
    /// <b>A mark, not a send.</b> One interlocked OR into a per-cluster bitmap the fence already drains; duplicates are free. After the fence the entity is
    /// encoded once, compared against what was last encoded (so a spurious push costs an encode and no wire), and fanned out to the sessions around it.
    /// </para>
    /// <para>
    /// <b>The contract is the developer's.</b> A change that is never pushed is never sent. Spawns, destroys and <c>WriteSpatial</c> moves are pushed by the
    /// engine; a component written through <c>GetSpan</c> or <c>EntityRefMut</c> is not. A no-op for an archetype no push profile observes.
    /// </para>
    /// </remarks>
    public void Replicate<TArchetype>(in ClusterRef<TArchetype> cluster, int slot) where TArchetype : class
    {
        if ((uint)slot < 64u)
        {
            cluster.NotePushed(1UL << slot);
        }
    }

    /// <summary><see cref="Replicate{TArchetype}(in ClusterRef{TArchetype}, int)"/> for a set of slots of one cluster.</summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="slots">The slots, as a mask.</param>
    public void Replicate<TArchetype>(in ClusterRef<TArchetype> cluster, ulong slots) where TArchetype : class
    {
        if (slots != 0UL)
        {
            cluster.NotePushed(slots);
        }
    }

    /// <summary>
    /// <see cref="Replicate{TArchetype}(in ClusterRef{TArchetype}, int)"/> for an entity reached by id rather than by walking its cluster — the target of a
    /// command, resolved with <see cref="TryResolve"/> and opened with <c>OpenMut</c>.
    /// </summary>
    /// <param name="entity">The entity the system wrote.</param>
    /// <remarks>
    /// The same mark, in the same per-cluster word: one interlocked OR, duplicates free. Call it after the write, as with the cluster form; a no-op for an
    /// archetype no profile observes.
    /// </remarks>
    public void Replicate(in EntityRefMut entity) => entity.NotePushed();

    /// <summary>Every session that is open right now, for an application that has to touch all of them — placing their observers, most of it.</summary>
    public OpenSessionView OpenSessions => new(_ingress.Sessions);

    /// <summary>The kind a session named in its <c>HELLO</c>, or <see langword="null"/> when it is gone.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The kind.</returns>
    public string SessionKindOf(SessionId session) => _ingress.Sessions.SessionKind(session);

    /// <summary>Clusters asleep across every replicated archetype, refreshed by each read of <see cref="ProjectionBlocks"/>. A diagnostic.</summary>
    public int SleepingClusters;

    /// <summary>ENTER and LEAVE records published since start.</summary>
    /// <remarks>
    /// Enters far above leaves is a view still filling. Enters and leaves in step, at a rate far above what the world actually spawns and moves, is the same
    /// entities crossing the observers' boundary and crossing back.
    /// </remarks>
    public (long Entered, long Left) EnterFlow => _ingress.Frames == null ? default : _ingress.Frames.EnterFlow;

    /// <summary>Replication entries carried from one cluster to another since start, summed over every archetype.</summary>
    public long EntriesMigrated
    {
        get
        {
            var total = 0L;
            var states = _ingress.ReplicationStates;
            for (var i = 0; states != null && i < states.Length; i++)
            {
                total += states[i] == null ? 0 : states[i].EntriesMigrated;
            }

            return total;
        }
    }

    /// <summary>Network identities minted, released and reused since start.</summary>
    /// <remarks>Every archetype shares one allocator, so the first non-null state answers for all of them.</remarks>
    public (long Minted, long Released, long Reused) IdentityFlow
    {
        get
        {
            var states = _ingress.ReplicationStates;
            for (var i = 0; states != null && i < states.Length; i++)
            {
                if (states[i] != null)
                {
                    return states[i].NetIds.IdentityFlow;
                }
            }

            return default;
        }
    }

    /// <summary>The send path, measured while phase timing is on — see <c>SendPump.SendPath</c>.</summary>
    public (double WakeMsPerPublish, double WokenPerPublish, double QueueDelayUs, double SendUs, long SendsSync, long SendsAsync) SendPath =>
        _ingress.SendPump == null ? default : _ingress.SendPump.SendPath;

    /// <summary>Frames and bytes handed to links since start.</summary>
    public (long Frames, long Bytes) SendTotals => _ingress.SendPump == null ? default : (_ingress.SendPump.FramesSent, _ingress.SendPump.BytesSent);

    /// <summary>Mean microseconds a subscriptions chunk spends entering its epoch, and how many chunks were measured.</summary>
    public (double MeanUs, long Chunks) EpochEnter
    {
        get
        {
            var count = Volatile.Read(ref SubscriptionsExecSystemBase.EpochEnterCount);
            return count == 0
                ? default
                : (Volatile.Read(ref SubscriptionsExecSystemBase.EpochEnterTicks) * 1_000_000d / System.Diagnostics.Stopwatch.Frequency / count, count);
        }
    }

    /// <summary>The frame stage's span against the busy time inside it, and the concurrency the two imply.</summary>
    public (double SpanMs, double BusyMs, double Concurrency, double StartSpreadMs) FrameSpan => _ingress.Frames == null ? default : _ingress.Frames.ChunkSpan;

    /// <summary>
    /// The projection stage's serial blocks step per tick, split into the push-set preparation, the parked-entry drain and the push marks, and its parallel
    /// busy time per tick, in ms. Zero unless phase timing is on.
    /// </summary>
    public (double Prepare, double Drain, double Mark, double Busy) ProjectPrologueMs
    {
        get
        {
            var n = Volatile.Read(ref SubscriptionsProjectExecSystem.PrologueCount);
            if (n == 0)
            {
                return default;
            }

            var k = 1000d / System.Diagnostics.Stopwatch.Frequency / n;
            return (SubscriptionsProjectExecSystem.PrologueCreateTicks * k, SubscriptionsProjectExecSystem.PrologueDrainTicks * k,
                SubscriptionsProjectExecSystem.PrologueGatherTicks * k, Volatile.Read(ref SubscriptionsProjectExecSystem.ProjectBusyTicks) * k);
        }
    }

    /// <summary>The frame stage's single-threaded prologue, per tick, in ms.</summary>
    public (double Prologue, double Sweep, double Prepare) FramePrologueMs => _ingress.Frames == null ? default : _ingress.Frames.PrologueMs;

    /// <summary>The frame stage's effective worker count and parallel efficiency; zero unless phase timing is on.</summary>
    public (double Effective, double Efficiency, long Ticks) FrameBalance => _ingress.Frames == null ? default : _ingress.Frames.ChunkBalance;

    /// <summary>The frame stage's phases, in ms of CPU summed over workers since start. All zero unless phase timing was enabled.</summary>
    public (double Gather, double Sort, double Encode, double Publish) FramePhases =>
        _ingress.Frames == null ? default : _ingress.Frames.PhaseMilliseconds;

    /// <summary>Blocks the projection pass read, and blocks it declined because their cluster was dormant, since start.</summary>
    /// <remarks>
    /// The second number is zero unless the application enabled cluster dormancy, which is what makes the pair readable: a projection cost that did not
    /// move means one thing if nothing was declined and the opposite if most of it was.
    /// </remarks>
    public (long Projected, long Dormant) ProjectionBlocks
    {
        get
        {
            SleepingClusters = 0;
            var states = _ingress.ReplicationStates;
            if (states == null)
            {
                return (0L, 0L);
            }

            var projected = 0L;
            var dormant = 0L;
            for (var i = 0; i < states.Length; i++)
            {
                if (states[i] == null)
                {
                    continue;
                }

                projected += states[i].BlocksProjected;
                dormant += states[i].BlocksDormant;
                SleepingClusters += states[i].ClusterState?.SleepingClusterCount ?? 0;
            }

            return (projected, dormant);
        }
    }

    /// <summary>
    /// This tick's commands of one type.
    /// </summary>
    /// <typeparam name="T">The command's struct, as the application declared it.</typeparam>
    /// <returns>The batch, empty when nothing arrived.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> was never declared as a command type.</exception>
    public CommandBatch<T> Commands<T>() where T : unmanaged
    {
        var info = _ingress.Commands.ByStruct(typeof(T))
            ?? throw new InvalidOperationException(
                $"'{typeof(T).Name}' is not a declared command. Declare it with runtime.Subscriptions.Command<{typeof(T).Name}>(…) before Start, or the " +
                "catalog clients negotiate against would not name it and no client could ever send one.");

        var buffer = _ingress.Buffers.ByWireIdx(info.WireIdx);
        if (buffer != null && buffer.Stride != Unsafe.SizeOf<T>())
        {
            throw new InvalidOperationException(
                $"Command '{typeof(T).Name}' was bound at {buffer.Stride} bytes and measures {Unsafe.SizeOf<T>()} here. The struct's layout has to be the one " +
                "the decoder measured at Start.");
        }

        return new CommandBatch<T>(buffer);
    }

    /// <summary>
    /// Answers a command with a rejection, which reaches the client as an <c>ACK</c> record.
    /// </summary>
    /// <typeparam name="T">The command's struct.</typeparam>
    /// <param name="command">The command being refused.</param>
    /// <param name="reasonCode">An <see cref="AckReasons"/> code; 128-255 are the application's own.</param>
    /// <returns><see langword="false"/> when the tick's rejection log was full and the answer was dropped.</returns>
    /// <remarks>
    /// A command that is simply not applied sends nothing — the client learns only that its <c>seq</c> was consumed. Rejecting is for telling it WHY, and it
    /// costs a record in the next frame, so it is a decision rather than a default.
    /// </remarks>
    public bool Reject<T>(in ClientCommand<T> command, byte reasonCode) where T : unmanaged => Reject(command.Session, command.Seq, reasonCode);

    /// <summary>
    /// Answers a session's command by sequence number.
    /// </summary>
    /// <param name="session">Whose command it was.</param>
    /// <param name="seq">Its sequence number.</param>
    /// <param name="reasonCode">An <see cref="AckReasons"/> code.</param>
    /// <returns><see langword="false"/> when the tick's rejection log was full.</returns>
    public bool Reject(SessionId session, ushort seq, byte reasonCode) => _ingress.Buffers.Acks.Add(session, seq, reasonCode);

    /// <summary>
    /// Resolves an entity reference a client sent — a <c>netId</c> on the wire — back to the entity it names, if the session holds it (SUB-26).
    /// </summary>
    /// <param name="session">The session that sent the reference.</param>
    /// <param name="netId">The network identity, as the command carried it.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>
    /// <see langword="false"/> when nothing live holds that identity, the session is gone, or the session does not hold the entity: it is neither the
    /// session's controlled entity nor inside the geometry its client was last told about.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>A client can only name what it was shown</b> (01 § 7). What a session holds is geometric (SUB-16), so this is the geometric test against the
    /// session's committed geometry — the anchor, radius and delivered cells of its last published frame, or its committed hull, or its World cursor — on the
    /// entity's v̂. An entity that left the view this tick is still accepted: the client saw it when it sent the command. One that it learned of only from an
    /// event (an attacker beyond its view) is refused — events inform, they do not grant reach.
    /// </para>
    /// <para>
    /// <b>An unknown identity is not an error.</b> A client may name an entity that has since left, or one it was never shown; the answer is "no", and the
    /// system decides what that means. Treating it as malformed input would let one stale reference close a connection.
    /// </para>
    /// <para>
    /// Costs an EntityMap probe through freshly opened chunk accessors and a geometric test, ≈ 1–2 µs: call it for the commands that name entities, not per
    /// entity per tick. It is committed-state accurate: a session served every few ticks (a rate class, overload) is judged against its last published frame,
    /// while the entity's v̂ is this tick's — near the edge the two can disagree by the motion of those ticks.
    /// </para>
    /// </remarks>
    public bool TryResolve(SessionId session, uint netId, out EntityId entity)
    {
        entity = EntityId.Null;
        if (!_ingress.Sessions.IsOpen(session) || !_ingress.NetIds.TryGet(netId, out var candidate))
        {
            return false;
        }

        var frames = _ingress.Frames;
        if (frames == null || !frames.Holds(session, netId, candidate))
        {
            return false;
        }

        entity = candidate;
        return true;
    }

    /// <summary>
    /// Resolves a <c>netId</c> to the live entity that holds it, whatever the session holds — for tools that are not clients (an admin console, a replay).
    /// A client's command goes through <see cref="TryResolve"/>, which refuses what its client was never shown.
    /// </summary>
    /// <param name="session">The session that sent the reference.</param>
    /// <param name="netId">The network identity.</param>
    /// <param name="entity">The entity.</param>
    /// <returns><see langword="false"/> when nothing live holds that identity, or the session is gone.</returns>
    public bool TryResolveAny(SessionId session, uint netId, out EntityId entity)
    {
        entity = EntityId.Null;
        if (!_ingress.Sessions.IsOpen(session) || !_ingress.NetIds.TryGet(netId, out var candidate) || _ingress.Frames?.IsLive(netId, candidate) != true)
        {
            return false;
        }

        entity = candidate;
        return true;
    }

    /// <summary>
    /// The highest sequence number drained into a tick for a session, which is what <c>SELF.lastSeq</c> reports.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="lastSeq">The sequence number.</param>
    /// <returns><see langword="false"/> when that session has never sent a command.</returns>
    /// <remarks>
    /// Every sequence at or below it was applied, rejected or coalesced away, which is exactly what a client's prediction reconciliation needs
    /// (03-wire-protocol § 12 W31).
    /// </remarks>
    public bool TryGetLastSeq(SessionId session, out ushort lastSeq)
    {
        var row = _ingress.RowOf(session);
        lastSeq = row?.LastSeq ?? 0;
        return row is { HasLastSeq: true };
    }
}

/// <summary>Every open session, as a <c>foreach</c> an application can write without the table being public.</summary>
/// <remarks>
/// A view rather than a copy: the sessions are walked straight out of the table's open list, so touching all of them costs no allocation. It is valid for
/// the tick that produced it and must not be stored — a slot recycled between ticks would be walked as though it still held its previous occupant.
/// </remarks>
[PublicAPI]
public readonly struct OpenSessionView
{
    private readonly SessionTable _table;

    internal OpenSessionView(SessionTable table) => _table = table;

    /// <summary>Walks the open sessions.</summary>
    /// <returns>The enumerator.</returns>
    public Enumerator GetEnumerator() => new(_table);

    /// <summary>The cursor over the open sessions.</summary>
    [PublicAPI]
    public struct Enumerator
    {
        private SessionTable.OpenSessionEnumerator _inner;

        internal Enumerator(SessionTable table) => _inner = table.GetEnumerator();

        /// <summary>The session at the cursor.</summary>
        public SessionId Current => _inner.Current;

        /// <summary>Advances the cursor.</summary>
        /// <returns><see langword="false"/> at the end.</returns>
        public bool MoveNext() => _inner.MoveNext();
    }
}
