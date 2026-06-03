using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Typhon.Engine.Internals;

/// <summary>
/// Tracks entities whose revision cleanup was deferred because a long-running transaction (the "tail")
/// was blocking cleanup at commit time. When the blocking transaction completes, queued entries are
/// processed and old revisions are removed.
/// </summary>
/// <remarks>
/// <para>
/// The queue is keyed by the blocking transaction's TSN. When a tail commits, all entries with
/// <c>blockingTSN ≤ completedTSN</c> are collected and cleaned up outside the lock.
/// </para>
/// <para>
/// A reverse index ensures each (ComponentTable, PrimaryKey) pair appears at most once in the queue,
/// regardless of how many concurrent transactions update the same entity while blocked.
/// </para>
/// </remarks>
internal class DeferredCleanupManager
{
    internal struct CleanupEntry
    {
        public ComponentTable Table;
        public long PrimaryKey;
        public int FirstChunkId;
    }

    internal struct DeferredChunkFreeEntry
    {
        public ComponentTable Table;
        public int ChunkId;
    }

    // Primary storage: sorted by blocking TSN for efficient range queries
    private readonly SortedDictionary<long, List<CleanupEntry>> _pendingCleanups;

    // Reverse index: (table, pk) → blockingTSN for O(1) dedup
    private readonly Dictionary<(ComponentTable, long), long> _entityToBlockingTSN;

    // Pool for reusing List<CleanupEntry> instances across TSN buckets — avoids GC alloc per bucket.
    // All access is under _lock, so no additional synchronization needed.
    private const int ListPoolMaxSize = 16;
    private readonly Stack<List<CleanupEntry>> _listPool = new();

    // Deferred content chunk freeing: keyed by safeAfterTSN — chunks freed when nextMinTSN >= key
    private readonly SortedDictionary<long, List<DeferredChunkFreeEntry>> _pendingChunkFrees;
    private readonly Stack<List<DeferredChunkFreeEntry>> _chunkFreeListPool = new();

    // Thread safety
    private AccessControl _lock;

    // Configuration
    private readonly DeferredCleanupOptions _options;

    // Logger (nullable — no nullable annotations per project conventions)
    private readonly ILogger _log;

    // Observability counters

    /// <summary>
    /// Current number of entities in the deferred cleanup queue. Updated atomically for observability.
    /// </summary>
    public int QueueSize;

    /// <summary>Total entities enqueued for deferred cleanup.</summary>
    public long EnqueuedTotal { get; private set; }

    /// <summary>Total entities cleaned via the deferred path.</summary>
    public long ProcessedTotal { get; private set; }

    /// <summary>Current number of content chunks awaiting deferred freeing.</summary>
    public int ChunkFreeQueueSize;

    /// <summary>Total content chunks freed via the deferred path.</summary>
    public long ChunkFreedTotal { get; private set; }

    public DeferredCleanupManager(DeferredCleanupOptions options, ILogger log = null)
    {
        _options = options;
        _log = log;
        _pendingCleanups = new SortedDictionary<long, List<CleanupEntry>>();
        _entityToBlockingTSN = new Dictionary<(ComponentTable, long), long>();
        _pendingChunkFrees = new SortedDictionary<long, List<DeferredChunkFreeEntry>>();
    }

    /// <summary>
    /// Enqueue a batch of entities for deferred cleanup under a single lock acquisition.
    /// All entries share the same <paramref name="blockingTSN"/> (the tail TSN at the time of commit).
    /// </summary>
    /// <param name="blockingTSN">The TSN of the oldest active transaction blocking cleanup</param>
    /// <param name="entries">The batch of entities to enqueue. Must not be null or empty.</param>
    public void EnqueueBatch(long blockingTSN, List<CleanupEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_lock.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("DeferredCleanup/EnqueueBatch", TimeoutOptions.Current.TransactionChainLockTimeout);
        }

        // Get-or-create the TSN bucket once for the whole batch
        if (!_pendingCleanups.TryGetValue(blockingTSN, out var list))
        {
            list = RentList();
            _pendingCleanups[blockingTSN] = list;
        }

        var added = 0;
        var span = CollectionsMarshal.AsSpan(entries);
        for (int i = 0; i < span.Length; i++)
        {
            ref var entry = ref span[i];
            var key = (entry.Table, entry.PrimaryKey);

            if (_entityToBlockingTSN.TryGetValue(key, out var existingTSN))
            {
                if (blockingTSN >= existingTSN)
                {
                    continue; // Already queued under an older (or same) TSN
                }

                // New blocking TSN is older — migrate to new bucket
                RemoveFromList(existingTSN, entry.Table, entry.PrimaryKey);
            }

            list.Add(entry);
            _entityToBlockingTSN[key] = blockingTSN;
            added++;
        }

