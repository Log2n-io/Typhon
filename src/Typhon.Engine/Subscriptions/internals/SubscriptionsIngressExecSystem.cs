using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// One session's inbound path: the ring a transport thread frames into, the per-type token buckets that gate it, and the region and sequence the tick keeps.
/// </summary>
/// <remarks>
/// <b>Two owners, split by field and not by lock.</b> The ring is SPSC and needs neither side to hold anything; the buckets and the drop counters belong to
/// the transport thread that owns the connection; the region, the last sequence and the delivered counters belong to the tick. Nothing here is written by
/// both, which is what makes the whole path lock-free once a session exists (SUB-05's shape, one level below the session table).
/// </remarks>
internal sealed class SessionIngress
{
    /// <summary>Creates a row for a session that has just taken a ring.</summary>
    /// <param name="session">The identity.</param>
    /// <param name="lease">Its ring.</param>
    /// <param name="commandTypeCount">How many command types the catalog declares, which sizes the token buckets.</param>
    public SessionIngress(SessionId session, in IngressRingLease lease, int commandTypeCount)
    {
        Session = session;
        Lease = lease;
        Tokens = new double[commandTypeCount];
        BucketStamp = new long[commandTypeCount];
    }

    /// <summary>Whose row this is.</summary>
    public SessionId Session { get; }

    /// <summary>The ring, and the proof the pool handed it out.</summary>
    public IngressRingLease Lease { get; }

    /// <summary>The ring itself.</summary>
    public IngressRing Ring => Lease.Ring;

    /// <summary>Tokens left in each command type's bucket. Transport-thread owned.</summary>
    public double[] Tokens { get; }

    /// <summary>When each bucket was last refilled, as a <see cref="Stopwatch"/> timestamp. Transport-thread owned.</summary>
    public long[] BucketStamp { get; }

    /// <summary>Whether the buckets have been primed. Transport-thread owned.</summary>
    public bool BucketsPrimed { get; set; }

    /// <summary>Commands refused on the transport thread — past the rate, refused by role, or failing the pre-check. Transport-thread owned.</summary>
    public long RefusedCommands;

    /// <summary>
    /// Commands refused for ignoring the catalog's declared limits — past a command's rate, or sent by a role that may not — which the abuse rule counts
    /// (SUB-27). A pre-check refusal or an invalid region is not in it: those are game outcomes or client bugs, not a client ignoring its limits.
    /// Transport-thread owned.
    /// </summary>
    public long PolicyRefusals;

    /// <summary>Whole <c>COMMANDS</c> messages refused over the session's inbound budget. Transport-thread owned.</summary>
    public long OverBudgetMessages;

    /// <summary>The tick <see cref="RefusalAcksThisTick"/> counts for. Tick owned.</summary>
    public long RefusalAckTick = long.MinValue;

    /// <summary>Transport-side refusals placed in the tick's acknowledgement log this tick. Tick owned.</summary>
    public int RefusalAcksThisTick;

    /// <summary>
    /// Transport-side refusals past <see cref="SubscriptionsIngress.RefusalAcksPerTick"/> in one tick: settled by <c>lastSeq</c>, not acknowledged, so one
    /// session cannot take the whole shared log from the others. Tick owned.
    /// </summary>
    public long RefusalAcksCapped;

    /// <summary>Commands the ring had no room for. Transport-thread owned.</summary>
    public long DroppedCommands;

    /// <summary>The session's footprint, once it has sent a valid one. Tick owned.</summary>
    public ClientRegionCommand Region;

    /// <summary>Whether <see cref="Region"/> holds anything. Tick owned.</summary>
    public bool HasRegion;

    /// <summary>The highest sequence drained into a tick, as <c>SELF.lastSeq</c> reports it. Tick owned.</summary>
    public ushort LastSeq;

    /// <summary>Whether <see cref="LastSeq"/> means anything yet. Tick owned.</summary>
    public bool HasLastSeq;

    /// <summary>Commands drained into a tick for this session. Tick owned.</summary>
    public long DrainedCommands;

    /// <summary>
    /// Takes one token from a command type's bucket, refilling it from the clock first.
    /// </summary>
    /// <param name="typeIndex">The command type's wire index.</param>
    /// <param name="perSecond">The sustained rate; zero or less means the type is not rate limited.</param>
    /// <param name="burst">The bucket's depth.</param>
    /// <param name="now">The current <see cref="Stopwatch"/> timestamp.</param>
    /// <returns><see langword="false"/> when the client is past its rate for this type.</returns>
    /// <remarks>
    /// On the transport thread, before the ring, so a client flooding one command type costs the tick nothing at all (01-model § 7). The bucket starts FULL:
    /// a client's first batch after connecting is within its burst by definition, and starting it empty would throttle the handshake's own first intent.
    /// </remarks>
    public bool TryTakeToken(int typeIndex, int perSecond, int burst, long now)
    {
        if (perSecond <= 0)
        {
            return true;
        }

        var depth = Math.Max(burst, perSecond);
        if (!BucketsPrimed)
        {
            for (var i = 0; i < Tokens.Length; i++)
            {
                Tokens[i] = depth;
                BucketStamp[i] = now;
            }

            BucketsPrimed = true;
        }

        var elapsed = (now - BucketStamp[typeIndex]) / (double)Stopwatch.Frequency;
        BucketStamp[typeIndex] = now;
        var tokens = Math.Min(depth, Tokens[typeIndex] + (elapsed * perSecond));
        if (tokens < 1d)
        {
            Tokens[typeIndex] = tokens;
            return false;
        }

        Tokens[typeIndex] = tokens - 1d;
        return true;
    }
}

