// LEGACY — will be removed after #168. Kept as reference for index maintenance patterns.
// ECS path inserts indexes directly in FinalizeSpawns (Transaction.ECS.cs).

using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

internal static unsafe class IndexMaintainer
{
    internal static void UpdateIndices(long pk, ComponentInfo info, ComponentInfo.CompRevInfo compRevInfo, int prevCompChunkId, ChangeSet changeSet, long tsn)
    {
        // If there's a previous revision, we need to update the indices if some indexed fields changed
        var startChunkId = compRevInfo.CompRevTableFirstChunkId;
        if (prevCompChunkId != 0)
        {
            var prev = info.CompContentAccessor.GetChunkAddress(prevCompChunkId);
            var cur = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId, true);
            var prevSpan = new Span<byte>(prev, info.ComponentTable.ComponentTotalSize);
            var curSpan = new Span<byte>(cur, info.ComponentTable.ComponentTotalSize);

            var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
            for (int i = 0; i < indexedFieldInfos.Length; i++)
            {
                ref var ifi = ref indexedFieldInfos[i];
                var index = ifi.PersistentIndex;

                // The update changed the field?
                if (prevSpan.Slice(ifi.OffsetToField, ifi.Size).SequenceEqual(curSpan.Slice(ifi.OffsetToField, ifi.Size)) == false)
                {
                    var accessor = index.Segment.CreateChunkAccessor(changeSet);
                    if (ifi.AllowMultiple)
                    {
                        var tailVSBS = info.ComponentTable.TailVSBS;

                        // Compound MoveValue: atomic remove-from-old + insert-under-new in a single traversal.
                        // With TAIL tracking, preserveEmptyBuffer keeps the old HEAD buffer alive for tombstone writes.
                        *(int*)&cur[ifi.OffsetToIndexElementId] = index.MoveValue(&prev[ifi.OffsetToField], &cur[ifi.OffsetToField],
                            *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref accessor,
                            out var oldHeadBufferId, out var newHeadBufferId, tailVSBS != null);

                        if (tailVSBS != null)
                        {
                            var tailAccessor = tailVSBS.Segment.CreateChunkAccessor(changeSet);

                            // Tombstone on old key's TAIL buffer (entity left this key)
                            if (oldHeadBufferId >= 0)
                            {
                                var oldTailBufferId = EnsureTailPopulated(oldHeadBufferId, tailVSBS,
                                    ref accessor, ref tailAccessor, info, includeChainId: startChunkId);
                                // Ensure this entity has an Active entry (may be missing if created after a prior backfill)
                                var creationTsn = LookupCreationTSN(startChunkId, info);
                                tailVSBS.AddElement(oldTailBufferId, VersionedIndexEntry.Active(startChunkId, creationTsn), ref tailAccessor);
                                tailVSBS.AddElement(oldTailBufferId, VersionedIndexEntry.Tombstone(startChunkId, tsn), ref tailAccessor);
                            }

                            // Active on new key's TAIL buffer (entity arrived at this key)
                            if (newHeadBufferId >= 0)
                            {
                                var newTailBufferId = EnsureTailPopulated(newHeadBufferId, tailVSBS,
                                    ref accessor, ref tailAccessor, info, startChunkId);
                                tailVSBS.AddElement(newTailBufferId, VersionedIndexEntry.Active(startChunkId, tsn), ref tailAccessor);
                            }

                            tailAccessor.Dispose();
                        }
                    }
                    else
                    {
                        // Unique index — compound Move for atomic single-traversal move
                        index.Move(&prev[ifi.OffsetToField], &cur[ifi.OffsetToField], startChunkId, ref accessor);
                    }
                    accessor.Dispose();

                    NotifyViews(info.ComponentTable, i, pk, tsn, prev + ifi.OffsetToField, cur + ifi.OffsetToField, ifi.Size, false, false);
                }
                else if (ifi.AllowMultiple)
                {
                    // Carry forward the elementId for unchanged AllowMultiple fields so that
                    // the new content chunk has valid buffer references for later removal (e.g., on delete).
                    *(int*)&cur[ifi.OffsetToIndexElementId] = *(int*)&prev[ifi.OffsetToIndexElementId];
                }
            }

            info.ComponentTable.MutationsSinceRebuild++;
        }

        // No previous revision, it means we're adding the first component revision, add the indices
        // But only if this is truly a new component (Created operation), not a resurrection (Updated operation with prevCompChunkId == 0)
        else if ((compRevInfo.Operations & ComponentInfo.OperationType.Created) == ComponentInfo.OperationType.Created)
        {
            var cur = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId, true);

