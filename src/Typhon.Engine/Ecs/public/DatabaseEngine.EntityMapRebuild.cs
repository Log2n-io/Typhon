using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// The crash open's EntityMap rebuild of a cluster archetype (#1665): the map built in bucket order at its final size, the deferred Versioned HEAD rebuild
/// folded into its cluster walk, and each revision segment's chain roots scanned once for both the rebuild and the reservation before the replay.
/// </summary>
public partial class DatabaseEngine
{
    /// <summary>One entity staged for the EntityMap's bulk build: what its record is built from, and the bucket it is sorted by.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct EntityMapBuildEntry
    {
        public long EntityPK;
        public int Bucket;

        /// <summary>The entity's cluster, as its index in the active-cluster list, shifted left 6, and its slot in it.</summary>
        public int Location;

        public ushort EnabledBits;
    }

    /// <summary>The chain roots of one revision segment, by EntityPK, and the named chunks among them (see
    /// <see cref="ComponentRevisionManager.EnumerateVersionedChainHeads(ChunkBasedSegment{PersistentStore}, ushort?, bool, HashSet{int})"/>); after
    /// every EntityMap rebuild, only the roots (<see cref="CompactCrashChainHeadsForReservation"/>).</summary>
    internal sealed class CrashChainHeads
    {
        public Dictionary<long, int> Heads;
        public HashSet<int> Named;

        /// <summary>The roots alone, what the reservation reads: set, and <see cref="Heads"/> and <see cref="Named"/> dropped, once every map is rebuilt.</summary>
        public int[] Roots;

        /// <summary>The segment's allocated chunk count when it was scanned: a segment that allocated or freed since is scanned again.</summary>
        public int AllocatedChunks;

        public int RootOf(long entityPK) => Heads.TryGetValue(entityPK, out var root) ? root : 0;
    }

    /// <summary>
    /// Each Versioned revision segment's chain roots as the crash open's EntityMap rebuild scanned them, unfiltered and named roots accepted, kept for the
    /// reservation before the replay, which needs the same scan (#1665). Null outside a crash open. A dictionary by EntityPK while the maps are rebuilt, about
    /// 28 bytes per entity; then the roots alone, 4 bytes each, within <see cref="IndexRebuildOptions.MemoryBudgetBytes"/>.
    /// </summary>
    private Dictionary<ChunkBasedSegment<PersistentStore>, CrashChainHeads> _crashChainHeads;

    /// <summary>
    /// The entries a partition of the EntityMap's bulk build holds when positive, instead of what <see cref="IndexRebuildOptions.MemoryBudgetBytes"/> holds:
    /// few enough that a test archetype takes several partitions. Test seam.
    /// </summary>
    internal static int EntityMapBuildEntriesForTest { get; set; }

    /// <summary>The fewest entries a partition of the bulk build holds, whatever the budget.</summary>
    private const int MinimumEntityMapBuildEntries = 1024;

    /// <summary>The partitions of the last crash open's EntityMap bulk builds, summed over the archetypes: one per staging walk. Test seam.</summary>
    internal int LastOpenEntityMapBuildPartitions { get; private set; }

    /// <summary>The staging ranges the last crash open's EntityMap bulk builds halved because their entities overflowed the buffer. Test seam.</summary>
    internal int LastOpenEntityMapRangesHalved { get; private set; }

    /// <summary>The revision segments whose chain-root scan the last crash open's reservation took from the EntityMap rebuild's. Test seam.</summary>
    internal int ChainHeadScansReusedAtOpen { get; private set; }

    /// <summary>The bytes the compacted chain roots may hold when positive, instead of <see cref="IndexRebuildOptions.MemoryBudgetBytes"/>. Test seam.</summary>
    internal static long ChainHeadCacheBudgetForTest { get; set; }

    private ResourceNode _entityMapRebuildNode;

