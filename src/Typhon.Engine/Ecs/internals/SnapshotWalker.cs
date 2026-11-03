using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.Internals;
using static Typhon.Engine.Internals.SnapshotSlot;

namespace Typhon.Engine;

/// <summary>
/// The engine behind <see cref="ArchetypeWalk{TArch}"/>: a walk of one archetype at a transaction's snapshot, one batch per step, that holds a bounded
/// window of pages and — between steps — no epoch pin.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two paths, chosen by whether the fence can move an entity (WALK-01).</b> Every migration — a cell crossing, a relocation, a repair — is queued
/// against a spatial grid, so only a spatial archetype's entities change cluster. Elsewhere the walk reads the active cluster list in storage order: the
/// fence only compacts it (the reclamation of emptied clusters) and reissues freed chunk ids, which a bitmap of visited chunk ids and the list's
/// <see cref="ArchetypeClusterState.ReclaimGeneration"/> absorb. On a spatial archetype a cluster-order walk spanning a fence could meet an entity twice or
/// never, so it walks the entity map instead — an entity keeps its key and its bucket wherever it moves (EMAP-02) — and gathers each batch's columns from
/// the clusters the records name, resolved again whenever a fence ran since.
/// </para>
/// <para>
/// <b>Fences.</b> A step never starts while a fence runs on another thread, and a step a fence overlapped is read again (<see cref="ExclusiveWindow"/>'s
/// seqcount): the reclamation compacts the list in place and frees chunks, which a read that straddles it can see half done.
/// </para>
/// <para>
/// <b>The epoch.</b> A step re-enters the transaction's scope at the current epoch, reads, and leaves it again
/// (<see cref="EntityAccessor.TrySuspendEpochAtWalkStep"/>): what the batch hands out lives in the walk's buffers or on pages its scan accessors hold by slot
/// reference, never on the epoch. A column read later in the step that needs a page — a Versioned chain — re-enters the scope for its own length.
/// </para>
/// </remarks>
[MustDispose(Reason = "owns scan accessors over the cluster and entity-map segments, and native column buffers: until disposed their pages stay pinned")]
internal sealed unsafe class SnapshotWalker
{
    /// <summary>Runs between a cluster step's read and its fence validation (tests: a fence overlapping a step).</summary>
    internal static Action AfterReadProbeForTest;

    /// <summary>Cluster steps read again because a fence overlapped them (tests).</summary>
    internal static int RedosForTest;

    /// <summary>Mutant seam (WALK-01): the walk keeps its list position across a reclamation.</summary>
    internal static bool IgnoreReclamationForTest;

    /// <summary>Mutant seam (WALK-01): a spatial archetype walked in cluster order, across the fence's migrations.</summary>
    internal static bool ForceClusterPathForTest;

    /// <summary>Mutant seam (WALK-02): a cluster-filtered walk reads only the index's candidates, not the clusters changed above its snapshot.</summary>
    internal static bool IgnoreChangedClustersForTest;

    /// <summary>Clusters read on this thread, all walks (tests).</summary>
    [ThreadStatic]
    internal static int ClustersReadForTest;

    /// <summary>Entities per batch on the gather path: one cluster's worth, so both paths hand out the same shape.</summary>
    private const int GatherBatch = 64;

    private readonly Transaction _tx;
    private readonly ArchetypeMetadata _meta;
    private readonly ArchetypeEngineState _es;
    private readonly ArchetypeClusterState _cs;
    private readonly ArchetypeClusterInfo _layout;
    private readonly ExclusiveWindow _window;
    private readonly CommitCardTable _cards;
    private readonly ushort _routingId;
    private readonly long _tsn;
    private readonly bool _gather;
    private readonly bool _visGated;

    private ChunkAccessor<PersistentStore> _clusterAccessor;
    private ChunkAccessor<TransientStore> _transientAccessor;
    private ChunkAccessor<PersistentStore> _mapAccessor;
    private readonly bool _hasPersistent;
    private readonly bool _hasTransient;
    private byte* _record;
    private OwnDestroyFilter _ownDestroys;
    private long _refreshedAt;
    private bool _disposed;

    /// <summary>Bumped by every step and by <see cref="Dispose"/>: a <see cref="WalkBatch{TArch}"/> from an earlier step refuses to read.</summary>
    internal int Step;

