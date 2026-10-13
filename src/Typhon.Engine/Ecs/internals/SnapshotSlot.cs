namespace Typhon.Engine.Internals;

/// <summary>
/// The snapshot tests every index and scan path applies to a cluster slot it would emit: born and died against the reader's snapshot (VIS-08, VIS-09),
/// for an occupied slot and for one claimed and not occupied. Shared by the query scans and the ordered merge's streams (#1347).
/// </summary>
internal static unsafe class SnapshotSlot
{
    /// <summary>
    /// Born/died visibility for one entity emitted by the cluster SoA scan, resolved from its EntityMap record header — the same
    /// <c>BornTSN</c>/<c>DiedTSN</c> test the chain-walking paths apply (<c>BroadScanAction.Process</c>, <c>MatchAction.Matches</c>).
    /// </summary>
    /// <remarks>
    /// Returns true unconditionally when the archetype is not Versioned: <c>SingleVersion</c> and <c>Transient</c> are non-MVCC and
    /// promise no isolation, so gating them would cost a hash lookup per match to enforce a guarantee they do not make. When the record
    /// cannot be read the entity is treated as visible — the scan already proved it occupied, and dropping it would silently shrink the
    /// result, the failure mode this whole migration exists to remove (#663).
    /// </remarks>
    internal static bool IsVisibleAtSnapshot(long rawId, bool visGated, ArchetypeEngineState visState, byte* visBuf, int visRecordSize, long txTsn,
        CommitCardTable cards, ref ChunkAccessor<PersistentStore> visAccessor)
    {
        if (!visGated || visState == null)
        {
            return true;
        }

        var key = EntityId.FromRaw(rawId).EntityKey;
        if (!visState.EntityMap.TryGet(key, visBuf, ref visAccessor))
        {
            return true;
        }

        // Born after this snapshot — the phantom the fixed snapshot must hide — or died at or before it; a pending born through its owner's card (VIS-08).
        return EntityVisibility.IsVisibleInScan(visBuf, txTsn, cards, visState.EntityMap, key);
    }

    /// <summary>Whether the component at <paramref name="entityComp"/> passes every one of <paramref name="evaluators"/>.</summary>
    internal static bool MatchesAll(FieldEvaluator[] evaluators, byte* entityComp)
    {
        for (var e = 0; e < evaluators.Length; e++)
        {
            if (!FieldEvaluator.Evaluate(ref evaluators[e], entityComp + evaluators[e].FieldOffset))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A slot claimed and not occupied — a spawn placed in PREPARE and not yet published (VIS-08), or a destroyed entity hidden until the cleanup (VIS-09):
    /// visible only through a record that exists, names this slot, and is visible at the snapshot, a pending born or died resolved through its owner's card.
    /// Unlike an occupied slot, a reserved one with no record yet is a spawn still being placed.
    /// </summary>
    internal static bool IsReservedSpawnVisible(long rawId, int clusterChunkId, int slotIndex, ArchetypeEngineState visState, byte* visBuf, long txTsn,
        CommitCardTable cards, ref ChunkAccessor<PersistentStore> visAccessor)
    {
        if (rawId == 0 || visState == null)
        {
            return false;
        }

        // The record must name THIS slot: a migration's destination is claimed and unoccupied while its source still holds the entity, and the record still
        // points at the source, so the entity is found there and not twice.
        var key = EntityId.FromRaw(rawId).EntityKey;
        return visState.EntityMap.TryGet(key, visBuf, ref visAccessor)
               && ClusterEntityRecordAccessor.GetClusterChunkId(visBuf) == clusterChunkId && ClusterEntityRecordAccessor.GetSlotIndex(visBuf) == slotIndex
               && EntityVisibility.IsVisibleInScan(visBuf, txTsn, cards, visState.EntityMap, key);
    }
}
