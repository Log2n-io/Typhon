using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>Which of an <c>ENTITIES</c> block's four sub-lists a record belongs to (03 § 5).</summary>
internal enum FrameListKind
{
    /// <summary>A full enter: the position, the <c>onEnter</c> body and every group, with no mask.</summary>
    Enter = 0,

    /// <summary>A motion segment.</summary>
    Segment = 1,

    /// <summary>A state record: a mask and the bodies of the groups it names.</summary>
    State = 2,

    /// <summary>A leave. Last in its block, and applied last in the frame.</summary>
    Leave = 3,
}

/// <summary>
/// One chunk's tally of what its sessions cost, accumulated on the stack and flushed to the assembler once.
/// </summary>
/// <remarks>
/// <b>An atomic per session per tick over consecutive <c>long</c> fields is two cache lines ping-ponging between every worker for the whole stage</b>, and
/// none of the numbers is read until the tick has joined. Accumulating per CHUNK turns that into one flush per worker.
/// </remarks>
internal struct FrameCounters
{
    /// <summary>Frames published.</summary>
    public long FramesProduced;

    /// <summary>Bytes encoded.</summary>
    public long BytesEncoded;

    /// <summary>Records encoded.</summary>
    public long RecordsEncoded;

    /// <summary>ENTER records the published frames carried.</summary>
    public long EntersEmitted;

    /// <summary>LEAVE records the published frames carried.</summary>
    public long LeavesEmitted;

    /// <summary>Frames refused for exceeding the ceiling.</summary>
    public long OversizeSkips;

    /// <summary><c>SELF</c> blocks published (11 § 2).</summary>
    public long SelfBlocks;

    /// <summary>Acknowledgement records published.</summary>
    public long AcksWritten;

    /// <summary>Acknowledgements a published frame had no room for (past <see cref="FrameAssembler.MaxAcksPerFrame"/>).</summary>
    public long AcksOverflowed;

    /// <summary>Published frames whose acknowledgement window reached past the history: rejections of the ticks it no longer held may be lost.</summary>
    public long AckWindowsLost;

    // ── Phase timings, in Stopwatch ticks ───────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Collected only while `FrameAssembler.PhaseTimingEnabled` is set, so the ordinary path pays one static bool read per phase boundary. A sampling profiler
    // attributes almost all of the stage to one method, because every callee that matters is inlined in Release; these say WHICH PART of it is the cost.

    /// <summary>Gathering the session's records from the push index.</summary>
    public long GatherTicks;

    /// <summary>Sorting records by netId and counting them.</summary>
    public long SortTicks;

    /// <summary>Encoding the frame.</summary>
    public long EncodeTicks;

    /// <summary>Publishing to the session's hand-off.</summary>
    public long PublishTicks;
}

/// <summary>
/// One worker's scratch for a tick of the frame stage: the four sub-lists per archetype, the sort's ping-pong partner, and the buffer a frame is encoded
/// into.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per worker, so nothing here is shared and nothing here is synchronized</b>. A session belongs to exactly one chunk for the whole of its assembly, and
/// the lists are rewound between sessions rather than between ticks.
/// </para>
/// <para>
/// <b>Native, and grown by doubling, so the steady state allocates nothing managed</b> (SUB-07). The one managed array is the radix histogram, which is
/// reached through a <see cref="Span{T}"/> and never through a pointer.
/// </para>
/// </remarks>
internal sealed unsafe class FrameWorkerScratch : IDisposable
{
    /// <summary>Distance LOD: far updates withheld by this worker's current gather, added to the shared counter once per gather.</summary>
    internal long Deferred;

    /// <summary>Events (09 § 11): the (tick, event) pairs the current session's frame carries.</summary>
    internal readonly EventPicks EventPicks = new();

    /// <summary>Aggregates (09 § 8): the rows the current session's <c>AGG</c> block carries, and their tiles, sorted.</summary>
    internal int[] AggRows = new int[64];

    /// <inheritdoc cref="AggRows"/>
    internal uint[] AggTiles = new uint[64];

    /// <summary>A ClientRegion session's aggregate region this frame, sorted: the tiles its next AGG measures "newly covered" against.</summary>
    internal uint[] AggRegion = new uint[64];

    /// <inheritdoc cref="AggRegion"/>
    internal int AggRegionCount;

    /// <summary>DEBUG (09 § 15): the current session's <c>PUSH_GEOMETRY</c> payload, built to be compared with the last one sent.</summary>
    internal readonly byte[] DebugGeometry = new byte[PushGeometry.MaxBytes];

    /// <summary>One session's acknowledgements for its next frame (11 § 2.3); past it they are counted as lost.</summary>
    internal readonly CommandAck[] Acks = new CommandAck[FrameAssembler.MaxAcksPerFrame];

    /// <summary>Cell deliveries and sweeps this worker's current gather skipped as empty, added to the shared counter once per gather.</summary>
    internal long EmptyCellsSkipped;

    private struct RecordList
    {
        public FrameRecord* Items;
        public int Count;
        public int Capacity;
    }

    private readonly int[] _histogram = new int[RecordSorter.HistogramSlots];
    private SessionId[] _ready = new SessionId[32];
    private int _readyCount;
    private RecordList[] _lists = [];
    private FrameRecord* _sortScratch;
    private int _sortCapacity;
    private byte* _bytes;
    private int _byteCapacity;
    private bool _disposed;

    /// <summary>Native bytes this scratch holds, for the owner's resource accounting.</summary>
    public long EstimatedBytes
    {
        get
        {
            var bytes = _byteCapacity + ((long)_sortCapacity * sizeof(FrameRecord));
            for (var i = 0; i < _lists.Length; i++)
            {
                bytes += (long)_lists[i].Capacity * sizeof(FrameRecord);
            }

            return bytes;
        }
    }

    /// <summary>Rewinds every list for one session's assembly, growing the list table to <paramref name="archetypes"/> archetypes if it has to.</summary>
    /// <param name="archetypes">How many replicated archetypes the runtime holds.</param>
    public void BeginSession(int archetypes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_lists.Length < archetypes * 4)
        {
            Array.Resize(ref _lists, archetypes * 4);
        }