/// <summary>
/// The engine's inbound half: a transport thread's <c>COMMANDS</c> message becomes framed records in that session's ring, and the Engine-Pre drain turns them
/// into the tick's typed buffers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the two threads meet is the ring and nowhere else.</b> The transport side validates, decodes, rate-limits and frames; the tick side drains,
/// coalesces and indexes. The ring is the whole interface between them (archive/Subscriptions/foundation/05), which is why neither side takes a lock on
/// the hot path and why a late tick can never make a network thread wait.
/// </para>
/// <para>
/// <b>Records carry a small header of their own inside the ring's framing</b> — <c>u16 wireIdx | u16 seq | u32 clientTick</c>, then the decoded command. The
/// ring frames the length; this frames the meaning. A <see cref="AckRecordMarker"/> in place of a wire index is how the transport side hands a rejection it
/// decided by itself (a rate limit) to the tick, over the same SPSC channel rather than over a second one that would need its own synchronization.
/// </para>
/// </remarks>
internal sealed class SubscriptionsIngress : IDisposable
{
    /// <summary>A record whose wire index is this one is not a command but a rejection the transport side decided.</summary>
    internal const ushort AckRecordMarker = 0xFFFF;

    /// <summary>
    /// The transport-side refusals (rate, role, budget, pre-check, region) one session may place in a tick's shared acknowledgement log. The log is sized
    /// to hold this many for every session (<see cref="CommandTypeBuffers.AckCapacity"/>), so no session's refusals can crowd out another's. An honest client
    /// is refused a handful of commands a tick at most; past this share the rest settle through <c>lastSeq</c> and are counted.
    /// </summary>
    internal const int RefusalAcksPerTick = 8;

    /// <summary>
    /// The per-archetype replication states, for diagnostics only. Set by the runtime once both exist, so <see cref="SubscriptionsCommands"/> can report
    /// projection and migration counts without the application reaching into an internal type. Nothing on the tick path reads it.
    /// </summary>
    internal ArchetypeReplicationState[] ReplicationStates;

    /// <summary>The frame assembler, for diagnostics only. Set by the runtime once both exist; nothing on the tick path reads it.</summary>
    internal FrameAssembler Frames;

    /// <summary>The engine's realms, which <see cref="SubscriptionsCommands.Place(SessionId, RealmId, Vector3D)"/> checks a realm against; set at Start.</summary>
    internal RealmTable Realms;

    /// <summary>The compiled profiles, whose anchors decide who may move a session between realms; set at Start.</summary>
    internal SubscriptionProfiles Profiles;

    /// <summary>The engine is configured for more than one realm: a session is then in no realm until placed, entered or anchored (12-realms § 1.2).</summary>
    internal bool MultiRealm;

    /// <summary>Bytes of a ring record before the command itself.</summary>
    internal const int RecordHeaderBytes = 8;

    /// <summary>How many drain passes one session may cost in one tick. A bound, so a producer racing the drain cannot hold a worker indefinitely.</summary>
    private const int MaxDrainPassesPerTick = 64;

    private readonly SessionTable _sessions;
    private readonly SubscriptionsRegistry _registry;
    private readonly IngressRingPool _pool;
    private readonly SendPump _sendPump;

    /// <summary>The runtime's send pump, for its telemetry.</summary>
    internal SendPump SendPump => _sendPump;
    private readonly Lock _rowLock = new();

    private readonly SessionIngress[] _rows;
    private readonly List<IngressRingLease> _abandoned = [];
    private readonly List<int> _closing = [];

    private SessionId[] _tickSessions = [];
    private byte[][] _scratch = [];
    private int _tickSessionCount;
    private long _refusedRings;
    private long _drainFaults;

