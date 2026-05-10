using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Thread-safe in-memory hash set using striped open addressing with per-stripe OLC.
/// Lock-free reads, exclusive writes per stripe. Each stripe is an independent open-addressing table with linear probing and backward-shift deletion
/// (same internals as <see cref="HashMap{TKey}"/>).
/// <para>
/// Concurrency protocol:
/// <list type="bullet">
///   <item><b>Reads</b>: Lock-free via OLC (Optimistic Lock Coupling). Zero writes to shared state.</item>
///   <item><b>Writes</b>: Per-stripe exclusive lock via CAS on OlcVersion bit 0.</item>
///   <item><b>Resize</b>: Per-stripe, under existing write lock. Other stripes remain fully accessible.</item>
/// </list>
/// </para>
/// Uses POH (Pinned Object Heap) allocation — same memory model as <see cref="HashMap{TKey}"/>.
/// </summary>
internal unsafe class ConcurrentHashMap<TKey> : IDisposable where TKey : unmanaged, IEquatable<TKey>
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
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Fields
    // ═══════════════════════════════════════════════════════════════════════

    private readonly int _entryStride;       // bytes per entry: (4 + sizeof(TKey)) aligned to 4
    private readonly int _stripeCount;
    private readonly int _stripeShift;       // 32 - log2(stripeCount), for stripe selection via hash >> shift
    private readonly Stripe[] _stripes;
    private bool _disposed;

    // ═══════════════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════════════

    public ConcurrentHashMap(int initialCapacity = 1024)
    {
        _entryStride = (4 + sizeof(TKey) + 3) & ~3;
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

    /// <summary>Add <paramref name="key"/> to the set if not already present. Thread-safe (acquires stripe lock).</summary>
    /// <returns><c>true</c> if the key was added; <c>false</c> if it already existed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(TKey key)
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

    /// <summary>Check whether <paramref name="key"/> exists. Lock-free via OLC — zero writes to shared state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(TKey key)
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

            int version = stripe.OlcVersion;
            if ((version & 1) != 0)
            {
                Thread.SpinWait(1);
                continue;
            }

            byte* entries = stripe.Entries;
            int mask = stripe.Mask;
            int idx = (int)(hash & (uint)mask);
            bool found = false;

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
                    found = true;
                    break;
                }

                idx = (idx + 1) & mask;
            }

            if (stripe.OlcVersion != version)
            {
                continue;
            }
            return found;
        }
    }

    /// <summary>Remove <paramref name="key"/> from the set. Thread-safe (acquires stripe lock). Uses backward-shift deletion.</summary>
    /// <returns><c>true</c> if the key was found and removed; <c>false</c> if not present.</returns>
    public bool TryRemove(TKey key)
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

    /// <summary>Grow all stripes so the set can hold at least <paramref name="minimumEntries"/> without per-stripe resizing.</summary>
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

    /// <summary>Best-effort value-type enumerator. Iterates stripes sequentially, no locking.</summary>
    public ref struct Enumerator
    {
        private readonly ConcurrentHashMap<TKey> _map;
        private int _stripeIdx;
        private int _entryIdx;

        internal Enumerator(ConcurrentHashMap<TKey> map)
        {
            _map = map;
            _stripeIdx = 0;
            _entryIdx = -1;
        }

        public TKey Current { get; private set; }

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
                        Current = *(TKey*)(entry + 4);
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
            _stripes[i].Count = 0;
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
    /// On x64, store-store ordering (TSO) guarantees all prior writes are visible before this version bump.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReleaseStripeLock(ref Stripe stripe) => stripe.OlcVersion += 1;

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
                idx = j;
            }

            j = (j + 1) & mask;
        }

        *(uint*)(stripe.Entries + (long)idx * stride) = 0;
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
            }
        }

        // Old POH array: GC collects it once lock-free readers finish probing.
        // OLC version bump in ReleaseStripeLock ensures readers retry with new entries pointer.
        stripe.PohArray = newPoh;
        stripe.Entries = newEntries;
        stripe.Capacity = newCapacity;
        stripe.Mask = newMask;
        stripe.ResizeThreshold = (int)(newCapacity * MaxLoadFactor);
    }
}
