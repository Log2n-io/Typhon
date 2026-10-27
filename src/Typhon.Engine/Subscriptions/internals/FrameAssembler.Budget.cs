using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>What an owed record is (15 § 4.3).</summary>
internal static class OwedKind
{
    /// <summary>Cancelled or carried: no longer owed.</summary>
    public const byte None = 0;

    /// <summary>The session's committed geometry or keys hold the entity, and its client does not have it: it is owed a whole enter.</summary>
    public const byte Enter = 1;

    /// <summary>The client holds the entity, older than the groups (and the segment) the record names.</summary>
    public const byte Update = 2;
}

/// <summary>
/// One record a session's frames had no room for (15 § 4.3): the entity's slot, and what it is owed. The record carries no value — it is sent, when it
/// fits, from the slot's state as it is then, which is what makes a deferred change replaced by a newer one rather than queued behind it.
/// </summary>
internal struct OwedRecord
{
    public nint Block;

    /// <summary>The entity, to be located again when its slot no longer holds it — a migration while its update was deferred (SUB-22).</summary>
    public EntityId Entity;

    public uint NetId;

    public ushort Archetype;
    public byte Slot;

    /// <summary>An <see cref="OwedKind"/>.</summary>
    public byte Kind;

    /// <summary>An update's change groups.</summary>
    public byte Mask;

    /// <summary>An update's motion segment.</summary>
    public bool Segment;
}

internal sealed unsafe partial class FrameAssembler
{
    /// <summary>What <see cref="FitEntities"/> did with a frame's entity records.</summary>
    private enum FitOutcome
    {
        /// <summary>Everything fits and nothing was owed: the lists are untouched.</summary>
        Fits,

        /// <summary>The lists now fit; what did not is owed.</summary>
        Fitted,

        /// <summary>More is owed than the cap: the frame is abandoned and the next one is a <c>RESET</c> (15 § 4.3, C2).</summary>
        Overflow,
    }

    private long _owedOverflows;
    private long _owedLosts;
    private long _framesOwing;
    private int _owedLargestStage;
    private int _owedLargest;

    /// <summary>
    /// The largest owed set (15 § 4.3) a session's frame committed in the last frame stage: 0 when every session's frames fit, the god camera's backlog of
    /// records when its movers outrun its frame. A gauge, read off the tick.
    /// </summary>
    public int OwedLargest => Volatile.Read(ref _owedLargest);

    /// <summary>Frames the fit left owing records to the next ones: a frame budget that bound (15 § 4.4).</summary>
    public long FramesOwing => Interlocked.Read(ref _framesOwing);

    /// <summary>Frames abandoned for a <c>RESET</c> by the frame budget, whatever the cause: <see cref="OwedOverflows"/> + <see cref="OwedLosts"/>.</summary>
    public long OwedResets => OwedOverflows + OwedLosts;

    /// <summary>Frames abandoned for a <c>RESET</c> because they grew the owed set past its cap (15 § 4.3, C2).</summary>
    public long OwedOverflows => Interlocked.Read(ref _owedOverflows);

    /// <summary>Frames abandoned for a <c>RESET</c> because an owed entity was found nowhere and met by no record of the frame (15 § 10).</summary>
    public long OwedLosts => Interlocked.Read(ref _owedLosts);

    /// <summary>
    /// The largest frame <paramref name="session"/> may be sent (C10): its own limit under the engine's ceiling, and never below twice the largest enter
    /// record — the room <c>Start</c> guaranteed every record against the global one (15 § 4.7), so its oldest owed record always fits.
    /// </summary>
    private int FrameLimitOf(SessionId session)
    {
        var own = _sessions.FrameBytesOf(session);
        return own > 0 ? Math.Min(_maxFrameBytes, Math.Max(own, _minFrameLimit)) : _maxFrameBytes;
    }

    private static int LargestEnter(ArchetypeEncodePlan[] plans)
    {
        var largest = 0;
        foreach (var plan in plans)
        {
            largest = Math.Max(largest, plan.MaxEnterBytes);
        }

        return largest;
    }

