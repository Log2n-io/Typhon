using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Tracks per-UoW state during WAL scan.
/// </summary>
internal class UowScanState
{
    public bool HasBegin;
    public bool HasCommit;
    public List<(WalRecordHeader Header, byte[] Payload)> Records = [];
}

/// <summary>
/// Collected TickFence chunk data during WAL scan. One entry per chunk (one chunk per SV ComponentTable per tick).
/// </summary>
internal class TickFenceScanEntry
{
    public long LSN;
    public long TickNumber;
    public ushort ComponentTypeId;
    public ushort PayloadStride;
    public List<(int ChunkId, byte[] ComponentData)> Entries;
}

/// <summary>
/// Collected ClusterTickFence chunk data during WAL scan. One entry per chunk (one chunk per cluster-eligible archetype per tick).
/// </summary>
internal class ClusterTickFenceScanEntry
{
    public long LSN;
    public ushort ArchetypeId;
    public List<(int EntityIndex, byte[] AllComponentData)> Entries;
}

/// <summary>
/// Orchestrates WAL crash recovery: scans WAL segments, identifies committed UoWs,
/// voids pending ones, and replays committed records to restore data consistency.
/// </summary>
internal sealed class WalRecovery : IDisposable
{
    private readonly IWalFileIO _fileIO;
    private readonly string _walDirectory;
    private readonly PagedMMF _mmf;

    public WalRecovery(IWalFileIO fileIO, string walDirectory, PagedMMF mmf = null)
    {
        ArgumentNullException.ThrowIfNull(fileIO);
        ArgumentNullException.ThrowIfNull(walDirectory);
        _fileIO = fileIO;
        _walDirectory = walDirectory;
        _mmf = mmf;
    }