    /// <summary>Creates the ingress path for a started runtime.</summary>
    /// <param name="sessions">The session table.</param>
    /// <param name="registry">The frozen declarations, for the profiles a region is clamped by.</param>
    /// <param name="commands">The bound command types.</param>
    /// <param name="buffers">This tick's typed buffers.</param>
    /// <param name="pool">Where a session's ring comes from.</param>
    /// <param name="maxSessions">The session table's width.</param>
    /// <param name="sendPump">
    /// The send side, so a close the tick performs reaches the client as a <c>KICK</c>. <see langword="null"/> in fixtures that exercise the drain alone,
    /// where a close has no socket behind it.
    /// </param>
    public SubscriptionsIngress(SessionTable sessions, SubscriptionsRegistry registry, CommandRegistry commands, CommandTypeBuffers buffers,
        IngressRingPool pool, int maxSessions, SendPump sendPump = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(buffers);
        ArgumentNullException.ThrowIfNull(pool);

        _sessions = sessions;
        _registry = registry;
        _pool = pool;
        _sendPump = sendPump;
        _rows = new SessionIngress[maxSessions];
        Commands = commands;
        Buffers = buffers;
        NetIds = new NetIdEntityIndex();

        // One segment per worker, sized at the first BeginTick. It is created here rather than by the runtime because its lifetime is the ingress's: a
        // request is made by an application system during a tick and applied by the next tick's prologue, which is the same span this object is alive for.
        Requests = new SessionRequestLog(1);
    }

    /// <summary>The bound command types.</summary>
    public CommandRegistry Commands { get; }

    /// <summary>
    /// The served realm's frame: a command's realm-framed field is decoded over it at <see cref="RealmFrame.CommandPositionBits"/>. Realm 0's, fixed for the
    /// runtime's life until sessions are placed in realms (R4.3), which is what lets the transport decode it without reading the session's realm (SUB-05).
    /// </summary>
    public RealmFrame Realm { get; set; }

    /// <summary>This tick's typed buffers.</summary>
    public CommandTypeBuffers Buffers { get; }

    /// <summary>Which entity each network identity names, so a command's <c>entityRef</c> can be resolved.</summary>
    public NetIdEntityIndex NetIds { get; }

    /// <summary>
    /// What application systems ask of a session — its profile, its observers, the entity it controls — staged per worker and applied by the next tick.
    /// </summary>
    /// <remarks>
    /// <b>Why it is staged and not applied where it is asked.</b> A system runs on a worker, and a session row belongs to the tick (SUB-05). Writing the row
    /// from a system would put a second writer on it; recording the intent costs a bounded append and lets one thread apply the batch at a point where nothing
    /// else is reading. It is also what makes "bind the profile in the Opened handler" work: the handler runs during the tick, the binding takes effect at the
    /// start of the next one, before interest is gathered.
    /// </remarks>
    public SessionRequestLog Requests { get; }

    /// <summary>The session table, so the public surface can check an identity before answering about it.</summary>
    public SessionTable Sessions => _sessions;

    /// <summary>Sessions whose ring could not be leased. Each one is a client whose commands were dropped before the ring.</summary>
    public long RefusedRingCount => Interlocked.Read(ref _refusedRings);

    /// <summary>Drains that threw. A non-zero value is a defect: the drain runs on an engine track whose failure is terminal, so it never propagates.</summary>
    public long DrainFaults => Interlocked.Read(ref _drainFaults);

    /// <summary>The last exception a drain swallowed, kept so a test or an operator can see what it was.</summary>
    public Exception LastDrainFault { get; private set; }

    /// <summary>Sessions the current tick drains, snapshotted before the chunks run.</summary>
    public int TickSessionCount => _tickSessionCount;

    /// <summary>A session's ingress row, or <see langword="null"/> when it has never sent a command.</summary>
    /// <param name="session">The identity.</param>
    /// <returns>The row.</returns>
    public SessionIngress RowOf(SessionId session)
    {
        if (!session.IsValid || session.Slot >= _rows.Length)
        {
            return null;
        }

        var row = Volatile.Read(ref _rows[session.Slot]);
        return row != null && row.Session == session ? row : null;
    }

    // ── the transport side ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates a whole <c>COMMANDS</c> message and frames each command it carries into the session's ring.
    /// </summary>
    /// <param name="session">Whose commands they are.</param>
    /// <param name="message">The message, type byte included. Borrowed; everything kept is copied.</param>
    /// <exception cref="WireFormatException">The message is malformed. Nothing was framed — the caller turns it into a close.</exception>
    /// <remarks>
    /// <b>Whole-message validation is <see cref="CommandsMessage.Read"/>'s, not a second parser's</b> (03 § 8, § 10): it decodes once into its own checking
    /// sink and only then into this one, so a malformed tail can never deliver the valid commands in front of it. The role check, the token bucket and the
    /// declaration's pre-check run here, per command, because they are policy rather than framing and because running them on the tick would be paying for
    /// traffic that was never going to be applied.
    /// </remarks>
    public void OnCommands(SessionId session, ReadOnlySpan<byte> message)
    {
        var row = RowFor(session);
        if (row == null)
        {
            // No ring to frame into (the pool is exhausted, or the session is going away) — but a malformed message is still malformed: validated, so it
            // still closes with 1007.
            var validating = new RefusingSink(null);
            CommandsMessage.Read(message, Commands.Plan, ref validating, Realm);
            return;
        }

        var role = SessionRole.Player;
        try
        {
            role = (SessionRole)_sessions.Row(session).Role;
        }
        catch (InvalidOperationException)
        {
            // The session went away between the connection's state check and here. Its ring is about to be reclaimed; dropping the batch is the whole answer.
            return;
        }

        // The decode buffers are the CALLER's stack, not the sink's: a ref struct cannot stackalloc in its own constructor (the frame is gone before it is
        // used), and a heap buffer per message is the allocation this whole path exists to avoid.
        Span<byte> payload = stackalloc byte[CommandRegistry.MaxPayloadBytes];
        Span<RegionVertex> vertices = stackalloc RegionVertex[BuiltInCommands.MaxRegionVertices];
        var sink = new IngressCommandSink(this, row, role, payload, vertices);
        CommandsMessage.Read(message, Commands.Plan, ref sink, Realm);
        sink.Flush();
    }

