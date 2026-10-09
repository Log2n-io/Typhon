// unset

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
// ReSharper disable InconsistentNaming

namespace Typhon.Engine.Internals;

/// <summary>
/// Page-backed hash map with unmanaged key and value types. O(1) average-case lookup, insert, delete.
/// Bucket data is accessed via pointer arithmetic into raw chunk memory:
/// [Header 12B] [Key₀..Key_{cap-1}] [Val₀..Val_{cap-1}].
/// <para>
/// Hash function is selected at JIT time based on <c>sizeof(TKey)</c>:
/// 4 bytes → Wang/Jenkins (~3-4 cycles), 8 bytes → xxHash32 (~8-10 cycles), other sizes → xxHash32 over raw bytes.
/// </para>
/// </summary>
unsafe class PagedHashMap<TKey, TValue, TStore> : PagedHashMapBase<TStore> where TKey : unmanaged, IEquatable<TKey> where TValue : unmanaged where TStore : struct, IPageStore
{
    // ═══════════════════════════════════════════════════════════════════════
    // Layout fields (computed once at construction)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Number of entries a single bucket chunk can hold.</summary>
    private readonly int _bucketCapacity;

    /// <summary>Byte offset from chunk start to the keys array.</summary>
    private readonly int _keysOffset;

    /// <summary>Byte offset from chunk start to the values array.</summary>
    private readonly int _valuesOffset;

    /// <summary>VSBS for multi-value storage. Null when <see cref="PagedHashMapBase{TStore}._allowMultiple"/> is false.</summary>
    private readonly VariableSizedBufferSegment<TValue, TStore> _vsbs;
    private readonly ChunkBasedSegment<TStore> _bufferSegment;

    // ═══════════════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════════════