        var currentSize = _entityToBlockingTSN.Count;
        QueueSize = currentSize;
        _lock.ExitExclusiveAccess();

        EnqueuedTotal += added;

        // High-water-mark logging — only at exact crossing points to avoid spam
        if (currentSize == _options.CriticalThreshold)
        {
            _log?.LogWarning("Deferred cleanup queue reached critical threshold ({Size} entities)", currentSize);
        }
        else if (currentSize == _options.HighWaterMark)
        {
            _log?.LogWarning("Deferred cleanup queue reached high water mark ({Size} entities)", currentSize);
        }
    }

    /// <summary>
    /// Enqueue content chunks for deferred freeing. Chunks will be freed when <c>nextMinTSN ≥ safeAfterTSN</c>,
    /// meaning all transactions that were active at enqueue time have departed.
    /// </summary>
    /// <param name="safeAfterTSN">The NextFreeId at enqueue time — chunks become safe to free once nextMinTSN reaches this value</param>
    /// <param name="entries">The chunk entries to enqueue. Ownership transfers to the manager.</param>
    public void EnqueueChunkFrees(long safeAfterTSN, List<DeferredChunkFreeEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_lock.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("DeferredCleanup/EnqueueChunkFrees", TimeoutOptions.Current.TransactionChainLockTimeout);
        }

        if (!_pendingChunkFrees.TryGetValue(safeAfterTSN, out var list))
        {
            list = RentChunkFreeList();
            _pendingChunkFrees[safeAfterTSN] = list;
        }

        list.AddRange(entries);
        ChunkFreeQueueSize += entries.Count;
        _lock.ExitExclusiveAccess();
    }

    /// <summary>
    /// Process all deferred cleanups for entities blocked by transactions with TSN ≤ <paramref name="completedTSN"/>.
    /// </summary>
    /// <param name="completedTSN">The TSN of the tail transaction that just committed</param>
    /// <param name="nextMinTSN">The minimum TSN to keep revisions for (the next oldest active transaction)</param>
    /// <param name="dbe">The database engine (for epoch manager and MMF access)</param>
    /// <param name="changeSet">The change set for tracking dirty pages</param>
    /// <returns>The number of entities cleaned up</returns>
    public int ProcessDeferredCleanups(long completedTSN, long nextMinTSN, DatabaseEngine dbe, ChangeSet changeSet)
    {
        // Lockless early-exit: Count is a plain int field read (atomic on x64).
        // Stale zero → skip (entries caught on next call). Stale non-zero → acquire lock, find nothing, exit.
        if (_pendingCleanups.Count == 0 && _pendingChunkFrees.Count == 0)
        {
            return 0;
        }

        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_lock.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("DeferredCleanup/Process", TimeoutOptions.Current.TransactionChainLockTimeout);
        }

        // ── Collect mature entity cleanup entries ──
        List<CleanupEntry> toCleanup = null;
        var maxTsnCount = Math.Max(_pendingCleanups.Count, _pendingChunkFrees.Count);
        Span<long> tsnsToRemove = maxTsnCount <= 32 ? stackalloc long[32] : new long[maxTsnCount];
        var tsnsToRemoveCount = 0;

        foreach (var kvp in _pendingCleanups)
        {
            if (kvp.Key > completedTSN)
            {
                break; // SortedDictionary — no more relevant entries
            }

            toCleanup ??= new List<CleanupEntry>();
            toCleanup.AddRange(kvp.Value);
            tsnsToRemove[tsnsToRemoveCount++] = kvp.Key;

            // Remove from reverse lookup, then return the list to the pool
            foreach (var entry in kvp.Value)
            {
                _entityToBlockingTSN.Remove((entry.Table, entry.PrimaryKey));
            }
            ReturnList(kvp.Value);
        }

        for (var i = 0; i < tsnsToRemoveCount; i++)
        {
            _pendingCleanups.Remove(tsnsToRemove[i]);
        }

        QueueSize = _entityToBlockingTSN.Count;

        // ── Collect mature chunk free entries (keyed by safeAfterTSN, mature when nextMinTSN >= key) ──
        List<DeferredChunkFreeEntry> chunksToFree = null;
        tsnsToRemoveCount = 0;

        foreach (var kvp in _pendingChunkFrees)
        {
            if (kvp.Key > nextMinTSN)
            {
                break;
            }

            chunksToFree ??= new List<DeferredChunkFreeEntry>();
            chunksToFree.AddRange(kvp.Value);
            tsnsToRemove[tsnsToRemoveCount++] = kvp.Key;
            ReturnChunkFreeList(kvp.Value);
        }

        for (var i = 0; i < tsnsToRemoveCount; i++)
        {
            _pendingChunkFrees.Remove(tsnsToRemove[i]);
        }

        if (chunksToFree != null)
        {
            ChunkFreeQueueSize -= chunksToFree.Count;
        }

        _lock.ExitExclusiveAccess();

        // ── Free mature content chunks OUTSIDE the lock (requires epoch scope for chunk accessors) ──
        if (chunksToFree != null)
        {
            using var chunkFreeGuard = EpochGuard.Enter(dbe.EpochManager);
            foreach (var entry in chunksToFree)
            {
                FreeContentChunk(entry.Table, entry.ChunkId);
            }
            ChunkFreedTotal += chunksToFree.Count;
        }

        // ── Perform entity cleanup OUTSIDE the lock (I/O operations) ──
        var cleanedCount = 0;
        List<DeferredChunkFreeEntry> collectedChunkFrees = null;

        if (toCleanup != null)
        {
            collectedChunkFrees = new List<DeferredChunkFreeEntry>(toCleanup.Count);
            using var entityGuard = EpochGuard.Enter(dbe.EpochManager);
            var cleanupSpan = CollectionsMarshal.AsSpan(toCleanup);

            // Fast path: if all entries share the same table (common: BulkUpdate uses one component type), single batch
            var allSameTable = true;
            for (var i = 1; i < cleanupSpan.Length; i++)
            {
                if (!ReferenceEquals(cleanupSpan[i].Table, cleanupSpan[0].Table))
                {
                    allSameTable = false;
                    break;
                }
            }

            if (allSameTable)
            {
                CleanupEntityRevisionsBatched(cleanupSpan, nextMinTSN, changeSet, collectedChunkFrees, ref cleanedCount);
            }
            else
            {
                // Multi-table path: sort by table identity hash to group, then process contiguous groups
                cleanupSpan.Sort((a, b) => RuntimeHelpers.GetHashCode(a.Table).CompareTo(RuntimeHelpers.GetHashCode(b.Table)));
                var groupStart = 0;
                while (groupStart < cleanupSpan.Length)
                {
                    var groupTable = cleanupSpan[groupStart].Table;
                    var groupEnd = groupStart + 1;
                    while (groupEnd < cleanupSpan.Length && ReferenceEquals(cleanupSpan[groupEnd].Table, groupTable))
                    {
                        groupEnd++;
                    }
                    CleanupEntityRevisionsBatched(cleanupSpan.Slice(groupStart, groupEnd - groupStart), nextMinTSN, changeSet,
                        collectedChunkFrees, ref cleanedCount);
                    groupStart = groupEnd;
                }
            }
        }

        if (cleanedCount > 0)
        {
            ProcessedTotal += cleanedCount;
        }

        // Entity cleanup may have produced new deferred chunk frees — enqueue them with the current safeAfterTSN
        if (collectedChunkFrees is { Count: > 0 })
        {
            var safeAfterTSN = dbe.TransactionChain.NextFreeId;
            EnqueueChunkFrees(safeAfterTSN, collectedChunkFrees);
        }

        return cleanedCount;
    }

    /// <summary>
    /// Frees a content chunk and its associated collection buffers. Used by the deferred chunk free path
    /// (creates its own accessor since the cleanup's accessor is disposed by the time deferred freeing runs).
    /// </summary>
    internal static void FreeContentChunk(ComponentTable ct, int chunkId)
    {
        if (chunkId == 0)
        {
            return;
        }

        if (ct.HasCollections)
        {
            var accessor = ct.ComponentSegment.CreateChunkAccessor();
            foreach (var kvp in ct.ComponentCollectionVSBSByOffset)
            {
                // kvp.Key is the offset within the component struct; content chunks prefix it with ComponentOverhead
                // (Versioned = 0, SingleVersion = 8 for the entity PK). Cluster slots have no overhead.
                var bufferId = accessor.GetChunkAsReadOnlySpan(chunkId).Slice(ct.ComponentOverhead + kvp.Key).Cast<byte, int>()[0];
                var collAccessor = kvp.Value.Segment.CreateChunkAccessor();
                kvp.Value.BufferRelease(bufferId, ref collAccessor);
                collAccessor.Dispose();
            }
            accessor.Dispose();
        }

        ct.ComponentSegment.FreeChunk(chunkId);
    }

    /// <summary>
    /// Flush all pending chunk frees regardless of TSN maturity. For test/diagnostic use.
    /// </summary>
    internal void FlushChunkFrees(EpochManager epochManager)
    {
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_lock.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("DeferredCleanup/FlushChunkFrees", TimeoutOptions.Current.TransactionChainLockTimeout);
        }

        var allEntries = new List<DeferredChunkFreeEntry>();
        foreach (var kvp in _pendingChunkFrees)
        {
            allEntries.AddRange(kvp.Value);
            ReturnChunkFreeList(kvp.Value);
        }
        _pendingChunkFrees.Clear();
        ChunkFreeQueueSize = 0;
        _lock.ExitExclusiveAccess();

        if (allEntries.Count > 0)
        {
            using var guard = EpochGuard.Enter(epochManager);
            foreach (var entry in allEntries)
            {
                FreeContentChunk(entry.Table, entry.ChunkId);
            }
            ChunkFreedTotal += allEntries.Count;
        }
    }

    /// <summary>
    /// Clean up old revisions for a batch of entities sharing the same <see cref="ComponentTable"/>.
    /// Shares CompRevTable and CompContent accessors across the batch to avoid per-entity creation overhead.
    /// Caller must be inside an epoch scope.
    /// </summary>
    private static void CleanupEntityRevisionsBatched(Span<CleanupEntry> entries, long nextMinTSN, ChangeSet changeSet,
        List<DeferredChunkFreeEntry> collectedChunkFrees, ref int cleanedCount)
    {
        Debug.Assert(entries.Length > 0);

        var table = entries[0].Table;

        // Shared accessors — created once for the entire batch
        var compRevTableAccessor = table.CompRevTableSegment.CreateChunkAccessor(changeSet);
        var compContentAccessor = table.ComponentSegment.CreateChunkAccessor(changeSet);

        for (var i = 0; i < entries.Length; i++)
        {
            ref var entry = ref entries[i];
            Debug.Assert(ReferenceEquals(entry.Table, table));

            var firstChunkId = entry.FirstChunkId;

            // Acquire exclusive lock — cleanup restructures the chain, must be serialized with AddCompRev.
            // Use TryEnter: if contended, skip this entry — it will be retried on the next cleanup pass.
            ref var header = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId, true);
            if (!header.Control.TryEnterExclusiveAccess())
            {
                continue;
            }

            bool isDeleted;
            try
            {
                isDeleted = ComponentRevisionManager.CleanUpUnusedEntriesCore(
                    table, firstChunkId, nextMinTSN, ref compRevTableAccessor, ref compContentAccessor, collectedChunkFrees);
            }
            finally
            {
                header = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId);
                header.Control.ExitExclusiveAccess();
            }

            if (isDeleted)
            {
                table.CompRevTableSegment.FreeChunk(firstChunkId);
            }
            else
            {
                compRevTableAccessor.DirtyChunk(firstChunkId);
            }

            cleanedCount++;
        }

        compRevTableAccessor.Dispose();
        compContentAccessor.Dispose();
    }

    /// <summary>Rent a list from the pool or create a new one. Must be called under lock.</summary>
    private List<CleanupEntry> RentList()
    {
        if (_listPool.TryPop(out var list))
        {
            return list;
        }
        return [];
    }

    /// <summary>Clear and return a list to the pool. Must be called under lock.</summary>
    private void ReturnList(List<CleanupEntry> list)
    {
        list.Clear();
        if (_listPool.Count < ListPoolMaxSize)
        {
            _listPool.Push(list);
        }
    }

    /// <summary>
    /// Remove a specific entity from a TSN bucket in the pending cleanups list.
    /// Must be called under lock.
    /// </summary>
    private void RemoveFromList(long tsn, ComponentTable table, long pk)
    {
        if (!_pendingCleanups.TryGetValue(tsn, out var list))
        {
            return;
        }

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Table == table && list[i].PrimaryKey == pk)
            {
                list.RemoveAt(i);
                break;
            }
        }

        if (list.Count == 0)
        {
            ReturnList(list);
            _pendingCleanups.Remove(tsn);
        }
    }

    /// <summary>Rent a chunk free list from the pool or create a new one. Must be called under lock.</summary>
    private List<DeferredChunkFreeEntry> RentChunkFreeList()
    {
        if (_chunkFreeListPool.TryPop(out var list))
        {
            return list;
        }
        return [];
    }

    /// <summary>Clear and return a chunk free list to the pool. Must be called under lock.</summary>
    private void ReturnChunkFreeList(List<DeferredChunkFreeEntry> list)
    {
        list.Clear();
        if (_chunkFreeListPool.Count < ListPoolMaxSize)
        {
            _chunkFreeListPool.Push(list);
        }
    }
}
