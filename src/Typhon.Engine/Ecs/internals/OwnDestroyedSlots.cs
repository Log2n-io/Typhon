using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// The cluster slots one transaction's own pending destroys occupy in one archetype — what a cluster walk through that transaction subtracts from the
/// occupancy word (CLUSTERWALK-04). Owned by the <see cref="Transaction"/>, built lazily by <see cref="Transaction.OwnDestroyedSlotsFor"/>, read by
/// <see cref="ClusterEnumerator{TArch}.MoveNext"/> once per cluster.
/// </summary>
/// <remarks>
/// <para>
/// Two levels, so the common miss costs one L1 load: <see cref="_filter"/> holds one bit per cluster chunk id ("this cluster holds an own destroy"), and only
/// on a hit is the small open-addressed <c>(chunkId, slot mask)</c> table probed. A transaction destroys a handful of entities in a handful of clusters, so
/// the table stays within a cache line or two and never allocates on the walk.
/// </para>
/// <para>
/// A recorded slot is where the entity WAS when it was located. It is hidden only while the slot still holds that entity — the id stored in the slot is
/// confirmed against the transaction's pending destroys — so a slot another transaction's committed destroy released and a spawn reused stays visible. A
/// fence migration moving the entity is caught by <see cref="ArchetypeClusterState.SlotMoveEpoch"/>: the owner resets this table and locates every destroy
/// again.
/// </para>
/// </remarks>
internal sealed unsafe class OwnDestroyedSlots
{
    private const int InitialTableCapacity = 8;

    internal ushort RoutingId;
    internal ArchetypeClusterState State;
    internal long MoveEpoch;

    /// <summary>How many entries of the transaction's destroy order this table has located.</summary>
    internal int Scanned;

    private HashSet<EntityId> _destroys;
    private int _entityIdsOffset;
    private ulong[] _filter = [];

    // Open-addressed, linear probing, power-of-two capacity. A key is chunkId + 1: 0 marks an empty bucket.
    private int[] _keys = new int[InitialTableCapacity];
    private ulong[] _masks = new ulong[InitialTableCapacity];
    private int _count;

    internal bool IsEmpty => _count == 0;

    /// <summary>Start over for <paramref name="routingId"/>: forget every located slot. O(clusters recorded), not O(filter).</summary>
    internal void Reset(ushort routingId, ArchetypeClusterState state, long moveEpoch, HashSet<EntityId> destroys)
    {
        if (_count > 0)
        {
            for (var i = 0; i < _keys.Length; i++)
            {
                var key = _keys[i];
                if (key != 0)
                {
                    var chunkId = key - 1;
                    _filter[chunkId >> 6] &= ~(1UL << chunkId);
                    _keys[i] = 0;
                    _masks[i] = 0;
                }
            }
            _count = 0;
        }

        RoutingId = routingId;
        State = state;
        MoveEpoch = moveEpoch;
        Scanned = 0;
        _destroys = destroys;
        _entityIdsOffset = state.Layout.EntityIdsOffset;
    }

    /// <summary>Record that an own pending destroy holds <paramref name="slot"/> of cluster <paramref name="chunkId"/>.</summary>
    internal void Add(int chunkId, int slot)
    {
        var word = chunkId >> 6;
        if (word >= _filter.Length)
        {
            Array.Resize(ref _filter, Math.Max(word + 1, _filter.Length * 2));
        }
        _filter[word] |= 1UL << chunkId;

        if ((_count + 1) * 2 > _keys.Length)
        {
            Grow();
        }

        var i = Find(chunkId + 1);
        if (_keys[i] == 0)
        {
            _keys[i] = chunkId + 1;
            _count++;
        }
        _masks[i] |= 1UL << slot;
    }