    /// <summary><see cref="CrashChainHeads"/> of <paramref name="table"/>'s revision segment, scanned on the first call. Inside an epoch scope.</summary>
    internal CrashChainHeads CrashChainHeadsOf(ComponentTable table)
    {
        var segment = table.CompRevTableSegment;
        if (_crashChainHeads != null && _crashChainHeads.TryGetValue(segment, out var cached) && cached.AllocatedChunks == segment.AllocatedChunkCount)
        {
            return cached;
        }

        var named = new HashSet<int>();
        var scanned = new CrashChainHeads
        {
            AllocatedChunks = segment.AllocatedChunkCount,
            Heads = ComponentRevisionManager.EnumerateVersionedChainHeads(segment, null, acceptNamedRoots: true, named),
            Named = named,
        };
        (_crashChainHeads ??= [])[segment] = scanned;
        return scanned;
    }

    /// <summary>
    /// After every EntityMap rebuild: each cached scan cut down to its roots, the one thing the reservation reads, within
    /// <see cref="IndexRebuildOptions.MemoryBudgetBytes"/>. A segment whose roots would pass the budget is dropped, and the reservation scans it again.
    /// </summary>
    internal void CompactCrashChainHeadsForReservation()
    {
        if (_crashChainHeads == null)
        {
            return;
        }

        var budget = ChainHeadCacheBudgetForTest > 0 ? ChainHeadCacheBudgetForTest : _options.IndexRebuild.MemoryBudgetBytes;
        long held = 0;
        List<ChunkBasedSegment<PersistentStore>> dropped = null;
        foreach (var (segment, scan) in _crashChainHeads)
        {
            var bytes = (long)scan.Heads.Count * sizeof(int);
            if (held + bytes > budget)
            {
                (dropped ??= []).Add(segment);
                continue;
            }

            held += bytes;
            scan.Roots = new int[scan.Heads.Count];
            scan.Heads.Values.CopyTo(scan.Roots, 0);
            scan.Heads = null;
            scan.Named = null;
        }

        if (dropped != null)
        {
            foreach (var segment in dropped)
            {
                _crashChainHeads.Remove(segment);
            }
        }
    }

    /// <summary>
    /// The chain roots the reservation unions with the live records' (see <see cref="ReserveChainChunksAfterCrash()"/>): the EntityMap rebuild's scan, taken
    /// out of the cache, when no chain of the table collapsed since — a collapse unlinks chunks, which the scan then finds as roots — else a new scan.
    /// </summary>
    internal IEnumerable<int> ChainHeadsForReservation(ComponentTable table, bool collapsed)
    {
        var segment = table.CompRevTableSegment;
        if (_crashChainHeads != null && _crashChainHeads.Remove(segment, out var cached) && cached.Roots != null && !collapsed
            && cached.AllocatedChunks == segment.AllocatedChunkCount)
        {
            ChainHeadScansReusedAtOpen++;
            return cached.Roots;
        }

        return ComponentRevisionManager.EnumerateVersionedChainHeads(table, null, acceptNamedRoots: true).Values;
    }

