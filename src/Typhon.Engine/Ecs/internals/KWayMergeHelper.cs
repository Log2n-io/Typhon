using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// One archetype's contribution to an ordered query: a live B+Tree range cursor, plus one leaf's worth of
/// (orderedKey, ClusterLocation) pairs buffered ahead of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This used to drain its whole range up front</b>, because <c>BTree.RangeEnumerator</c> is a <c>ref struct</c> and K
/// of them cannot be held at once. Draining is bounded by <c>Skip+Take</c>, so the cost was not unbounded — it was
/// <c>K·(Skip+Take)</c> entries built to emit <c>Skip+Take</c> of them, and the waste grew linearly with the number of
/// archetypes a polymorphic query happens to span.
/// </para>
/// <para>
/// <b>Now it holds a <see cref="LeafPageCursorState"/></b>, which is a plain struct and therefore array-storable, and
/// refills one B+Tree leaf at a time. Read-ahead is bounded by the page, never by <c>Skip+Take</c>, so a stream that
/// loses every comparison in the merge reads one leaf and stops. The page starts at
/// <see cref="InitialPageCapacity"/> and doubles only while fills keep coming back full — that is, only for a stream
/// the merge is demonstrably draining, which is what lets a deep <c>Skip</c> amortise without handing the same budget
/// to the streams that contribute one row.
/// </para>
/// <para>
/// <b>Rows are resolved to entity keys only when the merge emits them.</b> Resolution is a cluster chunk lookup whose
/// locality is uncorrelated with key order (the index yields keys in order; the entities sit wherever they were
/// spawned), so it is a cold lookup on essentially every row. Charging it to candidates instead of winners was the
/// single largest cost in the old fill.
/// </para>
/// </remarks>
internal unsafe struct ArchetypeSortedStream : IDisposable
{
    /// <summary>
    /// Read-ahead budget for the first fill, in entries. Sized so a stream the merge abandons after one row has read
    /// about one B+Tree leaf (19-29 entries) and stopped.
    /// </summary>
    private const int InitialPageCapacity = 64;

    /// <summary>
    /// Ceiling on the read-ahead budget. Reached only by a stream that has already supplied thousands of rows, at which
    /// point the merge is plainly consuming it and the per-fill overhead is worth amortising.
    /// </summary>
    private const int MaxPageCapacity = 8192;

    private long[] _orderedKeys;    // Rented from ArrayPool
    private int[] _locations;       // Rented from ArrayPool — packed ClusterLocation, resolved to an EntityPK on demand
    private int _count;             // entries currently buffered
    private int _pos;
    private int _pageCapacity;      // grows geometrically while the merge keeps draining this stream

    private BTreeBase<PersistentStore> _tree;
    private LeafPageCursorState _cursor;
    private ChunkAccessor<PersistentStore> _indexAccessor;
    private ChunkAccessor<PersistentStore> _clusterAccessor;
    private ArchetypeClusterInfo _layout;
    private bool _hasAccessors;

    // QFENCE-01: the entities written since the last fence (null when there are none, or nothing to check them against), the query's evaluators, and
    // where the evaluated component sits in a cluster.
    private DirtyBitmap _fenceWritten;
    private FieldEvaluator[] _evaluators;
    private int _compOffset;
    private int _compSize;

    // QENABLE-01: the query's enabled gate on this archetype, applied to every page; open (admitting everything) for a caller that passes none.
    private EnabledGate _gate;
    private bool _gated;

    // #1347: the reader's snapshot — every row's born and died tested against it, as the scans test them — and the clusters a side stream already holds
    // whole, whose rows this stream drops. Null state: no snapshot test (a non-Versioned archetype, or a caller that maps keys only).
    private ArchetypeClusterState _clusterState;
    private ArchetypeEngineState _visState;
    private CommitCardTable _cards;
    private long _txTsn;
    private int _recordSize;
    private ulong[] _wholeClusters;
    private bool _filtering;    // any of the three above, or the gate: TryAdmitCurrent tests the row
    private ChunkAccessor<PersistentStore> _mapAccessor;
    private bool _mapOpen;

