using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Page-backed hash map with unmanaged key type and runtime-configured value size.
/// Identical to <see cref="PagedHashMap{TKey,TValue,TStore}"/> except values are accessed as raw <c>byte*</c>
/// pointers with <see cref="Unsafe.CopyBlock"/> instead of typed <c>ref TValue</c>.
/// <para>
/// Designed for entity records whose size varies per archetype (14 + componentCount × 4 bytes).
/// Bucket layout: [Header 12B] [Key₀..Key_{cap-1}] [Val₀..Val_{cap-1}].
/// </para>
/// </summary>
/// <summary>
/// Callback shape consumed by <see cref="RawValuePagedHashMap{TKey,TStore}.TryUpdateInPlace"/>. Implementations
/// receive a pointer to the existing value bytes inside the bucket and mutate them in place. The pointer is
/// only valid while the call is on the stack — must not be stored or returned.
/// <para>
/// Implementations should be small <c>ref struct</c> or <c>struct</c> types with the actual update parameters
/// stored as fields, so the JIT can devirtualise the <see cref="Update"/> call and inline the body.
/// </para>
/// </summary>
internal unsafe interface IRawValueUpdater
{
    void Update(byte* valueBytes);
}

unsafe class RawValuePagedHashMap<TKey, TStore> : PagedHashMapBase<TStore> where TKey : unmanaged, IEquatable<TKey> where TStore : struct, IPageStore
{
    // ═══════════════════════════════════════════════════════════════════════
    // Layout fields (computed once at construction)
    // ═══════════════════════════════════════════════════════════════════════

    private readonly int _valueSize;
    private readonly int _bucketCapacity;
    private readonly int _keysOffset;
    private readonly int _valuesOffset;

    // ═══════════════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════════════