    /// <summary>The current batch's slots.</summary>
    internal ulong Rows;

    /// <summary>
    /// The current cluster's rows read from a reserved slot — claimed and not occupied: a destroy hidden until its cleanup, which the snapshot still sees
    /// (VIS-09). Their enabled state is the record's, not the cluster's words. Always 0 on the gather path.
    /// </summary>
    internal ulong ReservedRows;

    /// <summary>
    /// Restricts the cluster path to the clusters with a non-zero word here, indexed by chunk id — a field query's candidates from its index (the bitmap
    /// <c>CollectClusterLocationsFromBTree</c> fills) — and to any cluster a change above the snapshot touched, whose entries the tree may no longer hold at
    /// the keys this snapshot sees (#1347, VIS-12). Null walks every cluster. Ignored on the gather path.
    /// </summary>
    internal ulong[] CandidateClusters;

    /// <summary>
    /// With no <see cref="CandidateClusters"/>, restricts the cluster path to the clusters this admits — a field query's zone maps — and to any cluster a
    /// change above the snapshot touched, whose values the maps, re-tightened from the HEAD at the fence, may no longer cover (VIS-12). Read at each
    /// cluster, as the SoA scan reads its zone maps. Null walks every cluster.
    /// </summary>
    internal Func<int, bool> ClusterFilter;

    // Versioned columns read at the snapshot, one native buffer per component slot, and which of them this step filled.
    private readonly nint[] _columns;
    private readonly ulong[] _present;   // per Versioned slot, the rows its fill found a value for, this step
    private uint _filled;

    // ── The cluster path ─────────────────────────────────────────────────
    private int _index;
    private int _reclaimGeneration;
    private ulong[] _visited = [];
    private ulong[] _startClusters = [];   // the clusters listed when the walk began: the walk is bounded to them
    private int _chunkId;
    private byte* _base;            // the primary base: the occupancy word and the entity ids
    private byte* _transientBase;   // the Transient segment's base, on an archetype that has both

    // ── The gather path ──────────────────────────────────────────────────
    private RawValuePagedHashMap<long, PersistentStore>.EntryCursor _cursor;
    private long[] _queueKeys;
    private int[] _queueChunks;
    private byte[] _queueSlots;
    private int _queueCount;
    private int _queueIndex;
    private int _queueToken;
    private readonly long[] _batchKeys = new long[GatherBatch];
    private readonly int[] _batchChunks = new int[GatherBatch];
    private readonly byte[] _batchSlots = new byte[GatherBatch];
    private int _batchCount;
    private int _batchToken;
    private long* _batchIds;

