using System.Diagnostics;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
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
/// <b>Six atomics per session per tick over consecutive <c>long</c> fields is two cache lines ping-ponging between every worker for the whole stage</b>,
/// and none of the numbers is read until the tick has joined. Accumulating per CHUNK turns two hundred sessions' worth of contended increments into one
/// flush per worker — the same move <c>HitArena.Note</c> makes, and for the same reason.
/// </remarks>
internal struct FrameCounters
{
    /// <summary>Frames published.</summary>
    public long FramesProduced;

    /// <summary>Frames the session was skipped.</summary>
    public long FramesSkipped;

    /// <summary>Bytes encoded.</summary>
    public long BytesEncoded;

    /// <summary>Records encoded.</summary>
    public long RecordsEncoded;

    /// <summary>Enter candidates the budget deferred.</summary>
    public long EntersDeferred;

    /// <summary>ENTER records the published frames carried.</summary>
    public long EntersEmitted;

    /// <summary>LEAVE records the published frames carried.</summary>
    public long LeavesEmitted;

    /// <summary>Identities the interest pass stopped reaching, whether or not a leave was emitted for them.</summary>
    public long LeavesConsidered;

    /// <summary>Leaves emitted because the netId had been reissued to another entity (SUB-06).</summary>
    public long LeavesStale;

    /// <summary>Leaves emitted by the known-set sweep: an entry this tick's hits did not reach.</summary>
    public long LeavesSwept;

    /// <summary>Known-set size at the moment each frame committed, summed. Divide by <see cref="FramesProduced"/> for the mean.</summary>
    public long KnownTotal;

    /// <summary>Outstanding slot debt at the moment each frame committed, summed.</summary>
    public long OwedTotal;

    /// <summary>Frames refused for exceeding the ceiling.</summary>
    public long OversizeSkips;

    /// <summary>Gathers that read only what entered and what changed.</summary>
    public long TemporalGathers;

    /// <summary>Gathers that read every slot of the session's interest.</summary>
    public long FullGathers;

    /// <summary>Hit slots read.</summary>
    public long SlotsRead;

    /// <summary>Hit slots the reduction skipped.</summary>
    public long SlotsSkipped;

    // ── Phase timings, in Stopwatch ticks ───────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Why they live here and not in a profiler: the frame stage is the subsystem's largest term and `FrameAssembler.Assemble` is one method, so a sampling
    // profiler attributes almost all of it to `Assemble` itself — every callee that matters is inlined in Release. A stopwatch read is ~20 ns against
    // phases measured in microseconds, it accumulates on the stack beside the counters already here, and it costs one flush per worker per tick. It is
    // what turns "the frame stage is 5 ms" into a statement about WHICH PART of it is 5 ms, which is the only form of that sentence worth acting on.
    //
    // They are collected only while `FrameAssembler.PhaseTimingEnabled` is set, so the ordinary path pays one static bool read per phase boundary.

    /// <summary>Walking the session's runs and classifying slots.</summary>
    public long GatherTicks;

    /// <summary>Ranking enter candidates and applying the per-frame budget.</summary>
    public long SelectTicks;

    /// <summary>The known-set sweep, on the paths that still run one.</summary>
    public long SweepTicks;

    /// <summary>Sorting records by netId and counting them.</summary>
    public long SortTicks;

    /// <summary>Encoding the frame, or copying a shared encode.</summary>
    public long EncodeTicks;

    /// <summary>Publishing to the session's hand-off, and committing the known-set and the view.</summary>
    public long PublishTicks;

    // ── Why a gather went full ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Four conditions force the full walk and they call for completely different fixes, so a single "full gathers" count cannot be acted on. Counted in
    // priority order, each attributed to the FIRST condition that fired, so the four sum to the full-gather total.

    /// <summary>Full because the session is being reset.</summary>
    public long FullReset;

    /// <summary>Full because the previous frame left something owed, or a slot reuse condemned this one.</summary>
    public long FullForced;

    /// <summary>Full because the session has never completed its view.</summary>
    public long FullIncomplete;

    /// <summary>Full because the session is not exactly one tick behind — it was skipped, or it is new.</summary>
    public long FullBehind;

    /// <summary>Visited slots that had just entered the session's view.</summary>
    public long VisitEntered;

    /// <summary>Visited slots the block's change mask named.</summary>
    public long VisitChanged;

    /// <summary>Visited slots that were there only because an earlier frame owed them.</summary>
    public long VisitOwed;

    /// <summary>Retained slots read in full because the block's change mask named another tick.</summary>
    public long StaleMaskSlots;

    /// <summary>Runs whose block was not projected on the tick that read it.</summary>
    public long StaleMaskRuns;

    /// <summary>Interest runs the gather walked, which is the per-cluster fixed cost the per-slot cost is measured against.</summary>
    public long RunsWalked;

    /// <summary>Of those, the ones that turned out to have nothing to say: no enter, nothing the change mask named, nothing owed.</summary>
    public long RunsEmpty;


    /// <summary>Slots owed to the next frame because a stale-generation reuse landed in them.</summary>
    /// <remarks>
    /// Counted separately from the deferred-enter debt because it is the half that is hard to reach and easy to lose: the arriving entity is in no change
    /// mask, and if the slot is one the view has never held there is no stored identity to notice the swap either. A fixture asserting the carry is
    /// correct is worthless unless this is non-zero in it.
    /// </remarks>
    public long OwedStale;
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
    /// <summary>Distance LOD: far updates withheld by this worker's current gather, added to the shared counter once per gather.</summary>
    internal long Deferred;

    // A sparse session's synthetic runs — one per held cluster whose content changed this tick and that no interest run already covered — and the stamps that
    // say which of its view entries a real run covered. Per worker and reused, so a session's gather allocates nothing.
    internal InterestRun[] SparseRuns = new InterestRun[256];
    internal int[] CoveredStamp = new int[256];
    internal int CoveredEpoch;
    internal long SyntheticRuns;

    private struct RecordList
    {
        public FrameRecord* Items;
        public int Count;
        public int Capacity;
    }

    private struct SharedList
    {
        public SharedRunBytes* Items;
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

    // ── Runs this session only REFERENCES (17 § 18) ─────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Two lists per archetype — segments and states — parallel to the four record sub-lists beside them. They hold no bytes: a cluster's run was encoded
    // once by the projection worker that owned the block, and what is collected here is where those bytes are. The frame copies them in verbatim.
    private SharedList[] _shared = [];
    private int _sharedBytes;
    private int _sharedRecords;
    private bool _disposed;

    /// <summary>This worker's cache of the last frame it encoded, so identical sessions copy rather than re-encode (P1-15).</summary>
    public SharedFrameSet Shared { get; } = new();

    /// <summary>This worker's tables for the temporal gather: last tick's runs by cluster, and the identities this tick read (15 § 3.2).</summary>
    public FrameIdentityScratch Temporal { get; } = new();

