using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Thread-safe in-memory hash map using striped open addressing with per-stripe OLC.
/// Lock-free reads, exclusive writes per stripe. Each stripe is an independent open-addressing table with linear probing and backward-shift deletion.
/// <para>
/// JIT-specialized dual path via <see cref="RuntimeHelpers.IsReferenceOrContainsReferences{T}"/>:
/// <list type="bullet">
///   <item>Unmanaged TValue: values stored inline in entry array. Zero GC pressure.</item>
///   <item>Managed TValue: keys in entry array, values in parallel <c>TValue[]</c>.</item>
/// </list>
/// </para>
/// Uses POH (Pinned Object Heap) allocation — same memory model as <see cref="HashMap{TKey}"/>.
/// </summary>
internal unsafe class ConcurrentHashMap<TKey, TValue> : IDisposable where TKey : unmanaged, IEquatable<TKey>
{
    private const double MaxLoadFactor = 0.75;

    // ═══════════════════════════════════════════════════════════════════════
    // Stripe structure
    // ═══════════════════════════════════════════════════════════════════════

    private struct Stripe
    {
        public int OlcVersion;        // bit 0 = lock, bits 1-31 = version
        public int Count;
        public int Capacity;          // power of 2
        public int Mask;              // Capacity - 1
        public int ResizeThreshold;
        public byte* Entries;
        public byte[] PohArray;       // POH array reference — prevents GC collection
        public TValue[] ManagedValues;  // managed TValue path only (null for unmanaged)
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Fields
    // ═══════════════════════════════════════════════════════════════════════

    private readonly int _entryStride;
    private readonly int _valueOffset;       // byte offset of value within entry (unmanaged path only)
    private readonly int _stripeCount;
    private readonly int _stripeShift;       // 32 - log2(stripeCount), for stripe selection via hash >> shift
    private readonly Stripe[] _stripes;
    private bool _disposed;

    // ═══════════════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════════════

    public ConcurrentHashMap(int initialCapacity = 1024)
    {
        // Entry layout: [uint hash | TKey key | TValue value(unmanaged only)]
        _valueOffset = 4 + sizeof(TKey);
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            _entryStride = (4 + sizeof(TKey) + Unsafe.SizeOf<TValue>() + 3) & ~3;
        }
        else
        {
            _entryStride = (4 + sizeof(TKey) + 3) & ~3;
        }

        _stripeCount = Math.Max(64, (int)BitOperations.RoundUpToPowerOf2((uint)Environment.ProcessorCount * 4));
        _stripeShift = 32 - BitOperations.Log2((uint)_stripeCount);

        int perStripeCapacity = Math.Max(4, (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, initialCapacity / _stripeCount)));

        _stripes = new Stripe[_stripeCount];
        for (int i = 0; i < _stripeCount; i++)
        {
            ref var stripe = ref _stripes[i];
            stripe.Capacity = perStripeCapacity;
            stripe.Mask = perStripeCapacity - 1;
            stripe.ResizeThreshold = (int)(perStripeCapacity * MaxLoadFactor);
            int size = perStripeCapacity * _entryStride;
            stripe.PohArray = GC.AllocateArray<byte>(size, true);
            stripe.Entries = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(stripe.PohArray));

