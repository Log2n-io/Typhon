// unset

using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// Garbage collector for TAIL version-history buffers. Removes entries that are no longer needed for snapshot isolation because all active
/// transactions have TSN > retentionTSN.
/// </summary>
/// <remarks>
/// <para>
/// For each ChainId, the pruning algorithm keeps one "boundary sentinel" — the entry with the highest TSN &lt;= retentionTSN. This sentinel is needed because
/// a reader at exactly retentionTSN must still be able to determine the state of that chain.
/// </para>
/// <para>
/// A ChainId is fully removed (including the sentinel) when:
/// 1. The sentinel is a Tombstone (entity is gone at the retention point), AND
/// 2. No entries with TSN &gt; retentionTSN exist for that ChainId (no future state to preserve).
/// </para>
/// <para>
/// Integration: Hook into deferred cleanup flow when MinTSN advances (requires #46/#47).
/// </para>
/// <para>
/// <b>Zombie BTree key cleanup:</b> After Prune empties a TAIL buffer, if the HEAD buffer for that key is also empty (due to preserveEmptyBuffer), the BTree
/// key is a zombie that serves no purpose. The caller (GC integration layer) should detect this case and remove the BTree key entirely using
/// <c>BTreeBase.Remove</c>. This is NOT handled inside Prune because it requires access to the BTree, which is outside the TAIL segment's scope.
/// </para>
/// </remarks>
internal static class TailGarbageCollector
{
    /// <summary>
    /// Prune TAIL entries older than <paramref name="retentionTSN"/>, keeping one boundary sentinel per ChainId.
    /// </summary>
    /// <param name="tailVSBS">The TAIL VSBS containing version history entries.</param>
    /// <param name="tailBufferId">The root chunk ID of the TAIL buffer to prune.</param>
    /// <param name="retentionTSN">Entries with TSN below this may be pruned (oldest active transaction TSN).</param>
    /// <param name="accessor"><see cref="ChunkAccessor{PersistentStore}"/> for the TAIL index segment.</param>
    /// <param name="newTailBufferId">Return the ID of the new TAIL VSBS buffer</param>
    /// <returns>Number of entries removed.</returns>
    internal static int Prune(VariableSizedBufferSegment<VersionedIndexEntry, PersistentStore> tailVSBS, int tailBufferId, long retentionTSN, 
        ref ChunkAccessor<PersistentStore> accessor, out int newTailBufferId)
    {
        // Phase 1: Scan all entries and group by ChainId
        // For each ChainId, track: boundary sentinel (highest TSN <= retentionTSN),
        // older entries (to discard), and whether future entries exist (TSN > retentionTSN).
        var chainData = new Dictionary<int, ChainPruneState>();

        var allEntries = new List<VersionedIndexEntry>();
        foreach (ref readonly var entry in tailVSBS.EnumerateBuffer(tailBufferId))
        {
            allEntries.Add(entry);

            var chainId = entry.ChainId;
            if (!chainData.TryGetValue(chainId, out var state))
            {
                state = new ChainPruneState();
                chainData[chainId] = state;
            }

            if (entry.TSN > retentionTSN)
            {
                state.HasFutureEntries = true;
            }
            else if (state.BoundarySentinel == null || entry.TSN > state.BoundarySentinel.Value.TSN)
            {
                // This entry becomes the new boundary sentinel; previous sentinel (if any) is older and can be discarded
                state.BoundarySentinel = entry;
            }
            else
            {
                // Entry is older than current boundary sentinel — will be discarded in Phase 2
            }
        }

        // Phase 2: Determine which entries to keep
        var keptEntries = new List<VersionedIndexEntry>();
        int removedCount = 0;

        foreach (var entry in allEntries)
        {
            var chainId = entry.ChainId;
            var state = chainData[chainId];

            if (entry.TSN > retentionTSN)
            {
                // Future entry: always keep
                keptEntries.Add(entry);
                continue;
            }

            // Is this the boundary sentinel?
            if (state.BoundarySentinel != null && entry.TSN == state.BoundarySentinel.Value.TSN
                && entry.SignedChainId == state.BoundarySentinel.Value.SignedChainId)
            {
                // Keep the sentinel UNLESS it's a tombstone with no future entries
                if (state.BoundarySentinel.Value.IsTombstone && !state.HasFutureEntries)
                {
                    removedCount++;
                }
                else
                {
                    keptEntries.Add(entry);
                }
            }
            else
            {
                // Older than boundary sentinel: discard
                removedCount++;
            }
        }

        if (removedCount == 0)
        {
            newTailBufferId = tailBufferId;
            return 0;
        }

        // Phase 3: Compact — delete the old buffer and rewrite with kept entries only.
        // IMPORTANT: The new buffer ID will differ from the old one. The caller must update// the HEAD root header's TailBufferId to point to the new buffer.
        tailVSBS.DeleteBuffer(tailBufferId, ref accessor);

        newTailBufferId = tailVSBS.AllocateBuffer(ref accessor);
        foreach (var entry in keptEntries)
        {
            tailVSBS.AddElement(newTailBufferId, entry, ref accessor);
        }

        return removedCount;
    }

    private class ChainPruneState
    {
        public VersionedIndexEntry? BoundarySentinel;
        public bool HasFutureEntries;
    }
}
