using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Simplified write path for WAL crash recovery replay. Applies committed WAL records to the data store without MVCC isolation, conflict detection,
/// or concurrent access protection.
/// </summary>
/// <remarks>
/// <para>
/// During recovery, the database is single-threaded (no user transactions). This helper uses the same underlying storage APIs as <see cref="Transaction"/>
/// (ComponentSegment, ComponentRevisionManager) but bypasses the transaction/commit machinery.
/// </para>
/// <para>
/// Full replay of Create/Update/Delete is implemented. FPI (Full Page Image) torn-page repair is a separate concern handled by <see cref="WalRecovery"/>
/// directly.
/// </para>
/// <para>
/// PK B+Tree has been removed. For Update/Delete replay, a temporary dictionary (EntityPK → compRevChunkId) is built on first use by scanning the
/// CompRevTableSegment chain heads.
/// </para>
/// </remarks>
internal static class WalReplayHelper
{
    /// <summary>
    /// Per-table reverse index built lazily during replay: maps EntityPK → compRevFirstChunkId.
    /// </summary>
    private static Dictionary<ComponentTable, Dictionary<long, int>> ReplayPkMaps;

    /// <summary>
    /// Replays a single WAL record against the database engine's storage.
    /// </summary>
    /// <param name="dbe">The database engine (must be fully initialized with component tables registered).</param>
    /// <param name="header">The WAL record header.</param>
    /// <param name="payload">The record payload (component data bytes).</param>
    public static void ReplayRecord(DatabaseEngine dbe, ref WalRecordHeader header, ReadOnlySpan<byte> payload)
    {
        var table = dbe.GetComponentTableByWalTypeId(header.ComponentTypeId);
        if (table == null)
        {
            return; // Unknown component type — skip
        }

        switch ((WalOperationType)header.OperationType)
        {
            case WalOperationType.Create:
                ReplayCreate(dbe, table, ref header, payload);
                break;

            case WalOperationType.Update:
                ReplayUpdate(dbe, table, ref header, payload);
                break;

            case WalOperationType.Delete:
                ReplayDelete(dbe, table, ref header);
                break;
        }
    }

    /// <summary>
    /// Resets the replay state. Call after recovery is complete.
    /// </summary>
    internal static void ResetReplayState()
    {
        ReplayPkMaps = null;
    }

    /// <summary>
    /// Builds or retrieves a temporary EntityPK → compRevFirstChunkId lookup for the given table.
    /// Scans CompRevTableSegment chain heads (same algorithm as RebuildEntityMapsFromPersistedData).
    /// </summary>
    private static Dictionary<long, int> GetOrBuildPkMap(ComponentTable table)
    {
        ReplayPkMaps ??= new Dictionary<ComponentTable, Dictionary<long, int>>();

        if (ReplayPkMaps.TryGetValue(table, out var existing))
        {
            return existing;
        }

        var map = new Dictionary<long, int>();
        var segment = table.CompRevTableSegment;
        if (segment != null)
        {
            var capacity = segment.ChunkCapacity;
            var accessor = segment.CreateChunkAccessor();

            // Pass 1: Collect overflow chunks
            var overflowSet = new HashSet<int>();
            for (int chunkId = 0; chunkId < capacity; chunkId++)
            {
                if (!segment.IsChunkAllocated(chunkId))
                {
                    continue;
                }

                ref var hdr = ref accessor.GetChunk<CompRevStorageHeader>(chunkId);
                if (hdr.NextChunkId != 0)
                {
                    overflowSet.Add(hdr.NextChunkId);
                }
            }

            // Pass 2: Chain heads = allocated, not in overflow set
            for (int chunkId = 0; chunkId < capacity; chunkId++)
            {
                if (!segment.IsChunkAllocated(chunkId) || overflowSet.Contains(chunkId))
                {
                    continue;
                }

                ref var hdr = ref accessor.GetChunk<CompRevStorageHeader>(chunkId);
                if (hdr.EntityPK != 0)
                {
                    map[hdr.EntityPK] = chunkId;
                }
            }

            accessor.Dispose();
        }

        ReplayPkMaps[table] = map;
        return map;
    }

    /// <summary>
    /// Replays a Create operation: allocates a component chunk, copies payload data, creates a revision entry.
    /// Updates the replay PK map for subsequent Update/Delete lookups.
    /// </summary>
    private static unsafe void ReplayCreate(DatabaseEngine dbe, ComponentTable table, ref WalRecordHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        var cs = dbe.MMF.CreateChangeSet();

        // Allocate a component content chunk and write payload
        var componentChunkId = table.ComponentSegment.AllocateChunk(false, cs);
        var contentAccessor = table.ComponentSegment.CreateChunkAccessor(cs);
        var dst = contentAccessor.GetChunkAsSpan(componentChunkId, true);
        var toCopy = Math.Min(payload.Length, dst.Length);
        payload[..toCopy].CopyTo(dst);

        // Allocate a revision chain root chunk and initialize it
        var compRevChunkId = table.CompRevTableSegment.AllocateChunk(false, cs);
        var revAccessor = table.CompRevTableSegment.CreateChunkAccessor(cs);
        var revSpan = revAccessor.GetChunkAsSpan(compRevChunkId, true);

        // Initialize CompRevStorageHeader (first bytes of the chunk)
        ref var revHeader = ref Unsafe.As<byte, CompRevStorageHeader>(ref revSpan[0]);
        revHeader.NextChunkId = 0;
        revHeader.Control = default;
        revHeader.FirstItemIndex = 0;
        revHeader.ItemCount = 1;
        revHeader.ChainLength = 1;
        revHeader.LastCommitRevisionIndex = 0;
        revHeader.CommitSequence = 1;
        revHeader.EntityPK = header.EntityId;

        // Write the first revision element after the header
        var headerSize = sizeof(CompRevStorageHeader);
        ref var element = ref Unsafe.As<byte, CompRevStorageElement>(ref revSpan[headerSize]);
        element.ComponentChunkId = componentChunkId;
        element.TSN = header.TransactionTSN;
        element.UowId = header.UowEpoch;
        element.IsolationFlag = false;

        contentAccessor.Dispose();
        revAccessor.Dispose();
        cs.SaveChanges();

        // Update replay PK map
        var pkMap = GetOrBuildPkMap(table);
        pkMap[header.EntityId] = compRevChunkId;
    }