    /// <summary>The bound of every block of a frame but its entity records (15 § 4.4): what the fit's target leaves for them.</summary>
    private int OtherBlocksBound(FrameWorkerScratch scratch, bool emitStats, int eventCount, int eventBytes, bool aggWrite, int aggRows,
        AggregateCounts aggCounts, bool debugWrite, int debugGeometry, in SelfDue self) =>
        (emitStats ? Stats.MaxBlockBytes : 0) + (eventCount > 0 ? eventBytes + 16 : 0) + (aggWrite ? 24 + (aggRows * 5 * (1 + aggCounts.ArchetypeCount)) : 0)
        + (debugWrite ? 16 + DebugGrid.MaxBytes + debugGeometry : 0) + SelfBound(in self) + RealmBlockBound + SourcesBound(scratch);

    /// <summary>
    /// The entity records' bound the encode buffer is sized from, header included: the worst case when it fits the frame, the meter otherwise (F5) — a wide
    /// archetype's worst case is a quarter of a frame a record.
    /// </summary>
    private int EntityBound(FrameWorkerScratch scratch, int limit, int metered)
    {
        var worst = UpperBound(scratch);
        return worst <= limit ? worst : Math.Min(worst, (metered >= 0 ? metered : MeteredBytes(scratch)) + EntitiesEncoder.MaxHeaderBytes);
    }

    private int CountRecords(FrameWorkerScratch scratch)
    {
        var records = 0;
        for (var a = 0; a < _plans.Length; a++)
        {
            for (var k = 0; k < 4; k++)
            {
                records += scratch.Count(a, (FrameListKind)k);
            }
        }

        return records;
    }

    private static bool OwesEnter(FrameWorkerScratch scratch) => scratch.PendingOwedEnters > 0;

    private long _meterViolations;
    private long _meterChecks;

    /// <summary>Tests only (<c>CheckFrameMeterForTest</c>): records that encoded longer than the meter bounded them.</summary>
    public long MeterViolations => Interlocked.Read(ref _meterViolations);

    /// <summary>Tests only (<c>CheckFrameMeterForTest</c>): records whose encoding was compared with their meter.</summary>
    public long MeterChecks => Interlocked.Read(ref _meterChecks);

    // Each record encoded alone, against its meter: a one-record block less an empty one is the record and the run header one record adds. Tests only, so
    // the buffer is the call's own: the workers run in parallel.
    private void CheckMeter(FrameWorkerScratch scratch, int archetype)
    {
        var plan = _encodePlans[archetype];
        var buffer = new byte[_maxFrameBytes + 256];
        var w = new WireWriter(buffer);
        EntitiesEncoder.WriteEntities(ref w, plan, [], [], [], []);
        var empty = w.Position;
        for (var k = 0; k < 4; k++)
        {
            var kind = (FrameListKind)k;
            foreach (ref readonly var r in scratch.List(archetype, kind))
            {
                var one = new ReadOnlySpan<FrameRecord>(in r);
                w = new WireWriter(buffer);
                EntitiesEncoder.WriteEntities(ref w, plan, kind == FrameListKind.Enter ? one : [], kind == FrameListKind.Segment ? one : [],
                    kind == FrameListKind.State ? one : [], kind == FrameListKind.Leave ? one : []);
                Interlocked.Increment(ref _meterChecks);
                if (w.Position - empty - 1 > RecordBytes(plan, kind, in r))
                {
                    Interlocked.Increment(ref _meterViolations);
                }
            }
        }
    }

    /// <summary>
    /// The byte allowance of a frame's cursor-driven steps (15 § 4.2): half the frame, less the enters the session is owed — so a fill that spilled stops
    /// until its overshoot has drained. Owed updates are not counted: they merge and may never drain while their entities keep changing, and a fill they
    /// held back would never complete. A RESET empties the owed set, and fills under the whole half.
    /// </summary>
    private int FillAllowanceOf(SessionFrameState state, int limit, bool reset) =>
        _options.FrameBudgetOffForTest ? int.MaxValue : Math.Max(0, (limit / 2) - (reset ? 0 : state.OwedEnterBytes));