    /// <summary>Native bytes this scratch holds, for the owner's resource accounting.</summary>
    public long EstimatedBytes
    {
        get
        {
            var bytes = _byteCapacity + ((long)_sortCapacity * sizeof(FrameRecord)) + ((long)_enterCandidates.Capacity * sizeof(FrameRecord));
            for (var i = 0; i < _lists.Length; i++)
            {
                bytes += (long)_lists[i].Capacity * sizeof(FrameRecord);
            }

            for (var i = 0; i < _shared.Length; i++)
            {
                bytes += (long)_shared[i].Capacity * sizeof(SharedRunBytes);
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

        if (_shared.Length < archetypes * 2)
        {
            Array.Resize(ref _shared, archetypes * 2);
        }

        for (var i = 0; i < _shared.Length; i++)
        {
            _shared[i].Count = 0;
        }

        _sharedBytes = 0;
        _sharedRecords = 0;
        _enterCandidates.Count = 0;
    }

    /// <summary>Bytes of shared run this session will copy in, for the frame buffer's upper bound.</summary>
    public int SharedBytes => _sharedBytes;

    /// <summary>Records those runs carry, so a frame that owns none of its own still knows it has something to say.</summary>
    public int SharedRecords => _sharedRecords;

    /// <summary>One archetype's shared runs of one kind, as the encoder reads them.</summary>
    /// <param name="archetype">The archetype's plan index.</param>
    /// <param name="segments"><see langword="true"/> for the segment runs, <see langword="false"/> for the state runs.</param>
    /// <returns>The runs.</returns>
    public ReadOnlySpan<SharedRunBytes> SharedRuns(int archetype, bool segments)
    {
        ref var list = ref _shared[(archetype * 2) + (segments ? 0 : 1)];
        return new ReadOnlySpan<SharedRunBytes>(list.Items, list.Count);
    }

    /// <summary>Records a run this frame will reference rather than encode.</summary>
    /// <param name="archetype">The archetype's plan index.</param>
    /// <param name="segments">Which sub-list the run belongs to.</param>
    /// <param name="bytes">The run's first byte — its <c>varu</c> record count.</param>
    /// <param name="length">The run's length.</param>
    /// <param name="records">How many records it carries.</param>
    public void AddSharedRun(int archetype, bool segments, byte* bytes, int length, int records)
    {
        ref var list = ref _shared[(archetype * 2) + (segments ? 0 : 1)];
        if (list.Count == list.Capacity)
        {
            var capacity = list.Capacity == 0 ? 16 : list.Capacity * 2;
            list.Items = (SharedRunBytes*)NativeMemory.Realloc(list.Items, (nuint)capacity * (nuint)sizeof(SharedRunBytes));
            list.Capacity = capacity;
        }

        list.Items[list.Count++] = new SharedRunBytes { Bytes = bytes, Length = length };
        _sharedBytes += length;
        _sharedRecords += records;
    }

    /// <summary>
    /// PROTOTYPE (push): after the sort, drops every update the frame's enter for the same identity already covers, and merges repeated updates of one
    /// identity (their group masks OR'd) — a deferred far update and this tick's can name the same entity.
    /// </summary>
    /// <param name="archetype">The archetype's plan index.</param>
    public void DedupeUpdates(int archetype)
    {
        ref var enters = ref _lists[(archetype * 4) + (int)FrameListKind.Enter];
        foreach (var kind in (ReadOnlySpan<FrameListKind>)[FrameListKind.Segment, FrameListKind.State])
        {
            ref var list = ref _lists[(archetype * 4) + (int)kind];
            var write = 0;
            var e = 0;
            for (var r = 0; r < list.Count; r++)
            {
                var id = list.Items[r].NetId;
                while (e < enters.Count && enters.Items[e].NetId < id)
                {
                    e++;
                }

                if (e < enters.Count && enters.Items[e].NetId == id)
                {
                    continue;
                }

                if (write > 0 && list.Items[write - 1].NetId == id)
                {
                    list.Items[write - 1].GroupMask |= list.Items[r].GroupMask;
                    continue;
                }

                list.Items[write++] = list.Items[r];
            }

            list.Count = write;
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
        Temporal.Dispose();
        for (var i = 0; i < _lists.Length; i++)
        {
            NativeMemory.Free(_lists[i].Items);
        }

        _lists = [];
        for (var i = 0; i < _shared.Length; i++)
        {
            NativeMemory.Free(_shared[i].Items);
        }

        _shared = [];
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

    /// <summary>
    /// Slots the previous frame's gather visited, or <c>-1</c> when this session has never produced one.
    /// </summary>
    /// <remarks>
    /// <b>The frame stage's cost predictor, and the only one available that is actually proportional to the cost.</b> The gather is ~two thirds of the
    /// stage and its time is per visited slot; the view size is not a substitute, because a large view with nothing changed is cheap. A session in enter
    /// backlog visits an order of magnitude more slots than a settled one and stays that way from tick to tick, which is what makes last tick's count a
    /// usable prediction of this tick's.
    /// </remarks>
    public int LastVisited { get; set; } = -1;

    /// <summary>The profile the session was bound to when its last frame was built. A change is a <c>RESET</c>.</summary>
    public string Profile { get; set; }

    /// <summary>Whether the next frame must carry <c>RESET</c> and refill the view from nothing.</summary>
    public bool PendingReset { get; set; }

    /// <summary>Whether the initial fill under the enter budget has completed — the <c>VIEW_COMPLETE</c> flag.</summary>
    public bool ViewComplete { get; set; }

    /// <summary>
    /// Whether the last published frame left hits undescribed because their cluster had no block yet. Such hits are re-offered by the next tick's interest
    /// recomputing the cluster — which a member standing still does not do for a cluster whose structure did not change — so they bar that shortcut.
    /// </summary>
    public bool LeftPendingHits { get; set; }

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
        ForceFullGather = false;
        LastVisited = -1;
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
internal sealed unsafe partial class FrameAssembler : IDisposable
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
    private readonly int _closeBoundTicks;
    private readonly int _degradeBoundTicks;

    private PinnedMemoryBlock _sendBlock;
    private SessionSendState* _sendStates;
    private FrameWorkerScratch[] _workers = [];
    private InterestPass _interest;
    private long _tick;
    private int _tickSessionCount;
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

    /// <summary>The next session a worker should take, for the dynamic schedule. Reset by the prologue.</summary>
    private int _sessionCursor;

    // ── Runs encoded once per cluster (17 § 18) ─────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // The archetype states are held for two things only: the table that says which clusters published a run this tick, and the arena the bytes sit in.
    // Null until the runtime attaches them, which it does only when the option is on — so the whole feature is one null check on the gather's path.
    private ArchetypeReplicationState[] _replication;
    private readonly bool _verifySharedRuns;
    private long _sharedRunsUsed;
    private long _sharedRecordsUsed;
    private long _sharedRunsRefused;
    private long _shareMissGated;
    private long _shareMissNoRun;
    private long _shareMissNotReached;

    // ── Longest-job-first, over the dynamic cursor (17 § 17) ────────────────────────────────────────────────────────────────────────────────────────
    //
    // Taking sessions in arbitrary order leaves the stage's tail equal to whatever the LAST session taken happens to cost, and the expensive ones — a
    // session working through an enter backlog — cost an order of magnitude more than the median. Handing the expensive ones out FIRST means the tail is
    // a cheap session instead, which is the classic list-scheduling result and needs only an ordering, not an estimate of the makespan.
    //
    // The cost predictor is the previous frame's visited-slot count, because that is what the gather's time is proportional to and because a session in
    // backlog is in backlog again next tick. A session with no history sorts first, so its cost is discovered rather than assumed.
    private int[] _order = [];
    private int[] _orderKeys = [];

    private long _framesProduced;
    private long _framesSkipped;
    private long _bytesEncoded;
    private long _recordsEncoded;
    private long _entersDeferred;
    private long _entersEmitted;
    private long _leavesEmitted;
    private long _leavesConsidered;
    private long _leavesStale;
    private long _leavesSwept;
    private long _knownTotal;
    private long _owedTotal;
    private long _oversizeSkips;
    private long _sessionsDegraded;
    private long _sessionsClosedLagging;
    private long _sessionsClosedSilent;
    private int _longestSkipRun;
    private long _temporalGathers;
    private long _slotsRead;
    private long _slotsSkipped;
    private long _fullGathers;
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
    /// <param name="views">Each session's interest membership, shared with the interest pass, or <see langword="null"/> for Phase 1's shape.</param>
    public FrameAssembler(string id, IResource parent, IMemoryAllocator allocator, SubscriptionsOptions options, CompiledProjectionPlan[] plans,
        Catalog catalog, SessionTable sessions, uint tickPeriodUs, SessionViewStore views = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(sessions);

        _options = options;
        _verifySharedRuns = options.VerifySharedRuns;
        _plans = plans;
        _sessions = sessions;
        _views = views;
        _allocator = allocator;
        _parent = parent;
        _states = new SessionFrameState[options.MaxSessions];
        _encodePlans = BuildEncodePlans(plans, catalog);
        _lagBoundTicks = SkipPolicy.LagBoundTicks(options, tickPeriodUs);
        _silenceBoundTicks = SkipPolicy.SilenceBoundTicks(options, tickPeriodUs);

        // Converted once, here, beside the other two. The policy takes tick counts and never the options, so a duration cannot be read as a tick count by
        // a caller that does not know the tick rate - which is what the old bound was.
        _closeBoundTicks = SkipPolicy.CloseBoundTicks(options, tickPeriodUs);
        _degradeBoundTicks = SkipPolicy.DegradeBoundTicks(_closeBoundTicks);

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

    /// <summary>
    /// Attaches the archetype replication states, so a frame can reference a run their projection encoded once (17 § 18).
    /// </summary>
    /// <param name="states">One per plan, in plan order, or <see langword="null"/> to leave the feature off.</param>
    public void AttachReplication(ArchetypeReplicationState[] states) => _replication = states;

    /// <summary>
    /// The replication states whose changed-block tables sparse sessions read — independent of <see cref="AttachReplication"/>, which also enables encode
    /// sharing.
    /// </summary>
    private ArchetypeReplicationState[] _changeStates;

    /// <summary>Attaches the states whose changed-block tables sparse sessions read.</summary>
    /// <param name="states">The replication states.</param>
    public void AttachChangeStates(ArchetypeReplicationState[] states) => _changeStates = states;

    /// <summary>Runs referenced rather than encoded, the records they carried, and the clusters that offered one and could not be shared.</summary>
    public (long Runs, long Records, long Refused) SharedRunUse =>
        (Volatile.Read(ref _sharedRunsUsed), Volatile.Read(ref _sharedRecordsUsed), Volatile.Read(ref _sharedRunsRefused));

    /// <summary>
    /// Why a run with something to say did not reference a shared one: the session was not in a position to (<c>Gated</c>), the cluster published none
    /// (<c>NoRun</c>), or the run named a slot the session does not reach (<c>NotReached</c>).
    /// </summary>
    /// <remarks>
    /// <b>It is the breakdown, not the total, that says what to do next</b>, and it has already changed the design twice: <c>NoRun</c> dominating pointed
    /// at the projection, and <c>Gated</c> dominating pointed at a per-session precondition that turned out to be unnecessary. Zero unless the option is on.
    /// </remarks>
    public (long Gated, long NoRun, long NotReached) SharedRunMisses =>
        (Volatile.Read(ref _shareMissGated), Volatile.Read(ref _shareMissNoRun), Volatile.Read(ref _shareMissNotReached));

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

    /// <summary>ENTER and LEAVE records published since start, beside the enters the budget deferred.</summary>
    /// <remarks>
    /// <b>The ratio is what separates a backlog from churn</b>, and the two want opposite fixes. A session filling a view it has never completed emits
    /// enters and almost no leaves: the queue is draining, slowly, and a bigger budget drains it faster. A session whose entities cross its interest
    /// boundary and cross back emits the two in equal numbers, and a bigger budget spends more wire on the same entities arriving again. Read beside the
    /// world's true arrival rate: enters far above it, with leaves to match, is churn whatever the owed count says.
    /// </remarks>
    public (long Entered, long Left, long Deferred) EnterFlow =>
        (Volatile.Read(ref _entersEmitted), Volatile.Read(ref _leavesEmitted), Volatile.Read(ref _entersDeferred));

    /// <summary>Why the leaves were sent, split by the three paths that emit one, beside the identities the interest pass merely stopped reaching.</summary>
    /// <remarks>
    /// <para>
    /// <b>The three causes want three different fixes, and the split says which one is in front of you.</b> <c>Interest</c> is geometry: the observer no
    /// longer reaches the entity. <c>Stale</c> is identity: the netId was reissued to something else, which every session holding it must be told about,
    /// so a world that recycles identities quickly pays a leave and an enter per recycle per watching session whatever its entities are doing.
    /// <c>Swept</c> is the non-incremental path's backstop and is near zero wherever the interest pass produces a difference.
    /// </para>
    /// <para>
    /// <b><c>Considered</c> against <c>Interest</c> is the suppression rate</b>, which measures something different again: an identity dropped from one
    /// cluster but read this tick in another has MOVED inside the view rather than left it. A high suppression rate is cluster migration; a low one is
    /// entities genuinely crossing the observer's boundary.
    /// </para>
    /// </remarks>
    public (long Considered, long Interest, long Stale, long Swept) LeaveCauses
    {
        get
        {
            var stale = Volatile.Read(ref _leavesStale);
            var swept = Volatile.Read(ref _leavesSwept);

            // Interest is DERIVED rather than counted: the three sites that append a LEAVE record are exhaustive, so what the frames carried minus the
            // two named causes is the third by construction, and one fewer increment runs on the walk.
            return (Volatile.Read(ref _leavesConsidered), Volatile.Read(ref _leavesEmitted) - stale - swept, stale, swept);
        }
    }

    /// <summary>Mean known-set size and mean outstanding slot debt over the frames that published.</summary>
    /// <remarks>
    /// <b>The trajectory is the other half of the same question.</b> A known-set that climbs over a run is a view still filling; one that sits flat while
    /// enters are being emitted every tick is a view that finished filling long ago and is being re-told what it already knew.
    /// </remarks>
    public (double Known, double Owed, long Frames) ViewFill
    {
        get
        {
            var frames = Volatile.Read(ref _framesProduced);
            return frames == 0
                ? default
                : (Volatile.Read(ref _knownTotal) / (double)frames, Volatile.Read(ref _owedTotal) / (double)frames, frames);
        }
    }

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

    private readonly SessionViewStore _views;

    /// <summary>
    /// Gathers expressed as a difference against the session's previous tick: only the slots that entered its view, or that S1 marked changed, were read.
    /// </summary>
    public long TemporalGathers => Volatile.Read(ref _temporalGathers);

    /// <summary>
    /// Hit slots the difference read, and hit slots it carried forward without reading, since start.
    /// </summary>
    /// <remarks>
    /// The ratio is the whole of what the difference can save: a carried slot costs two sequential array touches, a visited one costs a block read and a
    /// known-set probe. A world in which almost everything changes every tick carries almost nothing, and no amount of tuning makes it pay there.
    /// </remarks>
    public (long Visited, long Carried) TemporalSlots => (Volatile.Read(ref _slotsRead), Volatile.Read(ref _slotsSkipped));

    /// <summary>Gathers that walked every watched slot — a session behind by more than one tick, still filling, or resetting.</summary>
    public long FullGathers => Volatile.Read(ref _fullGathers);

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

    /// <summary>Runs synthesized for sparse sessions from the changed-block tables — content changes delivered without an interest run. Cumulative.</summary>
    public long SyntheticRuns
    {
        get
        {
            var t = 0L;
            for (var w = 0; w < _workers.Length; w++)
            {
                t += _workers[w]?.SyntheticRuns ?? 0;
            }

            return t;
        }
    }

    /// <summary>
    /// Builds a sparse session's synthetic runs: one per cluster it holds whose content changed this tick — or, when its frame turned out to be a full one,
    /// every cluster it holds — that none of its real runs already covers.
    /// </summary>
    /// <param name="index">The session's index in this tick's arrays.</param>
    /// <param name="view">Its interest view.</param>
    /// <param name="scratch">The frame worker's scratch.</param>
    /// <param name="runs">Its real runs this tick.</param>
    /// <param name="allHeld">
    /// The frame is a full gather after interest had already treated the session as sparse — a profile switch that reset it, a forced full read — so the
    /// clusters interest skipped as unchanged must be read too, or the reset frame would never re-enter them.
    /// </param>
    /// <returns>The runs, empty when the session is not sparse.</returns>
    /// <remarks>
    /// <para>
    /// A synthetic run is exactly the run interest would have emitted for an unchanged cluster — the held mask, nothing entered, the session's own view
    /// entry — so the gather reads it with the same code and the same guarantees.
    /// </para>
    /// <para>
    /// <b>The session's view is the index.</b> Its entries are the clusters it holds, and projection has already recorded the ones whose content changed —
    /// computed once per block, not per session. What is left per session is one table read per held cluster, sequential over the view, in the session's
    /// own frame worker, with nothing serial.
    /// </para>
    /// </remarks>
    private ReadOnlySpan<InterestRun> SparseRunsFor(int index, SessionInterestView view, FrameWorkerScratch scratch, ReadOnlySpan<InterestRun> runs,
        bool allHeld)
    {
        var states = _changeStates;
        if (view == null || states == null || _interest == null || !_interest.IsSparse(index))
        {
            return ReadOnlySpan<InterestRun>.Empty;
        }

        var entries = view.EntryCount;

        // The view entries a real run covers this tick: those are gathered through their run and must not be gathered twice. Zero is never an epoch, so at
        // the wrap the stamps are cleared rather than letting one from 2^32 sessions ago read as this one's.
        if (++scratch.CoveredEpoch == 0)
        {
            Array.Clear(scratch.CoveredStamp);
            scratch.CoveredEpoch = 1;
        }

        var epoch = scratch.CoveredEpoch;
        if (scratch.CoveredStamp.Length < entries)
        {
            Array.Resize(ref scratch.CoveredStamp, Math.Max(entries, scratch.CoveredStamp.Length * 2));
        }

        for (var r = 0; r < runs.Length; r++)
        {
            var vi = runs[r].ViewIndex;
            if ((uint)vi < (uint)scratch.CoveredStamp.Length)
            {
                scratch.CoveredStamp[vi] = epoch;
            }
        }

        var tick = (uint)_tick;
        var n = 0;
        for (var e = 0; e < entries; e++)
        {
            var held = view.MaskAt(e);
            if (held == 0UL || scratch.CoveredStamp[e] == epoch)
            {
                continue;
            }

            var key = view.KeyAt(e);
            var archetype = SessionInterestView.ArchetypeOf(key);
            var state = archetype < states.Length ? states[archetype] : null;
            if (state == null)
            {
                continue;
            }

            var chunk = (int)(key & 0xFFFFFFFFL);
            var block = state.ChangedBlockOf(chunk, tick, out var changedSlots);
            if (allHeld)
            {
                if (block == null && !state.Directory.TryGetBlock(chunk, out block))
                {
                    continue;
                }
            }
            else if (block == null || (changedSlots & held) == 0UL)
            {
                continue;
            }

            // The entry was written for this tick, so the id can only disagree if the block was released and reused since — never within a tick.
            if (block->ChunkId != chunk)
            {
                continue;
            }

            if (n == scratch.SparseRuns.Length)
            {
                Array.Resize(ref scratch.SparseRuns, n * 2);
            }

            ref var run = ref scratch.SparseRuns[n++];
            run.Block = (nint)block;
            run.Slots = held;
            run.Entered = 0UL;
            run.ViewIndex = e;
            run.ChunkId = chunk;
            run.ArchetypeIndex = archetype;
            run.Flags = InterestRunFlags.None;
        }

        scratch.SyntheticRuns += n;
        return new ReadOnlySpan<InterestRun>(scratch.SparseRuns, 0, n);
    }

    /// <summary>
    /// Whether a member standing still may re-emit what it holds for the clusters whose structure did not change: its frame is incremental, so the view holds
    /// what the last published frame described, and that frame left no hit pending on a cluster without a block.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="tick">The tick about to be resolved.</param>
    /// <returns><see langword="true"/> when the held masks are exactly last tick's kernel output.</returns>
    public bool CanRetainStationary(SessionId session, long tick) => WillGatherIncrementally(session, tick) && !StateOf(session).LeftPendingHits;

    /// <summary>
    /// Whether <paramref name="session"/>'s frame on <paramref name="tick"/> will take the incremental path — the condition the gather itself applies.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="tick">The tick about to be resolved.</param>
    /// <returns><see langword="true"/> when its gather will read entered and changed slots only.</returns>
    /// <remarks>
    /// Read by interest before this stage runs for the tick. Every input — the reset and force flags, the baseline, the view-complete latch — is written by
    /// this stage at the end of the PREVIOUS tick or by ingress before interest, so the answer interest acts on is the one the gather will reach.
    /// </remarks>
    public bool WillGatherIncrementally(SessionId session, long tick)
    {
        var state = StateOf(session);
        if (state == null || _interest == null || !_interest.IncrementalInterest)
        {
            return false;
        }

        // The profile and generation are compared here because the reset they cause is only set by this stage's prologue, AFTER interest has asked:
        // a session that switched profile this tick is about to be reset, and is not incremental whatever its baseline says.
        if (state.Generation != session.Generation || !string.Equals(_sessions.ProfileName(session), state.Profile, StringComparison.Ordinal))
        {
            return false;
        }

        var fillNeedsFullRead = !state.ViewComplete && !_options.OwedSlotCarry;
        return !state.PendingReset && !state.ForceFullGather && !fillNeedsFullRead && state.Baseline == tick - 1;
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

        var prologueFrom = PhaseTimingEnabled ? Stopwatch.GetTimestamp() : 0L;
        _interest = interest;
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
        _sessionCursor = 0;

        // Drop every worker's cached encode. The tick is part of the share key as well, so this is belt and braces — but it is what keeps a cached pointer
        // from outliving the buffer it points into if that key logic is ever changed, and it costs one flag per worker per tick.
        for (var i = 0; i < _workers.Length; i++)
        {
            _workers[i]?.Shared.BeginTick();
        }

        // Before the early return, because the sweep is about sessions that are NOT being served: one whose interest produced nothing this tick still has to
        // be degraded, closed for a skip run, or closed for silence. Keying it off the interest list would exempt exactly the sessions it exists to catch.
        var sweepFrom = prologueFrom != 0L ? Stopwatch.GetTimestamp() : 0L;
        SweepSkipPolicy();
        if (sweepFrom != 0L)
        {
            _sweepTicks2 += Stopwatch.GetTimestamp() - sweepFrom;
        }

        // PROTOTYPE (push): push sessions are not in the interest pass's list; they are collected, prepared and indexed here.
        BeginPushTick();

        _tickSessionCount = interest?.TickSessionCount ?? 0;
        if (_tickSessionCount == 0 && _pushSessionCount == 0)
        {
            return 0;
        }

        var workers = Math.Max(1, workerCount);
        EnsureWorkers(workers);

        // The one encode of the tick's shared server segment (W25), here because this is the track's last single-threaded point before the chunks run: the
        // dispatch that follows is the barrier that publishes the bytes to every worker that will copy them. Through the property, not the field, so the one
        // acquire the field documents is the only way it is ever read.
        Stats?.BeginTick(tickNumber);

        var prepFrom = prologueFrom != 0L ? Stopwatch.GetTimestamp() : 0L;
        for (var i = 0; i < _tickSessionCount; i++)
        {
            PrepareSession(interest!.SessionAt(i));
        }

        if (prepFrom != 0L)
        {
            _prepareTicks += Stopwatch.GetTimestamp() - prepFrom;
        }

        if (_options.DynamicFrameScheduling)
        {
            BuildLongestFirstOrder(interest!);
        }

        if (prologueFrom != 0L)
        {
            _prologueTicks += Stopwatch.GetTimestamp() - prologueFrom;
        }

        var chunks = Math.Min(workers, Math.Max(_tickSessionCount, _pushSessionCount));
        _lastChunkCount = chunks;
        if (_chunkBusy.Length < chunks)
        {
            Array.Resize(ref _chunkBusy, Math.Max(16, chunks));
            Array.Resize(ref _chunkStart, Math.Max(16, chunks));
            Array.Resize(ref _chunkEnd, Math.Max(16, chunks));
        }

        return chunks;
    }

    /// <summary>
    /// Orders this tick's sessions most-expensive-first, so the stage's tail is a cheap session rather than whichever one was taken last.
    /// </summary>
    /// <param name="interest">The interest pass, for a first-tick cost when a session has no history.</param>
    /// <remarks>
    /// <b>Counting sort over the predictor, not a comparison sort</b>: the key is a slot count bucketed to 256 levels, so the order costs two linear passes
    /// over contiguous ints and no allocation. At ten thousand sessions that is a few tens of microseconds against a stage of tens of milliseconds.
    /// </remarks>
    private void BuildLongestFirstOrder(InterestPass interest)
    {
        var count = _tickSessionCount;
        if (_order.Length < count)
        {
            Array.Resize(ref _order, Math.Max(64, count));
            Array.Resize(ref _orderKeys, Math.Max(64, count));
        }

        // The predictor, bucketed: the previous frame's visited slots, or the view size when there is no previous frame. Bucket 255 is "most expensive",
        // and a session with no history lands there so that its cost is discovered on a worker that has time rather than assumed to be small.
        Span<int> histogram = stackalloc int[256];
        for (var i = 0; i < count; i++)
        {
            var state = StateOf(interest.SessionAt(i));
            var predicted = state == null || state.LastVisited < 0 ? int.MaxValue : state.LastVisited;
            var bucket = predicted == int.MaxValue ? 255 : 255 - Math.Min(255, predicted >> 4);
            _orderKeys[i] = bucket;
            histogram[bucket]++;
        }

        var running = 0;
        for (var b = 0; b < 256; b++)
        {
            var n = histogram[b];
            histogram[b] = running;
            running += n;
        }

        for (var i = 0; i < count; i++)
        {
            _order[histogram[_orderKeys[i]]++] = i;
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

    /// <summary>Assembles, encodes and publishes a frame for each session this chunk owns.</summary>
    /// <param name="chunkIndex">The chunk, which is also the index of the scratch it uses.</param>
    /// <param name="chunkCount">How many chunks the stage dispatched.</param>
    public void ExecuteChunk(int chunkIndex, int chunkCount)
    {
        if (chunkCount <= 0 || (uint)chunkIndex >= (uint)_workers.Length || _interest == null)
        {
            return;
        }

        var from = PhaseTimingEnabled ? Stopwatch.GetTimestamp() : 0L;
        var scratch = _workers[chunkIndex];

        // Accumulated on the stack and flushed once, not six atomics per session: the whole chunk's tally lands on the shared fields in one place, which
        // is what keeps every worker off two cache lines for the length of the stage.
        var counters = default(FrameCounters);

        if (_options.DynamicFrameScheduling)
        {
            // ── Why the sessions are taken one at a time ────────────────────────────────────────────────────────────────────────────────────────────
            //
            // A static split gives every chunk the same NUMBER of sessions, and the stage's wall time is its slowest chunk. Those two only agree while
            // sessions cost the same, and they do not: a session in enter backlog describes five hundred entities while a settled one describes a handful,
            // and which sessions land together is decided by an interest-cell sort nobody balanced. Measured at d06 with 1 000 sessions, the static split
            // delivered 59 ms of CPU in 17 ms of wall — an effective 3.5 workers out of 32, 11 % efficiency.
            //
            // One Interlocked.Increment per session is the whole mechanism. At a thousand sessions that is a thousand uncontended-ish atomics per tick
            // against a stage that costs tens of milliseconds, and it needs no estimate of what a session will cost — which is the thing no heuristic here
            // could get right, because the cost depends on a backlog that changes every tick.
            while (true)
            {
                var index = Interlocked.Increment(ref _sessionCursor) - 1;
                if (index >= _tickSessionCount)
                {
                    break;
                }

                Assemble(_order[index], scratch, ref counters);
            }
        }
        else
        {
            var start = (int)((long)chunkIndex * _tickSessionCount / chunkCount);
            var end = (int)((long)(chunkIndex + 1) * _tickSessionCount / chunkCount);
            for (var i = start; i < end; i++)
            {
                Assemble(i, scratch, ref counters);
            }
        }

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
        Add(ref _framesSkipped, counters.FramesSkipped);
        Add(ref _bytesEncoded, counters.BytesEncoded);
        Add(ref _recordsEncoded, counters.RecordsEncoded);
        Add(ref _entersDeferred, counters.EntersDeferred);
        Add(ref _entersEmitted, counters.EntersEmitted);
        Add(ref _leavesEmitted, counters.LeavesEmitted);
        Add(ref _leavesConsidered, counters.LeavesConsidered);
        Add(ref _leavesStale, counters.LeavesStale);
        Add(ref _leavesSwept, counters.LeavesSwept);
        Add(ref _knownTotal, counters.KnownTotal);
        Add(ref _owedTotal, counters.OwedTotal);
        Add(ref _oversizeSkips, counters.OversizeSkips);
        Add(ref _temporalGathers, counters.TemporalGathers);
        Add(ref _fullGathers, counters.FullGathers);
        Add(ref _slotsRead, counters.SlotsRead);
        Add(ref _slotsSkipped, counters.SlotsSkipped);
        Add(ref _gatherTicks, counters.GatherTicks);
        Add(ref _selectTicks, counters.SelectTicks);
        Add(ref _sweepTicks, counters.SweepTicks);
        Add(ref _sortTicks, counters.SortTicks);
        Add(ref _encodeTicks, counters.EncodeTicks);
        Add(ref _publishTicks, counters.PublishTicks);
        Add(ref _fullReset, counters.FullReset);
        Add(ref _fullForced, counters.FullForced);
        Add(ref _fullIncomplete, counters.FullIncomplete);
        Add(ref _fullBehind, counters.FullBehind);
        Add(ref _owedStale, counters.OwedStale);
        Add(ref _staleMaskSlots, counters.StaleMaskSlots);
        Add(ref _staleMaskRuns, counters.StaleMaskRuns);
        Add(ref _runsWalked, counters.RunsWalked);
        Add(ref _runsEmpty, counters.RunsEmpty);
        Add(ref _visitEntered, counters.VisitEntered);
        Add(ref _visitChanged, counters.VisitChanged);
        Add(ref _visitOwed, counters.VisitOwed);
    }

    private long _fullReset;
    private long _fullForced;
    private long _fullIncomplete;
    private long _fullBehind;
    private long _owedStale;

    /// <summary>Slots owed to a later frame because a stale-generation reuse landed in them, since start.</summary>
    public long OwedStaleSlots => Volatile.Read(ref _owedStale);

    private long _staleMaskSlots;
    private long _staleMaskRuns;

    /// <summary>Retained slots read in full because the block's change mask named another tick, and the runs that caused it, since start.</summary>
    public (long Slots, long Runs) StaleMask => (Volatile.Read(ref _staleMaskSlots), Volatile.Read(ref _staleMaskRuns));

    private long _runsWalked;

    private long _runsEmpty;

    /// <summary>
    /// Interest runs the gather walked, and how many of them had nothing to say.
    /// </summary>
    /// <remarks>
    /// The two together are the shape of the phase, and they are why the run loop leaves early. Measured at d06 with 200 sessions: <b>11.4 M runs, 6.25 M
    /// of them empty</b> — more than half the clusters a session reaches have no enter, nothing the change mask named and no debt. A timed split of the
    /// same run put the slot walk at about two thirds of the phase and the per-run preamble at the other third, which is what the early exit removes.
    /// </remarks>
    public (long Walked, long Empty) GatherShape => (Volatile.Read(ref _runsWalked), Volatile.Read(ref _runsEmpty));

    /// <summary>Interest runs the gather walked, over the life of the assembler.</summary>
    public long RunsWalked => Volatile.Read(ref _runsWalked);

    private long _visitEntered;
    private long _visitChanged;
    private long _visitOwed;

    /// <summary>What the reduced gather's visited slots were made of, since start.</summary>
    public (long Entered, long Changed, long Owed) VisitParts =>
        (Volatile.Read(ref _visitEntered), Volatile.Read(ref _visitChanged), Volatile.Read(ref _visitOwed));

    /// <summary>Why the incremental path fell back to the full walk, since start: reset, forced, incomplete view, and behind by more than a tick.</summary>
    public (long Reset, long Forced, long Incomplete, long Behind) FullGatherCauses =>
        (Volatile.Read(ref _fullReset), Volatile.Read(ref _fullForced), Volatile.Read(ref _fullIncomplete), Volatile.Read(ref _fullBehind));

    /// <summary>Whether the frame stage accumulates per-phase timings. Off by default; one static read per phase boundary when off.</summary>
    /// <remarks>
    /// A process-wide switch rather than an option, for the reason this repository's perf rule gives: two builds differ in JIT codegen as well as in the
    /// line under test, so an A/B wants one binary and one switch.
    /// </remarks>
    public static bool PhaseTimingEnabled;

    private long _gatherTicks;
    private long _selectTicks;
    private long _sweepTicks;
    private long _sortTicks;
    private long _encodeTicks;
    private long _publishTicks;

    /// <summary>The frame stage's phases, in milliseconds of CPU summed over workers since start.</summary>
    public (double Gather, double Select, double Sweep, double Sort, double Encode, double Publish) PhaseMilliseconds
    {
        get
        {
            var scale = 1000d / Stopwatch.Frequency;
            return (Volatile.Read(ref _gatherTicks) * scale,
                Volatile.Read(ref _selectTicks) * scale,
                Volatile.Read(ref _sweepTicks) * scale,
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
            if (slot >= (uint)_states.Length)
            {
                continue;
            }

            var state = BindSlot(slot, session);
            var send = SendStateOf(slot);

            // Silence first: a client that has stopped talking is gone whatever its skip run says, and 4001 tells its SDK to reconnect rather than to back off
            // as 1013 would.
            // The stamp is the tick plus one, so zero is "this slot was never bound" and every real tick — tick zero included — is a mark the sweep can use.
            var heardFrom = send->PingStamp;
            if (heardFrom > 0 && _tick - (heardFrom - 1) > _silenceBoundTicks)
            {
                _sessions.RequestClose(session, SessionCloseReason.Unacknowledged, CloseCodes.NoAcknowledgement);
                Interlocked.Increment(ref _sessionsClosedSilent);
                continue;
            }

            var skipRun = send->SkipRun;

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

    // ── One session's frame ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void Assemble(int index, FrameWorkerScratch scratch, ref FrameCounters counters)
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
            // NOT counted toward the close bound. This tick is skipped because the ENGINE decided to serve this session less, which is the mitigation, not
            // the symptom — counting it makes degradation the cause of the close it exists to avert. Nothing resets the run but a published frame, so a
            // degraded session in a world with nothing to send accumulated a skip on every non-producing tick and a published frame on none of them: the run
            // grew at 1 - 2^-level per tick with no ceiling and no recovery, because FramesSinceDegrade also advances only on a published frame. At 60 Hz and
            // the default three-second bound that closed a level-1 session after six quiet seconds, with 1013 "you are lagging" — the same conflation
            // SUB-15 forbids, reached through the rate class instead of through the idle abandon. Real back-pressure still counts: the K-slot claim below,
            // the acknowledgement lag above it, and the pool refusals further down all advance the run on the ticks this session DOES produce on.
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
        // ── The temporal path (15 § 3.2) ──────────────────────────────────────────────────────────────────────────────────────────────────────────────
        //
        // Strictly stronger than the fast path below, and taken in preference to it: it knows which of this tick's slots are NEW TO THIS SESSION, which is
        // the one fact C-1 lacked, and it produces leaves exactly rather than proving they cannot exist. Everything it needs is the snapshot of the session's
        // own previous tick, which is why it requires a frame to have been produced for that tick and nothing to have been left owed by it.
        //
        // ── The incremental path (15 § 3.2) ───────────────────────────────────────────────────────────────────────────────────────────────────────────
        //
        // Taken whenever the interest pass produced a difference, which is whenever the runtime was built with a view store. The REDUCTION inside it is a
        // separate question: a session exactly one tick behind, holding a complete view and owed nothing, reads only what entered and what changed;
        // anything else reads every slot, because one tick's change mask cannot carry the groups an older baseline is owed (SUB-03). Either way the leaves
        // are the interest pass's, so the known-set sweep does not run.
        var view = _views?.ViewOf(session.Slot, session.Generation);
        var incremental = _interest.IncrementalInterest;
        // A session still FILLING used to read every slot of its view on every frame of the fill, because an incomplete view was taken as proof that the
        // reduction could not be trusted. What it is actually proof of is that something is owed — and with the carry on, what is owed is recorded slot by
        // slot (SelectEnters, and the reuse path in GatherIncremental), so the reduction visits precisely it. The first frame of a session is still a full
        // read: its baseline is zero, which the last clause catches.
        var fillNeedsFullRead = !state.ViewComplete && !(_options.OwedSlotCarry && incremental);

        var full = !incremental
            || state.PendingReset
            || state.ForceFullGather
            || fillNeedsFullRead
            || state.Baseline != _tick - 1;

        // Whether this session may reference a cluster run rather than encode one (17 § 18). Per SESSION and settled once; the per-cluster half is
        // decided inside the gather, after the walk.
        //
        // `state.ViewComplete` is deliberately NOT one of the tests, and it was at first: it looked like the obvious guard against a session still
        // filling, and it cost a measurement to find that it is the wrong one. A sphere observer that keeps moving keeps gaining clusters, so it is
        // almost never owed nothing and almost never latches the flag — at d06 with 200 moving sessions it put NINE PERCENT of session-ticks inside the
        // gate at all. It is also unnecessary: what a filling session is owed shows up as an enter candidate on the very cluster that owes it, and the
        // per-run equivalence test below refuses that cluster and no other.
        var canShare = _replication != null && incremental && !full && view != null;

        if (full && incremental)
        {
            // First condition wins, so the four are disjoint and sum to the full-gather total.
            if (state.PendingReset)
            {
                counters.FullReset++;
            }
            else if (state.ForceFullGather)
            {
                counters.FullForced++;
            }
            else if (fillNeedsFullRead)
            {
                counters.FullIncomplete++;
            }
            else
            {
                counters.FullBehind++;
            }
        }

        state.ForceFullGather = false;

        // Phase timing. `timing` is read once so a flag flipped mid-tick cannot pair a start with no stop; `mark` is the running cursor, advanced by
        // each phase so one timestamp serves as both the end of one phase and the start of the next.
        var timing = PhaseTimingEnabled;
        var mark = timing ? Stopwatch.GetTimestamp() : 0L;

        int touched;
        int staleLeaves;
        int pending;
        if (incremental)
        {
            touched = GatherIncremental(index, state, scratch, stamp, view, full, canShare, out staleLeaves, out pending, out var read,
                out var skipped, out var staleOwed, out var staleMask, out var staleRuns, out var parts, out var shape);
            counters.RunsWalked += shape.Walked;
            counters.RunsEmpty += shape.Empty;
            counters.VisitEntered += parts.Entered;
            counters.VisitChanged += parts.Changed;
            counters.VisitOwed += parts.Owed;
            counters.StaleMaskSlots += staleMask;
            counters.StaleMaskRuns += staleRuns;
            counters.SlotsRead += read;
            counters.SlotsSkipped += skipped;
            state.LastVisited = read;
            counters.OwedStale += staleOwed;
            if (full)
            {
                counters.FullGathers++;
            }
            else
            {
                counters.TemporalGathers++;
            }
        }
        else
        {
            touched = Gather(index, state, scratch, stamp, out staleLeaves, out pending);
        }

        // A reused identity leaves now and enters next frame, and next frame it will be unchanged — so the slots it displaced have to be looked at again
        // whatever the change masks say.
        //
        // WHICH slots is the whole question. Condemning the session's entire next frame is correct and is what this did; it is also why the reduction was
        // off on about 40 % of frames at d06, and why those frames performed roughly 91 % of every slot read in the subsystem (16 § 4) — one reuse
        // anywhere in a 9 000-slot disc cost all 9 000. The incremental path now records the displaced slots themselves, per cluster, in the session's
        // view, and ORs them into the next visit (SessionInterestView._entryOwed). The blunt form stays for the paths that have no view to record into,
        // and behind the option, so the two are one binary apart.
        if (staleLeaves > 0 && (!incremental || !_options.OwedSlotCarry))
        {
            state.ForceFullGather = true;
        }

        if (timing)
        {
            var now = Stopwatch.GetTimestamp();
            counters.GatherTicks += now - mark;
            mark = now;
        }

        counters.LeavesStale += staleLeaves;
        counters.LeavesConsidered += scratch.Temporal.LeavesConsidered;

        var deferred = SelectEnters(state, scratch, view, _options.OwedSlotCarry && view != null);

        if (timing)
        {
            var now = Stopwatch.GetTimestamp();
            counters.SelectTicks += now - mark;
            mark = now;
        }
        if (!incremental)
        {
            counters.FullGathers++;
            counters.LeavesSwept += Sweep(state, scratch, scratch.Temporal, stamp, touched + staleLeaves);
        }

        if (timing)
        {
            var now = Stopwatch.GetTimestamp();
            counters.SweepTicks += now - mark;
            mark = now;
        }

        var records = SortAndCount(scratch);

        if (timing)
        {
            var now = Stopwatch.GetTimestamp();
            counters.SortTicks += now - mark;
            mark = now;
        }

        // The view is complete when nothing the session's interest reached is still owed to it — neither an enter the budget deferred nor a hit the engine
        // could not describe yet. The second half is not pedantry: a cluster that gained its replication block this tick is projected from the next one, so a
        // brand-new session's first tick legitimately has hits and no records, and calling that a complete view would tell the client its world was empty.
        // The view's outstanding debt counts as owed, and it must. The slice serves only part of a large debt, so a frame can visit everything it chose
        // to and still have entities queued that the client has never heard of — latching ViewComplete there would tell it its world was whole. This is
        // what keeps "a complete view owes nothing" true now that a frame no longer has to serve the whole debt to finish.
        // Counted ONLY when the slice can leave debt unserved. With no slice every owed slot is visited on the frame that owes it, so `deferred` already
        // describes the whole debt and adding the view's count would double it — and delay ViewComplete by a frame against the unsliced path for no reason.
        // The records this frame carries include the ones it only REFERENCES. Without this a session whose whole frame is shared runs takes the
        // "nothing to say" branch below, hands its slot back, and tells its client nothing at all — the feature silencing exactly the sessions it serves.
        records += scratch.SharedRecords;

        var outstanding = _options.OwedSlotCarry && _options.OwedSliceMultiplier > 0 && view != null ? view.OwedCount : 0;
        var owed = deferred + pending + outstanding;
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
            //
            // It is given back WITHOUT counting a skip. A skip is back-pressure — K slots full, an acknowledgement too far behind, a pool that would not
            // lend — and an idle world is none of those. Counting it here closed every session in a world quiet for fifty ticks, which is half a second at
            // 100 Hz, and reported it as 1013 "you are lagging".
            send->AbandonIdleFrame(sequence);
            ReturnIfValid(recycled);
            NoteSkip(state, counted: false);
            return;
        }

        // The FLAG is decided here because the frame carries it; the LATCH moves beside the baseline, after the publish. Setting it here left it set on
        // the two paths below that abandon the frame — the oversize skip and the pool refusal — and SUB-03 requires a tick that published nothing to leave
        // the session exactly as it was. It also feeds `full`, so a latch that ran ahead of the publish would drop the next frame onto the reduction while
        // the client was still owed its fill.
        if (completed || state.ViewComplete)
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
        // WORLD observers only. The share key is a vector of per-archetype RECORD COUNTS, which is sound exactly while two sessions on one profile are
        // known to reach the same clusters — true of a World observer by construction, false of a Sphere, whose members stand in different places and see
        // different entities. Two sphere sessions on one worker that happened to emit the same number of segments and states would copy each other's bytes,
        // and each client would be told about entities it cannot see and never told about the ones it can: silent, permanent divergence in both directions.
        // SharedFrameSet's own remarks call the count vector "a cross-check, not the guarantee", and this is the guarantee.
        // A frame that references runs is never whole-frame shared. The share key is a vector of per-archetype record COUNTS, and two sessions can
        // reference different clusters while agreeing on every count — which would copy one client's world into another's. The two mechanisms answer the
        // same question at different granularities and the finer one wins; whole-frame sharing is World observers only, so a spatial profile loses nothing.
        var shareable = owed == 0 && !emitStats && !state.PendingReset && state.ViewComplete && state.Profile != null
            && scratch.SharedRecords == 0
            && _interest.SharesFramesByProfile(index);
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
                var sharedSegments = scratch.SharedRuns(a, true);
                var sharedStates = scratch.SharedRuns(a, false);
                if (scratch.Count(a, FrameListKind.Enter) == 0 && scratch.Count(a, FrameListKind.Segment) == 0 && scratch.Count(a, FrameListKind.State) == 0
                    && scratch.Count(a, FrameListKind.Leave) == 0 && sharedSegments.Length == 0 && sharedStates.Length == 0)
                {
                    continue;
                }

                EntitiesEncoder.WriteEntities(ref writer, _encodePlans[a], scratch.List(a, FrameListKind.Enter), scratch.List(a, FrameListKind.Segment),
                    scratch.List(a, FrameListKind.State), scratch.List(a, FrameListKind.Leave), sharedSegments, sharedStates);
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

        if (timing)
        {
            var now = Stopwatch.GetTimestamp();
            counters.EncodeTicks += now - mark;
            mark = now;
        }

        if (length > _maxFrameBytes)
        {
            counters.OversizeSkips++;
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
        Commit(state, scratch, stamp, ref counters);
        state.Baseline = _tick;
        CommitView(index, view);
        if (completed)
        {
            state.ViewComplete = true;
        }

        // Anything the frame left owed, or any identity it had to retire, makes the NEXT frame read every slot rather than only what entered and changed.
        // A deferred enter is the reason that matters: the view now claims the slot, so the reduction would never offer it again, and the entity would be
        // one the client is never told about — C-1's failure reached from the other side. A stale leave is SUB-06's handover, and an owed hit is a cluster
        // whose block arrived too late to describe.
        // With the carry on, both halves of `owed` are recorded at slot granularity instead of as a whole frame.
        //
        // A deferred enter owes its own slot (SelectEnters). `pending` needs no debt, for two DIFFERENT reasons depending on which of its two sources it
        // came from, and both are worth stating because each would stop holding independently: a NoBlock run is left unclaimed by CommitView, so its slots
        // ENTER again next tick of their own accord; a slot that merely had no identity yet sits in a block-bearing run whose mask CommitView does commit,
        // and is covered instead by ProjectionPass skipping the changed-mask computation on identity starvation, which stamps the slot changed on the tick
        // it finally gets one.
        state.ForceFullGather |= owed > 0 && !(_options.OwedSlotCarry && incremental);
        state.LeftPendingHits = pending > 0;

        if (timing)
        {
            // Everything from the encode's end to here: the pool rent, the copy into the block, the publish, and the two commits. Read once at the end
            // rather than around each of them, because the point of the breakdown is which PHASE costs, not which statement.
            counters.PublishTicks += Stopwatch.GetTimestamp() - mark;
        }

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

        counters.FramesProduced++;
        counters.BytesEncoded += length;
        counters.RecordsEncoded += records;
        if (deferred > 0)
        {
            counters.EntersDeferred += deferred;
        }
    }

    /// <summary>
    /// Walks the session's hits: a known entity contributes the groups that changed after its baseline, an unknown one an enter candidate, and one whose
    /// generation moved on a leave.
    /// </summary>
    /// <returns>How many known-and-current entries the hits reached.</returns>
    private int Gather(int index, SessionFrameState state, FrameWorkerScratch scratch, ushort stamp, out int staleLeaves, out int pending)
    {
        var baseline = (uint)state.Baseline;
        var runs = _interest.HitsOf(index);
        var touched = 0;
        staleLeaves = 0;
        pending = 0;

        // The identity tables are reset here as well as on the incremental path, because ClassifyHit's stale branch and Sweep both gate on them. A table
        // still holding the previous session's identities would refuse this one's leaves.
        scratch.Temporal.BeginSession();

        for (var r = 0; r < runs.Length; r++)
        {
            ref readonly var run = ref runs[r];
            if ((run.Flags & InterestRunFlags.Departed) != 0)
            {
                continue;
            }

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

            while (slots != 0)
            {
                var slot = BitOperations.TrailingZeroCount(slots);
                slots &= slots - 1;
                ClassifyHit(state, scratch, scratch.Temporal, stamp, baseline, run.Block, out _, -1, archetype, plan, slot, ref touched,
                    ref staleLeaves, ref pending);
            }
        }

        return touched;
    }

    /// <summary>
    /// The same walk over a session's interest, reduced to the slots that entered it and the slots S1 marked changed (15 § 3.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two facts meet here, and neither is sufficient alone.</b> The interest pass supplies <c>Entered</c> — the slots this session did not hold when its
    /// last frame was published — which is the half no block can answer and the half whose absence made C-1 unsound. S1 supplies <c>ChangedSlots</c> — the
    /// slots that produced a record this tick — which is the half no per-session state can answer. Their union is exactly what the frame has to describe;
    /// everything else in the view is an entity the session already knows, unchanged, with nothing to say.
    /// </para>
    /// <para>
    /// <b>Leaves are not discovered here at all.</b> The interest pass computed them from the difference between this tick's per-cluster masks and the ones
    /// the session's last published frame described, and named them from the identities stored in the view — so <see cref="Sweep"/>, an O(known-set) walk
    /// per session per tick and [14 § 5.2](14)'s worst-scaling function, does not run.
    /// </para>
    /// <para>
    /// <b>When the session is not exactly one tick behind, every slot is read.</b> SUB-03 requires a frame to carry every group newer than the session's
    /// baseline, and one tick's change mask does not contain the previous tick's; the same applies after a reset, which empties the known-set, and after
    /// anything the last frame left owed. The reduction is an optimisation over the steady state and the full walk is its backstop, which is what keeps
    /// this a performance change rather than a change of contract.
    /// </para>
    /// </remarks>
    /// <returns>How many known-and-current entries the hits reached.</returns>
    private int GatherIncremental(int index, SessionFrameState state, FrameWorkerScratch scratch, ushort stamp, SessionInterestView view, bool full,
        bool canShare, out int staleLeaves, out int pending, out int slotsRead, out int slotsSkipped, out int staleOwed, out int staleMask,
        out int staleRuns, out (int Entered, int Changed, int Owed) visitParts, out (int Walked, int Empty) shape)
    {
        var baseline = (uint)state.Baseline;
        var runs = _interest.HitsOf(index);
        var temporal = scratch.Temporal;

        // Read ONCE for the whole gather. The option is init-only so it cannot change under us, but pairing "what was owed" with "what is cleared" off two
        // separate reads is the shape of a bug even when the field is immutable.
        var carry = _options.OwedSlotCarry;

        // Hoisted for the same reason as `carry`, and because the two tests below sit in the innermost loops of the whole subsystem: a JIT-time constant
        // ANDed with one field read, resolved once per gather rather than once per slot.
        var prefetch = Sse.IsSupported && _options.GatherPrefetch;

        // How many owed slots one run may serve. Everything owed is an enter and the frame can send at most this many in total, so serving far more than
        // the budget is provably wasted work; spreading it over the runs is what keeps distant clusters from starving.
        // The owed slice is the enter budget divided by the number of runs the session HOLDS, and a sparse session's run list names only the ones whose
        // membership moved. Dividing by that would hand each owed run a far larger slice and serve more deferred enters per frame than the full path does
        // — the divergence IncrementalInterestTests caught. The runs interest skipped are added back, so the slice is the full path's exactly.
        var runCount = Math.Max(1, runs.Length + _interest.SparseSkippedOf(index));
        var owedPerRun = _options.OwedSliceMultiplier <= 0
            ? int.MaxValue
            : Math.Max(1, (Math.Max(1, _options.EnterBudgetPerFrame) * _options.OwedSliceMultiplier) / runCount);
        var staleMaskRuns = 0;
        var staleMaskSlots = 0;
        var walked = 0;
        var empty = 0;
        var sharedRuns = 0;
        var sharedRecords = 0;
        var sharedRefused = 0;
        var missGated = 0;
        var missNoRun = 0;
        var missNotReached = 0;
        var visitEntered = 0;
        var visitChanged = 0;
        var visitOwed = 0;
        var touched = 0;
        staleLeaves = 0;
        pending = 0;

        // Accumulated in locals and flushed once, not per run: two atomics per cluster is a line ping-ponging between every worker for the whole stage.
        var read = 0;
        var skipped = 0;
        var owedStale = 0;

        temporal.BeginSession();

        // A sparse session's real runs name only membership changes; the clusters whose CONTENT changed without their membership moving come through the
        // fan-out as synthetic runs, read by exactly the same body below.
        var synthetic = SparseRunsFor(index, view, scratch, runs, allHeld: full);
        var total = runs.Length + synthetic.Length;
        for (var r = 0; r < total; r++)
        {
            ref readonly var run = ref (r < runs.Length ? ref runs[r] : ref synthetic[r - runs.Length]);

            // ── Why the gather prefetches at all ────────────────────────────────────────────────────────────────────────────────────────────────────
            //
            // Measured at d06 with 200 sessions: 11.3 M interest runs for 19.6 M visited slots — 1.7 slots per run — and 409 ns of gather per run.
            // Five misses in a dependent chain account for that number exactly: the block header, the view's debt word, the view's identity line, the
            // slot's hot entry and the known-set probe. Each address is computed from the load before it, so nothing overlaps and the core stalls on
            // every one. The next run's two independent lines do not depend on this run at all, so issuing them here buys a whole run of latency for
            // the cost of two instructions — and none where the platform has no prefetch, because the call folds to nothing.
            if (prefetch && r + 1 < runs.Length)
            {
                ref readonly var ahead = ref runs[r + 1];
                if (ahead.Block != 0)
                {
                    Prefetch((void*)ahead.Block);
                }

                if (view != null && ahead.ViewIndex >= 0)
                {
                    view.PrefetchOwed(ahead.ViewIndex);
                }
            }

            if ((run.Flags & InterestRunFlags.Departed) != 0)
            {
                // A cluster the observer walked away from: its leaves are already in the interest pass's list and its membership is committed below. It
                // carries no hits and must not be counted as owed.
                continue;
            }

            if (run.Block == 0 || (run.Flags & InterestRunFlags.NoBlock) != 0)
            {
                pending += run.HitCount;
                continue;
            }

            var archetype = run.ArchetypeIndex;
            if (archetype >= _plans.Length)
            {
                continue;
            }

            walked++;
            var plan = _encodePlans[archetype];
            var occupancy = BitOperations.PopCount(run.Slots);

            var owedThisRun = 0UL;
            var owedHere = view != null && run.ViewIndex >= 0 ? view.OwedAt(run.ViewIndex) : 0UL;

            // ── Is this a cluster whose records were encoded once for everybody? (17 § 18) ────────────────────────────────────────────────────────
            //
            // Noted here and DECIDED after the walk. What is shared is the encode — the gaps, the masks and the body copies — and not the classification,
            // because whether a slot is a STATE or an ENTER for a given session depends on what that session already knows. The obvious shortcut, reading
            // that off the session's own view, does not work and the differential oracle produced the counter-example within two hundred ticks: an entity
            // displaced from one of the session's clusters is dropped from its known-set while the cluster it moved into still names it, so the view says
            // "told" where the client has never heard of it. The known-set is the only authority and it is per entity.
            //
            // So the run is walked as usual, and the records it produced are DISCARDED in favour of the shared bytes only when the walk proved that this
            // session's records would have been exactly those bytes: every slot known under the current generation, nothing entering, nothing displaced.
            ulong visit;
            if (full)
            {
                visit = run.Slots;

                // A full walk reads every slot the session reaches, so it serves the WHOLE debt for this cluster. Leaving it owed made the count outlive
                // the frame that discharged it, and a view whose count never falls to zero never completes: EntitiesEncodingTests' golden vector caught it
                // as a ViewComplete flag arriving a tick late.
                owedThisRun = run.Slots;
            }
            else
            {
                visit = run.Entered;
                var retained = run.Slots & ~run.Entered;
                if (retained != 0)
                {
                    // ACQUIRE on the tick, pairing with the release in ProjectionPass. The mask is only meaningful for the tick it names, so reading the
                    // name with a plain load would let the mask read sink above it on arm64 and pair this tick's number with the previous tick's bits. A
                    // block S1 did not project this tick describes no change set at all, so every retained slot of it is read.
                    var header = (ReplicationBlockHeader*)run.Block;
                    if (Volatile.Read(ref header->ChangedTick) == (uint)_tick)
                    {
                        visit |= retained & header->ChangedSlots;
                    }
                    else
                    {
                        // The block was not projected on this tick, so its mask names another tick and cannot be trusted. Every retained slot is read.
                        // Counted because it is the difference between reading what changed and reading the view, and nothing said how often it happens.
                        visit |= retained;
                        staleMaskSlots += BitOperations.PopCount(retained);
                        staleMaskRuns++;
                    }
                }

                // Slots this cluster owed from an earlier frame: the ones whose occupant was replaced, which no change mask can show. Restricted to what
                // the session still reaches, because a slot it has since lost is described by a LEAVE and not by a visit. See SessionInterestView._entryOwed.
                if (owedHere != 0)
                {
                        // ── Why the debt is served a slice at a time ──────────────────────────────────────────────────────────────────────────────
                        //
                        // Everything owed is an ENTER waiting to be described, and a frame can describe at most EnterBudgetPerFrame of them. When the
                        // view is larger than the budget can fill — a 31 147-entity disc against a budget of 500 — the backlog never drains, and
                        // visiting all of it every tick reads thousands of slots to choose five hundred. Measured at d07 with 200 sessions: 428 of the
                        // 513 million slots the gather read were owed, 83.6 % of the whole stage's work, to send 500 per frame.
                        //
                        // So each run serves a SLICE and keeps the rest owed. Per run rather than a global cap because runs are walked in a fixed order
                        // and a global one would serve the first clusters every tick and starve the last ones forever. The slot the slice drops is not
                        // lost: its bit stays set and the next frame takes it, which is the whole point of the debt being a mask rather than a flag.
                    var slice = owedHere & run.Slots;
                    if (BitOperations.PopCount(slice) > owedPerRun)
                    {
                        // Drop the high bits until the slice fits. At most 64 iterations and only on a run that is genuinely over its share, which is the
                        // case this exists for; the common run owes nothing and never reaches here.
                        do
                        {
                            slice &= slice - 1;
                        }
                        while (BitOperations.PopCount(slice) > owedPerRun);
                    }

                    owedThisRun = slice;
                    visit |= owedThisRun;
                }
            }

            // What the visit is MADE OF, counted per run and disjoint by construction: entered first, then what the change mask added on top, then
            // what the debt added on top of both. Without this the only number available is "half the view was read", which names no cause.
            if (!full)
            {
                var e = BitOperations.PopCount(run.Entered);
                var c = BitOperations.PopCount(visit & ~run.Entered & ~owedThisRun);
                visitEntered += e;
                visitChanged += c;
                visitOwed += BitOperations.PopCount(visit) - e - c;
            }

            var visited = BitOperations.PopCount(visit);
            read += visited;
            skipped += occupancy - visited;

            if (visited == 0)
            {
                // ── The run that has nothing to say ─────────────────────────────────────────────────────────────────────────────────────────────────
                //
                // Half of them: 6.0 M of 12.2 M measured at d06 with 200 sessions, and the same half at 50 and 100. A cluster the session still reaches
                // where nothing entered, the change mask named nothing among the slots it retains, and no earlier frame owes it anything. Everything below
                // this point — the identity-region pointer, the debt's discharge, the prefetches and the walk — is work for a cluster that emits no record.
                //
                // The ONE thing that still has to happen is trimming a debt to what the session still reaches, because a slot it has lost is described by a
                // LEAVE and can owe nothing. That is only reachable when there IS a debt, which is why it is guarded by the word this run already read:
                // KeepOwed on an entry that owes nothing changes no value and dirties one line, twelve million times a run.
                empty++;
                if (owedHere != 0)
                {
                    view.KeepOwed(run.ViewIndex, ~run.Slots);
                }

                continue;
            }

            // ── A cluster whose records were encoded once, referenced without being walked (17 § 18) ──────────────────────────────────────────────
            //
            // Looked up HERE, below the empty-run exit: more than half of all runs turn out to have nothing to say, and the table entry is a random load
            // into a chunk-indexed array — a cache miss the session has no use for. Measured at d06 with 200 sessions that placement is 25 million lookups
            // against 58 million.
            //
            // <b>What makes it sound to skip the walk is that the session's per-slot identities are now exact.</b> They were not: an entity migrating
            // between clusters carried its group stamps with it, so if its projected bytes had not changed the destination's mask did not name the slot,
            // nobody visited it, and the session that watched the SOURCE emitted a leave for an entity still inside its own view. The block now names the
            // arrival (ReplicationBlockHeader.ArrivedSlots) and the slot is visited, which is what closes the gap between "the view says I told them" and
            // "the known-set says they know".
            //
            // The remaining per-slot test is one load each: an identity of zero is a slot S1 could not name when this session last read it, which the
            // committed mask claims and the client has never been told about.
            if (canShare && owedHere == 0 && run.Entered == 0 && run.ViewIndex >= 0)
            {
                // From the RUN's own chunk id rather than the block header's. They are the same number, but reading it from the header is a dependent
                // load through a pointer this branch would otherwise not touch.
                var candidate = _replication[archetype].SharedRuns.At(run.ChunkId, (uint)_tick);
                if (candidate == null)
                {
                    missNoRun++;
                }
                else if ((candidate->Slots & ~run.Slots) != 0 || (candidate->Slots & ~view.MaskAt(run.ViewIndex)) != 0)
                {
                    // Either the run names a slot this session does not reach — referencing it would tell the client about an entity it cannot see — or
                    // one it has never been told about. The converse, a slot the session reaches that the run does not name, is fine and expected: it
                    // simply did not change.
                    missNotReached++;
                }
                else if (TryTakeSharedRun(state, scratch, temporal, view, run, candidate, archetype, plan, ref sharedRecords))
                {
                    var described = BitOperations.PopCount(candidate->Slots);
                    read += described;
                    skipped += occupancy - described;
                    touched += described;
                    visitChanged += described;
                    sharedRuns++;
                    continue;
                }
                else
                {
                    sharedRefused++;
                }
            }
            else if (canShare)
            {
                missGated++;
            }

            // One pointer per run rather than a 512-byte-strided index per slot. Guarded on the index like its three neighbours: IdsAt(-1) returns a
            // pointer 512 bytes BEFORE the block and would be written through, which is native heap corruption rather than a null check away.
            var ids = view != null && run.ViewIndex >= 0 ? view.IdsAt(run.ViewIndex) : null;

            // Only what this run actually VISITED is cleared, plus anything the session no longer reaches — a slot it has lost is described by a leave
            // and can owe nothing. Clearing the whole entry would drop the part of the debt the slice above deferred. Done before the walk, so a reuse
            // found inside it is re-owed and not immediately cleared.
            if ((owedHere != 0 || owedThisRun != 0) && view != null && run.ViewIndex >= 0)
            {
                view.KeepOwed(run.ViewIndex, owedThisRun | ~run.Slots);
            }

            // Every line the walk below reads in this cluster, issued before the first of them is read. The hot entries are 64 B apart and the identity
            // words 8 B apart, so a visit of two slots is two independent lines in each region; without this they are read one stall at a time.
            if (prefetch)
            {
                var upcoming = visit;
                while (upcoming != 0)
                {
                    var s = BitOperations.TrailingZeroCount(upcoming);
                    upcoming &= upcoming - 1;
                    Sse.Prefetch0(plan.Hot(run.Block, s));
                    if (ids != null)
                    {
                        Sse.Prefetch0(ids + s);
                    }
                }
            }

            var displacedSlots = 0UL;

            while (visit != 0)
            {
                var slot = BitOperations.TrailingZeroCount(visit);
                visit &= visit - 1;

                // The known-set probe is the one miss whose address the block itself supplies, so it can only be overlapped by computing it a slot
                // early. The next slot's hot line was issued above, which is what makes reading its identity here cheap rather than another stall.
                if (prefetch && visit != 0)
                {
                    state.Known.PrefetchProbe(((ReplicationHotEntry*)plan.Hot(run.Block, BitOperations.TrailingZeroCount(visit)))->NetId);
                }

                var id = ClassifyHit(state, scratch, scratch.Temporal, stamp, baseline, run.Block, out var stale, run.ViewIndex, archetype, plan,
                    slot, ref touched, ref staleLeaves, ref pending);


                // A stale-generation reuse leaves now and ENTERS next frame (SUB-06), so the arriving entity has to be read again — and it will not be in
                // next tick's change mask, because it was written before this session reached it. Owed whatever the stored identity was: a slot this view
                // entry has never held reads `was == 0`, so keying the debt off `was != id` below would miss exactly the case that loses the entity.
                if (stale)
                {
                    displacedSlots |= 1UL << slot;
                    owedStale++;
                }
                if (id == 0)
                {
                    continue;
                }

                temporal.NoteSeen(SessionInterestView.NetIdOf(id));

                // The identity the session is being told about at this slot, kept so that a later tick can name it in a LEAVE without reading a block entry
                // that by then may describe somebody else. Written for every slot read, which is every slot whose occupant can have changed.
                if (ids != null)
                {
                    var was = ids[slot];
                    if (was != 0 && was != id)
                    {
                        // A REUSED slot, which the per-cluster mask cannot show: the bit stayed set and the occupant changed. Left undetected this is a
                        // known-set entry nothing can ever remove — the ghost the differential oracle catches at a 90 % skip rate, where a destroy and a
                        // spawn into the same slot routinely fall inside one session's skip window.
                        temporal.NoteDisplaced(was);

                        // The ARRIVING entity is not in this tick's ChangedSlots — it was written before this session watched it — so the next frame has
                        // to be told to look here again. THIS SLOT, not the whole view: that is the difference between owing a word and owing a walk.
                        displacedSlots |= 1UL << slot;
                    }

                    ids[slot] = id;
                }
            }

            // Gated so the OFF arm records nothing at all and is Phase 1's behaviour exactly, which is what makes the two an A/B on one binary.
            if (displacedSlots != 0 && carry && view != null && run.ViewIndex >= 0)
            {
                view.Owe(run.ViewIndex, displacedSlots);
            }
        }

        var leaves = _interest.LeavesOf(index);
        for (var i = 0; i < leaves.Length; i++)
        {
            EmitLeaveIfGone(state, scratch, temporal, leaves[i].Id, view, leaves[i].ViewIndex, leaves[i].Slot);
        }

        // AFTER the walk, because the test each one has to pass is "did this tick read that identity somewhere else", and that is only complete once every
        // run has been walked. An entity that moved between two clusters of the same view is displaced from one and read in the other.
        var displaced = temporal.Displaced;
        for (var i = 0; i < displaced.Length; i++)
        {
            EmitLeaveIfGone(state, scratch, temporal, displaced[i], view, -1, 0);
        }

        if (sharedRuns != 0 || sharedRefused != 0)
        {
            Add(ref _sharedRunsUsed, sharedRuns);
            Add(ref _sharedRecordsUsed, sharedRecords);
            Add(ref _sharedRunsRefused, sharedRefused);
        }

        if (canShare)
        {
            Add(ref _shareMissGated, missGated);
            Add(ref _shareMissNoRun, missNoRun);
            Add(ref _shareMissNotReached, missNotReached);
        }

        slotsRead = read;
        slotsSkipped = skipped;
        staleOwed = owedStale;
        staleMask = staleMaskSlots;
        staleRuns = staleMaskRuns;
        visitParts = (visitEntered, visitChanged, visitOwed);
        shape = (walked, empty);
        return touched;
    }

    /// <summary>Issues a hardware prefetch for <paramref name="address"/>, or nothing at all where the platform has no such instruction.</summary>
    /// <remarks>
    /// <c>Sse.IsSupported</c> is a JIT-time constant, so on arm64 the call and its argument fold away entirely and the feature costs the gather nothing.
    /// A prefetch observes no state and cannot fault, which is what allows it to name an address the caller has not established it may read.
    /// </remarks>
    /// <param name="address">The line to bring toward the core.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Prefetch(void* address)
    {
        if (Sse.IsSupported)
        {
            Sse.Prefetch0(address);
        }
    }

    /// <summary>
    /// Moves each cluster's membership in the session's view to what the frame just published described.
    /// </summary>
    /// <remarks>
    /// <b>Only on a publish, and that is the whole of SUB-03 applied to interest.</b> A session whose frame was skipped keeps the membership its last real
    /// frame described, so the next tick's difference is taken against the same baseline and carries the union of everything the skipped ticks would have
    /// said. Committing in the gather instead would silently narrow the next difference to the tick that was thrown away.
    /// </remarks>
    private void CommitView(int index, SessionInterestView view)
    {
        if (view == null)
        {
            return;
        }

        var runs = _interest.HitsOf(index);
        for (var r = 0; r < runs.Length; r++)
        {
            ref readonly var run = ref runs[r];
            if (run.ViewIndex < 0)
            {
                continue;
            }

            if ((run.Flags & InterestRunFlags.NoBlock) != 0)
            {
                // The cluster had no replication block when its hits were recorded, so the frame stage skipped it and described NOTHING in it. Committing
                // its slots would make the view claim entities the session has never been told about, and the next difference would report them as neither
                // entered nor left — the enter lost for good, which is SUB-18's on_violation exactly. Its hits are counted as owed instead, which puts the
                // session on the full path next tick.
                continue;
            }

            view.Commit(run.ViewIndex, run.Slots);
        }
    }

    /// <summary>
    /// Classifies one hit slot: an entity unknown to the session becomes an enter candidate, one under another generation a leave, and a known one
    /// contributes the groups that changed after the session's baseline.
    /// </summary>
    /// <returns>
    /// The identity standing in the slot with its generation, packed by <see cref="SessionInterestView.Pack"/>, or zero when S1 could not name it.
    /// </returns>
    /// <remarks>
    /// <b><c>stale</c> is set when the slot's identity was reissued under a new generation</b>, and is reported for the CALLER to re-read the slot on the
    /// next frame. It is set before the one-per-identity gate rather than derived from <c>staleLeaves</c> because that gate suppresses the second of two
    /// block entries sharing one netId — and the slot it occupies is owed just as much as the first.
    /// </remarks>
    private ulong ClassifyHit(SessionFrameState state, FrameWorkerScratch scratch, FrameIdentityScratch identities, ushort stamp, uint baseline, nint block,
        out bool stale, int viewIndex, int archetype, ArchetypeEncodePlan plan, int slot, ref int touched, ref int staleLeaves, ref int pending)
    {
        stale = false;
        var hot = (ReplicationHotEntry*)plan.Hot(block, slot);
        var netId = hot->NetId;
        if (netId == NetIdAllocator.NoNetId)
        {
            // S1 could not name the entity this tick — its worker's identity lease ran dry, or the block was rented after the mask was read. It is still
            // watched, so the next tick names it and this session sees it enter then; until then it is owed.
            pending++;
            return 0;
        }

        var generation = hot->Generation;
        var probe = state.Known.Probe(netId, generation, out var entry);
        if (probe == KnownProbe.Unknown)
        {
            scratch.AddEnterCandidate(new FrameRecord
            {
                ViewIndex = viewIndex,
                NetId = netId,
                Rank = Rank(state, plan, block, slot),
                Block = block,
                Slot = (byte)slot,
                Archetype = (ushort)archetype,
            });

            return SessionInterestView.Pack(netId, generation);
        }

        if (probe == KnownProbe.Stale)
        {
            // Reported BEFORE the dedupe below, and that is the whole of why it is a separate out parameter rather than a delta on staleLeaves. Two live
            // block entries were observed carrying one netId in the same tick; the second is deduped and increments nothing, but the slot it occupies is
            // just as owed as the first. Keying the debt off the counter would silently drop it and lose the arriving entity for good.
            stale = true;

            // The identity was reissued while this session was not being sent (02 § 5). The leave goes out now and the reuse enters in the session's NEXT
            // frame, so no frame ever carries both for one netId (03 § 10).
            //
            // Through the SAME one-per-identity gate as every other leave. Two live block entries were observed carrying one netId in the same tick, and
            // both reach this branch; a sub-list with a repeated id is refused by the encoder, which faults the stage and closes every session on the
            // server. The gate is also what stops the non-incremental path's Sweep adding a second leave for an entry this branch never stamps.
            if (!identities.WasEmitted(netId))
            {
                identities.NoteEmitted(netId);
                scratch.Add(archetype, FrameListKind.Leave, new FrameRecord { NetId = netId, Archetype = (ushort)archetype });
                staleLeaves++;
            }

            return SessionInterestView.Pack(netId, generation);
        }

        entry->SeenStamp = stamp;
        touched++;

        if (plan.Moving && hot->GroupTicks[plan.MotionTickSlot] > baseline)
        {
            scratch.Add(archetype, FrameListKind.Segment, new FrameRecord
            {
                NetId = netId,
                Block = block,
                Slot = (byte)slot,
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
                Block = block,
                Slot = (byte)slot,
                GroupMask = (byte)mask,
                Archetype = (ushort)archetype,
            });
        }

        return SessionInterestView.Pack(netId, generation);
    }

    /// <summary>
    /// Walks one session's interest view against its known-set and names the first identity the two disagree about, or <see langword="null"/>.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>A description of the disagreement, or <see langword="null"/> when there is none.</returns>
    /// <remarks>
    /// <b>The invariant a referenced cluster run rests on, checked where it LIVES rather than where it is used</b> (17 § 18). The per-slot verification
    /// inside the shared path only sees slots some run happened to name on some tick, so a latent disagreement can sit in a quiet cluster for hundreds of
    /// ticks and surface the moment that cluster changes. This walks the whole view: every slot the session's committed mask claims must name an identity
    /// its known-set holds under the same generation.
    /// </remarks>
    internal string FindViewIdentityDisagreement(SessionId session)
    {
        var state = StateOf(session);
        var view = _views?.ViewOf(session.Slot, session.Generation);
        if (state == null || view == null)
        {
            return null;
        }

        for (var e = 0; e < view.EntryCount; e++)
        {
            var mask = view.MaskAt(e);
            while (mask != 0)
            {
                var slot = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                var id = view.IdAt(e, slot);
                if (id == 0)
                {
                    continue;
                }

                var netId = SessionInterestView.NetIdOf(id);
                var generation = SessionInterestView.GenerationOf(id);
                if (state.Known.Probe(netId, generation, out _) != KnownProbe.Current)
                {
                    return $"session {session.Slot} view entry {e} (key {view.KeyAt(e):x}) slot {slot} claims netId {netId} generation {generation}, "
                        + "which the session's known-set does not hold under that generation";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Takes a cluster's shared run in place of walking it: records every identity it names as read this tick, then appends the references.
    /// </summary>
    /// <param name="state">The session.</param>
    /// <param name="scratch">The worker's scratch, which receives the references.</param>
    /// <param name="temporal">This tick's identity table, which decides which leaves are real.</param>
    /// <param name="view">The session's interest view, which holds the per-slot identities.</param>
    /// <param name="run">The interest run.</param>
    /// <param name="shared">The cluster's run for this tick.</param>
    /// <param name="archetype">The archetype's plan index.</param>
    /// <param name="plan">Its encoding constants, for the verification pass only.</param>
    /// <param name="records">Accumulates the records the references carry.</param>
    /// <returns><see langword="false"/> when a slot has no identity, in which case the caller walks the cluster as usual.</returns>
    /// <remarks>
    /// <para>
    /// <b>Recording the identities is not bookkeeping, it is what stops a leave.</b> A leave is emitted for an identity the interest pass dropped or the
    /// walk found displaced, UNLESS this tick read it somewhere else in the view — and a cluster that is referenced rather than walked reads nothing. The
    /// entity sitting quietly in it would be retracted from a client that can still see it.
    /// </para>
    /// <para>
    /// <b>The identity comes from the VIEW and not from the block</b>, which is the whole saving: the block's hot entry is the line this path exists to
    /// avoid touching, and the view's identities are contiguous — sixty-four to a 512-byte region the run has already been indexed into.
    /// </para>
    /// </remarks>
    private bool TryTakeSharedRun(SessionFrameState state, FrameWorkerScratch scratch, FrameIdentityScratch temporal, SessionInterestView view,
        in InterestRun run, SharedClusterRun* shared, int archetype, ArchetypeEncodePlan plan, ref int records)
    {
        var ids = view.IdsAt(run.ViewIndex);
        var bits = shared->Slots;
        while (bits != 0)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            var id = ids[slot];
            if (id == 0)
            {
                // A slot the session's committed mask claims but that S1 could not name when it was last read. The client has never been told about it,
                // so the run's records for this cluster are not what this session would have produced.
                return false;
            }

            // ── The verification, which is how this stays honest ──────────────────────────────────────────────────────────────────────────────────
            //
            // Everything above rests on an invariant across three structures — the view's identities, the session's known-set and the block's arrivals —
            // and a hole in it does not fail loudly, it hands one client a STATE for an entity it has never heard of. Under `VerifySharedRuns` the
            // known-set is probed for every slot, which is exactly the work this path exists to skip, so it is off outside the differential oracle.
            if (_verifySharedRuns)
            {
                var hot = (ReplicationHotEntry*)plan.Hot(run.Block, slot);
                var probe = state.Known.Probe(hot->NetId, hot->Generation, out _);
                if (probe != KnownProbe.Current || hot->NetId != SessionInterestView.NetIdOf(id)
                    || hot->Generation != SessionInterestView.GenerationOf(id))
                {
                    throw new InvalidOperationException(
                        $"a shared cluster run names slot {slot} of chunk {run.ChunkId} as netId {hot->NetId} generation {hot->Generation} ({probe}), "
                        + $"which this session's view records as {SessionInterestView.NetIdOf(id)} generation {SessionInterestView.GenerationOf(id)}. "
                        + "The view's identities are not exact, so referencing a run without walking it is unsound.");
                }
            }

            temporal.NoteSeen(SessionInterestView.NetIdOf(id));
        }

        var arena = _replication[archetype].Records[shared->Worker];
        if (shared->SegmentBytes != 0)
        {
            scratch.AddSharedRun(archetype, true, arena.At(shared->SegmentOffset), shared->SegmentBytes, shared->SegmentCount);
        }

        if (shared->StateBytes != 0)
        {
            scratch.AddSharedRun(archetype, false, arena.At(shared->StateOffset), shared->StateBytes, shared->StateCount);
        }

        records += shared->StateCount + shared->SegmentCount;
        return true;
    }

    /// <summary>
    /// Emits a leave for an identity this session's interest no longer reaches, unless this tick read that identity somewhere else in the view.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exception is not an optimisation. An entity that crossed clusters — or that the engine moved to another slot of the same cluster — vanishes from
    /// one position and appears at another within one tick, and a frame carrying both a leave and an enter for one identity is the divergence SUB-06
    /// forbids. The test is "did this tick read it", which the gather records for every slot it classifies, and NOT the known-set's seen stamp: a slot the
    /// reduction skips is deliberately never stamped, so a stamp that had not moved in 65 536 ticks would wrap onto this tick's and suppress a real leave.
    /// </para>
    /// <para>
    /// <b>The generation is checked as well as the identity.</b> Two live block entries were observed carrying one netId in the same tick on the SWG demo —
    /// the engine re-leases an identity as entities are destroyed and slots reused — and the session was told about exactly one of them; deciding on the
    /// number alone emitted a leave from each, and a sub-list with a repeated id is refused by the encoder, which faults the stage and closes every session
    /// on the server.
    /// </para>
    /// </remarks>
    /// <param name="state">The session.</param>
    /// <param name="scratch">The worker's scratch, which receives the leave record.</param>
    /// <param name="temporal">This tick's identity table.</param>
    /// <param name="id">The identity and generation the session was told about.</param>
    /// <param name="view">The session's interest view, whose claim on the identity is retracted with it.</param>
    /// <param name="viewIndex">The entry the identity was held in, or <c>-1</c> when the caller has none to name.</param>
    /// <param name="slot">The slot inside that entry.</param>
    private void EmitLeaveIfGone(SessionFrameState state, FrameWorkerScratch scratch, FrameIdentityScratch temporal, ulong id,
        SessionInterestView view, int viewIndex, int slot)
    {
        var netId = SessionInterestView.NetIdOf(id);
        if (id == 0)
        {
            return;
        }

        // ── The view's claim goes first, and before the suppression ───────────────────────────────────────────────────────────────────────────────
        //
        // The interest pass has just said this slot no longer holds anything this session reaches, so the identity it still names there is stale whatever
        // happens next. Retracting it only when a leave actually goes out is not enough, and the case is worth writing down because it is common rather
        // than exotic: an entity migrates to another cluster of the same view, the leave is SUPPRESSED because the walk read it there, and the frame then
        // carries no record at all and is abandoned as idle. Nothing is committed, so the next tick raises the same leave from the same stale claim — and
        // by then the destination is quiet, nothing reads the identity, and the leave goes out for an entity the session can still see. The client drops
        // it, the known-set forgets it, and the destination's view entry goes on naming it: the exact disagreement that makes a cluster run unsafe to
        // reference without walking it (17 § 18).
        if (view != null && viewIndex >= 0)
        {
            view.SetId(viewIndex, slot, 0);
        }

        // Counted for every identity the interest pass dropped, BEFORE the suppression test, because the point of the number is the ratio between what
        // was considered and what went out. One dropped from a cluster but read this tick in another is an entity that moved inside the view.
        temporal.LeavesConsidered++;
        if (temporal.WasSeen(netId) || temporal.WasEmitted(netId))
        {
            return;
        }

        if (state.Known.Probe(netId, SessionInterestView.GenerationOf(id), out var entry) != KnownProbe.Current)
        {
            return;
        }

        temporal.NoteEmitted(netId);
        scratch.Add(entry->Archetype, FrameListKind.Leave, new FrameRecord { NetId = netId, Archetype = entry->Archetype });
    }

    /// <summary>
    /// Applies the per-frame enter budget: the candidates it allows are moved into their archetypes' enter lists, and the rest wait for a later frame.
    /// </summary>
    /// <returns>How many candidates were deferred.</returns>
    private int SelectEnters(SessionFrameState state, FrameWorkerScratch scratch, SessionInterestView view, bool carry)
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

        var deferred = candidates.Length - take;

        // ── What a DECLINED enter owes the next frame ─────────────────────────────────────────────────────────────────────────────────────────────
        //
        // The interest view is about to claim every slot this tick reached, these included, so the difference would never offer them again and the client
        // would never be told about an entity the engine had decided to describe. The debt is the SLOTS, not the session's whole next frame: measured at
        // d06 with 200 sessions the blunt form forced a full walk on 40.8 % of frames and those frames did ~91 % of every slot read in the subsystem.
        //
        // Only the DECLINED tail is owed. Owing every candidate — which removes the need to know which were declined — makes the debt never settle, and a
        // view that always owes something never completes: FrameAssemblerTests.ViewCompleteIsSetWhenTheFillUnderTheEnterBudgetCompletes is the guard.
        if (deferred > 0 && carry)
        {
            for (var i = take; i < candidates.Length; i++)
            {
                ref readonly var declined = ref candidates[i];
                if (declined.ViewIndex >= 0)
                {
                    view.Owe(declined.ViewIndex, 1UL << declined.Slot);
                }
            }
        }

        return deferred;
    }

    /// <summary>Finds the entities this session knows that its hits did not reach: its leaves.</summary>
    private static int Sweep(SessionFrameState state, FrameWorkerScratch scratch, FrameIdentityScratch identities, ushort stamp, int accountedFor)
    {
        var known = state.Known;
        if (accountedFor >= known.KnownCount)
        {
            // Every entry the table holds was reached by a hit or is already leaving, so nothing can be missing. The steady state takes this branch, which
            // is what keeps a 10 000-entity view from walking its whole table every tick to discover that nobody left.
            return 0;
        }

        var emitted = 0;
        var enumerator = known.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var entry = enumerator.Current;
            if (entry->SeenStamp == stamp)
            {
                continue;
            }

            // Through the one-per-identity gate: ClassifyHit's stale branch retires an identity WITHOUT stamping its entry (the stamp is only written
            // for a current one), so the same entry reaches this walk unstamped and would be retired a second time. A sub-list with a repeated id is
            // refused by the encoder, which faults the stage and closes every session on the server.
            if (identities.WasEmitted(entry->NetId))
            {
                continue;
            }

            identities.NoteEmitted(entry->NetId);
            emitted++;
            scratch.Add(entry->Archetype, FrameListKind.Leave, new FrameRecord { NetId = entry->NetId, Archetype = entry->Archetype });
        }

        return emitted;
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
    /// <param name="state">The session.</param>
    /// <param name="scratch">The worker's scratch, holding the records the frame carried.</param>
    /// <param name="stamp">This tick, as the known-set stores it.</param>
    /// <param name="counters">The chunk's tally, which takes the enter and leave counts from the loops that are walking them anyway.</param>
    private void Commit(SessionFrameState state, FrameWorkerScratch scratch, ushort stamp, ref FrameCounters counters)
    {
        var known = state.Known;
        for (var a = 0; a < _plans.Length; a++)
        {
            var plan = _encodePlans[a];
            var enters = scratch.List(a, FrameListKind.Enter);
            counters.EntersEmitted += enters.Length;
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
            counters.LeavesEmitted += leaves.Length;
            for (var i = 0; i < leaves.Length; i++)
            {
                known.Remove(leaves[i].NetId);
            }
        }

        // Read AFTER the enters and leaves have been applied, so the pair describes what the client holds once this frame lands rather than what it held
        // before. The debt is the frame stage's own view of what it still owes; a session with no view owes nothing it can name.
        counters.KnownTotal += known.KnownCount;
        counters.OwedTotal += state.DeferredEnters;
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
            var shared = scratch.SharedRuns(a, true).Length + scratch.SharedRuns(a, false).Length;
            if (enters == 0 && segments == 0 && states == 0 && leaves == 0 && shared == 0)
            {
                continue;
            }

            var plan = _encodePlans[a];
            bound += EntitiesEncoder.MaxBlockOverheadBytes
                + (enters * plan.MaxEnterBytes)
                + (segments * plan.MaxSegmentBytes)
                + (states * plan.MaxStateBytes)
                + (leaves * EntitiesEncoder.MaxGapBytes)

                // One extra varu per referenced run, for the run's own record count — which is part of the bytes already counted below — plus the run
                // count itself, which MaxBlockOverheadBytes does not size for more than one run per sub-list.
                + (shared * EntitiesEncoder.MaxGapBytes);
        }

        // The referenced runs, whose length is known exactly rather than bounded: they are bytes some other pass already encoded.
        return bound + scratch.SharedBytes;
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

    /// <summary>Records that no frame was produced for a session this tick.</summary>
    /// <param name="state">The session's state.</param>
    /// <param name="counted">
    /// Whether this counts against <c>typhon.session.skippedFrames</c>. False for a tick that produced nothing because there was nothing to say.
    /// </param>
    /// <remarks>
    /// <b>An idle tick is not a skip, and the distinction is the metric's whole meaning.</b> A skip is back-pressure — K slots full, an acknowledgement too
    /// far behind, a pool that would not lend — and an operator reading a rising skip count is reading "this session cannot keep up". A world that had
    /// nothing to send is none of that, and counting it made every quiet session look like a struggling one. The baseline and the known-set are left alone
    /// either way, which is what SUB-03 requires of both.
    /// </remarks>
    private void NoteSkip(SessionFrameState state, bool counted = true)
    {
        if (BaselineAdvancesOnSkipForTest)
        {
            // The mutant: a baseline that moves on a tick whose frame was never produced. Every group stamped at or before it is then invisible to every
            // later frame, and the session diverges permanently — which is exactly what SUB-03's verifier has to catch.
            state.Baseline = _tick;
        }

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
