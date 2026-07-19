// unset

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Abstract base class for hash maps. Provides meta management, directory addressing, bucket resolution, split lock, and factory scaffolding.
/// Concrete class <see cref="PagedHashMap{TKey,TValue,TStore}"/> provides JIT-specialized hash functions via sizeof(TKey) branching.
/// </summary>
internal abstract unsafe class PagedHashMapBase<TStore> where TStore : struct, IPageStore
{
    // ═══════════════════════════════════════════════════════════════════════
    // Fields
    // ═══════════════════════════════════════════════════════════════════════

    private readonly ChunkBasedSegment<TStore> _segment;

    /// <summary>Initial bucket count (power of 2). Immutable after construction.</summary>
    private readonly int _n0;

    /// <summary>Atomic packed meta: Level(8) | Next(24) | BucketCount(32). Use <see cref="ReadMeta"/> to decode.</summary>
    protected long PackedMeta;

    /// <summary>Total entry count. Updated via <see cref="Interlocked"/>.</summary>
    protected long _entryCount;

    /// <summary>CAS spin lock for split serialization: 0=free, 1=held.</summary>
    protected int _splitLock;

    /// <summary>Diagnostic: total splits performed.</summary>
    internal long _splitCount;

    /// <summary>Diagnostic: OLC read restarts due to version mismatch.</summary>
    internal long _olcRestarts;

    /// <summary>Diagnostic: write lock spin contention.</summary>
    internal long _writeLockFailures;

    /// <summary>Maximum load factor before triggering a split.</summary>
    private const double MaxLoadFactor = 0.75;

    /// <summary>Whether this hash map supports multiple values per key via VSBS buffer indirection.</summary>
    protected readonly bool _allowMultiple;

    // ═══════════════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════════════

