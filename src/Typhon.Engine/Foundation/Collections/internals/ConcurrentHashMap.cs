using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Stripe = Typhon.Engine.Internals.ConcurrentStripe;

namespace Typhon.Engine.Internals;

/// <summary>One stripe of <see cref="ConcurrentHashMap{TKey}"/> and <see cref="ConcurrentHashMap{TKey,TValue}"/>, padded to 128 bytes.</summary>
/// <remarks>
/// Everything a lock-free reader touches (version, mask, table) is in the first 32 bytes, and the rest is padding. Writers CAS <see cref="OlcVersion"/> and
/// write <see cref="Count"/>, so unpadded stripes put a writer on one stripe and readers of its neighbours on the same cache line (the unpadded 24-byte
/// stripe cost contended readers and writers +22 %). At 128 bytes two stripes' hot fields are at least 96 bytes apart, so no cache line holds both,
/// whatever the array's alignment. Not generic, because a generic struct cannot have an explicit layout; the KV map keeps its values array in
/// <see cref="ManagedValues"/> as an <see cref="object"/>.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct ConcurrentStripe
{
    [FieldOffset(0)]
    public int OlcVersion;           // bit 0 = lock, bits 1-31 = version

    [FieldOffset(4)]
    public int Count;

    [FieldOffset(8)]
    public int Mask;                 // capacity - 1; published after Table on resize

    [FieldOffset(12)]
    public int ResizeThreshold;

    [FieldOffset(16)]
    public byte[] Table;             // entries; replaced whole on resize

    [FieldOffset(24)]
    public object ManagedValues;     // the KV map's TValue[] (managed TValue only); stored before Table on resize
}

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
/// </summary>
/// <remarks>
/// <para><b>Each stripe's table is a managed array</b> (entries <c>[uint hash][TKey key]</c>) reached through <c>ref</c>s. It replaced a pinned-object-heap
/// array reached through a <c>byte*</c>: a reader probing the old array while a resize dropped it held only a raw pointer, invisible to the GC, so a gen2
/// collection inside the probe could free the memory under it. A <c>ref</c> keeps the array alive.</para>
/// <para><b>Table and mask are published in order, and a table only ever grows.</b> A resize stores the new table, then the new mask, both with release
/// stores; a lock-free reader loads the mask, then the table, both with acquire loads. A reader that sees the new mask therefore sees the new table, and
/// one that still sees the old mask indexes a table at least that large, so no reader can index past the end of its table (the old version loaded the
/// two as unordered fields and could). The two loads stay independent, so the probe does not wait on a load chained through the table; a torn pair only
/// reaches a reader whose version check then fails.</para>
/// </remarks>
internal class ConcurrentHashMap<TKey> : IDisposable where TKey : unmanaged, IEquatable<TKey>
{
    private const double MaxLoadFactor = 0.75;

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
        _entryStride = (4 + Unsafe.SizeOf<TKey>() + 3) & ~3;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref uint HashOf(ref byte entry) => ref Unsafe.As<byte, uint>(ref entry);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TKey KeyOf(ref byte entry) => Unsafe.ReadUnaligned<TKey>(ref Unsafe.Add(ref entry, 4));

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
                    HashOf(ref entry) = hash;
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref entry, 4), key);
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

            // Acquire load: orders the following table reads after this version snapshot (paired with the release in ReleaseStripeLock).
            int version = Volatile.Read(ref stripe.OlcVersion);
            if ((version & 1) != 0)
            {
                Thread.SpinWait(1);
                continue;
            }

            // Mask, then table (class remarks): the mask never exceeds this table. The ref keeps the table alive if a resize replaces it mid-probe.
            int mask = Volatile.Read(ref stripe.Mask);
            var table = Volatile.Read(ref stripe.Table);
            ref byte entries = ref EntriesOf(table);
            int idx = (int)(hash & (uint)mask);
            bool found = false;

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

    /// <summary>Best-effort value-type enumerator. Iterates stripes sequentially, no locking; each stripe is walked on the table it had when reached.</summary>
    public ref struct Enumerator
    {
        private readonly ConcurrentHashMap<TKey> _map;
        private int _stripeIdx;
        private int _entryIdx;
        private int _capacity;
        private byte[] _table;

        internal Enumerator(ConcurrentHashMap<TKey> map)
        {
            _map = map;
            _stripeIdx = 0;
            _entryIdx = -1;
            _capacity = 0;
            _table = null;
        }

        public TKey Current { get; private set; }

        public bool MoveNext()
        {
            int stride = _map._entryStride;
            while (_stripeIdx < _map._stripeCount)
            {
                if (_table == null)
                {
                    _table = Volatile.Read(ref _map._stripes[_stripeIdx].Table);
                    _capacity = _table.Length / stride;   // from the table itself, so it can never disagree with it
                }

                ref byte entries = ref EntriesOf(_table);
                int capacity = _capacity;
                while (++_entryIdx < capacity)
                {
                    ref byte entry = ref Unsafe.Add(ref entries, (nint)_entryIdx * stride);
                    if (HashOf(ref entry) != 0)
                    {
                        Current = KeyOf(ref entry);
                        return true;
                    }
                }
                _stripeIdx++;
                _entryIdx = -1;
                _table = null;
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
                idx = j;
            }

            j = (j + 1) & mask;
        }

        HashOf(ref Unsafe.Add(ref entries, (nint)idx * stride)) = 0;
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
            }
        }

        stripe.ResizeThreshold = (int)(newCapacity * MaxLoadFactor);

        // Table, then mask, both release stores (class remarks). The first release makes the rehashed entries visible before the table that holds them; the
        // second makes the table visible before a mask that fits it. A reader still probing the old table keeps it alive through its ref, and fails its
        // version check — the stripe lock is held, so OlcVersion stays odd until ReleaseStripeLock.
        Volatile.Write(ref stripe.Table, newTable);
        Volatile.Write(ref stripe.Mask, newMask);
    }
}