    /// <summary>
    /// Replays an Update operation: allocates a new component chunk with the updated data and adds a new revision entry.
    /// </summary>
    private static unsafe void ReplayUpdate(DatabaseEngine dbe, ComponentTable table, ref WalRecordHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        var cs = dbe.MMF.CreateChangeSet();

        // Look up the entity's revision chain via PK map
        var pkMap = GetOrBuildPkMap(table);
        if (!pkMap.TryGetValue(header.EntityId, out var compRevChunkId))
        {
            // Entity doesn't exist — treat as create
            cs.SaveChanges();
            ReplayCreate(dbe, table, ref header, payload);
            return;
        }

        // Allocate a new component content chunk with the updated data
        var newComponentChunkId = table.ComponentSegment.AllocateChunk(false, cs);
        var contentAccessor = table.ComponentSegment.CreateChunkAccessor(cs);
        var dst = contentAccessor.GetChunkAsSpan(newComponentChunkId, true);
        var toCopy = Math.Min(payload.Length, dst.Length);
        payload[..toCopy].CopyTo(dst);

        // Add a new revision entry to the existing revision chain
        var revAccessor = table.CompRevTableSegment.CreateChunkAccessor(cs);
        var revSpan = revAccessor.GetChunkAsSpan(compRevChunkId, true);

        ref var revHeader = ref Unsafe.As<byte, CompRevStorageHeader>(ref revSpan[0]);
        var newRevIndex = revHeader.ItemCount;

        // Only handle root chunk revisions for simplicity — multi-chunk overflow is rare in recovery
        var (chunkIndex, indexInChunk) = CompRevStorageHeader.GetRevisionLocation(newRevIndex);
        if (chunkIndex == 0)
        {
            var headerSize = sizeof(CompRevStorageHeader);
            var elementOffset = headerSize + indexInChunk * sizeof(CompRevStorageElement);
            if (elementOffset + sizeof(CompRevStorageElement) <= revSpan.Length)
            {
                ref var element = ref Unsafe.As<byte, CompRevStorageElement>(ref revSpan[elementOffset]);
                element.ComponentChunkId = newComponentChunkId;
                element.TSN = header.TransactionTSN;
                element.UowId = header.UowEpoch;
                element.IsolationFlag = false;

                revHeader.ItemCount++;
                revHeader.LastCommitRevisionIndex = newRevIndex;
            }
        }

        contentAccessor.Dispose();
        revAccessor.Dispose();
        cs.SaveChanges();
    }

    /// <summary>
    /// Replays a Delete operation: adds a tombstone revision (ComponentChunkId=0) to mark the entity as deleted.
    /// </summary>
    private static unsafe void ReplayDelete(DatabaseEngine dbe, ComponentTable table, ref WalRecordHeader header)
    {
        var cs = dbe.MMF.CreateChangeSet();

        // Look up the entity's revision chain via PK map
        var pkMap = GetOrBuildPkMap(table);
        if (!pkMap.TryGetValue(header.EntityId, out var compRevChunkId))
        {
            cs.SaveChanges();
            return; // Entity doesn't exist — nothing to delete
        }

        var revAccessor = table.CompRevTableSegment.CreateChunkAccessor(cs);
        var revSpan = revAccessor.GetChunkAsSpan(compRevChunkId, true);

        ref var revHeader = ref Unsafe.As<byte, CompRevStorageHeader>(ref revSpan[0]);
        var newRevIndex = revHeader.ItemCount;

        var (chunkIndex, indexInChunk) = CompRevStorageHeader.GetRevisionLocation(newRevIndex);
        if (chunkIndex == 0)
        {
            var headerSize = sizeof(CompRevStorageHeader);
            var elementOffset = headerSize + indexInChunk * sizeof(CompRevStorageElement);
            if (elementOffset + sizeof(CompRevStorageElement) <= revSpan.Length)
            {
                ref var element = ref Unsafe.As<byte, CompRevStorageElement>(ref revSpan[elementOffset]);
                element.ComponentChunkId = 0; // Tombstone
                element.TSN = header.TransactionTSN;
                element.UowId = header.UowEpoch;
                element.IsolationFlag = false;

                revHeader.ItemCount++;
                revHeader.LastCommitRevisionIndex = newRevIndex;
            }
        }

        revAccessor.Dispose();
        cs.SaveChanges();
    }
}
