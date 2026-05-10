// unset

using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// Provides temporal (point-in-time) index lookup for AllowMultiple secondary indexes. Scans the TAIL version-history buffer to determine which chain IDs
/// were active for a given key at a specific target TSN.
/// </summary>
/// <remarks>
/// <para>
/// Current-state queries (HEAD path) are completely unchanged — this class is only invoked for historical/snapshot queries where the caller needs to see the
/// index state at an older TSN.
/// </para>
/// <para>
/// Algorithm: For each unique ChainId in the TAIL buffer, find the entry with the highest TSN that is &lt;= targetTSN. If that entry is Active, the ChainId
/// is included in the result. If it's a Tombstone (or no entry exists), it's excluded.
/// </para>
/// </remarks>
internal static unsafe class TemporalIndexQuery
{
    /// <summary>
    /// Returns chain IDs that were active for <paramref name="fieldValueAddr"/> at <paramref name="targetTSN"/>.
    /// </summary>
    /// <param name="ifi">The indexed field info (contains the B+Tree index).</param>
    /// <param name="fieldValueAddr">Pointer to the field value to look up in the B+Tree.</param>
    /// <param name="targetTSN">The snapshot TSN to query at.</param>
    /// <param name="tailVSBS">The TAIL VSBS containing version history entries.</param>
    /// <param name="changeSet">ChangeSet for page tracking.</param>
    /// <returns>List of chain IDs active at the target TSN (empty if none or key not found).</returns>
    internal static List<int> Query(IndexedFieldInfo ifi, byte* fieldValueAddr, long targetTSN, 
        VariableSizedBufferSegment<VersionedIndexEntry, PersistentStore> tailVSBS, ChangeSet changeSet)
    {
        var result = new List<int>(4);

        // Step 1: Look up the key in the B+Tree to get the HEAD buffer ID
        var accessor = ifi.PersistentIndex.Segment.CreateChunkAccessor(changeSet);
        var headResult = ifi.PersistentIndex.TryGet(fieldValueAddr, ref accessor);
        if (headResult.IsFailure)
        {
            accessor.Dispose();
            return result;
        }

        var headBufferId = headResult.Value;

        // Step 2: Read TailBufferId from HEAD root header's extra header
        var chunkAddr = accessor.GetChunkAddress(headBufferId);
        var tailBufferId = IndexBufferExtraHeader.FromChunkAddress(chunkAddr).TailBufferId;
        accessor.Dispose();

        // Step 3: If no TAIL buffer exists, fall back to HEAD (all current entries are valid)
        if (tailBufferId == 0)
        {
            return QueryHeadOnly(ifi, fieldValueAddr, changeSet);
        }

        // Step 4: Scan TAIL buffer entries, tracking the latest state per ChainId at targetTSN
        // Pre-sized for typical small chain counts to reduce heap pressure
        var chainStates = new Dictionary<int, (bool IsActive, long TSN)>(4);

        foreach (ref readonly var entry in tailVSBS.EnumerateBuffer(tailBufferId))
        {
            // Only consider entries at or before our target snapshot
            if (entry.TSN > targetTSN)
            {
                continue;
            }

            var chainId = entry.ChainId;
            if (chainStates.TryGetValue(chainId, out var existing))
            {
                // Keep the entry with the highest TSN <= targetTSN
                if (entry.TSN > existing.TSN)
                {
                    chainStates[chainId] = (entry.IsActive, entry.TSN);
                }
            }
            else
            {
                chainStates[chainId] = (entry.IsActive, entry.TSN);
            }
        }

        // Step 5: Collect ChainIds whose latest state is Active
        foreach (var kvp in chainStates)
        {
            if (kvp.Value.IsActive)
            {
                result.Add(kvp.Key);
            }
        }

        return result;
    }

    /// <summary>
    /// Fallback: when no TAIL buffer exists, return all current chain IDs from the HEAD buffer.
    /// </summary>
    private static List<int> QueryHeadOnly(IndexedFieldInfo ifi, byte* fieldValueAddr, ChangeSet changeSet)
    {
        var result = new List<int>(4);
        var accessor = ifi.PersistentIndex.Segment.CreateChunkAccessor(changeSet);
        using var bufferAccessor = ifi.PersistentIndex.TryGetMultiple(fieldValueAddr, ref accessor);
        accessor.Dispose();

        if (!bufferAccessor.IsValid)
        {
            return result;
        }

        do
        {
            var elements = bufferAccessor.Elements;
            for (int i = 0; i < elements.Length; i++)
            {
                result.Add(elements[i]);
            }
        } while (bufferAccessor.NextChunk());

        return result;
    }
}