    /// <summary>
    /// The fit (15 § 4.4): makes the frame's entity records take at most <paramref name="target"/> bytes, and owes what does not fit — leaves and the
    /// controlled entity's records always, then the enters earlier frames owed, oldest first, this frame's enters, the updates earlier frames owed, oldest
    /// first, and this frame's updates. Leaves
    /// <see cref="FrameWorkerScratch.PendingOwed"/> as the session's owed set once the frame is published.
    /// </summary>
    /// <param name="state">The session's frame state, whose committed owed set this frame starts from.</param>
    /// <param name="scratch">The worker's scratch: the sorted, de-duplicated lists, rewritten in place.</param>
    /// <param name="target">The bytes the entity records may take: the frame's limit less every other block's bound.</param>
    /// <param name="selfNetId">The controlled entity's identity, 0 for none: its records always go, because <c>SELF</c> names it.</param>
    /// <param name="reset">The frame is a RESET: the client's store is cleared, and so is what it was owed.</param>
    /// <param name="owedCap">
    /// The most enters the session may be owed — four of its profile's fill budgets: past it a <c>RESET</c> is the cheaper repair (15 § 4.3, C2). Owed
    /// updates are not capped: there is one per held entity at most, merged, and a RESET does not lower the rate they come at — it would only throw the fill
    /// away and start it again.
    /// </param>
    /// <param name="lost">An owed entity found nowhere — not in its slot, not by its identity — and met by no record of this frame: the next frame must
    /// RESET.</param>
    /// <param name="metered">The records' metered bytes when the fast path measured them, -1 otherwise: what sizes the encode buffer.</param>
    private FitOutcome FitEntities(SessionFrameState state, FrameWorkerScratch scratch, int target, uint selfNetId, bool reset, int owedCap, out bool lost,
        out int metered)
    {
        lost = false;
        metered = -1;
        scratch.PendingOwedCount = 0;
        scratch.PendingOwedEnters = 0;
        if (_options.FrameBudgetOffForTest)
        {
            return FitOutcome.Fits;
        }

        var owedIn = reset ? 0 : state.OwedCount;

        // The committed set's enters are its prefix (CommitOwed): [0, entersIn) enters, [entersIn, owedIn) updates.
        var entersIn = reset ? 0 : state.OwedEnters;
        if (owedIn == 0 && (UpperBound(scratch) - EntitiesEncoder.MaxHeaderBytes <= target || (metered = MeteredBytes(scratch)) <= target))
        {
            return FitOutcome.Fits;
        }

        metered = -1;

        // The committed owed set, copied to be decided on: what this frame carries or cancels is gone from it, what it adds is appended.
        scratch.ClearOwedTable(owedIn);
        Span<bool> owing = _plans.Length <= 256 ? stackalloc bool[_plans.Length] : new bool[_plans.Length];
        for (var i = 0; i < owedIn; i++)
        {
            scratch.FileOwed(scratch.AppendOwed(in state.Owed[i]));
            owing[state.Owed[i].Archetype] = true;
        }

        var spent = 0;
        for (var a = 0; a < _plans.Length; a++)
        {
            if (owing[a] || scratch.Count(a, FrameListKind.Enter) + scratch.Count(a, FrameListKind.Segment) + scratch.Count(a, FrameListKind.State)
                + scratch.Count(a, FrameListKind.Leave) > 0)
            {
                spent += EntitiesEncoder.MaxBlockOverheadBytes;
            }
        }

        // ── 1. Reconcile this frame's records with what is owed, and take the mandatory ones ─────────────────────────────────────────────────────────
        var removed = false;
        for (var a = 0; a < _plans.Length; a++)
        {
            var plan = _encodePlans[a];
            for (var k = 0; k < 4; k++)
            {
                var kind = (FrameListKind)k;
                var list = scratch.MutableList(a, kind);
                for (var i = 0; i < list.Length; i++)
                {
                    ref var r = ref list[i];
                    if (owedIn > 0)
                    {
                        var at = scratch.FindOwed(r.NetId);
                        if (at >= 0 && Reconcile(ref scratch.PendingOwed[at], ref r, kind))
                        {
                            removed = true;
                            continue;
                        }
                    }

                    if (kind == FrameListKind.Leave || (selfNetId != 0 && r.NetId == selfNetId))
                    {
                        spent += RecordBytes(plan, kind, in r);
                    }
                }
            }
        }

        // ── 2. The controlled entity's owed record, whatever its age ──────────────────────────────────────────────────────────────────────────────
        var full = false;
        var first = true;
        Span<bool> appended = _plans.Length <= 256 ? stackalloc bool[_plans.Length * 4] : new bool[_plans.Length * 4];

        // This frame's own records, before the owed ones carried are appended: what step 3 decides on.
        Span<int> own = _plans.Length <= 256 ? stackalloc int[_plans.Length * 4] : new int[_plans.Length * 4];
        for (var a = 0; a < _plans.Length; a++)
        {
            for (var k = 0; k < 4; k++)
            {
                own[(a * 4) + k] = scratch.Count(a, (FrameListKind)k);
            }
        }
        // The controlled entity's owed record first, whatever its age: SELF names it, so its client must hold it (11 § 2).
        var self = selfNetId != NetIdAllocator.NoNetId && owedIn > 0 ? scratch.FindOwed(selfNetId) : -1;
        if (self >= 0 && scratch.PendingOwed[self].Kind != OwedKind.None)
        {
            ref var e = ref scratch.PendingOwed[self];
            var plan = _encodePlans[e.Archetype];
            if (Located(plan, ref e))
            {
                spent += OwedBytesOf(plan, in e);
                Carry(scratch, in e, appended);
            }
            else
            {
                lost = true;
            }

            e.Kind = OwedKind.None;
        }

        // ── 3. Enters, then updates — what earlier frames owed first, oldest first, then this frame's own — while they fit; past the first that does not,
        // every one is owed. An entity its client lacks matters more than one it holds a little stale, and the enters are few — the fill takes at most half
        // a frame (FillAllowanceOf) — so the updates keep most of the frame. Taken in plain age order, a world of movers owed more updates than a frame
        // carries, every enter waited behind them, and the enters piled up past the cap (the god camera's RESET loop).
        //
        // While updates are owed, the enters take at most half of what the mandatory records left (the first owed enter always goes): a fill takes half the
        // frame's LIMIT, and the events up to the other half, so a session filling under a stream of events — a player riding into a fight, a god camera
        // over a battle — would otherwise carry its enters and owe every update, every frame, and the entities it holds would freeze.
        var entersTarget = owedIn > entersIn ? spent + ((target - spent) / 2) : target;
        var entersFull = false;
        CarryOwed(scratch, OwedKind.Enter, 0, entersIn, entersTarget, appended, ref spent, ref first, ref entersFull, ref lost);
        removed |= Spill(scratch, FrameListKind.Enter, own, selfNetId, entersTarget, ref spent, ref first, ref entersFull);
        CarryOwed(scratch, OwedKind.Update, entersIn, owedIn, target, appended, ref spent, ref first, ref full, ref lost);

        // What is still owed keeps its order within its kind: the enters earlier frames owed, this frame's spilled enters, the updates earlier frames owed —
        // and this frame's spilled updates after them. The enters stay the prefix, so what scans for them scans only them.
        var spare = scratch.OwedSpare;
        if (spare.Length < scratch.PendingOwedCount)
        {
            spare = new OwedRecord[scratch.PendingOwed.Length];
        }

        var kept = 0;
        for (var i = 0; i < entersIn; i++)
        {
            if (scratch.PendingOwed[i].Kind != OwedKind.None)
            {
                spare[kept++] = scratch.PendingOwed[i];
            }
        }

        for (var i = owedIn; i < scratch.PendingOwedCount; i++)
        {
            if (scratch.PendingOwed[i].Kind != OwedKind.None)
            {
                spare[kept++] = scratch.PendingOwed[i];
            }
        }

        scratch.PendingOwedEnters = kept;
        for (var i = entersIn; i < owedIn; i++)
        {
            if (scratch.PendingOwed[i].Kind != OwedKind.None)
            {
                spare[kept++] = scratch.PendingOwed[i];
            }
        }

        scratch.OwedSpare = scratch.PendingOwed;
        scratch.PendingOwed = spare;
        scratch.PendingOwedCount = kept;

        // The table now files only the updates this frame owes, so its segment and state records merge into one. No record left can meet a kept entry: the
        // reconcile took out every record of an entity owed when the frame began.
        scratch.ClearOwedTable(64, kept);

        removed |= Spill(scratch, FrameListKind.Segment, own, selfNetId, target, ref spent, ref first, ref full);
        removed |= Spill(scratch, FrameListKind.State, own, selfNetId, target, ref spent, ref first, ref full);

        // The lists without what was taken out, and re-sorted where owed records were appended to them.
        for (var a = 0; a < _plans.Length; a++)
        {
            for (var k = 0; k < 4; k++)
            {
                var kind = (FrameListKind)k;
                if (removed)
                {
                    Compact(scratch, a, kind);
                }

                if (appended[(a * 4) + k])
                {
                    scratch.SortByNetId(a, kind);
                }
            }
        }

        if (scratch.PendingOwedCount > 0)
        {
            Interlocked.Increment(ref _framesOwing);
        }

        // An overflow is a frame that GROWS the owed enters past the cap, and never a RESET's: a RESET is the overflow's repair, and a cell heavier than the
        // cap — delivered whole — owes more than it on the RESET and the frames that drain it, which would otherwise RESET for ever. Owed updates are not
        // counted (owedCap): a world whose movers outrun the frame is carried round robin, not refilled from nothing every few seconds.
        if (reset || scratch.PendingOwedCount <= owedCap)
        {
            return FitOutcome.Fitted;
        }

        var enters = scratch.PendingOwedEnters;
        return enters > owedCap && enters > entersIn ? FitOutcome.Overflow : FitOutcome.Fitted;
    }

