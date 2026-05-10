using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Typhon.Engine.Internals;

/// <summary>
/// High-performance in-memory hash set using open addressing with linear probing.
/// Single flat entry array — no chains, no overflow, no pointer indirection.
/// Backward-shift deletion avoids tombstone accumulation.
/// POH (Pinned Object Heap) allocation + software prefetch on resize.
/// </summary>
internal unsafe class HashMap<TKey> : IDisposable, IEnumerable<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    private const double MaxLoadFactor = 0.75;
    private const int PrefetchLookahead = 8;

    private readonly int _entryStride;
    private byte* _entries;
    private byte[] _pohArray; // POH array reference — prevents GC collection
    private int _capacity;
    private int _mask;
    private int _count;
    private int _resizeThreshold;

    public HashMap(int initialCapacity = 64)
    {
        _capacity = Math.Max(4, initialCapacity);
        if (!BitOperations.IsPow2(_capacity))
        {
            _capacity = (int)BitOperations.RoundUpToPowerOf2((uint)_capacity);
        }

        _entryStride = (4 + sizeof(TKey) + 3) & ~3;
        _mask = _capacity - 1;
        _resizeThreshold = (int)(_capacity * MaxLoadFactor);

        _pohArray = GC.AllocateArray<byte>(_capacity * _entryStride, true);
        _entries = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_pohArray));
    }

    public int Count => _count;
    public int Capacity => _capacity;

    /// <summary>Raw pointer to the entries array. Stable (POH allocation).</summary>
    internal byte* EntriesPtr => _entries;

    /// <summary>Stride in bytes between consecutive entries.</summary>
    internal int EntryStride => _entryStride;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(TKey key)
    {
        if (_count >= _resizeThreshold)
        {
            Resize(_capacity * 2);
        }

        uint hash = HashUtils.ComputeHash(key);
        if (hash == 0)
        {
            hash = 1;
        }

        int idx = (int)(hash & (uint)_mask);
        int stride = _entryStride;

        while (true)
        {
            byte* entry = _entries + (long)idx * stride;
            uint h = *(uint*)entry;

            if (h == 0)
            {
                *(uint*)entry = hash;
                *(TKey*)(entry + 4) = key;
                _count++;
                return true;
            }

            if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
            {
                return false;
            }

            idx = (idx + 1) & _mask;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(TKey key)
    {
        uint hash = HashUtils.ComputeHash(key);
        if (hash == 0)
        {
            hash = 1;
        }

        int idx = (int)(hash & (uint)_mask);
        int stride = _entryStride;

        while (true)
        {
            byte* entry = _entries + (long)idx * stride;
            uint h = *(uint*)entry;

            if (h == 0)
            {
                return false;
            }

            if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
            {
                return true;
            }

            idx = (idx + 1) & _mask;
        }
    }

    public bool TryRemove(TKey key)
    {
        uint hash = HashUtils.ComputeHash(key);
        if (hash == 0)
        {
            hash = 1;
        }

        int idx = (int)(hash & (uint)_mask);
        int stride = _entryStride;

        while (true)
        {
            byte* entry = _entries + (long)idx * stride;
            uint h = *(uint*)entry;

            if (h == 0)
            {
                return false;
            }

            if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
            {
                _count--;
                BackwardShiftDelete(idx);
                return true;
            }

            idx = (idx + 1) & _mask;
        }
    }

    public void Clear()
    {
        new Span<byte>(_entries, _capacity * _entryStride).Clear();
        _count = 0;
    }

    public void EnsureCapacity(int minimumEntries)
    {
        int needed = (int)(minimumEntries / MaxLoadFactor) + 1;
        needed = (int)BitOperations.RoundUpToPowerOf2((uint)needed);
        if (needed > _capacity)
        {
            Resize(needed);
        }
    }

    public Enumerator GetEnumerator() => new(this);

    public ref struct Enumerator
    {
        private readonly HashMap<TKey> _map;
        private int _index;

        internal Enumerator(HashMap<TKey> map)
        {
            _map = map;
            _index = -1;
        }

        public TKey Current { get; private set; }

        public bool MoveNext()
        {
            int stride = _map._entryStride;
            while (++_index < _map._capacity)
            {
                byte* entry = _map._entries + (long)_index * stride;
                if (*(uint*)entry != 0)
                {
                    Current = *(TKey*)(entry + 4);
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Returns a partition enumerator that walks a contiguous slice of the entries array.
    /// Each partition yields only the entries in its index range — no cross-partition contamination.
    /// O(1) setup, O(capacity/totalPartitions) iteration. Sequential L1-friendly access pattern.
    /// </summary>
    public PartitionEnumerator GetPartitionEnumerator(int partitionIndex, int totalPartitions)
    {
        int start = (int)((long)partitionIndex * _capacity / totalPartitions);
        int end = (int)((long)(partitionIndex + 1) * _capacity / totalPartitions);
        return new PartitionEnumerator(_entries, start, end, _entryStride);
    }

    public ref struct PartitionEnumerator
    {
        private readonly byte* _entries;
        private readonly int _end;
        private readonly int _stride;
        private int _index;

        internal PartitionEnumerator(byte* entries, int start, int end, int stride)
        {
            _entries = entries;
            _end = end;
            _stride = stride;
            _index = start - 1;
        }

        public TKey Current { get; private set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            while (++_index < _end)
            {
                byte* entry = _entries + (long)_index * _stride;
                if (*(uint*)entry != 0)
                {
                    Current = *(TKey*)(entry + 4);
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Creates a shallow copy with an independent entries array. Both the original and clone
    /// can be modified independently. Used by EcsView.RefreshFull for old-set snapshotting.
    /// </summary>
    public HashMap<TKey> Clone()
    {
        var clone = new HashMap<TKey>(_capacity);
        Buffer.MemoryCopy(_entries, clone._entries, clone._capacity * _entryStride, _capacity * _entryStride);
        clone._count = _count;
        return clone;
    }

    // IEnumerable<TKey> — boxed fallback for interface-based foreach (non-hot-path)
    IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() => new BoxedEnumerator(this);
    IEnumerator IEnumerable.GetEnumerator() => new BoxedEnumerator(this);

    private class BoxedEnumerator : IEnumerator<TKey>
    {
        private readonly HashMap<TKey> _map;
        private int _index = -1;

        public BoxedEnumerator(HashMap<TKey> map) => _map = map;
        public TKey Current { get; private set; }
        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_map._entries == null)
            {
                return false;
            }

            int stride = _map._entryStride;
            while (++_index < _map._capacity)
            {
                byte* entry = _map._entries + (long)_index * stride;
                if (*(uint*)entry != 0)
                {
                    Current = *(TKey*)(entry + 4);
                    return true;
                }
            }
            return false;
        }

        public void Reset() => _index = -1;
        public void Dispose() { }
    }

    public void Dispose()
    {
        if (_entries == null)
        {
            return;
        }

        _pohArray = null;
        _entries = null;
        _count = 0;
    }

    // ═══════════════════════════════════════════════════════════════
    // Private
    // ═══════════════════════════════════════════════════════════════

    private void BackwardShiftDelete(int idx)
    {
        int stride = _entryStride;
        int j = (idx + 1) & _mask;

        while (true)
        {
            byte* entryJ = _entries + (long)j * stride;
            uint hj = *(uint*)entryJ;

            if (hj == 0)
            {
                break;
            }

            int homeJ = (int)(hj & (uint)_mask);
            int distI = (idx - homeJ + _capacity) & _mask;
            int distJ = (j - homeJ + _capacity) & _mask;

            if (distI < distJ)
            {
                Unsafe.CopyBlock(_entries + (long)idx * stride, entryJ, (uint)stride);
                idx = j;
            }

            j = (j + 1) & _mask;
        }

        *(uint*)(_entries + (long)idx * stride) = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Resize(int newCapacity)
    {
        int stride = _entryStride;
        int newMask = newCapacity - 1;
        int newSize = newCapacity * stride;

        // POH allocation: pre-zeroed pages from OS, nearly free for large buffers
        var newPoh = GC.AllocateArray<byte>(newSize, true);
        byte* newEntries = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(newPoh));

        // Rehash with software prefetch to hide write latency
        for (int i = 0; i < _capacity; i++)
        {
            if (i + PrefetchLookahead < _capacity)
            {
                byte* future = _entries + (long)(i + PrefetchLookahead) * stride;
                uint fh = *(uint*)future;
                if (fh != 0)
                {
                    Sse.Prefetch0(newEntries + (long)((int)(fh & (uint)newMask)) * stride);
                }
            }

            byte* entry = _entries + (long)i * stride;
            uint h = *(uint*)entry;
            if (h != 0)
            {
                int idx = (int)(h & (uint)newMask);
                while (*(uint*)(newEntries + (long)idx * stride) != 0)
                {
                    idx = (idx + 1) & newMask;
                }
                Unsafe.CopyBlock(newEntries + (long)idx * stride, entry, (uint)stride);
            }
        }

        // Release old POH array (GC collects it)

        _entries = newEntries;
        _pohArray = newPoh;
        _capacity = newCapacity;
        _mask = newMask;
        _resizeThreshold = (int)(newCapacity * MaxLoadFactor);
    }
}
