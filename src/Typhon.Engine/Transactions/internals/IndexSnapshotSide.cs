using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The part of an index read's answer the trees cannot give at the transaction's snapshot (#1247, VIS-13): the entities a change newer than it destroyed —
/// their entries taken out at its publish (VIS-09) — or re-keyed (VIS-12), read at the snapshot, and the transaction's own spawns and destroys, as its
/// queries see them (QENABLE-01). The reader drops these entities from its tree rows (<see cref="IsDropped"/>) and merges these rows back in key order
/// (<see cref="Transaction.EnumerateIndex{T,TKey}"/>), or looks them up by key (an FK reverse lookup, <see cref="FkSnapshot"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Which entities:</b> in each archetype that promises snapshot isolation, the slots whose KEY stamp is above the snapshot
/// (<see cref="ArchetypeClusterState.KeyChangedSlotsAfter"/>) — destroyed, or re-keyed — in each cluster whose died or update watermark is; every slot
/// of a cluster with a destroy pending or a died watermark recorded without a stamp. An entity whose update moved no key stays a tree row: its key there
/// is its snapshot key, and its value is read through its chain. Each is decided at the snapshot by its record and read at it: a Versioned component
/// through its chain, a SingleVersion one from the slot, whose bytes a hidden destroy kept. In a cluster no update newer than the snapshot touched, the
/// slot's bytes are its value at the snapshot — re-validated after they are read, seqlock-style — so its key is tested before anything is probed.
/// </para>
/// <para>
/// <b>Only what the range can see.</b> An entity is dropped from the tree rows only when its rows can be in the range — the slot's bytes hold the key the
/// trees hold it under — and read beside them only when its key at the snapshot is in the range. One outside the range on both counts is passed over, and
/// a refresh looks at it again: the work stays proportional to the changes the range can see.
/// </para>
/// <para>
/// <b>Against the trees' reads.</b> Every destroy and key move bumps its archetype's <see cref="ArchetypeClusterState.KeyChangeCount"/> after its stamps
/// and before it touches a tree; the count is read FIRST (<see cref="Create"/>), so a reader that later sees it moved (<see cref="NeedsRefresh"/>) reads
/// the slots changed since (<see cref="Refresh"/>). A count, not the stamps: a destroy with a lower stamp can publish after one with a higher stamp. A
/// materialized reader reads its trees first and builds this after. A streaming one reads its trees as it goes, a key's entries at once: after each read,
/// before it merges any row of that key, it re-reads the count; an entity changed since the previous read was read unchanged there if its snapshot key is
/// at or below the PREVIOUS key read, and is not added again; above it, its entry may have left the stretch the cursor just crossed, and it is added — its
/// tree rows from there on dropped.
/// </para>
/// </remarks>
internal sealed unsafe class IndexSnapshotSide<TKey> where TKey : unmanaged
{
    private struct Row
    {
        public TKey Key;
        public long RawId;
        public int ValueOffset;
    }

    private sealed class SourceState
    {
        public Transaction.IndexArchetypeSource Source;
        public long Mark;
    }

#pragma warning disable TYPHON005 // The side borrows the reader's transaction, it does not own it
    private readonly Transaction _tx;
#pragma warning restore TYPHON005
    private readonly long _txTsn;
    private readonly TKey _min;
    private readonly TKey _max;
    private readonly int _valueSize;
    private readonly SourceState[] _states;
    private readonly HashSet<long> _dropped = [];
    private readonly List<Row> _rows = [];
    private byte[] _values;
    private int _valuesUsed;
    private int _next;

    // A refresh's floor: a changed entity whose snapshot key is at or below it was read, unchanged, before — its row is in the answer already.
    private bool _hasFloor;
    private TKey _floor;

    private IndexSnapshotSide(Transaction tx, TKey min, TKey max, int valueSize, SourceState[] states)
    {
        _tx = tx;
        _txTsn = tx.TSN;
        _min = min;
        _max = max;
        _valueSize = valueSize;
        _states = states;
    }

    /// <summary>
    /// The side of a read over <paramref name="sources"/>, or null when there is none: no archetype holds a destroy or key move above the snapshot and the
    /// transaction has no spawn or destroy of its own counted (<paramref name="includeOwn"/>) — three loads per archetype, nothing allocated.
    /// <paramref name="mark0"/>: the first source's <see cref="ArchetypeClusterState.KeyChangeCount"/>, read before anything else, for a streaming reader
    /// that keeps watching with no side. With <paramref name="hasFloor"/>, a streaming reader's late side: every change it reads was published during the
    /// walk, and those at or below <paramref name="floor"/> were read unchanged.
    /// </summary>
    internal static IndexSnapshotSide<TKey> Create(Transaction tx, Transaction.IndexArchetypeSource[] sources, TKey min, TKey max, out long mark0,
        bool hasFloor = false, TKey floor = default, bool includeOwn = true)
    {
        // The tree reads of a materialized reader before the count, on arm64 too (an acquire does not hold earlier plain loads back); JIT-folded on x64.
        if (!X86Base.IsSupported)
        {
            Interlocked.MemoryBarrier();
        }

        var txTsn = tx.TSN;
        var ownSpawns = includeOwn && tx.PendingSpawns is { Count: > 0 };
        var ownDestroys = includeOwn && tx.PendingDestroys is { Count: > 0 };
        var any = ownSpawns || ownDestroys;
        mark0 = 0;
        Span<long> marks = sources.Length <= 16 ? stackalloc long[sources.Length] : new long[sources.Length];
        for (var i = 0; i < sources.Length; i++)
        {
            var cs = sources[i].ClusterState;
            if (sources[i].Meta.VersionedSlotMask == 0)
            {
                continue;
            }

            // The count first: a change it misses is one the stamps below may miss too, and the reader's next look at the count finds it.
            marks[i] = cs.KeyChangeCount;
            any |= cs.MaxDestroyStamp > txTsn || cs.MaxKeyMoveStamp > txTsn;
        }

        if (sources.Length > 0)
        {
            mark0 = marks[0];
        }

        if (!any)
        {
            return null;
        }

        var states = new SourceState[sources.Length];
        for (var i = 0; i < sources.Length; i++)
        {
            if (sources[i].Meta.VersionedSlotMask != 0)
            {
                states[i] = new SourceState { Source = sources[i], Mark = marks[i] };
            }
        }

        var side = new IndexSnapshotSide<TKey>(tx, min, max, sources[0].CompSize, states) { _hasFloor = hasFloor, _floor = floor };

        // The transaction's own destroys first: an entity it destroyed is no row of its reads, whatever a later commit did to it.
        if (ownDestroys)
        {
            foreach (var id in tx.PendingDestroys)
            {
                side._dropped.Add((long)id.RawValue);
            }
        }

        for (var i = 0; i < states.Length; i++)
        {
            if (states[i] != null)
            {
                side.ReadChangedSlots(states[i]);
            }
        }

        if (ownSpawns && !hasFloor)
        {
            side.ReadOwnSpawns(sources);
        }

        side.SortRows(0);
        return side;
    }

    /// <summary>Whether an archetype's key-change count moved since its last read: a destroy or key move published since.</summary>
    internal bool NeedsRefresh()
    {
        for (var i = 0; i < _states.Length; i++)
        {
            if (_states[i] is { } s && s.Source.ClusterState.KeyChangeCount != s.Mark)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the slots changed since the last read in each archetype whose count moved, those not handled yet. A streaming reader passes the previous key it
    /// read (<paramref name="hasFloor"/>): a changed entity whose snapshot key is at or below it was read there unchanged, and is not added.
    /// </summary>
    internal void Refresh(bool hasFloor, TKey floor)
    {
        _hasFloor = hasFloor;
        _floor = floor;
        if (!X86Base.IsSupported)
        {
            Interlocked.MemoryBarrier();
        }

        var before = _rows.Count;
        for (var i = 0; i < _states.Length; i++)
        {
            if (_states[i] is not { } s)
            {
                continue;
            }

            var count = s.Source.ClusterState.KeyChangeCount;
            if (count == s.Mark)
            {
                continue;
            }

            s.Mark = count;
            ReadChangedSlots(s);
        }

        if (_rows.Count > before)
        {
            SortRows(_next);
        }
    }

    /// <summary>Whether the tree row of <paramref name="rawId"/> is not this read's: the entity is read here, or the transaction destroyed it.</summary>
    internal bool IsDropped(long rawId) => _dropped.Count != 0 && _dropped.Contains(rawId);

    /// <summary>The key of the next row to merge.</summary>
    internal bool TryPeek(out TKey key)
    {
        if (_next < _rows.Count)
        {
            key = _rows[_next].Key;
            return true;
        }

        key = default;
        return false;
    }

    /// <summary>The entities of the rows whose key is <paramref name="key"/>, through <paramref name="action"/> (a materialized side: an FK lookup).</summary>
    internal bool ForEachRowAt<TAction>(TKey key, ArchetypeMetadata meta, ref TAction action) where TAction : struct, IFkSourceAction
    {
        var comparer = Comparer<TKey>.Default;
        int lo = _next, hi = _rows.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (comparer.Compare(_rows[mid].Key, key) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (var i = lo; i < _rows.Count && comparer.Compare(_rows[i].Key, key) == 0; i++)
        {
            if (!action.Process(_rows[i].RawId, meta))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Takes the next row: its entity, its key and its component's bytes at the snapshot, valid until the next refresh.</summary>
    internal ReadOnlySpan<byte> Take(out long rawId, out TKey key)
    {
        var row = _rows[_next++];
        rawId = row.RawId;
        key = row.Key;
        return new ReadOnlySpan<byte>(_values, row.ValueOffset, _valueSize);
    }

    /// <summary>Gives the value buffer back.</summary>
    internal void Release()
    {
        if (_values != null)
        {
            ArrayPool<byte>.Shared.Return(_values);
            _values = null;
        }
    }

    private void ReadChangedSlots(SourceState state)
    {
        var src = state.Source;
        var cs = src.ClusterState;
        var es = src.EngineState;
        var cards = _tx.DBE.CommitCards;
        var record = stackalloc byte[src.RecordSize];
        var value = stackalloc byte[_valueSize];
        var mapAccessor = es.EntityMap.Segment.CreateScanAccessor();
        var clusterAccessor = cs.ClusterSegment.CreateScanAccessor();
        var refreshable = PagedMMF.RefreshableWaitDepth;
        PagedMMF.RefreshableWaitDepth = 0;   // point reads through the transaction's accessors: no epoch refresh under them (#1230)
        try
        {
            var table = cs.SlotWordTable;
            var activeIds = cs.ReadActiveClusterList(out var activeCount);
            for (var c = 0; c < activeCount; c++)
            {
                var clusterChunkId = activeIds[c];
                ref var words = ref ArchetypeClusterState.SlotWords(table, clusterChunkId);
                var pendingDeaths = ArchetypeClusterState.PendingDeathsOf(ref words);
                var died = ArchetypeClusterState.MaxDiedOf(ref words);
                var updatedAfter = ArchetypeClusterState.MaxUpdatedOf(ref words) > _txTsn;
                if (pendingDeaths == 0 && died <= _txTsn && !updatedAfter)
                {
                    continue;
                }

                var stamped = pendingDeaths != 0 || died == ArchetypeClusterState.VisibilityUnknown ? ~0UL : cs.KeyChangedSlotsAfter(clusterChunkId, _txTsn);
                var clusterBase = clusterAccessor.GetChunkAddress(clusterChunkId);
                var occupancy = Volatile.Read(ref *(ulong*)clusterBase);
                var pend = ArchetypeClusterState.ClaimOf(ref words) & ~occupancy;
                var slots = stamped & (occupancy | pend);
                while (slots != 0)
                {
                    var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(slots);
                    slots &= slots - 1;

                    // The key first: most changed slots are outside a narrow range, and the id and the set cost a line and a hash each. The slot's bytes
                    // hold the key the trees hold the entity under, if they still do.
                    var slotComp = clusterBase + src.CompOffset + slotIndex * src.CompSize;
                    var headInRange = InRange(*(TKey*)(slotComp + src.FieldOffset));

                    // No update newer than the snapshot in this cluster, or a SingleVersion component (no history): the bytes are the value at the snapshot.
                    // Validated after the read, seqlock-style: an update folds its cluster's watermark before it copies its HEAD.
                    var bytesAtSnapshot = src.VersionedIndex < 0;
                    if (!bytesAtSnapshot && !updatedAfter)
                    {
                        Volatile.ReadBarrier();
                        bytesAtSnapshot = ArchetypeClusterState.MaxUpdatedOf(ref words) <= _txTsn;
                    }

                    if (bytesAtSnapshot && !headInRange)
                    {
                        continue;   // its key at the snapshot is the slot's, outside the range
                    }

                    var rawId = *(long*)(clusterBase + cs.Layout.EntityIdsOffset + slotIndex * 8);
                    if (rawId == 0 || _dropped.Contains(rawId))
                    {
                        continue;   // no entity, or one handled already (or destroyed by this transaction)
                    }

                    // Dropped — its tree rows not this read's — only when they can be in the range. One outside the range on both counts is read again
                    // by the next refresh, which costs that much and no more.
                    if (headInRange)
                    {
                        _dropped.Add(rawId);
                    }

                    QueryPathProbe.IndexSideProbes++;
                    var visible = (pend & (1UL << slotIndex)) != 0
                        ? SnapshotSlot.IsReservedSpawnVisible(rawId, clusterChunkId, slotIndex, es, record, _txTsn, cards, ref mapAccessor)
                        : SnapshotSlot.IsVisibleAtSnapshot(rawId, true, es, record, src.RecordSize, _txTsn, cards, ref mapAccessor);
                    if (!visible)
                    {
                        continue;
                    }

                    if (src.VersionedIndex >= 0)
                    {
                        var chainRoot = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, src.VersionedIndex);
                        if (chainRoot == 0)
                        {
                            continue;   // never supplied
                        }

                        if (bytesAtSnapshot)
                        {
                            Buffer.MemoryCopy(slotComp, value, _valueSize, _valueSize);
                        }
                        else if (!_tx.TryCopyCommittedAtSnapshot(src.Meta, src.CompSlot, chainRoot, value, _valueSize))
                        {
                            continue;   // no value at the snapshot: supplied after it
                        }
                    }
                    else
                    {
                        Buffer.MemoryCopy(slotComp, value, _valueSize, _valueSize);
                    }

                    var key = *(TKey*)(value + src.FieldOffset);
                    if (InRange(key))
                    {
                        _dropped.Add(rawId);
                        AddRow(key, rawId, new ReadOnlySpan<byte>(value, _valueSize));
                    }
                }
            }
        }
        finally
        {
            PagedMMF.RefreshableWaitDepth = refreshable;
            clusterAccessor.Dispose();
            mapAccessor.Dispose();
        }
    }

    private void ReadOwnSpawns(Transaction.IndexArchetypeSource[] sources)
    {
        var destroys = _tx.PendingDestroys;
        Span<byte> value = _valueSize <= 1024 ? stackalloc byte[1024] : new byte[_valueSize];
        foreach (var spawn in _tx.PendingSpawns)
        {
            var id = spawn.Id;
            if (destroys != null && destroys.Contains(id))
            {
                continue;
            }

            var meta = _tx.DBE.GetMetaByRouting(id.ArchetypeId);
            for (var i = 0; i < sources.Length; i++)
            {
                var src = sources[i];
                if (!ReferenceEquals(src.Meta, meta) || !_tx.TryOpen(id, out var entity))
                {
                    continue;
                }

                if (entity.ReadRaw(src.CompSlot, value) < src.FieldOffset + sizeof(TKey))
                {
                    break;
                }

                _dropped.Add((long)id.RawValue);
                AddRow(MemoryMarshal.Read<TKey>(value[src.FieldOffset..]), (long)id.RawValue, value[.._valueSize]);
                break;
            }
        }
    }

    private bool InRange(TKey key) => Comparer<TKey>.Default.Compare(key, _min) >= 0 && Comparer<TKey>.Default.Compare(key, _max) <= 0;

    private void AddRow(TKey key, long rawId, ReadOnlySpan<byte> value)
    {
        if (!InRange(key))
        {
            return;
        }

        // A refresh: a row at or below the previous key the cursor read is in the answer already, from the trees.
        if (_hasFloor && Comparer<TKey>.Default.Compare(key, _floor) <= 0)
        {
            return;
        }

        if (_values == null || _valuesUsed + _valueSize > _values.Length)
        {
            var grown = ArrayPool<byte>.Shared.Rent(Math.Max(256, (_valuesUsed + _valueSize) * 2));
            if (_values != null)
            {
                Buffer.BlockCopy(_values, 0, grown, 0, _valuesUsed);
                ArrayPool<byte>.Shared.Return(_values);
            }

            _values = grown;
        }

        value.CopyTo(_values.AsSpan(_valuesUsed));
        _rows.Add(new Row { Key = key, RawId = rawId, ValueOffset = _valuesUsed });
        _valuesUsed += _valueSize;
    }

    /// <summary>Sorts the rows not yet taken by key, then entity.</summary>
    private void SortRows(int from)
    {
        if (_rows.Count - from < 2)
        {
            return;
        }

        CollectionsMarshal.AsSpan(_rows)[from..].Sort(static (a, b) =>
        {
            var c = Comparer<TKey>.Default.Compare(a.Key, b.Key);
            return c != 0 ? c : a.RawId.CompareTo(b.RawId);
        });
    }
}