    /// <summary>
    /// <paramref name="occupancy"/> of cluster <paramref name="chunkId"/>, minus the slots that hold one of the transaction's own pending destroys.
    /// <paramref name="clusterBase"/> is the cluster's primary base (the chunk whose word 0 is the occupancy and which holds the entity ids).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ulong Unhidden(int chunkId, ulong occupancy, byte* clusterBase)
    {
        var word = (uint)chunkId >> 6;
        var filter = _filter;
        return word < (uint)filter.Length && (filter[word] & (1UL << chunkId)) != 0 ? UnhiddenSlow(chunkId, occupancy, clusterBase) : occupancy;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong UnhiddenSlow(int chunkId, ulong occupancy, byte* clusterBase)
    {
        var ids = clusterBase + _entityIdsOffset;
        for (var candidates = _masks[Find(chunkId + 1)] & occupancy; candidates != 0; candidates &= candidates - 1)
        {
            var slot = BitOperations.TrailingZeroCount(candidates);
            // The slot holds the destroyed entity NOW — not a live entity that reused it after another transaction's committed destroy.
            if (_destroys.Contains(EntityId.FromRaw(*(long*)(ids + slot * 8))))
            {
                occupancy &= ~(1UL << slot);
            }
        }
        return occupancy;
    }

    /// <summary>The bucket holding <paramref name="key"/>, or the empty bucket where it would go. The table is never full (load ≤ 1/2).</summary>
    private int Find(int key)
    {
        var mask = _keys.Length - 1;
        var i = (int)(((uint)key * 0x9E3779B9u) >> 16) & mask;
        while (_keys[i] != 0 && _keys[i] != key)
        {
            i = (i + 1) & mask;
        }
        return i;
    }

    private void Grow()
    {
        var oldKeys = _keys;
        var oldMasks = _masks;
        _keys = new int[oldKeys.Length * 2];
        _masks = new ulong[oldKeys.Length * 2];
        for (var i = 0; i < oldKeys.Length; i++)
        {
            if (oldKeys[i] != 0)
            {
                var j = Find(oldKeys[i]);
                _keys[j] = oldKeys[i];
                _masks[j] = oldMasks[i];
            }
        }
    }
}

/// <summary>
/// One walk's view of its transaction's own pending destroys in one archetype (CLUSTERWALK-04): applied to each cluster's occupancy as the walk opens it.
/// Held by value in the walker — <see cref="ClusterEnumerator{TArch}"/>, the transaction-bound <see cref="AabbClusterEnumerator"/> and radius batch — and
/// never copied once the walk started. Default (no accessor, an engine walk): <see cref="Apply"/> returns the occupancy untouched for one null test; a
/// non-transaction accessor costs one more compare (its <see cref="EntityAccessor.OwnDestroyCount"/> stays 0).
/// </summary>
internal unsafe struct OwnDestroyFilter
{
    private readonly EntityAccessor _owner;   // the walking accessor; only a Transaction ever has an own destroy
    private readonly ArchetypeMetadata _meta;   // the walker holds the cluster state already and passes it to Apply: not kept twice
    private OwnDestroyedSlots _hidden;
    private int _hiddenAtCount;

    internal OwnDestroyFilter(EntityAccessor owner, ArchetypeMetadata meta)
    {
        _owner = owner;
        _meta = meta;
        _hidden = null;
        _hiddenAtCount = 0;
    }

    /// <summary>
    /// <paramref name="occupancy"/> of cluster <paramref name="chunkId"/> minus the transaction's own pending destroys. Re-reads the transaction's destroy
    /// count per cluster, so a destroy made mid-walk applies from the next cluster. <paramref name="clusterBase"/> is the cluster's primary base,
    /// <paramref name="state"/> the walked archetype's cluster state.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ulong Apply(int chunkId, ulong occupancy, byte* clusterBase, ArchetypeClusterState state)
    {
        if (_owner == null || occupancy == 0)
        {
            return occupancy;
        }
        if (_owner.OwnDestroyCount != _hiddenAtCount)
        {
            Refresh(state);
        }
        return _hidden == null ? occupancy : _hidden.Unhidden(chunkId, occupancy, clusterBase);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Refresh(ArchetypeClusterState state)
    {
        _hiddenAtCount = _owner.OwnDestroyCount;
        _hidden = _owner.OwnDestroyedSlotsFor(_meta, state);
    }
}
