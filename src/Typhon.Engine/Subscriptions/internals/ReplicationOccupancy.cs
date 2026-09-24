using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// The replication grid's occupancy (<c>claude/design/Subscriptions/10-phase15-3d-groundwork.md</c> § 2.4): per occupied cell, how many live entities of
/// observed archetypes have their last pushed position there. A cell with no entry holds nothing a delivery or a sweep could find, so its cluster query is
/// skipped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Maintained from the index, not recomputed.</b> Every change of an entity's cell makes an event (SUB-19), so the tick's sorted events carry the whole
/// delta: a primary that entered its cell adds one, a secondary (the cell a mover left) or a leave-only event takes one away. Only cells whose count
/// changed are touched, so the cost follows cell crossings, not the population or the grid.
/// </para>
/// <para>
/// <b>Sparse.</b> Open addressing on the packed cell key, deleted by backward shift when a count reaches zero, so the table holds exactly the occupied
/// cells and never tombstones. Single writer: the index's serial finish.
/// </para>
/// <para>
/// <b>Ordered on demand</b> (<see cref="Ordered"/>): the occupied keys ascending, what a <c>World</c> session's delivery walks. Built the first time it is
/// asked for; from then on every cell that appears or empties is noted, and the next call merges the noted cells into the order — O(occupied cells) on a
/// tick whose set changed, nothing otherwise, and nothing at all for a runtime that never asks.
/// </para>
/// </remarks>
internal sealed class ReplicationOccupancy
{
    // key + 1, so zero marks an empty slot.
    private ulong[] _keys = new ulong[1024];
    private int[] _counts = new int[1024];
    private int _mask = 1023;

    // The ordered view: the occupied keys ascending, valid up to the cells noted since (_toggled, unsorted, possibly repeated).
    private bool _ordering;
    private bool _orderStale = true;
    private ulong[] _sorted = [];
    private ulong[] _sortedNext = [];
    private int _sortedCount;
    private ulong[] _toggled = new ulong[64];
    private int _toggledCount;

    /// <summary>Cells with at least one entity.</summary>
    public int Count { get; private set; }

    /// <summary>Decrements that would have taken a cell below zero: an entity leaving a cell it was never counted in. Zero when the map is sound.</summary>
    public long Underflows;

    /// <summary>The live entities whose last pushed position lies in the cell; zero for a cell the map does not hold.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Get(ulong key)
    {
        var tagged = key + 1;
        var h = Hash(key) & _mask;
        while (true)
        {
            var k = _keys[h];
            if (k == tagged)
            {
                return _counts[h];
            }

            if (k == 0)
            {
                return 0;
            }

            h = (h + 1) & _mask;
        }
    }

    /// <summary>Adds <paramref name="delta"/> to a cell's count, inserting the cell or removing it when its count reaches zero.</summary>
    public void Add(ulong key, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        var tagged = key + 1;
        var h = Hash(key) & _mask;
        while (true)
        {
            var k = _keys[h];
            if (k == tagged)
            {
                var count = _counts[h] + delta;
                if (count > 0)
                {
                    _counts[h] = count;
                    return;
                }

                if (count < 0)
                {
                    Underflows++;
                }

                RemoveAt(h);
                Toggled(key);
                return;
            }

            if (k == 0)
            {
                if (delta < 0)
                {
                    Underflows++;
                    return;
                }

                _keys[h] = tagged;
                _counts[h] = delta;
                Toggled(key);
                if (++Count * 2 > _keys.Length)
                {
                    Grow();
                }

                return;
            }

            h = (h + 1) & _mask;
        }
    }

    /// <summary>Empties the map, keeping its table.</summary>
    public void Clear()
    {
        Array.Clear(_keys);
        Count = 0;
        _orderStale = true;
    }

    /// <summary>
    /// The occupied cells' keys, ascending. Serial: called between index finishes, never while the map changes. The span is valid until the next call.
    /// </summary>
    public ReadOnlySpan<ulong> Ordered()
    {
        if (!_ordering || _orderStale)
        {
            RebuildOrder();
        }
        else if (_toggledCount > 0)
        {
            MergeToggled();
        }

        return new ReadOnlySpan<ulong>(_sorted, 0, _sortedCount);
    }

