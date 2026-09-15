using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// High-performance in-memory hash set using open addressing with linear probing.
/// Single flat entry array — no chains, no overflow, no pointer indirection.
/// Backward-shift deletion avoids tombstone accumulation.
/// </summary>
/// <remarks>
/// <para>Entries are <c>[uint hash][TKey key]</c>, <see cref="EntryStride"/> bytes apart (hash 0 = empty), in an ordinary managed <c>byte[]</c> reached through
/// <c>ref</c>s. Raw pointers are reserved for page-cache and engine-allocator memory (CLAUDE.md, Unsafe Code). This used to be a pinned-object-heap array
/// reached through a <c>byte*</c> — pinned stops the GC moving an array, not freeing it, and that exact pattern let
/// <c>RawValuePagedHashMap.ForEachEntry</c> write into a freed buffer. A <c>ref</c> keeps its array alive and follows it if it moves, and compiles to the
/// same address arithmetic.</para>
/// </remarks>
internal class HashMap<TKey> : IDisposable, IEnumerable<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    private const double MaxLoadFactor = 0.75;

    private readonly int _entryStride;
    private byte[] _entries;
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

        _entryStride = (4 + Unsafe.SizeOf<TKey>() + 3) & ~3;
        _mask = _capacity - 1;
        _resizeThreshold = (int)(_capacity * MaxLoadFactor);
        _entries = new byte[_capacity * _entryStride];
    }

    public int Count => _count;
    public int Capacity => _capacity;

    /// <summary>The entry array. Each entry is <c>[uint hash][TKey key]</c>, <see cref="EntryStride"/> bytes apart; hash 0 means empty.</summary>
    internal byte[] Entries => _entries;

    /// <summary>Stride in bytes between consecutive entries.</summary>
    internal int EntryStride => _entryStride;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte EntryAt(byte[] entries, int idx, int stride) =>
        ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), (nint)idx * stride);

    /// <summary>The entry's hash. Aligned: the stride is a multiple of 4 and array data is 8-aligned.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref uint HashOf(ref byte entry) => ref Unsafe.As<byte, uint>(ref entry);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TKey KeyOf(ref byte entry) => Unsafe.ReadUnaligned<TKey>(ref Unsafe.Add(ref entry, 4));

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
        ref byte entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        while (true)
        {
            ref byte entry = ref Unsafe.Add(ref entries, (nint)idx * stride);
            uint h = HashOf(ref entry);

            if (h == 0)
            {
                HashOf(ref entry) = hash;
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref entry, 4), key);
                _count++;
                return true;
            }

            if (h == hash && KeyOf(ref entry).Equals(key))
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
        ref byte entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        while (true)
        {
            ref byte entry = ref Unsafe.Add(ref entries, (nint)idx * stride);
            uint h = HashOf(ref entry);

            if (h == 0)
            {
                return false;
            }

            if (h == hash && KeyOf(ref entry).Equals(key))
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
        ref byte entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        while (true)
        {
            ref byte entry = ref Unsafe.Add(ref entries, (nint)idx * stride);
            uint h = HashOf(ref entry);

            if (h == 0)
            {
                return false;
            }

            if (h == hash && KeyOf(ref entry).Equals(key))
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
        Array.Clear(_entries);
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

    /// <summary>Enumerates the table the map had when enumeration began; the map must not be modified meanwhile.</summary>
    public ref struct Enumerator
    {
        // A ref to the table data, not the map: MoveNext then needs no load through the map and no null check. The ref is GC-tracked and keeps the
        // table alive.
        private readonly ref byte _entries;
        private readonly int _capacity;
        private readonly int _stride;
        private int _index;

        internal Enumerator(HashMap<TKey> map)
        {
            _entries = ref MemoryMarshal.GetArrayDataReference(map._entries);
            _capacity = map._capacity;
            _stride = map._entryStride;
            _index = -1;
        }

        public TKey Current { get; private set; }

        public bool MoveNext()
        {
            ref byte entries = ref _entries;
            int stride = _stride;
            int capacity = _capacity;
            int index = _index;
            while (++index < capacity)
            {
                ref byte entry = ref Unsafe.Add(ref entries, (nint)index * stride);
                if (HashOf(ref entry) != 0)
                {
                    _index = index;
                    Current = KeyOf(ref entry);
                    return true;
                }
            }
            _index = index;
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
        // A ref to the table data (GC-tracked, keeps the table alive): MoveNext needs no null check and no array-offset add.
        private readonly ref byte _entries;
        private readonly int _end;
        private readonly int _stride;
        private int _index;

        internal PartitionEnumerator(byte[] entries, int start, int end, int stride)
        {
            _entries = ref MemoryMarshal.GetArrayDataReference(entries);
            _end = end;
            _stride = stride;
            _index = start - 1;
        }

        public TKey Current { get; private set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            ref byte entries = ref _entries;
            int stride = _stride;
            int end = _end;
            int index = _index;
            while (++index < end)
            {
                ref byte entry = ref Unsafe.Add(ref entries, (nint)index * stride);
                if (HashOf(ref entry) != 0)
                {
                    _index = index;
                    Current = KeyOf(ref entry);
                    return true;
                }
            }
            _index = index;
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
        _entries.AsSpan().CopyTo(clone._entries);
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
            var entries = _map._entries;
            if (entries == null)
            {
                return false;
            }

            int stride = _map._entryStride;
            while (++_index < _map._capacity)
            {
                ref byte entry = ref EntryAt(entries, _index, stride);
                if (HashOf(ref entry) != 0)
                {
                    Current = KeyOf(ref entry);
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

        _entries = null;
        _count = 0;
    }

    // ═══════════════════════════════════════════════════════════════
    // Private
    // ═══════════════════════════════════════════════════════════════

    private void BackwardShiftDelete(int idx)
    {
        int stride = _entryStride;
        ref byte entries = ref MemoryMarshal.GetArrayDataReference(_entries);
        int j = (idx + 1) & _mask;

        while (true)
        {
            ref byte entryJ = ref Unsafe.Add(ref entries, (nint)j * stride);
            uint hj = HashOf(ref entryJ);

            if (hj == 0)
            {
                break;
            }

            int homeJ = (int)(hj & (uint)_mask);
            int distI = (idx - homeJ + _capacity) & _mask;
            int distJ = (j - homeJ + _capacity) & _mask;

            if (distI < distJ)
            {
                Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref entries, (nint)idx * stride), ref entryJ, (uint)stride);
                idx = j;
            }

            j = (j + 1) & _mask;
        }

        HashOf(ref Unsafe.Add(ref entries, (nint)idx * stride)) = 0;
    }

    /// <summary>
    /// Rehash into a fresh array. (The old pinned-heap version prefetched each destination; a prefetch needs a raw address, so it went with the pointers.)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Resize(int newCapacity)
    {
        int stride = _entryStride;
        int newMask = newCapacity - 1;
        var newEntries = new byte[newCapacity * stride];
        ref byte src = ref MemoryMarshal.GetArrayDataReference(_entries);
        ref byte dst = ref MemoryMarshal.GetArrayDataReference(newEntries);

        for (int i = 0; i < _capacity; i++)
        {
            ref byte entry = ref Unsafe.Add(ref src, (nint)i * stride);
            uint h = HashOf(ref entry);
            if (h != 0)
            {
                int idx = (int)(h & (uint)newMask);
                while (HashOf(ref Unsafe.Add(ref dst, (nint)idx * stride)) != 0)
                {
                    idx = (idx + 1) & newMask;
                }
                Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref dst, (nint)idx * stride), ref entry, (uint)stride);
            }
        }

        _entries = newEntries;
        _capacity = newCapacity;
        _mask = newMask;
        _resizeThreshold = (int)(newCapacity * MaxLoadFactor);
    }
}
