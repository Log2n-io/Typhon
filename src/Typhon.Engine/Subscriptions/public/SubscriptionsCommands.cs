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
    internal ClientCommand(SessionId session, ushort seq, uint clientTick, in T value)
    {
        Session = session;
        Seq = seq;
        ClientTick = clientTick;
        Value = value;
    }

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
/// needs (SUB-08, foundation/05 § 4.2).
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
        return new ClientCommand<T>(SessionId.FromValue(header.Session), header.Seq, header.ClientTick, in MemoryMarshal.AsRef<T>(payload));
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
                _current = CommandBatch<T>.Read(_buffer, _onlySegment, _index);
                return true;
            }

            while (true)
            {
                _index++;
                if (_index < _buffer.CountIn(_segment))
                {
                    _current = CommandBatch<T>.Read(_buffer, _segment, _index);
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

    internal SubscriptionsCommands(SubscriptionsIngress ingress)
    {
        ArgumentNullException.ThrowIfNull(ingress);
        _ingress = ingress;
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
    public bool Place(SessionId session, Vector3D position) => _ingress.Sessions.SetViewpoint(session, position);

    /// <summary>Every session that is open right now, for an application that has to touch all of them — placing their observers, most of it.</summary>
    public OpenSessionView OpenSessions => new(_ingress.Sessions);

    /// <summary>The kind a session named in its <c>HELLO</c>, or <see langword="null"/> when it is gone.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The kind.</returns>
    public string SessionKindOf(SessionId session) => _ingress.Sessions.SessionKind(session);

    /// <summary>
    /// Last tick: how many interest cells the broad phase resolved, and how many sessions were served from a resolution somebody else paid for.
    /// </summary>
    /// <returns>The cells resolved and the sessions shared.</returns>
    /// <remarks>
    /// <b>A diagnostic, and the one the cell-keyed broad phase is judged by.</b> Its whole benefit is that co-located observers resolve once, so a
    /// deployment whose sessions never share a cell pays the grouping's sort and gets nothing back — and no timing comparison can tell that case from a
    /// design that does not work, because both look like "no change". Reading it is how an operator, or a measurement, tells them apart.
    /// </remarks>
    public (long Cells, long SessionsShared) InterestSharingLastTick =>
        _ingress.Interest == null ? (0L, 0L) : (_ingress.Interest.CellsResolved, _ingress.Interest.SessionsShared);

    /// <summary>
    /// How many frames since start were built as a difference against the session's previous tick, and how many walked the whole view (15 § 3.2).
    /// </summary>
    /// <remarks>
    /// <b>Cumulative, unlike <see cref="InterestSharingLastTick"/>.</b> It exists for the same reason: a measurement showing no change means "the
    /// difference does not pay" or "the difference never happened", and those call for opposite next steps. A ratio near zero is the second.
    /// </remarks>
    public (long Difference, long Full) GatherShape =>
        _ingress.Frames == null ? (0L, 0L) : (_ingress.Frames.TemporalGathers, _ingress.Frames.FullGathers);

    /// <summary>Hit slots the difference read, and hit slots it carried forward without reading, since start (15 § 3.2).</summary>
    public (long Visited, long Carried) GatherSlots => _ingress.Frames == null ? (0L, 0L) : _ingress.Frames.TemporalSlots;

    /// <summary>Blocks the projection pass read, and blocks it declined because their cluster was dormant, since start.</summary>
    /// <remarks>
    /// The second number is zero unless the application enabled cluster dormancy, which is what makes the pair readable: a projection cost that did not
    /// move means one thing if nothing was declined and the opposite if most of it was.
    /// </remarks>
    /// <summary>Clusters asleep across every replicated archetype, refreshed by each read of <see cref="ProjectionBlocks"/>. A diagnostic.</summary>
    public int SleepingClusters;

    /// <summary>Why the incremental path fell back to the full walk: reset, forced, incomplete view, or behind by more
    /// than one tick.</summary>
    public (long Reset, long Forced, long Incomplete, long Behind) FullGatherCauses =>
        _ingress.Frames == null ? default : _ingress.Frames.FullGatherCauses;

    /// <summary>What the reduced gather's visited slots were made of: newly entered, named by the change mask, owed by an earlier frame.</summary>
    public (long Entered, long Changed, long Owed) VisitParts => _ingress.Frames == null ? default : _ingress.Frames.VisitParts;

    /// <summary>ENTER and LEAVE records published since start, beside the enters the per-frame budget deferred.</summary>
    /// <remarks>
    /// <b>The ratio separates a view still filling from one being re-told what it already knew</b>, and the two want opposite fixes. Enters far above
    /// leaves is a queue draining, and it drains faster with a larger budget. Enters and leaves in step, at a rate far above what the world actually
    /// spawns and moves, is the same entities crossing the interest boundary and crossing back — there a larger budget spends more wire on the same
    /// entities arriving again. Read it beside <see cref="ViewFill"/>, which says whether the client's world is still growing.
    /// </remarks>
    public (long Entered, long Left, long Deferred) EnterFlow => _ingress.Frames == null ? default : _ingress.Frames.EnterFlow;

    /// <summary>Mean entities a client holds, and mean enters still owed to it, over the frames that published.</summary>
    public (double Known, double Owed, long Frames) ViewFill => _ingress.Frames == null ? default : _ingress.Frames.ViewFill;

    /// <summary>Cluster candidates the broad phase collected, and how many a session accepted.</summary>
    public (long Collected, long Accepted) ClusterCandidates =>
        _ingress.Interest == null ? default : (_ingress.Interest.ClusterCandidatesCollected, _ingress.Interest.ClusterCandidatesAccepted);

    /// <summary>
    /// Runs referenced rather than encoded, the records they carried, the clusters that offered one and could not be shared, and the records the
    /// projection encoded once (17 § 18).
    /// </summary>
    public (long Runs, long Records, long Refused, long Built) SharedRuns
    {
        get
        {
            if (_ingress.Frames == null)
            {
                return default;
            }

            var use = _ingress.Frames.SharedRunUse;
            var built = 0L;
            var states = _ingress.Interest?.ReplicationStates;
            if (states != null)
            {
                for (var i = 0; i < states.Length; i++)
                {
                    built += states[i] == null ? 0 : states[i].SharedRunRecords;
                }
            }

            return (use.Runs, use.Records, use.Refused, built);
        }
    }

    /// <summary>Clusters that published a shared cluster run, and why the others did not (17 § 18).</summary>
    public (long Published, long Released, long Init, long NoChange) SharedRunSkips
    {
        get
        {
            var states = _ingress.Interest?.ReplicationStates;
            var t = (0L, 0L, 0L, 0L);
            if (states != null)
            {
                for (var i = 0; i < states.Length; i++)
                {
                    if (states[i] == null)
                    {
                        continue;
                    }

                    var s = states[i].SharedRunSkips;
                    t = (t.Item1 + s.Published, t.Item2 + s.Released, t.Item3 + s.Init, t.Item4 + s.NoChange);
                }
            }

            return t;
        }
    }

    /// <summary>Why a run with something to say did not reference shared bytes (17 § 18).</summary>
    public (long Gated, long NoRun, long NotReached) SharedRunMisses =>
        _ingress.Frames == null ? default : _ingress.Frames.SharedRunMisses;

    /// <summary>
    /// Slots the projection named as changed, against the records the frame stage actually emitted — Layer 4's sharing ratio.
    /// </summary>
    public (long ChangedSlots, long Records) ShareCensus
    {
        get
        {
            if (_ingress.Frames == null)
            {
                return default;
            }

            var changed = 0L;
            var states = _ingress.Interest?.ReplicationStates;
            if (states != null)
            {
                for (var i = 0; i < states.Length; i++)
                {
                    changed += states[i] == null ? 0 : states[i].ChangedSlotsPublished;
                }
            }

            return (changed, _ingress.Frames.RecordsEncoded);
        }
    }

    /// <summary>Mean microseconds a subscriptions chunk spends entering its epoch, and how many chunks were measured.</summary>
    public (double MeanUs, long Chunks) EpochEnter
    {
        get
        {
            var count = Volatile.Read(ref Internals.SubscriptionsExecSystemBase.EpochEnterCount);
            return count == 0
                ? default
                : (Volatile.Read(ref Internals.SubscriptionsExecSystemBase.EpochEnterTicks) * 1_000_000d / System.Diagnostics.Stopwatch.Frequency / count, count);
        }
    }

    /// <summary>The frame stage's span against the busy time inside it, and the concurrency the two imply.</summary>
    public (double SpanMs, double BusyMs, double Concurrency, double StartSpreadMs) FrameSpan => _ingress.Frames == null ? default : _ingress.Frames.ChunkSpan;

    /// <summary>The frame stage's single-threaded prologue, per tick, in ms.</summary>
    public (double Prologue, double Sweep, double Prepare) FramePrologueMs => _ingress.Frames == null ? default : _ingress.Frames.PrologueMs;

    /// <summary>The frame stage's effective worker count and parallel efficiency; zero unless phase timing is on.</summary>
    public (double Effective, double Efficiency, long Ticks) FrameBalance => _ingress.Frames == null ? default : _ingress.Frames.ChunkBalance;

    /// <summary>Interest runs the gather walked, and how many of them had nothing to say.</summary>
    public (long Walked, long Empty) GatherRunShape => _ingress.Frames == null ? default : _ingress.Frames.GatherShape;

    /// <summary>Retained slots read in full because the block's change mask named another tick, and the runs that caused it.</summary>
    public (long Slots, long Runs) StaleMask => _ingress.Frames == null ? default : _ingress.Frames.StaleMask;

    /// <summary>The frame stage's phases, in ms of CPU summed over workers since start. All zero unless phase timing was enabled.</summary>
    public (double Gather, double Select, double Sweep, double Sort, double Encode, double Publish) FramePhases =>
        _ingress.Frames == null ? default : _ingress.Frames.PhaseMilliseconds;

    /// <summary>Blocks the projection pass read, and blocks it declined because their cluster was dormant, since start.</summary>
    public (long Projected, long Dormant) ProjectionBlocks
    {
        get
        {
            SleepingClusters = 0;
            var states = _ingress.Interest?.ReplicationStates;
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
    /// Resolves an entity reference a client sent — a <c>netId</c> on the wire — back to the entity it names.
    /// </summary>
    /// <param name="session">The session that sent the reference.</param>
    /// <param name="netId">The network identity, as the command carried it.</param>
    /// <param name="entity">The entity.</param>
    /// <returns><see langword="false"/> when nothing live holds that identity, or the session is gone.</returns>
    /// <remarks>
    /// <para>
    /// <b>An unknown identity is not an error.</b> A client may name an entity that has since left, or one it was never shown; the answer is "no", and the
    /// system decides what that means. Treating it as malformed input would let one stale reference close a connection.
    /// </para>
    /// <para>
    /// <b>The known-set half of this check is not built yet.</b> 01-model § 7 requires that a client can only target what it was shown, which needs the
    /// per-session known-set that P1-13a builds. Today the identity must merely be live and bound, so a client that guesses a valid netId is not refused for
    /// it. That gap is stated here rather than hidden: the clause is added where the known-set arrives, and nothing above this line has to change for it.
    /// </para>
    /// </remarks>
    public bool TryResolve(SessionId session, uint netId, out EntityId entity)
    {
        entity = EntityId.Null;
        return _ingress.Sessions.IsOpen(session) && _ingress.NetIds.TryGet(netId, out entity);
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