            if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
            {
                stripe.ManagedValues = new TValue[perStripeCapacity];
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Approximate count (sum of per-stripe counts, no locking).</summary>
    public int Count
    {
        get
        {
            int total = 0;
            for (int i = 0; i < _stripeCount; i++)
            {
                total += _stripes[i].Count;
            }
            return total;
        }
    }

    /// <summary>Number of independent stripes (for diagnostics).</summary>
    public int StripeCount => _stripeCount;

    // ═══════════════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Add a key-value pair if the key is not already present. Thread-safe (acquires stripe lock).</summary>
    /// <returns><c>true</c> if the pair was added; <c>false</c> if the key already existed (existing value unchanged).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(TKey key, TValue value)
    {
        uint hash = HashUtils.ComputeHash(key);
        if (hash == 0)
        {
            hash = 1;
        }

        int stripeIdx = (int)(hash >> _stripeShift);

        AcquireStripeLock(ref _stripes[stripeIdx]);
        try
        {
            ref var stripe = ref _stripes[stripeIdx];

            if (stripe.Count >= stripe.ResizeThreshold)
            {
                ResizeStripe(ref stripe, checked(stripe.Capacity * 2));
            }

            int idx = (int)(hash & (uint)stripe.Mask);
            int stride = _entryStride;

            while (true)
            {
                byte* entry = stripe.Entries + (long)idx * stride;
                uint h = *(uint*)entry;

                if (h == 0)
                {
                    *(uint*)entry = hash;
                    *(TKey*)(entry + 4) = key;
                    WriteValue(ref stripe, entry, idx, value);
                    stripe.Count++;
                    return true;
                }

                if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
                {
                    return false;
                }

                idx = (idx + 1) & stripe.Mask;
            }
        }
        finally
        {
            ReleaseStripeLock(ref _stripes[stripeIdx]);
        }
    }

    /// <summary>Look up the value for <paramref name="key"/>. Lock-free via OLC — zero writes to shared state.</summary>
    /// <returns><c>true</c> if found; <c>false</c> if the key is not present.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(TKey key, out TValue value)
    {
        uint hash = HashUtils.ComputeHash(key);
        if (hash == 0)
        {
            hash = 1;
        }

        int stripeIdx = (int)(hash >> _stripeShift);
        int stride = _entryStride;

        while (true)
        {
            ref var stripe = ref _stripes[stripeIdx];

            // Acquire load: orders the following entries/mask/value reads after this version snapshot (paired with the release in ReleaseStripeLock).
            int version = Volatile.Read(ref stripe.OlcVersion);
            if ((version & 1) != 0)
            {
                Thread.SpinWait(1);
                continue;
            }

            byte* entries = stripe.Entries;
            int mask = stripe.Mask;
            TValue[] managedValues = stripe.ManagedValues; // snapshot for managed path
            int idx = (int)(hash & (uint)mask);
            bool found = false;
            TValue result = default;

            while (true)
            {
                byte* entry = entries + (long)idx * stride;
                uint h = *(uint*)entry;

                if (h == 0)
                {
                    break;
                }

                if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
                {
                    result = ReadValue(managedValues, entry, idx);
                    found = true;
                    break;
                }

                idx = (idx + 1) & mask;
            }

            if (Volatile.Read(ref stripe.OlcVersion) != version)
            {
                continue;
            }
            value = result;
            return found;
        }
    }

    /// <summary>Remove the entry for <paramref name="key"/> and return its value. Thread-safe. Uses backward-shift deletion.</summary>
    /// <returns><c>true</c> if the key was found and removed; <c>false</c> if not present.</returns>
    public bool TryRemove(TKey key, out TValue value)
    {
        uint hash = HashUtils.ComputeHash(key);
        if (hash == 0)
        {
            hash = 1;
        }

        int stripeIdx = (int)(hash >> _stripeShift);

        AcquireStripeLock(ref _stripes[stripeIdx]);
        try
        {
            ref var stripe = ref _stripes[stripeIdx];
            int idx = (int)(hash & (uint)stripe.Mask);
            int stride = _entryStride;

            while (true)
            {
                byte* entry = stripe.Entries + (long)idx * stride;
                uint h = *(uint*)entry;

                if (h == 0)
                {
                    value = default;
                    return false;
                }

                if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
                {
                    value = ReadValue(stripe.ManagedValues, entry, idx);
                    stripe.Count--;
                    BackwardShiftDelete(ref stripe, idx);
                    return true;
                }

                idx = (idx + 1) & stripe.Mask;
            }
        }
        finally
        {
            ReleaseStripeLock(ref _stripes[stripeIdx]);
        }
    }

    /// <summary>Return the existing value for <paramref name="key"/>, or atomically add <paramref name="value"/> and return it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TValue GetOrAdd(TKey key, TValue value)
    {
        uint hash = HashUtils.ComputeHash(key);
        if (hash == 0)
        {
            hash = 1;
        }

        int stripeIdx = (int)(hash >> _stripeShift);

        AcquireStripeLock(ref _stripes[stripeIdx]);
        try
        {
            ref var stripe = ref _stripes[stripeIdx];

            if (stripe.Count >= stripe.ResizeThreshold)
            {
                ResizeStripe(ref stripe, checked(stripe.Capacity * 2));
            }

            int idx = (int)(hash & (uint)stripe.Mask);
            int stride = _entryStride;

            while (true)
            {
                byte* entry = stripe.Entries + (long)idx * stride;
                uint h = *(uint*)entry;

                if (h == 0)
                {
                    *(uint*)entry = hash;
                    *(TKey*)(entry + 4) = key;
                    WriteValue(ref stripe, entry, idx, value);
                    stripe.Count++;
                    return value;
                }

                if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
                {
                    return ReadValue(stripe.ManagedValues, entry, idx);
                }

                idx = (idx + 1) & stripe.Mask;
            }
        }
        finally
        {
            ReleaseStripeLock(ref _stripes[stripeIdx]);
        }
    }

    /// <summary>Update the value for an existing <paramref name="key"/>. Does not add if missing. Thread-safe.</summary>
    /// <returns><c>true</c> if the key was found and the value updated; <c>false</c> if the key was not present.</returns>
    public bool TryUpdate(TKey key, TValue newValue)
    {
        uint hash = HashUtils.ComputeHash(key);
        if (hash == 0)
        {
            hash = 1;
        }

        int stripeIdx = (int)(hash >> _stripeShift);

        AcquireStripeLock(ref _stripes[stripeIdx]);
        try
        {
            ref var stripe = ref _stripes[stripeIdx];
            int idx = (int)(hash & (uint)stripe.Mask);
            int stride = _entryStride;

            while (true)
            {
                byte* entry = stripe.Entries + (long)idx * stride;
                uint h = *(uint*)entry;

                if (h == 0)
                {
                    return false;
                }

                if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
                {
                    WriteValue(ref stripe, entry, idx, newValue);
                    return true;
                }

                idx = (idx + 1) & stripe.Mask;
            }
        }
        finally
        {
            ReleaseStripeLock(ref _stripes[stripeIdx]);
        }
    }

    /// <summary>
    /// Atomically update the value for <paramref name="key"/> only if the current value equals <paramref name="comparisonValue"/>.
    /// Compare-and-swap semantics — the check and write happen under the stripe lock.
    /// </summary>
    public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue)
    {
        uint hash = HashUtils.ComputeHash(key);
        if (hash == 0)
        {
            hash = 1;
        }

        int stripeIdx = (int)(hash >> _stripeShift);

        AcquireStripeLock(ref _stripes[stripeIdx]);
        try
        {
            ref var stripe = ref _stripes[stripeIdx];
            int idx = (int)(hash & (uint)stripe.Mask);
            int stride = _entryStride;

            while (true)
            {
                byte* entry = stripe.Entries + (long)idx * stride;
                uint h = *(uint*)entry;

                if (h == 0)
                {
                    return false;
                }

                if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
                {
                    if (!EqualityComparer<TValue>.Default.Equals(ReadValue(stripe.ManagedValues, entry, idx), comparisonValue))
                    {
                        return false;
                    }
                    WriteValue(ref stripe, entry, idx, newValue);
                    return true;
                }

                idx = (idx + 1) & stripe.Mask;
            }
        }
        finally
        {
            ReleaseStripeLock(ref _stripes[stripeIdx]);
        }
    }