        for (var i = 0; i < _lists.Length; i++)
        {
            _lists[i].Count = 0;
        }
    }

    /// <summary>How many records one archetype's sub-list holds.</summary>
    /// <param name="archetype">The archetype's plan index.</param>
    /// <param name="kind">The sub-list.</param>
    /// <returns>The count.</returns>
    public int Count(int archetype, FrameListKind kind) => _lists[(archetype * 4) + (int)kind].Count;

    /// <summary>One archetype's sub-list, as the encoder reads it.</summary>
    /// <param name="archetype">The archetype's plan index.</param>
    /// <param name="kind">The sub-list.</param>
    /// <returns>The records.</returns>
    public ReadOnlySpan<FrameRecord> List(int archetype, FrameListKind kind)
    {
        ref var list = ref _lists[(archetype * 4) + (int)kind];
        return new ReadOnlySpan<FrameRecord>(list.Items, list.Count);
    }

    /// <summary>Appends a record to one archetype's sub-list.</summary>
    /// <param name="archetype">The archetype's plan index.</param>
    /// <param name="kind">The sub-list.</param>
    /// <param name="record">The record.</param>
    public void Add(int archetype, FrameListKind kind, in FrameRecord record) => Append(ref _lists[(archetype * 4) + (int)kind], in record);

    /// <summary>The sessions this worker published a frame for this tick, in the order it published them.</summary>
    /// <remarks>
    /// <para>
    /// <b>Worker-local, and that is the point.</b> A published frame has to reach a send pump, and the obvious way — one shared queue the workers push onto —
    /// would put an atomic on the publication of every frame of every session. A list per worker costs nothing during the tick, and the tick driver walks all
    /// of them afterwards on one thread, which is where the hand-off to the pumps belongs anyway: it is gated on the flush, and the flush is the driver's.
    /// </para>
    /// <para>
    /// <b>The whole identity, not the slot.</b> A row can close, drain and be re-leased to a new connection between the publication and the driver's walk —
    /// leasing is transport-callable (SUB-05) — so a bare slot names whoever happens to occupy it by then. Carrying the generation makes every use of this
    /// list generation-exact: the pump wakes the session that produced the frame, and a discard closes that session and not its successor.
    /// </para>
    /// </remarks>
    public ReadOnlySpan<SessionId> Ready => new(_ready, 0, _readyCount);

    /// <summary>Notes that a frame was published for a session.</summary>
    /// <param name="session">The session, generation included.</param>
    public void AddReady(SessionId session)
    {
        if (_readyCount == _ready.Length)
        {
            Array.Resize(ref _ready, _ready.Length * 2);
        }

        _ready[_readyCount++] = session;
    }

    /// <summary>Empties the ready list, once the driver has handed it to the pumps.</summary>
    public void ClearReady() => _readyCount = 0;

    /// <summary>Sorts one archetype's sub-list ascending by netId.</summary>
    /// <param name="archetype">The archetype's plan index.</param>
    /// <param name="kind">The sub-list.</param>
    public void SortByNetId(int archetype, FrameListKind kind)
    {
        ref var list = ref _lists[(archetype * 4) + (int)kind];
        if (list.Count < 2)
        {
            return;
        }

        EnsureSortScratch(list.Count);
        RecordSorter.SortByNetId(new Span<FrameRecord>(list.Items, list.Count), new Span<FrameRecord>(_sortScratch, list.Count), _histogram);
    }

    /// <summary>The frame buffer, at least <paramref name="byteCount"/> long.</summary>
    /// <param name="byteCount">The upper bound the frame can occupy.</param>
    /// <returns>The buffer.</returns>
    public Span<byte> Bytes(int byteCount)
    {
        if (byteCount > _byteCapacity)
        {
            var capacity = _byteCapacity == 0 ? 8192 : _byteCapacity;
            while (capacity < byteCount)
            {
                capacity *= 2;
            }

            // native-alloc: doubling growth buffer: Realloc grows in place, where a resource-tree block would be disposed and re-parented on every doubling
            _bytes = (byte*)NativeMemory.Realloc(_bytes, (nuint)capacity);
            _byteCapacity = capacity;
        }

        return new Span<byte>(_bytes, byteCount);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = 0; i < _lists.Length; i++)
        {
            NativeMemory.Free(_lists[i].Items);
        }

        _lists = [];
        NativeMemory.Free(_sortScratch);
        NativeMemory.Free(_bytes);
        _sortScratch = null;
        _bytes = null;
        _sortCapacity = 0;
        _byteCapacity = 0;
    }

    private static void Append(ref RecordList list, in FrameRecord record)
    {
        if (list.Count == list.Capacity)
        {
            var capacity = list.Capacity == 0 ? 64 : list.Capacity * 2;
            // native-alloc: doubling growth buffer: Realloc grows in place, where a resource-tree block would be disposed and re-parented on every doubling
            list.Items = (FrameRecord*)NativeMemory.Realloc(list.Items, (nuint)capacity * (nuint)sizeof(FrameRecord));
            list.Capacity = capacity;
        }

        list.Items[list.Count++] = record;
    }

    private void EnsureSortScratch(int count)
    {
        if (count <= _sortCapacity)
        {
            return;
        }

        var capacity = _sortCapacity == 0 ? 64 : _sortCapacity;
        while (capacity < count)
        {
            capacity *= 2;
        }

        // native-alloc: doubling growth buffer: Realloc grows in place, where a resource-tree block would be disposed and re-parented on every doubling
        _sortScratch = (FrameRecord*)NativeMemory.Realloc(_sortScratch, (nuint)capacity * (nuint)sizeof(FrameRecord));
        _sortCapacity = capacity;
    }
}

/// <summary>
/// Everything one session's frames need between ticks: its profile, whether its view has finished filling, and its rate class.
/// </summary>
/// <remarks>
/// A managed object per session <i>slot</i>, kept for the life of the runtime and reset when the slot is handed to a new session, so a connect storm does not
/// allocate per connection. What a session holds is geometry, owned by <see cref="PushReplication"/>; nothing here is per entity.
/// </remarks>
internal sealed class SessionFrameState
{
    /// <summary>The generation of the session currently occupying the slot; a change is a new session and resets everything below.</summary>
    public ushort Generation { get; private set; }

    /// <summary>The profile the session was bound to when its last frame was built. A change is a <c>RESET</c>.</summary>
    public string Profile { get; set; }

    /// <summary>Whether the next frame must carry <c>RESET</c> and refill the view from nothing.</summary>
    public bool PendingReset { get; set; }

    /// <summary>
    /// The realm of the session's last published <c>RESET</c> — what its client holds (<c>typhon.3</c>, SUB-29): -1 before its first, <see cref="RealmId.NoneValue"/>
    /// for none. It moves only with a published frame, so a skipped switch is retried as a <c>RESET</c> rather than lost.
    /// </summary>
    public int CommittedRealm { get; set; } = -1;

    /// <summary>Whether a frame carrying the session's <c>REALM</c> has been published.</summary>
    public bool RealmSent => CommittedRealm != -1;

    /// <summary>The session's realm this tick, as the frame prologue resolved it; its next <c>RESET</c> carries it.</summary>
    public int PendingRealm { get; set; } = -1;

    /// <summary>The <c>REALM</c> block's frame for <see cref="PendingRealm"/>; <see langword="null"/> for none.</summary>
    public RealmFrame PendingFrame { get; set; }

    /// <summary>The realm the client held before its last switch, -1 for none yet; with <see cref="RealmSwitchTick"/>, what a command built before it names.</summary>
    public int PreviousRealm { get; set; } = -1;

    /// <summary>The tick whose published frame switched the client's realm.</summary>
    public uint RealmSwitchTick { get; set; }

    /// <summary>
    /// The tick of the session's last committed frame: its events are those after it (09 § 11). The session's, not a realm's, so a switch loses none of the
    /// events addressed to it (12-realms § 3).
    /// </summary>
    public uint EventsCursor { get; set; }

    /// <summary>Whether the initial fill under the enter budget has completed — the <c>VIEW_COMPLETE</c> flag.</summary>
    public bool ViewComplete { get; set; }

    /// <summary>Frames produced for the session currently in the slot.</summary>
    public long FramesProduced { get; set; }

    /// <summary>
    /// Rate classes this session has been dropped because it could not keep up: 0 serves every tick, 1 every other, 2 one in four.
    /// </summary>
    /// <remarks>
    /// Tick-owned, like every other field here — the pump never writes it. It is read by <see cref="SkipPolicy.ProducesOnTick"/> at the top of the session's
    /// assembly, which is why a drop costs the encode of nothing at all rather than the encode of a frame that is then thrown away.
    /// </remarks>
    public int DegradeLevel { get; set; }

    /// <summary>Frames produced since the degrade level last changed, which is what earns a class back.</summary>
    public long FramesSinceDegrade { get; set; }

    /// <summary>
    /// Frames this session was skipped, cumulative — <c>typhon.session.skippedFrames</c>. Not <see cref="SessionSendState.SkipRun"/>, which is the CURRENT
    /// run and is reset by every published frame; a counter has to survive the recovery it reports.
    /// </summary>
    public long FramesSkipped { get; set; }

    /// <summary>
    /// Bytes of frame published for this session, cumulative — what <c>typhon.session.outBytesPerSec</c> is differenced from.
    /// </summary>
    /// <remarks>
    /// Bytes PUBLISHED rather than bytes sent: the producer owns this field, so counting here needs no synchronisation, while the send side's count would
    /// have to be written by the pump and read by the tick. The two differ only by the frames a session was handed and never drained, which is at most
    /// <see cref="SessionSendState.K"/> of them.
    /// </remarks>
    public long BytesPublished { get; set; }

    /// <summary>The tick of the last <c>STATS</c> block written for this session, which sets the window its per-second value is divided by.</summary>
    public long StatsTick { get; set; }

    /// <summary><see cref="BytesPublished"/> as of that block, so the next one reports the window rather than the session's whole life.</summary>
    public long StatsBytesMark { get; set; }

    /// <summary>
    /// <see cref="BytesPublished"/> as of the last operator-telemetry emission (#WB-02) — a separate mark from <see cref="StatsBytesMark"/>, deliberately.
    /// </summary>
    /// <remarks>
    /// <see cref="StatsBytesMark"/> is advanced only when a client-facing <c>STATS</c> block is written, which needs the application to have declared metrics
    /// and the session to hold the Stats capability. The operator emission runs regardless of both — that independence is its whole point — so sharing the mark
    /// meant that on a server with no metric catalog it never moved, and every emission divided the session's lifetime byte total by one window.
    /// </remarks>
    public long OperatorStatsBytesMark { get; set; }