    private RawValuePagedHashMap(ChunkBasedSegment<TStore> segment, int n0, int valueSize) : base(segment, n0)
    {
        Debug.Assert(valueSize > 0, "Value size must be positive");
        _valueSize = valueSize;
        _bucketCapacity = (segment.Stride - sizeof(PagedHashMapBucketHeader)) / (sizeof(TKey) + valueSize);
        Debug.Assert(_bucketCapacity >= 1, $"Stride {segment.Stride} too small for entry size {sizeof(TKey) + valueSize}");
        _keysOffset = sizeof(PagedHashMapBucketHeader);
        _valuesOffset = sizeof(PagedHashMapBucketHeader) + _bucketCapacity * sizeof(TKey);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    public override int BucketCapacity => _bucketCapacity;

    /// <summary>Configured value size in bytes.</summary>
    public int ValueSize => _valueSize;

    // ═══════════════════════════════════════════════════════════════════════
    // Static helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the smallest supported stride (256 or 512) that yields at least <paramref name="minCapacity"/> entries per bucket.
    /// </summary>
    public static int RecommendedStride(int valueSize, int minCapacity = 4)
    {
        int entrySize = sizeof(TKey) + valueSize;
        if ((256 - sizeof(PagedHashMapBucketHeader)) / entrySize >= minCapacity)
        {
            return 256;
        }
        if ((512 - sizeof(PagedHashMapBucketHeader)) / entrySize >= minCapacity)
        {
            return 512;
        }
        throw new ArgumentException($"Entry size {entrySize}B too large for supported strides (need {minCapacity} entries)");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Pointer access helpers
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref PagedHashMapBucketHeader GetHeader(byte* chunkAddr) => ref Unsafe.AsRef<PagedHashMapBucketHeader>(chunkAddr);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TKey* KeysPtr(byte* chunkAddr) => (TKey*)(chunkAddr + _keysOffset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* ValueAt(byte* chunkAddr, int index)
    {
        Debug.Assert(index >= 0 && index < _bucketCapacity, $"ValueAt index {index} out of range [0, {_bucketCapacity})");
        return chunkAddr + _valuesOffset + index * _valueSize;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Hash function — JIT-specialized by sizeof(TKey)
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ComputeHash(TKey key)
    {
        if (sizeof(TKey) == 4)
        {
            return WangJenkins32(Unsafe.As<TKey, uint>(ref key));
        }
        if (sizeof(TKey) == 8)
        {
            return XxHash32_8Bytes(Unsafe.As<TKey, long>(ref key));
        }
        return XxHash32_Bytes((byte*)Unsafe.AsPointer(ref key), sizeof(TKey));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static uint WangJenkins32(uint h)
    {
        h = (h ^ 61) ^ (h >> 16);
        h *= 0x85EBCA6B;
        h ^= h >> 13;
        h *= 0xC2B2AE35;
        h ^= h >> 16;
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static uint XxHash32_8Bytes(long key)
    {
        // ReSharper disable InconsistentNaming
        const uint Prime3 = 3266489917u;
        const uint Prime4 = 668265263u;
        const uint Prime5 = 374761393u;
        const uint Prime2 = 2246822519u;
        // ReSharper restore InconsistentNaming

        uint lo = (uint)key;
        uint hi = (uint)(key >> 32);

        uint h = Prime5 + 8u;
        h += lo * Prime3;
        h = ((h << 17) | (h >> 15)) * Prime4;
        h += hi * Prime3;
        h = ((h << 17) | (h >> 15)) * Prime4;

        h ^= h >> 15;
        h *= Prime2;
        h ^= h >> 13;
        h *= Prime3;
        h ^= h >> 16;
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static uint XxHash32_Bytes(byte* input, int len)
    {
        // ReSharper disable InconsistentNaming
        const uint Prime1 = 2654435761u;
        const uint Prime2 = 2246822519u;
        const uint Prime3 = 3266489917u;
        const uint Prime4 = 668265263u;
        const uint Prime5 = 374761393u;
        // ReSharper restore InconsistentNaming

        uint h = Prime5 + (uint)len;
        byte* p = input;
        byte* end = input + len;

        while (p + 4 <= end)
        {
            h += *(uint*)p * Prime3;
            h = ((h << 17) | (h >> 15)) * Prime4;
            p += 4;
        }
        while (p < end)
        {
            h += *p * Prime5;
            h = ((h << 11) | (h >> 21)) * Prime1;
            p++;
        }

        h ^= h >> 15;
        h *= Prime2;
        h ^= h >> 13;
        h *= Prime3;
        h ^= h >> 16;
        return h;
    }

    internal static uint ComputeHashForTest(TKey key) => ComputeHash(key);

    // ═══════════════════════════════════════════════════════════════════════
    // Bucket initialization
    // ═══════════════════════════════════════════════════════════════════════

    protected override void InitializeBucket(int chunkId, ref ChunkAccessor<TStore> accessor)
    {
        byte* addr = accessor.GetChunkAddress(chunkId, true);
        ref var header = ref GetHeader(addr);
        header.OlcVersion = 4;          // version=1, locked=false, obsolete=false
        header.EntryCount = 0;
        header.Flags = 0;
        header.Reserved = 0;
        header.OverflowChunkId = -1;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Read path
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scan a bucket chain for a key. Returns index + chunk if found, -1 if not.
    /// When found, <paramref name="foundChunkId"/> and <paramref name="foundIndex"/> indicate the location.
    /// </summary>
    private bool ScanChain(int startChunkId, TKey key, ref ChunkAccessor<TStore> accessor, out int foundChunkId, out int foundIndex)
    {
        int chunkId = startChunkId;

        while (chunkId != -1)
        {
            byte* addr = accessor.GetChunkAddress(chunkId);
            ref readonly var header = ref GetHeader(addr);
            TKey* keys = KeysPtr(addr);
            int count = header.EntryCount;

            for (int i = 0; i < count; i++)
            {
                if (keys[i].Equals(key))
                {
                    foundChunkId = chunkId;
                    foundIndex = i;
                    return true;
                }
            }

            chunkId = header.OverflowChunkId;
        }

        foundChunkId = -1;
        foundIndex = -1;
        return false;
    }

    /// <summary>
    /// Look up a key using the OLC read protocol. Copies value bytes to <paramref name="valueOut"/>.
    /// </summary>
    public bool TryGet(TKey key, byte* valueOut, ref ChunkAccessor<TStore> accessor)
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = PackedMeta;
            var (level, next, _) = UnpackMeta(packed);
            int bucket = ResolveBucket(hash, level, next, N0);
            int chunkId = GetBucketChunkId(bucket, ref accessor);

            byte* addr = accessor.GetChunkAddress(chunkId);
            ref var header = ref GetHeader(addr);

            var latch = new OlcLatch(ref header.OlcVersion);
            int version = latch.ReadVersion();
            if (version == 0)
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            bool found = ScanChain(chunkId, key, ref accessor, out int fChunkId, out int fIndex);
            if (found)
            {
                byte* fAddr = accessor.GetChunkAddress(fChunkId);
                Unsafe.CopyBlock(valueOut, ValueAt(fAddr, fIndex), (uint)_valueSize);
            }

            if (!latch.ValidateVersion(version))
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            if (!found && PackedMeta != packed)
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            return found;
        }
    }

    /// <summary>
    /// Look up a key and return a pointer to the value in-place. The pointer is valid only while
    /// the chunk stays pinned (before OLC validation). Use <see cref="TryGet"/> for safe access.
    /// Returns null if the key is not found.
    /// </summary>
    public byte* TryGetPtr(TKey key, ref ChunkAccessor<TStore> accessor)
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = PackedMeta;
            var (level, next, _) = UnpackMeta(packed);
            int bucket = ResolveBucket(hash, level, next, N0);
            int chunkId = GetBucketChunkId(bucket, ref accessor);

            byte* addr = accessor.GetChunkAddress(chunkId);
            ref var header = ref GetHeader(addr);

            var latch = new OlcLatch(ref header.OlcVersion);
            int version = latch.ReadVersion();
            if (version == 0)
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            bool found = ScanChain(chunkId, key, ref accessor, out int fChunkId, out int fIndex);
            byte* result = found ? ValueAt(accessor.GetChunkAddress(fChunkId), fIndex) : null;

            if (!latch.ValidateVersion(version))
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            if (!found && PackedMeta != packed)
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            return result;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Write helpers (private)
    // ═══════════════════════════════════════════════════════════════════════

    private void AppendEntry(int startChunkId, TKey key, byte* value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        int chunkId = startChunkId;

        while (true)
        {
            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);

            if (header.EntryCount < _bucketCapacity)
            {
                int idx = header.EntryCount;
                KeysPtr(addr)[idx] = key;
                Unsafe.CopyBlock(ValueAt(addr, idx), value, (uint)_valueSize);
                header.EntryCount = (byte)(idx + 1);
                return;
            }

            if (header.OverflowChunkId != -1)
            {
                chunkId = header.OverflowChunkId;
                continue;
            }

            // Allocate overflow — re-fetch current chunk after (AllocateChunk may remap segment).
            //
            // ORDER MATTERS (#301 race-fix): fully initialise the new chunk's header — including writing OverflowChunkId = -1 (end-of-chain sentinel) — BEFORE
            // linking from the previous chunk. Reason: AllocateChunk(clearContent=true) zeroes the new chunk, so its OverflowChunkId field is 0 (the meta
            // chunk's id) until we explicitly write -1. If we linked from prev FIRST and then a background checkpoint snapshotted in the gap, the snapshot
            // would persist `prev → new → 0` (chunk 0 = the meta). On any later eviction-and-reload of `new`, the bad chain becomes permanent. Initialising
            // the new chunk fully before linking eliminates the window: any observer either sees `prev` not-yet-linked (chain ends at prev correctly) or `new`
            // fully-initialised with the -1 sentinel.
            //
            // CRITICAL (#301 cascade fix): use the caller-accessor AllocateChunk overload so the new chunk's content page stays under ACW > 0 protection
            // continuously from ClearChunk through our first write. The legacy `(true, changeSet)` overload has a transient ACW=0 window after its local
            // accessor disposes — a checkpoint racing in that window can snap the zeroed content, fsync, drop DC→0 and let eviction reload zeros over our
            // subsequent writes.
            int overflowChunkId = Segment.AllocateChunk(changeSet, ref accessor);

            byte* ovAddr = accessor.GetChunkAddress(overflowChunkId, true);
            ref var ovHeader = ref GetHeader(ovAddr);
            ovHeader.OlcVersion = 0;
            ovHeader.EntryCount = 1;
            ovHeader.Flags = 0;
            ovHeader.Reserved = 0;
            ovHeader.OverflowChunkId = -1;
            KeysPtr(ovAddr)[0] = key;
            Unsafe.CopyBlock(ValueAt(ovAddr, 0), value, (uint)_valueSize);

            // Re-fetch the predecessor chunk last (the OOM/Grow during AllocateChunk above may have evicted it) and publish the link. Any concurrent snapshot
            // of the predecessor now sees a fully-formed new chunk, not a half-initialised zero state.
            addr = accessor.GetChunkAddress(chunkId, true);
            GetHeader(addr).OverflowChunkId = overflowChunkId;
            return;
        }
    }

    private bool RemoveFromChain(int startChunkId, TKey key, ref ChunkAccessor<TStore> accessor)
    {
        int chunkId = startChunkId;
        int prevChunkId = -1;

        while (chunkId != -1)
        {
            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            TKey* keys = KeysPtr(addr);
            int count = header.EntryCount;

            for (int i = 0; i < count; i++)
            {
                if (keys[i].Equals(key))
                {
                    // Swap with last entry in this chunk (no holes)
                    int lastIdx = count - 1;
                    if (i != lastIdx)
                    {
                        keys[i] = keys[lastIdx];
                        Unsafe.CopyBlock(ValueAt(addr, i), ValueAt(addr, lastIdx), (uint)_valueSize);
                    }
                    header.EntryCount = (byte)(count - 1);

                    // If overflow chunk became empty, unlink and free it
                    if (header.EntryCount == 0 && prevChunkId != -1)
                    {
                        int nextOverflow = header.OverflowChunkId;
                        byte* prevAddr = accessor.GetChunkAddress(prevChunkId, true);
                        GetHeader(prevAddr).OverflowChunkId = nextOverflow;
                        Segment.FreeChunk(chunkId);
                    }

                    return true;
                }
            }

            prevChunkId = chunkId;
            chunkId = header.OverflowChunkId;
        }

        return false;
    }

    private bool UpdateInChain(int startChunkId, TKey key, byte* newValue, ref ChunkAccessor<TStore> accessor)
    {
        int chunkId = startChunkId;

        while (chunkId != -1)
        {
            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            TKey* keys = KeysPtr(addr);
            int count = header.EntryCount;

            for (int i = 0; i < count; i++)
            {
                if (keys[i].Equals(key))
                {
                    Unsafe.CopyBlock(ValueAt(addr, i), newValue, (uint)_valueSize);
                    return true;
                }
            }

            chunkId = header.OverflowChunkId;
        }

        return false;
    }

    /// <summary>
    /// Locate <paramref name="key"/> and invoke <see cref="IRawValueUpdater.Update"/> on the value bytes
    /// while holding the bucket's OLC write lock and with the page registered as dirty in <paramref name="changeSet"/>.
    /// Single chain scan, single dirty mark, single OLC critical section — used to mutate a few bytes of an
    /// existing value in place without paying for a TryGet+Upsert pair (two scans, two OLC ops, one full-record stack copy + rewrite).
    /// </summary>
    /// <returns>true if the key was found and the updater ran; false if the key wasn't present (no mutation).</returns>
    /// <remarks>
    /// The updater is a struct constrained to <see cref="IRawValueUpdater"/> so the JIT can devirtualise the call —
    /// the in-place pattern is hot-path-only (per-migration, per-tick), not occasional, so the devirtualisation matters.
    /// </remarks>
    private bool UpdateInChainCallback<TUpdater>(int startChunkId, TKey key, ref TUpdater updater, ref ChunkAccessor<TStore> accessor)
        where TUpdater : struct, IRawValueUpdater
    {
        int chunkId = startChunkId;

        while (chunkId != -1)
        {
            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            TKey* keys = KeysPtr(addr);
            int count = header.EntryCount;

            for (int i = 0; i < count; i++)
            {
                if (keys[i].Equals(key))
                {
                    updater.Update(ValueAt(addr, i));
                    return true;
                }
            }

            chunkId = header.OverflowChunkId;
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Write API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Insert a key-value pair. Returns true if inserted, false if key already exists.
    /// </summary>
    public bool Insert(TKey key, byte* value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = PackedMeta;
            var (level, next, _) = UnpackMeta(packed);
            int bucket = ResolveBucket(hash, level, next, N0);
            int chunkId = GetBucketChunkId(bucket, ref accessor);

            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            var latch = new OlcLatch(ref header.OlcVersion);
            if (!latch.TryWriteLock())
            {
                continue;
            }

            if (PackedMeta != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            // Check for duplicate
            if (ScanChain(chunkId, key, ref accessor, out _, out _))
            {
                latch.AbortWriteLock();
                return false;
            }

            AppendEntry(chunkId, key, value, ref accessor, changeSet);
            Interlocked.Increment(ref _entryCount);

            // Re-fetch primary for unlock after potential allocation
            byte* unlockAddr = accessor.GetChunkAddress(chunkId, true);
            new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();

            TrySplitIfNeeded(ref accessor, changeSet);
            return true;
        }
    }

    /// <summary>
    /// Insert a key-value pair, skipping duplicate detection. Caller guarantees the key does not exist.
    /// Used for batch inserts of known-unique keys (e.g., freshly generated EntityKeys in FinalizeSpawns).
    /// </summary>
    public void InsertNew(TKey key, byte* value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = PackedMeta;
            var (level, next, _) = UnpackMeta(packed);
            int bucket = ResolveBucket(hash, level, next, N0);
            int chunkId = GetBucketChunkId(bucket, ref accessor);

            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            var latch = new OlcLatch(ref header.OlcVersion);
            if (!latch.TryWriteLock())
            {
                continue;
            }

            if (PackedMeta != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            AppendEntry(chunkId, key, value, ref accessor, changeSet);
            Interlocked.Increment(ref _entryCount);

            byte* unlockAddr = accessor.GetChunkAddress(chunkId, true);
            new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();

            TrySplitIfNeeded(ref accessor, changeSet);
            return;
        }
    }

    /// <summary>
    /// Insert or update a key-value pair. Returns true if inserted, false if updated.
    /// </summary>
    public bool Upsert(TKey key, byte* value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = PackedMeta;
            var (level, next, _) = UnpackMeta(packed);
            int bucket = ResolveBucket(hash, level, next, N0);
            int chunkId = GetBucketChunkId(bucket, ref accessor);

            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            var latch = new OlcLatch(ref header.OlcVersion);
            if (!latch.TryWriteLock())
            {
                continue;
            }

            if (PackedMeta != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            if (UpdateInChain(chunkId, key, value, ref accessor))
            {
                latch.WriteUnlock();
                return false;
            }

            AppendEntry(chunkId, key, value, ref accessor, changeSet);
            Interlocked.Increment(ref _entryCount);

            byte* unlockAddr = accessor.GetChunkAddress(chunkId, true);
            new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();

            TrySplitIfNeeded(ref accessor, changeSet);
            return true;
        }
    }

    /// <summary>
    /// In-place value mutation primitive. Locates <paramref name="key"/>, marks the bucket page dirty, takes the bucket's OLC write lock, calls
    /// <c>updater.Update(valueBytes)</c> on the value pointer, and unlocks. Returns true if the key was found (and the updater ran), false otherwise.
    /// <para>
    /// Use when only a few bytes of an existing value need to change (e.g. updating two fields of a 14-byte ClusterEntityRecord). Avoids the TryGet+Upsert
    /// pair's two chain scans, full-record stack-buffer copy, and double OLC traversal — single descent, single critical section.
    /// </para>
    /// </summary>
    /// <typeparam name="TUpdater">A struct implementing <see cref="IRawValueUpdater"/>. The struct constraint enables JIT devirtualisation of the
    /// <see cref="IRawValueUpdater.Update"/> call so the callback inlines into this loop.</typeparam>
    public bool TryUpdateInPlace<TUpdater>(TKey key, ref TUpdater updater, ref ChunkAccessor<TStore> accessor) where TUpdater : struct, IRawValueUpdater
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = PackedMeta;
            var (level, next, _) = UnpackMeta(packed);
            int bucket = ResolveBucket(hash, level, next, N0);
            int chunkId = GetBucketChunkId(bucket, ref accessor);

            // GetChunkAddress(_, true) marks the page dirty + registers it with the ChangeSet up-front, matching Upsert's pattern. If the key isn't found we
            // still return having marked the page dirty — same belt-and-braces semantics as Upsert when UpdateInChain fails (it would still walk the
            // chain through dirty-marked pages). The cost is negligible vs the chain scan.
            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            var latch = new OlcLatch(ref header.OlcVersion);
            if (!latch.TryWriteLock())
            {
                continue;
            }

            if (PackedMeta != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            bool updated = UpdateInChainCallback(chunkId, key, ref updater, ref accessor);
            latch.WriteUnlock();
            return updated;
        }
    }

    /// <summary>
    /// Remove a key. Returns true if found and removed.
    /// </summary>
    public bool Remove(TKey key, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = PackedMeta;
            var (level, next, _) = UnpackMeta(packed);
            int bucket = ResolveBucket(hash, level, next, N0);
            int chunkId = GetBucketChunkId(bucket, ref accessor);

            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            var latch = new OlcLatch(ref header.OlcVersion);
            if (!latch.TryWriteLock())
            {
                continue;
            }

            if (PackedMeta != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            if (RemoveFromChain(chunkId, key, ref accessor))
            {
                Interlocked.Decrement(ref _entryCount);
                byte* unlockAddr = accessor.GetChunkAddress(chunkId, true);
                new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();
                return true;
            }

            latch.AbortWriteLock();

            if (PackedMeta != packed)
            {
                continue;
            }

            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Split
    // ═══════════════════════════════════════════════════════════════════════

    protected override void ExecuteSplit(ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        var (level, next, bucketCount) = ReadMeta();
        int mod = N0 << level;
        int newMod = mod << 1;
        int oldBucketId = next;
        int newBucketId = next + mod;

        int oldChunkId = GetBucketChunkId(oldBucketId, ref accessor);
        byte* oldAddr = accessor.GetChunkAddress(oldChunkId, true);
        SpinUntilWriteLock(ref GetHeader(oldAddr).OlcVersion);

        // ── Pass 1: count chain length and total entries ─────────────────────────────────────────
        // Caller-level serialization: linear-hash's split path ensures only one thread is running ExecuteSplit for this bucket at
        // a time, and no other writer is touching the bucket's chain while the split is in progress. The WriteLock on the primary
        // chunk is the local manifestation of that invariant — the chain is guaranteed quiescent for the duration of the walk.
        // Two passes (count → allocate-exact → classify) are cheap relative to the bucket's write cost. Previously the code guessed
        // an 8-chunk upper bound; that was insufficient for workloads where linear-hash splits lag relative to a hot bucket's growth
        // (bucket keeps accumulating overflow chunks until the algorithm round-robins to split it), and the fixed cap threw
        // InvalidOperationException with "move buffer overflow (72 >= 72)" mid-commit, corrupting the txn.
        int totalEntries = 0;
        int overflowChainLength = 0;   // count of OVERFLOW chunks (excluding the primary)
        {
            int countWalkId = oldChunkId;
            while (countWalkId != -1)
            {
                byte* countAddr = accessor.GetChunkAddress(countWalkId);
                ref readonly var countHeader = ref GetHeader(countAddr);
                totalEntries += countHeader.EntryCount;
                if (countWalkId != oldChunkId) overflowChainLength++;
                countWalkId = countHeader.OverflowChunkId;
            }
        }

        int entrySize = sizeof(TKey) + _valueSize;
        // StackEntryThreshold bounds the stackalloc fast path: at 72 entries × typical 12-16 B/entry × 2 buffers = ~2 KB. Safe
        // on every call stack we'd realistically see. Chains beyond this spill to ArrayPool rental (pinned) — rare enough that
        // the pool-rent overhead doesn't matter.
        const int StackEntryThreshold = 72;

        byte* keepBuf;
        byte* moveBuf;
        int bufCapacity;   // entries per buffer — determines the keys/values offset split
        byte[] rentedKeep = null;
        byte[] rentedMove = null;
        GCHandle keepHandle = default;
        GCHandle moveHandle = default;
        try
        {
            if (totalEntries <= StackEntryThreshold)
            {
                bufCapacity = StackEntryThreshold;
                byte* k = stackalloc byte[StackEntryThreshold * entrySize];
                byte* m = stackalloc byte[StackEntryThreshold * entrySize];
                keepBuf = k;
                moveBuf = m;
            }
            else
            {
                bufCapacity = totalEntries;
                int bufBytes = totalEntries * entrySize;
                rentedKeep = ArrayPool<byte>.Shared.Rent(bufBytes);
                rentedMove = ArrayPool<byte>.Shared.Rent(bufBytes);
                // Pin the rented arrays so the byte* pointers stay valid across the classify loop. GCHandle.Free in finally
                // releases the pinning; the arrays return to the pool in the same finally.
                keepHandle = GCHandle.Alloc(rentedKeep, GCHandleType.Pinned);
                moveHandle = GCHandle.Alloc(rentedMove, GCHandleType.Pinned);
                keepBuf = (byte*)keepHandle.AddrOfPinnedObject();
                moveBuf = (byte*)moveHandle.AddrOfPinnedObject();
            }

            TKey* keepKeys = (TKey*)keepBuf;
            byte* keepValues = keepBuf + bufCapacity * sizeof(TKey);
            TKey* moveKeys = (TKey*)moveBuf;
            byte* moveValues = moveBuf + bufCapacity * sizeof(TKey);
            int keepCount = 0, moveCount = 0;

            // Overflow IDs: sized to the exact chain length from pass 1. Stack-alloc the common case; rent for deep chains.
            // OverflowStackCap is one slot larger than the gate's upper bound (< 32) so even a chain that exactly hits the gate
            // has one cushion slot — protects against a future edit that accidentally raises the gate without resizing the buffer.
            // The rented-array branch slices to exactly overflowChainLength so AsSpan's length equals the pass-1 count — makes
            // the "overflowCount == overflowChainLength at end of pass 2" invariant visible at the span level.
            int[] rentedOverflowIds = null;
            const int OverflowStackCap = 33;
            Span<int> overflowIds = overflowChainLength < 32
                ? stackalloc int[OverflowStackCap]
                : (rentedOverflowIds = ArrayPool<int>.Shared.Rent(overflowChainLength)).AsSpan(0, overflowChainLength);
            int overflowCount = 0;

            try
            {
                // ── Pass 2: classify entries into keep/move buffers ────────────────────────────
                int walkId = oldChunkId;
                while (walkId != -1)
                {
                    byte* wAddr = accessor.GetChunkAddress(walkId);
                    ref readonly var wHeader = ref GetHeader(wAddr);
                    TKey* wKeys = KeysPtr(wAddr);
                    int count = wHeader.EntryCount;
                    int nextId = wHeader.OverflowChunkId;

                    for (int i = 0; i < count; i++)
                    {
                        TKey key = wKeys[i];
                        uint hash = ComputeHash(key);
                        int targetBucket = (int)(hash & (uint)(newMod - 1));

                        if (targetBucket == oldBucketId)
                        {
                            keepKeys[keepCount] = key;
                            Unsafe.CopyBlock(keepValues + keepCount * _valueSize, ValueAt(wAddr, i), (uint)_valueSize);
                            keepCount++;
                        }
                        else
                        {
                            moveKeys[moveCount] = key;
                            Unsafe.CopyBlock(moveValues + moveCount * _valueSize, ValueAt(wAddr, i), (uint)_valueSize);
                            moveCount++;
                        }
                    }

                    if (walkId != oldChunkId)
                    {
                        overflowIds[overflowCount] = walkId;
                        overflowCount++;
                    }

                    walkId = nextId;
                }

                // Rewrite old bucket
                RewriteBucket(oldChunkId, keepKeys, keepValues, keepCount, ref accessor, changeSet);

                // Allocate and write new bucket — caller-accessor overload keeps ACW > 0 on the new chunk's content page from ClearChunk through
                // WriteBucket's header/keys/values writes (#301).
                int newChunkId = Segment.AllocateChunk(changeSet, ref accessor);
                WriteBucket(newChunkId, moveKeys, moveValues, moveCount, ref accessor, changeSet);

                EnsureDirectoryCapacity(newBucketId, ref accessor, changeSet);
                SetBucketChunkId(newBucketId, newChunkId, ref accessor);

                // Free ALL overflow chunks — overflowIds is now sized to the real chain length from pass 1, so no excess leaks
                // into the free-list like before. Previously the array was capped at 8 and any overflow past that leaked chunks.
                for (int i = 0; i < overflowCount; i++)
                {
                    Segment.FreeChunk(overflowIds[i]);
                }

                int newNext = next + 1;
                int newLevel = level;
                if (newNext >= mod)
                {
                    newNext = 0;
                    newLevel = level + 1;
                }
                PackedMeta = PackMeta(newLevel, newNext, bucketCount + 1);
                FlushMetaToChunk(ref accessor);

                byte* unlockAddr = accessor.GetChunkAddress(oldChunkId, true);
                new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();
            }
            finally
            {
                if (rentedOverflowIds != null) ArrayPool<int>.Shared.Return(rentedOverflowIds);
            }
        }
        finally
        {
            if (keepHandle.IsAllocated) keepHandle.Free();
            if (moveHandle.IsAllocated) moveHandle.Free();
            if (rentedKeep != null) ArrayPool<byte>.Shared.Return(rentedKeep);
            if (rentedMove != null) ArrayPool<byte>.Shared.Return(rentedMove);
        }
    }

    private void RewriteBucket(int chunkId, TKey* keys, byte* values, int entryCount, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        byte* addr = accessor.GetChunkAddress(chunkId, true);
        ref var header = ref GetHeader(addr);
        int count = Math.Min(entryCount, _bucketCapacity);
        header.EntryCount = (byte)count;
        header.OverflowChunkId = -1;

        TKey* dstKeys = KeysPtr(addr);
        for (int i = 0; i < count; i++)
        {
            dstKeys[i] = keys[i];
            Unsafe.CopyBlock(ValueAt(addr, i), values + i * _valueSize, (uint)_valueSize);
        }

        if (entryCount > count)
        {
            WriteOverflowChain(chunkId, keys + count, values + count * _valueSize, entryCount - count, ref accessor, changeSet);
        }
    }

    private void WriteBucket(int chunkId, TKey* keys, byte* values, int entryCount, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        byte* addr = accessor.GetChunkAddress(chunkId, true);
        ref var header = ref GetHeader(addr);
        int count = Math.Min(entryCount, _bucketCapacity);
        header.OlcVersion = 4;
        header.EntryCount = (byte)count;
        header.Flags = 0;
        header.Reserved = 0;
        header.OverflowChunkId = -1;

        TKey* dstKeys = KeysPtr(addr);
        for (int i = 0; i < count; i++)
        {
            dstKeys[i] = keys[i];
            Unsafe.CopyBlock(ValueAt(addr, i), values + i * _valueSize, (uint)_valueSize);
        }

        if (entryCount > count)
        {
            WriteOverflowChain(chunkId, keys + count, values + count * _valueSize, entryCount - count, ref accessor, changeSet);
        }
    }

    private void WriteOverflowChain(int parentChunkId, TKey* keys, byte* values, int entryCount, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        if (entryCount == 0)
        {
            return;
        }

        int prevChunkId = parentChunkId;
        int offset = 0;

        while (offset < entryCount)
        {
            // ORDER MATTERS (#301 race-fix): same hazard as AppendEntry — initialise the new chunk FIRST, including OverflowChunkId = -1, then link from
            // prev. The previous order (link → init) left a window where a CheckpointManager snapshot could persist `prev → new → 0` (chunk 0 = meta).
            // Caller-accessor overload (#301 cascade fix) keeps ACW > 0 on the new chunk's content page continuously, preventing checkpoint-then-evict from
            // losing the writes below.
            int overflowChunkId = Segment.AllocateChunk(changeSet, ref accessor);

            byte* ovAddr = accessor.GetChunkAddress(overflowChunkId, true);
            ref var ovHeader = ref GetHeader(ovAddr);
            int writeCount = Math.Min(entryCount - offset, _bucketCapacity);
            ovHeader.OlcVersion = 0;
            ovHeader.EntryCount = (byte)writeCount;
            ovHeader.Flags = 0;
            ovHeader.Reserved = 0;
            ovHeader.OverflowChunkId = -1;

            TKey* dstKeys = KeysPtr(ovAddr);
            for (int i = 0; i < writeCount; i++)
            {
                dstKeys[i] = keys[offset + i];
                Unsafe.CopyBlock(ValueAt(ovAddr, i), values + (offset + i) * _valueSize, (uint)_valueSize);
            }

            // Link from prev LAST so any concurrent snapshot of prev sees a fully-formed new chunk.
            byte* prevAddr = accessor.GetChunkAddress(prevChunkId, true);
            GetHeader(prevAddr).OverflowChunkId = overflowChunkId;

            prevChunkId = overflowChunkId;
            offset += writeCount;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Rebuild support
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Insert without OLC or duplicate check. Single-threaded rebuild/recovery only. Triggers splits.
    /// </summary>
    internal void InsertDuringRebuild(TKey key, byte* value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        uint hash = ComputeHash(key);
        var (level, next, _) = UnpackMeta(PackedMeta);
        int bucket = ResolveBucket(hash, level, next, N0);
        int chunkId = GetBucketChunkId(bucket, ref accessor);

        AppendEntry(chunkId, key, value, ref accessor, changeSet);
        _entryCount++;
        TrySplitIfNeeded(ref accessor, changeSet);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Factory methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Create a new raw value hash map, allocating meta + directory + initial buckets.</summary>
    public static RawValuePagedHashMap<TKey, TStore> Create(ChunkBasedSegment<TStore> segment, int n0, int valueSize, ChangeSet changeSet = null)
    {
        Debug.Assert(n0 > 0 && BitOperations.IsPow2(n0), "N0 must be a positive power of 2");

        using var guard = EpochGuard.Enter(segment.Store.EpochManager);

        var map = new RawValuePagedHashMap<TKey, TStore>(segment, n0, valueSize);
        map.InitializeCreate(n0, changeSet);
        return map;
    }

    /// <summary>Open an existing raw value hash map by reading meta from chunk 0.</summary>
    public static RawValuePagedHashMap<TKey, TStore> Open(ChunkBasedSegment<TStore> segment, int n0, int valueSize)
    {
        using var guard = EpochGuard.Enter(segment.Store.EpochManager);

        var map = new RawValuePagedHashMap<TKey, TStore>(segment, n0, valueSize);
        map.InitializeOpen();
        return map;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Enumeration (broad scan)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Callback interface for zero-overhead iteration via JIT specialization.
    /// </summary>
    internal interface IEntryAction<in TK> where TK : unmanaged
    {
        /// <summary>Process one entry. Return false to stop iteration.</summary>
        bool Process(TK key, byte* value);
    }

    /// <summary>
    /// Iterate all live entries in the hash map, calling <paramref name="action"/> for each, under the OLC read protocol so the
    /// scan is safe against concurrent writers (insert / overflow append / bucket split). Each bucket is read optimistically into
    /// a scratch buffer; if a concurrent writer bumps the bucket's head version — or the directory splits — mid-read, the bucket
    /// is re-read. Entries are handed to <paramref name="action"/> only once a consistent snapshot of the bucket has been
    /// captured: the callback may have side effects (it collects / invokes), so it must never observe a torn read.
    /// <para>
    /// Snapshot semantics: an entry present for the whole scan is visited exactly once; an entry concurrently inserted, removed,
    /// or relocated (by a split) during the scan may or may not be visited. Callers that need a fully consistent view must run
    /// against a quiescent map (the engine's broad-scan query path already does — it runs on the owning transaction thread).
    /// Buckets are visited in directory order (cache-friendly).
    /// </para>
    /// </summary>
    /// <returns>Number of entries visited (handed to <paramref name="action"/>).</returns>
    internal int ForEachEntry<TAction>(ref ChunkAccessor<TStore> accessor, ref TAction action) where TAction : struct, IEntryAction<TKey>
    {
        int visited = 0;
        var (_, _, bucketCount) = ReadMeta();

        // Per-bucket optimistic snapshot buffers. The value buffer lives on the pinned object heap so the byte* handed to
        // action.Process stays valid without a fixed region spanning the (retryable) read. Both grow on demand to the largest
        // bucket seen — a single small allocation amortised across the whole O(n) scan.
        int bufCap = _bucketCapacity * 2;
        var keyBuf = new TKey[bufCap];
        var valBuf = GC.AllocateUninitializedArray<byte>(bufCap * _valueSize, pinned: true);
        byte* valPtr = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(valBuf));

        for (int b = 0; b < bucketCount; b++)
        {
            while (true)   // OLC retry for this bucket
            {
                // Re-read fresh each attempt: a concurrent writer can grow the segment (raising the capacity) mid-scan, so a
                // legitimately-valid head/overflow chunk id may exceed a stale snapshot — caching it would livelock the retry.
                int chunkCapacity = Segment.ChunkCapacity;
                long packed = PackedMeta;
                int headId = GetBucketChunkId(b, ref accessor);
                if (headId < 0)
                {
                    break;   // empty bucket
                }
                if ((uint)headId >= (uint)chunkCapacity)
                {
                    continue;   // torn directory read — retry
                }

                byte* headAddr = accessor.GetChunkAddress(headId);
                var latch = new OlcLatch(ref GetHeader(headAddr).OlcVersion);
                int version = latch.ReadVersion();
                if (version == 0)
                {
                    continue;   // bucket write-locked — retry
                }

                // Optimistically buffer the whole chain. Counts are clamped to _bucketCapacity and chunk ids are range-checked,
                // so a torn read can never index past a chunk (ValueAt assert) or follow a wild OverflowChunkId; the version
                // validation below discards any inconsistent snapshot before it reaches the callback.
                int n = 0;
                bool retry = false;
                int chunkId = headId;
                for (int walk = 0; chunkId >= 0; walk++)
                {
                    if ((uint)chunkId >= (uint)chunkCapacity || walk > chunkCapacity)
                    {
                        retry = true;   // torn OverflowChunkId or cycle from a repurposed chunk
                        break;
                    }

                    byte* addr = accessor.GetChunkAddress(chunkId);
                    ref readonly var header = ref GetHeader(addr);
                    int rawCount = header.EntryCount;
                    int count = rawCount <= _bucketCapacity ? rawCount : _bucketCapacity;
                    int nextId = header.OverflowChunkId;

                    if (n + count > bufCap)
                    {
                        bufCap = Math.Max(bufCap * 2, n + count);
                        keyBuf = new TKey[bufCap];
                        valBuf = GC.AllocateUninitializedArray<byte>(bufCap * _valueSize, pinned: true);
                        valPtr = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(valBuf));
                        retry = true;   // restart the bucket with the larger buffer
                        break;
                    }

                    TKey* keys = KeysPtr(addr);
                    for (int i = 0; i < count; i++)
                    {
                        keyBuf[n] = keys[i];
                        Unsafe.CopyBlock(valPtr + n * _valueSize, ValueAt(addr, i), (uint)_valueSize);
                        n++;
                    }

                    chunkId = nextId;
                }

                if (retry)
                {
                    continue;
                }

                // Validate the bucket stayed quiescent for the whole read: no writer bumped the head version, no split changed
                // the directory. On failure, re-read the bucket from the head.
                if (!latch.ValidateVersion(version) || PackedMeta != packed)
                {
                    continue;
                }

                for (int i = 0; i < n; i++)
                {
                    if (!action.Process(keyBuf[i], valPtr + i * _valueSize))
                    {
                        return visited;
                    }
                    visited++;
                }

                break;   // bucket complete
            }
        }

        return visited;
    }

}