    protected PagedHashMapBase(ChunkBasedSegment<TStore> segment, int n0, bool allowMultiple = false)
    {
        Debug.Assert(segment != null);
        Debug.Assert(n0 > 0 && BitOperations.IsPow2(n0), "N0 must be a positive power of 2");
        Debug.Assert(segment.Stride >= 64 && BitOperations.IsPow2(segment.Stride), "LinearHash requires stride >= 64 and power of 2");

        _segment = segment;
        _n0 = n0;
        _allowMultiple = allowMultiple;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Initial bucket count (immutable, power of 2).</summary>
    public int N0 => _n0;

    /// <summary>The backing segment.</summary>
    public ChunkBasedSegment<TStore> Segment => _segment;

    /// <summary>Whether this hash map supports multiple values per key via VSBS buffer indirection.</summary>
    public bool AllowMultiple => _allowMultiple;

    /// <summary>Total entries across all buckets.</summary>
    public long EntryCount => _entryCount;

    /// <summary>Current bucket count (decoded from packed meta).</summary>
    public int BucketCount
    {
        get
        {
            var (_, _, count) = ReadMeta();
            return count;
        }
    }

    /// <summary>Load factor: entries / (bucketCount × bucketCapacity).</summary>
    public double LoadFactor
    {
        get
        {
            var (_, _, bucketCount) = ReadMeta();
            long entries = _entryCount;
            return (double)entries / ((long)bucketCount * BucketCapacity);
        }
    }

    /// <summary>Number of entries a single bucket chunk can hold.</summary>
    public abstract int BucketCapacity { get; }

    // ═══════════════════════════════════════════════════════════════════════
    // Static helpers — PackMeta / UnpackMeta / ResolveBucket
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pack level, next, and bucketCount into a single 64-bit value.
    /// Layout: Level(bits 56-63) | Next(bits 32-55) | BucketCount(bits 0-31).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long PackMeta(int level, int next, int bucketCount) => ((long)(level & 0xFF) << 56) | ((long)(next & 0x00FFFFFF) << 32) | (uint)bucketCount;

    /// <summary>
    /// Unpack a 64-bit packed meta into (Level, Next, BucketCount).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (int Level, int Next, int BucketCount) UnpackMeta(long packed)
    {
        int level = (int)((packed >> 56) & 0xFF);
        int next = (int)((packed >> 32) & 0x00FFFFFF);
        int bucketCount = (int)(packed & 0xFFFFFFFF);
        return (level, next, bucketCount);
    }

    /// <summary>
    /// Resolve a hash to a bucket index using bitmask arithmetic (no modulo).
    /// If the bucket has already been split this round (bucket &lt; next), the finer modulus is used.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ResolveBucket(uint hash, int level, int next, int n0)
    {
        int mod = n0 << level;                        // N0 × 2^Level (always power of 2)
        int bucket = (int)(hash & (uint)(mod - 1));   // bitmask: 1 AND instruction

        if (bucket < next)
        {
            // This bucket already split this round — use finer modulus
            bucket = (int)(hash & (uint)((mod << 1) - 1));
        }

        return bucket;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Instance methods — Meta read, directory/bucket addressing
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Read the packed meta. On x64, reads of aligned 64-bit fields are naturally atomic.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected (int Level, int Next, int BucketCount) ReadMeta()
    {
        long packed = PackedMeta;
        return UnpackMeta(packed);
    }

    /// <summary>
    /// Get the directory chunk ID for a given directory index.
    /// Fast path for index &lt; 57 (inline in meta). Slow path walks the overflow dir-index chain.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetDirectoryChunkId(int dirIndex, ref ChunkAccessor<TStore> accessor)
    {
        ref readonly var meta = ref accessor.GetChunkReadOnly<PagedHashMapMeta>(0);

        // Bounds check against the live DirectoryChunkCount — guards against the inline branch returning a zero / stale slot when callers compute a dirIndex
        // past the populated range. Previously the inline path returned meta.DirectoryChunkIds[dirIndex] unconditionally, which means a lookup with dirIndex
        // past DirectoryChunkCount but < MaxInlineDirectoryChunks read zero (or stale data on a re-used chunk) and the bucket-chunk read downstream blew up
        // with an out-of-range chunk-id deep in ChunkBasedSegment.GetChunkLocation. Throwing here makes the violation impossible to confuse with segment
        // corruption.
        if (dirIndex >= meta.DirectoryChunkCount)
        {
            throw new InvalidOperationException(
                $"PagedHashMap.GetDirectoryChunkId: dirIndex={dirIndex} >= DirectoryChunkCount={meta.DirectoryChunkCount}. " +
                $"BucketCount={(int)(meta.PackedMeta & 0xFFFFFFFF)}, N0={meta.N0}. " +
                "This means a bucket lookup landed on a directory chunk that was never allocated — either ResolveBucket returned a bucketId past the live " +
                "bucketCount, or EnsureDirectoryCapacity was skipped before SetBucketChunkId.");
        }

        if (dirIndex < PagedHashMapMeta.MaxInlineDirectoryChunks)
        {
            return meta.DirectoryChunkIds[dirIndex];
        }

        // Overflow path: walk the overflow dir-index chain
        int overflowChunkId = meta.OverflowDirIndexChunkId;
        int remaining = dirIndex - PagedHashMapMeta.MaxInlineDirectoryChunks;

        while (remaining >= OverflowDirIndex.EntriesPerChunk)
        {
            ref readonly var overflow = ref accessor.GetChunkReadOnly<OverflowDirIndex>(overflowChunkId);
            overflowChunkId = overflow.NextOverflowChunkId;
            remaining -= OverflowDirIndex.EntriesPerChunk;
        }

        ref readonly var target = ref accessor.GetChunkReadOnly<OverflowDirIndex>(overflowChunkId);
        return target.DirectoryChunkIds[remaining];
    }

    /// <summary>
    /// Get the chunk ID of the primary bucket for a given bucket index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected int GetBucketChunkId(int bucketId, ref ChunkAccessor<TStore> accessor)
    {
        int dirIndex = bucketId >> PagedHashMapDirectory.Shift;  // bucketId / 64
        int dirSlot = bucketId & 0x3F;                         // bucketId % 64

        int dirChunkId = GetDirectoryChunkId(dirIndex, ref accessor);

        ref readonly var dir = ref accessor.GetChunkReadOnly<PagedHashMapDirectory>(dirChunkId);
        return dir.BucketChunkIds[dirSlot];
    }

    /// <summary>
    /// Set the chunk ID of a bucket in the directory. Used during split to register new buckets.
    /// </summary>
    protected void SetBucketChunkId(int bucketId, int chunkId, ref ChunkAccessor<TStore> accessor)
    {
        int dirIndex = bucketId >> PagedHashMapDirectory.Shift;
        int dirSlot = bucketId & 0x3F;

        int dirChunkId = GetDirectoryChunkId(dirIndex, ref accessor);

        ref var dir = ref accessor.GetChunk<PagedHashMapDirectory>(dirChunkId, true);
        dir.BucketChunkIds[dirSlot] = chunkId;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Split lock
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Try to acquire the split lock (non-blocking). Returns false if another thread is splitting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAcquireSplitLock() => Interlocked.CompareExchange(ref _splitLock, 1, 0) == 0;

    /// <summary>
    /// Release the split lock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReleaseSplitLock() => _splitLock = 0;

    // ═══════════════════════════════════════════════════════════════════════
    // Write support
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Spin-waits until write lock is acquired on the given OLC version field.
    /// Two-phase spin policy matching BTree: 64 tight PAUSE spins, then yield-capped SpinWait.
    /// </summary>
    protected void SpinUntilWriteLock(ref int olcVersion)
    {
        var latch = new OlcLatch(ref olcVersion);
        if (latch.TryWriteLock())
        {
            return;
        }

        // Phase 1: tight PAUSE spin — covers typical latch hold time
        for (int i = 0; i < 64; i++)
        {
            Interlocked.Increment(ref _writeLockFailures);
            Thread.SpinWait(1);
            if (latch.TryWriteLock())
            {
                return;
            }
        }

        // Phase 2: yield-capped SpinWait — sleep1Threshold: -1 avoids 15 ms Windows timer-tick
        SpinWait spin = default;
        do
        {
            Interlocked.Increment(ref _writeLockFailures);
            spin.SpinOnce(-1);
        }
        while (!latch.TryWriteLock());
    }

    /// <summary>
    /// Heuristic check: is the load factor above the split threshold?
    /// Non-atomic read of entry count and meta — harmless: at worst one unnecessary or skipped split.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldSplit()
    {
        var (_, _, bucketCount) = ReadMeta();
        long entries = _entryCount;
        return (double)entries / ((long)bucketCount * BucketCapacity) > MaxLoadFactor;
    }

    /// <summary>
    /// If load factor exceeds threshold, try to acquire split lock and execute a split.
    /// Double-checks after acquiring lock to avoid unnecessary splits.
    /// </summary>
    protected void TrySplitIfNeeded(ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        if (!ShouldSplit())
        {
            return;
        }

        if (!TryAcquireSplitLock())
        {
            return;
        }

        try
        {
            if (!ShouldSplit())
            {
                return;
            }

            ExecuteSplit(ref accessor, changeSet);
            Interlocked.Increment(ref _splitCount);
        }
        finally
        {
            ReleaseSplitLock();
        }
    }

    /// <summary>
    /// Pre-allocate backing storage so that organic splits triggered by subsequent inserts are cheap (no page-level Grow). Does NOT advance the linear
    /// hash state — entry redistribution happens correctly via per-insert <see cref="ExecuteSplit"/>.
    /// </summary>
    public void EnsureCapacity(int totalEntries, ChangeSet changeSet = null)
    {
        int targetBuckets = Math.Max(_n0, (int)((totalEntries / (BucketCapacity * MaxLoadFactor)) + 1));
        // Round up to power-of-2 × n0 boundary (valid linear hash state: bucketCount = n0 * 2^level)
        int rounded = _n0;
        while (rounded < targetBuckets)
        {
            rounded <<= 1;
        }

        var (_, _, bucketCount) = ReadMeta();
        if (bucketCount >= rounded)
        {
            return;
        }

        // Pre-grow the segment so AllocateChunk during organic splits won't trigger expensive page Grow.
        // Estimate: meta(1) + bucket chunks(rounded) + directory chunks(ceil(rounded/64)).
        int totalChunksNeeded = 1 + rounded + ((rounded + 63) >> 6);
        _segment.EnsureCapacity(totalChunksNeeded, changeSet);

        // Pre-expand directory so ExecuteSplit doesn't allocate directory chunks per-split.
        var accessor = _segment.CreateChunkAccessor(changeSet);
        try
        {
            EnsureDirectoryCapacity(rounded - 1, ref accessor, changeSet);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Ensure the directory has enough chunks to address bucket <paramref name="maxBucketId"/>.
    /// Allocates new directory chunks (inline or overflow dir-index) as needed.
    /// </summary>
    protected void EnsureDirectoryCapacity(int maxBucketId, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        int requiredDirChunks = (maxBucketId >> PagedHashMapDirectory.Shift) + 1;

        ref var meta = ref accessor.GetChunk<PagedHashMapMeta>(0, true);
        int currentCount = meta.DirectoryChunkCount;

        if (currentCount >= requiredDirChunks)
        {
            return;
        }

        for (int i = currentCount; i < requiredDirChunks; i++)
        {
            // CRITICAL (#301 cascade fix): caller-accessor overload keeps ACW > 0 on the new dir chunk's content page continuously. The legacy overload's
            // local-accessor-dispose window left the new dir chunk's CONTENT page with ACW=0 but DC=1 — a checkpoint racing in that window snapshotted
            // the cleared (zero-filled) content, fsync'd, dropped DC→0, and any eviction-then-reload before the first SetBucketChunkId would lose subsequent
            // slot writes (the SLOT-ZERO corruption).
            int newDirChunkId = _segment.AllocateChunk(changeSet, ref accessor);
            meta = ref accessor.GetChunk<PagedHashMapMeta>(0, true);

            if (i < PagedHashMapMeta.MaxInlineDirectoryChunks)
            {
                meta.DirectoryChunkIds[i] = newDirChunkId;
                meta.DirectoryChunkCount = (ushort)(i + 1);
            }
            else
            {
                int overflowOffset = i - PagedHashMapMeta.MaxInlineDirectoryChunks;
                int targetIdx = overflowOffset / OverflowDirIndex.EntriesPerChunk;
                int targetSlot = overflowOffset % OverflowDirIndex.EntriesPerChunk;

                if (meta.OverflowDirIndexChunkId == -1)
                {
                    // ORDER MATTERS (#301 race-fix): initialise the new overflow-dir-index chunk's NextOverflowChunkId sentinel BEFORE linking it from the
                    // meta. AllocateChunk(clearContent) zeroes the new chunk, so until we write `-1` its NextOverflowChunkId field is 0 (== meta chunk). If
                    // we linked first and a checkpoint snapshotted in the gap, walks via GetDirectoryChunkId would read `meta → ov → next=0 → meta`, returning
                    // garbage from meta's byte layout as directory chunk ids — which then propagates as `BucketChunkIds[slot] == 0` and corrupts every
                    // subsequent insert that hashes into that bucket. Caller-accessor overload (#301 cascade fix) keeps ACW > 0 on the new chunk's content
                    // page.
                    int ovId = _segment.AllocateChunk(changeSet, ref accessor);

                    ref var ov = ref accessor.GetChunk<OverflowDirIndex>(ovId, true);
                    ov.NextOverflowChunkId = -1;
                    ov.DirectoryChunkIds[targetSlot] = newDirChunkId;

                    // Link from meta LAST so any concurrent snapshot sees a fully-formed overflow chunk.
                    meta = ref accessor.GetChunk<PagedHashMapMeta>(0, true);
                    meta.OverflowDirIndexChunkId = ovId;
                }
                else
                {
                    int ovId = meta.OverflowDirIndexChunkId;
                    for (int j = 0; j < targetIdx; j++)
                    {
                        ref var ovChunk = ref accessor.GetChunk<OverflowDirIndex>(ovId, true);
                        if (ovChunk.NextOverflowChunkId == -1)
                        {
                            // ORDER MATTERS (#301 race-fix): initialise the new overflow-dir-index chunk's sentinel BEFORE linking it from prev. Same hazard
                            // as the first-overflow branch above: a half-formed link (prev → new, new.NextOverflowChunkId == 0) lets a concurrent snapshot
                            // persist a walk-into-meta, producing garbage directory slots. Caller-accessor overload (#301 cascade fix) keeps ACW > 0 on the
                            // new chunk's content page.
                            int newOvId = _segment.AllocateChunk(changeSet, ref accessor);

                            ref var newOv = ref accessor.GetChunk<OverflowDirIndex>(newOvId, true);
                            newOv.NextOverflowChunkId = -1;

                            // Link from prev LAST — any concurrent observer sees either prev unlinked (correct end-of-chain) or prev → fully-initialised new
                            // (chain terminates safely on -1).
                            ovChunk = ref accessor.GetChunk<OverflowDirIndex>(ovId, true);
                            ovChunk.NextOverflowChunkId = newOvId;
                            ovId = newOvId;
                        }
                        else
                        {
                            ovId = ovChunk.NextOverflowChunkId;
                        }
                    }

                    ref var target = ref accessor.GetChunk<OverflowDirIndex>(ovId, true);
                    target.DirectoryChunkIds[targetSlot] = newDirChunkId;
                }

                meta = ref accessor.GetChunk<PagedHashMapMeta>(0, true);
                meta.DirectoryChunkCount = (ushort)(i + 1);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test helpers (internal for InternalsVisibleTo)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Test-accessible wrapper for <see cref="GetBucketChunkId"/>.</summary>
    internal int GetBucketChunkIdForTest(int bucketId, ref ChunkAccessor<TStore> accessor) => GetBucketChunkId(bucketId, ref accessor);

    /// <summary>Test-accessible wrapper for <see cref="FlushMetaToChunk"/>.</summary>
    internal void FlushMetaForTest(ref ChunkAccessor<TStore> accessor) => FlushMetaToChunk(ref accessor);

    // ═══════════════════════════════════════════════════════════════════════
    // Meta persistence
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Persist in-memory <see cref="PackedMeta"/> and <see cref="_entryCount"/> to chunk 0.
    /// Called after split or during flush.
    /// </summary>
    protected void FlushMetaToChunk(ref ChunkAccessor<TStore> accessor)
    {
        ref var meta = ref accessor.GetChunk<PagedHashMapMeta>(0, true);
        meta.PackedMeta = PackedMeta;
        meta.EntryCount = _entryCount;
    }

    /// <summary>
    /// Flush <c>_entryCount</c> and <c>PackedMeta</c> to the persisted meta chunk (chunk 0). Call before
    /// engine dispose so the next <see cref="InitializeOpen"/> reads the correct total without
    /// having to walk every bucket chain. Bucket splits also flush meta as they run, so this call is
    /// only needed when an append-only workload never triggers a split (e.g., a small number of
    /// entities relative to <c>n0</c>).
    /// </summary>
    public void FlushMeta(ChangeSet changeSet)
    {
        var accessor = _segment.CreateChunkAccessor(changeSet);
        try
        {
            FlushMetaToChunk(ref accessor);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Create / Open scaffolding (called by concrete factory methods)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Initialize a new linear hash map: reserve chunk 0 (meta), allocate directory chunks and initial bucket chunks, and write the initial meta state.
    /// </summary>
    protected void InitializeCreate(int initialBuckets, ChangeSet changeSet)
    {
        Debug.Assert(initialBuckets > 0 && BitOperations.IsPow2(initialBuckets));

        // Reserve chunk 0 for the meta and clear it
        _segment.ReserveChunk(0, true, changeSet);

        InitializeEmptyState(initialBuckets, changeSet);
    }

    /// <summary>
    /// Allocate the directory + initial bucket chunks and write the empty meta state, assuming chunk 0 (the meta chunk) is already reserved. Shared by
    /// <see cref="InitializeCreate"/> (fresh map) and <see cref="ClearForRebuild"/> (crash-recovery reset of an existing map whose chunk 0 is preserved).
    /// </summary>
    private void InitializeEmptyState(int initialBuckets, ChangeSet changeSet)
    {
        Debug.Assert(initialBuckets > 0 && BitOperations.IsPow2(initialBuckets));

        var accessor = _segment.CreateChunkAccessor(changeSet);
        try
        {
            ref var meta = ref accessor.GetChunk<PagedHashMapMeta>(0, true);

            // Write immutable fields
            meta.N0 = _n0;
            meta.OverflowDirIndexChunkId = -1;
            meta.Flags = (byte)(_allowMultiple ? 1 : 0);

            // Compute how many directory chunks we need: ceil(initialBuckets / 64)
            int dirChunkCount = (initialBuckets + PagedHashMapDirectory.EntriesPerChunk - 1) / PagedHashMapDirectory.EntriesPerChunk;

            // Allocate directory chunks and store their IDs in meta.
            // Caller-accessor overload (#301 cascade fix) keeps ACW > 0 on each new dir chunk's content page.
            for (int i = 0; i < dirChunkCount; i++)
            {
                int dirChunkId = _segment.AllocateChunk(changeSet, ref accessor);

                // Re-obtain meta ref after allocation — AllocateChunk may trigger page eviction
                meta = ref accessor.GetChunk<PagedHashMapMeta>(0, true);
                meta.DirectoryChunkIds[i] = dirChunkId;
            }

            meta.DirectoryChunkCount = (ushort)dirChunkCount;

            // Allocate bucket chunks and register them in the directory.
            // Caller-accessor overload (#301 cascade fix) keeps ACW > 0 on each new bucket chunk's content page.
            for (int b = 0; b < initialBuckets; b++)
            {
                int bucketChunkId = _segment.AllocateChunk(changeSet, ref accessor);

                // Re-obtain meta ref after allocation
                meta = ref accessor.GetChunk<PagedHashMapMeta>(0, true);

                // Initialize the bucket
                InitializeBucket(bucketChunkId, ref accessor);

                // Register bucket in directory
                int dirIndex = b >> PagedHashMapDirectory.Shift;
                int dirSlot = b & 0x3F;
                int dirChunkId = meta.DirectoryChunkIds[dirIndex];
                ref var dir = ref accessor.GetChunk<PagedHashMapDirectory>(dirChunkId, true);
                dir.BucketChunkIds[dirSlot] = bucketChunkId;
            }

            // Write initial packed meta: level=0, next=0, bucketCount=initialBuckets
            PackedMeta = PackMeta(0, 0, initialBuckets);
            _entryCount = 0;

            // Persist to chunk 0
            meta = ref accessor.GetChunk<PagedHashMapMeta>(0, true);
            meta.PackedMeta = PackedMeta;
            meta.EntryCount = 0;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Reconnect to an existing linear hash map by reading <see cref="PackedMeta"/> and <see cref="_entryCount"/> from chunk 0.
    /// </summary>
    protected void InitializeOpen()
    {
        var accessor = _segment.CreateChunkAccessor();
        try
        {
            ref readonly var meta = ref accessor.GetChunkReadOnly<PagedHashMapMeta>(0);

            CheckConfig.Require(CheckConfig.Enabled, meta.N0 == _n0,
                $"N0 mismatch between meta chunk and constructor parameter (store corruption or schema drift)");
            CheckConfig.Require(CheckConfig.Enabled, ((meta.Flags & 1) != 0) == _allowMultiple,
                $"AllowMultiple mismatch between meta chunk and constructor parameter (store corruption or schema drift)");

            PackedMeta = meta.PackedMeta;
            _entryCount = meta.EntryCount;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Reset an existing map back to empty for a crash-recovery rebuild: free every bucket / directory / overflow chunk (everything but the reserved meta
    /// chunk 0) by the segment occupancy bitmap, then re-allocate an empty directory + <see cref="N0"/> buckets and reset <see cref="PackedMeta"/> /
    /// <see cref="_entryCount"/>. The map is then repopulated via <c>InsertDuringRebuild</c> from the authoritative source (revision chains / cluster data).
    /// <para>
    /// Torn-safe by construction: freeing is a pure occupancy-bitmap operation — a CRC-torn page is reclaimed without ever being parsed. This is the
    /// EntityMap analogue of <c>BTreeBase.ClearSharedSegment</c> (Phase 2), making the EntityMap a derived-on-crash structure (03-recovery.md §7).
    /// </para>
    /// </summary>
    internal void ClearForRebuild(ChangeSet changeSet)
    {
        using var guard = EpochGuard.Enter(_segment.Store.EpochManager);

        // Free all bucket / directory / overflow chunks by bitmap only (chunk 0 — the meta — is kept reserved). FreeChunk touches only occupancy metadata,
        // never the page content, so a torn page is reclaimed without being read.
        var capacity = _segment.ChunkCapacity;
        for (var chunkId = 1; chunkId < capacity; chunkId++)
        {
            if (_segment.IsChunkAllocated(chunkId))
            {
                _segment.FreeChunk(chunkId);
            }
        }

        // Re-allocate an empty directory + N0 buckets over the preserved meta chunk and reset the in-memory + persisted meta to (level 0, next 0, count 0).
        InitializeEmptyState(_n0, changeSet);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Diagnostics
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validate structural integrity of the hash map. Walks all bucket chains, checks header invariants, and verifies the total entry count
    /// matches <see cref="_entryCount"/>.
    /// </summary>
    public bool VerifyIntegrity(ref ChunkAccessor<TStore> accessor)
    {
        ref readonly var meta = ref accessor.GetChunkReadOnly<PagedHashMapMeta>(0);
        if (meta.N0 != _n0 || meta.N0 <= 0 || !BitOperations.IsPow2(meta.N0))
        {
            return false;
        }

        var (_, _, bucketCount) = ReadMeta();
        long totalEntries = 0;

        for (int b = 0; b < bucketCount; b++)
        {
            int chunkId = GetBucketChunkId(b, ref accessor);
            int visited = 0;

            while (chunkId != -1)
            {
                visited++;
                if (visited > 100)
                {
                    return false; // cycle detection
                }

                ref readonly var header = ref Unsafe.AsRef<PagedHashMapBucketHeader>(accessor.GetChunkAddress(chunkId));
                if (header.EntryCount > BucketCapacity)
                {
                    return false;
                }

                totalEntries += header.EntryCount;
                chunkId = header.OverflowChunkId;
            }
        }

        return totalEntries == _entryCount;
    }

    /// <summary>
    /// Collect diagnostic statistics: bucket count, entry distribution, overflow chain depths, fill histogram.
    /// </summary>
    public PagedHashMapStats GetStats(ref ChunkAccessor<TStore> accessor)
    {
        var (_, _, bucketCount) = ReadMeta();
        var stats = new PagedHashMapStats
        {
            BucketCount = bucketCount,
            EntryCount = _entryCount
        };

        for (int b = 0; b < bucketCount; b++)
        {
            int chunkId = GetBucketChunkId(b, ref accessor);
            ref readonly var primary = ref Unsafe.AsRef<PagedHashMapBucketHeader>(accessor.GetChunkAddress(chunkId));
            int primaryEntryCount = primary.EntryCount;

            // Fill histogram (primary bucket only)
            if (primaryEntryCount == 0)
            {
                stats.FillEmpty++;
            }
            else
            {
                double fill = (double)primaryEntryCount / BucketCapacity;
                if (fill <= 0.25)
                {
                    stats.FillQuarter++;
                }
                else if (fill <= 0.50)
                {
                    stats.FillHalf++;
                }
                else if (fill <= 0.75)
                {
                    stats.FillThreeQuarter++;
                }
                else
                {
                    stats.FillFull++;
                }
            }

            // Chain walk (with cycle detection matching VerifyIntegrity)
            int chainLength = 1;
            if (primary.OverflowChunkId != -1)
            {
                stats.OverflowBucketCount++;
                int overflowId = primary.OverflowChunkId;
                while (overflowId != -1)
                {
                    chainLength++;
                    if (chainLength > 100)
                    {
                        break; // cycle detection
                    }
                    ref readonly var overflow = ref Unsafe.AsRef<PagedHashMapBucketHeader>(accessor.GetChunkAddress(overflowId));
                    overflowId = overflow.OverflowChunkId;
                }
            }

            if (chainLength > stats.MaxChainLength)
            {
                stats.MaxChainLength = chainLength;
            }
        }

        stats.LoadFactor = bucketCount > 0
            ? (double)stats.EntryCount / ((long)bucketCount * BucketCapacity)
            : 0;

        return stats;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Abstract methods — implemented by concrete classes
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Initialize a freshly allocated bucket chunk (set OlcVersion, EntryCount, OverflowChunkId sentinel).
    /// </summary>
    protected abstract void InitializeBucket(int chunkId, ref ChunkAccessor<TStore> accessor);

    /// <summary>
    /// Execute a split: redistribute entries from the current split-pointer bucket to old and new buckets.
    /// Called while holding the split lock.
    /// </summary>
    protected abstract void ExecuteSplit(ref ChunkAccessor<TStore> accessor, ChangeSet changeSet);
}