    /// <summary>Rebinds the slot to a new session: every per-session number starts again.</summary>
    /// <param name="generation">The new session's generation.</param>
    /// <param name="tick">
    /// The tick the slot is being bound at, which seeds the statistics window. Not zero: <c>typhon.session.outBytesPerSec</c> divides a window's bytes by
    /// <c>tick − StatsTick</c>, so a zero here would divide the first block's bytes by the absolute tick number.
    /// </param>
    public void RebindTo(ushort generation, long tick)
    {
        Generation = generation;
        Profile = null;
        PendingReset = false;
        CommittedRealm = -1;
        PendingRealm = -1;
        PendingFrame = null;
        PreviousRealm = -1;
        RealmSwitchTick = 0;
        EventsCursor = 0;
        ViewComplete = false;
        FramesProduced = 0;
        DegradeLevel = 0;
        FramesSinceDegrade = 0;
        FramesSkipped = 0;
        BytesPublished = 0;
        StatsTick = tick;
        StatsBytesMark = 0;
        // Reset with the rest: a reused slot must not report the previous session's bytes as this one's first window.
        OperatorStatsBytesMark = 0;
        SelfEntity = EntityId.Null;
        CommittedProfile = -1;
        SelfNetId = 0;
        SelfArchetype = -1;
        SentSeq = 0;
        SentSeqValid = false;
        AckTick = 0;
    }

    /// <summary>
    /// The controlled entity as of the last published frame, located or not. A session whose controlled entity differs is sent every owner group (SUB-11).
    /// </summary>
    public EntityId SelfEntity { get; set; }

    /// <summary>The netId the last published <c>SELF</c> named; 0 when it named none (W17′) — then a release says nothing more.</summary>
    public uint SelfNetId { get; set; }

    /// <summary>The plan index of <see cref="SelfNetId"/>'s archetype, so an acknowledgement-only <c>SELF</c> names it without locating it again.</summary>
    public int SelfArchetype { get; set; } = -1;

    /// <summary>The <c>lastSeq</c> the last published <c>SELF</c> carried; meaningful when <see cref="SentSeqValid"/>.</summary>
    public ushort SentSeq { get; set; }

    /// <summary>Whether a published <c>SELF</c> carried a <c>lastSeq</c> for this session yet.</summary>
    public bool SentSeqValid { get; set; }

    /// <summary>The tick of the last published frame, up to which the session's acknowledgements were sent (11 § 2.3); 0 before the first.</summary>
    public uint AckTick { get; set; }

    /// <summary>
    /// The profile index the last published frame was built against, or -1 for none: what a command's entity reference is judged by (SUB-26). The
    /// current profile can differ for as long as the switch's RESET frame is not published — ticks, for a lagging session.
    /// </summary>
    public int CommittedProfile { get; set; } = -1;
}

/// <summary>
/// S2b — turns each session's share of the tick's push events into a <c>TICK</c> message, and hands it to the send side through the sequence protocol
/// (SUB-04).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it holds per session:</b> a <see cref="SessionSendState"/> (the hand-off's counters and two frame slots) and a <see cref="SessionFrameState"/>
/// (the pending reset, the view-complete latch, the rate class and the profile the last frame was built against). What the client holds is geometry,
/// committed by <see cref="PushReplication.Commit"/> with the frame that describes it — never before it is published, so a frame that cannot be produced
/// leaves the session exactly as it was and its next frame carries the union from the push log (SUB-03).
/// </para>
/// <para>
/// <b>Thread safety.</b> <see cref="BeginTick"/> is single-threaded and runs before the dispatch — it is where a session's state is created, rebound and
/// profile-checked. <see cref="ExecuteChunk"/> runs on pool workers, which take sessions from a shared cursor, each owning its scratch and its sessions'
/// state outright.
/// </para>
/// </remarks>
internal sealed unsafe partial class FrameAssembler : IDisposable
{
    private readonly SubscriptionsOptions _options;
    private readonly CompiledProjectionPlan[] _plans;
    private readonly ArchetypeEncodePlan[] _encodePlans;
    private readonly SessionTable _sessions;
    private readonly SessionFrameState[] _states;

    /// <summary>
    /// Cap on <c>SubscriptionsSessionTelemetry</c> (trace kind 69) rows per emission (#WB-02).
    /// </summary>
    /// <remarks>
    /// 64 is chosen against what a panel can show rather than what the engine can hold: a table of a few dozen sessions is readable, and the server record
    /// reports the population so an operator sees that the list is a sample. The alternative — one row per session at the 8192-session limit — would put
    /// half a megabyte of trace on the wire every second for rows nobody scrolls to.
    /// </remarks>
    private const int OperatorSessionRowCap = 64;

    /// <summary>Tick of the last operator-telemetry emission, -1 before the first. Its own window, independent of the encoder's — see <c>EmitOperatorTelemetry</c>.</summary>
    private long _operatorEmissionTick = -1;

    /// <summary>The send pump's byte count at that emission, so the next one reports its window rather than the process's whole life.</summary>
    private long _operatorSentBytesMark;
    private readonly int _maxFrameBytes;
    private readonly int _lagBoundTicks;

    /// <summary>The declared events' hub (09 § 11), or <see langword="null"/>; set by the runtime before the first tick.</summary>
    internal EventHub Events;

    // The tick period in seconds, the budget loop's clock: the nominal one until the runtime publishes the live one, which the overload multiplier stretches.
    private double _tickSeconds;

    // The overload tick multiplier the tick started with (09 § 10): 1 unless the overload detector stretched the tick.
    private int _tickMultiplier = 1;

    /// <summary>The tick multiplier the frame stage currently runs under (1 when not overloaded); what a test harness republishes each tick.</summary>
    internal int TickMultiplier => Math.Max(1, Volatile.Read(ref _tickMultiplier));

    /// <summary>
    /// The live tick state, from the runtime at each tick's start: the period — a stretched tick sends the same bytes over a longer time — and the overload
    /// multiplier that stretched it.
    /// </summary>
    /// <param name="periodUs">The period, in microseconds.</param>
    /// <param name="multiplier">The overload tick multiplier, 1 when not overloaded.</param>
    internal void SetTickState(uint periodUs, int multiplier)
    {
        Volatile.Write(ref _tickSeconds, periodUs / 1_000_000d);
        Volatile.Write(ref _tickMultiplier, Math.Max(1, multiplier));
    }
    private readonly int _silenceBoundTicks;
    private readonly int _closeBoundTicks;
    private readonly int _degradeBoundTicks;

    /// <summary>
    /// The per-session send states, as a pinned managed array indexed from <see cref="_sendStatesOffset"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Managed, and deliberately so (#1006).</b> This was a native <c>AllocatePinned</c> block addressed through a <c>SessionSendState*</c>, and the only
    /// thing that bought was a 64-byte base alignment — which a pinned array gives too, once the slack is taken in bytes. What it charged for that was C-style
    /// lifetime management on memory a send pump can still be holding after the runtime is torn down: the block was freed while a pump was inside a send, and
    /// the pump's completion then wrote through a stale address. A reference keeps this alive as long as the assembler, so there is nothing to free and a late
    /// completion writes into live memory that nobody reads again — harmless by construction rather than by a guard.
    /// </para>
    /// <para>
    /// <b>Why it is not an array of <see cref="SessionSendState"/>.</b> The element stride is 192 bytes, a multiple of 64, so every element shares the base's
    /// alignment and skipping whole elements can never fix a misaligned base. The slack has to be in bytes, so the array is bytes and the states are a cast
    /// span over it.
    /// </para>
    /// <para>
    /// <b>What it gives up.</b> The resource-tree accounting, telemetry id and leak diagnostics that <c>AllocatePinned</c> attaches. That is a real loss, and
    /// a fair price for a structure touched once per session per tick rather than once per entity.
    /// </para>
    /// </remarks>
    private readonly byte[] _sendStates;

    /// <summary>Bytes to skip in <see cref="_sendStates"/> so slot 0 starts on a cache-line boundary.</summary>
    private readonly int _sendStatesOffset;
    private FrameWorkerScratch[] _workers = [];
    private long _tick;
    private StatsEncoder _stats;