            var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
            for (int i = 0; i < indexedFieldInfos.Length; i++)
            {
                ref var ifi = ref indexedFieldInfos[i];
                var index = ifi.PersistentIndex;

                var accessor = index.Segment.CreateChunkAccessor(changeSet);
                if (ifi.AllowMultiple)
                {
                    *(int*)&cur[ifi.OffsetToIndexElementId] = index.Add(&cur[ifi.OffsetToField], startChunkId, ref accessor, out _);
                    // TAIL write deferred to first mutation — see EnsureTailPopulated
                }
                else
                {
                    index.Add(&cur[ifi.OffsetToField], startChunkId, ref accessor);
                }
                accessor.Dispose();
            }

            // Notify views for all indexed fields on creation
            for (int i = 0; i < indexedFieldInfos.Length; i++)
            {
                ref var ifi = ref indexedFieldInfos[i];
                NotifyViews(info.ComponentTable, i, pk, tsn, null, cur + ifi.OffsetToField, ifi.Size, true, false);
            }

            info.ComponentTable.MutationsSinceRebuild++;
        }
    }

    internal static void RemoveSecondaryIndices(long pk, ComponentInfo info, int prevCompChunkId, int startChunkId, ChangeSet changeSet, long tsn)
    {
        var prev = info.CompContentAccessor.GetChunkAddress(prevCompChunkId);
        var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;

        // Notify views before B+Tree removal (prev pointer still valid)
        for (int i = 0; i < indexedFieldInfos.Length; i++)
        {
            ref var ifi = ref indexedFieldInfos[i];
            NotifyViews(info.ComponentTable, i, pk, tsn, prev + ifi.OffsetToField, null, ifi.Size, false, true);
        }

        for (int i = 0; i < indexedFieldInfos.Length; i++)
        {
            ref var ifi = ref indexedFieldInfos[i];
            var index = ifi.PersistentIndex;
            var accessor = index.Segment.CreateChunkAccessor(changeSet);
            if (ifi.AllowMultiple)
            {
                var tailVSBS = info.ComponentTable.TailVSBS;

                // When TAIL tracking is active, preserve the BTree key even if the HEAD buffer empties.
                // This keeps the TAIL version-history buffer reachable for temporal queries.
                index.RemoveValue(&prev[ifi.OffsetToField], *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref accessor, tailVSBS != null);

                // TAIL: backfill + Active + Tombstone for the deleted entity.
                // preserveEmptyBuffer keeps the key alive so TryGet succeeds after RemoveValue.
                if (tailVSBS != null)
                {
                    var tailAccessor = tailVSBS.Segment.CreateChunkAccessor(changeSet);
                    var headResult = index.TryGet(&prev[ifi.OffsetToField], ref accessor);
                    if (headResult.IsSuccess)
                    {
                        var tailBufId = EnsureTailPopulated(headResult.Value, tailVSBS,
                            ref accessor, ref tailAccessor, info, includeChainId: startChunkId);
                        var creationTsn = LookupCreationTSN(startChunkId, info);
                        tailVSBS.AddElement(tailBufId, VersionedIndexEntry.Active(startChunkId, creationTsn), ref tailAccessor);
                        tailVSBS.AddElement(tailBufId, VersionedIndexEntry.Tombstone(startChunkId, tsn), ref tailAccessor);
                    }
                    tailAccessor.Dispose();
                }
            }
            else
            {
                index.Remove(&prev[ifi.OffsetToField], out _, ref accessor);
            }
            accessor.Dispose();
        }

        info.ComponentTable.MutationsSinceRebuild++;
    }

    /// <summary>
    /// Batched overload of <see cref="UpdateIndices"/>: uses pre-created accessors to eliminate per-entity accessor create/dispose overhead.
    /// Caller owns accessor lifecycle.
    /// </summary>
    internal static void UpdateIndices(long pk, ComponentInfo info, ComponentInfo.CompRevInfo compRevInfo, int prevCompChunkId, ChangeSet changeSet, long tsn,
        ChunkAccessor<PersistentStore>[] indexAccessors, ref ChunkAccessor<PersistentStore> tailAccessor)
    {
        var startChunkId = compRevInfo.CompRevTableFirstChunkId;
        if (prevCompChunkId != 0)
        {
            var prev = info.CompContentAccessor.GetChunkAddress(prevCompChunkId);
            var cur = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId, true);
            var prevSpan = new Span<byte>(prev, info.ComponentTable.ComponentTotalSize);
            var curSpan = new Span<byte>(cur, info.ComponentTable.ComponentTotalSize);

            var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
            for (int i = 0; i < indexedFieldInfos.Length; i++)
            {
                ref var ifi = ref indexedFieldInfos[i];
                var index = ifi.PersistentIndex;

                if (prevSpan.Slice(ifi.OffsetToField, ifi.Size).SequenceEqual(curSpan.Slice(ifi.OffsetToField, ifi.Size)) == false)
                {
                    if (ifi.AllowMultiple)
                    {
                        var tailVSBS = info.ComponentTable.TailVSBS;

                        *(int*)&cur[ifi.OffsetToIndexElementId] = index.MoveValue(&prev[ifi.OffsetToField], &cur[ifi.OffsetToField],
                            *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref indexAccessors[i],
                            out var oldHeadBufferId, out var newHeadBufferId, tailVSBS != null);

                        if (tailVSBS != null)
                        {
                            if (oldHeadBufferId >= 0)
                            {
                                var oldTailBufferId = EnsureTailPopulated(oldHeadBufferId, tailVSBS,
                                    ref indexAccessors[i], ref tailAccessor, info, includeChainId: startChunkId);
                                var creationTsn = LookupCreationTSN(startChunkId, info);
                                tailVSBS.AddElement(oldTailBufferId, VersionedIndexEntry.Active(startChunkId, creationTsn), ref tailAccessor);
                                tailVSBS.AddElement(oldTailBufferId, VersionedIndexEntry.Tombstone(startChunkId, tsn), ref tailAccessor);
                            }

                            if (newHeadBufferId >= 0)
                            {
                                var newTailBufferId = EnsureTailPopulated(newHeadBufferId, tailVSBS,
                                    ref indexAccessors[i], ref tailAccessor, info, startChunkId);
                                tailVSBS.AddElement(newTailBufferId, VersionedIndexEntry.Active(startChunkId, tsn), ref tailAccessor);
                            }
                        }
                    }
                    else
                    {
                        index.Move(&prev[ifi.OffsetToField], &cur[ifi.OffsetToField], startChunkId, ref indexAccessors[i]);
                    }

                    NotifyViews(info.ComponentTable, i, pk, tsn, prev + ifi.OffsetToField, cur + ifi.OffsetToField, ifi.Size, false, false);
                }
                else if (ifi.AllowMultiple)
                {
                    *(int*)&cur[ifi.OffsetToIndexElementId] = *(int*)&prev[ifi.OffsetToIndexElementId];
                }
            }

            info.ComponentTable.MutationsSinceRebuild++;
        }
        else if ((compRevInfo.Operations & ComponentInfo.OperationType.Created) == ComponentInfo.OperationType.Created)
        {
            var cur = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId, true);

            var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
            for (int i = 0; i < indexedFieldInfos.Length; i++)
            {
                ref var ifi = ref indexedFieldInfos[i];
                var index = ifi.PersistentIndex;

                if (ifi.AllowMultiple)
                {
                    *(int*)&cur[ifi.OffsetToIndexElementId] = index.Add(&cur[ifi.OffsetToField], startChunkId, ref indexAccessors[i], out _);
                }
                else
                {
                    index.Add(&cur[ifi.OffsetToField], startChunkId, ref indexAccessors[i]);
                }
            }

            for (int i = 0; i < indexedFieldInfos.Length; i++)
            {
                ref var ifi = ref indexedFieldInfos[i];
                NotifyViews(info.ComponentTable, i, pk, tsn, null, cur + ifi.OffsetToField, ifi.Size, true, false);
            }

            info.ComponentTable.MutationsSinceRebuild++;
        }
    }

    /// <summary>
    /// Batched overload of <see cref="RemoveSecondaryIndices"/>: uses pre-created accessors to eliminate per-entity accessor create/dispose overhead.
    /// Caller owns accessor lifecycle.
    /// </summary>
    internal static void RemoveSecondaryIndices(long pk, ComponentInfo info, int prevCompChunkId, int startChunkId, ChangeSet changeSet, long tsn,
        ChunkAccessor<PersistentStore>[] indexAccessors, ref ChunkAccessor<PersistentStore> tailAccessor)
    {
        var prev = info.CompContentAccessor.GetChunkAddress(prevCompChunkId);
        var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;

        // Notify views before B+Tree removal (prev pointer still valid)
        for (int i = 0; i < indexedFieldInfos.Length; i++)
        {
            ref var ifi = ref indexedFieldInfos[i];
            NotifyViews(info.ComponentTable, i, pk, tsn, prev + ifi.OffsetToField, null, ifi.Size, false, true);
        }

        for (int i = 0; i < indexedFieldInfos.Length; i++)
        {
            ref var ifi = ref indexedFieldInfos[i];
            var index = ifi.PersistentIndex;
            if (ifi.AllowMultiple)
            {
                var tailVSBS = info.ComponentTable.TailVSBS;

                index.RemoveValue(&prev[ifi.OffsetToField], *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref indexAccessors[i], tailVSBS != null);

                if (tailVSBS != null)
                {
                    var headResult = index.TryGet(&prev[ifi.OffsetToField], ref indexAccessors[i]);
                    if (headResult.IsSuccess)
                    {
                        var tailBufId = EnsureTailPopulated(headResult.Value, tailVSBS,
                            ref indexAccessors[i], ref tailAccessor, info, includeChainId: startChunkId);
                        var creationTsn = LookupCreationTSN(startChunkId, info);
                        tailVSBS.AddElement(tailBufId, VersionedIndexEntry.Active(startChunkId, creationTsn), ref tailAccessor);
                        tailVSBS.AddElement(tailBufId, VersionedIndexEntry.Tombstone(startChunkId, tsn), ref tailAccessor);
                    }
                }
            }
            else
            {
                index.Remove(&prev[ifi.OffsetToField], out _, ref indexAccessors[i]);
            }
        }

        info.ComponentTable.MutationsSinceRebuild++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NotifyViews(ComponentTable table, int fieldIndex, long pk, long tsn, byte* beforeFieldPtr, byte* afterFieldPtr, int fieldSize,
        bool isCreation, bool isDeletion)
    {
        var views = table.ViewRegistry.GetViewsForField(fieldIndex);
        if (views.Length == 0)
        {
            return;
        }

        var beforeKey = beforeFieldPtr != null ? KeyBytes8.FromPointer(beforeFieldPtr, fieldSize) : default;
        var afterKey = afterFieldPtr != null ? KeyBytes8.FromPointer(afterFieldPtr, fieldSize) : default;

        // Pack flags: [7]=isDeletion, [6]=isCreation, [5:0]=fieldIndex & 0x3F
        var flags = (byte)((fieldIndex & 0x3F) | (isCreation ? 0x40 : 0) | (isDeletion ? 0x80 : 0));

        for (int v = 0; v < views.Length; v++)
        {
            var reg = views[v];
            if (reg.View.IsDisposed)
            {
                continue;
            }
            reg.DeltaBuffer.TryAppend(pk, beforeKey, afterKey, tsn, flags, reg.ComponentTag);
        }
    }

    /// <summary>
    /// Ensures the TAIL version-history buffer is populated for the given HEAD buffer.
    /// On first call (TailBufferId == 0), locks the HEAD buffer, scans all HEAD entries,
    /// and backfills Active entries to a newly allocated TAIL buffer.
    /// Subsequent calls return the existing TailBufferId immediately (fast path).
    /// </summary>
    /// <param name="headBufferId">Root chunk ID of the HEAD buffer.</param>
    /// <param name="tailVSBS">The TAIL VSBS for VersionedIndexEntry storage.</param>
    /// <param name="headAccessor">ChunkAccessor<PersistentStore> for the BTree's segment (HEAD buffer lives here).</param>
    /// <param name="tailAccessor">ChunkAccessor<PersistentStore> for the TailIndexSegment.</param>
    /// <param name="info">ComponentInfo for looking up creation TSNs via the revision chain.</param>
    /// <param name="excludeChainId">Chain ID to skip during backfill (entity just arrived at this key via MoveValue).</param>
    /// <param name="includeChainId">Chain ID to include even though it's been removed from HEAD (entity just left via MoveValue/RemoveValue).</param>
    /// <returns>The TAIL buffer ID (existing or newly allocated and backfilled).</returns>
    private static int EnsureTailPopulated(int headBufferId, VariableSizedBufferSegment<VersionedIndexEntry, PersistentStore> tailVSBS, ref ChunkAccessor<PersistentStore> headAccessor, 
        ref ChunkAccessor<PersistentStore> tailAccessor, ComponentInfo info, int excludeChainId = 0, int includeChainId = 0)
    {
        // Fast path: TAIL already exists
        ref var extra = ref IndexBufferExtraHeader.FromChunkAddress(headAccessor.GetChunkAddress(headBufferId));
        if (extra.TailBufferId != 0)
        {
            return extra.TailBufferId;
        }

        // Slow path: lock HEAD buffer, double-check, backfill
        ref var rh = ref headAccessor.GetChunk<VariableSizedBufferRootHeader>(headBufferId, true);
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.SegmentAllocationLockTimeout);
        if (!rh.Lock.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("IndexMaintainer/EnsureTailPopulated", TimeoutOptions.Current.SegmentAllocationLockTimeout);
        }

        try
        {
            // Re-read after lock (another thread may have populated)
            extra = ref IndexBufferExtraHeader.FromChunkAddress(headAccessor.GetChunkAddress(headBufferId, true));
            if (extra.TailBufferId != 0)
            {
                return extra.TailBufferId;
            }

            // Allocate TAIL buffer
            var tailBufferId = tailVSBS.AllocateBuffer(ref tailAccessor);

            // Backfill: scan HEAD entries, write Active for each to TAIL
            BackfillHeadEntriesToTail(headBufferId, tailBufferId, tailVSBS, ref headAccessor, ref tailAccessor, info, excludeChainId, includeChainId);

            // Publish (visible after lock release)
            extra = ref IndexBufferExtraHeader.FromChunkAddress(headAccessor.GetChunkAddress(headBufferId, true));
            extra.TailBufferId = tailBufferId;
            return tailBufferId;
        }
        finally
        {
            rh = ref headAccessor.GetChunk<VariableSizedBufferRootHeader>(headBufferId, true);
            rh.Lock.ExitExclusiveAccess();
        }
    }

    /// <summary>
    /// Scans all entries in the HEAD buffer and writes corresponding Active entries to the TAIL buffer.
    /// Called once per key on first mutation to populate the TAIL version history.
    /// </summary>
    private static void BackfillHeadEntriesToTail(int headBufferId, int tailBufferId, VariableSizedBufferSegment<VersionedIndexEntry, PersistentStore> tailVSBS, 
        ref ChunkAccessor<PersistentStore> headAccessor, ref ChunkAccessor<PersistentStore> tailAccessor, ComponentInfo info, int excludeChainId, int includeChainId)
    {
        int rootHeaderTotalSize = sizeof(VariableSizedBufferRootHeader) + sizeof(IndexBufferExtraHeader);

        // Read root header to get the stored chunk chain start
        ref var rh = ref headAccessor.GetChunk<VariableSizedBufferRootHeader>(headBufferId);
        var curChunkId = rh.FirstStoredChunkId;

        while (curChunkId != 0)
        {
            var chunkAddr = headAccessor.GetChunkAddress(curChunkId);
            ref var chunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(chunkAddr);
            var elementCount = chunkHeader.ElementCount;
            // Read NextChunkId to local before any further accessor calls that might evict this slot
            var nextChunkId = chunkHeader.NextChunkId;
            var isRoot = (curChunkId == headBufferId);
            var offset = isRoot ? rootHeaderTotalSize : sizeof(VariableSizedBufferChunkHeader);
            var elements = new Span<int>(chunkAddr + offset, elementCount);

            for (int i = 0; i < elements.Length; i++)
            {
                var chainId = elements[i];
                if (chainId == excludeChainId)
                {
                    continue;
                }
                var creationTsn = LookupCreationTSN(chainId, info);
                tailVSBS.AddElement(tailBufferId, VersionedIndexEntry.Active(chainId, creationTsn), ref tailAccessor);
            }

            curChunkId = nextChunkId;
        }

        // Include entry that was already removed from HEAD (by MoveValue/RemoveValue)
        if (includeChainId > 0)
        {
            var creationTsn = LookupCreationTSN(includeChainId, info);
            tailVSBS.AddElement(tailBufferId, VersionedIndexEntry.Active(includeChainId, creationTsn), ref tailAccessor);
        }
    }

    /// <summary>
    /// Recovers the creation TSN for a given revision chain by reading the oldest surviving revision element.
    /// Returns 0 (sentinel) if the chain has been fully cleaned up, meaning "active since before recorded history".
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long LookupCreationTSN(int startChunkId, ComponentInfo info)
    {
        ref var header = ref info.CompRevTableAccessor.GetChunk<CompRevStorageHeader>(startChunkId);
        if (header.ItemCount == 0)
        {
            return 0;
        }
        var element = ComponentRevisionManager.GetRevisionElement(ref info.CompRevTableAccessor, startChunkId, header.FirstItemIndex);
        return element.Element.TSN;
    }
}
