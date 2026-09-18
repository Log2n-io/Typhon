using System;
using System.Numerics;
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
/// One worker's scratch for a tick of S2b: the four sub-lists per archetype, the sort's ping-pong partner, and the buffer a frame is encoded into.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per worker, so nothing here is shared and nothing here is synchronized</b> — the same property <see cref="HitArena"/> and <see cref="RecordArena"/>
/// rest on. A session belongs to exactly one chunk for the whole of its assembly, and the lists are rewound between sessions rather than between ticks.
/// </para>
/// <para>
/// <b>Native, and grown by doubling, so the steady state allocates nothing managed</b> (SUB-07). The one managed array is the radix histogram, which is
/// reached through a <see cref="Span{T}"/> and never through a pointer.
/// </para>
/// </remarks>
internal sealed unsafe class FrameWorkerScratch : IDisposable
{
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
    private RecordList _enterCandidates;
    private FrameRecord* _sortScratch;
    private int _sortCapacity;
    private byte* _bytes;
    private int _byteCapacity;
    private int[] _shareKey = [];
    private bool _disposed;

    /// <summary>This worker's cache of the last frame it encoded, so identical sessions copy rather than re-encode (P1-15).</summary>
    public SharedFrameSet Shared { get; } = new();

    /// <summary>Native bytes this scratch holds, for the owner's resource accounting.</summary>
    public long EstimatedBytes
    {
        get
        {
            var bytes = (long)_byteCapacity + ((long)_sortCapacity * sizeof(FrameRecord)) + ((long)_enterCandidates.Capacity * sizeof(FrameRecord));
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

        _enterCandidates.Count = 0;
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

    /// <summary>The enter candidates gathered so far, across every archetype — the list the enter budget ranks and cuts.</summary>
    public Span<FrameRecord> EnterCandidates => new(_enterCandidates.Items, _enterCandidates.Count);

    /// <summary>Appends a record to one archetype's sub-list.</summary>
    /// <param name="archetype">The archetype's plan index.</param>
    /// <param name="kind">The sub-list.</param>
    /// <param name="record">The record.</param>
    public void Add(int archetype, FrameListKind kind, in FrameRecord record) => Append(ref _lists[(archetype * 4) + (int)kind], in record);

    /// <summary>Appends an enter candidate to the flat staging list the budget selects from.</summary>
    /// <param name="record">The candidate.</param>
    public void AddEnterCandidate(in FrameRecord record) => Append(ref _enterCandidates, in record);

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

    /// <summary>Sorts the enter candidates ascending by the budget's rank, netId breaking ties.</summary>
    public void SortCandidatesByRank()
    {
        if (_enterCandidates.Count < 2)
        {
            return;
        }

        EnsureSortScratch(_enterCandidates.Count);
        RecordSorter.SortByRank(EnterCandidates, new Span<FrameRecord>(_sortScratch, _enterCandidates.Count), _histogram);
    }

    /// <summary>
    /// The per-archetype record counts, as the cross-check half of the shared-frame key.
    /// </summary>
    /// <param name="archetypes">How many replicated archetypes the runtime holds.</param>
    /// <returns>Four counts per archetype — enters, segments, states, leaves — in plan order.</returns>
    /// <remarks>
    /// Not the guarantee that two frames are identical; that comes from the construction argument in <see cref="SharedFrameSet"/>. This is what makes a
    /// mistaken reuse implausible rather than merely unlikely, at the cost of one pass over the archetype table.
    /// </remarks>
    public ReadOnlySpan<int> ShareKey(int archetypes)
    {
        if (_shareKey.Length != archetypes * 4)
        {
            _shareKey = new int[archetypes * 4];
        }

        for (var a = 0; a < archetypes; a++)
        {
            _shareKey[(a * 4) + 0] = Count(a, FrameListKind.Enter);
            _shareKey[(a * 4) + 1] = Count(a, FrameListKind.Segment);
            _shareKey[(a * 4) + 2] = Count(a, FrameListKind.State);
            _shareKey[(a * 4) + 3] = Count(a, FrameListKind.Leave);
        }

        return _shareKey;
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
        Shared.Dispose();
        for (var i = 0; i < _lists.Length; i++)
        {
            NativeMemory.Free(_lists[i].Items);
        }

        _lists = [];
        NativeMemory.Free(_enterCandidates.Items);
        NativeMemory.Free(_sortScratch);
        NativeMemory.Free(_bytes);
        _enterCandidates = default;
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

        _sortScratch = (FrameRecord*)NativeMemory.Realloc(_sortScratch, (nuint)capacity * (nuint)sizeof(FrameRecord));
        _sortCapacity = capacity;
    }
}

/// <summary>
/// Everything one session's frames need between ticks: what it knows, where its baseline is, and whether its view has finished filling.
/// </summary>
/// <remarks>
/// A managed object per session <i>slot</i>, kept for the life of the runtime and reset when the slot is handed to a new session, so a connect storm neither
/// allocates a known-set per connection nor churns the resource graph. The native halves — the table's entries and the hand-off's counters — live where
/// their own contracts put them.
/// </remarks>
internal sealed class SessionFrameState
{
    /// <summary>Creates the state of one session slot.</summary>
    /// <param name="known">The slot's known-set, created once and reused across the sessions that occupy the slot.</param>
    public SessionFrameState(KnownSet known)
    {
        ArgumentNullException.ThrowIfNull(known);
        Known = known;
    }

    /// <summary>What this session has been told exists.</summary>
    public KnownSet Known { get; }

    /// <summary>The generation of the session currently occupying the slot; a change is a new session and resets everything below.</summary>
    public ushort Generation { get; private set; }

    /// <summary>
    /// The tick of the last frame <b>produced</b> for this session (02 § 5). A group whose tick beats it is carried by the next frame; nothing else is.
    /// </summary>
    public long Baseline { get; set; }

    /// <summary>
    /// Forces the next frame to walk everything watched rather than only what changed.
    /// </summary>
    /// <remarks>
    /// Set when this frame carried a <b>stale leave</b>: a reused identity leaves in one frame and the reuse enters in the NEXT (SUB-06, 03 § 10), and the
    /// entity that must enter is by then unchanged — S1 stamped it on the tick the reuse happened, not on this one — so a gather that visits only changed
    /// slots would never see it and the session would lose the entity permanently. The flag is the handover between the two frames.
    /// </remarks>
    public bool ForceFullGather { get; set; }

    /// <summary>The profile the session was bound to when its last frame was built. A change is a <c>RESET</c>.</summary>
    public string Profile { get; set; }

    /// <summary>Whether the next frame must carry <c>RESET</c> and refill the view from nothing.</summary>
    public bool PendingReset { get; set; }

    /// <summary>Whether the initial fill under the enter budget has completed — the <c>VIEW_COMPLETE</c> flag.</summary>
    public bool ViewComplete { get; set; }

    /// <summary>Enter candidates the budget deferred on the last frame. Zero is what completes the view.</summary>
    public int DeferredEnters { get; set; }

    /// <summary>The focus the enter budget ranks by, in quantized position codes; see <see cref="FrameAssembler.SetFocus"/>.</summary>
    public uint FocusX { get; set; }

    /// <summary>The focus's second axis.</summary>
    public uint FocusY { get; set; }

    /// <summary>Whether a focus has been declared at all. Without one the budget selects in hit order.</summary>
    public bool HasFocus { get; set; }

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

    /// <summary>Rebinds the slot to a new session: the known-set is emptied and every per-session number starts again.</summary>
    /// <param name="generation">The new session's generation.</param>
    /// <param name="tick">
    /// The tick the slot is being bound at, which seeds the statistics window. Not zero: <c>typhon.session.outBytesPerSec</c> divides a window's bytes by
    /// <c>tick − StatsTick</c>, so a zero here would divide the first block's bytes by the absolute tick number — a session joining a runtime at tick 130 of
    /// a 10 Hz world would report its first second of traffic spread over thirteen.
    /// </param>
    public void RebindTo(ushort generation, long tick)
    {
        Generation = generation;
        Known.Clear();
        Baseline = 0;
        Profile = null;
        PendingReset = false;
        ViewComplete = false;
        DeferredEnters = 0;
        HasFocus = false;
        FramesProduced = 0;
        DegradeLevel = 0;
        FramesSinceDegrade = 0;
        FramesSkipped = 0;
        BytesPublished = 0;
        StatsTick = tick;
        StatsBytesMark = 0;
    }
}

/// <summary>
/// S2b — turns one session's hits into a <c>TICK</c> message, and hands it to the send side through the sequence protocol (SUB-04).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it holds per session:</b> a <see cref="KnownSet"/> (what the client has been told exists), a baseline tick (what it has been told about them),
/// a <see cref="SessionSendState"/> (the hand-off's four counters and two frame slots), and four booleans — the pending reset, the view-complete latch, the
/// deferred-enter count and the profile the last frame was built against. Nothing else: records are absolute, so there is no per-entity value memory
/// anywhere (SUB-03).
/// </para>
/// <para>
/// <b>Each frame is built in six steps, and the order is the rule.</b> Gather the hits into sub-lists; select the enters the budget allows; sweep the
/// known-set for what the hits did not reach; sort; encode; publish. Only then is the known-set mutated and the baseline advanced — <b>after</b> the frame
/// is published, never before. A frame that cannot be produced (no slot, no block) therefore leaves the session exactly as it was, and its next frame
/// carries the union: that is SUB-03, and building the commit as a separate step is how it is made true rather than hoped for.
/// </para>
/// <para>
/// <b>Bytes come from the replication block, never from S1's arena.</b> The arena holds the records of the tick that produced them; a session skipped for K
/// ticks needs everything since its baseline, which only the per-entity state carries. Choosing groups by <c>GroupTicks[g] &gt; baseline</c> against the hot
/// entry's stored bodies is the same code for K = 1 and for K = 50, so the skipped path is the path, not a rarely-exercised variant of it.
/// </para>
/// <para>
/// <b>Thread safety.</b> <see cref="BeginTick"/> is single-threaded and runs before the dispatch — it is where a session's state is created, rebound and
/// profile-checked, so no worker ever touches the resource graph. <see cref="ExecuteChunk"/> runs on pool workers over a disjoint slice of the tick's
/// sessions, each owning its scratch and its sessions' state outright.
/// </para>
/// </remarks>
internal sealed unsafe class FrameAssembler : IDisposable
{
    private readonly SubscriptionsOptions _options;
    private readonly CompiledProjectionPlan[] _plans;
    private readonly ArchetypeEncodePlan[] _encodePlans;
    private readonly SessionTable _sessions;
    private readonly IMemoryAllocator _allocator;
    private readonly IResource _parent;
    private readonly SessionFrameState[] _states;
    private readonly int _maxFrameBytes;
    private readonly int _lagBoundTicks;
    private readonly int _silenceBoundTicks;

    private PinnedMemoryBlock _sendBlock;
    private SessionSendState* _sendStates;
    private FrameWorkerScratch[] _workers = [];
    private InterestPass _interest;
    private long _tick;
    private int _tickSessionCount;
    private StatsEncoder _stats;

    private long _framesProduced;
    private long _framesSkipped;
    private long _bytesEncoded;
    private long _recordsEncoded;
    private long _entersDeferred;
    private long _oversizeSkips;
    private long _sessionsDegraded;
    private long _sessionsClosedLagging;
    private long _sessionsClosedSilent;
    private long _changedOnlyGathers;
    private long _fullGathers;
    private long _unprovenGathers;
    private bool _disposed;

    /// <summary>Builds the assembler and everything a frame is made of: the pool, the per-slot hand-off counters and the per-archetype encoding constants.</summary>
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
        _allocator = allocator;
        _parent = parent;
        _states = new SessionFrameState[options.MaxSessions];
        _encodePlans = BuildEncodePlans(plans, catalog);
        _lagBoundTicks = SkipPolicy.LagBoundTicks(options, tickPeriodUs);
        _silenceBoundTicks = SkipPolicy.SilenceBoundTicks(options, tickPeriodUs);

        // The ceiling a frame is measured against: the operator's, but never above what the pool can serve — a frame larger than the largest size class
        // would be refused by the pool anyway, and refusing it here is what turns "the pool said no" into a number that names the reason.
        _maxFrameBytes = Math.Min(options.FrameBytes, FramePool.LargestClassBytes);

        Pool = new FramePool($"{id}.Pool", parent, allocator, options);
        _sendBlock = allocator.AllocatePinned($"{id}.SendStates", parent, options.MaxSessions * SessionSendState.Bytes, true, 64);
        _sendStates = (SessionSendState*)_sendBlock.DataAsPointer;
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

    /// <summary>Enter candidates the per-frame budget deferred to a later frame.</summary>
    public long EntersDeferred => Volatile.Read(ref _entersDeferred);

    /// <summary>Frames the ceiling refused, which the pool would have refused too.</summary>
    public long OversizeSkips => Volatile.Read(ref _oversizeSkips);

    /// <summary>Times a session was dropped a rate class for a run of skips.</summary>
    public long SessionsDegraded => Volatile.Read(ref _sessionsDegraded);

    /// <summary>Sessions closed with 1013 for a skip run past <see cref="SubscriptionsOptions.CloseAfterSkips"/>.</summary>
    public long SessionsClosedLagging => Volatile.Read(ref _sessionsClosedLagging);

    /// <summary><b>Switch.</b> When false every session takes the full walk, which is what the fast path is measured against on one binary.</summary>
    internal bool ChangedOnlyGatherEnabled => _options.ChangedOnlyGather;

    /// <summary>Gathers that visited only the slots S1 marked changed.</summary>
    public long ChangedOnlyGathers => Volatile.Read(ref _changedOnlyGathers);

    /// <summary>Gathers that walked every watched slot — a session behind by more than one tick, still filling, or resetting.</summary>
    public long FullGathers => Volatile.Read(ref _fullGathers);

    /// <summary>Fast gathers that could not prove nothing had left and were redone in full. Counted in both of the above.</summary>
    public long UnprovenGathers => Volatile.Read(ref _unprovenGathers);

    /// <summary>Sessions closed with 4001 for having stopped sending <c>PING</c>.</summary>
    public long SessionsClosedSilent => Volatile.Read(ref _sessionsClosedSilent);

    /// <summary>
    /// <b>Test seam, and a deliberate one.</b> Advances a skipped session's baseline as though its frame had been produced — the exact violation SUB-03
    /// forbids — so the rule's verifier can be shown to reject it. It mirrors <c>SubscriptionsContext.FaultGateForTest</c>; nothing in production sets it.
    /// </summary>
    internal bool BaselineAdvancesOnSkipForTest;

    /// <summary>The per-slot hand-off state, which a send pump claims frames through.</summary>
    /// <param name="slot">The session table row.</param>
    /// <returns>The state.</returns>
    public SessionSendState* SendStateOf(int slot) => (SessionSendState*)((byte*)_sendStates + ((long)slot * SessionSendState.Bytes));

    /// <summary>One session's frame state, or <see langword="null"/> when the slot has never produced a frame.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The state.</returns>
    public SessionFrameState StateOf(SessionId session)
    {
        var slot = session.Slot;
        return (uint)slot < (uint)_states.Length ? _states[slot] : null;
    }

    /// <summary>The archetype encoding constants, parallel to the runtime's plans.</summary>
    /// <param name="archetype">The plan index.</param>
    /// <returns>The encoding constants.</returns>
    public ArchetypeEncodePlan EncodePlanOf(int archetype) => _encodePlans[archetype];

    /// <summary>
    /// Declares the point the enter budget ranks a session's deferred entities against — "nearest first" (01 § 4).
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="x">The focus's first axis, as a quantized position code of the archetype's <c>pos</c> codec.</param>
    /// <param name="y">The focus's second axis. A three-axis archetype is ranked on its first two: a ranking does not need the third, and a session's focus
    /// is one point for archetypes whose codecs need not agree on a third axis at all.</param>
    /// <remarks>
    /// <para>
    /// The ranking is in QUANTIZED CODE SPACE, not in metres, because that is the only position a replication block holds: the enter cache and the motion
    /// segment both store <c>p0</c> as the wire's codes, and converting them back per candidate to rank them would be arithmetic with no effect on the
    /// order. A code distance is monotone in the real one for a single archetype's codec, which is all a ranking needs.
    /// </para>
    /// <para>
    /// <b>Phase 1 declares no focus of its own.</b> A <c>World</c> observer has no origin — its region is the archetype — so this stays unset and the budget
    /// selects in hit order, which is the honest behaviour for an interest that has no notion of near. Phase 2's sphere and region observers carry a distance
    /// per hit (<see cref="HitArena"/>'s remarks), and that per-hit distance replaces this per-session approximation.
    /// </para>
    /// </remarks>
    public void SetFocus(SessionId session, uint x, uint y)
    {
        var state = StateOf(session);
        if (state == null)
        {
            return;
        }

        state.FocusX = x;
        state.FocusY = y;
        state.HasFocus = true;
    }

    /// <summary>
    /// The stage's prologue: binds this tick's interest output, creates or rebinds the state of every session in the partition, and returns the chunk count.
    /// </summary>
    /// <param name="interest">S2a's output for this tick.</param>
    /// <param name="tickNumber">The tick.</param>
    /// <param name="workerCount">Worker-pool width.</param>
    /// <returns>Chunks the stage should dispatch.</returns>
    /// <remarks>
    /// Single-threaded, before the dispatch. Creating a session's known-set here rather than on a worker is what keeps the resource graph off the parallel
    /// path — a node registers under its parent, and two workers registering at once would race a structure that has no reason to be concurrent.
    /// </remarks>
    public int BeginTick(InterestPass interest, long tickNumber, int workerCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _interest = interest;
        _tick = tickNumber;

        // Drop every worker's cached encode. The tick is part of the share key as well, so this is belt and braces — but it is what keeps a cached pointer
        // from outliving the buffer it points into if that key logic is ever changed, and it costs one flag per worker per tick.
        for (var i = 0; i < _workers.Length; i++)
        {
            _workers[i]?.Shared.BeginTick();
        }

        // Before the early return, because the sweep is about sessions that are NOT being served: one whose interest produced nothing this tick still has to
        // be degraded, closed for a skip run, or closed for silence. Keying it off the interest list would exempt exactly the sessions it exists to catch.
        SweepSkipPolicy();

        _tickSessionCount = interest?.TickSessionCount ?? 0;
        if (_tickSessionCount == 0)
        {
            return 0;
        }

        var workers = Math.Max(1, workerCount);
        EnsureWorkers(workers);

        // The one encode of the tick's shared server segment (W25), here because this is the track's last single-threaded point before the chunks run: the
        // dispatch that follows is the barrier that publishes the bytes to every worker that will copy them. Through the property, not the field, so the one
        // acquire the field documents is the only way it is ever read.
        Stats?.BeginTick(tickNumber);

        for (var i = 0; i < _tickSessionCount; i++)
        {
            PrepareSession(interest!.SessionAt(i));
        }

        return Math.Min(workers, _tickSessionCount);
    }

    /// <summary>Assembles, encodes and publishes a frame for each session this chunk owns.</summary>
    /// <param name="chunkIndex">The chunk, which is also the index of the scratch it uses.</param>
    /// <param name="chunkCount">How many chunks the stage dispatched.</param>
    public void ExecuteChunk(int chunkIndex, int chunkCount)
    {
        if (chunkCount <= 0 || (uint)chunkIndex >= (uint)_workers.Length || _interest == null)
        {
            return;
        }

        var scratch = _workers[chunkIndex];
        var start = (int)((long)chunkIndex * _tickSessionCount / chunkCount);
        var end = (int)((long)(chunkIndex + 1) * _tickSessionCount / chunkCount);

        for (var i = start; i < end; i++)
        {
            Assemble(i, scratch);
        }
    }

    /// <summary>How many worker scratches exist, and therefore how many ready lists a driver has to walk.</summary>
    public int WorkerCount => _workers.Length;

    /// <summary>
    /// Frames this runtime actually encoded, across every worker. AC-1's numerator.
    /// </summary>
    /// <remarks>
    /// Read against <see cref="FramesCopied"/>: 110 identical sessions on eight workers should encode eight times and copy 102, not encode 110. The counter
    /// is what makes "one encode per tick for all of them" an assertion rather than something inferred from a timing.
    /// </remarks>
    public long FramesEncoded
    {
        get
        {
            var total = 0L;
            for (var i = 0; i < _workers.Length; i++)
            {
                total += _workers[i]?.Shared.Encodes ?? 0;
            }

            return total;
        }
    }

    /// <summary>Frames produced by copying an encode the same worker had already done this tick.</summary>
    public long FramesCopied
    {
        get
        {
            var total = 0L;
            for (var i = 0; i < _workers.Length; i++)
            {
                total += _workers[i]?.Shared.Copies ?? 0;
            }

            return total;
        }
    }

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
    public void Dispose()
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
        _sendStates = null;
        _sendBlock?.Dispose();
        _sendBlock = null;

        for (var slot = 0; slot < _states.Length; slot++)
        {
            _states[slot]?.Known.Dispose();
            _states[slot] = null;
        }

        Pool.Dispose();
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
            if ((uint)slot >= (uint)_states.Length)
            {
                continue;
            }

            var state = BindSlot(slot, session);
            var send = SendStateOf(slot);

            // Silence first: a client that has stopped talking is gone whatever its skip run says, and 4001 tells its SDK to reconnect rather than to back off
            // as 1013 would.
            var heardFrom = send->PingTick;
            if (heardFrom > 0 && _tick - heardFrom > _silenceBoundTicks)
            {
                _sessions.RequestClose(session, SessionCloseReason.Unacknowledged, CloseCodes.NoAcknowledgement);
                Interlocked.Increment(ref _sessionsClosedSilent);
                continue;
            }

            var skipRun = send->SkipRun;
            if (SkipPolicy.Evaluate(skipRun, _options) == SkipVerdict.Close)
            {
                _sessions.RequestClose(session, SessionCloseReason.Lagging, CloseCodes.TryAgainLater);
                Interlocked.Increment(ref _sessionsClosedLagging);
                continue;
            }

            if (SkipPolicy.ShouldDegrade(skipRun, state.DegradeLevel, _options))
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
    /// <b>Called from the sweep, which walks every open session, rather than only from the interest partition.</b> A session bound to no profile — or to one
    /// whose observers reach nothing — never appears in a tick's session set, so binding it there left it with no state, and the sweep's generation check then
    /// skipped it: it could be neither degraded, nor closed for a skip run, nor closed for silence. It held its slot, its known-set and its ingress ring until
    /// the process ended, which is exactly the case the silence bound exists to catch.
    /// </remarks>
    private SessionFrameState BindSlot(int slot, SessionId session)
    {
        var state = _states[slot];
        if (state == null)
        {
            state = new SessionFrameState(new KnownSet($"Known-{slot}", _parent, _allocator));
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
        if ((uint)slot >= (uint)_states.Length)
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

    // ── One session's frame ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void Assemble(int index, FrameWorkerScratch scratch)
    {
        var session = _interest.SessionAt(index);
        var state = StateOf(session);
        if (state == null || state.Generation != session.Generation)
        {
            return;
        }

        var send = SendStateOf(session.Slot);

        // The two producer-side skips, before a slot is claimed so a skipped session costs neither a sequence nor a block. Both leave the known-set and the
        // baseline exactly as they were, which is what makes the next frame carry the union of everything the session missed (SUB-03).
        if (!SkipPolicy.ProducesOnTick(state.DegradeLevel, _tick))
        {
            send->NoteSkipped();
            NoteSkip(state);
            return;
        }

        if (SkipPolicy.AcknowledgementLag(send->ProducedTick, send->AckedTick) > _lagBoundTicks)
        {
            // The client has told us, through its PING, that it is further behind than a round trip and a ping period can explain. Producing for it would
            // encode bytes it will not reach before the next frame supersedes them.
            send->NoteSkipped();
            NoteSkip(state);
            return;
        }

        if (!send->TryBeginFrame(out var sequence, out var recycled))
        {
            // K frames are already outstanding: skip, never queue. Nothing here mutates the known-set or the baseline, which is what makes the next frame
            // this session does receive carry the union of everything it missed (SUB-03).
            NoteSkip(state);
            return;
        }

        scratch.BeginSession(_plans.Length);

        var flags = TickFlags.None;
        if (state.PendingReset)
        {
            state.Known.Clear();
            state.ViewComplete = false;
            state.DeferredEnters = 0;
            flags |= TickFlags.Reset;
        }

        var stamp = (ushort)_tick;
        // ── Fast path (12 § 6, C-1) ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        // A session that was produced for last tick and holds a complete view needs exactly this tick's changes, which S1 has already identified. Walking
        // only those turns the per-session cost from O(entities watched) into O(entities changed) — measured at 4-15 % of the watched set.
        //
        // It is NOT taken when the session is behind: SUB-03 requires every group with groupTick > baseline, and a single tick's change set does not contain
        // them. That session takes the walk below, unchanged, which is the invariant's backstop and the reason this is an optimisation rather than a new
        // contract. `KnownFlags.NeedsFull` has no setter in Phase 1; whoever adds one (SUB-11's resend) must gate this too, because an unchanged entity
        // carrying that flag would not be visited.
        var changedOnly = ChangedOnlyGatherEnabled && state.Baseline == _tick - 1 && state.ViewComplete && !state.PendingReset && !state.ForceFullGather;
        state.ForceFullGather = false;
        var touched = Gather(index, state, scratch, stamp, changedOnly, out var staleLeaves, out var pending, out var hits);

        // A reused identity leaves now and enters next frame, and next frame it will be unchanged — so the frame after a stale leave has to look at
        // everything, not only at what moved.
        if (staleLeaves > 0)
        {
            state.ForceFullGather = true;
        }

        if (changedOnly)
        {
            Interlocked.Increment(ref _changedOnlyGathers);

            // The one thing the fast path cannot observe is a LEAVE, because it never looks at the slots that did not change and therefore stamps nothing.
            // Sweep's own condition decides it instead, computed from run popcounts: if the hits reach at least as many entries as the table holds, once the
            // enters it is about to gain and the stale identities it is about to drop are taken out, then every known entry was reached and nothing left.
            // When that cannot be shown the walk is redone in full — rare, and cheaper than being wrong, whose failure mode is a silently diverged client.
            if (hits - scratch.EnterCandidates.Length - staleLeaves < state.Known.KnownCount)
            {
                scratch.BeginSession(_plans.Length);
                changedOnly = false;
                Interlocked.Increment(ref _unprovenGathers);
                touched = Gather(index, state, scratch, stamp, false, out staleLeaves, out pending, out hits);
            }
        }
        var deferred = SelectEnters(state, scratch);
        if (!changedOnly)
        {
            Interlocked.Increment(ref _fullGathers);
            Sweep(state, scratch, stamp, touched + staleLeaves);
        }

        var records = SortAndCount(scratch);

        // The view is complete when nothing the session's interest reached is still owed to it — neither an enter the budget deferred nor a hit the engine
        // could not describe yet. The second half is not pedantry: a cluster that gained its replication block this tick is projected from the next one, so a
        // brand-new session's first tick legitimately has hits and no records, and calling that a complete view would tell the client its world was empty.
        var owed = deferred + pending;
        var completed = owed == 0 && !state.ViewComplete;

        // The STATS block is a reason to produce a frame in its own right (P1-16). Without this the block would ride only on ticks that happened to carry an
        // entity record, so a quiet world — the very case a statistics HUD is watching — would receive one every few seconds or never.
        // ACQUIRE, once, into a local: this runs on a worker and the encoder was published from the thread that called Start, which is exactly the ordering
        // the field's property exists for. Reading it once also means the frame is built against one answer rather than two.
        var stats = Stats;
        var emitStats = stats != null && stats.IsEmissionTick && (send->Caps & Capabilities.Stats) != 0;

        if (records == 0 && (flags & TickFlags.Reset) == 0 && !completed && !emitStats)
        {
            // Nothing to say. The frame slot is given back rather than spent on a header, and the keepalive that a silent session still owes its client is
            // the send pump's business (P1-14b), not the assembler's.
            send->AbandonFrame(sequence);
            ReturnIfValid(recycled);
            NoteSkip(state);
            return;
        }

        if (completed)
        {
            state.ViewComplete = true;
        }

        if (state.ViewComplete)
        {
            flags |= TickFlags.ViewComplete;
        }

        // ENCODE — or copy an encode this worker has already done (P1-15). "Shareable" means another session on this worker would have produced these same
        // bytes: the same profile, the same baseline, a complete view and nothing owed. Everything the encoder reads is then identical for both, because the
        // records come from the same hits against the same known-set content and the emit rule compares against the same baseline number.
        //
        // A STATS-carrying frame is never shareable: its session segment is this session's bytes/s, skipped frames and dropped commands, which is the one
        // part of a frame that differs between two otherwise identical sessions.
        //
        // What is NOT shared is the gather above or the commit below. A shared frame tells N clients about the same enters and leaves, and every one of
        // those N known-sets has to learn about them, or the next frame's emit rule is wrong for all but the first.
        var shareable = owed == 0 && !emitStats && !state.PendingReset && state.ViewComplete && state.Profile != null;
        var shareKey = shareable ? scratch.ShareKey(_plans.Length) : default;

        int length;
        Span<byte> buffer;
        if (shareable && scratch.Shared.TryReuse(state.Profile, state.Baseline, _tick, flags, shareKey, out var cached))
        {
            length = cached.Length;
            buffer = scratch.Bytes(length);
            cached.CopyTo(buffer);
        }
        else
        {
            var bound = UpperBound(scratch) + (emitStats ? stats.MaxBlockBytes : 0);
            buffer = scratch.Bytes(bound);
            var writer = new WireWriter(buffer);
            EntitiesEncoder.WriteHeader(ref writer, (uint)_tick, flags);
            for (var a = 0; a < _plans.Length; a++)
            {
                if (scratch.Count(a, FrameListKind.Enter) == 0 && scratch.Count(a, FrameListKind.Segment) == 0 && scratch.Count(a, FrameListKind.State) == 0
                    && scratch.Count(a, FrameListKind.Leave) == 0)
                {
                    continue;
                }

                EntitiesEncoder.WriteEntities(ref writer, _encodePlans[a], scratch.List(a, FrameListKind.Enter), scratch.List(a, FrameListKind.Segment),
                    scratch.List(a, FrameListKind.State), scratch.List(a, FrameListKind.Leave));
            }

            // After the ENTITIES blocks (03 § 3 lists the block types, not an order, and a client decodes by type) and before the length is taken.
            if (emitStats)
            {
                stats.WriteBlock(ref writer, session, state, _tick);
            }

            length = writer.Position;
            scratch.Shared.NoteEncode();
            if (shareable)
            {
                scratch.Shared.Store(state.Profile, state.Baseline, _tick, flags, shareKey, buffer[..length]);
            }
        }

        if (length > _maxFrameBytes)
        {
            Interlocked.Increment(ref _oversizeSkips);
            send->AbandonFrame(sequence);
            ReturnIfValid(recycled);
            NoteSkip(state);
            return;
        }

        var block = recycled;
        if (!Pool.TryRentOrKeep(length, ref block, out var previous))
        {
            // The pool's budget binds. The block handed over by TryBeginFrame is this caller's from that moment, so an abandoned encode owes it back.
            ReturnIfValid(block);
            send->AbandonFrame(sequence);
            NoteSkip(state);
            return;
        }

        ReturnIfValid(previous);
        buffer[..length].CopyTo(new Span<byte>(block.Bytes, block.Capacity));

        // PUBLISH — every byte of the frame is written above this line, and the release inside PublishFrame is what makes them visible (SUB-04).
        send->PublishFrame(sequence, block, length, _tick);

        // COMMIT, and only now. The known-set and the baseline move because a frame that carries them exists; had anything above failed, the session would
        // have been left exactly as it was and its next frame would carry the same union (SUB-03).
        Commit(state, scratch, stamp);
        state.Baseline = _tick;

        // The window closes BEFORE this frame is counted, and the order is the whole of it: the block was encoded from the byte total as it stood on entry,
        // so a mark taken after the addition would leave this frame's own bytes in neither window — reported by the block it rode on, because they were not
        // yet counted, and excluded from the next, because the mark had swallowed them. Every second's largest frame would go missing from the rate.
        if (emitStats)
        {
            StatsEncoder.NoteBlockPublished(state, _tick);
        }

        state.BytesPublished += length;

        // Cleared HERE and not where the flag was read, so a reset that could not be published is still owed. The known-set was emptied above either way,
        // which is the right pairing: a client that never received the RESET still holds a store this session can no longer describe, and the next frame it
        // does receive has to tell it to clear.
        state.PendingReset = false;
        state.DeferredEnters = owed;
        state.FramesProduced++;
        state.FramesSinceDegrade++;

        // The send pump learns about this frame from here: worker-local, no atomic, and drained by the tick driver after the flush.
        scratch.AddReady(session);

        Interlocked.Increment(ref _framesProduced);
        Interlocked.Add(ref _bytesEncoded, length);
        Interlocked.Add(ref _recordsEncoded, records);
        if (deferred > 0)
        {
            Interlocked.Add(ref _entersDeferred, deferred);
        }
    }

    /// <summary>
    /// Walks the session's hits: a known entity contributes the groups that changed after its baseline, an unknown one an enter candidate, and one whose
    /// generation moved on a leave.
    /// </summary>
    /// <returns>How many known-and-current entries the hits reached, which is what lets the leave sweep be skipped when nothing left.</returns>
    private int Gather(int index, SessionFrameState state, FrameWorkerScratch scratch, ushort stamp, bool changedOnly, out int staleLeaves, out int pending,
        out int hits)
    {
        var known = state.Known;
        var baseline = (uint)state.Baseline;
        var runs = _interest.HitsOf(index);
        var touched = 0;
        staleLeaves = 0;
        pending = 0;
        hits = 0;

        for (var r = 0; r < runs.Length; r++)
        {
            ref readonly var run = ref runs[r];
            if (run.Block == 0 || (run.Flags & InterestRunFlags.NoBlock) != 0)
            {
                // The cluster had no replication block when its hits were recorded; the blocks step created one and the next tick's interest marks it. There
                // is nothing to read here, and inventing an enter from the columns would be the per-session re-encode this design exists to avoid. The hits
                // are counted as owed, so the frame does not claim a complete view over entities it has not described.
                pending += run.HitCount;
                continue;
            }

            var archetype = run.ArchetypeIndex;
            if (archetype >= _plans.Length)
            {
                continue;
            }

            var plan = _encodePlans[archetype];
            var slots = run.Slots;

            // The hit count comes from a popcount per RUN, not from counting the slots we visit, because the fast path deliberately visits only some of
            // them and the "did anything leave" proof below needs the full number.
            hits += BitOperations.PopCount(slots);

            if (changedOnly)
            {
                // S1 published which slots produced a record this tick (ReplicationBlockHeader.ChangedSlots). Everything else in this block is a known
                // entity whose every group compared equal, so it has nothing to say to a session that already has last tick's frame. A block whose stamp is
                // not this tick is not trusted — it is visited in full, which is the safe direction.
                var header = (ReplicationBlockHeader*)run.Block;
                if (header->ChangedTick == (uint)_tick)
                {
                    slots &= header->ChangedSlots;
                }
            }

            while (slots != 0)
            {
                var slot = BitOperations.TrailingZeroCount(slots);
                slots &= slots - 1;

                var hot = (ReplicationHotEntry*)plan.Hot(run.Block, slot);
                var netId = hot->NetId;
                if (netId == NetIdAllocator.NoNetId)
                {
                    // S1 could not name the entity this tick — its worker's identity lease ran dry, or the block was rented after the mask was read. It is
                    // still watched, so the next tick names it and this session sees it enter then; until then it is owed.
                    pending++;
                    continue;
                }

                var probe = known.Probe(netId, hot->Generation, out var entry);
                if (probe == KnownProbe.Unknown)
                {
                    scratch.AddEnterCandidate(new FrameRecord
                    {
                        NetId = netId,
                        Rank = Rank(state, plan, run.Block, slot),
                        Block = run.Block,
                        Slot = (ushort)slot,
                        Archetype = (ushort)archetype,
                    });
                    continue;
                }

                if (probe == KnownProbe.Stale)
                {
                    // The identity was reissued while this session was not being sent (02 § 5). The leave goes out now and the reuse enters in the session's
                    // NEXT frame, so no frame ever carries both for one netId (03 § 10).
                    scratch.Add(archetype, FrameListKind.Leave, new FrameRecord { NetId = netId, Archetype = (ushort)archetype });
                    staleLeaves++;
                    continue;
                }

                entry->SeenStamp = stamp;
                touched++;

                if (plan.Moving && hot->GroupTicks[plan.MotionTickSlot] > baseline)
                {
                    scratch.Add(archetype, FrameListKind.Segment, new FrameRecord
                    {
                        NetId = netId,
                        Block = run.Block,
                        Slot = (ushort)slot,
                        Archetype = (ushort)archetype,
                    });
                }

                var mask = 0;
                var full = (entry->Flags & KnownFlags.NeedsFull) != 0;
                for (var g = 0; g < plan.GroupCount; g++)
                {
                    if (full || hot->GroupTicks[plan.GroupTickSlot[g]] > baseline)
                    {
                        mask |= 1 << g;
                    }
                }

                if (mask != 0)
                {
                    scratch.Add(archetype, FrameListKind.State, new FrameRecord
                    {
                        NetId = netId,
                        Block = run.Block,
                        Slot = (ushort)slot,
                        GroupMask = (byte)mask,
                        Archetype = (ushort)archetype,
                    });
                }
            }
        }

        return touched;
    }

    /// <summary>
    /// Applies the per-frame enter budget: the candidates it allows are moved into their archetypes' enter lists, and the rest wait for a later frame.
    /// </summary>
    /// <returns>How many candidates were deferred.</returns>
    private int SelectEnters(SessionFrameState state, FrameWorkerScratch scratch)
    {
        var candidates = scratch.EnterCandidates;
        var budget = Math.Max(1, _options.EnterBudgetPerFrame);
        var take = candidates.Length;
        if (take > budget)
        {
            // Only when the budget actually binds. Ranking a list that fits costs a pass for an order nothing would use, and the fill of a small view is
            // exactly the case where the budget never binds.
            scratch.SortCandidatesByRank();
            candidates = scratch.EnterCandidates;
            take = budget;
        }

        for (var i = 0; i < take; i++)
        {
            scratch.Add(candidates[i].Archetype, FrameListKind.Enter, in candidates[i]);
        }

        return candidates.Length - take;
    }

    /// <summary>Finds the entities this session knows that its hits did not reach: its leaves.</summary>
    private static void Sweep(SessionFrameState state, FrameWorkerScratch scratch, ushort stamp, int accountedFor)
    {
        var known = state.Known;
        if (accountedFor >= known.KnownCount)
        {
            // Every entry the table holds was reached by a hit or is already leaving, so nothing can be missing. The steady state takes this branch, which
            // is what keeps a 10 000-entity view from walking its whole table every tick to discover that nobody left.
            return;
        }

        var enumerator = known.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var entry = enumerator.Current;
            if (entry->SeenStamp == stamp)
            {
                continue;
            }

            scratch.Add(entry->Archetype, FrameListKind.Leave, new FrameRecord { NetId = entry->NetId, Archetype = entry->Archetype });
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

    /// <summary>
    /// Applies to the known-set what the published frame told the client: the enters it carried are now known, and the leaves it carried are forgotten.
    /// </summary>
    private void Commit(SessionFrameState state, FrameWorkerScratch scratch, ushort stamp)
    {
        var known = state.Known;
        for (var a = 0; a < _plans.Length; a++)
        {
            var plan = _encodePlans[a];
            var enters = scratch.List(a, FrameListKind.Enter);
            for (var i = 0; i < enters.Length; i++)
            {
                ref readonly var record = ref enters[i];
                var hot = (ReplicationHotEntry*)plan.Hot(record.Block, record.Slot);

                // One source in Phase 1 — a World observer — so the bit is 1. The mask exists because an entity reached through several sources is known
                // once and leaves when the last of them stops reaching it (02 § 5); Phase 2's observers are what set the other seven.
                if (known.AddSource(record.NetId, hot->Generation, 1, stamp, out var entry) != KnownAdd.Stale)
                {
                    entry->Archetype = (ushort)a;
                    entry->Flags &= ~KnownFlags.NeedsFull;
                }
            }

            // A state record that carried every group is what SUB-11's "resend everything" asks for, so the flag that asked for it is cleared by the frame
            // that answered it — not by the gather, which runs before anything is known to have been sent.
            var states = scratch.List(a, FrameListKind.State);
            for (var i = 0; i < states.Length; i++)
            {
                if (known.Probe(states[i].NetId, 0, out var entry) != KnownProbe.Unknown)
                {
                    entry->Flags &= ~KnownFlags.NeedsFull;
                }
            }

            var leaves = scratch.List(a, FrameListKind.Leave);
            for (var i = 0; i < leaves.Length; i++)
            {
                known.Remove(leaves[i].NetId);
            }
        }
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

    /// <summary>The enter budget's ranking key: the code-space distance from the session's focus, saturating, or zero when it declared none.</summary>
    private static uint Rank(SessionFrameState state, ArchetypeEncodePlan plan, nint block, int slot)
    {
        if (!state.HasFocus || !plan.HasPosition || plan.PositionAxisBytes == 0)
        {
            return 0;
        }

        // The quantized p0 an enter record would carry, read from wherever this archetype keeps it: the hot entry's segment for a mover, the cold entry's
        // enter cache for a static position. Ranking reads the same bytes the wire will, so a candidate cannot be ranked against a position it is not sent.
        var source = plan.Moving
            ? plan.Hot(block, slot) + plan.Layout.SegmentOffsetInHotEntry
            : plan.Cold(block, slot) + plan.Layout.EnterPositionOffsetInColdEntry;

        var bytes = plan.PositionAxisBytes;
        var x = ReadCode(source, bytes);
        var y = ReadCode(source + bytes, bytes);
        var dx = (double)x - state.FocusX;
        var dy = (double)y - state.FocusY;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        return distance >= uint.MaxValue ? uint.MaxValue : (uint)distance;
    }

    private static uint ReadCode(byte* at, int bytes)
    {
        var code = 0u;
        for (var i = 0; i < bytes; i++)
        {
            code |= (uint)at[i] << (8 * i);
        }

        return code;
    }

    private void NoteSkip(SessionFrameState state)
    {
        if (BaselineAdvancesOnSkipForTest)
        {
            // The mutant: a baseline that moves on a tick whose frame was never produced. Every group stamped at or before it is then invisible to every
            // later frame, and the session diverges permanently — which is exactly what SUB-03's verifier has to catch.
            state.Baseline = _tick;
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