    /// <summary>
    /// Runs the in-ctor recovery scan (v1 path, being retired):
    /// Phase 1 — Discover segments, Phase 2 — Scan records + collect TickFences, Phase 3 — Cross-reference with registry,
    /// Phase 6 — Replay TickFence entries (SV recovery), Phase 7 — Finalize. (Phase 4 FPI repair + Phase 5 committed-replay were deleted: FPI is retired — the
    /// rebuild net heals torn pages — and the WAL v2 RecoveryDriver owns record apply.)
    /// </summary>
    /// <param name="registry">The UoW registry (loaded via <see cref="UowRegistry.LoadFromDiskRaw"/>).</param>
    /// <param name="checkpointLSN">LSN up to which data is already checkpointed. 0 = scan all.</param>
    /// <param name="dbe">The database engine for record replay.</param>
    /// <returns>Recovery statistics.</returns>
    public WalRecoveryResult Recover(UowRegistry registry, long checkpointLSN, DatabaseEngine dbe)
    {
        var startTicks = Stopwatch.GetTimestamp();
        var result = new WalRecoveryResult();

        // Phase 8: Recovery:Start instant — fires once at recovery entry. Reason byte: 0 = normal startup recovery.
        TyphonEvent.EmitDurabilityRecoveryStart(checkpointLSN, 0);

        // ═══════════════════════════════════════════════════════════
        // Phase 1: Discover segments
        // ═══════════════════════════════════════════════════════════

        // Phase 8: Recovery:Discover span — covers segment enumeration. Stats filled in after the call.
        List<string> segmentPaths;
        var discoverScope = TyphonEvent.BeginDurabilityRecoveryDiscover(0, 0, 0);
        try
        {
            segmentPaths = DiscoverSegments();
            discoverScope.SegCount = segmentPaths.Count;
            // TotalBytes/FirstSegId left at 0 — DiscoverSegments doesn't currently expose those; fillable in a follow-up.
        }
        finally
        {
            discoverScope.Dispose();
        }

        if (segmentPaths.Count == 0)
        {
            // No WAL segments — void all remaining Pending entries
            registry.VoidRemainingPending();
            result.UowsVoided = registry.VoidEntryCount;
            result.ElapsedMicroseconds = ElapsedUs(startTicks);
            return result;
        }

        // ═══════════════════════════════════════════════════════════
        // Phase 2: Scan records, build committed set + collect TickFences
        // ═══════════════════════════════════════════════════════════

        var uowStates = new Dictionary<ushort, UowScanState>();
        var tickFenceEntries = new List<TickFenceScanEntry>();
        var clusterTickFenceEntries = new List<ClusterTickFenceScanEntry>();

        using var reader = new WalSegmentReader(_fileIO);

        var segIndex = 0;
        foreach (var segmentPath in segmentPaths)
        {
            if (!reader.OpenSegment(segmentPath))
            {
                segIndex++;
                continue; // Invalid segment header — skip
            }

            result.SegmentsScanned++;

            // Phase 8: Recovery:Segment span — per-segment scan. RecCount/Bytes filled at the end of each segment.
            var segScope = TyphonEvent.BeginDurabilityRecoverySegment(segIndex);
            try
            {
                var beforeRecords = result.RecordsScanned;
                while (reader.TryReadNext(out var chunkHeader, out var body))
                {
                    result.RecordsScanned++;

                    switch ((WalChunkType)chunkHeader.ChunkType)
                    {
                        case WalChunkType.Transaction:
                            ProcessTransactionChunk(uowStates, body, checkpointLSN);
                            break;

                        case WalChunkType.TickFence:
                            CollectTickFenceChunk(tickFenceEntries, body);
                            break;

                        case WalChunkType.ClusterTickFence:
                            CollectClusterTickFenceChunk(clusterTickFenceEntries, body);
                            break;

                        case WalChunkType.BulkBegin:
                            // P3 (v1): no Phase-3b action — visibility correctness is provided by the standard UowRegistry void path. A BulkBegin without a
                            // matching durable BulkEnd means the bulk's UoW remains Pending in the registry; the existing VoidRemainingPending step voids it
                            // (UR-03 / UR-05), making the bulk's revisions invisible via CommittedBeforeTSN=0 + bitmap fallback. P3 future: scan into a
                            // session-keyed map for explicit page-free in Phase 3b (deferred — see claude/design/Durability/BulkLoad/03-recovery.md).
                            result.BulkBeginCount++;
                            break;

                        case WalChunkType.BulkEnd:
                            // P3 (v1): no Phase-3b action. Standard recovery already promotes the bulk's UoW to WalDurable via the per-row machinery (no
                            // per-row records exist for bulk, but the UowRegistry slot was transitioned via UoW.Flush during CompleteBulkLoad).
                            // See above for the future Phase-3b note.
                            result.BulkEndCount++;
                            break;
                    }
                }
                segScope.RecCount = result.RecordsScanned - beforeRecords;
                segScope.Truncated = (byte)(reader.WasTruncated ? 1 : 0);
            }
            finally
            {
                segScope.Dispose();
            }

            if (reader.WasTruncated)
            {
                break; // Stop at truncation point
            }
            segIndex++;
        }

        result.LastValidLSN = reader.LastValidLSN;

        // ═══════════════════════════════════════════════════════════
        // Phase 3: Cross-reference with registry
        // ═══════════════════════════════════════════════════════════

        foreach (var kvp in uowStates)
        {
            if (kvp.Value.HasCommit)
            {
                registry.PromoteToWalDurable(kvp.Key);
                result.UowsPromoted++;
            }
        }

        // Phase 8: Recovery:Undo span — covers voiding pending entries.
        var undoScope = TyphonEvent.BeginDurabilityRecoveryUndo(0);
        try
        {
            // Void all remaining Pending entries
            var voidCountBefore = registry.VoidEntryCount;
            registry.VoidRemainingPending();
            result.UowsVoided = registry.VoidEntryCount - voidCountBefore;
            undoScope.VoidedUowCount = result.UowsVoided;
        }
        finally
        {
            undoScope.Dispose();
        }

        // Phase 4 (FPI torn-page repair) deleted in increment D — the rebuild net replaces FPI: derived structures rebuild (RB-01), occupancy re-derives (CK-09),
        // and a torn primary page heals-by-apply or fails the open loudly via suspect resolution (RB-04, DatabaseEngine.ResolveSuspectPrimaryPages).

        // Phase 5 (v1 committed-transaction replay via WalReplayHelper) deleted: the WAL v2 RecoveryDriver owns record apply now
        // (DatabaseEngine.RunWalV2Recovery, post-archetype-init). This in-ctor scan keeps only what the v2 path does not yet own —
        // the recovery frontier (LastValidLSN) and voiding pending UoWs. The dbe argument is always null here; Phase 6 below is
        // likewise dead and removed with the rest of the v1 read path later.

        // ═══════════════════════════════════════════════════════════
        // Phase 6: Replay TickFence entries (SV crash recovery)
        // ═══════════════════════════════════════════════════════════

        if (dbe != null && (tickFenceEntries.Count > 0 || clusterTickFenceEntries.Count > 0))
        {
            // Phase 8: Recovery:TickFence span — covers SV/cluster TickFence replay.
            var tfScope = TyphonEvent.BeginDurabilityRecoveryTickFence();
            try
            {
                if (tickFenceEntries.Count > 0)
                {
                    ReplayTickFences(dbe, tickFenceEntries, ref result);
                }
                if (clusterTickFenceEntries.Count > 0)
                {
                    ReplayClusterTickFences(dbe, clusterTickFenceEntries, ref result);
                }
                tfScope.TickFenceCount = result.TickFenceChunksProcessed;
                tfScope.Entries = result.TickFenceEntriesReplayed;
                tfScope.TickNumber = 0;  // No single tick number — chunks span multiple ticks; left at 0.
            }
            finally
            {
                tfScope.Dispose();
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Phase 7: Finalize
        // ═══════════════════════════════════════════════════════════

        result.ElapsedMicroseconds = ElapsedUs(startTicks);
        return result;
    }

    /// <summary>
    /// Processes a Transaction chunk body: parses the WalRecordHeader, extracts payload,
    /// groups by UowEpoch, and tracks UowBegin/UowCommit flags.
    /// </summary>
    private static void ProcessTransactionChunk(Dictionary<ushort, UowScanState> uowStates, ReadOnlySpan<byte> body, long checkpointLSN)
    {
        if (body.Length < WalRecordHeader.SizeInBytes)
        {
            return; // Malformed transaction chunk — skip
        }

        var header = MemoryMarshal.Read<WalRecordHeader>(body);
        var payload = body.Length > WalRecordHeader.SizeInBytes ?
            body.Slice(WalRecordHeader.SizeInBytes, Math.Min(header.PayloadLength, body.Length - WalRecordHeader.SizeInBytes)) : ReadOnlySpan<byte>.Empty;

        var uowId = header.UowEpoch;
        if (uowId == 0)
        {
            return; // Skip records with no UoW association
        }

        // Skip records before checkpoint LSN
        if (checkpointLSN > 0 && header.LSN <= checkpointLSN)
        {
            return;
        }

        if (!uowStates.TryGetValue(uowId, out var state))
        {
            state = new UowScanState();
            uowStates[uowId] = state;
        }

        if ((header.Flags & (byte)WalRecordFlags.UowBegin) != 0)
        {
            state.HasBegin = true;
        }

        if ((header.Flags & (byte)WalRecordFlags.UowCommit) != 0)
        {
            state.HasCommit = true;
        }

        // Buffer the record for potential replay
        state.Records.Add((header, payload.ToArray()));
    }

    /// <summary>
    /// Collects a TickFence chunk body into the entry list.
    /// TickFence body layout: [TickFenceHeader (24B)] [entries: ChunkId (4B) + ComponentData (stride B) × EntryCount].
    /// </summary>
    private static void CollectTickFenceChunk(List<TickFenceScanEntry> entries, ReadOnlySpan<byte> body)
    {
        if (body.Length < TickFenceHeader.SizeInBytes)
        {
            return; // Malformed tick fence chunk — skip
        }

        var header = MemoryMarshal.Read<TickFenceHeader>(body);
        var entryData = body.Slice(TickFenceHeader.SizeInBytes);
        int entrySize = 4 + header.PayloadStride;

        if (entryData.Length < header.EntryCount * entrySize)
        {
            return; // Truncated tick fence data — skip
        }

        var scanEntry = new TickFenceScanEntry
        {
            LSN = header.LSN,
            TickNumber = header.TickNumber,
            ComponentTypeId = header.ComponentTypeId,
            PayloadStride = header.PayloadStride,
            Entries = new List<(int, byte[])>(header.EntryCount),
        };

        int offset = 0;
        for (int i = 0; i < header.EntryCount; i++)
        {
            int chunkId = MemoryMarshal.Read<int>(entryData.Slice(offset));
            offset += 4;

            var componentData = entryData.Slice(offset, header.PayloadStride).ToArray();
            offset += header.PayloadStride;

            scanEntry.Entries.Add((chunkId, componentData));
        }

        entries.Add(scanEntry);
    }

    /// <summary>
    /// Replays collected TickFence entries by overwriting component data in the appropriate ComponentSegment chunks.
    /// Entries are applied in LSN order — later writes to the same ChunkId naturally overwrite earlier ones (last-writer-wins).
    /// Only applies to SingleVersion ComponentTables; Versioned/Transient are skipped.
    /// </summary>
    private void ReplayTickFences(DatabaseEngine dbe, List<TickFenceScanEntry> entries, ref WalRecoveryResult result)
    {
        // Sort by LSN to ensure last-writer-wins ordering
        entries.Sort((a, b) => a.LSN.CompareTo(b.LSN));

        using var epochGuard = EpochGuard.Enter(dbe.EpochManager);
        var cs = dbe.MMF.CreateChangeSet();

        foreach (var scanEntry in entries)
        {
            var table = dbe.GetComponentTableByWalTypeId(scanEntry.ComponentTypeId);
            if (table == null || table.StorageMode != StorageMode.SingleVersion)
            {
                continue; // Unknown or non-SV table — skip
            }

            var accessor = table.ComponentSegment.CreateChunkAccessor(cs);
            int overhead = table.ComponentOverhead;

            foreach (var (chunkId, componentData) in scanEntry.Entries)
            {
                var dst = accessor.GetChunkAsSpan(chunkId, true);
                componentData.AsSpan().CopyTo(dst.Slice(overhead));
                result.TickFenceEntriesReplayed++;
            }

            accessor.Dispose();
            result.TickFenceChunksProcessed++;
        }

        cs.SaveChanges();
        dbe.MMF.FlushToDisk();
    }

    /// <summary>
    /// Parses a ClusterTickFence chunk body and collects entries for later replay.
    /// </summary>
    private static void CollectClusterTickFenceChunk(List<ClusterTickFenceScanEntry> entries, ReadOnlySpan<byte> body)
    {
        if (body.Length < ClusterTickFenceHeader.SizeInBytes)
        {
            return; // Malformed — skip
        }

        var header = MemoryMarshal.Read<ClusterTickFenceHeader>(body);
        var entryData = body.Slice(ClusterTickFenceHeader.SizeInBytes);
        int entrySize = 4 + header.PerEntityPayload;

        if (entryData.Length < header.EntryCount * entrySize)
        {
            return; // Truncated — skip
        }

        var scanEntry = new ClusterTickFenceScanEntry
        {
            LSN = header.LSN,
            ArchetypeId = header.ArchetypeId,
            Entries = new List<(int, byte[])>(header.EntryCount),
        };

        int offset = 0;
        for (int i = 0; i < header.EntryCount; i++)
        {
            int entityIndex = MemoryMarshal.Read<int>(entryData.Slice(offset));
            offset += 4;

            var allCompData = entryData.Slice(offset, header.PerEntityPayload).ToArray();
            offset += header.PerEntityPayload;

            scanEntry.Entries.Add((entityIndex, allCompData));
        }

        entries.Add(scanEntry);
    }

    /// <summary>
    /// Replays collected ClusterTickFence entries by overwriting component data in the appropriate cluster SoA slots.
    /// Entries are applied in LSN order (last-writer-wins). Only applies to cluster-eligible archetypes with active ClusterState.
    /// </summary>
    private unsafe void ReplayClusterTickFences(DatabaseEngine dbe, List<ClusterTickFenceScanEntry> entries, ref WalRecoveryResult result)
    {
        entries.Sort((a, b) => a.LSN.CompareTo(b.LSN));

        using var epochGuard = EpochGuard.Enter(dbe.EpochManager);
        var cs = dbe.MMF.CreateChangeSet();

        foreach (var scanEntry in entries)
        {
            if (scanEntry.ArchetypeId >= dbe._archetypeStates.Length)
            {
                continue;
            }

            var engineState = dbe._archetypeStates[scanEntry.ArchetypeId];
            var clusterState = engineState?.ClusterState;
            if (clusterState == null)
            {
                continue; // Cluster segment not loaded — skip
            }

            var layout = clusterState.Layout;

            // Pure-Transient archetypes have no WAL entries — skip
            if (clusterState.ClusterSegment == null)
            {
                continue;
            }

            var accessor = clusterState.ClusterSegment.CreateChunkAccessor(cs);
            ushort transientMask = layout.TransientSlotMask;

            foreach (var (entityIndex, allCompData) in scanEntry.Entries)
            {
                int clusterChunkId = entityIndex >> 6;
                int slotIndex = entityIndex & 0x3F;

                byte* clusterBase = accessor.GetChunkAddress(clusterChunkId, true);

                // Verify slot is occupied — skip entries for freed slots (stale WAL entries)
                ulong occupancy = *(ulong*)clusterBase;
                if ((occupancy & (1UL << slotIndex)) == 0)
                {
                    continue; // Slot freed — skip this entry
                }

                int dataOffset = 0;
                for (int slot = 0; slot < layout.ComponentCount; slot++)
                {
                    // Transient slots were not serialized to WAL — skip during replay
                    if ((transientMask & (1 << slot)) != 0)
                    {
                        continue;
                    }
                    int compOffset = layout.ComponentOffset(slot);
                    int compSize = layout.ComponentSize(slot);
                    if (dataOffset + compSize > allCompData.Length)
                    {
                        break; // Truncated entry — skip remaining components
                    }
                    byte* dst = clusterBase + compOffset + slotIndex * compSize;
                    allCompData.AsSpan(dataOffset, compSize).CopyTo(new Span<byte>(dst, compSize));
                    dataOffset += compSize;
                }

                result.TickFenceEntriesReplayed++;
            }

            accessor.Dispose();
            result.TickFenceChunksProcessed++;
        }

        cs.SaveChanges();
        dbe.MMF.FlushToDisk();
    }

    /// <summary>
    /// Discovers WAL segment files in the WAL directory, sorted by segment ID ascending.
    /// </summary>
    private List<string> DiscoverSegments()
    {
        if (!Directory.Exists(_walDirectory))
        {
            return [];
        }

        var walFiles = Directory.GetFiles(_walDirectory, "*.wal");
        if (walFiles.Length == 0)
        {
            return [];
        }

        // Sort by segment ID (filename is {segmentId:D16}.wal)
        Array.Sort(walFiles, (a, b) =>
        {
            var aId = ParseSegmentId(a);
            var bId = ParseSegmentId(b);
            return aId.CompareTo(bId);
        });

        return [.. walFiles];
    }

    /// <summary>
    /// Parses the segment ID from a WAL file path. Format: {segmentId:D16}.wal
    /// </summary>
    private static long ParseSegmentId(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (long.TryParse(fileName, out var segmentId))
        {
            return segmentId;
        }

        return long.MaxValue; // Unknown format — sort to end
    }

    private static long ElapsedUs(long startTicks) => (Stopwatch.GetTimestamp() - startTicks) * 1_000_000 / Stopwatch.Frequency;

    public void Dispose()
    {
        // No persistent resources to dispose
    }
}