    /// <summary>
    /// A whole <c>COMMANDS</c> message over its session's inbound budget: each command answered with a <c>RATE_LIMITED</c> <c>ACK</c>, none framed. Validated
    /// like any other — a malformed message throws <see cref="WireFormatException"/> here too — so it is decoded, twice (the validating pass, then this one):
    /// the budget bounds what reaches the tick, and the abuse rule is what bounds the decoding.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="message">The whole message.</param>
    /// <returns>The commands the message carried, each one a refusal for the abuse rule.</returns>
    public int RefuseCommands(SessionId session, ReadOnlySpan<byte> message)
    {
        var row = RowFor(session);
        var sink = new RefusingSink(row);
        CommandsMessage.Read(message, Commands.Plan, ref sink, Realm);
        if (row != null)
        {
            row.OverBudgetMessages++;
        }

        return sink.Count;
    }

    /// <summary>Answers every command of a refused message with a <c>RATE_LIMITED</c> <c>ACK</c>, counts them, and reads nothing else.</summary>
    private struct RefusingSink : ICommandSink
    {
        private readonly SessionIngress _row;

        public RefusingSink(SessionIngress row)
        {
            _row = row;
            Count = 0;
        }

        /// <summary>Commands seen.</summary>
        public int Count { get; private set; }

        public void Command(MessagePlan type, ushort seq, uint clientTick)
        {
            Count++;
            if (_row == null)
            {
                return;
            }

            Span<byte> reason = [AckReasons.RateLimited];
            Publish(_row, AckRecordMarker, seq, clientTick, reason);
        }

        public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
        {
        }

        public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
        {
        }

        public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
        {
        }

        public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
        {
        }
    }

