using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Typhon.Engine.Internals;

/// <summary>
/// High-performance in-memory hash map using open addressing with linear probing.
/// Single flat entry array — no chains, no overflow, no pointer indirection.
/// Backward-shift deletion avoids tombstone accumulation.
/// POH (Pinned Object Heap) allocation + software prefetch on resize.
/// <para>
/// JIT-specialized dual path via <see cref="RuntimeHelpers.IsReferenceOrContainsReferences{T}"/>:
/// <list type="bullet">
///   <item>Unmanaged TValue: values stored inline in entry array. Zero GC pressure.</item>
///   <item>Managed TValue: keys in entry array, values in parallel <c>TValue[]</c>.</item>
/// </list>
/// </para>
/// </summary>
internal unsafe class HashMap<TKey, TValue> : IDisposable where TKey : unmanaged, IEquatable<TKey>
{
    private const double MaxLoadFactor = 0.75;
    private const int PrefetchLookahead = 8;

    private readonly int _entryStride;
    private readonly int _valueOffset;
    private byte* _entries;
    private byte[] _pohArray;
    private int _capacity;
    private int _mask;
    private int _count;
    private int _resizeThreshold;
    private TValue[] _managedValues;

    public HashMap(int initialCapacity = 64)
    {
        _capacity = Math.Max(4, initialCapacity);
        if (!BitOperations.IsPow2(_capacity))
        {
            _capacity = (int)BitOperations.RoundUpToPowerOf2((uint)_capacity);
        }

        _valueOffset = 4 + sizeof(TKey);
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            _entryStride = (4 + sizeof(TKey) + Unsafe.SizeOf<TValue>() + 3) & ~3;
        }
        else
        {
            _entryStride = (4 + sizeof(TKey) + 3) & ~3;
            _managedValues = new TValue[_capacity];
        }

        _mask = _capacity - 1;
        _resizeThreshold = (int)(_capacity * MaxLoadFactor);

        _pohArray = GC.AllocateArray<byte>(_capacity * _entryStride, true);
        _entries = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_pohArray));
    }

    public int Count => _count;
    public int Capacity => _capacity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(TKey key, TValue value)
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
                WriteValue(entry, idx, value);
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
    public bool TryGetValue(TKey key, out TValue value)
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
                value = default;
                return false;
            }

            if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
            {
                value = ReadValue(entry, idx);
                return true;
            }

            idx = (idx + 1) & _mask;
        }
    }

    public bool TryRemove(TKey key, out TValue value)
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
                value = default;
                return false;
            }

            if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
            {
                value = ReadValue(entry, idx);
                _count--;
                BackwardShiftDelete(idx);
                return true;
            }

            idx = (idx + 1) & _mask;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOrAdd(TKey key, TValue value)
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
                WriteValue(entry, idx, value);
                _count++;
                return value;
            }

            if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
            {
                return ReadValue(entry, idx);
            }

            idx = (idx + 1) & _mask;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryUpdate(TKey key, TValue newValue)
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
                WriteValue(entry, idx, newValue);
                return true;
            }

            idx = (idx + 1) & _mask;
        }
    }

    public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue)
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
                if (!EqualityComparer<TValue>.Default.Equals(ReadValue(entry, idx), comparisonValue))
                {
                    return false;
                }
                WriteValue(entry, idx, newValue);
                return true;
            }

            idx = (idx + 1) & _mask;
        }
    }

    public TValue this[TKey key]
    {
        get
        {
            if (!TryGetValue(key, out TValue value))
            {
                throw new KeyNotFoundException($"Key not found: {key}");
            }
            return value;
        }
        set
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
                    WriteValue(entry, idx, value);
                    _count++;
                    return;
                }

                if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
                {
                    WriteValue(entry, idx, value);
                    return;
                }

                idx = (idx + 1) & _mask;
            }
        }
    }

    public void Clear()
    {
        new Span<byte>(_entries, _capacity * _entryStride).Clear();
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            Array.Clear(_managedValues);
        }
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
        private readonly HashMap<TKey, TValue> _map;
        private int _index;

        internal Enumerator(HashMap<TKey, TValue> map)
        {
            _map = map;
            _index = -1;
        }

        public (TKey Key, TValue Value) Current { get; private set; }

        public bool MoveNext()
        {
            int stride = _map._entryStride;
            while (++_index < _map._capacity)
            {
                byte* entry = _map._entries + (long)_index * stride;
                if (*(uint*)entry != 0)
                {
                    TKey key = *(TKey*)(entry + 4);
                    TValue value = _map.ReadValue(entry, _index);
                    Current = (key, value);
                    return true;
                }
            }
            return false;
        }
    }

    public void Dispose()
    {
        if (_entries == null)
        {
            return;
        }

        _pohArray = null;
        _entries = null;
        _managedValues = null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Private — value access (JIT-specialized)
    // ═══════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TValue ReadValue(byte* entry, int idx)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            return Unsafe.Read<TValue>(entry + _valueOffset);
        }
        return _managedValues[idx];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteValue(byte* entry, int idx, TValue value)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            Unsafe.Write(entry + _valueOffset, value);
        }
        else
        {
            _managedValues[idx] = value;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Private — backward shift deletion
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

                if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                {
                    _managedValues[idx] = _managedValues[j];
                }

                idx = j;
            }

            j = (j + 1) & _mask;
        }

        *(uint*)(_entries + (long)idx * stride) = 0;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            _managedValues[idx] = default;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Private — resize (POH allocation + prefetch rehash)
    // ═══════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Resize(int newCapacity)
    {
        int stride = _entryStride;
        int newMask = newCapacity - 1;
        int newSize = newCapacity * stride;

        // POH allocation: pre-zeroed pages from OS, nearly free for large buffers
        var newPoh = GC.AllocateArray<byte>(newSize, true);
        byte* newEntries = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(newPoh));

        TValue[] newManagedValues = null;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            newManagedValues = new TValue[newCapacity];
        }

        // Rehash with software prefetch to hide write latency.
        // Sse.IsSupported is a JIT constant: on non-x86 (e.g. arm64) the whole prefetch block is elided; Prefetch0 is x86-only and would otherwise throw.
        for (int i = 0; i < _capacity; i++)
        {
            if (Sse.IsSupported && i + PrefetchLookahead < _capacity)
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

                if (newManagedValues != null)
                {
                    newManagedValues[idx] = _managedValues[i];
                }
            }
        }

        // Release old POH array (GC collects it)

        _entries = newEntries;
        _pohArray = newPoh;
        _capacity = newCapacity;
        _mask = newMask;
        _resizeThreshold = (int)(newCapacity * MaxLoadFactor);

        if (newManagedValues != null)
        {
            _managedValues = newManagedValues;
        }
    }
}