    internal SnapshotWalker(Transaction tx, ArchetypeMetadata meta, ArchetypeEngineState engineState)
    {
        _tx = tx;
        _meta = meta;
        _es = engineState;
        _cs = engineState.ClusterState;
        _layout = _cs.Layout;
        _window = tx.DBE.EpochManager.FenceWindow;
        _cards = tx.DBE.CommitCards;
        _routingId = tx.DBE.RoutingIdOf(meta);
        _tsn = tx.TSN;
        _gather = _cs.SpatialSlot.HasSpatialIndex && !ForceClusterPathForTest;
        _visGated = meta.VersionedSlotMask != 0;
        _columns = new nint[meta.ComponentCount];
        _present = new ulong[meta.ComponentCount];
        _ownDestroys = new OwnDestroyFilter(tx, meta);
        _refreshedAt = Stopwatch.GetTimestamp();
        _reclaimGeneration = Volatile.Read(ref _cs.ReclaimGeneration);
        // native-alloc: per-walk scratch of one record, freed in Dispose: a resource-tree block per walk would cost a registration on every walk
        _record = (byte*)NativeMemory.Alloc((nuint)meta._entityRecordSize);

        try
        {
            // Scan accessors throughout: what a walk holds is a window, not every page it read (EP-02).
            _hasPersistent = _cs.ClusterSegment != null;
            if (_hasPersistent)
            {
                _clusterAccessor = _cs.ClusterSegment.CreateScanAccessor();
            }

            _hasTransient = _cs.TransientSegment != null;
            if (_hasTransient)
            {
                _transientAccessor = _cs.TransientSegment.CreateChunkAccessor();
            }

            _mapAccessor = engineState.EntityMap.Segment.CreateScanAccessor();
            if (_gather)
            {
                // native-alloc: per-walk scratch, 64 ids handed out as a span, freed in Dispose
                _batchIds = (long*)NativeMemory.AllocZeroed(GatherBatch * sizeof(long));
            }
            else
            {
                // A cluster listed after this point holds only entities spawned after the walk began, which its snapshot does not see on a Versioned
                // archetype and need not on another — the reasoning ScanClusterSoa's frozen list rests on. Bounded to these, a walk beside a steady
                // appender ends.
                var ids = _cs.ReadActiveClusterList(out var count);
                for (var i = 0; i < count; i++)
                {
                    SetBit(ref _startClusters, ids[i]);
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Steps
    // ═══════════════════════════════════════════════════════════════════════

    internal bool MoveNext()
    {
        if (_disposed)
        {
            return false;
        }

        CheckLive();
        Step++;
        Rows = 0;
        ReservedRows = 0;
        _filled = 0;

        // The fence is waited out before the scope is re-entered: a fence under back-pressure must not wait on this thread's pin while this thread waits on
        // it. Then the scope comes back at the current epoch — a step is a call boundary (EP-03) — and the epoch moves on the walk's own clock, as a long
        // cluster walk's does: a suspension does not advance the global epoch, and on an engine no other thread advances, the pages the walk tagged would
        // otherwise stay protected for its whole length.
        WaitOutFence();
        _tx.ResumeForWalkStep();
        if (!_tx.EpochSuspended)
        {
            _tx.YieldDuringWalk(ref _refreshedAt);
        }

        var found = _gather ? NextGatherBatch() : NextCluster();
        if (found)
        {
            _tx.TrySuspendEpochAtWalkStep();
        }

        return found;
    }

    /// <summary>The next cluster in storage order holding an entity this walk yields; false at the end of the list.</summary>
    private bool NextCluster()
    {
        PagedMMF.RefreshableWaitDepth++;   // the walk holds only its scan accessors' pins: a back-pressure wait may move the epoch (#1230)
        try
        {
            while (true)
            {
                WaitOutFence();
                var token = _window.ReadToken();

                // The reclamation compacted the list: positions moved. The visited chunk ids say which clusters were read; start over through them.
                var generation = Volatile.Read(ref _cs.ReclaimGeneration);
                if (generation != _reclaimGeneration && !IgnoreReclamationForTest)
                {
                    _reclaimGeneration = generation;
                    _index = 0;
                }

                // CLUSTERWALK-02: count first, then the array, through the one reader.
                var ids = _cs.ReadActiveClusterList(out var count);
                var i = _index;
                while (i < count && (IsVisited(ids[i]) || !TestBit(_startClusters, ids[i]) || !IsCandidate(ids[i])))
                {
                    i++;
                }

                if (i >= count)
                {
                    if (_window.ReadOverlapped(token))
                    {
                        continue;
                    }

                    _index = i;
                    return false;
                }

                var chunkId = ids[i];
                var rows = ReadRows(chunkId, out var clusterBase, out var transientBase);
                ClustersReadForTest++;
                AfterReadProbeForTest?.Invoke();

                // A fence overlapped the read: the list may have been compacted under it and the chunk freed. Read this position again.
                if (_window.ReadOverlapped(token))
                {
                    RedosForTest++;
                    continue;
                }

                MarkVisited(chunkId);
                _index = i + 1;
                if (rows == 0)
                {
                    continue;
                }

                _chunkId = chunkId;
                _base = clusterBase;
                _transientBase = transientBase;
                Rows = rows;
                return true;
            }
        }
        finally
        {
            PagedMMF.RefreshableWaitDepth--;
        }
    }

    /// <summary>
    /// Whether <see cref="CandidateClusters"/> admits cluster <paramref name="chunkId"/>: a candidate of the index, or a cluster where a destroy is pending,
    /// a death or an update landed above the snapshot — read from the cluster's words, no page.
    /// </summary>
    private bool IsCandidate(int chunkId)
    {
        var candidates = CandidateClusters;
        if (candidates != null)
        {
            if ((uint)chunkId < (uint)candidates.Length && candidates[chunkId] != 0)
            {
                return true;
            }
        }
        else if (ClusterFilter == null || ClusterFilter(chunkId))
        {
            return true;
        }

        if (IgnoreChangedClustersForTest)
        {
            return false;
        }

        // The zone map's loads before the watermarks' re-read, on arm64 too (load-load); nothing on x64. The tree's are ordered by the caller's barrier.
        Volatile.ReadBarrier();
        ref var words = ref ArchetypeClusterState.SlotWords(_cs.SlotWordTable, chunkId);
        return ArchetypeClusterState.PendingDeathsOf(ref words) != 0 || ArchetypeClusterState.MaxDiedOf(ref words) > _tsn
               || ArchetypeClusterState.MaxUpdatedOf(ref words) > _tsn;
    }

    /// <summary>
    /// The slots of cluster <paramref name="chunkId"/> whose entity this walk yields: visible at the snapshot on an archetype with a Versioned component,
    /// decided as the field query's SoA scan decides it (VIS-08, VIS-09); the occupancy otherwise; minus the transaction's own pending destroys
    /// (CLUSTERWALK-04).
    /// </summary>
    private ulong ReadRows(int chunkId, out byte* clusterBase, out byte* transientBase)
    {
        clusterBase = _hasPersistent ? _clusterAccessor.GetChunkAddress(chunkId) : _transientAccessor.GetChunkAddress(chunkId);
        transientBase = _hasPersistent && _hasTransient ? _transientAccessor.GetChunkAddress(chunkId) : null;

        if (!_visGated)
        {
            var raw = _ownDestroys.ReadOccupancy(chunkId, clusterBase, _cs, out var pendingDeaths);
            return _ownDestroys.Apply(chunkId, raw, clusterBase, _cs, pendingDeaths);
        }

        // The pending destroys BEFORE the occupancy word (VIS-09), the claims after it, as ScanClusterSoa reads them.
        ref var slotWords = ref ArchetypeClusterState.SlotWords(_cs.SlotWordTable, chunkId);
        var pendingDeathsHere = ArchetypeClusterState.PendingDeathsOf(ref slotWords);
        var occupancy = Volatile.Read(ref *(ulong*)clusterBase);
        if (Unsafe.IsNullRef(ref slotWords))
        {
            slotWords = ref ArchetypeClusterState.SlotWords(_cs.SlotWordTable, chunkId);
        }

        var reserved = ArchetypeClusterState.ClaimOf(ref slotWords) & ~occupancy;
        var candidates = occupancy | reserved;
        if (candidates == 0)
        {
            return 0;
        }

        // Cluster-granularity visibility: every entity committed before the snapshot and none dying, which is almost every cluster in steady state.
        var gated = pendingDeathsHere != 0 || !ArchetypeClusterState.WatermarksVisibleAt(ref slotWords, _tsn);
        var ids = clusterBase + _layout.EntityIdsOffset;
        var recordSize = _meta._entityRecordSize;
        var rows = 0UL;
        for (var bits = candidates; bits != 0; bits &= bits - 1)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            var rawId = Volatile.Read(ref *(long*)(ids + slot * 8));
            var visible = (reserved & (1UL << slot)) != 0
                ? IsReservedSpawnVisible(rawId, chunkId, slot, _es, _record, _tsn, _cards, ref _mapAccessor)
                : IsVisibleAtSnapshot(rawId, gated, _es, _record, recordSize, _tsn, _cards, ref _mapAccessor);
            if (visible)
            {
                rows |= 1UL << slot;
            }
        }

        // Committed destroys are decided by the snapshot above; only the transaction's own remain.
        rows = _ownDestroys.Apply(chunkId, rows, clusterBase, _cs, 0);
        ReservedRows = rows & reserved;
        return rows;
    }

    /// <summary>The next batch of up to 64 entities in entity-map order; false once the map is walked.</summary>
    private bool NextGatherBatch()
    {
        var destroys = _tx.OwnDestroyCount > 0 ? _tx.PendingDestroys : null;
        var n = 0;
        while (n < GatherBatch)
        {
            if (_queueIndex >= _queueCount && !RefillQueue())
            {
                break;
            }

            // A batch is one fill's entities: their locations were read at one token.
            if (n > 0 && _queueToken != _batchToken)
            {
                break;
            }

            _batchToken = _queueToken;
            var key = _queueKeys[_queueIndex];
            var chunk = _queueChunks[_queueIndex];
            var slot = _queueSlots[_queueIndex];
            _queueIndex++;

            // A destroy this transaction made after the fill.
            if (destroys != null && destroys.Contains(new EntityId(key, _routingId)))
            {
                continue;
            }

            _batchKeys[n] = key;
            _batchChunks[n] = chunk;
            _batchSlots[n] = slot;
            _batchIds[n] = (long)new EntityId(key, _routingId).RawValue;
            n++;
        }

        _batchCount = n;
        if (n == 0)
        {
            return false;
        }

        Rows = n == 64 ? ulong.MaxValue : (1UL << n) - 1;
        return true;
    }

    /// <summary>Reads the next stretch of the entity map into the queue: the keys visible at the snapshot and where their records place them.</summary>
    private bool RefillQueue()
    {
        _queueIndex = 0;
        _queueCount = 0;
        while (!_cursor.Finished)
        {
            WaitOutFence();
            var token = _window.ReadToken();
            if (_queueKeys == null)
            {
                _queueKeys = ArrayPool<long>.Shared.Rent(GatherBatch * 2);
                _queueChunks = ArrayPool<int>.Shared.Rent(GatherBatch * 2);
                _queueSlots = ArrayPool<byte>.Shared.Rent(GatherBatch * 2);
            }

            var action = new GatherKeyAction
            {
                TxTsn = _tsn,
                Cards = _cards,
                Map = _es.EntityMap,
                RoutingId = _routingId,
                PendingDestroys = _tx.OwnDestroyCount > 0 ? _tx.PendingDestroys : null,
                Keys = _queueKeys,
                Chunks = _queueChunks,
                Slots = _queueSlots,
            };
            PagedMMF.RefreshableWaitDepth++;
            try
            {
                _es.EntityMap.FillFrom(ref _mapAccessor, ref _cursor, ref action, GatherBatch);
            }
            finally
            {
                PagedMMF.RefreshableWaitDepth--;
                _queueKeys = action.Keys;
                _queueChunks = action.Chunks;
                _queueSlots = action.Slots;
            }

            _queueCount = action.Count;

            // Locations read while a fence ran may be half migrated: marked stale, so the gather resolves them again. Visibility does not move with them.
            _queueToken = _window.ReadOverlapped(token) ? token - 1 : token;
            if (_queueCount > 0)
            {
                return true;
            }
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Columns
    // ═══════════════════════════════════════════════════════════════════════

    internal ReadOnlySpan<long> EntityIds() =>
        _gather ? new ReadOnlySpan<long>(_batchIds, GatherBatch) : new ReadOnlySpan<long>(_base + _layout.EntityIdsOffset, _layout.ClusterSize);

    /// <summary>On the cluster path, the base of the current cluster's primary chunk: its occupancy and enabled words, its entity ids.</summary>
    internal byte* ClusterBase => _base;

    /// <summary>The walk's entity-map scan accessor, for a caller's record reads between steps on the walking thread.</summary>
    internal ref ChunkAccessor<PersistentStore> MapAccessor => ref _mapAccessor;

    /// <summary>The walk's one-record scratch, the archetype's record size, for a caller's record reads between steps.</summary>
    internal byte* RecordBuffer => _record;

    internal ReadOnlySpan<T> Column<T>(int componentTypeId) where T : unmanaged
    {
        CheckLive();
        var slot = _meta.GetSlot(componentTypeId);
        var size = _layout.ComponentSize(slot);
        if (sizeof(T) != size)
        {
            ThrowHelper.ThrowInvalidOp($"Component at slot {slot} has a column stride of {size} bytes but {typeof(T).Name} is {sizeof(T)} bytes. "
                                       + "The Comp<T> handle most likely names a different component.");
        }

        return new ReadOnlySpan<T>(ColumnAddress(slot, out _), _gather ? GatherBatch : _layout.ClusterSize);
    }

    /// <summary>
    /// Component slot <paramref name="slot"/>'s column for the current batch, one value per batch slot at the component's stride: the HEAD for
    /// SingleVersion and Transient, the value at the snapshot for Versioned. Valid until the next step. <paramref name="present"/> is the rows whose
    /// component exists at the snapshot: on a Versioned slot, an entity whose component was first supplied after the snapshot has none, and reads zero.
    /// </summary>
    internal byte* ColumnAddress(byte slot, out ulong present)
    {
        CheckLive();
        var size = _layout.ComponentSize(slot);
        present = Rows;
        if (_gather)
        {
            return GatherColumn(slot, size);
        }

        if ((_meta.VersionedSlotMask & (1 << slot)) == 0)
        {
            // SingleVersion or Transient: the HEAD, zero-copy, on the page the cluster accessor holds for this step.
            var columnBase = _transientBase != null && (_meta.TransientSlotMask & (1 << slot)) != 0 ? _transientBase : _base;
            return columnBase + _layout.ComponentOffset(slot);
        }

        // Rounded up to 16 slots: a SIMD predicate reads whole lanes past the cluster's last slot and masks them afterwards (SimdPredicateEvaluator).
        var column = ColumnBuffer(slot, size, (_layout.ClusterSize + 15) & ~15);
        if ((_filled & (1u << slot)) == 0)
        {
            // A chain read needs the epoch: the scope comes back for the fill's length, at the epoch it left at.
            _tx.ResumeIfSuspended();
            try
            {
                var filled = SnapshotColumns.Fill(_tx, _meta, _es, _cs, _base, _chunkId, slot, size, Rows, column, _record, ref _mapAccessor) & Rows;

                // A row with no value at the snapshot reads zero, not what the buffer held from another cluster.
                for (var absent = Rows & ~filled; absent != 0; absent &= absent - 1)
                {
                    new Span<byte>(column + BitOperations.TrailingZeroCount(absent) * size, size).Clear();
                }

                _present[slot] = filled;
            }
            finally
            {
                _tx.TrySuspendEpochAtWalkStep();
            }

            _filled |= 1u << slot;
        }

        present = _present[slot];
        return column;
    }

    /// <summary>
    /// Gathers <paramref name="slot"/>'s column for the batch on the gather path: HEAD bytes for SingleVersion and Transient, the value at the snapshot for
    /// Versioned. Locations are resolved again when a fence ran since the batch's fill, and the gather is read again when one overlaps it. Each value is
    /// taken only from a slot that still holds the batch's entity: a SingleVersion destroy releases its slot at publish and a spawn may reuse it before any
    /// fence runs, which would hand the entity another one's values.
    /// </summary>
    private byte* GatherColumn(byte slot, int size)
    {
        var column = ColumnBuffer(slot, size, GatherBatch);
        if ((_filled & (1u << slot)) != 0)
        {
            return column;
        }

        var versioned = (_meta.VersionedSlotMask & (1 << slot)) != 0;
        var transientColumn = _hasTransient && (_meta.TransientSlotMask & (1 << slot)) != 0;
        var offset = _layout.ComponentOffset(slot);
        _tx.ResumeIfSuspended();
        PagedMMF.RefreshableWaitDepth++;
        try
        {
            while (true)
            {
                WaitOutFence();
                var token = _window.ReadToken();
                if (token != _batchToken)
                {
                    ResolveLocations();
                    _batchToken = token;
                }

                for (var i = 0; i < _batchCount; i++)
                {
                    GatherOne(i, slot, size, offset, versioned, transientColumn, column + i * size);
                }

                if (!_window.ReadOverlapped(token))
                {
                    break;
                }
            }
        }
        finally
        {
            PagedMMF.RefreshableWaitDepth--;
            _tx.TrySuspendEpochAtWalkStep();
        }

        _filled |= 1u << slot;
        return column;
    }

    /// <summary>One entity's value for <see cref="GatherColumn"/>, from the slot its record names once that slot is confirmed to still hold it.</summary>
    private void GatherOne(int i, byte slot, int size, int offset, bool versioned, bool transientColumn, byte* dest)
    {
        for (var attempt = 0; ; attempt++)
        {
            var chunk = _batchChunks[i];
            if (chunk < 0)
            {
                new Span<byte>(dest, size).Clear();   // gone since the fill: a SingleVersion destroy, which promises no snapshot
                return;
            }

            var s = _batchSlots[i];
            var primary = _hasPersistent ? _clusterAccessor.GetChunkAddress(chunk) : _transientAccessor.GetChunkAddress(chunk);
            var source = (transientColumn && _hasPersistent ? _transientAccessor.GetChunkAddress(chunk) : primary) + offset + s * size;

            // VIS-12 for Versioned: the HEAD only when no change above the snapshot touched the slot, validated after the copy as the SoA scan does.
            var changed = versioned && (_cs.ChangedSlotsAfter(chunk, _tsn) & (1UL << s)) != 0;
            if (!changed)
            {
                Buffer.MemoryCopy(source, dest, size, size);
                Volatile.ReadBarrier();   // the copy's loads before the id's and the stamps' re-reads; nothing on x64
                changed = versioned && (_cs.ChangedSlotsAfter(chunk, _tsn) & (1UL << s)) != 0;
            }

            if (Volatile.Read(ref *(long*)(primary + _layout.EntityIdsOffset + s * 8)) != _batchIds[i])
            {
                // The slot holds another entity now. Found again through the record once; still elsewhere, it is gone.
                if (attempt > 0 || !ResolveLocation(i))
                {
                    _batchChunks[i] = -1;
                }

                continue;
            }

            if (changed)
            {
                var refreshable = PagedMMF.RefreshableWaitDepth;
                PagedMMF.RefreshableWaitDepth = 0;
                try
                {
                    SnapshotColumns.ReadOne(_tx, _meta, _es, slot, size, _batchIds[i], dest, _record, ref _mapAccessor);
                }
                finally
                {
                    PagedMMF.RefreshableWaitDepth = refreshable;
                }
            }

            return;
        }
    }

    /// <summary>Where each entity of the batch lives now, through its record: a fence may have migrated it since the fill.</summary>
    private void ResolveLocations()
    {
        for (var i = 0; i < _batchCount; i++)
        {
            if (!ResolveLocation(i))
            {
                _batchChunks[i] = -1;
            }
        }
    }

    /// <summary>Where entity <paramref name="i"/> of the batch lives now, through its record; false when it has none.</summary>
    private bool ResolveLocation(int i)
    {
        if (!_es.EntityMap.TryGet(_batchKeys[i], _record, ref _mapAccessor))
        {
            return false;
        }

        _batchChunks[i] = ClusterEntityRecordAccessor.GetClusterChunkId(_record);
        _batchSlots[i] = (byte)ClusterEntityRecordAccessor.GetSlotIndex(_record);
        return true;
    }

    private byte* ColumnBuffer(byte slot, int size, int slots)
    {
        var column = (byte*)_columns[slot];
        if (column == null)
        {
            // native-alloc: per-walk column buffer handed out as a span, freed in Dispose
            column = (byte*)NativeMemory.AllocZeroed((nuint)(size * slots));
            _columns[slot] = (nint)column;
        }

        return column;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Fences and visited clusters
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A step never starts while a fence runs on another thread: the structures it reads are mid-rewrite (EW-01's readers clause). On a fence thread, or
    /// inside a system, the window cannot be open under the walk.
    /// </summary>
    private void WaitOutFence()
    {
        if (!_window.IsOpen || _window.OnFenceThread)
        {
            return;
        }

        // Unpinned: a fence that waits for an eviction must not find this thread's pin holding the page it waits for while this thread waits for it. In the
        // middle of a step the walk holds only its scan accessors' slot references, and what it read before the fence opened is read again after.
        var suspended = _tx.TrySuspendEpochAtWalkStep();
        var spinner = new SpinWait();
        while (_window.IsOpen)
        {
            spinner.SpinOnce(sleep1Threshold: -1);
        }

        if (suspended)
        {
            _tx.ResumeForWalkStep();
        }
    }

    /// <summary>A walk outlives neither its transaction nor the transaction's reuse from the pool.</summary>
    private void CheckLive()
    {
        if (!IsTransactionLive)
        {
            ThrowHelper.ThrowInvalidOp("A walk was used after its transaction was disposed: walk inside the transaction, and dispose the walk first.");
        }
    }

    private bool IsTransactionLive => !_tx.IsDisposedForWalk && _tx.TSN == _tsn;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsVisited(int chunkId) => TestBit(_visited, chunkId);

    private void MarkVisited(int chunkId) => SetBit(ref _visited, chunkId);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TestBit(ulong[] bits, int chunkId)
    {
        var word = (uint)chunkId >> 6;
        return word < (uint)bits.Length && (bits[word] & (1UL << chunkId)) != 0;
    }

    private static void SetBit(ref ulong[] bits, int chunkId)
    {
        var word = chunkId >> 6;
        if (word >= bits.Length)
        {
            var grown = ArrayPool<ulong>.Shared.Rent(Math.Max(word + 1, bits.Length * 2));
            Array.Clear(grown);
            Array.Copy(bits, grown, bits.Length);
            if (bits.Length > 0)
            {
                ArrayPool<ulong>.Shared.Return(bits);
            }

            bits = grown;
        }

        bits[word] |= 1UL << chunkId;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Disposal
    // ═══════════════════════════════════════════════════════════════════════

#pragma warning disable TYPHON006 // Its _tx is borrowed: the walking transaction, its caller's to dispose
    internal void Dispose()
#pragma warning restore TYPHON006
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Step++;
        Rows = 0;

        // Accessors release their pins; the transaction's scope comes back first if the walk left it, as it would at its next operation. A transaction
        // already disposed, or reused from the pool by another owner, is not touched.
        if (IsTransactionLive)
        {
            _tx.ResumeIfSuspended();
        }
        if (_hasPersistent)
        {
            _clusterAccessor.Dispose();
        }

        if (_hasTransient)
        {
            _transientAccessor.Dispose();
        }

        _mapAccessor.Dispose();
        _cursor.Dispose();

        for (var i = 0; i < _columns.Length; i++)
        {
            NativeMemory.Free((void*)_columns[i]);
            _columns[i] = 0;
        }

        NativeMemory.Free(_record);
        _record = null;
        NativeMemory.Free(_batchIds);
        _batchIds = null;

        if (_visited.Length > 0)
        {
            ArrayPool<ulong>.Shared.Return(_visited);
            _visited = [];
        }

        if (_startClusters.Length > 0)
        {
            ArrayPool<ulong>.Shared.Return(_startClusters);
            _startClusters = [];
        }

        if (_queueKeys != null)
        {
            ArrayPool<long>.Shared.Return(_queueKeys);
            ArrayPool<int>.Shared.Return(_queueChunks);
            ArrayPool<byte>.Shared.Return(_queueSlots);
            _queueKeys = null;
        }
    }

    /// <summary>
    /// The gather path's fill: the keys visible at the snapshot and not destroyed by the transaction, each with the cluster slot its record names. A bucket's
    /// chain can overrun the arrays; they grow then, through the pool.
    /// </summary>
    private struct GatherKeyAction : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public long TxTsn;
        public CommitCardTable Cards;
        public RawValuePagedHashMap<long, PersistentStore> Map;
        public ushort RoutingId;
        public System.Collections.Generic.HashSet<EntityId> PendingDestroys;
        public long[] Keys;
        public int[] Chunks;
        public byte[] Slots;
        public int Count;

        public bool Process(long key, byte* value)
        {
            if (!EntityVisibility.IsVisibleInScan(value, TxTsn, Cards, Map, key))
            {
                return true;
            }

            if (PendingDestroys != null && PendingDestroys.Contains(new EntityId(key, RoutingId)))
            {
                return true;
            }

            if (Count == Keys.Length)
            {
                Keys = Grow(Keys, Count);
                Chunks = Grow(Chunks, Count);
                Slots = Grow(Slots, Count);
            }

            Keys[Count] = key;
            Chunks[Count] = ClusterEntityRecordAccessor.GetClusterChunkId(value);
            Slots[Count] = (byte)ClusterEntityRecordAccessor.GetSlotIndex(value);
            Count++;
            return true;
        }

        private static T[] Grow<T>(T[] array, int count)
        {
            var grown = ArrayPool<T>.Shared.Rent(array.Length * 2);
            Array.Copy(array, grown, count);
            ArrayPool<T>.Shared.Return(array);
            return grown;
        }
    }
}