    public bool HasCurrent
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _pos < _count;
    }

    public long CurrentKey
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _orderedKeys[_pos];
    }

    /// <summary>Resolves this row's cluster location to its entity key. Costs one cluster chunk lookup, so only call it for rows you are keeping.</summary>
    public long CurrentEntityPK
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ResolveEntityPK(_locations[_pos], ref _clusterAccessor, _layout);
    }

    /// <summary>
    /// Opens a streaming cursor over <paramref name="tree"/> restricted to [<paramref name="scanMin"/>,
    /// <paramref name="scanMax"/>] and buffers its first page.
    /// </summary>
    /// <remarks>
    /// <paramref name="scanMin"/> / <paramref name="scanMax"/> carry the bound keys' RAW BITS widened to a
    /// <see cref="long"/> — the same convention the typed dispatch used when it cast them back with
    /// <c>(int)</c> / <c>BitConverter.Int32BitsToSingle</c> — not the order-preserving encoding the merge compares on.
    /// </remarks>
    /// <param name="tree">The ordered field's per-archetype tree.</param>
    /// <param name="keyType">The tree's key type.</param>
    /// <param name="scanMin">Lower bound, raw key bits (see the remarks).</param>
    /// <param name="scanMax">Upper bound, raw key bits (see the remarks).</param>
    /// <param name="descending">Stream from <paramref name="scanMax"/> down.</param>
    /// <param name="clusterState">The archetype's cluster state.</param>
    /// <param name="layout">The archetype's cluster layout.</param>
    /// <param name="evaluators">The query's predicates, re-checked on the entities written since the last fence (QFENCE-01): the tree holds such an entity
    /// under its key as of the fence, so a value that has left the range since must not ride it into the result. <c>null</c> streams the tree as is.</param>
    /// <param name="componentSlot">The archetype slot of the component <paramref name="evaluators"/> read.</param>
    /// <param name="gate">The query's enabled gate on this archetype (QENABLE-01); <c>null</c> admits every slot.</param>
    /// <param name="snapshot">The reader's snapshot, for a Versioned archetype (#1347): a row born after it, or died at or before it, is dropped, and a row
    /// whose slot a destroy hid since the tree read is decided by its record. <c>null</c> streams HEAD.</param>
    public static ArchetypeSortedStream Create(BTreeBase<PersistentStore> tree, KeyType keyType, long scanMin, long scanMax, bool descending,
        ArchetypeClusterState clusterState, ArchetypeClusterInfo layout, FieldEvaluator[] evaluators = null, int componentSlot = -1,
        EnabledGate? gate = null, StreamSnapshot? snapshot = null)
    {
        // Bool and String64 have no typed B+Tree. Dispatch is virtual now, so such a tree would not fall out of a switch
        // — it would fill pages whose keys all encode to 0, comparing equal, and the merge would return this archetype's
        // rows in an arbitrary order with nothing raised. KeyRange.IsStreamable is supposed to keep them off this path;
        // this is the assertion that it did (#663 shape, #675).
        if (!KeyRange.IsStreamable(keyType))
        {
            ThrowHelper.ThrowInvalidOp(
                $"Ordered query requested a sorted stream over key type {keyType}, which has no B+Tree range scan. KeyRange.IsStreamable must reject this "
                + "type so the query sorts from the SoA scan instead.");
        }

        var stream = new ArchetypeSortedStream
        {
            _tree = tree,
            _layout = layout,
            _fenceWritten = evaluators != null && componentSlot >= 0 && clusterState.MayHaveFenceStaleKeys(componentSlot) ? clusterState.ClusterShadowBitmap : null,
            _gate = gate ?? default,
            _gated = gate.HasValue,
            _clusterState = clusterState,
            _visState = snapshot?.State,
            _cards = snapshot?.Cards,
            _txTsn = snapshot?.TxTsn ?? 0,
            _recordSize = snapshot?.RecordSize ?? 0,
            _wholeClusters = snapshot?.WholeClusters,
            _filtering = gate.HasValue || snapshot.HasValue,
            _evaluators = evaluators,
            _compOffset = componentSlot >= 0 ? layout.ComponentOffset(componentSlot) : 0,
            _compSize = componentSlot >= 0 ? layout.ComponentSize(componentSlot) : 0,
            _clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor(),
            _indexAccessor = tree.Segment.CreateChunkAccessor(),
            _hasAccessors = true,
            _orderedKeys = ArrayPool<long>.Shared.Rent(InitialPageCapacity),
            _locations = ArrayPool<int>.Shared.Rent(InitialPageCapacity),
            _pageCapacity = InitialPageCapacity,
            _cursor = new LeafPageCursorState
            {
                MinKeyBits = scanMin,
                MaxKeyBits = scanMax,
                KeyType = keyType,
                Reverse = descending
            }
        };

        try
        {
            stream.Refill();
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        return stream;
    }

    /// <summary>
    /// A stream over rows already filtered and sorted — the clusters an ordered query read whole for a snapshot older than a destroy (#1347). Takes
    /// ownership of the two arrays, rented from <see cref="ArrayPool{T}"/>.
    /// </summary>
    public static ArchetypeSortedStream FromSorted(long[] orderedKeys, int[] locations, int count, ArchetypeClusterState clusterState,
        ArchetypeClusterInfo layout) =>
        new()
        {
            _layout = layout,
            _clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor(),
            _hasAccessors = true,
            _orderedKeys = orderedKeys,
            _locations = locations,
            _count = count,
            _pageCapacity = Math.Min(orderedKeys.Length, locations.Length),
            _cursor = new LeafPageCursorState { Exhausted = true }
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Advance()
    {
        _pos++;
        if (_pos < _count)
        {
            return true;
        }

        Refill();
        return _pos < _count;
    }

    /// <summary>Pulls the next leaf's entries into the page buffer, growing it if one indivisible key needs more room.</summary>
    /// <remarks>Kept out of line so <see cref="Advance"/> stays small enough to inline — the buffered step is the hot one by a factor of a leaf.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Refill()
    {
        _pos = 0;
        _count = 0;

        // A fill can legitimately come back empty without the range being over — it walked leaves whose entries were all
        // behind the cursor, or hit its per-call leaf budget. Only Exhausted ends the stream.
        var pageFull = false;
        while (_count == 0 && !_cursor.Exhausted)
        {
            var written = _tree.FillOrderedPage(ref _cursor, _orderedKeys.AsSpan(0, _pageCapacity), _locations.AsSpan(0, _pageCapacity), ref _indexAccessor);
            if (written < 0)
            {
                // An AllowMultiple key whose value list is larger than the whole page. Grow and ask again; the cursor
                // has not moved, so nothing is lost or repeated.
                GrowBuffers(-written);
                continue;
            }

            pageFull = written == _pageCapacity;
            _count = _fenceWritten != null ? DropFailedWrites(written) : written;
        }

        // A page that came back completely full is a stream the merge is actually draining, so widen its read-ahead for
        // next time. This is what keeps a deep Skip from paying a fresh leaf snapshot every 64 rows, without giving that
        // budget to the streams that lose every comparison and are read once.
        if (pageFull && _pageCapacity < MaxPageCapacity)
        {
            GrowBuffers(_pageCapacity * 2, true);
        }
    }

    /// <summary>
    /// QFENCE-01: compacts the first <paramref name="written"/> entries of the page, dropping each entity written since the last fence that is no longer
    /// live or whose current value fails the query. Its tree key is the value as of the fence; a written entity that still matches keeps that position,
    /// which is the order the query documents for it. Returns the entries kept.
    /// </summary>
    /// <remarks>
    /// Liveness matters only here: a destroy leaves the index entries of an entity written earlier in the tick for the fence to remove, while it clears
    /// the occupancy bit and the entity id at once — the merge would resolve that entry to entity 0. An entity destroyed without a write is removed from
    /// the index by the destroy itself.
    /// </remarks>
    private int DropFailedWrites(int written)
    {
        var kept = 0;
        var word = -1;
        ulong dirty = 0;
        for (var i = 0; i < written; i++)
        {
            var location = _locations[i];
            var chunkId = location >> 6;
            if (chunkId != word)
            {
                word = chunkId;
                dirty = _fenceWritten.ReadWord(chunkId);
            }

            var bit = 1UL << (location & 0x3F);
            if ((dirty & bit) != 0)
            {
                var clusterBase = _clusterAccessor.GetChunkAddress(chunkId);
                if ((Volatile.Read(ref *(ulong*)clusterBase) & bit) == 0)
                {
                    if (_visState == null)
                    {
                        continue;   // destroyed since its write
                    }

                    // Destroyed, its slot hidden from HEAD and kept for older snapshots (VIS-09): TryAdmitCurrent decides it from its record and its bytes.
                    _orderedKeys[kept] = _orderedKeys[i];
                    _locations[kept] = location;
                    kept++;
                    continue;
                }

                var entityComp = clusterBase + _compOffset + (location & 0x3F) * _compSize;
                var matches = true;
                for (var e = 0; e < _evaluators.Length; e++)
                {
                    if (!FieldEvaluator.Evaluate(ref _evaluators[e], entityComp + _evaluators[e].FieldOffset))
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }
            }

            _orderedKeys[kept] = _orderedKeys[i];
            _locations[kept] = location;
            kept++;
        }

        return kept;
    }

    /// <summary>
    /// Whether the current row is one the query returns: the query's enabled gate admits it (QENABLE-01) and, with a snapshot, the snapshot sees it (#1347)
    /// — born at or before it, not dead at it, not in a cluster a side stream holds whole. <paramref name="entityPK"/>: the row's entity key, read on the
    /// way when <paramref name="resolve"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked by the merge for the row it is about to consume — emitted or skipped — and for no other. Filtering each page as it was read cost a cluster
    /// access for every row read ahead and a second one for each row emitted: on an ordered <c>Take(50)</c> the read-ahead alone was a third of the query.
    /// The rows a page holds sit in clusters uncorrelated with key order, so a check here is one cluster access per row consumed, shared with the
    /// resolution of the entity key.
    /// </para>
    /// <para>
    /// A row whose slot is no longer occupied was hidden by a destroy after the tree read (VIS-09): its record decides it, as the SoA scan decides a slot
    /// claimed and not occupied, and its predicates are evaluated on the bytes the slot kept. The pending deaths are read before the occupancy word and the
    /// claims after it, as in <c>ScanClusterSoa</c>.
    /// </para>
    /// </remarks>
    public bool TryAdmitCurrent(bool resolve, out long entityPK)
    {
        var location = _locations[_pos];
        entityPK = 0;
        if (!_filtering)
        {
            if (resolve)
            {
                entityPK = ResolveEntityPK(location, ref _clusterAccessor, _layout);
            }

            return true;
        }

        var chunkId = location >> 6;
        if (_wholeClusters != null && (uint)(chunkId >> 6) < (uint)_wholeClusters.Length && (_wholeClusters[chunkId >> 6] & (1UL << chunkId)) != 0)
        {
            return false;
        }

        var slotIndex = location & 0x3F;
        var bit = 1UL << slotIndex;
        var clusterBase = _clusterAccessor.GetChunkAddress(chunkId);
        var occupancy = ulong.MaxValue;
        ulong pend = 0;
        var clusterVisible = true;
        if (_visState != null)
        {
            ref var words = ref ArchetypeClusterState.SlotWords(_clusterState.SlotWordTable, chunkId);
            var pendingDeaths = ArchetypeClusterState.PendingDeathsOf(ref words);
            occupancy = Volatile.Read(ref *(ulong*)clusterBase);
            if (Unsafe.IsNullRef(ref words))
            {
                words = ref ArchetypeClusterState.SlotWords(_clusterState.SlotWordTable, chunkId);
            }

            pend = ArchetypeClusterState.ClaimOf(ref words) & ~occupancy;
            clusterVisible = pendingDeaths == 0 && ArchetypeClusterState.WatermarksVisibleAt(ref words, _txTsn);
        }

        bool admitted;
        if ((occupancy & bit) == 0)
        {
            // Hidden since the tree read, or released: only a slot still claimed holds an entity a snapshot can see.
            admitted = (pend & bit) != 0 && AdmitsHidden(clusterBase, chunkId, slotIndex);
        }
        else
        {
            var exact = true;
            var mask = _gated ? _gate.ClusterMask(clusterBase, out exact) : ulong.MaxValue;
            admitted = (mask & bit) != 0
                       && ((clusterVisible && (!_gated || !_gate.NeedsPerSlot(exact))) || AdmitsByRecord(clusterBase, slotIndex, exact, clusterVisible));
        }

        if (admitted && resolve)
        {
            entityPK = *(long*)(clusterBase + _layout.EntityIdsOffset + slotIndex * 8);
        }

        return admitted;
    }

    /// <summary>An occupied row the cluster words cannot decide: the gate's per-slot resolution, the snapshot's born and died, each from the record.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool AdmitsByRecord(byte* clusterBase, int slotIndex, bool exact, bool clusterVisible)
    {
        var record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        var rawId = *(long*)(clusterBase + _layout.EntityIdsOffset + slotIndex * 8);
        if (_gated && _gate.NeedsPerSlot(exact))
        {
            if (_gate.NeedsRecords)
            {
                OpenMap(_gate.State);
            }

            if (!_gate.Admits(rawId, clusterBase, slotIndex, exact, record, ref _mapAccessor))
            {
                return false;
            }
        }

        if (!clusterVisible)
        {
            OpenMap(_visState);
            return SnapshotSlot.IsVisibleAtSnapshot(rawId, true, _visState, record, _recordSize, _txTsn, _cards, ref _mapAccessor);
        }

        return true;
    }

    /// <summary>A row whose slot a destroy hid since the tree read: its predicates on the bytes the slot kept, then its record at the snapshot.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool AdmitsHidden(byte* clusterBase, int chunkId, int slotIndex)
    {
        if (_evaluators != null && !SnapshotSlot.MatchesAll(_evaluators, clusterBase + _compOffset + slotIndex * _compSize))
        {
            return false;
        }

        var record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        var rawId = *(long*)(clusterBase + _layout.EntityIdsOffset + slotIndex * 8);
        OpenMap(_visState);
        return SnapshotSlot.IsReservedSpawnVisible(rawId, chunkId, slotIndex, _visState, record, _txTsn, _cards, ref _mapAccessor)
               && (!_gated || _gate.AdmitsRecord(rawId, record));
    }

    private void OpenMap(ArchetypeEngineState state)
    {
        if (!_mapOpen)
        {
            _mapAccessor = state.EntityMap.Segment.CreateScanAccessor();
            _mapOpen = true;
        }
    }

    /// <summary>Resolve a ClusterLocation (packed int) to an EntityPK by reading the cluster's EntityIds array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ResolveEntityPK(int clusterLocation, ref ChunkAccessor<PersistentStore> clusterAccessor, ArchetypeClusterInfo layout)
    {
        int chunkId = clusterLocation >> 6;
        int slotIndex = clusterLocation & 0x3F;
        byte* clusterBase = clusterAccessor.GetChunkAddress(chunkId);
        Debug.Assert(clusterBase != null, $"Cluster chunk {chunkId} not accessible");
        return *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
    }

    /// <summary>
    /// Re-rents the page buffers at <paramref name="minimumCapacity"/>. Set <paramref name="preserve"/> when entries
    /// already on the page must survive the growth; the "one key does not fit" path does not need it, because that page
    /// is empty by definition.
    /// </summary>
    private void GrowBuffers(int minimumCapacity, bool preserve = false)
    {
        var newKeys = ArrayPool<long>.Shared.Rent(minimumCapacity);
        var newLocations = ArrayPool<int>.Shared.Rent(minimumCapacity);

        if (preserve && _count > 0)
        {
            Array.Copy(_orderedKeys, newKeys, _count);
            Array.Copy(_locations, newLocations, _count);
        }

        ArrayPool<long>.Shared.Return(_orderedKeys);
        ArrayPool<int>.Shared.Return(_locations);
        _orderedKeys = newKeys;
        _locations = newLocations;

        // Always take the whole rented array. Clamping to MaxPageCapacity here would be a livelock: a single
        // AllowMultiple key holding more than that many values would demand a bigger page, get one, and then be told to
        // fill a span that is still too small — forever. The ceiling belongs to the VOLUNTARY growth in Refill, which is
        // an optimisation, not to the demanded growth, which is a requirement.
        _pageCapacity = Math.Min(newKeys.Length, newLocations.Length);
    }

    public void Dispose()
    {
        if (_orderedKeys != null)
        {
            ArrayPool<long>.Shared.Return(_orderedKeys);
            _orderedKeys = null;
        }

        if (_locations != null)
        {
            ArrayPool<int>.Shared.Return(_locations);
            _locations = null;
        }

        if (_hasAccessors)
        {
            _hasAccessors = false;
            _clusterAccessor.Dispose();
            _indexAccessor.Dispose();
        }

        if (_mapOpen)
        {
            _mapOpen = false;
            _mapAccessor.Dispose();
        }
    }
}

/// <summary>The reader's snapshot an <see cref="ArchetypeSortedStream"/> tests its rows against (#1347).</summary>
internal readonly struct StreamSnapshot
{
    /// <summary>The archetype's engine state, whose EntityMap holds the records.</summary>
    public ArchetypeEngineState State { get; init; }

    /// <summary>The engine's commit cards, through which a pending born or died resolves.</summary>
    public CommitCardTable Cards { get; init; }

    /// <summary>The reader's snapshot.</summary>
    public long TxTsn { get; init; }

    /// <summary>The archetype's EntityMap record size.</summary>
    public int RecordSize { get; init; }

    /// <summary>One bit per cluster a side stream holds whole (<see cref="ArchetypeSortedStream.FromSorted"/>); null for none.</summary>
    public ulong[] WholeClusters { get; init; }
}

/// <summary>
/// K-way merge of K sorted <see cref="ArchetypeSortedStream"/> instances.
/// Uses a binary min-heap (or max-heap for descending) to efficiently yield entries in global sort order.
/// Supports early termination for Skip/Take pagination.
/// </summary>
internal struct KWayMergeState : IDisposable
{
    private ArchetypeSortedStream[] _streams;
    private int _streamCount;       // Actual number of streams (array may be larger if rented)
    private int[] _heap;            // Heap of stream indices, rented from ArrayPool to avoid GC allocation
    private int _heapSize;
    private bool _descending;
    private bool _ownsStreamsArray; // True if _streams was rented from ArrayPool

    /// <summary>
    /// Initialize the merge state from K pre-filled streams.
    /// Builds the initial heap from all non-empty streams.
    /// </summary>
    /// <param name="streams">Array of streams (may be larger than streamCount if rented from ArrayPool).</param>
    /// <param name="streamCount">Actual number of valid streams in the array.</param>
    /// <param name="descending">True for descending sort order.</param>
    /// <param name="ownsArray">True if the array was rented from ArrayPool and should be returned on Dispose.</param>
    public static KWayMergeState Create(ArchetypeSortedStream[] streams, int streamCount, bool descending, bool ownsArray = false)
    {
        int heapCapacity = streamCount <= 16 ? 16 : streamCount;
        var state = new KWayMergeState
        {
            _streams = streams,
            _streamCount = streamCount,
            _heap = ArrayPool<int>.Shared.Rent(heapCapacity),
            _heapSize = 0,
            _descending = descending,
            _ownsStreamsArray = ownsArray
        };

        // Insert all non-empty streams into the heap
        for (int i = 0; i < streamCount; i++)
        {
            if (streams[i].HasCurrent)
            {
                state._heap[state._heapSize] = i;
                state._heapSize++;
            }
        }

        // Build heap bottom-up (O(K))
        for (int i = state._heapSize / 2 - 1; i >= 0; i--)
        {
            state.SiftDown(i);
        }

        return state;
    }

    /// <summary>
    /// Pop the next entry from the merged stream.
    /// Returns false when all streams are exhausted.
    /// </summary>
    /// <param name="entityPK">The row's entity key, or 0 when <paramref name="resolveEntity"/> is false.</param>
    /// <param name="resolveEntity">
    /// False for a row the caller is about to discard. Resolving costs a cluster chunk lookup whose locality is
    /// uncorrelated with key order, and <c>Skip(n)</c> discards exactly n rows — this used to resolve all of them, so a
    /// <c>Skip(2000).Take(50)</c> paid 2 050 lookups to return 50 rows. The stream defers resolution precisely so that
    /// the merge can decline it; asking for it unconditionally here threw that away.
    /// </param>
    /// <remarks>A row its stream refuses (<see cref="ArchetypeSortedStream.TryAdmitCurrent"/>) is consumed and not counted: the next one is taken.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext(out long entityPK, bool resolveEntity = true)
    {
        while (_heapSize > 0)
        {
            int topStream = _heap[0];
            var admitted = _streams[topStream].TryAdmitCurrent(resolveEntity, out entityPK);

            // Advance the stream that yielded the current entry
            if (_streams[topStream].Advance())
            {
                // Stream has more entries — re-heapify from root
                SiftDown(0);
            }
            else
            {
                // Stream exhausted — remove from heap
                _heapSize--;
                if (_heapSize > 0)
                {
                    _heap[0] = _heap[_heapSize];
                    SiftDown(0);
                }
            }

            if (admitted)
            {
                return true;
            }
        }

        entityPK = 0;
        return false;
    }

    /// <summary>Peek at the current top key without consuming it.</summary>
    public long PeekKey => _heapSize > 0 ? _streams[_heap[0]].CurrentKey : 0;

    public bool IsEmpty => _heapSize == 0;

    private void SiftDown(int i)
    {
        while (true)
        {
            int left = 2 * i + 1;
            int right = 2 * i + 2;
            int best = i;

            if (left < _heapSize && IsHigherPriority(left, best))
            {
                best = left;
            }
            if (right < _heapSize && IsHigherPriority(right, best))
            {
                best = right;
            }
            if (best == i)
            {
                break;
            }

            // Swap
            (_heap[i], _heap[best]) = (_heap[best], _heap[i]);
            i = best;
        }
    }

    /// <summary>
    /// Returns true if heap position a has higher priority (should be closer to root) than b.
    /// For ascending: smaller key = higher priority. For descending: larger key = higher priority.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsHigherPriority(int a, int b)
    {
        long keyA = _streams[_heap[a]].CurrentKey;
        long keyB = _streams[_heap[b]].CurrentKey;
        return _descending ? keyA > keyB : keyA < keyB;
    }

    public void Dispose()
    {
        if (_streams != null)
        {
            for (int i = 0; i < _streamCount; i++)
            {
                _streams[i].Dispose();
            }

            if (_ownsStreamsArray)
            {
                ArrayPool<ArchetypeSortedStream>.Shared.Return(_streams, true);
            }

            _streams = null;
        }

        if (_heap != null)
        {
            ArrayPool<int>.Shared.Return(_heap);
            _heap = null;
        }
    }
}
