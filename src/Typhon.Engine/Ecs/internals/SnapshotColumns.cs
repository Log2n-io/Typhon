using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// A cluster's Versioned component column read at a reader's snapshot (VIS-12): shared by the field queries' SoA scan (<c>EcsQuery.FillSnapshotColumn</c>)
/// and the snapshot walk (<see cref="SnapshotWalker"/>), so the two cannot answer differently.
/// </summary>
internal static unsafe class SnapshotColumns
{
    /// <summary>
    /// Fills <paramref name="column"/> — laid out as the cluster's HEAD column — with the value of <paramref name="compSlot"/>'s Versioned component each of
    /// <paramref name="slots"/> held at <paramref name="reader"/>'s snapshot. Returns the slots whose entity had a value there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per slot (#1247): only a slot whose change stamp is above the snapshot (<see cref="ArchetypeClusterState.ChangedSlotsAfter"/>) is read through its
    /// record and its chain; every other slot's last change is at or below the snapshot, so its HEAD bytes are its value there.
    /// </para>
    /// <para>
    /// The HEAD copy is validated after it, seqlock-style: an update folds its slot stamp before it copies its value into the HEAD, so a copy that read a value
    /// newer than the snapshot finds that slot's stamp above it on the re-read (behind a load barrier on arm64), and the slot is read at the snapshot after all.
    /// </para>
    /// </remarks>
    internal static ulong Fill(EntityAccessor reader, ArchetypeMetadata meta, ArchetypeEngineState engineState, ArchetypeClusterState clusterState,
        byte* clusterBase, int clusterChunkId, int compSlot, int compSize, ulong slots, byte* column, byte* record, ref ChunkAccessor<PersistentStore> mapAccessor)
    {
        var layout = clusterState.Layout;
        var txTsn = reader.TSN;
        var changed = clusterState.ChangedSlotsAfter(clusterChunkId, txTsn) & slots;
        var fromHead = slots & ~changed;
        if (fromHead != 0)
        {
            QueryPathProbe.SnapshotFillHook?.Invoke();
            Buffer.MemoryCopy(clusterBase + layout.ComponentOffset(compSlot), column, (long)compSize * layout.ClusterSize, (long)compSize * layout.ClusterSize);

            // The HEAD loads before the stamps' re-read; nothing on x64.
            Volatile.ReadBarrier();
            var late = clusterState.ChangedSlotsAfter(clusterChunkId, txTsn) & fromHead;
            fromHead &= ~late;
            changed |= late;
        }

        return fromHead | ReadSlots(reader, meta, engineState, layout, clusterBase, compSlot, compSize, changed, column, record, ref mapAccessor);
    }

    /// <summary>The point-read half of <see cref="Fill"/>: each of <paramref name="slots"/> read through its record and its chain at the snapshot.</summary>
    internal static ulong ReadSlots(EntityAccessor reader, ArchetypeMetadata meta, ArchetypeEngineState engineState, ArchetypeClusterInfo layout,
        byte* clusterBase, int compSlot, int compSize, ulong slots, byte* column, byte* record, ref ChunkAccessor<PersistentStore> mapAccessor)
    {
        if (slots == 0)
        {
            return 0;
        }

        QueryPathProbe.SnapshotSlotReads += System.Numerics.BitOperations.PopCount(slots);
        var present = 0UL;
        // The point reads go through the reader's accessors, which a back-pressure wait must not refresh the epoch under (#1230): a walk's refreshable depth
        // is set aside meanwhile.
        var refreshable = PagedMMF.RefreshableWaitDepth;
        PagedMMF.RefreshableWaitDepth = 0;
        try
        {
            while (slots != 0)
            {
                var slotIndex = System.Numerics.BitOperations.TrailingZeroCount(slots);
                slots &= slots - 1;
                var rawId = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
                if (ReadOne(reader, meta, engineState, compSlot, compSize, rawId, column + slotIndex * compSize, record, ref mapAccessor))
                {
                    present |= 1UL << slotIndex;
                }
            }
        }
        finally
        {
            PagedMMF.RefreshableWaitDepth = refreshable;
        }

        return present;
    }

    /// <summary>
    /// The value of the entity <paramref name="rawId"/>'s Versioned component at <paramref name="compSlot"/> at <paramref name="reader"/>'s snapshot, through
    /// its record's chain root, copied to <paramref name="destination"/>. False when the entity has no record or the snapshot holds no value for it.
    /// </summary>
    internal static bool ReadOne(EntityAccessor reader, ArchetypeMetadata meta, ArchetypeEngineState engineState, int compSlot, int compSize, long rawId,
        byte* destination, byte* record, ref ChunkAccessor<PersistentStore> mapAccessor)
    {
        if (rawId == 0 || !engineState.EntityMap.TryGet(EntityId.FromRaw(rawId).EntityKey, record, ref mapAccessor))
        {
            return false;
        }

        var versionedIndex = engineState.ClusterState.Layout.SlotToVersionedIndex[compSlot];
        var chainRoot = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, versionedIndex);
        return reader.TryCopyCommittedAtSnapshot(meta, compSlot, chainRoot, destination, compSize);
    }
}
