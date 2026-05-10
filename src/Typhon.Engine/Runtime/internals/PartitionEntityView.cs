using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Zero-allocation entity view over a <see cref="HashMap{TKey}"/> partition range.
/// Pre-allocated per worker per parallel system, reconfigured each chunk via <see cref="Reset"/>.
/// <para>
/// Implements both <see cref="IReadOnlyCollection{T}"/> and <see cref="IEnumerator{T}"/> —
/// <see cref="GetEnumerator()"/> returns <c>this</c>, avoiding per-chunk allocation.
/// Safe because only one foreach loop runs per chunk invocation.
/// </para>
/// <para>
/// Iteration walks the HashMap's flat entry array sequentially (L1-friendly), skipping empty slots (hash == 0).
/// Each entry stores [4-byte hash][8-byte long key]. The long key is reinterpreted as <see cref="EntityId"/>.
/// </para>
/// </summary>
internal sealed unsafe class PartitionEntityView : IReadOnlyCollection<EntityId>, IEnumerator<EntityId>
{
    private byte* _entries;
    private int _start;
    private int _end;
    private int _stride;
    private int _index;
    private int _approximateCount;
    private EntityId _current;

    /// <summary>
    /// Reconfigure this view for a new HashMap partition. O(1).
    /// </summary>
    /// <param name="map">The HashMap to iterate over.</param>
    /// <param name="partitionIndex">Zero-based index of this partition (0..totalPartitions-1).</param>
    /// <param name="totalPartitions">Total number of partitions across all workers.</param>
    public void Reset(HashMap<long> map, int partitionIndex, int totalPartitions)
    {
        _entries = map.EntriesPtr;
        _stride = map.EntryStride;
        var capacity = map.Capacity;
        _start = (int)((long)partitionIndex * capacity / totalPartitions);
        _end = (int)((long)(partitionIndex + 1) * capacity / totalPartitions);
        _index = _start - 1;
        _approximateCount = totalPartitions > 0 ? (map.Count + totalPartitions - 1) / totalPartitions : map.Count;
        _current = default;
    }

    // ═══════════════════════════════════════════════════════════════
    // IReadOnlyCollection<EntityId>
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Approximate entity count in this partition (map.Count / totalPartitions, rounded up).</summary>
    public int Count => _approximateCount;

    public IEnumerator<EntityId> GetEnumerator()
    {
        _index = _start - 1;
        _current = default;
        return this;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ═══════════════════════════════════════════════════════════════
    // IEnumerator<EntityId>
    // ═══════════════════════════════════════════════════════════════

    public EntityId Current => _current;
    object IEnumerator.Current => _current;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        while (++_index < _end)
        {
            byte* entry = _entries + (long)_index * _stride;
            if (*(uint*)entry != 0) // hash != 0 → occupied slot
            {
                _current = EntityId.FromRaw(*(long*)(entry + 4));
                return true;
            }
        }

        return false;
    }

    void IEnumerator.Reset() => _index = _start - 1;
    void IDisposable.Dispose() { } // No-op — pre-allocated, reused
}