    // ── Per-chunk busy time, for the stage's parallel efficiency (17 § 17) ──────────────────────────────────────────────────────────────────────────
    //
    // A stage's wall time is its SLOWEST chunk, so CPU-summed-over-workers divided by wall is the effective worker count, and dividing that by the chunk
    // count is the efficiency. Neither number exists without measuring both halves: the phase timings give the sum, and only a per-chunk stamp gives the
    // max. Written by one chunk each, aggregated by the next tick's prologue, which is the stage's one single-threaded point.
    private long[] _chunkBusy = [];
    private long[] _chunkStart = [];
    private long[] _chunkEnd = [];
    private long _spanTicks;
    private long _startSpreadTicks;
    private long _chunksEntered;
    private int _lastChunkCount;
    private long _busySum;
    private long _busyMax;
    private long _busyTicks;
    private long _busyChunks;

    /// <summary>Timestamp ticks the single-threaded prologue spent, and the sweep half of it.</summary>
    private long _prologueTicks;
    private long _sweepTicks2;
    private long _prepareTicks;


    private long _framesProduced;
    private long _framesSkipped;
    private long _bytesEncoded;
    private long _recordsEncoded;
    private long _entersEmitted;
    private long _leavesEmitted;
    private long _oversizeSkips;
    private long _sessionsDegraded;
    private long _sessionsClosedLagging;
    private long _sessionsClosedSilent;
    private int _longestSkipRun;
    private bool _disposed;

    /// <summary>
    /// Builds the assembler and everything a frame is made of: the pool, the per-slot hand-off counters and the per-archetype encoding constants.
    /// </summary>
    /// <param name="id">Resource id prefix for the pool and the per-session tables.</param>
    /// <param name="parent">Resource-graph parent.</param>
    /// <param name="allocator">Engine allocator.</param>
    /// <param name="options">The operator's replication options.</param>
    /// <param name="plans">One compiled plan per replicated archetype, in declaration order.</param>
    /// <param name="catalog">The emitted catalog, which is where an archetype's canonical WIRE index comes from.</param>
    /// <param name="sessions">The session table, read for each session's bound profile.</param>
    /// <param name="tickPeriodUs">The nominal tick period, which the lag and silence bounds are expressed in ticks of.</param>
    public FrameAssembler(string id, IResource parent, IMemoryAllocator allocator, SubscriptionsOptions options, CompiledProjectionPlan[] plans,
        Catalog catalog, SessionTable sessions, uint tickPeriodUs)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(sessions);

        _options = options;
        _plans = plans;
        _sessions = sessions;
        _states = new SessionFrameState[options.MaxSessions];
        _followed = new Vector3D[options.MaxSessions];
        _followedGeneration = new uint[options.MaxSessions];
        _followedEntity = new EntityId[options.MaxSessions];
        _ackHistory = new AckHistory(CommandTypeBuffers.AckCapacity(options.MaxSessions));
        _aggLastTick = new uint[options.MaxSessions];
        _aggGeneration = new ushort[options.MaxSessions];
        _debugGeneration = new ushort[options.MaxSessions];
        _debugHash = new ulong[options.MaxSessions];
        _aggAnchor = new Vector3D[options.MaxSessions];
        _aggRegion = new uint[options.MaxSessions][];
        _aggRegionCount = new int[options.MaxSessions];
        _followedValid = new bool[options.MaxSessions];
        _encodePlans = BuildEncodePlans(plans, catalog);
        _lagBoundTicks = SkipPolicy.LagBoundTicks(options, tickPeriodUs);
        _tickSeconds = tickPeriodUs / 1_000_000d;
        _silenceBoundTicks = SkipPolicy.SilenceBoundTicks(options, tickPeriodUs);


        // Converted once, here, beside the other two. The policy takes tick counts and never the options, so a duration cannot be read as a tick count by
        // a caller that does not know the tick rate - which is what the old bound was.
        _closeBoundTicks = SkipPolicy.CloseBoundTicks(options, tickPeriodUs);
        _degradeBoundTicks = SkipPolicy.DegradeBoundTicks(_closeBoundTicks);

        // The ceiling a frame is measured against: the operator's, but never above what the pool can serve — a frame larger than the largest size class
        // would be refused by the pool anyway, and refusing it here is what turns "the pool said no" into a number that names the reason.
        _maxFrameBytes = Math.Min(options.FrameBytes, FramePool.LargestClassBytes);