    /// <summary>Frames one decoded command into the session's ring.</summary>
    /// <param name="row">The session's row.</param>
    /// <param name="wireIdx">The command's wire index, or <see cref="AckRecordMarker"/> for a rejection.</param>
    /// <param name="seq">The client's sequence number.</param>
    /// <param name="clientTick">The client's tick.</param>
    /// <param name="payload">The decoded command.</param>
    internal static void Publish(SessionIngress row, ushort wireIdx, ushort seq, uint clientTick, scoped ReadOnlySpan<byte> payload)
    {
        Span<byte> record = stackalloc byte[RecordHeaderBytes + CommandRegistry.MaxPayloadBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(record, wireIdx);
        BinaryPrimitives.WriteUInt16LittleEndian(record[2..], seq);
        BinaryPrimitives.WriteUInt32LittleEndian(record[4..], clientTick);
        payload.CopyTo(record[RecordHeaderBytes..]);

        if (!row.Ring.TryWrite(record[..(RecordHeaderBytes + payload.Length)]))
        {
            // Never a throw and never a wait: the producer is a network thread, and the drop counter is the session's own backpressure signal (EQ-03's shape).
            row.DroppedCommands++;
        }
    }

    /// <summary>Takes or creates the ingress row of a session that is sending commands. Callable from a transport thread.</summary>
    private SessionIngress RowFor(SessionId session)
    {
        if (!session.IsValid || session.Slot >= _rows.Length)
        {
            return null;
        }

        var existing = Volatile.Read(ref _rows[session.Slot]);
        if (existing != null && existing.Session == session)
        {
            return existing;
        }

        lock (_rowLock)
        {
            existing = _rows[session.Slot];
            if (existing != null && existing.Session == session)
            {
                return existing;
            }

            if (existing != null)
            {
                // A recycled slot. The previous tenant's ring is NOT returned here: a drain chunk may be reading it this very instant, and the pool's Release
                // resets the cursors. It is handed to the tick, which returns it at the next BeginTick, where no chunk is running.
                _abandoned.Add(existing.Lease);
                Volatile.Write(ref _rows[session.Slot], null);
            }

            if (!_pool.TryAcquire(out var lease))
            {
                Interlocked.Increment(ref _refusedRings);
                return null;
            }

            var row = new SessionIngress(session, lease, Commands.WireIdxCount);

            // Release: the row's fields are written above and must be visible to a drain that sees the reference.
            Volatile.Write(ref _rows[session.Slot], row);
            return row;
        }
    }

    // ── the tick side ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances the session table, resets the tick's buffers and plans the drain. Runs single-threaded, before any chunk.
    /// </summary>
    /// <param name="ctx">The tick's replication context, whose session count this sets.</param>
    /// <returns>How many chunks the drain should dispatch.</returns>
    public int BeginTick(SubscriptionsContext ctx)
    {
        ReturnRetiredRings();

        // Closes a transport thread asked for become real closes, then this tick's lifecycle batch is published — both before any application system runs,
        // which is what lets an app react to an Opened or a Closed with an ordinary transaction in the same tick (archive/Subscriptions/foundation/05 § 4.2).
        _sessions.ApplyPendingCloses();

        // What last tick's systems asked for, applied before the table publishes this tick's open set — so a profile bound in an Opened handler is in force
        // for the first frame that can see the session, rather than one tick later.
        Requests.Apply(_sessions);

        _sessions.BeginTick();

        foreach (ref readonly var e in _sessions.Events)
        {
            if (e.Kind == SessionEventKind.Closed)
            {
                _closing.Add(e.Session.Slot);

                // The client is told here, once, for every reason a session ends — silence, a skip run, an unpublished tick, the application's Kick verb. The
                // table's close marks the row and queues this event and does nothing else; without this line the socket stays open and no code ever arrives,
                // which is the one failure a client cannot recover from because nothing tells it to reconnect. A close the client itself started has already
                // unbound its link, and the pump answers false for it rather than sending a second goodbye.
                _sendPump?.RequestKick(e.Session, e.CloseCode, _sessions.CloseDetail(e.Session));
            }
        }

        ctx.SessionCount = _sessions.OpenCount;

        var workers = Math.Max(1, ctx.WorkerCount);
        EnsureScratch(workers);

        _tickSessionCount = 0;
        foreach (var session in _sessions)
        {
            if (_tickSessionCount == _tickSessions.Length)
            {
                Array.Resize(ref _tickSessions, Math.Max(16, _tickSessions.Length * 2));
            }

            _tickSessions[_tickSessionCount++] = session;
        }

        Buffers.BeginTick(ctx.TickNumber, workers);
        Requests.EnsureWorkers(workers);
        return Math.Min(workers, _tickSessionCount);
    }

    /// <summary>Drains the sessions this chunk owns.</summary>
    /// <param name="chunkIndex">The chunk.</param>
    /// <param name="chunkCount">How many chunks the drain dispatched.</param>
    /// <remarks>
    /// Sessions are partitioned by index, so each is touched by exactly one worker and its records stay in that worker's segment in arrival order — which is
    /// where per-session order comes from (SUB-08). Ordering between sessions is not defined, and nothing needs it.
    /// </remarks>
    public void DrainChunk(int chunkIndex, int chunkCount)
    {
        var count = _tickSessionCount;
        if (chunkCount <= 0 || (uint)chunkIndex >= (uint)_scratch.Length)
        {
            // Unreachable through the exec system, which dispatches exactly what BeginTick planned. Answered rather than asserted because the caller is an
            // engine track whose failure is terminal, and a bounds check is cheaper than the outcome of being wrong about that.
            NoteFault(new ArgumentOutOfRangeException(nameof(chunkIndex), chunkIndex, $"chunk {chunkIndex} of {chunkCount} has no drain scratch"));
            return;
        }

        var start = (int)((long)chunkIndex * count / chunkCount);
        var end = (int)((long)(chunkIndex + 1) * count / chunkCount);

        for (var i = start; i < end; i++)
        {
            var session = _tickSessions[i];
            var row = RowOf(session);
            if (row == null)
            {
                continue;
            }

            try
            {
                DrainSession(row, chunkIndex);
            }
            catch (Exception e)
            {
                // The drain is on Engine-Pre, whose failure IS terminal for the engine. A command that cannot be decoded must cost that client its command,
                // never the database, so the fault is counted and kept rather than propagated.
                NoteFault(e);
            }
        }
    }

    /// <summary>Records a fault the drain swallowed. A non-zero count is a defect; propagating it would be a worse one.</summary>
    /// <param name="fault">What went wrong.</param>
    internal void NoteFault(Exception fault)
    {
        Interlocked.Increment(ref _drainFaults);
        LastDrainFault = fault;
    }

    private void DrainSession(SessionIngress row, int segment)
    {
        var scratch = _scratch[segment];
        for (var pass = 0; pass < MaxDrainPassesPerTick; pass++)
        {
            var written = row.Ring.Drain(scratch, out var records);
            if (records == 0)
            {
                return;
            }

            var framed = new ReadOnlySpan<byte>(scratch, 0, written);
            while (!framed.IsEmpty)
            {
                var record = IngressRing.ReadFramed(framed, out var consumed);
                framed = framed[consumed..];
                Apply(row, segment, record);
            }
        }
    }

    private void Apply(SessionIngress row, int segment, ReadOnlySpan<byte> record)
    {
        if (record.Length < RecordHeaderBytes)
        {
            return;
        }

        var wireIdx = BinaryPrimitives.ReadUInt16LittleEndian(record);
        var seq = BinaryPrimitives.ReadUInt16LittleEndian(record[2..]);
        var clientTick = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
        var body = record[RecordHeaderBytes..];

        if (wireIdx == AckRecordMarker)
        {
            // Each session's share of the shared log (SUB-27): past it, the refusal settles through lastSeq alone and is counted, so a session flooding
            // refusals cannot drop another session's acknowledgements.
            if (row.RefusalAckTick != Buffers.Tick)
            {
                row.RefusalAckTick = Buffers.Tick;
                row.RefusalAcksThisTick = 0;
            }

            if (row.RefusalAcksThisTick < RefusalAcksPerTick)
            {
                row.RefusalAcksThisTick++;
                Buffers.Acks.Add(row.Session, seq, body.Length > 0 ? body[0] : AckReasons.Rejected);
            }
            else
            {
                row.RefusalAcksCapped++;
            }

            NoteSeq(row, seq);
            return;
        }

        var info = Commands.ByWireIdx(wireIdx);
        if (info == null)
        {
            return;
        }

        NoteSeq(row, seq);
        row.DrainedCommands++;

        if (info.IsClientRegion)
        {
            ApplyRegion(row, seq, body);
            return;
        }

        Buffers.ByWireIdx(wireIdx)?.Append(segment, row.Session, seq, clientTick, body);
    }

    private void ApplyRegion(SessionIngress row, ushort seq, ReadOnlySpan<byte> body)
    {
        if (!ClientRegionCommand.TryRead(body, out var region))
        {
            Buffers.Acks.Add(row.Session, seq, AckReasons.RegionInvalid);
            return;
        }

        if (!TakeRegion(row, ref region))
        {
            // The ingress hull accepted it and the clamp scales it uniformly, so this is a hostile or corrupted record, refused like one.
            Buffers.Acks.Add(row.Session, seq, AckReasons.RegionInvalid);
        }
    }

    // Clamped to the session's profile and turned into half-spaces, then the session's region: what the frame stage reads (09 § 7).
    private bool TakeRegion(SessionIngress row, ref ClientRegionCommand region)
    {
        region.ClampToMaxEdge(MaxEdgeOf(row.Session));
        if (!region.BuildPlanes())
        {
            return false;
        }

        row.Region = region;
        row.HasRegion = true;
        return true;
    }

    /// <summary>
    /// Tests only: gives a session a region as a drained <c>ClientRegion</c> command would — the hull of the vertices, clamped by its profile, as
    /// half-spaces — without a client, a ring record or a drain. The row is the tick's (SUB-05): call it between ticks only.
    /// </summary>
    internal bool SetRegionForTest(SessionId session, ReadOnlySpan<RegionVertex> vertices, int dims)
    {
        var row = RowFor(session);
        if (row == null || ConvexHull.Build(vertices, dims, 0f, 0, out var region) != ClientRegionOutcome.Accepted)
        {
            return false;
        }

        return TakeRegion(row, ref region);
    }

    /// <summary>The longest edge the session's profile accepts for a client region, or zero when it declares none.</summary>
    private double MaxEdgeOf(SessionId session)
    {
        var profileName = _sessions.ProfileName(session);
        if (profileName == null)
        {
            return 0;
        }

        foreach (var profile in _registry.Profiles)
        {
            if (!string.Equals(profile.Name, profileName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var observer in profile.Observers)
            {
                if (observer.Kind == ObserverKind.ClientRegion)
                {
                    return observer.MaxEdgeM;
                }
            }

            return 0;
        }

        return 0;
    }

    /// <summary>Advances the session's <c>lastSeq</c>, comparing with RFC 1982 serial arithmetic so the wrap at 65 535 is not a step backwards.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NoteSeq(SessionIngress row, ushort seq)
    {
        if (!row.HasLastSeq || (short)(seq - row.LastSeq) > 0)
        {
            row.LastSeq = seq;
            row.HasLastSeq = true;
        }
    }

    /// <summary>Gives back the rings of sessions that are gone. Runs where no chunk is in flight, which is what makes resetting a ring safe.</summary>
    private void ReturnRetiredRings()
    {
        lock (_rowLock)
        {
            foreach (var lease in _abandoned)
            {
                _pool.Release(lease);
            }

            _abandoned.Clear();

            for (var i = _closing.Count - 1; i >= 0; i--)
            {
                var slot = _closing[i];
                var row = _rows[slot];
                if (row == null)
                {
                    _closing.RemoveAt(i);
                    continue;
                }

                // Only once the table has recycled the slot: that is the point at which the Closed event has been delivered and no send is in flight, so no
                // transport thread can still be framing into this ring.
                if (_sessions.IsLive(row.Session))
                {
                    continue;
                }

                _rows[slot] = null;
                _pool.Release(row.Lease);
                _closing.RemoveAt(i);
            }
        }
    }

    private void EnsureScratch(int workers)
    {
        if (_scratch.Length >= workers)
        {
            return;
        }

        Array.Resize(ref _scratch, workers);
        for (var i = 0; i < workers; i++)
        {
            _scratch[i] ??= new byte[_pool.RingBytes];
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_rowLock)
        {
            _abandoned.Clear();
            _closing.Clear();
            Array.Clear(_rows);
        }
    }
}

/// <summary>
/// The sink <see cref="CommandsMessage.Read"/> delivers a validated message into: it rebuilds each command in a stack buffer, applies the checks that belong
/// to the transport thread, and frames what survives into the session's ring.
/// </summary>
/// <remarks>
/// A <c>ref struct</c> because the payload it builds is a <c>stackalloc</c>: a command never becomes a heap object on this path, which is what keeps a
/// message from costing the GC anything at all.
/// </remarks>
internal ref struct IngressCommandSink : ICommandSink
{
    private readonly SubscriptionsIngress _ingress;
    private readonly SessionIngress _row;
    private readonly SessionRole _role;
    private readonly Span<byte> _payload;
    private readonly Span<RegionVertex> _vertices;

    private CommandTypeInfo _current;
    private ushort _seq;
    private uint _clientTick;
    private int _vertexCount;
    private int _vertexDims;
    private float _altitudeM;
    private ushort _budgetKiBps;
    private bool _open;

    /// <summary>Creates a sink for one message.</summary>
    /// <param name="ingress">The ingress path.</param>
    /// <param name="row">The session's row.</param>
    /// <param name="role">The session's role, which decides what it may send.</param>
    /// <param name="payload">The caller's scratch for the command being rebuilt, <see cref="CommandRegistry.MaxPayloadBytes"/> wide.</param>
    /// <param name="vertices">The caller's scratch for a region's vertices.</param>
    public IngressCommandSink(SubscriptionsIngress ingress, SessionIngress row, SessionRole role, Span<byte> payload, Span<RegionVertex> vertices)
    {
        _ingress = ingress;
        _row = row;
        _role = role;
        _payload = payload;
        _vertices = vertices;
    }

    /// <inheritdoc />
    public void Command(MessagePlan type, ushort seq, uint clientTick)
    {
        Flush();

        _current = _ingress.Commands.ByWireIdx(type.Idx);
        _seq = seq;
        _clientTick = clientTick;
        _vertexCount = 0;
        _vertexDims = 2;
        _altitudeM = 0;
        _budgetKiBps = 0;
        _open = _current != null;

        if (_current == null)
        {
            // A type the catalog declares and the registry did not bind: never expected, and still answered — every refused command is (SUB-27).
            Refuse(AckReasons.Rejected);
            return;
        }

        _payload.Clear();

        if (!_current.AllowsRole(_role))
        {
            // Refused, not fatal: a spectator sending a player's command is a client that has not read the catalog's roles, which is not a protocol violation.
            _row.PolicyRefusals++;
            _open = false;
            Refuse(AckReasons.Forbidden);
            return;
        }

        if (!_row.TryTakeToken(_current.WireIdx, _current.RatePerSecond, _current.RateBurst, Stopwatch.GetTimestamp()))
        {
            _row.PolicyRefusals++;
            _open = false;
            Refuse(AckReasons.RateLimited);
        }
    }

    /// <summary>Answers the command being decoded with a refusal <c>ACK</c> and counts it.</summary>
    /// <param name="reasonCode">An <see cref="AckReasons"/> code.</param>
    private void Refuse(byte reasonCode)
    {
        _row.RefusedCommands++;
        Span<byte> reason = [reasonCode];
        SubscriptionsIngress.Publish(_row, SubscriptionsIngress.AckRecordMarker, _seq, _clientTick, reason);
    }

    /// <inheritdoc />
    public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
    {
        if (!_open)
        {
            return;
        }

        if (_current.IsClientRegion)
        {
            if (field.Name == BuiltInCommands.RegionAltitudeField)
            {
                _altitudeM = (float)components[0];
            }
            else if (field.Name == BuiltInCommands.RegionBudgetField)
            {
                _budgetKiBps = (ushort)Math.Clamp(components[0], 0, ushort.MaxValue);
            }

            return;
        }

        var bindings = _current.Bindings;
        if ((uint)field.Ordinal < (uint)bindings.Length)
        {
            bindings[field.Ordinal].Store(_payload, components);
        }
    }

    /// <inheritdoc />
    public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
    {
        // A command struct holds no text, and the registry refuses one at Start, so reaching here would mean the catalog and the binding disagree.
    }

    /// <inheritdoc />
    public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
    {
    }

    /// <inheritdoc />
    public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
    {
        if (!_open || !_current.IsClientRegion || field.Name != BuiltInCommands.RegionVerticesField)
        {
            return;
        }

        // Always pos3 on the wire (typhon.3, D-8); the realm decides the hull: a flat realm's region is (x, y) on the plane z = 0 (10 § 6).
        var stride = field.Components;
        _vertexDims = _ingress.Realm is { Deep: true } ? 3 : 2;
        _vertexCount = Math.Min(count, BuiltInCommands.MaxRegionVertices);
        for (var i = 0; i < _vertexCount; i++)
        {
            var at = i * stride;
            _vertices[i] = new RegionVertex { X = components[at], Y = components[at + 1], Z = _vertexDims == 3 ? components[at + 2] : 0d };
        }
    }

    /// <summary>Frames the command being decoded, if any. Called before each new command and once at the end of the message.</summary>
    public void Flush()
    {
        if (!_open)
        {
            _open = false;
            return;
        }

        _open = false;

        if (_current.IsClientRegion)
        {
            FlushRegion();
            return;
        }

        if (_current.PrecheckAdapter != null && !_current.PrecheckAdapter(_current.Precheck, _payload[.._current.PayloadSize]))
        {
            // The application's own verdict, answered as the application's rejection would be.
            Refuse(AckReasons.Rejected);
            return;
        }

        SubscriptionsIngress.Publish(_row, (ushort)_current.WireIdx, _seq, _clientTick, _payload[.._current.PayloadSize]);
    }

    private void FlushRegion()
    {
        // The hull is taken here, on the transport thread: it needs no engine state, and a footprint that cannot become a polygon must never reach the tick.
        // The profile's edge clamp is the tick's, because the profile is session state the transport side does not own (SUB-05).
        if (ConvexHull.Build(_vertices[.._vertexCount], _vertexDims, _altitudeM, _budgetKiBps, out var region) != ClientRegionOutcome.Accepted)
        {
            Refuse(AckReasons.RegionInvalid);
            return;
        }

        Span<byte> scratch = stackalloc byte[ClientRegionCommand.MaxRecordBytes];
        SubscriptionsIngress.Publish(_row, (ushort)_current.WireIdx, _seq, _clientTick, region.Write(scratch));
    }
}

/// <summary>
/// The Engine-Pre drain: every session's ring into the tick's typed command buffers, chunked over sessions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Engine-Pre, because that track is this drain's home and carries nothing else</b> (<c>RuntimeSchedule.cs:38</c>). It runs before the application's own
/// track, which is what "a command arrives inside one tick" means: a message that reached a ring before this tick's Engine-Pre is applied by a system in the
/// same tick, and one that arrives afterwards waits exactly one tick rather than racing the systems that would read it.
/// </para>
/// <para>
/// <b>It is not a <see cref="SubscriptionsExecSystemBase"/>.</b> That base gates on <see cref="SubscriptionsContext.ShouldTrackRun"/>, which requires a
/// non-zero session count — and this system is what establishes the session count, by advancing the session table. Sharing the gate would make the drain
/// depend on its own output, and the first session would never be seen.
/// </para>
/// <para>
/// <b>Nothing here may throw.</b> Engine-Pre carries the engine tag, and a throw on an engine track latches the terminal fence verdict; the Subscriptions
/// track is exempt from that by <c>Track.FailureIsTerminal</c>, and this one is not. A replication defect must cost a client its command, never the database,
/// so every fault is counted on <see cref="SubscriptionsIngress.DrainFaults"/> and the tick continues.
/// </para>
/// </remarks>
internal sealed class SubscriptionsIngressExecSystem : ChunkedCallbackSystem<SubscriptionsContext>
{
    /// <inheritdoc />
    protected override void Configure(SystemBuilder<SubscriptionsContext> b) => b
        .Name("SubscriptionsIngress")
        .ChunkedParallel(1);

    /// <inheritdoc />
    protected override bool ShouldRun(SubscriptionsContext ctx) => ctx.Subscriptions is { IsActive: true };

    /// <inheritdoc />
    protected override int Prepare(SubscriptionsContext ctx)
    {
        var ingress = ctx.Subscriptions?.Ingress;
        if (ingress == null)
        {
            return 0;
        }

        try
        {
            // The shadow oracle's check runs HERE — after last tick's frames were published and before this tick's fence moves any
            // replication entry. Anywhere later compares the clients against entries a migration has already carried or parked.
            var push = ctx.Subscriptions.Push;
            if (push != null && push.Shadow)
            {
                // Blocks are replication's own native memory and the active list is an array: no page is read, so no epoch is needed.
                push.RunQueuedShadowChecks();
            }

            return ingress.BeginTick(ctx);
        }
        catch (Exception e)
        {
            // Same reasoning as the chunk body: Prepare runs on the scheduler's thread for an engine-tagged track, so a throw here would be terminal. It is
            // counted on the same fault counter rather than swallowed, because a prologue that cannot run is the loudest kind of replication defect there is.
            ingress.NoteFault(e);
            return 0;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// No <c>EpochGuard</c>, unlike the Engine-Subscriptions stages: this drain touches native ring buffers and replication's own managed segments, and reads
    /// no cluster page at all, so PS-02 has nothing to say about it. The outer catch is the same terminal-failure argument as everywhere else on this track.
    /// </remarks>
    protected override void Execute(TickContext tick)
    {
        var ingress = Context?.Subscriptions?.Ingress;
        if (ingress == null)
        {
            return;
        }

        try
        {
            ingress.DrainChunk(tick.ChunkIndex, tick.ChunkCount);
        }
        catch (Exception e)
        {
            ingress.NoteFault(e);
        }
    }
}