    /// <param name="segment">The map's own segment: its meta, buckets and overflow chunks, and nothing else (#1205).</param>
    /// <param name="n0">The initial bucket count.</param>
    /// <param name="bufferSegment">
    /// Where a multi-value map keeps its value buffers, or <c>null</c> for a single-value map. A segment of their own: a bucket is addressed by its position
    /// in the map's segment, so a buffer chunk there would sit where a split must put a bucket, and could never be moved out of the way.
    /// </param>
    private PagedHashMap(ChunkBasedSegment<TStore> segment, int n0, ChunkBasedSegment<TStore> bufferSegment) : base(segment, n0, bufferSegment != null)
    {
        _bucketCapacity = (segment.Stride - sizeof(PagedHashMapBucketHeader)) / (sizeof(TKey) + sizeof(TValue));
        Debug.Assert(_bucketCapacity >= 1, $"Stride {segment.Stride} too small for entry size {sizeof(TKey) + sizeof(TValue)}");

        _keysOffset = sizeof(PagedHashMapBucketHeader);
        _valuesOffset = sizeof(PagedHashMapBucketHeader) + _bucketCapacity * sizeof(TKey);

        if (bufferSegment != null)
        {
            _bufferSegment = bufferSegment;
            _vsbs = new VariableSizedBufferSegment<TValue, TStore>(bufferSegment);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    public override int BucketCapacity => _bucketCapacity;

    // ═══════════════════════════════════════════════════════════════════════
    // Static helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the smallest supported stride (256 or 512) that yields at least <paramref name="minCapacity"/> entries per bucket. Throws if no supported
    /// stride is large enough.
    /// </summary>
    public static int RecommendedStride(int minCapacity = 8)
    {
        int entrySize = sizeof(TKey) + sizeof(TValue);
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
    private ref PagedHashMapBucketHeader GetHeader(byte* chunkAddr) => ref Unsafe.AsRef<PagedHashMapBucketHeader>(chunkAddr);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TKey* KeysPtr(byte* chunkAddr) => (TKey*)(chunkAddr + _keysOffset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TValue* ValuesPtr(byte* chunkAddr) => (TValue*)(chunkAddr + _valuesOffset);

    // ═══════════════════════════════════════════════════════════════════════
    // Hash function — JIT-specialized by sizeof(TKey)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compute the hash of a key. JIT eliminates dead branches based on <c>sizeof(TKey)</c>:
    /// 4 bytes → Wang/Jenkins, 8 bytes → xxHash32 (8-byte variant), other → xxHash32 (generic bytes).
    /// </summary>
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

    /// <summary>Wang/Jenkins integer hash — deterministic, excellent distribution, ~3-4 cycles.</summary>
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

    /// <summary>Inlined xxHash32 over 8 bytes — deterministic, excellent distribution, ~8-10 cycles.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static uint XxHash32_8Bytes(long key)
    {
        const uint Prime2 = 2246822519u;
        const uint Prime3 = 3266489917u;
        const uint Prime4 = 668265263u;
        const uint Prime5 = 374761393u;

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

    /// <summary>xxHash32 over arbitrary byte length — fallback for key sizes other than 4 or 8.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static uint XxHash32_Bytes(byte* input, int len)
    {
        const uint Prime1 = 2654435761u;
        const uint Prime2 = 2246822519u;
        const uint Prime3 = 3266489917u;
        const uint Prime4 = 668265263u;
        const uint Prime5 = 374761393u;

        uint h = Prime5 + (uint)len;
        byte* p = input;
        byte* end = input + len;

        // Process 4-byte blocks
        while (p + 4 <= end)
        {
            h += *(uint*)p * Prime3;
            h = ((h << 17) | (h >> 15)) * Prime4;
            p += 4;
        }

        // Process remaining bytes
        while (p < end)
        {
            h += *p * Prime5;
            h = ((h << 11) | (h >> 21)) * Prime1;
            p++;
        }

        // Avalanche
        h ^= h >> 15;
        h *= Prime2;
        h ^= h >> 13;
        h *= Prime3;
        h ^= h >> 16;
        return h;
    }

    /// <summary>Test-accessible wrapper for <see cref="ComputeHash"/>.</summary>
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
        header.OverflowChunkId = -1;    // no overflow
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Read path
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Linear scan of the key array across a bucket chain (primary + overflow).
    /// Returns true if <paramref name="key"/> is found, with <paramref name="value"/> set.
    /// </summary>
    private bool ScanChain(int startChunkId, TKey key, out TValue value, ref ChunkAccessor<TStore> accessor)
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
                    value = ValuesPtr(addr)[i];
                    return true;
                }
            }

            chunkId = header.OverflowChunkId;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Look up a key using the OLC read protocol.
    /// Lock-free: reads version, scans chain, validates version. Retries on contention or split.
    /// </summary>
    public bool TryGet(TKey key, out TValue value, ref ChunkAccessor<TStore> accessor)
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = ReadPackedMeta();
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

            bool found = ScanChain(chunkId, key, out value, ref accessor);

            if (!latch.ValidateVersion(version))
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            if (!found && ReadPackedMeta() != packed)
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            return found;
        }
    }

    /// <summary>
    /// Look up all values for a key in an AllowMultiple hash map. Returns a VSBS accessor for iterating the buffer. Returns default (empty) if key not found.
    /// Caller must dispose the returned accessor (ref struct, use <c>using</c>).
    /// </summary>
    public VariableSizedBufferAccessor<TValue, TStore> TryGetMultiple(TKey key, ref ChunkAccessor<TStore> accessor)
    {
        Debug.Assert(_allowMultiple, "TryGetMultiple requires AllowMultiple");
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = ReadPackedMeta();
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

            bool found = ScanChain(chunkId, key, out TValue value, ref accessor);

            if (!latch.ValidateVersion(version))
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            if (!found && ReadPackedMeta() != packed)
            {
                Interlocked.Increment(ref _olcRestarts);
                continue;
            }

            if (!found)
            {
                return default;
            }

            int bufferId = Unsafe.As<TValue, int>(ref value);
            return _vsbs.GetReadOnlyAccessor(bufferId);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Write helpers (private)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Append an entry to the bucket chain. Walks from <paramref name="startChunkId"/> looking for space.
    /// Allocates a new overflow chunk if the chain is full.
    /// </summary>
    private void AppendEntry(int startChunkId, TKey key, TValue value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
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
                ValuesPtr(addr)[idx] = value;
                header.EntryCount = (byte)(idx + 1);
                return;
            }

            if (header.OverflowChunkId != -1)
            {
                chunkId = header.OverflowChunkId;
                continue;
            }

            // Allocate overflow — re-fetch current chunk after (AllocateChunk may remap segment)
            int overflowChunkId = Segment.AllocateChunk(true, changeSet);
            addr = accessor.GetChunkAddress(chunkId, true);
            GetHeader(addr).OverflowChunkId = overflowChunkId;

            byte* ovAddr = accessor.GetChunkAddress(overflowChunkId, true);
            ref var ovHeader = ref GetHeader(ovAddr);
            TagOverflowOwner(ref ovHeader, startChunkId - 1);   // not independently latched; startChunkId is the primary, bucket + 1
            ovHeader.EntryCount = 1;
            ovHeader.Flags = 0;
            ovHeader.Reserved = 0;
            ovHeader.OverflowChunkId = -1;
            KeysPtr(ovAddr)[0] = key;
            ValuesPtr(ovAddr)[0] = value;
            return;
        }
    }