    /// <summary>
    /// Re-derive a cluster archetype's EntityMap from the cluster segment alone. Walks every live slot of every active cluster and rebuilds the
    /// <c>ClusterEntityRecord</c> from self-describing cluster state: the EntityKey from <c>EntityKeys[slot]</c>, the per-entity enabled mask reconstructed
    /// from the per-component <c>EnabledBits[C]</c> bitmaps, and each Versioned slot's compRevFirstChunkId from the chain-head scan. BornTSN/DiedTSN = 0
    /// (live, committed before checkpoint — same convention as the flat rebuild).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>In bucket order (#1665).</b> Inserted in the clusters' order, the entries landed on a random bucket each, and the map split as it grew: a map larger
    /// than the cache was rewritten many times over — 6.9 GB written for a 742 MiB map, in 22 s, on a 7 GB world at 512 MiB. The first walk counts the
    /// entities; the map is then emptied at its final bucket count and filled bucket by bucket, from entries staged in a native buffer and sorted by bucket,
    /// so its pages are written in order, each once. The buffer holds the whole archetype when it fits the budget
    /// (<see cref="IndexRebuildOptions.MemoryBudgetBytes"/>, the index rebuild's, which runs later); otherwise each further walk stages the entities of one
    /// range of buckets.
    /// </para>
    /// <para>
    /// <b>The HEAD rebuild in the same walk.</b> A crash open defers this archetype's Versioned HEAD rebuild until its map is derived
    /// (<see cref="DrainDeferredVersionedHeadRebuilds"/>), and that pass then looked each entity's roots up in the map just built. The first walk has the
    /// roots in hand and copies the HEADs itself; its batch points are the <see cref="RecoveryPass.VersionedHeads"/> pass's.
    /// </para>
    /// </remarks>
    private unsafe void RebuildClusterEntityMapEntries(ArchetypeMetadata meta, ArchetypeEngineState state, ChangeSet cs)
    {
        var clusterState = state.ClusterState;
        var layout = clusterState.Layout;
        var slotToVi = layout.SlotToVersionedIndex;
        var map = state.EntityMap;
        var clusterCount = clusterState.ActiveClusterCount;
        if (clusterCount >= 1 << 25)
        {
            ThrowHelper.ThrowInvalidOp($"EntityMap rebuild of {meta.Name}: {clusterCount} clusters is past the 2^25 a staged entry can locate.");
        }

        // Versioned chain heads per slot (compRevFirstChunkId keyed by EntityPK) — the same source the flat rebuild uses for the per-slot location.
        MarkOpenPhase("EntityMap.ChainHeads");
        var chainHeads = new CrashChainHeads[meta.ComponentCount];
        if (meta.VersionedSlotMask != 0 && slotToVi != null)
        {
            for (var slot = 0; slot < meta.ComponentCount; slot++)
            {
                var table = state.SlotToComponentTable[slot];
                if (slotToVi[slot] >= 0 && table?.CompRevTableSegment != null && table.StorageMode == StorageMode.Versioned
                    && table.CompRevTableSegment.ChunkCapacity > 0 && table.CompRevTableSegment.AllocatedChunkCount > 0)
                {
                    chainHeads[slot] = CrashChainHeadsOf(table);
                }
            }
        }

        // The deferred HEAD rebuild, run by the first walk. The map it would read is the one this re-derives, so the enabled bits a rootless slot is
        // classified by come from the cluster SoA too (#398): absence is unclassifiable on this path, as in the drain.
        var foldHeads = _deferredVersionedHeadRebuilds != null && _deferredVersionedHeadRebuilds.Remove(meta) && slotToVi != null;
        VersionedHeadCopier heads = null;

        var capacity = EntityMapBuildEntriesForTest > 0
            ? EntityMapBuildEntriesForTest
            : Math.Max(MinimumEntityMapBuildEntries, _options.IndexRebuild.MemoryBudgetBytes / (2L * sizeof(EntityMapBuildEntry)));
        capacity = Math.Min(capacity, int.MaxValue / sizeof(EntityMapBuildEntry));
        var stageAll = (long)clusterCount * 64 <= capacity;
        capacity = Math.Min(capacity, (long)clusterCount * 64);
        var owner = new BufferOwner(MemoryAllocator, _entityMapRebuildNode ??= new ResourceNode("EntityMapRebuild", ResourceType.Node, this));
        NativeBuffer entriesBuffer = null;
        NativeBuffer sortedBuffer = null;
        var recordBuf = stackalloc byte[map.ValueSize];
        var mapAccessor = default(ChunkAccessor<PersistentStore>);
        var mapAccessorLive = false;
        try
        {
            if (foldHeads)
            {
                heads = new VersionedHeadCopier(meta, layout, state.SlotToComponentTable, false, 0);
            }

            entriesBuffer = NativeBuffer.Allocate((int)Math.Max(1, capacity * sizeof(EntityMapBuildEntry)), owner, $"EntityMapStage.{meta.ArchetypeId}");
            sortedBuffer = NativeBuffer.Allocate((int)Math.Max(1, capacity * sizeof(EntityMapBuildEntry)), owner, $"EntityMapSorted.{meta.ArchetypeId}");
            var entries = (EntityMapBuildEntry*)entriesBuffer.Pointer;
            var sorted = (EntityMapBuildEntry*)sortedBuffer.Pointer;

            MarkOpenPhase("EntityMap.Walk");
            var entities = WalkClustersForEntityMapRebuild(meta, state, chainHeads, heads, cs, stageAll ? entries : null, out var maxEntityKey);
            if (heads != null)
            {
                NoteVersionedHeadRebuildSkips(meta, in heads.Skips);
                LastOpenVersionedHeadRebuildCount++;
                heads.Dispose();
                heads = null;
            }

            MarkOpenPhase("EntityMap.Insert");
            var bucketCount = map.BeginBulkBuild(entities, cs);
            mapAccessor = map.Segment.CreateChunkAccessor(cs);
            mapAccessorLive = true;
            var sinceBatchPoint = 0;
            if (stageAll)
            {
                for (var i = 0L; i < entities; i++)
                {
                    entries[i].Bucket = map.BucketIndexOf(EntityId.FromRaw(entries[i].EntityPK).EntityKey);
                }

                LastOpenEntityMapBuildPartitions++;
                BuildEntityMapRange(meta, state, chainHeads, cs, entries, sorted, (int)entities, 0, bucketCount, recordBuf, ref mapAccessor,
                    ref sinceBatchPoint);
            }
            else
            {
                // Each range's expected share four fifths of the buffer, so the spread of the hash rarely overflows it; a range that does is halved.
                var share = Math.Max(1, capacity * 4 / 5);
                var partitions = Math.Max(1, (entities + share - 1) / share);
                var span = (int)Math.Max(1, (bucketCount + partitions - 1) / partitions);
                var lo = 0;
                while (lo < bucketCount)
                {
                    var hi = (int)Math.Min(bucketCount, (long)lo + span);
                    MarkOpenPhase("EntityMap.Stage");
                    var staged = StageEntityMapRange(clusterState, meta.ComponentCount, map, entries, capacity, lo, hi);
                    MarkOpenPhase("EntityMap.Insert");
                    if (staged < 0)
                    {
                        // One bucket alone past the buffer cannot be halved further: a hash this degenerate, or a buffer this small, is refused.
                        if (hi - lo == 1)
                        {
                            ThrowHelper.ThrowInvalidOp(
                                $"EntityMap rebuild of {meta.Name}: bucket {lo} holds more than the {capacity} entries a staging range holds.");
                        }

                        span = Math.Max(1, (hi - lo) / 2);
                        LastOpenEntityMapRangesHalved++;
                        continue;
                    }

                    LastOpenEntityMapBuildPartitions++;
                    BuildEntityMapRange(meta, state, chainHeads, cs, entries, sorted, staged, lo, hi, recordBuf, ref mapAccessor, ref sinceBatchPoint);
                    lo = hi;
                }
            }

            map.EndBulkBuild(ref mapAccessor, cs);

            // A max, never an assignment: the persisted counter may be above every occupied key — the top-key entity destroyed — and keys are never
            // recycled (RB-06). Assigned, the next spawn was handed the dead entity's key (#1164).
            if (maxEntityKey > state.NextEntityKey)
            {
                state.NextEntityKey = maxEntityKey;
            }
        }
        finally
        {
            heads?.Dispose();
            if (mapAccessorLive)
            {
                mapAccessor.Dispose();
            }

            entriesBuffer?.Free();
            sortedBuffer?.Free();
        }
    }