        Pool = new FramePool($"{id}.Pool", parent, allocator, options);
        // 63 bytes of slack for the alignment, pinned so the address that decides the offset cannot change afterwards. See the field's remarks.
        _sendStates = GC.AllocateArray<byte>((options.MaxSessions * SessionSendState.Bytes) + CacheLineBytes - 1, pinned: true);
        _sendStatesOffset = CacheLinePadding(_sendStates);
    }

    /// <summary>The frame pool every published frame's bytes come from.</summary>
    public FramePool Pool { get; }

    /// <summary>
    /// The <c>STATS</c> producer (P1-16), or <see langword="null"/> before it is attached and on a runtime that declares no metric.
    /// </summary>
    /// <remarks>
    /// Attached after construction because it reads the send pump and the ingress path, both of which the runtime builds after this object. Published with a
    /// release and read with an acquire: the write is on the thread that calls <c>Start</c>, the reads are on workers.
    /// </remarks>
    public StatsEncoder Stats => Volatile.Read(ref _stats);

    /// <summary>Binds the <c>STATS</c> producer. Called once, from <c>SubscriptionsRuntime</c>'s constructor, before any worker exists.</summary>
    /// <param name="stats">The producer.</param>
    public void AttachStats(StatsEncoder stats) => Volatile.Write(ref _stats, stats);

    /// <summary>The durability gate a send pump reads before it sends anything (P1-14b writes it).</summary>
    public FramePublicationGate Gate { get; } = new();

    /// <summary>Frames published since the runtime started.</summary>
    public long FramesProduced => Volatile.Read(ref _framesProduced);

    /// <summary>Sessions skipped: no free frame slot, no block from the pool, or a frame above the ceiling.</summary>
    public long FramesSkipped => Volatile.Read(ref _framesSkipped);

    /// <summary>Bytes of frame published.</summary>
    public long BytesEncoded => Volatile.Read(ref _bytesEncoded);

    /// <summary>Records published, across every kind.</summary>
    public long RecordsEncoded => Volatile.Read(ref _recordsEncoded);

    /// <summary>ENTER and LEAVE records published since start.</summary>
    public (long Entered, long Left) EnterFlow => (Volatile.Read(ref _entersEmitted), Volatile.Read(ref _leavesEmitted));

    /// <summary>Frames the ceiling refused, which the pool would have refused too.</summary>
    public long OversizeSkips => Volatile.Read(ref _oversizeSkips);

    /// <summary>Times a session was dropped a rate class for a run of skips.</summary>
    public long SessionsDegraded => Volatile.Read(ref _sessionsDegraded);

    /// <summary>Sessions closed with 1013 for a skip run past <see cref="SubscriptionsOptions.CloseStalledAfter"/>.</summary>
    public long SessionsClosedLagging => Volatile.Read(ref _sessionsClosedLagging);

    /// <summary>
    /// The longest run of consecutive skips any session has reached since the runtime started, in ticks.
    /// </summary>
    /// <remarks>
    /// <b>What <see cref="SubscriptionsOptions.CloseStalledAfter"/> has to clear.</b> A healthy client still stalls: a garbage collection, a throttled
    /// browser tab, a frame that took long to apply, ordinary scheduler jitter. Each of those stops it draining for a while and the session's run climbs.
    /// The close bound is only defensible if it sits above the tail of that distribution, and this is the only way to know where the tail is on a given
    /// deployment — asking "how long may a client stall" in the abstract has no answer. Read it beside <see cref="SessionsClosedLagging"/>: a high-water
    /// approaching the bound with no closures is a server about to start shedding clients that were doing nothing wrong.
    /// </remarks>
    public int LongestSkipRun => Volatile.Read(ref _longestSkipRun);

    /// <summary>Sessions closed with 4001 for having stopped sending <c>PING</c>.</summary>
    public long SessionsClosedSilent => Volatile.Read(ref _sessionsClosedSilent);

    /// <summary>The per-slot hand-off state, which a send pump claims frames through.</summary>
    /// <param name="slot">The session table row.</param>
    /// <returns>The state.</returns>
    public ref SessionSendState SendStateOf(int slot) => ref MemoryMarshal.Cast<byte, SessionSendState>(_sendStates.AsSpan(_sendStatesOffset))[slot];

    /// <summary>A cache line, the granularity <see cref="SessionSendState"/>'s layout is built around.</summary>
    private const int CacheLineBytes = 64;

    /// <summary>
    /// How many bytes to skip in <paramref name="pinned"/> for a cache-line-aligned start.
    /// </summary>
    /// <param name="pinned">A pinned array, so the answer stays true for its lifetime.</param>
    /// <remarks>
    /// One address read, to compute a constant; the pointer is not retained. Holding a pointer over GC memory is what the engine's rule forbids — a buffer
    /// reachable only through one can be freed under it — and a number derived from an address is not a way to reach memory.
    /// </remarks>
    private static int CacheLinePadding(byte[] pinned)
    {
        var misalignment = (int)((nuint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(pinned)) % CacheLineBytes);
        return misalignment == 0 ? 0 : CacheLineBytes - misalignment;
    }

    /// <summary>One session's frame state, or <see langword="null"/> when the slot has never produced a frame.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The state.</returns>
    public SessionFrameState StateOf(SessionId session)
    {
        var slot = session.Slot;
        return slot < (uint)_states.Length ? _states[slot] : null;
    }

    /// <summary>The archetype encoding constants, parallel to the runtime's plans.</summary>
    /// <param name="archetype">The plan index.</param>
    /// <returns>The encoding constants.</returns>
    public ArchetypeEncodePlan EncodePlanOf(int archetype) => _encodePlans[archetype];

    /// <summary>
    /// The stage's prologue: applies the skip policy to every open session, collects this tick's sessions and prepares their state, and returns the chunk
    /// count.
    /// </summary>
    /// <param name="tickNumber">The tick.</param>
    /// <param name="workerCount">Worker-pool width.</param>
    /// <returns>Chunks the stage should dispatch.</returns>
    /// <remarks>Single-threaded, before the dispatch.</remarks>
    public int BeginTick(long tickNumber, int workerCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var prologueFrom = PhaseTimingEnabled ? Stopwatch.GetTimestamp() : 0L;
        _tick = tickNumber;

        // The PREVIOUS tick's chunk stamps, folded before this tick overwrites them. Doing it here rather than at the end of the stage is what keeps it
        // off every worker: the prologue is single-threaded by construction.
        if (PhaseTimingEnabled && _lastChunkCount > 0)
        {
            var sum = 0L;
            var max = 0L;
            var first = long.MaxValue;
            var last = 0L;
            var lastStart = 0L;
            var entered = 0;
            for (var i = 0; i < _lastChunkCount && i < _chunkBusy.Length; i++)
            {
                var busy = _chunkBusy[i];
                sum += busy;
                if (busy > max)
                {
                    max = busy;
                }

                if (_chunkStart[i] != 0)
                {
                    entered++;
                    if (_chunkStart[i] < first)
                    {
                        first = _chunkStart[i];
                    }

                    if (_chunkStart[i] > lastStart)
                    {
                        lastStart = _chunkStart[i];
                    }

                    if (_chunkEnd[i] > last)
                    {
                        last = _chunkEnd[i];
                    }
                }

                _chunkBusy[i] = 0;
                _chunkStart[i] = 0;
                _chunkEnd[i] = 0;
            }

            if (last > first && first != long.MaxValue)
            {
                _spanTicks += last - first;
                _chunksEntered += entered;

                _startSpreadTicks += lastStart - first;
            }

            if (max > 0)
            {
                _busySum += sum;
                _busyMax += max;
                _busyChunks += _lastChunkCount;
                _busyTicks++;
            }
        }

        _lastChunkCount = 0;

        // Before the early return, because the sweep is about sessions that are NOT being served: one whose profile reaches nothing this tick still has to
        // be degraded, closed for a skip run, or closed for silence.
        var sweepFrom = prologueFrom != 0L ? Stopwatch.GetTimestamp() : 0L;
        SweepSkipPolicy();
        if (sweepFrom != 0L)
        {
            _sweepTicks2 += Stopwatch.GetTimestamp() - sweepFrom;
        }

        var prepFrom = prologueFrom != 0L ? Stopwatch.GetTimestamp() : 0L;
        BeginPushTick();
        if (prepFrom != 0L)
        {
            _prepareTicks += Stopwatch.GetTimestamp() - prepFrom;
        }

        if (_pushSessionCount + _eventSessionCount == 0)
        {
            return 0;
        }

        var workers = Math.Max(1, workerCount);
        EnsureWorkers(workers);

        // The one encode of the tick's shared server segment (W25), here because this is the track's last single-threaded point before the chunks run: the
        // dispatch that follows is the barrier that publishes the bytes to every worker that will copy them. Through the property, not the field, so the one
        // acquire the field documents is the only way it is ever read.
        Stats?.BeginTick(tickNumber);

        if (prologueFrom != 0L)
        {
            _prologueTicks += Stopwatch.GetTimestamp() - prologueFrom;
        }

        var chunks = Math.Min(workers, _pushSessionCount + _eventSessionCount);
        _lastChunkCount = chunks;
        if (_chunkBusy.Length < chunks)
        {
            Array.Resize(ref _chunkBusy, Math.Max(16, chunks));
            Array.Resize(ref _chunkStart, Math.Max(16, chunks));
            Array.Resize(ref _chunkEnd, Math.Max(16, chunks));
        }

        return chunks;
    }

    /// <summary>The single-threaded prologue's cost per tick, and the two halves of it, in ms.</summary>
    public (double Prologue, double Sweep, double Prepare) PrologueMs
    {
        get
        {
            var ticks = Math.Max(1L, Volatile.Read(ref _busyTicks));
            var scale = 1000d / Stopwatch.Frequency / ticks;
            return (Volatile.Read(ref _prologueTicks) * scale, Volatile.Read(ref _sweepTicks2) * scale, Volatile.Read(ref _prepareTicks) * scale);
        }
    }

    /// <summary>
    /// The frame stage's parallel efficiency: CPU summed over chunks against the slowest chunk, averaged over the ticks measured.
    /// </summary>
    /// <remarks>
    /// <b>Effective workers = sum / max, and efficiency = that over the chunk count.</b> A stage whose sessions cost the same would sit near 1; one whose
    /// slowest chunk does ten times the median sits near 0.1, and every worker but that one is idle for nine tenths of the stage. Zero unless phase timing
    /// is enabled.
    /// </remarks>
    public (double Effective, double Efficiency, long Ticks) ChunkBalance
    {
        get
        {
            var max = Volatile.Read(ref _busyMax);
            var ticks = Volatile.Read(ref _busyTicks);
            if (max == 0 || ticks == 0)
            {
                return (0d, 0d, 0L);
            }

            var effective = (double)Volatile.Read(ref _busySum) / max;
            var chunks = (double)Volatile.Read(ref _busyChunks) / ticks;
            return (effective, chunks <= 0d ? 0d : effective / chunks, ticks);
        }
    }

    /// <summary>
    /// The stage's SPAN against the work inside it: busy time summed over chunks, the wall from the first chunk entering to the last leaving, how many
    /// chunks actually ran, and the concurrency the two imply.
    /// </summary>
    /// <remarks>
    /// <b>Busy-over-max measures imbalance; this measures whether the chunks ran at the same time at all.</b> A dynamic cursor makes every chunk finish
    /// together, so imbalance goes to zero whether or not the workers overlapped — only the span can tell the difference between thirty-two workers for
    /// one millisecond and one worker for thirty-two.
    /// </remarks>
    public (double SpanMs, double BusyMs, double Concurrency, double StartSpreadMs) ChunkSpan
    {
        get
        {
            var ticks = Math.Max(1L, Volatile.Read(ref _busyTicks));
            var scale = 1000d / Stopwatch.Frequency / ticks;
            var span = Volatile.Read(ref _spanTicks) * scale;
            var busy = Volatile.Read(ref _busySum) * scale;
            return (span, busy, span <= 0d ? 0d : busy / span, Volatile.Read(ref _startSpreadTicks) * scale);
        }
    }

    /// <summary>Assembles, encodes and publishes a frame for each session this chunk takes from the shared cursor.</summary>
    /// <param name="chunkIndex">The chunk, which is also the index of the scratch it uses.</param>
    /// <param name="chunkCount">How many chunks the stage dispatched.</param>
    public void ExecuteChunk(int chunkIndex, int chunkCount)
    {
        if (chunkCount <= 0 || (uint)chunkIndex >= (uint)_workers.Length)
        {
            return;
        }

        var from = PhaseTimingEnabled ? Stopwatch.GetTimestamp() : 0L;
        var scratch = _workers[chunkIndex];

        // Accumulated on the stack and flushed once: the whole chunk's tally lands on the shared fields in one place, which is what keeps every worker off
        // two cache lines for the length of the stage.
        var counters = default(FrameCounters);
        ExecutePushSessions(scratch, ref counters);

        if (from != 0L)
        {
            var now = Stopwatch.GetTimestamp();
            _chunkBusy[chunkIndex] = now - from;
            _chunkStart[chunkIndex] = from;
            _chunkEnd[chunkIndex] = now;
        }

        Flush(in counters);
    }

    /// <summary>Adds one chunk's tally to the assembler's, once per worker per tick.</summary>
    /// <param name="counters">The chunk's tally.</param>
    private void Flush(in FrameCounters counters)
    {
        Add(ref _framesProduced, counters.FramesProduced);
        Add(ref _bytesEncoded, counters.BytesEncoded);
        Add(ref _recordsEncoded, counters.RecordsEncoded);
        Add(ref _entersEmitted, counters.EntersEmitted);
        Add(ref _leavesEmitted, counters.LeavesEmitted);
        Add(ref _oversizeSkips, counters.OversizeSkips);
        Add(ref SelfBlocks, counters.SelfBlocks);
        Add(ref AcksWritten, counters.AcksWritten);
        Add(ref AcksOverflowed, counters.AcksOverflowed);
        Add(ref AckWindowsLost, counters.AckWindowsLost);
        Add(ref _gatherTicks, counters.GatherTicks);
        Add(ref _sortTicks, counters.SortTicks);
        Add(ref _encodeTicks, counters.EncodeTicks);
        Add(ref _publishTicks, counters.PublishTicks);
    }


    /// <summary>Whether the frame stage accumulates per-phase timings. Off by default; one static read per phase boundary when off.</summary>
    /// <remarks>
    /// A process-wide switch rather than an option, for the reason this repository's perf rule gives: two builds differ in JIT codegen as well as in the
    /// line under test, so an A/B wants one binary and one switch.
    /// </remarks>
    public static bool PhaseTimingEnabled;

    private long _gatherTicks;
    private long _sortTicks;
    private long _encodeTicks;
    private long _publishTicks;

    /// <summary>The frame stage's phases, in milliseconds of CPU summed over workers since start.</summary>
    public (double Gather, double Sort, double Encode, double Publish) PhaseMilliseconds
    {
        get
        {
            var scale = 1000d / Stopwatch.Frequency;
            return (Volatile.Read(ref _gatherTicks) * scale,
                Volatile.Read(ref _sortTicks) * scale,
                Volatile.Read(ref _encodeTicks) * scale,
                Volatile.Read(ref _publishTicks) * scale);
        }
    }


    private static void Add(ref long target, long value)
    {
        if (value != 0)
        {
            Interlocked.Add(ref target, value);
        }
    }

    /// <summary>How many worker scratches exist, and therefore how many ready lists a driver has to walk.</summary>
    public int WorkerCount => _workers.Length;

    /// <summary>The session slots one worker published a frame for this tick.</summary>
    /// <param name="worker">The worker, below <see cref="WorkerCount"/>.</param>
    /// <returns>The slots, in publication order.</returns>
    public ReadOnlySpan<SessionId> ReadyOf(int worker)
    {
        var scratch = _workers[worker];
        return scratch == null ? default : scratch.Ready;
    }

    /// <summary>Empties every worker's ready list. Called by the tick driver once it has handed them on.</summary>
    public void ClearReady()
    {
        for (var i = 0; i < _workers.Length; i++)
        {
            _workers[i]?.ClearReady();
        }
    }

    /// <inheritdoc />
    public void Dispose() => Dispose(pumpsStillRunning: false);

    /// <summary>
    /// Tears the assembler down, leaving the frame pool's slabs allocated when <paramref name="pumpsStillRunning"/> says a send may still be reading out of one.
    /// </summary>
    /// <param name="pumpsStillRunning">
    /// <see cref="SendPump.PumpsStillRunningAtDispose"/> was non-zero: a pump was still inside a send when the quiesce deadline passed.
    /// </param>
    /// <remarks>
    /// The send states need nothing here — they are a managed array this object references (#1006). The frame BYTES are different: they are native by
    /// necessity, because they exist to reach a socket without a copy, and a pointer into them is held across the send's await. See
    /// <see cref="FramePool.KeepSlabsForOutstandingSends"/>.
    /// </remarks>
    internal void Dispose(bool pumpsStillRunning)

    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        for (var i = 0; i < _workers.Length; i++)
        {
            _workers[i]?.Dispose();
        }

        _workers = [];

        // The counters and the slots go FIRST, and nothing here reads them. Their buffer is a child of the resource parent, which may already have been
        // torn down by the time this runs; and a block still sitting in a slot needs no return, because the pool is about to free the slabs it was carved
        // from. Walking the slots to hand them back would be bookkeeping paid for with a read of memory that may no longer exist.
        // The send states are a managed array held by a field, so there is nothing to free here: the GC keeps them alive while this object is, and a send pump
        // that outlives the quiesce can complete into them without writing through a freed address (#1006).

        for (var slot = 0; slot < _states.Length; slot++)
        {
            _states[slot] = null;
        }

        if (pumpsStillRunning)
        {
            Pool.KeepSlabsForOutstandingSends();
        }

        Pool.Dispose();
    }


    /// <summary>
    /// Emits the push-replication operator records (#WB-02, kinds 68 and 69) once per stats period.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Called from the end of <c>SubscriptionsContext.Reset</c>, not from this class's own <c>BeginTick</c>.</b> An operator needs "0 sessions, 0 bytes/s"
    /// to arrive as a reading — a server whose clients have all left must not look like an engine that stopped reporting. `BeginTick` cannot provide that at
    /// any position inside it: the frames track is not dispatched at all when no session is served, so the method is never entered. `Reset` runs
    /// unconditionally on the tick driver ahead of the fence, which is the only per-tick single-threaded point that is always reached. It is called LAST in
    /// that method, after every per-tick field is reset, because a throw from here propagates out of <c>OnTickEndInternal</c> — see the comment at the call.
    ///
    /// <b>Nothing reached here may throw, and that is not a style preference.</b> The first version asked <see cref="StatsEncoder"/> for a durability
    /// percentile as a call argument, and on tick 0 that pass indexed a tick-telemetry ring holding nothing and threw. C# evaluates arguments before entering
    /// the gated method, so the throw happened whether or not the record was wanted; because the ring is written at the END of a tick, the throw stopped the
    /// recording that would have made the next tick's call legal, and the condition sustained itself. The symptom was not an exception anywhere visible — it
    /// was 508 ticks, 2 337 trace records and not one record of kind 68, with the gate true and every null check passing. The window passes tolerate an empty
    /// ring now; a new argument here is a new obligation to check.
    ///
    /// <b>Independent of <see cref="StatsEncoder"/>, and on its own window.</b> The encoder's <c>BeginTick</c> returns before collecting anything
    /// when the application declared no metrics, because with an empty catalog there is no client-facing <c>STATS</c> block to encode. An operator's view of
    /// replication must not be silenceable that way — an app that declares no metrics is exactly an app whose replication nobody has looked at. So this runs
    /// unconditionally at the same cadence, keeps its own <see cref="_operatorEmissionTick"/> / <see cref="_operatorSentBytesMark"/> pair, and reads the
    /// encoder's figures on demand instead of borrowing values indexed by the app's plan.
    /// </para>
    /// <para>
    /// <b>Cost when the gates are off is two flag reads</b> — but only because they are read HERE, before anything else. The generator puts each gate check
    /// inside its <c>Emit</c>, and C# evaluates arguments before the call, so passing a percentile as an argument would compute it whether or not the record is
    /// wanted. Reading both flags up front is what makes the claim true; it was not, in the first version of this method.
    /// </para>
    /// <para>
    /// <b>The row emission is capped</b> at <see cref="OperatorSessionRowCap"/>. Volume scales with the session count, not with the engine, so a server at its
    /// 8192-session limit would otherwise spend the trace on rows nobody reads; the server record's <c>ReportedSessions</c> against its <c>Sessions</c> says
    /// what was left out, and a consumer is required to render that difference rather than treat the rows as the whole population.
    /// </para>
    /// </remarks>
    internal void EmitOperatorTelemetry(long tickNumber)
    {
        var stats = Stats;
        if (stats == null)
        {
            return;
        }

        // Gate FIRST, and here rather than relying on the generated Emit. The generator puts `if (!TelemetryConfig.<Gate>) return;` INSIDE the emit method
        // (TraceEventGenerator.cs:1273/1428/1446), and C# evaluates a call's arguments before entering it — so passing `stats.TrackP99Ms(...)` and
        // `stats.DurabilityWaitP99Ms(...)` as arguments ran two window passes and two Array.Sorts every second with both gates off, and mutated the encoder's
        // own `_samples` scratch while doing it. `DatabaseEngine.TickFence.cs` uses this same shape for the same reason.
        var wantServer = TelemetryConfig.SubscriptionsServerTelemetryActive;
        var wantSessions = TelemetryConfig.SubscriptionsSessionTelemetryActive;
        if (!wantServer && !wantSessions)
        {
            return;
        }

        var period = stats.EmissionPeriodTicksForTest > 0 ? stats.EmissionPeriodTicksForTest : stats.EmissionPeriodTicks;
        if (tickNumber % period != 0)
        {
            return;
        }

        // The window is this emission's own, in ticks, and never zero — the first emission would otherwise divide by the absolute tick number.
        var window = _operatorEmissionTick < 0 ? period : (int)Math.Min(period, Math.Max(1, tickNumber - _operatorEmissionTick));
        // Volatile.Read, pairing with SetTickState's Volatile.Write, exactly as the two readers in FrameAssembler.Push.cs do. The runtime publishes the live
        // period from the tick's start and this runs on the tick driver, so the ordering is real; on x64 the acquire is a plain mov, so the pairing is free.
        var seconds = window * Volatile.Read(ref _tickSeconds);
        var sent = stats.SentBytes;
        var outBytesPerSec = seconds <= 0 ? 0 : Math.Max(0, sent - _operatorSentBytesMark) / seconds;

        // One pass over the open sessions, serving both records: the server's skip total and, under its own gate, the per-session rows.
        var skipped = 0L;
        var reported = 0;
        {
            var sessions = _sessions.GetEnumerator();
            while (sessions.MoveNext())
            {
                var session = sessions.Current;
                var slot = session.Slot;
                if (slot >= (uint)_states.Length)
                {
                    continue;
                }

                var state = _states[slot];
                if (state == null)
                {
                    continue;
                }

                skipped += state.FramesSkipped;

                if (!wantSessions || reported >= OperatorSessionRowCap)
                {
                    continue;
                }

                // Our own mark, never `StatsBytesMark`: that one is written only by `StatsEncoder.WriteSessionSegment`, which runs only when the
                // application declared metrics AND the session holds the Stats capability. With no metric catalog — the very case this emission exists to
                // cover — it stays 0 for the session's life, so subtracting it divided a lifetime byte total by a one-second window and reported a figure
                // that climbed for ever.
                var bytes = Math.Max(0, state.BytesPublished - state.OperatorStatsBytesMark);
                state.OperatorStatsBytesMark = state.BytesPublished;
                TyphonEvent.EmitSubscriptionsSessionTelemetry(
                    sessionId: session.Value,
                    // -1 before the session's first RESET, widened to the wire's unsigned field so a consumer can tell that from realm 0.
                    realmId: state.CommittedRealm < 0 ? ushort.MaxValue : (ushort)state.CommittedRealm,
                    bytesPerSec: seconds <= 0 ? 0f : (float)(bytes / seconds),
                    framesSkipped: state.FramesSkipped,
                    degradeLevel: state.DegradeLevel);
                reported++;
            }
        }

        if (!wantServer)
        {
            _operatorEmissionTick = tickNumber;
            _operatorSentBytesMark = sent;
            return;
        }

        var pool = Pool;
        TyphonEvent.EmitSubscriptionsServerTelemetry(
            sessions: stats.OpenSessions,
            netOutBytesPerSec: (float)outBytesPerSec,
            trackP99Ms: (float)stats.TrackP99Ms(tickNumber, window),
            durabilityWaitP99Ms: (float)stats.DurabilityWaitP99Ms(tickNumber, window),
            framesSkipped: skipped,
            framePoolRented: pool?.RentedCount ?? 0,
            framePoolBlocks: pool?.BlockCount ?? 0,
            framePoolBudgetSkips: pool?.BudgetSkipCount ?? 0,
            reportedSessions: reported);

        _operatorEmissionTick = tickNumber;
        _operatorSentBytesMark = sent;
    }

    // ── The prologue ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads every open session's skip run and last-heard-from mark against the operator's thresholds, degrading and closing where they are crossed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On the tick thread, walking the table.</b> The session table's open list is the tick's to read, and closing is the tick's to do — which is why this
    /// is here and not in the send pump, where the same decision would need the table's synchronisation and would violate SUB-05's single-writer rule.
    /// </para>
    /// <para>
    /// <b>Closes are requested, not applied.</b> <see cref="SessionTable.RequestClose"/> latches the code and the reason; the table applies it at its own
    /// pending-close point, so a session closed here is still a legal, drained row for the rest of this tick.
    /// </para>
    /// </remarks>
    private void SweepSkipPolicy()
    {
        var sessions = _sessions.GetEnumerator();
        while (sessions.MoveNext())
        {
            var session = sessions.Current;
            var slot = session.Slot;
            if (slot >= (uint)_states.Length)
            {
                continue;
            }

            var state = BindSlot(slot, session);
            ref var send = ref SendStateOf(slot);

            // Silence first: a client that has stopped talking is gone whatever its skip run says, and 4001 tells its SDK to reconnect rather than to back off
            // as 1013 would.
            // The stamp is the tick plus one, so zero is "this slot was never bound" and every real tick — tick zero included — is a mark the sweep can use.
            var heardFrom = send.PingStamp;
            if (heardFrom > 0 && _tick - (heardFrom - 1) > _silenceBoundTicks)
            {
                _sessions.RequestClose(session, SessionCloseReason.Unacknowledged, CloseCodes.NoAcknowledgement);
                Interlocked.Increment(ref _sessionsClosedSilent);
                continue;
            }

            var skipRun = send.SkipRun;

            // The high-water mark, read where every session's run is already in hand. One compare per open session per tick, on the prologue rather than on
            // the encode path, and it is what turns the close bound from a number somebody chose into one the deployment's own behaviour argues for.
            if (skipRun > Volatile.Read(ref _longestSkipRun))
            {
                Volatile.Write(ref _longestSkipRun, skipRun);
            }

            if (SkipPolicy.Evaluate(skipRun, _closeBoundTicks) == SkipVerdict.Close)
            {
                _sessions.RequestClose(session, SessionCloseReason.Lagging, CloseCodes.TryAgainLater);
                Interlocked.Increment(ref _sessionsClosedLagging);
                continue;
            }

            if (SkipPolicy.ShouldDegrade(skipRun, state.DegradeLevel, _degradeBoundTicks))
            {
                state.DegradeLevel++;
                state.FramesSinceDegrade = 0;
                Interlocked.Increment(ref _sessionsDegraded);
            }
            else if (state.DegradeLevel > 0 && state.FramesSinceDegrade >= SkipPolicy.RecoveryFrames)
            {
                state.DegradeLevel--;
                state.FramesSinceDegrade = 0;
            }
        }
    }

    /// <summary>
    /// The frame state of a slot, created or rebound to <paramref name="session"/> if it is not already.
    /// </summary>
    /// <param name="slot">The session table row.</param>
    /// <param name="session">Who occupies it.</param>
    /// <returns>The state, bound to this session's generation.</returns>
    /// <remarks>
    /// <b>Called from the sweep, which walks every open session, rather than only from the tick's served sessions.</b> A session bound to no profile — or to
    /// one whose observer reaches nothing — is served nothing, and binding it only there would leave it with no state: it could be neither degraded, nor
    /// closed for a skip run, nor closed for silence.
    /// </remarks>
    private SessionFrameState BindSlot(int slot, SessionId session)
    {
        var state = _states[slot];
        if (state == null)
        {
            state = new SessionFrameState();
            _states[slot] = state;
        }

        if (state.Generation != session.Generation)
        {
            // A new session in the slot. The hand-off counters were zeroed when the link was bound, on the admitting thread, so nothing is reset here: the
            // table only re-leases a row once every frame it produced has drained, and zeroing them now would race the PING that can already be arriving.
            state.RebindTo(session.Generation, _tick);
        }

        return state;
    }

    private void PrepareSession(SessionId session)
    {
        var slot = session.Slot;
        if (slot >= (uint)_states.Length)
        {
            return;
        }

        var state = BindSlot(slot, session);
        var profile = _sessions.ProfileName(session);
        if (!string.Equals(profile, state.Profile, StringComparison.Ordinal))
        {
            // A profile switch: the view the client holds describes an interest that no longer exists, so the next frame tells it to clear the store and
            // refills from nothing (03 § 5). The first frame of a session takes this path too, but its RESET costs a client with an empty store nothing.
            state.Profile = profile;
            state.PendingReset = state.FramesProduced > 0;
        }
    }

    private int SortAndCount(FrameWorkerScratch scratch)
    {
        var records = 0;
        for (var a = 0; a < _plans.Length; a++)
        {
            for (var k = 0; k < 4; k++)
            {
                var kind = (FrameListKind)k;
                var count = scratch.Count(a, kind);
                if (count == 0)
                {
                    continue;
                }

                scratch.SortByNetId(a, kind);
                records += count;
            }
        }

        return records;
    }

    private int UpperBound(FrameWorkerScratch scratch)
    {
        var bound = EntitiesEncoder.MaxHeaderBytes;
        for (var a = 0; a < _plans.Length; a++)
        {
            var enters = scratch.Count(a, FrameListKind.Enter);
            var segments = scratch.Count(a, FrameListKind.Segment);
            var states = scratch.Count(a, FrameListKind.State);
            var leaves = scratch.Count(a, FrameListKind.Leave);
            if (enters == 0 && segments == 0 && states == 0 && leaves == 0)
            {
                continue;
            }

            var plan = _encodePlans[a];
            bound += EntitiesEncoder.MaxBlockOverheadBytes
                + (enters * plan.MaxEnterBytes)
                + (segments * plan.MaxSegmentBytes)
                + (states * plan.MaxStateBytes)
                + (leaves * EntitiesEncoder.MaxGapBytes);
        }

        return bound;
    }

    /// <summary>Records that no frame was produced for a session this tick.</summary>
    /// <param name="state">The session's state.</param>
    /// <param name="counted">
    /// Whether this counts against <c>typhon.session.skippedFrames</c>. False for a tick that produced nothing because there was nothing to say.
    /// </param>
    /// <remarks>
    /// <b>An idle tick is not a skip, and the distinction is the metric's whole meaning.</b> A skip is back-pressure — K slots full, an acknowledgement too
    /// far behind, a pool that would not lend — and an operator reading a rising skip count is reading "this session cannot keep up". A world that had
    /// nothing to send is none of that, and counting it made every quiet session look like a struggling one. What the session holds is left alone either
    /// way, which is what SUB-03 requires.
    /// </remarks>
    private void NoteSkip(SessionFrameState state, bool counted = true)
    {
        if (!counted)
        {
            return;
        }

        state.FramesSkipped++;
        Interlocked.Increment(ref _framesSkipped);
    }

    private void ReturnIfValid(in FrameBlock block)
    {
        if (block.IsValid)
        {
            Pool.Return(block);
        }
    }

    private void EnsureWorkers(int workers)
    {
        if (_workers.Length >= workers)
        {
            return;
        }

        var grown = new FrameWorkerScratch[workers];
        Array.Copy(_workers, grown, _workers.Length);
        for (var i = _workers.Length; i < workers; i++)
        {
            grown[i] = new FrameWorkerScratch();
        }

        _workers = grown;
    }

    // ── Encoding constants ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static ArchetypeEncodePlan[] BuildEncodePlans(CompiledProjectionPlan[] plans, Catalog catalog)
    {
        var encodePlans = new ArchetypeEncodePlan[plans.Length];
        for (var i = 0; i < plans.Length; i++)
        {
            var plan = plans[i];
            var layout = plan.BlockLayout;
            var moving = plan.Position is { Moving: true };
            var axisBytes = plan.Position != null ? plan.Position.Pos.Bits / 8 : 0;

            var groups = new ArchetypeEncodePlan.SectionWalk[plan.Groups.Length];
            var tickSlots = new int[plan.Groups.Length];
            var at = 0;
            for (var g = 0; g < plan.Groups.Length; g++)
            {
                groups[g] = Walk(plan.Fields, plan.Groups[g].Section, at);
                tickSlots[g] = plan.Groups[g].TickSlot;
                at += plan.Groups[g].Section.MaxBodyBytes;
            }

            var ownerGroups = new ArchetypeEncodePlan.SectionWalk[plan.OwnerGroups.Length];
            var ownerAt = 0;
            var ownerAll = 0;
            for (var g = 0; g < plan.OwnerGroups.Length; g++)
            {
                ownerGroups[g] = Walk(plan.OwnerFields, plan.OwnerGroups[g].Section, ownerAt);
                ownerAt += plan.OwnerGroups[g].Section.MaxBodyBytes;
                ownerAll |= 1 << g;
            }

            var enterPosBytes = moving ? layout.SegmentBytes : layout.EnterPositionBytes;
            encodePlans[i] = new ArchetypeEncodePlan
            {
                WireIndex = WireIndexOf(catalog, plan.Name, i),
                Layout = layout,
                HasPosition = plan.Position != null,
                Moving = moving,
                PositionAxisBytes = axisBytes,
                GroupCount = plan.Groups.Length,
                GroupTickSlot = tickSlots,
                MotionTickSlot = moving ? 0 : -1,
                OnEnter = Walk(plan.Fields, plan.OnEnter, 0),
                Groups = groups,
                OwnerGroups = ownerGroups,
                OwnerAllMask = (byte)ownerAll,
                MaxSelfBytes = 24 + ownerAt,
                MaxEnterBytes = EntitiesEncoder.MaxGapBytes + enterPosBytes + layout.EnterBodyBytes + plan.MaxStateBodyBytes,
                MaxSegmentBytes = EntitiesEncoder.MaxGapBytes + layout.SegmentBytes,
                MaxStateBytes = EntitiesEncoder.MaxGapBytes + 1 + plan.MaxStateBodyBytes,
            };
        }

        return encodePlans;
    }

    /// <summary>
    /// The canonical wire index of an archetype, which is the catalog's and not the plan's own position.
    /// </summary>
    /// <remarks>
    /// A plan's index is declaration order; the catalog canonicalizes by name, so the two agree only by accident. Falling back to the plan index when no
    /// catalog is supplied is what lets a fixture drive the encoder without building one, and a production runtime always has one.
    /// </remarks>
    private static int WireIndexOf(Catalog catalog, string name, int fallback)
    {
        var archetypes = catalog?.Archetypes;
        if (archetypes == null)
        {
            return fallback;
        }

        for (var i = 0; i < archetypes.Length; i++)
        {
            if (string.Equals(archetypes[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return fallback;
    }

    private static ArchetypeEncodePlan.SectionWalk Walk(CompiledField[] fields, in CompiledSection section, int offset)
    {
        var aligned = section.FieldCount - section.PackedCount;
        var widths = new int[aligned];
        var fixedBytes = section.PackBytes;
        var variable = false;
        for (var i = 0; i < aligned; i++)
        {
            ref readonly var field = ref fields[section.FirstField + section.PackedCount + i];
            var width = field.CodecKind is CodecKind.Varu or CodecKind.Vari or CodecKind.EntityRef ? 0 : field.MaxBodyBytes;
            widths[i] = width;
            variable |= width == 0;
            fixedBytes += width;
        }

        return new ArchetypeEncodePlan.SectionWalk
        {
            FixedBytes = variable ? -1 : fixedBytes,
            PackBytes = section.PackBytes,
            FieldBytes = widths,
            Offset = offset,
            MaxBytes = section.MaxBodyBytes,
        };
    }
}