    /// <summary>
    /// Remove a key from the bucket chain using swap-with-last within the same chunk.
    /// Frees empty overflow chunks. Returns true if key was found and removed.
    /// </summary>
    private bool RemoveFromChain(int startChunkId, TKey key, out TValue value, ref ChunkAccessor<TStore> accessor)
    {
        int chunkId = startChunkId;
        int prevChunkId = -1;

        while (chunkId != -1)
        {
            byte* addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);
            TKey* keys = KeysPtr(addr);
            TValue* values = ValuesPtr(addr);
            int count = header.EntryCount;

            for (int i = 0; i < count; i++)
            {
                if (keys[i].Equals(key))
                {
                    value = values[i];

                    // Swap with last entry in this chunk (no holes)
                    int lastIdx = count - 1;
                    if (i != lastIdx)
                    {
                        keys[i] = keys[lastIdx];
                        values[i] = values[lastIdx];
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

        value = default;
        return false;
    }

    /// <summary>
    /// Update the value of an existing key in the bucket chain. Returns true if found and updated.
    /// </summary>
    private bool UpdateInChain(int startChunkId, TKey key, TValue newValue, ref ChunkAccessor<TStore> accessor)
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
                    ValuesPtr(addr)[i] = newValue;
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
    /// Insert a key-value pair. Returns true if inserted, false if key already exists (duplicate rejected).
    /// Uses OLC write protocol: acquire bucket latch -> verify meta -> scan for duplicate -> append -> unlock.
    /// </summary>
    public bool Insert(TKey key, TValue value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        TrySplitIfNeeded(ref accessor, changeSet);   // before the write takes effect (see RawValuePagedHashMap.AppendUnderBucketLock)
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = ReadPackedMeta();
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

            if (ReadPackedMeta() != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            // Check for duplicate
            if (ScanChain(chunkId, key, out TValue existingValue, ref accessor))
            {
                if (!_allowMultiple)
                {
                    latch.AbortWriteLock();
                    return false;
                }

                // AllowMultiple: append value to existing VSBS buffer
                int bufferId = Unsafe.As<TValue, int>(ref existingValue);
                var buffers = _bufferSegment.CreateChunkAccessor(changeSet);
                try
                {
                    _vsbs.AddElement(bufferId, value, ref buffers);
                }
                finally
                {
                    buffers.Dispose();
                }

                // Re-fetch primary for unlock after potential VSBS allocation
                byte* unlockAddr2 = accessor.GetChunkAddress(chunkId, true);
                new OlcLatch(ref GetHeader(unlockAddr2).OlcVersion).WriteUnlock();
                return true;
            }

            if (_allowMultiple)
            {
                // Key not found + AllowMultiple: create new buffer, store buffer ID
                int bufferId;
                var buffers = _bufferSegment.CreateChunkAccessor(changeSet);
                try
                {
                    bufferId = _vsbs.AllocateBuffer(ref buffers);
                    _vsbs.AddElement(bufferId, value, ref buffers);
                }
                finally
                {
                    buffers.Dispose();
                }

                TValue bufferIdAsValue = Unsafe.As<int, TValue>(ref bufferId);
                AppendEntry(chunkId, key, bufferIdAsValue, ref accessor, changeSet);
            }
            else
            {
                // AppendEntry may allocate overflow — invalidates refs
                AppendEntry(chunkId, key, value, ref accessor, changeSet);
            }

            Interlocked.Increment(ref _entryCount);

            // Re-fetch primary for unlock after potential allocation
            byte* unlockAddr = accessor.GetChunkAddress(chunkId, true);
            new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();
            return true;
        }
    }

    /// <summary>
    /// Insert or update a key-value pair. Returns true if inserted (new key), false if updated (existing key).
    /// </summary>
    public bool Upsert(TKey key, TValue value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        TrySplitIfNeeded(ref accessor, changeSet);   // before the write takes effect
        Debug.Assert(!_allowMultiple, "Upsert is not supported with AllowMultiple");
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = ReadPackedMeta();
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

            if (ReadPackedMeta() != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            // Try update in place (no allocation, latch stays valid)
            if (UpdateInChain(chunkId, key, value, ref accessor))
            {
                latch.WriteUnlock();
                return false;
            }

            // Not found — insert (may allocate overflow)
            AppendEntry(chunkId, key, value, ref accessor, changeSet);
            Interlocked.Increment(ref _entryCount);

            byte* unlockAddr = accessor.GetChunkAddress(chunkId, true);
            new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();
            return true;
        }
    }

    /// <summary>
    /// Remove a key. Returns true if found and removed, with the removed value in <paramref name="value"/>.
    /// </summary>
    public bool Remove(TKey key, out TValue value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = ReadPackedMeta();
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

            if (ReadPackedMeta() != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            if (RemoveFromChain(chunkId, key, out value, ref accessor))
            {
                Interlocked.Decrement(ref _entryCount);
                if (_allowMultiple)
                {
                    int bufferId = Unsafe.As<TValue, int>(ref value);
                    DeleteBuffer(bufferId, changeSet);
                }
                // Re-fetch primary for unlock after potential VSBS operations
                byte* unlockAddr = accessor.GetChunkAddress(chunkId, true);
                new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();
                return true;
            }

            // Not found — abort lock (no modification, no version bump)
            latch.AbortWriteLock();

            // Re-check meta: split may have moved key to a different bucket
            if (ReadPackedMeta() != packed)
            {
                continue;
            }

            return false;
        }
    }

    /// <summary>
    /// Remove one specific value from a key's VSBS buffer. If the buffer becomes empty,
    /// the key is removed entirely from the hash map. AllowMultiple only.
    /// </summary>
    public bool RemoveValue(TKey key, TValue valueToRemove, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        Debug.Assert(_allowMultiple, "RemoveValue requires AllowMultiple");
        uint hash = ComputeHash(key);

        while (true)
        {
            long packed = ReadPackedMeta();
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

            if (ReadPackedMeta() != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            if (!ScanChain(chunkId, key, out TValue existingValue, ref accessor))
            {
                latch.AbortWriteLock();
                if (ReadPackedMeta() != packed)
                {
                    continue;
                }
                return false;
            }

            int bufferId = Unsafe.As<TValue, int>(ref existingValue);

            // Walk buffer's stored chunk chain to find and remove the value
            int remaining = -1;
            int walkChunkId = bufferId;
            var buffers = _bufferSegment.CreateChunkAccessor(changeSet);
            try
            {
                while (walkChunkId != 0)
                {
                    remaining = _vsbs.DeleteElement(bufferId, walkChunkId, valueToRemove, ref buffers);
                    if (remaining != -1)
                    {
                        break;
                    }
                    // Value not in this chunk — advance to next via chunk header
                    byte* walkAddr = buffers.GetChunkAddress(walkChunkId);
                    walkChunkId = Unsafe.AsRef<VariableSizedBufferChunkHeader>(walkAddr).NextChunkId;
                }
            }
            finally
            {
                buffers.Dispose();
            }

            if (remaining == -1)
            {
                // Value not found in any chunk — abort (no modification, no version bump)
                latch.AbortWriteLock();
                return false;
            }

            if (remaining == 0)
            {
                // Buffer empty — remove key entirely from the hash map
                RemoveFromChain(chunkId, key, out _, ref accessor);
                Interlocked.Decrement(ref _entryCount);
                DeleteBuffer(bufferId, changeSet);
            }

            // Re-fetch primary for unlock after potential operations
            byte* unlockAddr = accessor.GetChunkAddress(chunkId, true);
            new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Split
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Execute a split: redistribute entries from bucket <c>next</c> into old and new buckets
    /// using the finer modulus. Critical ordering: meta update BEFORE unlock.
    /// </summary>
    protected override bool ExecuteSplit(ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        var (level, next, bucketCount) = ReadMeta();
        if (bucketCount >= BucketCap)
        {
            return false;
        }

        int mod = N0 << level;
        int newMod = mod << 1;
        int oldBucketId = next;
        int newBucketId = next + mod;

        // The new bucket's chunk is its position (#1205): claimed before the old bucket is locked, or the split waits for a later insert.
        var reservation = ChunkReservation<TStore>.Current;
        reservation.Begin(Segment);
        try
        {
            if (!ClaimBucketChunk(newBucketId, ref accessor, changeSet, reservation))
            {
                return false;
            }
        }
        finally
        {
            reservation.End();
        }

        int newChunkId = BucketChunkId(newBucketId);

        // Acquire write lock on old bucket (spin — must succeed)
        int oldChunkId = GetBucketChunkId(oldBucketId, ref accessor);
        byte* oldAddr = accessor.GetChunkAddress(oldChunkId, true);
        SpinUntilWriteLock(ref GetHeader(oldAddr).OlcVersion);

        // First pass: count total entries and overflow chunks for exact stackalloc sizing
        int totalEntries = 0;
        int overflowChunkCount = 0;
        int walkId = oldChunkId;
        while (walkId != -1)
        {
            byte* wAddr = accessor.GetChunkAddress(walkId);
            ref readonly var wHeader = ref GetHeader(wAddr);
            totalEntries += wHeader.EntryCount;
            if (walkId != oldChunkId)
            {
                overflowChunkCount++;
            }
            walkId = wHeader.OverflowChunkId;
        }

        // Classify entries: keep in old bucket vs move to new bucket
        // Use byte-level stackalloc and cast to TKey*/TValue* (generics can't appear in stackalloc directly)
        int keyBufSize = totalEntries * sizeof(TKey);
        int valBufSize = totalEntries * sizeof(TValue);
        byte* keepBuf = stackalloc byte[keyBufSize + valBufSize];
        byte* moveBuf = stackalloc byte[keyBufSize + valBufSize];
        TKey* keepKeys = (TKey*)keepBuf;
        TValue* keepValues = (TValue*)(keepBuf + keyBufSize);
        TKey* moveKeys = (TKey*)moveBuf;
        TValue* moveValues = (TValue*)(moveBuf + keyBufSize);
        int keepCount = 0, moveCount = 0;

        // Track overflow chunk IDs for freeing after redistribution
        Span<int> overflowIds = stackalloc int[Math.Max(overflowChunkCount, 1)];
        int overflowCount = 0;

        walkId = oldChunkId;
        while (walkId != -1)
        {
            byte* wAddr = accessor.GetChunkAddress(walkId);
            ref readonly var wHeader = ref GetHeader(wAddr);
            TKey* wKeys = KeysPtr(wAddr);
            TValue* wValues = ValuesPtr(wAddr);
            int count = wHeader.EntryCount;
            int nextId = wHeader.OverflowChunkId;

            for (int i = 0; i < count; i++)
            {
                TKey key = wKeys[i];
                TValue val = wValues[i];
                uint hash = ComputeHash(key);
                int targetBucket = (int)(hash & (uint)(newMod - 1));

                if (targetBucket == oldBucketId)
                {
                    keepKeys[keepCount] = key;
                    keepValues[keepCount] = val;
                    keepCount++;
                }
                else
                {
                    moveKeys[moveCount] = key;
                    moveValues[moveCount] = val;
                    moveCount++;
                }
            }

            if (walkId != oldChunkId)
            {
                overflowIds[overflowCount++] = walkId;
            }

            walkId = nextId;
        }

        // Rewrite old bucket with keep entries
        RewriteBucket(oldChunkId, keepKeys, keepValues, keepCount, ref accessor, changeSet);

        // Write the new bucket at the chunk its position names, claimed above
        WriteBucket(newChunkId, moveKeys, moveValues, moveCount, ref accessor, changeSet);

        // Free old overflow chunks
        for (int i = 0; i < overflowCount; i++)
        {
            Segment.FreeChunk(overflowIds[i]);
        }

        // Update meta BEFORE unlock — readers unblocked by unlock must see new bucket layout
        PublishBucketCount(bucketCount + 1);
        PersistBucketCount(ref accessor);

        // Unlock old bucket (re-fetch after allocations)
        byte* unlockAddr = accessor.GetChunkAddress(oldChunkId, true);
        new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();
        return true;
    }

    /// <summary>
    /// Rewrite primary bucket with the given entries. Clears overflow link.
    /// Does NOT touch OlcVersion (still locked by caller).
    /// Creates new overflow if entries exceed capacity.
    /// </summary>
    private void RewriteBucket(int chunkId, TKey* keys, TValue* values, int entryCount, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        byte* addr = accessor.GetChunkAddress(chunkId, true);
        ref var header = ref GetHeader(addr);
        int count = Math.Min(entryCount, _bucketCapacity);
        header.EntryCount = (byte)count;
        header.OverflowChunkId = -1;

        TKey* dstKeys = KeysPtr(addr);
        TValue* dstValues = ValuesPtr(addr);
        for (int i = 0; i < count; i++)
        {
            dstKeys[i] = keys[i];
            dstValues[i] = values[i];
        }

        if (entryCount > count)
        {
            WriteOverflowChain(chunkId, keys + count, values + count, entryCount - count, ref accessor, changeSet);
        }
    }

    /// <summary>
    /// Initialize a new bucket chunk with the given entries. Sets OlcVersion=4 (version=1, unlocked).
    /// Creates overflow if entries exceed capacity.
    /// </summary>
    private void WriteBucket(int chunkId, TKey* keys, TValue* values, int entryCount, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
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
        TValue* dstValues = ValuesPtr(addr);
        for (int i = 0; i < count; i++)
        {
            dstKeys[i] = keys[i];
            dstValues[i] = values[i];
        }

        if (entryCount > count)
        {
            WriteOverflowChain(chunkId, keys + count, values + count, entryCount - count, ref accessor, changeSet);
        }
    }

    /// <summary>
    /// Write remaining entries as a chain of overflow chunks linked from <paramref name="parentChunkId"/>.
    /// </summary>
    private void WriteOverflowChain(int parentChunkId, TKey* keys, TValue* values, int entryCount, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        if (entryCount == 0)
        {
            return;
        }

        int prevChunkId = parentChunkId;
        int offset = 0;
        var ownerBucket = parentChunkId - 1;   // the chain starts at the bucket's primary chunk, bucket + 1

        while (offset < entryCount)
        {
            int overflowChunkId = Segment.AllocateChunk(true, changeSet);

            byte* prevAddr = accessor.GetChunkAddress(prevChunkId, true);
            GetHeader(prevAddr).OverflowChunkId = overflowChunkId;

            byte* ovAddr = accessor.GetChunkAddress(overflowChunkId, true);
            ref var ovHeader = ref GetHeader(ovAddr);
            int writeCount = Math.Min(entryCount - offset, _bucketCapacity);
            TagOverflowOwner(ref ovHeader, ownerBucket);   // not independently latched
            ovHeader.EntryCount = (byte)writeCount;
            ovHeader.Flags = 0;
            ovHeader.Reserved = 0;
            ovHeader.OverflowChunkId = -1;

            TKey* dstKeys = KeysPtr(ovAddr);
            TValue* dstValues = ValuesPtr(ovAddr);
            for (int i = 0; i < writeCount; i++)
            {
                dstKeys[i] = keys[offset + i];
                dstValues[i] = values[offset + i];
            }

            prevChunkId = overflowChunkId;
            offset += writeCount;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Rebuild support
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Insert a key-value pair without OLC and without duplicate check. For single-threaded rebuild/recovery contexts only. Still triggers splits.
    /// </summary>
    private void InsertDuringRebuild(TKey key, TValue value, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        TrySplitIfNeeded(ref accessor, changeSet);
        uint hash = ComputeHash(key);
        var (level, next, _) = UnpackMeta(ReadPackedMeta());
        int bucket = ResolveBucket(hash, level, next, N0);
        int chunkId = GetBucketChunkId(bucket, ref accessor);

        AppendEntry(chunkId, key, value, ref accessor, changeSet);
        _entryCount++;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Enumerator
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get a best-effort enumerator over all entries. For diagnostics and single-threaded rebuild.
    /// Not snapshot-safe — concurrent mutations may cause missed or duplicate entries.
    /// </summary>
    public Enumerator GetEnumerator(ref ChunkAccessor<TStore> accessor) => new(this, ref accessor);

    /// <summary>
    /// Sequential enumerator over all (Key, Value) pairs in the hash map.
    /// Walks buckets 0..bucketCount-1, following overflow chains within each bucket.
    /// </summary>
    public ref struct Enumerator
    {
        private readonly PagedHashMap<TKey, TValue, TStore> _map;
        private ref ChunkAccessor<TStore> _accessor;
        private int _bucketIndex;
        private readonly int _bucketCount;
        private int _currentChunkId;
        private int _entryIndex;

        public (TKey Key, TValue Value) Current { get; private set; }

        internal Enumerator(PagedHashMap<TKey, TValue, TStore> map, ref ChunkAccessor<TStore> accessor)
        {
            _map = map;
            _accessor = ref accessor;
            _bucketCount = map.ReadMeta().BucketCount;
            _bucketIndex = -1;
            _currentChunkId = -1;
            _entryIndex = 0;
            Current = default;
        }

        public bool MoveNext()
        {
            while (true)
            {
                if (_currentChunkId != -1)
                {
                    byte* addr = _accessor.GetChunkAddress(_currentChunkId);
                    ref readonly var header = ref Unsafe.AsRef<PagedHashMapBucketHeader>(addr);

                    if (_entryIndex < header.EntryCount)
                    {
                        TKey* keys = (TKey*)(addr + _map._keysOffset);
                        TValue* values = (TValue*)(addr + _map._valuesOffset);
                        Current = (keys[_entryIndex], values[_entryIndex]);
                        _entryIndex++;
                        return true;
                    }

                    if (header.OverflowChunkId != -1)
                    {
                        _currentChunkId = header.OverflowChunkId;
                        _entryIndex = 0;
                        continue;
                    }
                }

                _bucketIndex++;
                if (_bucketIndex >= _bucketCount)
                {
                    return false;
                }

                _currentChunkId = _map.GetBucketChunkId(_bucketIndex, ref _accessor);
                _entryIndex = 0;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Concurrent Enumerator
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get a concurrent-safe enumerator over all entries. Uses per-bucket OLC validation and
    /// live <c>bucketCount</c> to handle concurrent writers and splits. No torn reads are yielded.
    /// Duplicates are possible when splits occur during enumeration — callers using <c>HashSet</c>
    /// absorb them naturally. Caller must call <see cref="ConcurrentEnumerator.Dispose"/>.
    /// </summary>
    public ConcurrentEnumerator GetConcurrentEnumerator(ref ChunkAccessor<TStore> accessor) => new(this, ref accessor);

    /// <summary>
    /// Concurrent-safe enumerator: per-bucket OLC read protocol with collect-then-yield.
    /// Entries are collected into a temp buffer under OLC, validated, then yielded one by one.
    /// Uses live <c>bucketCount</c> so newly split buckets at the end are visited.
    /// </summary>
    public ref struct ConcurrentEnumerator : IDisposable
    {
        private readonly PagedHashMap<TKey, TValue, TStore> _map;
        private ref ChunkAccessor<TStore> _accessor;
        private int _bucketIndex;
        private readonly int _keysOffset;
        private readonly int _valuesOffset;

        // Collect-then-yield buffer: allocated once, reused per bucket
        private readonly byte* _buffer;     // keys then values, sized for MaxBufferEntries
        private int _collectedCount;
        private int _yieldIndex;

        // Buffer capacity: primary + up to 3 overflow chunks
        private const int MaxBufferEntries = 128;

        public (TKey Key, TValue Value) Current { get; private set; }

        internal ConcurrentEnumerator(PagedHashMap<TKey, TValue, TStore> map, ref ChunkAccessor<TStore> accessor)
        {
            _map = map;
            _accessor = ref accessor;
            _bucketIndex = -1;
            _keysOffset = map._keysOffset;
            _valuesOffset = map._valuesOffset;
            _collectedCount = 0;
            _yieldIndex = 0;
            Current = default;

            int bufferSize = MaxBufferEntries * (sizeof(TKey) + sizeof(TValue));
            // native-alloc: per-enumerator buffer, freed with the enumerator; a tree node per enumeration would churn the resource tree
            _buffer = (byte*)NativeMemory.AllocZeroed((nuint)bufferSize);
        }

        public bool MoveNext()
        {
            while (true)
            {
                // Phase 1: yield from collected buffer
                if (_yieldIndex < _collectedCount)
                {
                    TKey* keys = (TKey*)_buffer;
                    TValue* values = (TValue*)(_buffer + MaxBufferEntries * sizeof(TKey));
                    Current = (keys[_yieldIndex], values[_yieldIndex]);
                    _yieldIndex++;
                    return true;
                }

                // Phase 2: advance to next bucket and collect under OLC
                _bucketIndex++;
                var (_, _, bucketCount) = _map.ReadMeta();
                if (_bucketIndex >= bucketCount)
                {
                    return false;
                }

                if (CollectBucketEntries())
                {
                    _yieldIndex = 0;
                    // Loop back to Phase 1
                }
                // If CollectBucketEntries returned false (OLC fail), it already reset state — retry same bucket
            }
        }

        /// <summary>
        /// Collect all entries from bucket <see cref="_bucketIndex"/> under OLC read protocol.
        /// Returns true if entries were successfully collected and validated.
        /// Returns false if OLC validation failed (bucket index is NOT advanced — caller retries).
        /// </summary>
        private bool CollectBucketEntries()
        {
            int chunkId = _map.GetBucketChunkId(_bucketIndex, ref _accessor);

            while (true)
            {
                byte* primaryAddr = _accessor.GetChunkAddress(chunkId);
                ref readonly var primaryHeader = ref Unsafe.AsRef<PagedHashMapBucketHeader>(primaryAddr);

                var latch = new OlcLatch(ref Unsafe.AsRef(in primaryHeader.OlcVersion));
                int version = latch.ReadVersion();
                if (version == 0)
                {
                    // Bucket is write-locked — spin briefly and retry
                    Thread.SpinWait(1);
                    continue;
                }

                // Walk chain collecting entries into buffer
                TKey* bufKeys = (TKey*)_buffer;
                TValue* bufValues = (TValue*)(_buffer + MaxBufferEntries * sizeof(TKey));
                int count = 0;

                int walkId = chunkId;
                while (walkId != -1 && count < MaxBufferEntries)
                {
                    byte* addr = _accessor.GetChunkAddress(walkId);
                    ref readonly var header = ref Unsafe.AsRef<PagedHashMapBucketHeader>(addr);
                    TKey* keys = (TKey*)(addr + _keysOffset);
                    TValue* values = (TValue*)(addr + _valuesOffset);
                    int entryCount = header.EntryCount;

                    for (int i = 0; i < entryCount && count < MaxBufferEntries; i++)
                    {
                        bufKeys[count] = keys[i];
                        bufValues[count] = values[i];
                        count++;
                    }

                    walkId = header.OverflowChunkId;
                }

                // Validate OLC — if version changed, a concurrent writer modified this bucket
                // Re-fetch primary since accessor cache may have shifted during chain walk
                primaryAddr = _accessor.GetChunkAddress(chunkId);
                latch = new OlcLatch(ref Unsafe.AsRef<PagedHashMapBucketHeader>(primaryAddr).OlcVersion);
                if (!latch.ValidateVersion(version))
                {
                    // OLC fail — retry this bucket
                    continue;
                }

                _collectedCount = count;
                return true;
            }
        }

        public void Dispose()
        {
            if (_buffer != null)
            {
                NativeMemory.Free(_buffer);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Seed an entry directly into a bucket's primary chunk. Test-only: no overflow handling,
    /// asserts bucket is not full.
    /// </summary>
    internal void SeedEntryForTest(TKey key, TValue value, ref ChunkAccessor<TStore> accessor)
    {
        uint hash = ComputeHash(key);
        var (level, next, _) = ReadMeta();
        int bucket = ResolveBucket(hash, level, next, N0);
        int chunkId = GetBucketChunkId(bucket, ref accessor);

        byte* addr = accessor.GetChunkAddress(chunkId, true);
        ref var header = ref GetHeader(addr);
        Debug.Assert(header.EntryCount < _bucketCapacity, "SeedEntryForTest: bucket full, no overflow handling");

        KeysPtr(addr)[header.EntryCount] = key;
        ValuesPtr(addr)[header.EntryCount] = value;
        header.EntryCount++;

        Interlocked.Increment(ref _entryCount);
    }

    /// <summary>Deletes value buffer <paramref name="bufferId"/>, through an accessor on the buffers' own segment.</summary>
    private void DeleteBuffer(int bufferId, ChangeSet changeSet)
    {
        var buffers = _bufferSegment.CreateChunkAccessor(changeSet);
        try
        {
            _vsbs.DeleteBuffer(bufferId, ref buffers);
        }
        finally
        {
            buffers.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Factory methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a new hash map on a fresh segment. A multi-value map passes <paramref name="bufferSegment"/>, a segment of its own for the value buffers.
    /// </summary>
    public static PagedHashMap<TKey, TValue, TStore> Create(ChunkBasedSegment<TStore> segment, int initialBuckets = 64,
        ChunkBasedSegment<TStore> bufferSegment = null, ChangeSet changeSet = null)
    {
        Debug.Assert(initialBuckets > 0 && BitOperations.IsPow2(initialBuckets), "initialBuckets must be a positive power of 2");
        if (bufferSegment == segment)
        {
            throw new ArgumentException("A multi-value map's buffers need a segment of their own: the map's buckets are addressed by position in its segment.",
                nameof(bufferSegment));
        }

        using var guard = EpochGuard.Enter(segment.Store.EpochManager);

        var map = new PagedHashMap<TKey, TValue, TStore>(segment, initialBuckets, bufferSegment);
        map.InitializeCreate(initialBuckets, changeSet);
        return map;
    }

    /// <summary>
    /// Open an existing hash map from a persisted segment. A multi-value map passes the segment its value buffers live in.
    /// </summary>
    public static PagedHashMap<TKey, TValue, TStore> Open(ChunkBasedSegment<TStore> segment, ChunkBasedSegment<TStore> bufferSegment = null)
    {
        using var guard = EpochGuard.Enter(segment.Store.EpochManager);

        int n0;
        bool allowMultiple;
        var accessor = segment.CreateChunkAccessor();
        try
        {
            ref readonly var meta = ref accessor.GetChunkReadOnly<PagedHashMapMeta>(0);
            n0 = meta.N0;
            allowMultiple = (meta.Flags & 1) != 0;
        }
        finally
        {
            accessor.Dispose();
        }

        if (allowMultiple != (bufferSegment != null))
        {
            throw new ArgumentException(allowMultiple
                ? "The map holds multiple values per key: open it with the segment its value buffers live in."
                : "The map holds one value per key: it has no buffer segment.", nameof(bufferSegment));
        }

        var map = new PagedHashMap<TKey, TValue, TStore>(segment, n0, bufferSegment);
        map.InitializeOpen();
        return map;
    }

    /// <summary>
    /// Create a new hash map and bulk-populate it from <paramref name="sourceData"/>.
    /// Single-threaded factory: no OLC, no duplicate check.
    /// </summary>
    public static PagedHashMap<TKey, TValue, TStore> CreateAndPopulate(ChunkBasedSegment<TStore> segment, IEnumerable<(TKey Key, TValue Value)> sourceData, int initialBuckets = 64,
        ChangeSet changeSet = null)
    {
        var map = Create(segment, initialBuckets, changeSet: changeSet);

        using var guard = EpochGuard.Enter(segment.Store.EpochManager);
        var accessor = segment.CreateChunkAccessor(changeSet);
        try
        {
            foreach (var (key, value) in sourceData)
            {
                map.InsertDuringRebuild(key, value, ref accessor, changeSet);
            }

            map.PersistBucketCount(ref accessor);
            map.PersistEntryCount(ref accessor);
        }
        finally
        {
            accessor.Dispose();
        }

        return map;
    }
}
