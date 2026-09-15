using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Stripe = Typhon.Engine.Internals.ConcurrentStripe;

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
/// </summary>
/// <remarks>
/// <para>Same tables and publication as <see cref="ConcurrentHashMap{TKey}"/> (entries <c>[uint hash][TKey key][TValue value]</c>): a managed array per stripe
/// reached through <c>ref</c>s, the table then the mask published with release stores, loaded mask-then-table by lock-free readers — see there.</para>
/// <para><b>Managed values.</b> A resize stores the new <c>ManagedValues</c> array before it publishes the new table, and a reader loads the table before
/// the values. A reader that sees the new table therefore sees the new values; one that sees the old table sees values at least as large, so its index
/// stays in range, and its version check rejects whatever it read.</para>
/// </remarks>
internal class ConcurrentHashMap<TKey, TValue> : IDisposable where TKey : unmanaged, IEquatable<TKey>
{
    private const double MaxLoadFactor = 0.75;

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
        _valueOffset = 4 + Unsafe.SizeOf<TKey>();
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            _entryStride = (4 + Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>() + 3) & ~3;
        }
        else
        {
            _entryStride = (4 + Unsafe.SizeOf<TKey>() + 3) & ~3;
        }

        _stripeCount = Math.Max(64, (int)BitOperations.RoundUpToPowerOf2((uint)Environment.ProcessorCount * 4));
        _stripeShift = 32 - BitOperations.Log2((uint)_stripeCount);

        int perStripeCapacity = Math.Max(4, (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, initialCapacity / _stripeCount)));

        _stripes = new Stripe[_stripeCount];
        for (int i = 0; i < _stripeCount; i++)
        {
            ref var stripe = ref _stripes[i];
            stripe.Mask = perStripeCapacity - 1;
            stripe.ResizeThreshold = (int)(perStripeCapacity * MaxLoadFactor);
            stripe.Table = new byte[perStripeCapacity * _entryStride];

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
    // Entry access
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte EntriesOf(byte[] table) => ref MemoryMarshal.GetArrayDataReference(table);

    /// <summary>The stripe's values array (managed TValue only). The shared stripe keeps it as an object, as it cannot be generic.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TValue[] ValuesOf(ref Stripe stripe) => Unsafe.As<TValue[]>(stripe.ManagedValues);

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
                ResizeStripe(ref stripe, checked((stripe.Mask + 1) * 2));
            }

            int mask = stripe.Mask;
            ref byte entries = ref EntriesOf(stripe.Table);
            int idx = (int)(hash & (uint)mask);
            int stride = _entryStride;

            while (true)
            {
                ref byte entry = ref Unsafe.Add(ref entries, (nint)idx * stride);
                uint h = HashOf(ref entry);

                if (h == 0)
                {
                    SetKey(ref entry, hash, key);
                    WriteValue(ref stripe, ref entry, idx, value);
                    stripe.Count++;
                    return true;
                }

                if (h == hash && KeyOf(ref entry).Equals(key))
                {
                    return false;
                }

                idx = (idx + 1) & mask;
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

            // Acquire load: orders the following table/value reads after this version snapshot (paired with the release in ReleaseStripeLock).
            int version = Volatile.Read(ref stripe.OlcVersion);
            if ((version & 1) != 0)
            {
                Thread.SpinWait(1);
                continue;
            }

            // Mask, table, then values — see the class remarks. The refs keep this table and its values alive if a resize replaces them mid-probe.
            int mask = Volatile.Read(ref stripe.Mask);
            var table = Volatile.Read(ref stripe.Table);
            TValue[] managedValues = RuntimeHelpers.IsReferenceOrContainsReferences<TValue>()
                ? Unsafe.As<TValue[]>(Volatile.Read(ref stripe.ManagedValues))
                : null;
            ref byte entries = ref EntriesOf(table);
            int idx = (int)(hash & (uint)mask);
            bool found = false;
            TValue result = default;

            while (true)
            {
                ref byte entry = ref Unsafe.Add(ref entries, (nint)idx * stride);
                uint h = HashOf(ref entry);

                if (h == 0)
                {
                    break;
                }

                if (h == hash && KeyOf(ref entry).Equals(key))
                {
                    result = ReadValue(managedValues, ref entry, idx);
                    found = true;
                    break;
                }

                idx = (idx + 1) & mask;
            }

            // The probe's loads are plain; on arm64 an acquire load alone would let them sink below the validating re-read (CLAUDE.md,
            // memory-ordering discipline). JIT-folded away on x64.
            if (!X86Base.IsSupported)
            {
                Interlocked.MemoryBarrier();
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
            int mask = stripe.Mask;
            ref byte entries = ref EntriesOf(stripe.Table);
            int idx = (int)(hash & (uint)mask);
            int stride = _entryStride;

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
                    value = ReadValue(ValuesOf(ref stripe), ref entry, idx);
                    stripe.Count--;
                    BackwardShiftDelete(ref stripe, idx);
                    return true;
                }

                idx = (idx + 1) & mask;
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
                ResizeStripe(ref stripe, checked((stripe.Mask + 1) * 2));
            }

            int mask = stripe.Mask;
            ref byte entries = ref EntriesOf(stripe.Table);
            int idx = (int)(hash & (uint)mask);
            int stride = _entryStride;

            while (true)
            {
                ref byte entry = ref Unsafe.Add(ref entries, (nint)idx * stride);
                uint h = HashOf(ref entry);

                if (h == 0)
                {
                    SetKey(ref entry, hash, key);
                    WriteValue(ref stripe, ref entry, idx, value);
                    stripe.Count++;
                    return value;
                }

                if (h == hash && KeyOf(ref entry).Equals(key))
                {
                    return ReadValue(ValuesOf(ref stripe), ref entry, idx);
                }

                idx = (idx + 1) & mask;
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
            int mask = stripe.Mask;
            ref byte entries = ref EntriesOf(stripe.Table);
            int idx = (int)(hash & (uint)mask);
            int stride = _entryStride;

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
                    WriteValue(ref stripe, ref entry, idx, newValue);
                    return true;
                }

                idx = (idx + 1) & mask;
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
            int mask = stripe.Mask;
            ref byte entries = ref EntriesOf(stripe.Table);
            int idx = (int)(hash & (uint)mask);
            int stride = _entryStride;

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
                    if (!EqualityComparer<TValue>.Default.Equals(ReadValue(ValuesOf(ref stripe), ref entry, idx), comparisonValue))
                    {
                        return false;
                    }
                    WriteValue(ref stripe, ref entry, idx, newValue);
                    return true;
                }

                idx = (idx + 1) & mask;
            }
        }
        finally
        {
            ReleaseStripeLock(ref _stripes[stripeIdx]);
        }
    }

    /// <summary>
    /// Get or set the value for <paramref name="key"/>. Getter is lock-free (OLC); throws <see cref="KeyNotFoundException"/> if missing. Setter acquires
    /// stripe lock; adds or overwrites.
    /// </summary>
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
                    ResizeStripe(ref stripe, checked((stripe.Mask + 1) * 2));
                }

                int mask = stripe.Mask;
                ref byte entries = ref EntriesOf(stripe.Table);
                int idx = (int)(hash & (uint)mask);
                int stride = _entryStride;

                while (true)
                {
                    ref byte entry = ref Unsafe.Add(ref entries, (nint)idx * stride);
                    uint h = HashOf(ref entry);

                    if (h == 0)
                    {
                        SetKey(ref entry, hash, key);
                        WriteValue(ref stripe, ref entry, idx, value);
                        stripe.Count++;
                        return;
                    }

                    if (h == hash && KeyOf(ref entry).Equals(key))
                    {
                        WriteValue(ref stripe, ref entry, idx, value);
                        return;
                    }

                    idx = (idx + 1) & mask;
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
                stripe.Table.AsSpan().Clear();
                if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                {
                    Array.Clear(ValuesOf(ref stripe));
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
            if (perStripe <= stripe.Mask + 1)
            {
                continue;
            }

            AcquireStripeLock(ref stripe);
            try
            {
                if (perStripe > stripe.Mask + 1)
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
        private int _capacity;
        private byte[] _table;
        private TValue[] _managedValues;

        internal Enumerator(ConcurrentHashMap<TKey, TValue> map)
        {
            _map = map;
            _stripeIdx = 0;
            _entryIdx = -1;
            _capacity = 0;
            _table = null;
            _managedValues = null;
        }

        public (TKey Key, TValue Value) Current { get; private set; }

        public bool MoveNext()
        {
            int stride = _map._entryStride;
            while (_stripeIdx < _map._stripeCount)
            {
                if (_table == null)
                {
                    ref var stripe = ref _map._stripes[_stripeIdx];
                    if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                    {
                        _table = Volatile.Read(ref stripe.Table);
                    }
                    else
                    {
                        // The table and its values must be one generation's. A resize stores the new values before it publishes the new table, so two
                        // plain loads can take the old table with the new values and pair a key with another key's value. Read both between two equal,
                        // even versions: a resize holds the stripe lock, so it moves the version. Every load is volatile, which keeps them in order on
                        // arm64 too.
                        while (true)
                        {
                            int version = Volatile.Read(ref stripe.OlcVersion);
                            if ((version & 1) == 0)
                            {
                                _table = Volatile.Read(ref stripe.Table);
                                _managedValues = Unsafe.As<TValue[]>(Volatile.Read(ref stripe.ManagedValues));
                                if (Volatile.Read(ref stripe.OlcVersion) == version)
                                {
                                    break;
                                }
                            }
                            Thread.SpinWait(1);
                        }
                    }

                    _capacity = _table.Length / stride;   // from the table itself, so it can never disagree with it
                }

                ref byte entries = ref EntriesOf(_table);
                int capacity = _capacity;
                while (++_entryIdx < capacity)
                {
                    ref byte entry = ref Unsafe.Add(ref entries, (nint)_entryIdx * stride);
                    if (HashOf(ref entry) != 0)
                    {
                        Current = (KeyOf(ref entry), _map.ReadValue(_managedValues, ref entry, _entryIdx));
                        return true;
                    }
                }
                _stripeIdx++;
                _entryIdx = -1;
                _table = null;
                _managedValues = null;
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
            _stripes[i].Table = null;
            _stripes[i].ManagedValues = null;
            _stripes[i].Count = 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Private — value access (JIT-specialized)
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TValue ReadValue(TValue[] managedValues, ref byte entry, int idx)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            return Unsafe.ReadUnaligned<TValue>(ref Unsafe.Add(ref entry, _valueOffset));
        }
        return managedValues[idx];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteValue(ref Stripe stripe, ref byte entry, int idx, TValue value)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref entry, _valueOffset), value);
        }
        else
        {
            ValuesOf(ref stripe)[idx] = value;
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
    // Private — backward shift deletion (per-stripe, under the stripe lock)
    // ═══════════════════════════════════════════════════════════════════════

    private void BackwardShiftDelete(ref Stripe stripe, int idx)
    {
        int stride = _entryStride;
        int mask = stripe.Mask;
        int capacity = mask + 1;
        ref byte entries = ref EntriesOf(stripe.Table);
        int j = (idx + 1) & mask;

        while (true)
        {
            ref byte entryJ = ref Unsafe.Add(ref entries, (nint)j * stride);
            uint hj = HashOf(ref entryJ);

            if (hj == 0)
            {
                break;
            }

            int homeJ = (int)(hj & (uint)mask);
            int distI = (idx - homeJ + capacity) & mask;
            int distJ = (j - homeJ + capacity) & mask;

            if (distI < distJ)
            {
                Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref entries, (nint)idx * stride), ref entryJ, (uint)stride);

                if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                {
                    ValuesOf(ref stripe)[idx] = ValuesOf(ref stripe)[j];
                }

                idx = j;
            }

            j = (j + 1) & mask;
        }

        // Clear the gap
        HashOf(ref Unsafe.Add(ref entries, (nint)idx * stride)) = 0;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            ValuesOf(ref stripe)[idx] = default;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Private — per-stripe resize (called under stripe lock)
    // ═══════════════════════════════════════════════════════════════════════

    private void ResizeStripe(ref Stripe stripe, int newCapacity)
    {
        int stride = _entryStride;
        int oldCapacity = stripe.Mask + 1;
        var newTable = new byte[newCapacity * stride];
        int newMask = newCapacity - 1;
        ref byte src = ref EntriesOf(stripe.Table);
        ref byte dst = ref EntriesOf(newTable);

        TValue[] newManagedValues = null;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            newManagedValues = new TValue[newCapacity];
        }

        for (int i = 0; i < oldCapacity; i++)
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
                    newManagedValues[idx] = ValuesOf(ref stripe)[i];
                }
            }
        }

        stripe.ResizeThreshold = (int)(newCapacity * MaxLoadFactor);

        // Values, then table, then mask (class remarks). The table's release store makes the values and the rehashed entries visible before the table; the
        // mask's makes the table visible before a mask that fits it. A reader still probing the old table keeps it alive through its ref, and fails its
        // version check — the stripe lock is held, so OlcVersion stays odd until ReleaseStripeLock.
        if (newManagedValues != null)
        {
            stripe.ManagedValues = newManagedValues;
        }

        Volatile.Write(ref stripe.Table, newTable);
        Volatile.Write(ref stripe.Mask, newMask);
    }
}
