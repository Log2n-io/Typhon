// The per-ComponentTable index maintainer: the sole index maintainer for Versioned components in NON-cluster (pure-Versioned) archetypes. The old
// "LEGACY — will be removed after #168" header was wrong twice over — this file is live, and #168 is not what removes it. #666 does, once pure-Versioned
// archetypes own per-archetype trees. The cluster path inserts indexes directly in FinalizeSpawns (Transaction.ECS.cs).
//
// The TAIL version-history machinery this file used to drive was deleted by #666: TemporalIndexQuery and TailGarbageCollector had zero production
// callers, no public temporal API ever existed, and nothing pruned the TAIL — it was an unbounded write amplifier paid for on every AllowMultiple
// Versioned mutation.

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
                        // Compound MoveValue: atomic remove-from-old + insert-under-new in a single traversal.
                        *(int*)&cur[ifi.OffsetToIndexElementId] = index.MoveValue(&prev[ifi.OffsetToField], &cur[ifi.OffsetToField],
                            *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref accessor, out _, out _);
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
                index.RemoveValue(&prev[ifi.OffsetToField], *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref accessor);
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
    /// Batched overload of <see cref="UpdateIndices(long, ComponentInfo, ComponentInfo.CompRevInfo, int, ChangeSet, long)"/>: uses pre-created
    /// accessors to eliminate per-entity accessor create/dispose overhead. Caller owns accessor lifecycle.
    /// </summary>
    internal static void UpdateIndices(long pk, ComponentInfo info, ComponentInfo.CompRevInfo compRevInfo, int prevCompChunkId, ChangeSet changeSet, long tsn,
        ChunkAccessor<PersistentStore>[] indexAccessors)
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
                        *(int*)&cur[ifi.OffsetToIndexElementId] = index.MoveValue(&prev[ifi.OffsetToField], &cur[ifi.OffsetToField],
                            *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref indexAccessors[i], out _, out _);
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
    /// Batched overload of <see cref="RemoveSecondaryIndices(long, ComponentInfo, int, int, ChangeSet, long)"/>: uses pre-created accessors to
    /// eliminate per-entity accessor create/dispose overhead. Caller owns accessor lifecycle.
    /// </summary>
    internal static void RemoveSecondaryIndices(long pk, ComponentInfo info, int prevCompChunkId, int startChunkId, ChangeSet changeSet, long tsn,
        ChunkAccessor<PersistentStore>[] indexAccessors)
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
                index.RemoveValue(&prev[ifi.OffsetToField], *(int*)&prev[ifi.OffsetToIndexElementId], startChunkId, ref indexAccessors[i]);
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
            // pk is the full entity PK on this path (Versioned commit) — wrap it rather than widening the signature, since this
            // whole file is slated for deletion when the per-ComponentTable index goes (#666).
            reg.DeltaBuffer.TryAppend(EntityId.FromRaw(pk), beforeKey, afterKey, tsn, flags, reg.ComponentTag);
        }
    }
}
