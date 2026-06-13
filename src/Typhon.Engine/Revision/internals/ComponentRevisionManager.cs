using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

[PublicAPI]
internal ref struct ComponentRevisionManager
{
    internal const int CompRevChunkSize = 64;
    internal unsafe static readonly int CompRevCountInRoot = (CompRevChunkSize - sizeof(CompRevStorageHeader)) / sizeof(CompRevStorageElement);
    internal unsafe static readonly int CompRevCountInNext = (CompRevChunkSize / sizeof(CompRevStorageElement));

    internal ref struct ElementRevisionHandle
    {
        private ref ChunkAccessor<PersistentStore> _accessor;
        private readonly int _chunkId;
        private readonly bool _isFirst;
        private readonly short _elementIndex;

        public ElementRevisionHandle(ref ChunkAccessor<PersistentStore> accessor, int chunkId, bool isFirst, short elementIndex)
        {
            _accessor = ref accessor;
            _chunkId = chunkId;
            _isFirst = isFirst;
            _elementIndex = elementIndex;
        }

        // The resolved physical location of this revision element. Captured during PREPARE (where the locking walk is allowed) so the AP-01 PUBLISH pass
        // can reconstruct the handle without re-running the locking GetRevisionElement walk (AP-03: publish acquires no bounded-timeout lock).
        public int ChunkId => _chunkId;
        public bool IsFirst => _isFirst;
        public short ElementIndex => _elementIndex;

        public unsafe ref CompRevStorageElement Element
        {
            get
            {
                var headerSize = _isFirst ? sizeof(CompRevStorageHeader) : sizeof(int);
                return ref _accessor.GetChunkAsSpan(_chunkId, true).Slice(headerSize).Cast<byte, CompRevStorageElement>().Slice(_elementIndex, 1)[0];
            }
        }

        public void Commit(long tsn)
        {
            ref var el = ref Element;
            el.TSN = tsn;
            el.IsolationFlag = false;
        }

    }

    internal static ElementRevisionHandle GetRevisionElement(ref ChunkAccessor<PersistentStore> accessor, int firstChunkId, short revisionIndex)
    {
        ref var firstHeader = ref accessor.GetChunk<CompRevStorageHeader>(firstChunkId);
        if (revisionIndex < CompRevCountInRoot)
        {
            return new ElementRevisionHandle(ref accessor, firstChunkId, true, revisionIndex);
        }

        var (chunkIndexInChain, indexInChunk) = CompRevStorageHeader.GetRevisionLocation(revisionIndex);

        // Walk through the linked list until we find the chunk that is our starting point
        var nextChunkId = firstHeader.NextChunkId;

        var curChunkId = nextChunkId;
        var useLock = !firstHeader.Control.IsLockedByCurrentThread;
        if (useLock)
        {
            var wc = WaitContext.FromTimeout(TimeoutOptions.Current.RevisionChainLockTimeout);
            if (!firstHeader.Control.EnterSharedAccess(ref wc))
            {
                ThrowHelper.ThrowLockTimeout("RevisionChain/GetElement", TimeoutOptions.Current.RevisionChainLockTimeout);
            }
        }
        while (--chunkIndexInChain >= 0)
        {
            curChunkId = nextChunkId;
            nextChunkId = accessor.GetChunk<int>(nextChunkId);
        }

        if (useLock)
        {
            firstHeader.Control.ExitSharedAccess();
        }

        return new ElementRevisionHandle(ref accessor, curChunkId, false, (short)indexInChunk);
    }

    internal static unsafe void AddCompRev(ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo, long tsn, ushort uowId, bool isDelete,
        bool lockAlreadyHeld = false)
    {
        ref var compRevTableAccessor = ref info.CompRevTableAccessor;
        var compContent = info.CompContentSegment;

        ref var firstHeader = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(compRevInfo.CompRevTableFirstChunkId, true);

        // Enter exclusive access for the Revision Table (skip if caller already holds the lock)
        if (!lockAlreadyHeld)
        {
            var wc = WaitContext.FromTimeout(TimeoutOptions.Current.RevisionChainLockTimeout);
            if (!firstHeader.Control.EnterExclusiveAccess(ref wc))
            {
                ThrowHelper.ThrowLockTimeout("RevisionChain/AddRevision", TimeoutOptions.Current.RevisionChainLockTimeout);
            }
        }

        try
        {
            // Check if we need to add one more chunk to the chain
            if (ComputeRevElementCount(firstHeader.ChainLength) == firstHeader.ItemCount)
            {
                GrowChain(info, compRevInfo.CompRevTableFirstChunkId, ref firstHeader);
            }

            // Add our new entry
            var newRevIndex = (short)(firstHeader.FirstItemIndex + firstHeader.ItemCount);
            var indexInChunk = GetRevisionLocation(ref compRevTableAccessor, compRevInfo.CompRevTableFirstChunkId, newRevIndex, out var curChunkId);

            Span<CompRevStorageElement> curChunkElements;

            // Still in the first chunk? The elements are right after the header
            if (compRevInfo.CompRevTableFirstChunkId == curChunkId)
            {
                curChunkElements = compRevTableAccessor.GetChunkAsSpan(compRevInfo.CompRevTableFirstChunkId, true)
                    .Slice(sizeof(CompRevStorageHeader)).Cast<byte, CompRevStorageElement>();
            }

            // In another chunk, the subsequent ones have a one int header (the ID of the next chunk in the chain), then the elements
            else
            {
                curChunkElements = compRevTableAccessor.GetChunkAsSpan(curChunkId, true).Slice(sizeof(int)).Cast<byte, CompRevStorageElement>();
            }

            // Allocate a new component
            var componentChunkId = isDelete ? 0 : compContent.AllocateChunk(false, info.CompContentAccessor.ChangeSet);

            // Add our new entry
            curChunkElements[indexInChunk].TSN = tsn;
            curChunkElements[indexInChunk].IsolationFlag = true;
            curChunkElements[indexInChunk].UowId = uowId;
            curChunkElements[indexInChunk].ComponentChunkId = componentChunkId;

            // Update the compRevInfo
            compRevInfo.PrevCompContentChunkId = compRevInfo.CurCompContentChunkId;
            compRevInfo.PrevRevisionIndex = compRevInfo.CurRevisionIndex;
            compRevInfo.CurCompContentChunkId = componentChunkId;
            compRevInfo.CurRevisionIndex = newRevIndex;

            // One more item, update the header
            firstHeader.ItemCount++;
        }
        finally
        {
            if (!lockAlreadyHeld)
            {
                firstHeader.Control.ExitExclusiveAccess();
            }
        }
    }

    internal static unsafe int AllocCompRevStorage(ComponentInfo info, long tsn, ushort uowId, int firstChunkId, long pk)
    {
        var chunkId = info.CompRevTableSegment.AllocateChunk(false, info.CompRevTableAccessor.ChangeSet);
        var chunkSpan = info.CompRevTableAccessor.GetChunkAsSpan(chunkId, true);

        ref var header = ref chunkSpan.Cast<byte, CompRevStorageHeader>()[0];

        // Initialize the header
        header.NextChunkId = 0;
        header.Control = default;
        header.FirstItemIndex = 0;
        header.ItemCount = 1;
        header.ChainLength = 1;
        header.LastCommitRevisionIndex = -1;
        header.CommitSequence = 1;
        header.EntityPK = pk;

        // Initialize the first element
        var elements = chunkSpan.Slice(sizeof(CompRevStorageHeader)).Cast<byte, CompRevStorageElement>();
        elements[0].TSN = tsn;
        elements[0].IsolationFlag = true;                                  // Isolate this revision from the rest of the database (other transactions)
        elements[0].UowId = uowId;
        elements[0].ComponentChunkId = firstChunkId;

        return chunkId;
    }

    /// <summary>
    /// Core cleanup logic for a component's revision chain. Removes all entries older than <paramref name="nextMinTSN"/>,
    /// releases unused component chunks, and defragments the remaining revisions.
    /// </summary>
    /// <param name="ct">The component table owning the entity</param>
    /// <param name="firstChunkId">The first chunk ID of the revision chain</param>
    /// <param name="nextMinTSN">The minimal TSN to keep revisions</param>
    /// <param name="compRevTableAccessor">Accessor for the revision table segment</param>
    /// <param name="compContentAccessor">Accessor for the component content segment</param>
    /// <returns><c>true</c> if the component is fully deleted (single tombstone remaining), <c>false</c> otherwise</returns>
    /// <remarks>
    /// <para>This method is transaction-agnostic and can be called from both the transaction commit path and the lazy cleanup path.</para>
    /// <para>A <b>sentinel</b> revision is preserved when the first kept entry has TSN &gt; nextMinTSN: this is the last committed
    /// revision before the cutoff, needed because active transactions at MinTSN may have cached its content chunk ID.</para>
    /// </remarks>
    internal static unsafe bool CleanUpUnusedEntriesCore(ComponentTable ct, int firstChunkId, long nextMinTSN,
        ref ChunkAccessor<PersistentStore> compRevTableAccessor, ref ChunkAccessor<PersistentStore> compContentAccessor,
        List<DeferredCleanupManager.DeferredChunkFreeEntry> deferredChunkFrees = null)
    {
        ref var firstChunkHeader = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId);

        // Phase 6: Data:MVCC:VersionCleanup span — covers the compaction work for one entity's revision chain.
        var versionCleanupScope = TyphonEvent.BeginDataMvccVersionCleanup(firstChunkHeader.EntityPK);

        // Create a temporary chunk to store the cleaned-up content of the first chunk (we can't overwrite the first chunk right away)
        Span<byte> tempChunk = stackalloc byte[CompRevChunkSize];
        tempChunk.Clear();
        tempChunk.Split(out Span<CompRevStorageHeader> tempFirstHeader, out Span<CompRevStorageElement> tempElements);
        tempFirstHeader[0].ChainLength = 1;
        tempFirstHeader[0].CommitSequence = firstChunkHeader.CommitSequence;
        tempFirstHeader[0].EntityPK = firstChunkHeader.EntityPK;
        var curNextChunkId = tempChunk.Slice(0, sizeof(int)).Cast<byte, int>();
        var curDestElements = tempElements;
        var curDestIndex = 0;
        var curDestIndexInChunk = 0;
        var skipCount = 0;
        var hasCollections = ct.HasCollections;

        // Collect chunk IDs to free AFTER enumeration completes (avoid use-after-free in circular buffer)
        // Maximum chunks we might need to free is ChainLength - 1 (we keep the first chunk)
        Span<int> chunksToFree = (firstChunkHeader.ChainLength < 128) ? stackalloc int[firstChunkHeader.ChainLength] : new int[firstChunkHeader.ChainLength];
        var chunksToFreeCount = 0;

        {
            using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, firstChunkId, false, true);
            var prevChunkId = enumerator.IndexInChunk == 0 ? enumerator.CurChunkId : 0;
            var maxSkipCount = firstChunkHeader.ItemCount;

            // Sentinel: delayed-free pattern — each newer committed skip candidate frees the previous one's chunk.
            // The final sentinel is either emitted to the compacted output or freed, depending on the first kept entry's TSN.
            CompRevStorageElement sentinelEntry = default;
            int sentinelCompChunkId = 0;
            bool hasSentinel = false;
            bool inSkipPhase = true;

            while (enumerator.MoveNext())
            {
                bool changedChunk = (enumerator.CurChunkId != prevChunkId) && (prevChunkId != 0);
                if (changedChunk)
                {
                    // Mark the previous revision table chunk for freeing if it's not the first chunk (which we keep and reuse)
                    // IMPORTANT: We defer freeing until after enumeration to avoid use-after-free in circular buffers
                    if (prevChunkId != firstChunkId)
                    {
                        chunksToFree[chunksToFreeCount++] = prevChunkId;
                    }
                    prevChunkId = enumerator.CurChunkId;
                }

                // Skip phase: remove entries older than the cutoff, track the last committed one as sentinel. Active uncommitted entries (IsolationFlag=true)
                // must NOT enter the skip phase — they belong to transactions that will commit later. Freeing them would corrupt their data.
                // IsolationFlag check is before maxSkipCount decrement to avoid wasting the guard count.
                if (inSkipPhase && !enumerator.Current.IsolationFlag && (--maxSkipCount > 0) && (enumerator.Current.TSN < nextMinTSN))
                {
                    var revChunkId = enumerator.Current.ComponentChunkId;
                    bool isCommitted = (enumerator.Current.TSN > 0);

                    if (isCommitted)
                    {
                        // Supersede previous sentinel: free/defer its chunk, promote current entry
                        if (hasSentinel)
                        {
                            DeferOrFreeContentChunk(ct, ref compContentAccessor, sentinelCompChunkId, hasCollections, deferredChunkFrees);
                        }
                        sentinelEntry = enumerator.Current;
                        sentinelCompChunkId = revChunkId;
                        hasSentinel = true;
                    }
                    else
                    {
                        // Voided entry (all fields zeroed from rollback): ComponentChunkId is 0, effectively a no-op.
                        DeferOrFreeContentChunk(ct, ref compContentAccessor, revChunkId, hasCollections, deferredChunkFrees);
                    }

                    enumerator.CurrentAsSpan.Clear();
                    skipCount++;
                    continue;
                }

                // Transition out of skip phase: resolve sentinel
                int newChunkId;
                if (inSkipPhase)
                {
                    inSkipPhase = false;
                    if (hasSentinel)
                    {
                        if (enumerator.Current.TSN >= nextMinTSN || enumerator.Current.IsolationFlag)
                        {
                            // Keep sentinel: the first kept entry is at or beyond the cutoff, OR is an active uncommitted entry (IsolationFlag=true) invisible
                            // to readers — the sentinel provides the read baseline for transactions at MinTSN.
                            curDestElements[curDestIndexInChunk++] = sentinelEntry;
                            tempFirstHeader[0].ItemCount++;
                            tempFirstHeader[0].LastCommitRevisionIndex = (short)curDestIndex;
                            curDestIndex++;
                            skipCount--; // Sentinel was counted as skipped but is being kept

                            if (curDestIndexInChunk == curDestElements.Length)
                            {
                                curDestIndexInChunk = 0;
                                tempFirstHeader[0].ChainLength++;

                                newChunkId = ct.CompRevTableSegment.AllocateChunk(false, compRevTableAccessor.ChangeSet);
                                curNextChunkId[0] = newChunkId;
                                var newChunkSpan = compRevTableAccessor.GetChunkAsSpan(newChunkId, true);
                                newChunkSpan.Split(out curNextChunkId, out curDestElements);
                            }
                        }
                        else
                        {
                            // Sentinel not needed: the first kept entry is visible to all remaining transactions
                            DeferOrFreeContentChunk(ct, ref compContentAccessor, sentinelCompChunkId, hasCollections, deferredChunkFrees);
                        }
                    }
                }

                // Copy entry to compacted output
                curDestElements[curDestIndexInChunk++] = enumerator.Current;
                tempFirstHeader[0].ItemCount++;
                if (!enumerator.Current.IsolationFlag)
                {
                    tempFirstHeader[0].LastCommitRevisionIndex = (short)curDestIndex;
                }
                curDestIndex++;

                if (curDestIndexInChunk == curDestElements.Length)
                {
                    curDestIndexInChunk = 0;
                    tempFirstHeader[0].ChainLength++;

                    newChunkId = ct.CompRevTableSegment.AllocateChunk(false, compRevTableAccessor.ChangeSet);
                    curNextChunkId[0] = newChunkId;
                    var newChunkSpan = compRevTableAccessor.GetChunkAsSpan(newChunkId, true);
                    newChunkSpan.Split(out curNextChunkId, out curDestElements);
                }
            }
        }

        // Now that enumeration is complete, free all the collected revision table chunks
        // This is done AFTER the enumerator is disposed to avoid use-after-free in circular buffers
        for (var i = 0; i < chunksToFreeCount; i++)
        {
            var chunkId = chunksToFree[i];
            if (hasCollections)
            {
                foreach (var kvp in ct.ComponentCollectionVSBSByOffset)
                {
                    var bufferId = compContentAccessor.GetChunkAsReadOnlySpan(chunkId).Slice(kvp.Key).Cast<byte, int>()[0];
                    var collAccessor = kvp.Value.Segment.CreateChunkAccessor();
                    kvp.Value.BufferRelease(bufferId, ref collAccessor);
                    collAccessor.Dispose();
                }
            }
            ct.CompRevTableSegment.FreeChunk(chunkId);
        }

        // Cleanup does NOT increment CommitSequence. CS represents the total number of commits to this entity, and cleanup removing old entries
        // doesn't constitute a new commit. The commit path's CurRevisionIndex re-validation (FindRevisionIndexByChunkId) handles stale indices
        // caused by chain compaction. Keeping CS stable across cleanup ensures the snapshot-isolated revision number formula
        // (CS - totalCommitted + visibleOrdinal) remains correct.

        // Copy the compacted data to the first chunk, but SKIP the Control field (offset 4, size 4)
        // to avoid zeroing it. The caller holds the exclusive lock and the Control must remain intact.
        var destSpan = compRevTableAccessor.GetChunkAsSpan(firstChunkId, true);
        var controlFieldEnd = sizeof(int) + sizeof(int); // NextChunkId (4) + Control (4) = 8
        tempChunk.Slice(0, sizeof(int)).CopyTo(destSpan.Slice(0, sizeof(int)));
        tempChunk.Slice(controlFieldEnd).CopyTo(destSpan.Slice(controlFieldEnd));
        firstChunkHeader = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId);

        // Phase 6: capture entries freed (== skipCount, capped at u16) before disposing the span.
        versionCleanupScope.EntriesFreed = (ushort)Math.Min(skipCount, ushort.MaxValue);
        versionCleanupScope.Dispose();

        // Is the component totally deleted? Return true, otherwise false
        return (tempFirstHeader[0].ItemCount == 1 && tempElements[0].ComponentChunkId == 0);
    }

    /// <summary>
    /// Either defers content chunk freeing (when <paramref name="deferredChunkFrees"/> is non-null) or frees immediately.
    /// The deferred path collects entries for later freeing when all referencing transactions have departed.
    /// The immediate path is used during the transaction commit path where the caller is the tail.
    /// </summary>
    private static void DeferOrFreeContentChunk(ComponentTable ct, ref ChunkAccessor<PersistentStore> compContentAccessor, int chunkId, bool hasCollections,
        List<DeferredCleanupManager.DeferredChunkFreeEntry> deferredChunkFrees)
    {
        if (chunkId == 0)
        {
            return;
        }

        if (deferredChunkFrees != null)
        {
            deferredChunkFrees.Add(new DeferredCleanupManager.DeferredChunkFreeEntry { Table = ct, ChunkId = chunkId });
            return;
        }

        FreeCompContentChunk(ct, ref compContentAccessor, chunkId, hasCollections);
    }

    /// <summary>
    /// Searches the revision chain for an entry matching the given identifiers and returns its absolute revision index. Used to recover from stale cached
    /// indices after chain compaction.
    /// </summary>
    /// <param name="compRevTableAccessor">Accessor for revision table chunks</param>
    /// <param name="firstChunkId">First chunk of the revision chain</param>
    /// <param name="componentChunkId">Content chunk ID to match (unique for non-delete entries)</param>
    /// <param name="tsn">Transaction TSN — used as discriminator for delete entries (ComponentChunkId = 0)</param>
    /// <returns>The absolute revision index if found; -1 otherwise.</returns>
    internal static short FindRevisionIndexByChunkId(ref ChunkAccessor<PersistentStore> compRevTableAccessor, int firstChunkId, int componentChunkId, long tsn = 0)
    {
        using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, firstChunkId, false, true);
        while (enumerator.MoveNext())
        {
            if (componentChunkId != 0)
            {
                // Non-delete: ComponentChunkId is unique per allocation — unambiguous match
                if (enumerator.Current.ComponentChunkId == componentChunkId)
                {
                    return (short)(enumerator.Header.FirstItemIndex + enumerator.RevisionIndex);
                }
            }
            else if (enumerator.Current.IsolationFlag && enumerator.Current.TSN == tsn)
            {
                // Delete entry: ComponentChunkId = 0, disambiguate by TSN + uncommitted flag
                return (short)(enumerator.Header.FirstItemIndex + enumerator.RevisionIndex);
            }
        }
        return -1;
    }

    /// <summary>
    /// Releases a component content chunk and its associated collection buffers.
    /// </summary>
    private static void FreeCompContentChunk(ComponentTable ct, ref ChunkAccessor<PersistentStore> compContentAccessor, int chunkId, bool hasCollections)
    {
        if (chunkId == 0)
        {
            return;
        }

        if (hasCollections)
        {
            foreach (var kvp in ct.ComponentCollectionVSBSByOffset)
            {
                // kvp.Key is the offset within the component struct; content chunks prefix it with ComponentOverhead
                // (Versioned = 0, SingleVersion = 8 for the entity PK). Cluster slots have no overhead.
                var bufferId = compContentAccessor.GetChunkAsReadOnlySpan(chunkId).Slice(ct.ComponentOverhead + kvp.Key).Cast<byte, int>()[0];
                var collAccessor = kvp.Value.Segment.CreateChunkAccessor();
                kvp.Value.BufferRelease(bufferId, ref collAccessor);
                collAccessor.Dispose();
            }
        }

        ct.ComponentSegment.FreeChunk(chunkId);
    }

    private static void GrowChain(ComponentInfo info, int firstChunkId, ref CompRevStorageHeader firstHeader)
    {
        ref var compRevTableAccessor = ref info.CompRevTableAccessor;
        var compRevTable = info.CompRevTableSegment;

        // Special case, the first revision is in the first chunk, we need to walk to the end of the chain and add a new chunk there
        if (firstHeader.FirstItemIndex < CompRevCountInRoot)
        {
            using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, firstChunkId, false, false);
            enumerator.StepToChunk(firstHeader.ChainLength - 1, false);         // Walk to the last chunk in the chain
            enumerator.NextChunkId = compRevTable.AllocateChunk(true, info.CompRevTableAccessor.ChangeSet); // Allocated, clear content to make sure the next chunk ID is 0, set as next
            compRevTableAccessor.DirtyChunk(enumerator.CurChunkId);
            firstHeader.ChainLength++;
        }
        else
        {
            // Locate the first index in the chain, we add a chunk just before it
            var (firstChunkInChain, firstItemIndexInChunk) = CompRevStorageHeader.GetRevisionLocation(firstHeader.FirstItemIndex);
            using var enumerator = new RevisionEnumerator(ref compRevTableAccessor, firstChunkId, false, false);
            enumerator.StepToChunk(firstChunkInChain-1, false);                 // In a circular buffer, the chunk before the first is the last one

            // Get the ID of the first chunk in the chain
            var firstChunkIndexInChain = enumerator.NextChunkId;

            // Add a new chunk after the last in the chain
            var newChunkId = compRevTable.AllocateChunk(true, info.CompRevTableAccessor.ChangeSet); // Clear content to make sure the next chunk ID is 0
            enumerator.NextChunkId = newChunkId;
            compRevTableAccessor.DirtyChunk(enumerator.CurChunkId);

            // Copy the elements from the first chunk to the new chunk
            var newChunkElements = compRevTableAccessor.GetChunkAsSpan(newChunkId, true).Slice(sizeof(int)).Cast<byte, CompRevStorageElement>();
            var firstChunkElements = compRevTableAccessor.GetChunkAsSpan(firstChunkIndexInChain, true).Slice(sizeof(int)).Cast<byte, CompRevStorageElement>();
            firstChunkElements.Slice(0, firstItemIndexInChunk).CopyTo(newChunkElements);

            firstHeader.ChainLength++;                                              // One more item in the chain
            firstHeader.FirstItemIndex += (short)CompRevCountInNext; // We added a chunk before, the first item index gets shifted
        }
        compRevTableAccessor.DirtyChunk(firstChunkId);
    }

    private static int ComputeRevElementCount(int chainLength) => CompRevCountInRoot + ((chainLength - 1) * CompRevCountInNext);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe short GetRevisionLocation(ref ChunkAccessor<PersistentStore> accessor, int firstChunkId, short revisionIndex, out int resChunkId)
    {
        if (revisionIndex < CompRevCountInRoot)
        {
            resChunkId = firstChunkId;
            return revisionIndex;
        }

        (int chunkIndexInChain, int indexInChunk) = CompRevStorageHeader.GetRevisionLocation(revisionIndex);

        // Walk through the linked list until we find the chunk that is our starting point
        var header = (CompRevStorageHeader*)accessor.GetChunkAddress(firstChunkId);
        resChunkId = header->NextChunkId;

        var first = header;
        var useLock = !first->Control.IsLockedByCurrentThread;
        if (useLock)
        {
            var wc = WaitContext.FromTimeout(TimeoutOptions.Current.RevisionChainLockTimeout);
            if (!first->Control.EnterSharedAccess(ref wc))
            {
                ThrowHelper.ThrowLockTimeout("RevisionChain/GetLocation", TimeoutOptions.Current.RevisionChainLockTimeout);
            }
        }
        while (--chunkIndexInChain != 0)
        {
            resChunkId = *(int*)accessor.GetChunkAddress(resChunkId);
        }
        if (useLock)
        {
            first->Control.ExitSharedAccess();
        }

        return (short)indexInChunk;
    }
}