    /// <summary>
    /// Commits a fitted frame's owed set as the session's: the pending and committed arrays trade places, so neither is copied. The enters — the set's
    /// prefix — are metered again, a reconcile may have moved their slot, for the allowance the next frames give up; the updates are not, nothing reads
    /// their bytes, and a world of them is a cache miss each.
    /// </summary>
    private void CommitOwed(SessionFrameState state, FrameWorkerScratch scratch)
    {
        var committed = state.Owed;
        state.Owed = scratch.PendingOwed;
        scratch.PendingOwed = committed.Length > 0 ? committed : new OwedRecord[16];
        state.OwedCount = scratch.PendingOwedCount;
        state.OwedEnters = scratch.PendingOwedEnters;
        var enterBytes = 0;
        for (var i = 0; i < state.OwedEnters; i++)
        {
            ref var e = ref state.Owed[i];
            enterBytes += OwedBytesOf(_encodePlans[e.Archetype], in e);
        }

        state.OwedEnterBytes = enterBytes;
        scratch.PendingOwedCount = 0;
        scratch.PendingOwedEnters = 0;
    }

    /// <summary>A RESET was published: the client's store is empty, and nothing is owed into it.</summary>
    private static void ClearOwed(SessionFrameState state)
    {
        state.OwedCount = 0;
        state.OwedEnters = 0;
        state.OwedEnterBytes = 0;
    }