    /// <summary>
    /// The bulk build's first walk: every live entity counted, and staged into <paramref name="staged"/> when given (its bucket left for later). The walk
    /// also seeds each cluster's visibility summary, resets the lock of each named chunk adopted as a root, and copies the Versioned HEADs when
    /// <paramref name="heads"/> is given. Returns the entities.
    /// </summary>
    private unsafe long WalkClustersForEntityMapRebuild(ArchetypeMetadata meta, ArchetypeEngineState state, CrashChainHeads[] chainHeads,
        VersionedHeadCopier heads, ChangeSet cs, EntityMapBuildEntry* staged, out long maxEntityKey)
    {
        var clusterState = state.ClusterState;
        var tables = state.SlotToComponentTable;
        var layout = clusterState.Layout;
        var slotToVi = layout.SlotToVersionedIndex;
        var pass = heads != null ? RecoveryPass.VersionedHeads : RecoveryPass.EntityMap;
        long entities = 0;
        maxEntityKey = 0;
        var sinceBatchPoint = 0;

        // The clusters read and released page by page (EP-02): the segment can be larger than the cache, and an epoch-tagged walk of it pinned all of it
        // (#1183). A cluster's address holds until this accessor's next miss — the next cluster's. The HEADs are written through it while its window
        // holds the page, and the page marked modified before the walk moves on (PS-10).
        var clusterAccessor = clusterState.ClusterSegment.CreateScanAccessor();
        try
        {
            for (var c = 0; c < clusterState.ActiveClusterCount; c++)
            {
                // Between clusters, every RecoveryBatchSize entities: the batch's pages unpinned, the owed ones written past the threshold (#1183, CK-08).
                if (sinceBatchPoint >= RecoveryBatchSize)
                {
                    RecoveryBatchPoint(cs, pass);
                    sinceBatchPoint = 0;
                }

                var chunkId = clusterState.ActiveClusterIds[c];
                var clusterBase = clusterAccessor.GetChunkAddress(chunkId);
                var occupancy = *(ulong*)clusterBase;
                if (occupancy == 0)
                {
                    continue;
                }

                sinceBatchPoint += BitOperations.PopCount(occupancy);
                clusterState.NoteClusterBorn(chunkId, 0);   // H1: reopened clusters are all-genesis, so seed the summary rather than leaving it unknown

                while (occupancy != 0)
                {
                    var slotIndex = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;

                    var entityPK = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
                    if (heads != null)
                    {
                        ArchetypeClusterState.ThrowIfZeroEntityId(meta, chunkId, slotIndex, entityPK);
                    }

                    var entityKey = EntityId.FromRaw(entityPK).EntityKey;
                    if (entityKey > maxEntityKey)
                    {
                        maxEntityKey = entityKey;
                    }

                    var enabledBits = EnabledBitsOf(clusterBase, layout, meta.ComponentCount, slotIndex);
                    if (slotToVi != null)
                    {
                        for (var slot = 0; slot < meta.ComponentCount; slot++)
                        {
                            if (slotToVi[slot] < 0)
                            {
                                continue;
                            }

                            var root = chainHeads[slot]?.RootOf(entityPK) ?? 0;

                            // A root adopted from the named chunks: the lock reset at the table's load leaves named chunks as they are (#1251). Before the
                            // HEAD walk, which takes it.
                            if (root != 0 && chainHeads[slot].Named.Contains(root))
                            {
                                ComponentRevisionManager.ResetChainLocks(tables[slot].CompRevTableSegment, this, [root]);
                            }

                            heads?.CopySlot(slot, root, clusterBase, slotIndex, enabledBits);
                        }
                    }

                    if (staged != null)
                    {
                        staged[entities] = new EntityMapBuildEntry { EntityPK = entityPK, Location = (c << 6) | slotIndex, EnabledBits = enabledBits };
                    }

                    entities++;
                }

                if (heads != null)
                {
                    clusterState.NoteClusterPageModifiedAt(clusterBase);
                }
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }

        return entities;
    }

    /// <summary>
    /// A further walk of the bulk build: stages the entities whose bucket is in [<paramref name="lo"/>, <paramref name="hi"/>). Returns how many, or -1 when
    /// they are more than <paramref name="capacity"/>.
    /// </summary>
    private static unsafe int StageEntityMapRange(ArchetypeClusterState clusterState, int componentCount, RawValuePagedHashMap<long, PersistentStore> map,
        EntityMapBuildEntry* staged, long capacity, int lo, int hi)
    {
        var layout = clusterState.Layout;
        var count = 0;
        var clusterAccessor = clusterState.ClusterSegment.CreateScanAccessor();
        try
        {
            for (var c = 0; c < clusterState.ActiveClusterCount; c++)
            {
                var clusterBase = clusterAccessor.GetChunkAddress(clusterState.ActiveClusterIds[c]);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slotIndex = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    var entityPK = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
                    var bucket = map.BucketIndexOf(EntityId.FromRaw(entityPK).EntityKey);
                    if (bucket < lo || bucket >= hi)
                    {
                        continue;
                    }

                    if (count == capacity)
                    {
                        return -1;
                    }

                    staged[count++] = new EntityMapBuildEntry
                    {
                        EntityPK = entityPK,
                        Bucket = bucket,
                        Location = (c << 6) | slotIndex,
                        EnabledBits = EnabledBitsOf(clusterBase, layout, componentCount, slotIndex),
                    };
                }
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }

        return count;
    }

    /// <summary>
    /// Sorts <paramref name="count"/> staged entries of buckets [<paramref name="lo"/>, <paramref name="hi"/>) by bucket (a counting sort into
    /// <paramref name="sorted"/>) and appends their records to the map in that order, with a batch point every <see cref="RecoveryBatchSize"/> entries.
    /// </summary>
    private unsafe void BuildEntityMapRange(ArchetypeMetadata meta, ArchetypeEngineState state, CrashChainHeads[] chainHeads, ChangeSet cs,
        EntityMapBuildEntry* staged, EntityMapBuildEntry* sorted, int count, int lo, int hi, byte* recordBuf, ref ChunkAccessor<PersistentStore> mapAccessor,
        ref int sinceBatchPoint)
    {
        MarkOpenPhase("EntityMap.Sort");
        var starts = new int[hi - lo + 1];
        for (var i = 0; i < count; i++)
        {
            starts[staged[i].Bucket - lo + 1]++;
        }

        for (var b = 1; b < starts.Length; b++)
        {
            starts[b] += starts[b - 1];
        }

        for (var i = 0; i < count; i++)
        {
            sorted[starts[staged[i].Bucket - lo]++] = staged[i];
        }

        MarkOpenPhase("EntityMap.Insert");
        var map = state.EntityMap;
        var clusterIds = state.ClusterState.ActiveClusterIds;
        var slotToVi = state.ClusterState.Layout.SlotToVersionedIndex;
        var valueSize = (uint)map.ValueSize;
        for (var i = 0; i < count; i++)
        {
            // Every RecoveryBatchSize entries: the map's pages written so far stay owed, the batch's pages unpinned (#1183, CK-08). The map accessor is
            // released across it, so nothing here holds an epoch-protected address over the refresh.
            if (sinceBatchPoint >= RecoveryBatchSize)
            {
                mapAccessor.Dispose();
                RecoveryBatchPoint(cs, RecoveryPass.EntityMap);
                mapAccessor = map.Segment.CreateChunkAccessor(cs);
                sinceBatchPoint = 0;
            }

            ref var entry = ref sorted[i];
            new Span<byte>(recordBuf, (int)valueSize).Clear();
            ref var header = ref ClusterEntityRecordAccessor.GetHeader(recordBuf);
            header.BornTSN = 0; // committed before checkpoint → always visible
            header.DiedTSN = 0; // live (occupancy bit set)
            header.EnabledBits = entry.EnabledBits;
            ClusterEntityRecordAccessor.SetClusterChunkId(recordBuf, clusterIds[entry.Location >> 6]);
            ClusterEntityRecordAccessor.SetSlotIndex(recordBuf, (byte)(entry.Location & 63));
            if (slotToVi != null)
            {
                for (var slot = 0; slot < meta.ComponentCount; slot++)
                {
                    var vi = slotToVi[slot];
                    if (vi >= 0)
                    {
                        ClusterEntityRecordAccessor.SetCompRevFirstChunkId(recordBuf, vi, chainHeads[slot]?.RootOf(entry.EntityPK) ?? 0);
                    }
                }
            }

            var value = map.BulkBuildAppend(EntityId.FromRaw(entry.EntityPK).EntityKey, entry.Bucket, ref mapAccessor, cs);
            Buffer.MemoryCopy(recordBuf, value, valueSize, valueSize);
            sinceBatchPoint++;
        }
    }

    /// <summary>
    /// The 16-bit enabled mask of the entity at <paramref name="slotIndex"/>, from the cluster's per-component <c>EnabledBits[c]</c> (bit slotIndex set ⇒
    /// component c enabled), which every commit keeps equal to the committed record and dirties with the cluster page (ENABLE-01).
    /// </summary>
    private static unsafe ushort EnabledBitsOf(byte* clusterBase, ArchetypeClusterInfo layout, int componentCount, int slotIndex)
    {
        ushort enabledMask = 0;
        for (var comp = 0; comp < componentCount; comp++)
        {
            if ((*(ulong*)(clusterBase + layout.EnabledBitsOffset(comp)) & (1UL << slotIndex)) != 0)
            {
                enabledMask |= (ushort)(1 << comp);
            }
        }

        return enabledMask;
    }
}
