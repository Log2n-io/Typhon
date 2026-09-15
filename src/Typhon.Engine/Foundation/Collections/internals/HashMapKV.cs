using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// High-performance in-memory hash map using open addressing with linear probing.
/// Single flat entry array — no chains, no overflow, no pointer indirection.
/// Backward-shift deletion avoids tombstone accumulation.
/// <para>
/// JIT-specialized dual path via <see cref="RuntimeHelpers.IsReferenceOrContainsReferences{T}"/>:
/// <list type="bullet">
///   <item>Unmanaged TValue: values stored inline in entry array. Zero GC pressure.</item>
///   <item>Managed TValue: keys in entry array, values in parallel <c>TValue[]</c>.</item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// The entry array is an ordinary managed <c>byte[]</c> reached through <c>ref</c>s, never a pointer — see <see cref="HashMap{TKey}"/> for why the
/// pinned-heap + <c>byte*</c> version had to go.
/// </remarks>
internal class HashMap<TKey, TValue> : IDisposable where TKey : unmanaged, IEquatable<TKey>
{
    private const double MaxLoadFactor = 0.75;

    private readonly int _entryStride;
    private readonly int _valueOffset;
    private byte[] _entries;
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

        _valueOffset = 4 + Unsafe.SizeOf<TKey>();
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            _entryStride = (4 + Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>() + 3) & ~3;
        }
        else
        {
            _entryStride = (4 + Unsafe.SizeOf<TKey>() + 3) & ~3;
            _managedValues = new TValue[_capacity];
        }

        _mask = _capacity - 1;
        _resizeThreshold = (int)(_capacity * MaxLoadFactor);
        _entries = new byte[_capacity * _entryStride];
    }

    public int Count => _count;
    public int Capacity => _capacity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte EntryAt(byte[] entries, int idx, int stride) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), (nint)idx * stride);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref uint HashOf(ref byte entry) => ref Unsafe.As<byte, uint>(ref entry);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TKey KeyOf(ref byte entry) => Unsafe.ReadUnaligned<TKey>(ref Unsafe.Add(ref entry, 4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetKey(ref byte entry, uint hash, TKey key)
    {
        HashOf(ref entry) = hash;
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref entry, 4), key);
    }

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
        ref byte entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        while (true)
        {
            ref byte entry = ref Unsafe.Add(ref entries, (nint)idx * stride);
            uint h = HashOf(ref entry);

            if (h == 0)
            {
                SetKey(ref entry, hash, key);
                WriteValue(ref entry, idx, value);
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
    public bool TryGetValue(TKey key, out TValue value)
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
                value = default;
                return false;
            }

            if (h == hash && KeyOf(ref entry).Equals(key))
            {
                value = ReadValue(ref entry, idx);
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
        ref byte entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        while (true)
        {
            ref byte entry = ref Unsafe.Add(ref entries, (nint)idx * stride);
            uint h = HashOf(ref entry);

            if (h == 0)
            {
                value = default;
                return false;
            }

            if (h == hash && KeyOf(ref entry).Equals(key))
            {
                value = ReadValue(ref entry, idx);
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
        ref byte entries = ref MemoryMarshal.GetArrayDataReference(_entries);

        while (true)
        {
            ref byte entry = ref Unsafe.Add(ref entries, (nint)idx * stride);
            uint h = HashOf(ref entry);

            if (h == 0)
            {
                SetKey(ref entry, hash, key);
                WriteValue(ref entry, idx, value);
                _count++;
                return value;
            }

            if (h == hash && KeyOf(ref entry).Equals(key))
            {
                return ReadValue(ref entry, idx);
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
                WriteValue(ref entry, idx, newValue);
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
                if (!EqualityComparer<TValue>.Default.Equals(ReadValue(ref entry, idx), comparisonValue))
                {
                    return false;
                }
                WriteValue(ref entry, idx, newValue);
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
            ref byte entries = ref MemoryMarshal.GetArrayDataReference(_entries);

            while (true)
            {
                ref byte entry = ref Unsafe.Add(ref entries, (nint)idx * stride);
                uint h = HashOf(ref entry);

                if (h == 0)
                {
                    SetKey(ref entry, hash, key);
                    WriteValue(ref entry, idx, value);
                    _count++;
                    return;
                }

                if (h == hash && KeyOf(ref entry).Equals(key))
                {
                    WriteValue(ref entry, idx, value);
                    return;
                }

                idx = (idx + 1) & _mask;
            }
        }
    }

    public void Clear()
    {
        Array.Clear(_entries);
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

    /// <summary>Enumerates the table and values the map had when enumeration began; the map must not be modified meanwhile.</summary>
    public ref struct Enumerator
    {
        // A ref to the table data and the values array, not the map: MoveNext then needs no load through the map and no null check. The ref is
        // GC-tracked and keeps the table alive.
        private readonly ref byte _entries;
        private readonly TValue[] _managedValues;
        private readonly int _capacity;
        private readonly int _stride;
        private readonly int _valueOffset;
        private int _index;

        internal Enumerator(HashMap<TKey, TValue> map)
        {
            _entries = ref MemoryMarshal.GetArrayDataReference(map._entries);
            _managedValues = map._managedValues;
            _capacity = map._capacity;
            _stride = map._entryStride;
            _valueOffset = map._valueOffset;
            _index = -1;
        }

        public (TKey Key, TValue Value) Current { get; private set; }

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
                    var value = RuntimeHelpers.IsReferenceOrContainsReferences<TValue>()
                        ? _managedValues[index] : Unsafe.ReadUnaligned<TValue>(ref Unsafe.Add(ref entry, _valueOffset));
                    Current = (KeyOf(ref entry), value);
                    return true;
                }
            }
            _index = index;
            return false;
        }
    }

    public void Dispose()
    {
        if (_entries == null)
        {
            return;
        }

        _entries = null;
        _managedValues = null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Private — value access (JIT-specialized)
    // ═══════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TValue ReadValue(ref byte entry, int idx)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            return Unsafe.ReadUnaligned<TValue>(ref Unsafe.Add(ref entry, _valueOffset));
        }
        return _managedValues[idx];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteValue(ref byte entry, int idx, TValue value)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref entry, _valueOffset), value);
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

                if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                {
                    _managedValues[idx] = _managedValues[j];
                }

                idx = j;
            }

            j = (j + 1) & _mask;
        }

        HashOf(ref Unsafe.Add(ref entries, (nint)idx * stride)) = 0;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            _managedValues[idx] = default;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Private — resize
    // ═══════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Resize(int newCapacity)
    {
        int stride = _entryStride;
        int newMask = newCapacity - 1;
        var newEntries = new byte[newCapacity * stride];
        ref byte src = ref MemoryMarshal.GetArrayDataReference(_entries);
        ref byte dst = ref MemoryMarshal.GetArrayDataReference(newEntries);

        TValue[] newManagedValues = null;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            newManagedValues = new TValue[newCapacity];
        }

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

                if (newManagedValues != null)
                {
                    newManagedValues[idx] = _managedValues[i];
                }
            }
        }

        _entries = newEntries;
        _capacity = newCapacity;
        _mask = newMask;
        _resizeThreshold = (int)(newCapacity * MaxLoadFactor);

        if (newManagedValues != null)
        {
            _managedValues = newManagedValues;
        }
    }
}
