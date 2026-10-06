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
/// pointers with <see cref="Unsafe.CopyBlock(void*, void*, uint)"/> instead of typed <c>ref TValue</c>.
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

unsafe partial class RawValuePagedHashMap<TKey, TStore> : PagedHashMapBase<TStore> where TKey : unmanaged, IEquatable<TKey> where TStore : struct, IPageStore
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

    /// <summary>This map's engine-scoped <c>EW-01</c> guard. The EntityMap is one of the structures the tick fence rewrites, so a mutation from
    /// outside the fence while it is open is the same defect as one on a cluster B+Tree.</summary>
    private readonly ExclusiveWindow _fenceWindow;

    private RawValuePagedHashMap(ChunkBasedSegment<TStore> segment, int n0, int valueSize) : base(segment, n0)
    {
        _fenceWindow = segment?.FenceWindow;
        Debug.Assert(valueSize > 0, "Value size must be positive");
        _valueSize = valueSize;
        _bucketCapacity = (segment!.Stride - sizeof(PagedHashMapBucketHeader)) / (sizeof(TKey) + valueSize);
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

    /// <summary>
    /// The head chunk's latch, resolved again through <paramref name="accessor"/>. A bucket read validates against this, not the latch it took at the head:
    /// walking the overflow chain loads other pages through the same accessor, and a scan accessor (EP-02) may have let the head's page go meanwhile, so a
    /// reference taken before the walk could point into a slot that now holds another page. The version lives in the page, so a reloaded head still carries
    /// every bump a writer made: a page being written is dirty, and a dirty page is written back before it can be evicted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static OlcLatch HeadLatch(int headChunkId, ref ChunkAccessor<TStore> accessor)
        => new(ref GetHeader(accessor.GetChunkAddress(headChunkId)).OlcVersion);

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

    /// <summary>The overflow chunk id linked from <paramref name="chunkId"/>, or -1 when the chain ends there. Tests only.</summary>
    /// <remarks>Lets <c>AC-7.6</c> CONSTRUCT its overflow-chain case and fail loudly when the map has none, instead of hoping one exists.</remarks>
    internal static int BucketOverflowChunkIdForTest(int chunkId, ref ChunkAccessor<TStore> accessor) 
        => GetHeader(accessor.GetChunkAddress(chunkId)).OverflowChunkId;

    /// <summary>Raw hint-slot contents for <paramref name="key"/>: packed <c>((chunkId &lt;&lt; 8) | index) + 1</c>, or 0 when unset. Tests only.</summary>
    /// <remarks>
    /// Exposed for <c>AC-7.2</c>. Comparing <c>TryGetWithHint</c> against <c>TryGet</c> proves the two AGREE, which a cache that was silently cleared would
    /// also satisfy; reading the slot is what distinguishes "the hint survived" from "the hint was thrown away and the fallback covered for it".
    /// </remarks>
    internal long HintSlotForTest(TKey key)
    {
        var hints = _locationHints;
        return hints == null ? 0 : hints[HintSlot(key, hints.Length)];
    }

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

            if (!HeadLatch(chunkId, ref accessor).ValidateVersion(version))
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

    // ═══════════════════════════════════════════════════════════════════════
    // Hinted read path (per-key location cache)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Power-of-2 location-hint cache: low key bits → packed <c>((bucketChunkId &lt;&lt; 8) | indexInBucket) + 1</c> (0 = empty slot). Pure accelerator
    /// for <see cref="TryGetWithHint"/>: a valid hint skips the hash + bucket-directory resolve + chain scan (one chunk-address translation + key compare
    /// instead). Hints are stored only for entries found in a bucket ROOT chunk — roots are never freed while the map lives (only empty overflow chunks
    /// are, see <see cref="RemoveFromChain"/>) — so a stale hint can never dereference a freed chunk. Staleness and slot collisions are caught by the
    /// EntryCount/key compare + OLC validate (whose pre-validation barrier makes the plain data reads safe on arm64 — see
    /// <see cref="OlcLatch.ValidateVersion"/>) and fall back to the full lookup, which refreshes the hint. Slots are aligned 8-byte longs (single-copy
    /// atomic on x64 and arm64); a torn/stale slot is validated like any other hint. No correctness dependency — the cache can be dropped at any time.
    /// </summary>
    private long[] _locationHints;

    /// <summary>Hint-cache slot count ceiling (2^20 slots = 8 MiB) — bounds per-map memory on huge maps; beyond it collisions just lower the hit rate.</summary>
    private const int MaxHintSlots = 1 << 20;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HintSlot(TKey key, int length) =>
        sizeof(TKey) == 8
            ? (int)(Unsafe.As<TKey, long>(ref key) & (length - 1))
            : (int)(ComputeHash(key) & (uint)(length - 1));

    /// <summary>
    /// <see cref="TryGet"/> with the location-hint cache in front: monotonic dense keys (e.g. EntityKey) hit the hint slot directly and resolve in a
    /// single bucket-chunk visit. Identical semantics/result to <see cref="TryGet"/>.
    /// </summary>
    public bool TryGetWithHint(TKey key, byte* valueOut, ref ChunkAccessor<TStore> accessor)
    {
        var hints = _locationHints;
        if (hints != null)
        {
            long packedLoc = hints[HintSlot(key, hints.Length)];
            if (packedLoc != 0)
            {
                long loc = packedLoc - 1;
                int hChunkId = (int)(loc >> 8);
                int hIndex = (int)(loc & 0xFF);
                byte* addr = accessor.GetChunkAddress(hChunkId);
                ref var header = ref GetHeader(addr);
                var latch = new OlcLatch(ref header.OlcVersion);
                int version = latch.ReadVersion();
                if (version != 0 && hIndex < header.EntryCount && KeysPtr(addr)[hIndex].Equals(key))
                {
                    Unsafe.CopyBlock(valueOut, ValueAt(addr, hIndex), (uint)_valueSize);
                    if (latch.ValidateVersion(version))
                    {
                        return true;
                    }
                }
            }
        }

        return TryGetFullAndRefreshHint(key, valueOut, ref accessor);
    }

    /// <summary>Full <see cref="TryGet"/> lookup that additionally stores/refreshes the location hint on a root-chunk hit.</summary>
    private bool TryGetFullAndRefreshHint(TKey key, byte* valueOut, ref ChunkAccessor<TStore> accessor)
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

            if (!HeadLatch(chunkId, ref accessor).ValidateVersion(version))
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            if (!found && PackedMeta != packed)
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            // Root-chunk hits only (see _locationHints doc) — overflow chunks can be freed, roots cannot.
            if (found && fChunkId == chunkId)
            {
                StoreHint(key, fChunkId, fIndex, level, next);
            }

            return found;
        }
    }

    private void StoreHint(TKey key, int chunkId, int index, int level, int next)
    {
        var hints = _locationHints;

        // Size the cache to the map: pow2 ≥ bucketCount × bucketCapacity, floor 4096, ceiling MaxHintSlots. Growing replaces the array (old hints
        // re-warm lazily); readers always index with the local array's own length, so a concurrent swap is benign.
        int totalBuckets = (N0 << level) + next;
        long target = Math.Min((long)totalBuckets * _bucketCapacity, MaxHintSlots);
        int desired = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(4096, target));
        if (hints == null || hints.Length < desired)
        {
            hints = new long[desired];
            _locationHints = hints;
        }

        hints[HintSlot(key, hints.Length)] = (((long)chunkId << 8) | (uint)index) + 1;
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
            //
            // IXW-07: the chunk was reserved before the bucket was locked, its page pinned; this takes it rather than allocating under the lock.
            int overflowChunkId = ChunkReservation<TStore>.AllocateUnderLatch(Segment, changeSet, ref accessor);
            Interlocked.Increment(ref _overflowChunksChained);

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
    /// while holding the bucket's OLC write lock and marking the touched chunk dirty through <paramref name="accessor"/>.
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
        _fenceWindow?.NoteMutation("EntityMap.Insert");
        return AppendUnderBucketLock(key, value, BucketWrite.Insert, ref accessor, changeSet);
    }

    /// <summary>
    /// Insert a key-value pair, skipping duplicate detection. Caller guarantees the key does not exist.
    /// Used for batch inserts of known-unique keys (e.g., freshly generated EntityKeys in FinalizeSpawns).
    /// </summary>
    public void InsertNew(TKey key, byte* value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        _fenceWindow?.NoteMutation("EntityMap.InsertNew");
        AppendUnderBucketLock(key, value, BucketWrite.InsertNew, ref accessor, changeSet);
    }

    /// <summary>
    /// Insert or update a key-value pair. Returns true if inserted, false if updated.
    /// </summary>
    public bool Upsert(TKey key, byte* value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        _fenceWindow?.NoteMutation("EntityMap.Upsert");
        return AppendUnderBucketLock(key, value, BucketWrite.Upsert, ref accessor, changeSet);
    }

    /// <summary>What <see cref="AppendUnderBucketLock"/> does before appending: reject a duplicate, update in place, or neither.</summary>
    private enum BucketWrite : byte
    {
        Insert,
        InsertNew,
        Upsert,
    }

    /// <summary>
    /// The locked write shared by <see cref="Insert"/>, <see cref="InsertNew"/> and <see cref="Upsert"/>. Returns true when the entry was appended, false
    /// for a duplicate (<see cref="BucketWrite.Insert"/>) or an in-place update (<see cref="BucketWrite.Upsert"/>).
    /// </summary>
    /// <remarks>
    /// IXW-07: nothing under the bucket lock allocates. When the chain's tail is full the lock is released, an overflow chunk is reserved
    /// (<see cref="ChunkReservation{TStore}"/>), and the bucket is locked and inspected again; the append then takes the reserved chunk, whose page is
    /// pinned. An allocation can grow the segment and wait seconds on page-cache back-pressure, or throw — under the lock the wait held every writer of
    /// the bucket, and the throw left the bucket locked for good: a storm froze at 175 623 operations with a writer spinning on it. What the lock still
    /// covers may fault pages in (the chain walks), and a fault there releases the lock without a version bump, since nothing was written yet.
    /// </remarks>
    private bool AppendUnderBucketLock(TKey key, byte* value, BucketWrite mode, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        uint hash = ComputeHash(key);
        var reservation = ChunkReservation<TStore>.Current;
        reservation.Begin(Segment);
        try
        {
            while (true)
            {
                long packed = PackedMeta;
                var (level, next, _) = UnpackMeta(packed);
                int bucket = ResolveBucket(hash, level, next, N0);
                int chunkId = GetBucketChunkId(bucket, ref accessor);

                // Reserve before locking when the tail already looks full: the check under the lock below then finds the chunk waiting, instead of
                // releasing the lock to reserve and walking the chain again. An unlatched read, so only a hint; the locked check stays the authority.
                if (reservation.Available == 0 && ChainOverflowChunksHint(chunkId, 1, ref accessor) > 0)
                {
                    reservation.Fill(1, changeSet, ref accessor);
                }

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

                bool tailFull;
                var updated = false;
                try
                {
                    if (mode == BucketWrite.Insert && ScanChain(chunkId, key, ref accessor, out _, out _))
                    {
                        HeadLatchForRelease(chunkId, ref accessor).AbortWriteLock();
                        return false;
                    }

                    if (mode == BucketWrite.Upsert)
                    {
                        // Set before the update, which writes in place: a fault from here to the release must bump the version, or an optimistic reader
                        // overlapping the write validates the value it saw half-written.
                        updated = true;
                        if (UpdateInChain(chunkId, key, value, ref accessor))
                        {
                            HeadLatchForRelease(chunkId, ref accessor).WriteUnlock();
                            return false;
                        }

                        updated = false;   // not in the chain: nothing written
                    }

                    tailFull = ChainTailIsFull(chunkId, ref accessor);
                }
                catch
                {
                    ReleaseAfterFault(chunkId, written: updated, ref accessor);
                    throw;
                }

                if (tailFull && reservation.Available == 0)
                {
                    HeadLatchForRelease(chunkId, ref accessor).AbortWriteLock();
                    reservation.Fill(1, changeSet, ref accessor);
                    continue;
                }

                try
                {
                    AppendEntry(chunkId, key, value, ref accessor, changeSet);
                    Interlocked.Increment(ref _entryCount);

                    // Re-fetch primary for unlock after potential allocation
                    HeadLatchForRelease(chunkId, ref accessor).WriteUnlock();
                }
                catch
                {
                    ReleaseAfterFault(chunkId, written: true, ref accessor);
                    throw;
                }

                TrySplitIfNeeded(ref accessor, changeSet);
                return true;
            }
        }
        finally
        {
            reservation.End();
        }
    }

    /// <summary>
    /// Overflow chunks appending <paramref name="entries"/> entries to the chain at <paramref name="startChunkId"/> would take, read WITHOUT the bucket
    /// lock: a hint for reserving before the lock is taken (IXW-07), never an answer. A writer may be rewriting the chain, so every chunk id is
    /// range-checked and the walk is cut at the segment's capacity; anything inconsistent answers 0, and the locked check decides.
    /// </summary>
    private int ChainOverflowChunksHint(int startChunkId, int entries, ref ChunkAccessor<TStore> accessor)
    {
        var capacity = Segment.ChunkCapacity;
        var chunkId = startChunkId;
        for (var walk = 0; walk <= capacity; walk++)
        {
            if ((uint)chunkId >= (uint)capacity)
            {
                return 0;
            }

            ref readonly var header = ref GetHeader(accessor.GetChunkAddress(chunkId));
            var next = header.OverflowChunkId;
            if (next == -1)
            {
                var count = Math.Min((int)header.EntryCount, _bucketCapacity);
                var beyondTail = entries - (_bucketCapacity - count);
                return beyondTail <= 0 ? 0 : (beyondTail + _bucketCapacity - 1) / _bucketCapacity;
            }

            chunkId = next;
        }

        return 0;
    }

    /// <summary>Whether appending to the chain at <paramref name="startChunkId"/> needs a new overflow chunk: its last chunk is full.</summary>
    private bool ChainTailIsFull(int startChunkId, ref ChunkAccessor<TStore> accessor)
    {
        int chunkId = startChunkId;
        while (true)
        {
            ref readonly var header = ref GetHeader(accessor.GetChunkAddress(chunkId));
            if (header.OverflowChunkId == -1)
            {
                return header.EntryCount >= _bucketCapacity;
            }

            chunkId = header.OverflowChunkId;
        }
    }

    /// <summary>
    /// The bucket's head latch, fetched again and marked dirty, for the release after a chain walk: the walk may have loaded other pages through the
    /// accessor since the lock was taken, and a release is a write to the head page.
    /// </summary>
    private static OlcLatch HeadLatchForRelease(int headChunkId, ref ChunkAccessor<TStore> accessor)
        => new(ref GetHeader(accessor.GetChunkAddress(headChunkId, true)).OlcVersion);

    /// <summary>
    /// Releases a bucket lock on the way out of a fault: <see cref="OlcLatch.AbortWriteLock"/> when nothing was written under it, otherwise
    /// <see cref="OlcLatch.WriteUnlock"/>, whose version bump sends every optimistic reader back. A failure to re-fetch the head is swallowed: the fault
    /// already propagating is the one the caller has to see.
    /// </summary>
    private static void ReleaseAfterFault(int headChunkId, bool written, ref ChunkAccessor<TStore> accessor)
    {
        try
        {
            var latch = HeadLatchForRelease(headChunkId, ref accessor);
            if (written)
            {
                latch.WriteUnlock();
            }
            else
            {
                latch.AbortWriteLock();
            }
        }
        catch
        {
            // See the summary.
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
        _fenceWindow?.NoteMutation("EntityMap.TryUpdateInPlace");
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
        _fenceWindow?.NoteMutation("EntityMap.Remove");
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

    /// <remarks>
    /// IXW-07: the split takes every chunk it writes under the old bucket's lock from a reservation made with the lock released — the new bucket's chain,
    /// both halves' overflow and the directory's growth — and pins every page it writes before its first write, so nothing under the lock allocates or
    /// faults a page in once the bucket is being rewritten. A fault before that releases the lock without a version bump; one after it (none is expected)
    /// releases it with one, rather than leaving the bucket locked for good.
    /// </remarks>
    protected override void ExecuteSplit(ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        var reservation = ChunkReservation<TStore>.Current;
        reservation.Begin(Segment);
        try
        {
            ExecuteSplitReserved(ref accessor, changeSet, reservation);
        }
        finally
        {
            reservation.End();
        }
    }

    /// <summary>
    /// The chunks splitting the bucket at <paramref name="oldChunkId"/> takes, estimated WITHOUT its lock — the count <see cref="ExecuteSplitReserved"/>
    /// makes exactly under the lock, as a hint for reserving before it. Range-checked and cut at the segment's capacity; anything inconsistent answers 0.
    /// </summary>
    private int SplitChunksHint(int oldChunkId, int oldBucketId, int newMod, int newBucketId, ref ChunkAccessor<TStore> accessor)
    {
        var capacity = Segment.ChunkCapacity;
        int total = 0, move = 0;
        var chunkId = oldChunkId;
        for (var walk = 0; chunkId != -1; walk++)
        {
            if ((uint)chunkId >= (uint)capacity || walk > capacity)
            {
                return 0;
            }

            byte* addr = accessor.GetChunkAddress(chunkId);
            ref readonly var header = ref GetHeader(addr);
            var count = Math.Min((int)header.EntryCount, _bucketCapacity);
            TKey* keys = KeysPtr(addr);
            for (var i = 0; i < count; i++)
            {
                if ((int)(ComputeHash(keys[i]) & (uint)(newMod - 1)) != oldBucketId)
                {
                    move++;
                }
            }

            total += count;
            chunkId = header.OverflowChunkId;
        }

        var keep = total - move;
        var newChain = Math.Max(1, (move + _bucketCapacity - 1) / _bucketCapacity);
        var keepOverflow = Math.Max(0, (keep + _bucketCapacity - 1) / _bucketCapacity - 1);
        var missingDirChunks = Math.Max(0, (newBucketId >> PagedHashMapDirectory.Shift) + 1 - DirectoryChunkCount(ref accessor));
        return Math.Min(newChain + keepOverflow + 2 * missingDirChunks, ChunkReservation<TStore>.InitialCapacity);
    }

    private void ExecuteSplitReserved(ref ChunkAccessor<TStore> accessor, ChangeSet changeSet, ChunkReservation<TStore> reservation)
    {
        var (level, next, bucketCount) = ReadMeta();
        int mod = N0 << level;
        int newMod = mod << 1;
        int oldBucketId = next;
        int newBucketId = next + mod;

        int oldChunkId = GetBucketChunkId(oldBucketId, ref accessor);

        // Reserve before locking, from an unlatched estimate: the exact count under the lock then usually finds enough, instead of releasing the lock to
        // reserve and counting the chain again.
        reservation.Fill(SplitChunksHint(oldChunkId, oldBucketId, newMod, newBucketId, ref accessor), changeSet, ref accessor);

        int totalEntries;
        int overflowChainLength;
        while (true)
        {
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
            totalEntries = 0;
            overflowChainLength = 0;   // count of OVERFLOW chunks (excluding the primary)
            var moveEntries = 0;
            int need;
            try
            {
                int countWalkId = oldChunkId;
                while (countWalkId != -1)
                {
                    byte* countAddr = accessor.GetChunkAddress(countWalkId);
                    ref readonly var countHeader = ref GetHeader(countAddr);
                    totalEntries += countHeader.EntryCount;
                    TKey* countKeys = KeysPtr(countAddr);
                    for (int i = 0; i < countHeader.EntryCount; i++)
                    {
                        if ((int)(ComputeHash(countKeys[i]) & (uint)(newMod - 1)) != oldBucketId)
                        {
                            moveEntries++;
                        }
                    }

                    if (countWalkId != oldChunkId) overflowChainLength++;
                    reservation.Pin(countWalkId, ref accessor);   // rewritten or freed below, under the lock
                    countWalkId = countHeader.OverflowChunkId;
                }

                // Chunks the split takes under the lock, exactly: the new bucket's chain, the old bucket's overflow past its primary, and the directory's
                // growth — a directory chunk per chunk it lacks, each possibly with an overflow directory index chunk.
                var keepEntries = totalEntries - moveEntries;
                var newChain = Math.Max(1, (moveEntries + _bucketCapacity - 1) / _bucketCapacity);
                var keepOverflow = Math.Max(0, (keepEntries + _bucketCapacity - 1) / _bucketCapacity - 1);
                var missingDirChunks = Math.Max(0, (newBucketId >> PagedHashMapDirectory.Shift) + 1 - DirectoryChunkCount(ref accessor));
                need = newChain + keepOverflow + 2 * missingDirChunks;
            }
            catch
            {
                ReleaseAfterFault(oldChunkId, written: false, ref accessor);
                throw;
            }

            if (reservation.Available >= need)
            {
                break;
            }

            HeadLatchForRelease(oldChunkId, ref accessor).AbortWriteLock();
            reservation.Fill(need, changeSet, ref accessor);
        }

        var written = false;
        var locked = true;
        try
        {
            // The directory grows before the bucket is rewritten, and every page the rewrite writes is pinned first: after the rewrite starts, nothing under
            // the lock may fault. Growing the directory early is harmless — it only adds capacity no bucket points at yet.
            EnsureDirectoryCapacity(newBucketId, ref accessor, changeSet, underLatch: true);
            reservation.Pin(0, ref accessor);
            reservation.Pin(GetDirectoryChunkId(newBucketId >> PagedHashMapDirectory.Shift, ref accessor), ref accessor);

            int entrySize = sizeof(TKey) + _valueSize;
            // StackEntryThreshold bounds the stackalloc fast path: at 72 entries × typical 12-16 B/entry × 2 buffers = ~2 KB. Safe
            // on every call stack we'd realistically see. Chains beyond this spill to native memory freed in the finally — never to a
            // pinned managed array: pointers here address only stack or engine-owned native memory (CLAUDE.md, Unsafe Code).
            const int stackEntryThreshold = 72;

            byte* nativeKeep = null;
            byte* nativeMove = null;
            try
            {
                byte* keepBuf;
                byte* moveBuf;
                int bufCapacity;   // entries per buffer — determines the keys/values offset split
                if (totalEntries <= stackEntryThreshold)
                {
                    bufCapacity = stackEntryThreshold;
                    byte* k = stackalloc byte[stackEntryThreshold * entrySize];
                    byte* m = stackalloc byte[stackEntryThreshold * entrySize];
                    keepBuf = k;
                    moveBuf = m;
                }
                else
                {
                    bufCapacity = totalEntries;
                    var bufBytes = (nuint)totalEntries * (nuint)entrySize;
                    // native-alloc: transient rehash buffer, freed before this call returns
                    nativeKeep = (byte*)NativeMemory.Alloc(bufBytes);
                    // native-alloc: transient rehash buffer, freed before this call returns
                    nativeMove = (byte*)NativeMemory.Alloc(bufBytes);
                    keepBuf = nativeKeep;
                    moveBuf = nativeMove;
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

                    Interlocked.Add(ref _splitEntriesRehashed, keepCount + moveCount);

                    // Rewrite old bucket
                    written = true;
                    RewriteBucket(oldChunkId, keepKeys, keepValues, keepCount, ref accessor, changeSet);

                    // Allocate and write new bucket — caller-accessor overload keeps ACW > 0 on the new chunk's content page from ClearChunk through
                    // WriteBucket's header/keys/values writes (#301). IXW-07: reserved before the lock, its page pinned.
                    int newChunkId = ChunkReservation<TStore>.AllocateUnderLatch(Segment, changeSet, ref accessor);
                    WriteBucket(newChunkId, moveKeys, moveValues, moveCount, ref accessor, changeSet);

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
                    locked = false;
                }
                finally
                {
                    if (rentedOverflowIds != null) ArrayPool<int>.Shared.Return(rentedOverflowIds);
                }
            }
            finally
            {
                NativeMemory.Free(nativeKeep);   // Free(null) is a no-op: the stack path allocated nothing
                NativeMemory.Free(nativeMove);
            }
        }
        catch
        {
            if (locked)
            {
                ReleaseAfterFault(oldChunkId, written, ref accessor);
            }

            throw;
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
            // losing the writes below. IXW-07: reserved by the split before it took the bucket lock.
            int overflowChunkId = ChunkReservation<TStore>.AllocateUnderLatch(Segment, changeSet, ref accessor);

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

        // Per-bucket optimistic snapshot buffers, addressed by pointer because action.Process takes the value as a byte*. So they live on the
        // stack or in native memory — never in a managed array (CLAUDE.md, Unsafe Code). This buffer used to be a pinned-heap array reached only
        // through its pointer; pinned stops the GC moving an array, not freeing it, and a gen2 collection during the scan (action.Process is where
        // callers allocate) freed it while the copies below kept writing into it — the SWG Tatooine x64 crash, "Internal CLR error" in the next
        // scan's pinned allocation.
        //
        // Two chunks' worth of entries is under ~1.2 KB for every value size RecommendedStride accepts, so the stack takes the common case. A
        // bucket whose overflow chain outgrows it moves the scan to a native block, freed in the finally however the scan ends.
        int bufCap = _bucketCapacity * 2;
        TKey* keyBuf = stackalloc TKey[bufCap];
        byte* valBuf = stackalloc byte[bufCap * _valueSize];
        byte* spilled = null;

        try
        {
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
                            // Allocate before freeing: if Alloc throws, spilled still holds a live block for the finally, never a freed one.
                            // native-alloc: doubling growth buffer: Realloc grows in place, where a resource-tree block would be disposed and re-parented on every doubling
                            var grown = (byte*)NativeMemory.Alloc((nuint)bufCap * (nuint)(sizeof(TKey) + _valueSize));
                            NativeMemory.Free(spilled);
                            spilled = grown;
                            keyBuf = (TKey*)spilled;
                            valBuf = spilled + (nint)bufCap * sizeof(TKey);
                            retry = true;   // restart the bucket with the larger buffer
                            break;
                        }

                        TKey* keys = KeysPtr(addr);
                        for (int i = 0; i < count; i++)
                        {
                            keyBuf[n] = keys[i];
                            Unsafe.CopyBlock(valBuf + n * _valueSize, ValueAt(addr, i), (uint)_valueSize);
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
                    if (!HeadLatch(headId, ref accessor).ValidateVersion(version) || PackedMeta != packed)
                    {
                        continue;
                    }

                    for (int i = 0; i < n; i++)
                    {
                        if (!action.Process(keyBuf[i], valBuf + i * _valueSize))
                        {
                            return visited;
                        }
                        visited++;
                    }

                    break;   // bucket complete
                }
            }
        }
        finally
        {
            NativeMemory.Free(spilled);   // Free(null) is a no-op: most scans never spill
        }

        return visited;
    }

    /// <summary>
    /// Visits every entry it can reach in a map no other thread is using and whose pages may be damaged: the open's snapshots of a persisted map it is about
    /// to replace (<c>DatabaseEngine.SnapshotEnabledBits</c>). Unreachable entries are skipped, never waited for.
    /// </summary>
    /// <remarks>
    /// <see cref="ForEachEntry{TAction}"/> retries any inconsistent read, because under its contract the cause is a concurrent writer that will finish. On a
    /// page torn on disk the inconsistency is permanent — a lock bit set in a bucket's version word, an out-of-range chunk id — and it retried forever: a crash
    /// reopen hung in the EntityMap rebuild. Here nothing writes, so nothing is retried: every chunk id is range-checked before it is read, directory ones
    /// included (<see cref="PagedHashMapBase{TStore}.GetBucketChunkIdChecked"/>), and a chain is cut at the segment's capacity (a cycle). What a damaged page
    /// yields is garbage keys, which the callers' lookups by authoritative key do not match.
    /// </remarks>
    /// <returns>Number of entries visited (handed to <paramref name="action"/>).</returns>
    internal int ForEachEntryQuiescent<TAction>(ref ChunkAccessor<TStore> accessor, ref TAction action) where TAction : struct, IEntryAction<TKey>
    {
        var visited = 0;
        var (_, _, bucketCount) = ReadMeta();
        var chunkCapacity = Segment.ChunkCapacity;
        for (var b = 0; b < bucketCount; b++)
        {
            // -1 when the directory leading to the bucket is damaged: the bucket's entries are lost
            var chunkId = GetBucketChunkIdChecked(b, ref accessor);
            for (var walk = 0; (uint)chunkId < (uint)chunkCapacity && walk <= chunkCapacity; walk++)
            {
                var addr = accessor.GetChunkAddress(chunkId);
                ref readonly var header = ref GetHeader(addr);
                var count = Math.Min((int)header.EntryCount, _bucketCapacity);
                var nextId = header.OverflowChunkId;
                var keys = KeysPtr(addr);
                for (var i = 0; i < count; i++)
                {
                    if (!action.Process(keys[i], ValueAt(addr, i)))
                    {
                        return visited;
                    }

                    visited++;
                }

                chunkId = nextId;
            }
        }

        return visited;
    }

    /// <summary>
    /// Side-effect-free predicate for the optimistic in-place aggregation paths (<see cref="CountEntries{TPred}"/> / <see cref="AnyEntry{TPred}"/>).
    /// </summary>
    internal interface IEntryPredicate<in TK> where TK : unmanaged
    {
        /// <summary>
        /// Return true if the entry counts as a match. MUST be pure: it runs on a yet-to-be-validated (possibly torn) optimistic read and may be re-invoked
        /// when the bucket snapshot is rejected, so it must not mutate shared state nor dereference data beyond <paramref name="key"/> / <paramref name="value"/>.
        /// </summary>
        bool Matches(TK key, byte* value);
    }

    /// <summary>
    /// Count live entries matching <paramref name="pred"/>, under the same per-bucket OLC read protocol as <see cref="ForEachEntry{TAction}"/> but WITHOUT the
    /// per-entry snapshot copy: because counting is reversible, each bucket is evaluated in place and simply re-counted if a concurrent writer (insert / overflow
    /// append / split) invalidates the read. The predicate runs on the live, un-copied bytes during the optimistic walk; counts are clamped to
    /// <c>_bucketCapacity</c> and chunk ids range-checked, so a torn read can never index past a chunk — it only yields a bogus per-bucket tally that the
    /// version validation discards before it reaches the running total. Snapshot semantics match ForEachEntry: an entry present for the whole scan is counted
    /// exactly once; an entry concurrently inserted / removed / relocated may or may not be counted.
    /// </summary>
    internal int CountEntries<TPred>(ref ChunkAccessor<TStore> accessor, ref TPred pred) where TPred : struct, IEntryPredicate<TKey>
    {
        int total = 0;
        var (_, _, bucketCount) = ReadMeta();

        for (int b = 0; b < bucketCount; b++)
        {
            while (true)   // OLC retry for this bucket
            {
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

                int bucketMatches = 0;
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

                    TKey* keys = KeysPtr(addr);
                    for (int i = 0; i < count; i++)
                    {
                        if (pred.Matches(keys[i], ValueAt(addr, i)))
                        {
                            bucketMatches++;
                        }
                    }

                    chunkId = nextId;
                }

                if (retry)
                {
                    continue;
                }

                // Validate the bucket stayed quiescent for the whole read: no writer bumped the head version, no split changed the directory. On failure,
                // discard this attempt's tally and re-count the bucket from the head.
                if (!HeadLatch(headId, ref accessor).ValidateVersion(version) || PackedMeta != packed)
                {
                    continue;
                }

                total += bucketMatches;
                break;   // bucket complete
            }
        }

        return total;
    }

    /// <summary>
    /// Existence check: returns true as soon as a VALIDATED bucket contains an entry matching <paramref name="pred"/>. Same optimistic in-place / no-copy
    /// protocol as <see cref="CountEntries{TPred}"/>; a candidate seen on a torn read is only trusted once the bucket version validates, so a false positive can
    /// never escape. Short-circuits the walk on the first candidate and the scan on the first validated match.
    /// </summary>
    internal bool AnyEntry<TPred>(ref ChunkAccessor<TStore> accessor, ref TPred pred) where TPred : struct, IEntryPredicate<TKey>
    {
        var (_, _, bucketCount) = ReadMeta();

        for (int b = 0; b < bucketCount; b++)
        {
            while (true)   // OLC retry for this bucket
            {
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

                bool candidate = false;
                bool retry = false;
                int chunkId = headId;
                for (int walk = 0; chunkId >= 0 && !candidate; walk++)
                {
                    if ((uint)chunkId >= (uint)chunkCapacity || walk > chunkCapacity)
                    {
                        retry = true;
                        break;
                    }

                    byte* addr = accessor.GetChunkAddress(chunkId);
                    ref readonly var header = ref GetHeader(addr);
                    int rawCount = header.EntryCount;
                    int count = rawCount <= _bucketCapacity ? rawCount : _bucketCapacity;
                    int nextId = header.OverflowChunkId;

                    TKey* keys = KeysPtr(addr);
                    for (int i = 0; i < count; i++)
                    {
                        if (pred.Matches(keys[i], ValueAt(addr, i)))
                        {
                            candidate = true;
                            break;
                        }
                    }

                    chunkId = nextId;
                }

                if (retry)
                {
                    continue;
                }

                // Trust the candidate only once the bucket snapshot validates.
                if (!HeadLatch(headId, ref accessor).ValidateVersion(version) || PackedMeta != packed)
                {
                    continue;
                }

                if (candidate)
                {
                    return true;
                }
                break;   // bucket had no match
            }
        }

        return false;
    }

}