    /// <summary>Get or set the value for <paramref name="key"/>. Getter is lock-free (OLC); throws <see cref="KeyNotFoundException"/> if missing. Setter acquires stripe lock; adds or overwrites.</summary>
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
            uint hash = HashUtils.ComputeHash(key);
            if (hash == 0)
            {
                hash = 1;
            }

            int stripeIdx = (int)(hash >> _stripeShift);

            AcquireStripeLock(ref _stripes[stripeIdx]);
            try
            {
                ref var stripe = ref _stripes[stripeIdx];

                if (stripe.Count >= stripe.ResizeThreshold)
                {
                    ResizeStripe(ref stripe, checked(stripe.Capacity * 2));
                }

                int idx = (int)(hash & (uint)stripe.Mask);
                int stride = _entryStride;

                while (true)
                {
                    byte* entry = stripe.Entries + (long)idx * stride;
                    uint h = *(uint*)entry;

                    if (h == 0)
                    {
                        *(uint*)entry = hash;
                        *(TKey*)(entry + 4) = key;
                        WriteValue(ref stripe, entry, idx, value);
                        stripe.Count++;
                        return;
                    }

                    if (h == hash && (*(TKey*)(entry + 4)).Equals(key))
                    {
                        WriteValue(ref stripe, entry, idx, value);
                        return;
                    }

                    idx = (idx + 1) & stripe.Mask;
                }
            }
            finally
            {
                ReleaseStripeLock(ref _stripes[stripeIdx]);
            }
        }
    }

    /// <summary>Clear all entries. Acquires all stripe locks in order.</summary>
    public void Clear()
    {
        for (int i = 0; i < _stripeCount; i++)
        {
            AcquireStripeLock(ref _stripes[i]);
        }

        try
        {
            for (int i = 0; i < _stripeCount; i++)
            {
                ref var stripe = ref _stripes[i];
                new Span<byte>(stripe.Entries, stripe.Capacity * _entryStride).Clear();
                if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                {
                    Array.Clear(stripe.ManagedValues);
                }
                stripe.Count = 0;
            }
        }
        finally
        {
            for (int i = _stripeCount - 1; i >= 0; i--)
            {
                ReleaseStripeLock(ref _stripes[i]);
            }
        }
    }

    /// <summary>Grow all stripes so the map can hold at least <paramref name="minimumEntries"/> without per-stripe resizing.</summary>
    public void EnsureCapacity(int minimumEntries)
    {
        int perStripe = Math.Max(4, (int)(minimumEntries / (double)_stripeCount / MaxLoadFactor) + 1);
        perStripe = (int)BitOperations.RoundUpToPowerOf2((uint)perStripe);

        for (int i = 0; i < _stripeCount; i++)
        {
            ref var stripe = ref _stripes[i];
            if (perStripe <= stripe.Capacity)
            {
                continue;
            }

            AcquireStripeLock(ref stripe);
            try
            {
                if (perStripe > stripe.Capacity)
                {
                    ResizeStripe(ref stripe, perStripe);
                }
            }
            finally
            {
                ReleaseStripeLock(ref stripe);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Enumerator — best-effort, no locking
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Returns a best-effort <see langword="ref struct"/> enumerator. No locks held — may observe partial state under concurrent writes.</summary>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>Best-effort value-type enumerator yielding <c>(TKey Key, TValue Value)</c> tuples. Iterates stripes sequentially, no locking.</summary>
    public ref struct Enumerator
    {
        private readonly ConcurrentHashMap<TKey, TValue> _map;
        private int _stripeIdx;
        private int _entryIdx;

        internal Enumerator(ConcurrentHashMap<TKey, TValue> map)
        {
            _map = map;
            _stripeIdx = 0;
            _entryIdx = -1;
        }

        public (TKey Key, TValue Value) Current { get; private set; }

        public bool MoveNext()
        {
            int stride = _map._entryStride;
            while (_stripeIdx < _map._stripeCount)
            {
                ref var stripe = ref _map._stripes[_stripeIdx];
                while (++_entryIdx < stripe.Capacity)
                {
                    byte* entry = stripe.Entries + (long)_entryIdx * stride;
                    if (*(uint*)entry != 0)
                    {
                        TKey key = *(TKey*)(entry + 4);
                        TValue value = _map.ReadValue(stripe.ManagedValues, entry, _entryIdx);
                        Current = (key, value);
                        return true;
                    }
                }
                _stripeIdx++;
                _entryIdx = -1;
            }
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dispose
    // ═══════════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        for (int i = 0; i < _stripeCount; i++)
        {
            _stripes[i].PohArray = null;
            _stripes[i].Entries = null;
            _stripes[i].ManagedValues = null;
            _stripes[i].Count = 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Private — value access (JIT-specialized)
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TValue ReadValue(TValue[] managedValues, byte* entry, int idx)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            return Unsafe.Read<TValue>(entry + _valueOffset);
        }
        return managedValues[idx];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteValue(ref Stripe stripe, byte* entry, int idx, TValue value)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            Unsafe.Write(entry + _valueOffset, value);
        }
        else
        {
            stripe.ManagedValues[idx] = value;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Private — OLC stripe locking
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AcquireStripeLock(ref Stripe stripe)
    {
        int v = stripe.OlcVersion;
        if ((v & 1) == 0 && Interlocked.CompareExchange(ref stripe.OlcVersion, v | 1, v) == v)
        {
            return;
        }
        AcquireStripeLockSlow(ref stripe);
    }

    /// <summary>
    /// Two-phase spin policy matching BTree's <c>SpinWriteLock</c>:
    /// Phase 1: 64 tight PAUSE spins (~100 ns on Zen) — covers typical lock hold time.
    /// Phase 2: SpinWait with Sleep(1) disabled — avoids 15 ms Windows timer-tick penalty.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AcquireStripeLockSlow(ref Stripe stripe)
    {
        for (int i = 0; i < 64; i++)
        {
            Thread.SpinWait(1);
            int v = stripe.OlcVersion;
            if ((v & 1) == 0 && Interlocked.CompareExchange(ref stripe.OlcVersion, v | 1, v) == v)
            {
                return;
            }
        }

        SpinWait spin = default;
        while (true)
        {
            spin.SpinOnce(-1);
            int v = stripe.OlcVersion;
            if ((v & 1) == 0 && Interlocked.CompareExchange(ref stripe.OlcVersion, v | 1, v) == v)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Release stripe lock and increment version.
    /// OlcVersion is odd (locked): adding 1 makes it even (unlocked) and increments the version in bits 1-31.
    /// Release store: publishes every write made under the lock before the version bump becomes visible.
    /// Free on x64 (TSO stores are already release-ordered — compiles to a plain mov); emits stlr on arm64, where store-store ordering is not guaranteed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReleaseStripeLock(ref Stripe stripe) => Volatile.Write(ref stripe.OlcVersion, stripe.OlcVersion + 1);

    // ═══════════════════════════════════════════════════════════════════════
    // Private — backward shift deletion (per-stripe)
    // ═══════════════════════════════════════════════════════════════════════

    private void BackwardShiftDelete(ref Stripe stripe, int idx)
    {
        int stride = _entryStride;
        int mask = stripe.Mask;
        int j = (idx + 1) & mask;

        while (true)
        {
            byte* entryJ = stripe.Entries + (long)j * stride;
            uint hj = *(uint*)entryJ;

            if (hj == 0)
            {
                break;
            }

            int homeJ = (int)(hj & (uint)mask);
            int distI = (idx - homeJ + stripe.Capacity) & mask;
            int distJ = (j - homeJ + stripe.Capacity) & mask;

            if (distI < distJ)
            {
                Unsafe.CopyBlock(stripe.Entries + (long)idx * stride, entryJ, (uint)stride);

                if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                {
                    stripe.ManagedValues[idx] = stripe.ManagedValues[j];
                }

                idx = j;
            }

            j = (j + 1) & mask;
        }

        // Clear the gap
        *(uint*)(stripe.Entries + (long)idx * stride) = 0;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            stripe.ManagedValues[idx] = default;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Private — per-stripe resize (called under stripe lock)
    // ═══════════════════════════════════════════════════════════════════════

    private void ResizeStripe(ref Stripe stripe, int newCapacity)
    {
        int stride = _entryStride;
        int newSize = newCapacity * stride;
        var newPoh = GC.AllocateArray<byte>(newSize, true);
        byte* newEntries = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(newPoh));
        int newMask = newCapacity - 1;

        TValue[] newManagedValues = null;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            newManagedValues = new TValue[newCapacity];
        }

        for (int i = 0; i < stripe.Capacity; i++)
        {
            byte* entry = stripe.Entries + (long)i * stride;
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
                    newManagedValues[idx] = stripe.ManagedValues[i];
                }
            }
        }

        // Old POH array: GC collects it once lock-free readers finish probing.
        // OLC version bump in ReleaseStripeLock ensures readers retry with new entries pointer.
        stripe.PohArray = newPoh;
        stripe.Entries = newEntries;
        stripe.Capacity = newCapacity;
        stripe.Mask = newMask;
        stripe.ResizeThreshold = (int)(newCapacity * MaxLoadFactor);

        if (newManagedValues != null)
        {
            stripe.ManagedValues = newManagedValues;
        }
    }
}