    // The frame's records, metered: the encoder's block overhead per archetype with records, and each record's bound (15 § 4.1).
    private int MeteredBytes(FrameWorkerScratch scratch)
    {
        var bytes = 0;
        for (var a = 0; a < _plans.Length; a++)
        {
            var plan = _encodePlans[a];
            var any = false;
            for (var k = 0; k < 4; k++)
            {
                var kind = (FrameListKind)k;
                var list = scratch.List(a, kind);
                if (list.Length == 0)
                {
                    continue;
                }

                any = true;
                if (kind == FrameListKind.Leave || kind == FrameListKind.Segment || (kind == FrameListKind.Enter && !plan.HasWide))
                {
                    bytes += list.Length * RecordBytes(plan, kind, in list[0]);
                    continue;
                }

                foreach (ref readonly var r in list)
                {
                    bytes += RecordBytes(plan, kind, in r);
                }
            }

            bytes += any ? EntitiesEncoder.MaxBlockOverheadBytes : 0;
        }

        return bytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RecordBytes(ArchetypeEncodePlan plan, FrameListKind kind, in FrameRecord r) => kind switch
    {
        FrameListKind.Enter => plan.EnterBytes(r.Block, r.Slot),
        FrameListKind.Segment => plan.MaxSegmentBytes,
        FrameListKind.State => plan.StateBytes(r.Block, r.Slot, r.GroupMask),
        _ => EntitiesEncoder.MaxGapBytes,
    };

    private static int OwedBytesOf(ArchetypeEncodePlan plan, in OwedRecord e) => e.Kind == OwedKind.Enter
        ? plan.EnterBytes(e.Block, e.Slot)
        : (e.Segment ? plan.MaxSegmentBytes : 0) + (e.Mask != 0 ? plan.StateBytes(e.Block, e.Slot, e.Mask) : 0);

    /// <summary>
    /// One record of this frame met an owed one for its entity (15 § 4.4, step 1). Returns whether the record is taken out of the frame: the entity's
    /// owed record now speaks for it, carrying the slot this frame's record named.
    /// </summary>
    private static bool Reconcile(ref OwedRecord e, ref FrameRecord r, FrameListKind kind)
    {
        if (e.Kind == OwedKind.None)
        {
            return false;
        }

        if (kind == FrameListKind.Enter)
        {
            // The entity is entered whole by this frame: nothing more is owed for it.
            e.Kind = OwedKind.None;
            return false;
        }

        if (kind == FrameListKind.Leave)
        {
            // The client never had an owed enter's entity, so it is not told it left; an owed update's entity simply leaves.
            var never = e.Kind == OwedKind.Enter;
            e.Kind = OwedKind.None;
            if (never)
            {
                r.NetId = NetIdAllocator.NoNetId;
            }

            return never;
        }

        // A change of an entity already owed: the owed record goes when it fits, with the state as it is then. An enter carries every group.
        e.Block = r.Block;
        e.Slot = r.Slot;
        if (e.Kind == OwedKind.Update)
        {
            if (kind == FrameListKind.Segment)
            {
                e.Segment = true;
            }
            else
            {
                e.Mask |= r.GroupMask;
            }
        }

        r.NetId = NetIdAllocator.NoNetId;
        return true;
    }

    /// <summary>
    /// Whether an owed record's entity can be sent: its slot still holds it, or — when the entity migrated while no record of it was due (a far band's
    /// deferred update) — its identity locates it, and the record moves to its slot. False only for an entity gone, or no longer this identity.
    /// </summary>
    private bool Located(ArchetypeEncodePlan plan, ref OwedRecord e)
    {
        if (SlotHolds(plan, in e))
        {
            return true;
        }

        if (e.Entity.IsNull || Push == null || Engine == null || e.Entity.ArchetypeId >= Engine._stateByRouting.Length)
        {
            return false;
        }

        using var epoch = EpochGuard.Enter(Engine.EpochManager);
        var locator = new EntityLocator(Engine);
        try
        {
            if (!locator.TryLocate(e.Entity, out var chunk, out var slot)
                || !Push.TryReplicaAt(Engine._stateByRouting[e.Entity.ArchetypeId].ClusterState, chunk, slot, e.Entity, out var archetype, out var block,
                    out var netId)
                || netId != e.NetId || archetype != e.Archetype)
            {
                return false;
            }

            e.Block = block;
            e.Slot = (byte)slot;
            return true;
        }
        finally
        {
            locator.Dispose();
        }
    }

    // Whether an owed record's slot still holds its entity: the block is live, the slot occupied, and its identity is the record's.
    private static bool SlotHolds(ArchetypeEncodePlan plan, in OwedRecord e)
    {
        var header = (ReplicationBlockHeader*)e.Block;
        return header->PoolState != ReplicationBlockPool.PoolStateFree && ((header->ProjectedOccupancy >> e.Slot) & 1UL) != 0
            && ((ReplicationHotEntry*)plan.Hot(e.Block, e.Slot))->NetId == e.NetId;
    }

    // An owed record, put back into this frame's lists: an enter, or an update's segment and state records.
    private static void Carry(FrameWorkerScratch scratch, in OwedRecord e, Span<bool> appended)
    {
        var record = new FrameRecord { NetId = e.NetId, Block = e.Block, Slot = e.Slot, Archetype = e.Archetype };
        if (e.Kind == OwedKind.Enter)
        {
            scratch.Add(e.Archetype, FrameListKind.Enter, in record);
            appended[(e.Archetype * 4) + (int)FrameListKind.Enter] = true;
            return;
        }

        if (e.Segment)
        {
            scratch.Add(e.Archetype, FrameListKind.Segment, in record);
            appended[(e.Archetype * 4) + (int)FrameListKind.Segment] = true;
        }

        if (e.Mask != 0)
        {
            record.GroupMask = e.Mask;
            scratch.Add(e.Archetype, FrameListKind.State, in record);
            appended[(e.Archetype * 4) + (int)FrameListKind.State] = true;
        }
    }

    /// <summary>
    /// Carries what earlier frames owed of one kind, oldest first, while it fits (15 § 4.4, step 2): the first record of the frame always goes, so the oldest
    /// owed one is never starved by the mandatory ones.
    /// </summary>
    private void CarryOwed(FrameWorkerScratch scratch, byte kind, int from, int to, int target, Span<bool> appended, ref int spent, ref bool first,
        ref bool full, ref bool lost)
    {
        for (var i = from; i < to && !full; i++)
        {
            ref var e = ref scratch.PendingOwed[i];
            if (e.Kind != kind)
            {
                continue;
            }

            var plan = _encodePlans[e.Archetype];
            if (!Located(plan, ref e))
            {
                // Neither its slot nor its identity holds the entity, and no record of this frame said where it went. Nothing can be sent for it, and the
                // client and the geometry would disagree about it from here on: the next frame is a RESET (counted).
                lost = true;
                e.Kind = OwedKind.None;
                continue;
            }

            var bytes = OwedBytesOf(plan, in e);
            if (!first && spent + bytes > target)
            {
                full = true;
                return;
            }

            first = false;
            spent += bytes;
            Carry(scratch, in e, appended);
            e.Kind = OwedKind.None;
        }
    }

    /// <summary>
    /// Takes one kind of this frame's records while they fit, and owes the rest (15 § 4.4, steps 3–4): an update as the groups it carried, merged with the
    /// entity's other record, an enter whole. The controlled entity's records were taken already, and are skipped.
    /// </summary>
    private bool Spill(FrameWorkerScratch scratch, FrameListKind kind, ReadOnlySpan<int> own, uint selfNetId, int target, ref int spent, ref bool first,
        ref bool full)
    {
        var removed = false;
        for (var a = 0; a < _plans.Length; a++)
        {
            var plan = _encodePlans[a];
            var list = scratch.MutableList(a, kind)[..own[(a * 4) + (int)kind]];
            for (var i = 0; i < list.Length; i++)
            {
                ref var r = ref list[i];
                if (r.NetId == NetIdAllocator.NoNetId || (selfNetId != 0 && r.NetId == selfNetId))
                {
                    continue;
                }

                var bytes = RecordBytes(plan, kind, in r);
                if (!full && spent + bytes <= target)
                {
                    spent += bytes;
                    first = false;
                    continue;
                }

                full = true;
                Owe(scratch, plan, kind, in r);
                r.NetId = NetIdAllocator.NoNetId;
                removed = true;
            }
        }

        return removed;
    }

    private static void Owe(FrameWorkerScratch scratch, ArchetypeEncodePlan plan, FrameListKind kind, in FrameRecord r)
    {
        var at = scratch.FindOwed(r.NetId);
        if (at >= 0 && kind != FrameListKind.Enter)
        {
            // The entity's other update record was owed already: one record, both parts.
            ref var e = ref scratch.PendingOwed[at];
            e.Segment |= kind == FrameListKind.Segment;
            e.Mask |= kind == FrameListKind.State ? r.GroupMask : (byte)0;
            return;
        }

        var record = new OwedRecord
        {
            Block = r.Block,
            Entity = ((ReplicationHotEntry*)plan.Hot(r.Block, r.Slot))->Entity,
            NetId = r.NetId,
            Archetype = r.Archetype,
            Slot = r.Slot,
            Kind = kind == FrameListKind.Enter ? OwedKind.Enter : OwedKind.Update,
            Mask = kind == FrameListKind.State ? r.GroupMask : (byte)0,
            Segment = kind == FrameListKind.Segment,
        };
        scratch.FileOwed(scratch.AppendOwed(in record));
    }

    // Squeezes the records taken out (netId 0) from one sub-list, keeping the order of the rest.
    private static void Compact(FrameWorkerScratch scratch, int archetype, FrameListKind kind)
    {
        var list = scratch.MutableList(archetype, kind);
        var write = 0;
        for (var read = 0; read < list.Length; read++)
        {
            if (list[read].NetId != NetIdAllocator.NoNetId)
            {
                list[write++] = list[read];
            }
        }

        scratch.Truncate(archetype, kind, write);
    }
}
