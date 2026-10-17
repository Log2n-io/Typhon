using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
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

        /// <summary>A revision-chain chunk (freed in the table's revision segment), not a content chunk.</summary>
        public bool Revision;
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

    /// <summary>
    /// Set once a cleanup threw where chunks it had taken off a chain, or off the free queue, may be left allocated in no chain (#1268): no later cleanup
    /// finds them, since a cleanup walks chains. The close then writes no clean-shutdown marker, and the next open's orphan sweep frees them.
    /// </summary>
    internal volatile bool FreesLost;

    /// <summary>Invoked as each entity of a cleanup batch is compacted, under its chain lock, with its index in the batch. Test seam: a throw stages a failing
    /// compaction.</summary>
    internal Action<int> EntityCleanupProbeForTest { get; set; }

    /// <summary>Test seam: called with the entry's index as its pre-lock ownership check starts (EP-02).</summary>
    internal Action<int> ChainCheckProbeForTest { get; set; }

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

        var added = 0;
        int currentSize;
        try
        {
            // Get-or-create the TSN bucket once for the whole batch
            if (!_pendingCleanups.TryGetValue(blockingTSN, out var list))
            {
                list = RentList();
                _pendingCleanups[blockingTSN] = list;
            }

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

            currentSize = _entityToBlockingTSN.Count;
            QueueSize = currentSize;
        }
        finally
        {
            _lock.ExitExclusiveAccess();
        }

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

        try
        {
            if (!_pendingChunkFrees.TryGetValue(safeAfterTSN, out var list))
            {
                list = RentChunkFreeList();
                _pendingChunkFrees[safeAfterTSN] = list;
            }

            list.AddRange(entries);
            ChunkFreeQueueSize += entries.Count;
        }
        finally
        {
            _lock.ExitExclusiveAccess();
        }
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
        List<DeferredChunkFreeEntry> chunksToFree = null;
        try
        {
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
        }
        finally
        {
            _lock.ExitExclusiveAccess();
        }

        // ── Free mature content chunks OUTSIDE the lock (requires epoch scope for chunk accessors) ──
        if (chunksToFree != null)
        {
            using var chunkFreeGuard = EpochGuard.Enter(dbe.EpochManager);
            var epochYield = new EpochYield(dbe.EpochManager);
            try
            {
                foreach (var entry in chunksToFree)
                {
                    epochYield.BetweenEntries();
                    FreeDeferred(entry);
                }
            }
            catch
            {
                // Off the queue already: the rest stay allocated in no chain (#1268).
                FreesLost = true;
                throw;
            }

            ChunkFreedTotal += chunksToFree.Count;
        }

        // ── Perform entity cleanup OUTSIDE the lock (I/O operations) ──
        var cleanedCount = 0;
        List<DeferredChunkFreeEntry> collectedChunkFrees = null;

        var faulted = true;
        try
        {
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

                var epochYield = new EpochYield(dbe.EpochManager);
                if (allSameTable)
                {
                    CleanupEntityRevisionsBatched(cleanupSpan, nextMinTSN, changeSet, collectedChunkFrees, ref cleanedCount, ref epochYield);
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
                            collectedChunkFrees, ref cleanedCount, ref epochYield);
                        groupStart = groupEnd;
                    }
                }
            }

            faulted = false;
        }
        finally
        {
            if (cleanedCount > 0)
            {
                ProcessedTotal += cleanedCount;
            }

            // Entity cleanup may have produced new deferred chunk frees — enqueue them with the current safeAfterTSN. Also when a batch threw: the entities
            // it compacted before named their chunks in no chain any more, and dropped with the list they were lost for good (#1268). An enqueue that
            // fails loses them as well, and never masks the batch's own failure.
            if (collectedChunkFrees is { Count: > 0 })
            {
                try
                {
                    EnqueueChunkFrees(dbe.TransactionChain.NextFreeId, collectedChunkFrees);
                }
                catch
                {
                    FreesLost = true;
                    if (!faulted)
                    {
                        throw;
                    }
                }
            }
        }

        return cleanedCount;
    }

    /// <summary>
    /// Moves a long drain's epoch between its entries (#1230, EP-02): the tail's drain runs every transaction's cleanups, and refreshed only where it began, a
    /// batch of thousands held its pin while every other thread's pages piled up behind it — the oldest pin on a 64 MiB market, 6,800 to 15,700 epochs
    /// behind. Between two entries the drain holds only its accessors' slot-reference pins: a call boundary (EP-03). At most every
    /// <see cref="EntityAccessor.ScanEpochRefreshInterval"/>, never under a live index cursor.
    /// </summary>
    internal struct EpochYield
    {
        private readonly EpochManager _epochManager;
        private long _refreshedAt;

        internal EpochYield(EpochManager epochManager)
        {
            _epochManager = epochManager;
            _refreshedAt = Stopwatch.GetTimestamp();
        }

        /// <summary>Whether the interval has passed, with no live index cursor: the drain may move its epoch here (<see cref="Refresh"/>).</summary>
        internal bool Due => _epochManager != null && EntityAccessor.LiveIndexCursors == 0
                             && Stopwatch.GetElapsedTime(_refreshedAt) >= EntityAccessor.ScanEpochRefreshInterval;

        /// <summary>
        /// Between two entries, once due: gives back <paramref name="marks"/>' dirty marks and moves the thread's epoch. The drain's accessors must have
        /// committed their changes (a disposed accessor has). Kept, the marks held every page the drain had touched unevictable until it ended, and the
        /// drain is unbounded: one held 3,434 pages of a 64 MiB cache while it waited on back-pressure itself (#1382). Released, a page is held by its
        /// writeback debt until the checkpoint writes it (PS-10), as at a transaction's epoch refresh.
        /// </summary>
        internal void Refresh(ChangeSet marks)
        {
            marks?.ReleaseDirtyMarks();
            ChunkBasedSegment<PersistentStore>.RefreshWarmCacheEpoch(_epochManager.RefreshScope());
            _refreshedAt = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref DrainEpochRefreshes);
        }

        /// <summary>Between two entries whose accessors are disposed: <see cref="Refresh"/> if due.</summary>
        internal void BetweenEntries(ChangeSet marks = null)
        {
            if (Due)
            {
                Refresh(marks);
            }
        }
    }

    /// <summary>Epoch refreshes taken between drain entries (<see cref="EpochYield"/>). Diagnostic.</summary>
    internal static long DrainEpochRefreshes;

    private static void FreeDeferred(DeferredChunkFreeEntry entry)
    {
        if (entry.Revision)
        {
            FreeChunkWaitingRefreshed(entry.Table.CompRevTableSegment, entry.ChunkId);
        }
        else if (entry.ChunkId != 0)
        {
            if (entry.Table.HasCollections)
            {
                var accessor = entry.Table.ComponentSegment.CreateChunkAccessor();
                ReleaseCollectionBuffers(entry.Table, accessor.GetChunkAsReadOnlySpan(entry.ChunkId));
                accessor.Dispose();
            }

            FreeChunkWaitingRefreshed(entry.Table.ComponentSegment, entry.ChunkId);
        }
    }

    /// <summary>
    /// A drain's chunk free, whose back-pressure waits move this thread's epoch (EP-02, #1382). The free holds no page address past its bitmap page's
    /// fetch, and between two entries the drain holds only slot-reference pins (<see cref="EpochYield"/>), so a refresh in that fetch releases nothing
    /// anyone still reads; the page fetched is tagged with the moved pin (PS-03). Waiting with its pin instead, the transaction draining the queue at
    /// its dispose was the cache's oldest pin in two of every three of a 32 MiB market's waits.
    /// </summary>
    private static void FreeChunkWaitingRefreshed(ChunkBasedSegment<PersistentStore> segment, int chunkId)
    {
        PagedMMF.RefreshableWaitDepth++;
        try
        {
            segment.FreeChunk(chunkId);
        }
        finally
        {
            PagedMMF.RefreshableWaitDepth--;
        }
    }

    /// <summary>
    /// Frees a content chunk and its associated collection buffers. Used by the deferred chunk free path
    /// (creates its own accessor since the cleanup's accessor is disposed by the time deferred freeing runs).
    /// </summary>
    /// <param name="ct">The table.</param>
    /// <param name="chunkId">The content chunk.</param>
    /// <param name="neverCommitted">
    /// The chunk held only a rolled-back transaction's value: freed at once, not held until a checkpoint (CK-17). An element naming it on disk is
    /// uncommitted, which recovery never reads, and a head that reuses it is protected from the scrub's free of the uncommitted element's content.
    /// </param>
    internal static void FreeContentChunk(ComponentTable ct, int chunkId, bool neverCommitted = false)
    {
        if (chunkId == 0)
        {
            return;
        }

        if (ct.HasCollections)
        {
            var accessor = ct.ComponentSegment.CreateChunkAccessor();
            ReleaseCollectionBuffers(ct, accessor.GetChunkAsReadOnlySpan(chunkId));
            accessor.Dispose();
        }

        if (neverCommitted)
        {
            ct.ComponentSegment.FreeChunkUnheld(chunkId);
        }
        else
        {
            ct.ComponentSegment.FreeChunk(chunkId);
        }
    }

    /// <summary>
    /// Releases every <c>ComponentCollection</c> buffer referenced by one component payload, given the payload's bytes.
    /// </summary>
    /// <param name="ct">The component's table — supplies the collection field descriptors and the payload's overhead.</param>
    /// <param name="payload">
    /// The full <c>[ComponentOverhead][value]</c> payload. It does not matter where those bytes live: this is the half of
    /// <see cref="FreeContentChunk"/> that has nothing to do with chunks, split out so a spawn payload staged in the
    /// transaction's <c>SpawnStagingArena</c> can be cleaned up on rollback (#839). Before that split, the buffer release
    /// and the chunk free were welded together, so removing the chunk would have silently taken the release with it —
    /// and DC-01 is <c>[fatal][silent]</c>.
    /// </param>
    internal static void ReleaseCollectionBuffers(ComponentTable ct, ReadOnlySpan<byte> payload)
    {
        foreach (var f in ct.CollectionFields)
        {
            // The descriptor's offset is within the component struct; a content payload prefixes it with ComponentOverhead
            // (Versioned = 0, SingleVersion = 8 for the entity PK). Cluster slots have no overhead.
            var bufferId = payload.Slice(ct.ComponentOverhead + f.OffsetInComponentStorage).Cast<byte, int>()[0];

            // 0 means the field never got a buffer. BufferRelease has no zero guard of its own: it would decrement the
            // refcount of the RESERVED root chunk 0 and, on reaching zero, free its chain. The cluster-destroy path
            // guards this at ArchetypeClusterState (`if (bufferId != 0)`) and this path must too — more so since #839,
            // because a spawn payload is now zeroed on allocation, which turns "unpopulated collection" from
            // whatever-the-recycled-chunk-held into a reliable 0.
            if (bufferId == 0)
            {
                continue;
            }

            var collAccessor = f.Vsbs.Segment.CreateChunkAccessor();
            f.Vsbs.BufferRelease(bufferId, ref collAccessor);
            collAccessor.Dispose();
        }
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
        try
        {
            foreach (var kvp in _pendingChunkFrees)
            {
                allEntries.AddRange(kvp.Value);
                ReturnChunkFreeList(kvp.Value);
            }
            _pendingChunkFrees.Clear();
            ChunkFreeQueueSize = 0;
        }
        finally
        {
            _lock.ExitExclusiveAccess();
        }

        if (allEntries.Count > 0)
        {
            using var guard = EpochGuard.Enter(epochManager);
            foreach (var entry in allEntries)
            {
                FreeDeferred(entry);
            }
            ChunkFreedTotal += allEntries.Count;
        }
    }

    /// <summary>
    /// Clean up old revisions for a batch of entities sharing the same <see cref="ComponentTable"/>.
    /// Shares CompRevTable and CompContent accessors across the batch to avoid per-entity creation overhead.
    /// Caller must be inside an epoch scope.
    /// </summary>
    private void CleanupEntityRevisionsBatched(Span<CleanupEntry> entries, long nextMinTSN, ChangeSet changeSet,
        List<DeferredChunkFreeEntry> collectedChunkFrees, ref int cleanedCount, ref EpochYield epochYield)
    {
        Debug.Assert(entries.Length > 0);

        var table = entries[0].Table;

        // Shared accessors — created once for the entire batch
        var compRevTableAccessor = table.CompRevTableSegment.CreateChunkAccessor(changeSet);
        var compContentAccessor = table.ComponentSegment.CreateChunkAccessor(changeSet);

        try
        {
            for (var i = 0; i < entries.Length; i++)
            {
                // The batch's accessors are shared: their changes committed first, so the marks can go with the epoch (#1382).
                if (epochYield.Due)
                {
                    compRevTableAccessor.CommitChanges();
                    compContentAccessor.CommitChanges();
                    epochYield.Refresh(changeSet);
                }

                ref var entry = ref entries[i];
                Debug.Assert(ReferenceEquals(entry.Table, table));

                var firstChunkId = entry.FirstChunkId;

                // Still this entry's chain? A chain an entity's cleanup released and the allocator handed out again names another owner, or is not allocated at
                // all: compacting it would rearrange someone else's revisions (REAP-02).
                // Up to its chain lock an entry holds what it holds between two entries, its accessors' slot-reference pins: its page waits move the
                // epoch (EP-02, #1382), as the refresh between entries does. Waiting with its pin, the drain was the oldest pin of a 32 MiB market.
                ChainCheckProbeForTest?.Invoke(i);
                PagedMMF.RefreshableWaitDepth++;
                bool ours;
                try
                {
                    ours = table.CompRevTableSegment.IsChunkLive(firstChunkId)
                           && compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId).EntityPK == entry.PrimaryKey;
                }
                finally
                {
                    PagedMMF.RefreshableWaitDepth--;
                }

                if (!ours)
                {
                    Interlocked.Increment(ref ForeignChainsSkipped);
                    continue;
                }

                // Acquire exclusive lock — cleanup restructures the chain, must be serialized with AddCompRev.
                // Use TryEnter: if contended, skip this entry — it will be retried on the next cleanup pass.
                ref var header = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId, true);
                var ticket = ComponentRevisionManager.ChainLockTicket(ref compRevTableAccessor, ref header);
                if (!header.Control.TryEnterExclusiveAccess())
                {
                    continue;
                }

                var collectedBefore = collectedChunkFrees?.Count ?? 0;
                try
                {
                    EntityCleanupProbeForTest?.Invoke(i);
                    _ = ComponentRevisionManager.CleanUpUnusedEntriesCore(
                        table, firstChunkId, nextMinTSN, ref compRevTableAccessor, ref compContentAccessor, collectedChunkFrees);
                }
                catch
                {
                    // A compaction that throws leaves its chain as it was and queues nothing (#1308); what else this entity's pass collected is dropped,
                    // never freed twice, and the sweep is owed all the same (#1268): the probe's throw, a chunk the compaction could not give back.
                    collectedChunkFrees?.RemoveRange(collectedBefore, collectedChunkFrees.Count - collectedBefore);
                    FreesLost = true;
                    throw;
                }
                finally
                {
                    header = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId);
                    header.Control.ExitExclusiveAccess();
                    ComponentRevisionManager.EndChainLockHold(ref compRevTableAccessor, in ticket);
                }

                // A lone tombstone is not freed here, though nothing can read it any more: only an ECS destroy writes tombstones, and the destroyed entity's
                // cleanup frees the chain once the entity is past every snapshot (ReleaseDestroyedEntityChain). Both used to free it, and between the two
                // frees a spawn on another thread could take the chunk: the second free then took a LIVE chain, and its commit found its own revision gone
                // (AP-05). One owner, the one that also unmaps the entity.
                compRevTableAccessor.DirtyChunk(firstChunkId);

                cleanedCount++;
            }
        }
        finally
        {
            compRevTableAccessor.Dispose();
            compContentAccessor.Dispose();
        }
    }

    /// <summary>Revision-GC entries skipped because their chain names another owner, or is no longer allocated. Diagnostic.</summary>
    internal long ForeignChainsSkipped;

    /// <summary>Destroyed entities' chains released whole by <see cref="ReleaseDestroyedEntityChain"/>. Diagnostic.</summary>
    internal long DestroyedChainsReleased;

    /// <summary>
    /// Releases put off for a later cleanup pass: a revision cleanup pending, the chain's lock held, or an uncommitted revision in it. Diagnostic.
    /// </summary>
    internal long DestroyedChainsDeferred;

    /// <summary>
    /// Destroyed entities' chain roots that were not theirs (not allocated, or naming another owner): not freed. Diagnostic — each is a bug.
    /// </summary>
    internal long DestroyedChainsForeign;

    /// <summary>What <see cref="ReleaseDestroyedEntityChain"/> did with a chain.</summary>
    internal enum ChainRelease
    {
        /// <summary>Every chunk of the chain and every content chunk it names is queued for freeing.</summary>
        Released,

        /// <summary>Not yet: try again on a later pass. Nothing was touched.</summary>
        Deferred,

        /// <summary>The root is not this entity's chain: nothing freed, nothing to retry.</summary>
        NotThisEntitys,
    }

    /// <summary>
    /// Releases a destroyed entity's revision chain for one Versioned component, whole: every chunk of the chain and every content chunk a live element of
    /// it names, queued for freeing once every transaction alive now has finished. Called by the entity cleanup once the entity's death is below every
    /// live snapshot, which makes it the chain's one owner (REAP-02): the revision GC trims a destroyed chain to its tombstone and never frees the root.
    /// </summary>
    /// <remarks>
    /// <para><b>Put off, never forced</b>, while the chain is not quiescent: a revision cleanup still pending for it (the GC must not meet a released root),
    /// its lock held, or an element still uncommitted (a transaction that opened the entity before the destroy and has not finished). The caller keeps the
    /// entity queued and tries again.</para>
    /// <para><b>Deferred frees</b>, the root included: a transaction that resolved the entity before its destroy committed can still walk the chain until it
    /// finishes. The content chunks of voided elements were freed by whoever voided them; a lone tombstone names none.</para>
    /// </remarks>
    internal ChainRelease ReleaseDestroyedEntityChain(ComponentTable table, int firstChunkId, long entityPK, ChangeSet changeSet,
        List<DeferredChunkFreeEntry> frees)
    {
        if (HasPendingRevisionCleanup(table, entityPK))
        {
            Interlocked.Increment(ref DestroyedChainsDeferred);
            return ChainRelease.Deferred;
        }

        var revisions = table.CompRevTableSegment;
        if (revisions == null || !revisions.IsChunkLive(firstChunkId))
        {
            Interlocked.Increment(ref DestroyedChainsForeign);
            return ChainRelease.NotThisEntitys;
        }

        var accessor = revisions.CreateChunkAccessor(changeSet);
        try
        {
            ref var header = ref accessor.GetChunk<CompRevStorageHeader>(firstChunkId);
            if (header.EntityPK != entityPK)
            {
                Interlocked.Increment(ref DestroyedChainsForeign);
                return ChainRelease.NotThisEntitys;
            }

            var ticket = ComponentRevisionManager.ChainLockTicket(ref accessor, ref header);
            if (!header.Control.TryEnterExclusiveAccess())
            {
                Interlocked.Increment(ref DestroyedChainsDeferred);
                return ChainRelease.Deferred;
            }

            var firstFree = frees.Count;
            try
            {
                for (var i = 0; i < header.ItemCount; i++)
                {
                    ref var element = ref ComponentRevisionManager.GetRevisionElement(ref accessor, firstChunkId, (short)(header.FirstItemIndex + i)).Element;
                    if (element.IsVoid)
                    {
                        continue;
                    }

                    if (element.IsolationFlag)
                    {
                        // A transaction still writing this entity: it ends, then the chain is quiescent.
                        frees.RemoveRange(firstFree, frees.Count - firstFree);
                        Interlocked.Increment(ref DestroyedChainsDeferred);
                        return ChainRelease.Deferred;
                    }

                    if (element.ComponentChunkId > 0)
                    {
                        frees.Add(new DeferredChunkFreeEntry { Table = table, ChunkId = element.ComponentChunkId });
                    }
                }

                // The chain's chunks: the root, then its ChainLength - 1 overflow chunks through their leading next-chunk ids — exactly that many: the last
                // one's link is not trusted to be 0 (a chunk reissued uncleared kept its previous owner's).
                frees.Add(new DeferredChunkFreeEntry { Table = table, ChunkId = firstChunkId, Revision = true });
                var next = header.NextChunkId;
                for (var left = header.ChainLength - 1; next != 0 && left > 0; left--)
                {
                    frees.Add(new DeferredChunkFreeEntry { Table = table, ChunkId = next, Revision = true });
                    next = accessor.GetChunk<int>(next);
                }
            }
            finally
            {
                header = ref accessor.GetChunk<CompRevStorageHeader>(firstChunkId);
                header.Control.ExitExclusiveAccess();
                ComponentRevisionManager.EndChainLockHold(ref accessor, in ticket);
            }

            Interlocked.Increment(ref DestroyedChainsReleased);
            return ChainRelease.Released;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>Whether a revision cleanup is queued for (<paramref name="table"/>, <paramref name="pk"/>).</summary>
    private bool HasPendingRevisionCleanup(ComponentTable table, long pk)
    {
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_lock.EnterExclusiveAccess(ref wc))
        {
            return true;   // cannot tell: treat as pending, and retry later
        }

        try
        {
            return _entityToBlockingTSN.ContainsKey((table, pk));
        }
        finally
        {
            _lock.ExitExclusiveAccess();
        }
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