    /// <summary>
    /// <see cref="Ordered"/> as the array behind it and its length, for a reader that holds it across a parallel stage: valid until the next call to
    /// either, which only a serial point makes.
    /// </summary>
    public ulong[] OrderedKeys(out int count)
    {
        count = Ordered().Length;
        return _sorted;
    }

    private void Toggled(ulong key)
    {
        if (!_ordering || _orderStale)
        {
            return;
        }

        // Past the map's own size a merge costs no less than a rebuild, and the list would grow without bound while nobody asks for the order.
        if (_toggledCount == _toggled.Length)
        {
            if (_toggledCount >= Math.Max(64, Count))
            {
                _orderStale = true;
                _toggledCount = 0;
                return;
            }

            Array.Resize(ref _toggled, _toggledCount * 2);
        }

        _toggled[_toggledCount++] = key;
    }

    private void RebuildOrder()
    {
        if (_sorted.Length < Count)
        {
            _sorted = new ulong[Math.Max(64, Count + (Count >> 1))];
        }

        var n = 0;
        for (var i = 0; i < _keys.Length; i++)
        {
            if (_keys[i] != 0)
            {
                _sorted[n++] = _keys[i] - 1;
            }
        }

        Array.Sort(_sorted, 0, n);
        _sortedCount = n;
        _toggledCount = 0;
        _ordering = true;
        _orderStale = false;
    }

    // One pass over the order and the noted cells, both ascending: a noted cell is in the order when it holds something now, whatever it held before.
    private void MergeToggled()
    {
        Array.Sort(_toggled, 0, _toggledCount);
        if (_sortedNext.Length < Count)
        {
            _sortedNext = new ulong[Math.Max(64, Count + (Count >> 1))];
        }

        var output = _sortedNext;
        int i = 0, j = 0, n = 0;
        while (i < _sortedCount || j < _toggledCount)
        {
            if (j == _toggledCount || (i < _sortedCount && _sorted[i] < _toggled[j]))
            {
                output[n++] = _sorted[i++];
                continue;
            }

            var key = _toggled[j];
            while (j < _toggledCount && _toggled[j] == key)
            {
                j++;
            }

            if (i < _sortedCount && _sorted[i] == key)
            {
                i++;
            }

            if (Get(key) != 0)
            {
                output[n++] = key;
            }
        }

        (_sorted, _sortedNext) = (_sortedNext, _sorted);
        _sortedCount = n;
        _toggledCount = 0;
    }

    /// <summary>The cells whose counts differ between this map and <paramref name="other"/>, counted from both sides.</summary>
    public int Differences(ReplicationOccupancy other)
    {
        var differences = 0;
        for (var i = 0; i < _keys.Length; i++)
        {
            if (_keys[i] != 0 && other.Get(_keys[i] - 1) != _counts[i])
            {
                differences++;
            }
        }

        for (var i = 0; i < other._keys.Length; i++)
        {
            if (other._keys[i] != 0 && Get(other._keys[i] - 1) == 0)
            {
                differences++;
            }
        }

        return differences;
    }

    // Backward-shift deletion: every entry after the hole that would still be found from its home slot moves back into it, so no probe sequence breaks.
    private void RemoveAt(int hole)
    {
        var j = hole;
        while (true)
        {
            j = (j + 1) & _mask;
            var k = _keys[j];
            if (k == 0)
            {
                break;
            }

            var home = Hash(k - 1) & _mask;
            var between = hole <= j ? home > hole && home <= j : home > hole || home <= j;
            if (between)
            {
                continue;
            }

            _keys[hole] = k;
            _counts[hole] = _counts[j];
            hole = j;
        }

        _keys[hole] = 0;
        Count--;
    }

    private void Grow()
    {
        var keys = _keys;
        var counts = _counts;
        _keys = new ulong[keys.Length * 2];
        _counts = new int[keys.Length * 2];
        _mask = _keys.Length - 1;
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i] == 0)
            {
                continue;
            }

            var h = Hash(keys[i] - 1) & _mask;
            while (_keys[h] != 0)
            {
                h = (h + 1) & _mask;
            }

            _keys[h] = keys[i];
            _counts[h] = counts[i];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(ulong key) => (int)((key * 0x9E3779B97F4A7C15UL) >> 32);
}
