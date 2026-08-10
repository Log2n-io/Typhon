using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// <c>MAP</c> — the per-archetype EntityMap: a linear hash map whose every structural field is a chunk-id pointer.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>MAP-04</c> is a rule about this code, not a finding it emits.</b> The catalogue says so in as many words:
/// <i>"a scanner-safety rule as much as a check ... the offline scanner must validate every pointer against the
/// allocation map before following it — a checker that crashes on the databases it was built to diagnose is worse than
/// useless"</i>. <c>RB-01</c> supplies the urgency: a torn EntityMap page holds a hash directory of chunk-id pointers,
/// and trusting one <i>"dereferences garbage into a hard process crash before any loud-fail can fire"</i>. So every hop
/// is range- and allocation-checked <b>before</b> the read; the finding is what survives the walk.
/// </para>
/// <para>
/// <b>The bucket is SoA, and its capacity needs the value size.</b> A bucket chunk is
/// <c>[header 12 B][key₀..key_{cap-1}][value₀..value_{cap-1}]</c> with
/// <c>cap = (stride − 12) / (8 + recordSize)</c>. The EntityMap is a <c>RawValuePagedHashMap&lt;long,…&gt;</c> whose
/// value size is a <i>runtime</i> constructor argument, so that capacity is not derivable from the persisted stride —
/// it comes from the archetype's Versioned component count via <c>ArchetypeView.EntityRecordSize</c>, which is why this
/// family waited on the VSBS decode (<c>09 §5.5</c>).
/// </para>
/// <para>
/// <b>Every chunk is copied out before the next is read.</b> The walk nests three deep — meta → directory → bucket
/// chain — and a cursor handing back a span over one reused page buffer means a nested read silently rewrites the
/// directory the outer loop is still iterating. That defect shipped: it is what made an earlier version of this walk
/// recover a strict subset of a healthy map, which read as a layout problem for two rounds of debugging. Owned buffers
/// per nesting level make it unrepresentable rather than merely fixed.
/// </para>
/// </remarks>
internal static class EntityMapChecks
{
    /// <summary>Check code: every EntityMap entry names an entity the cluster actually holds.</summary>
    public const string EntriesResolve = "CHK-MAP-01";

    /// <summary>Check code: every live cluster entity appears in the EntityMap.</summary>
    public const string SlotsAreReachable = "CHK-MAP-02";

    /// <summary>Check code: no duplicate entity id across buckets.</summary>
    public const string NoDuplicateIds = "CHK-MAP-03";

    /// <summary>Check code: every hash-directory chunk-id pointer resolves before it is dereferenced.</summary>
    public const string PointersResolve = "CHK-MAP-04";

    private const int InlineDirectoryChunks = 57;
    private const int BucketIdsPerDirectoryChunk = 64;
    private const int DirectoryIdsPerOverflowChunk = 63;
    private const int MetaInlineIdsOffset = 28;
    private const int MetaPackedOffset = 8;
    private const int MetaDirectoryCountOffset = 24;
    private const int MetaOverflowHeadOffset = 4;
    private const int BucketHeaderSize = 12;
    private const int BucketEntryCountOffset = 4;
    private const int BucketOverflowOffset = 8;

    /// <summary>
    /// The map's meta record lives in chunk <b>0</b> — the slot every other chunk-based segment reserves as its null
    /// sentinel (<c>PagedHashMapBase</c> reads it as <c>GetChunkReadOnly&lt;PagedHashMapMeta&gt;(0)</c>).
    /// </summary>
    /// <remarks>
    /// Worth stating rather than assuming, because assuming cost a debugging round: read from chunk 1 instead and the
    /// meta is a zero-filled record whose directory ids are all garbage, so a healthy database reports an unusable
    /// directory and a circular overflow chain.
    /// </remarks>
    private const int MetaChunkId = 0;

    /// <summary>Runs the entity-map family. Requires <see cref="ScanDepth.Deep"/> and a readable manifest.</summary>
    /// <param name="ctx">The scan context, with the manifest read and segments walked.</param>
    public static void Run(ScanContext ctx)
    {
        if (!ctx.AtLeast(ScanDepth.Deep))
        {
            ctx.Findings.NoteSkipped("CHK-MAP-*", "needs Deep depth");
            return;
        }

        if (ctx.Manifest is not { IsUsable: true })
        {
            ctx.Findings.NoteSkipped("CHK-MAP-*", "the schema manifest could not be read, so entity maps cannot be located");
            return;
        }

        foreach (var archetype in ctx.Manifest.Archetypes.Values)
        {
            if (archetype.EntityMapRoot == 0)
            {
                continue;   // "not persisted, rebuild from PK indexes" is a documented, legitimate state
            }

            Walk(ctx, archetype);
        }
    }

    /// <summary>
    /// The archetype's Versioned components, in the slot order the entity record's chain-pointer array uses.
    /// </summary>
    /// <remarks>
    /// The record holds one <c>compRevFirstChunkId</c> per <i>Versioned</i> slot, densely — non-Versioned components
    /// occupy no position. So the mapping from array index to component is the archetype's component list filtered to
    /// Versioned, in order, and getting that filter wrong attributes one component's chain roots to another.
    /// </remarks>
    private static List<string> VersionedComponentsInSlotOrder(ScanContext ctx, ArchetypeView archetype)
    {
        var ordered = new List<string>();
        foreach (var name in archetype.ComponentNames)
        {
            if (ctx.Manifest.Components.TryGetValue(name, out var component) && component.StorageMode == StorageMode.Versioned)
            {
                ordered.Add(name);
            }
        }

        return ordered;
    }

    private static void Walk(ScanContext ctx, ArchetypeView archetype)
    {
        if (!ctx.Segments.TryGetValue(archetype.EntityMapRoot, out var segment) || segment.Pages.Count == 0)
        {
            return;
        }

        var page = new byte[IntegrityConstants.PageSize];
        if (!ctx.Source.TryReadPage(segment.Pages[0], page))
        {
            return;
        }

        var geometry = ChunkGeometry.FromPage(page);
        if (!geometry.IsUsable)
        {
            ctx.Findings.NoteCaveat(
                $"The EntityMap for '{archetype.Name}' (root page {segment.RootPageIndex}) records no chunk stride, so its "
                + "directory was not walked.");
            return;
        }

        var cursor = new ChunkCursor(ctx.Source, segment, geometry);
        var locus = new Locus(segment.RootPageIndex, segment.RootPageIndex, segment.Kind);

        // One buffer per nesting level, owned by this frame. See the type remarks: sharing the cursor's page across a
        // nested read is what broke the previous walk.
        var meta = new byte[geometry.Stride];
        var directory = new byte[geometry.Stride];
        var bucket = new byte[geometry.Stride];

        if (!cursor.TryRead(MetaChunkId, meta, out _))
        {
            ctx.Report(PointersResolve, IntegritySeverity.Fatal, "RB-01", locus,
                $"The EntityMap for '{archetype.Name}' has no readable meta record.",
                $"Chunk {MetaChunkId} of the segment rooted at page {segment.RootPageIndex} could not be read, so the hash "
                + "directory cannot be located at all. Every entity of this archetype is unfindable by id until the map is "
                + "rebuilt from the cluster, which loses nothing.",
                Repairability.Lossless);
            return;
        }

        var declaredDirectories = MemoryMarshal.Read<ushort>(meta.AsSpan(MetaDirectoryCountOffset));
        var overflowHead = MemoryMarshal.Read<int>(meta.AsSpan(MetaOverflowHeadOffset));

        // No bound on the bucket scan beyond the directory itself. The directory is sized to CAPACITY rather than to
        // population, so slots past the live bucket count name ids that were never allocated — and over-scanning is safe
        // precisely because an unallocated chunk id is a caveat rather than a finding.
        var directoryIds = new List<int>();
        for (var i = 0; i < InlineDirectoryChunks && directoryIds.Count < declaredDirectories; i++)
        {
            var id = MemoryMarshal.Read<int>(meta.AsSpan(MetaInlineIdsOffset + (i * sizeof(int))));
            if (id != 0)
            {
                directoryIds.Add(id);
            }
        }

        CollectOverflowDirectories(ctx, archetype, cursor, locus, overflowHead, directoryIds, declaredDirectories);

        // Bucket capacity needs the value size, and with it the key array's true extent. Without it the walk can still
        // report structure (MAP-03/04) but cannot bound the keys, so the identity checks stand down rather than read
        // whatever lies past the key array.
        var recordSize = archetype.EntityRecordSize;
        var bucketCapacity = recordSize > 0 ? (geometry.Stride - BucketHeaderSize) / (sizeof(long) + recordSize) : 0;

        var entries = new Dictionary<long, int>();
        var walkedBuckets = new HashSet<int>();

        // Chain roots this map's entity records reference, gathered per component so CHN-06 can ask the reverse
        // question of each revision segment.
        var versioned = VersionedComponentsInSlotOrder(ctx, archetype);
        var referenced = new List<HashSet<int>>(versioned.Count);
        foreach (var name in versioned)
        {
            if (!ctx.ReferencedChainRoots.TryGetValue(name, out var set))
            {
                set = [];
                ctx.ReferencedChainRoots[name] = set;
            }

            referenced.Add(set);
        }

        for (var dirIndex = 0; dirIndex < directoryIds.Count; dirIndex++)
        {
            if (!Resolve(ctx, archetype, cursor, locus, directoryIds[dirIndex], "directory", directory))
            {
                continue;
            }

            for (var slot = 0; slot < BucketIdsPerDirectoryChunk; slot++)
            {
                var bucketId = MemoryMarshal.Read<int>(directory.AsSpan(slot * sizeof(int)));
                if (bucketId <= 0 || !walkedBuckets.Add(bucketId))
                {
                    continue;   // unpopulated, or a bucket already reached through another directory slot
                }

                WalkBucketChain(ctx, archetype, cursor, locus, bucketId, bucket, bucketCapacity, recordSize, entries,
                    walkedBuckets, referenced);
            }
        }

        if (bucketCapacity <= 0)
        {
            ctx.Findings.NoteSkipped($"{EntriesResolve}, {SlotsAreReachable}",
                $"the entity-record size for '{archetype.Name}' could not be derived from the manifest, so the map's "
                + "entries could not be located within their buckets");
            return;
        }

        CompareAgainstCluster(ctx, archetype, locus, entries);
    }

    /// <summary>
    /// <c>MAP-01</c> and <c>MAP-02</c> — the map's entries against the cluster's slots, in both directions.
    /// </summary>
    /// <remarks>
    /// Both directions are required and shipping one is the classic mistake: forward-only checking passes trivially on a
    /// map that is missing half its entries, which is exactly what a rebuild over pre-apply state produces
    /// (<c>RB-02</c>'s failure mode). The reverse direction is the one that catches it.
    /// </remarks>
    /// <param name="ctx">The scan context.</param>
    /// <param name="archetype">The archetype whose map was walked.</param>
    /// <param name="locus">Where to report.</param>
    /// <param name="entries">Entity key to the packed <c>ClusterLocation</c> the map's value record names.</param>
    private static void CompareAgainstCluster(ScanContext ctx, ArchetypeView archetype, Locus locus,
        Dictionary<long, int> entries)
    {
        if (archetype.ClusterSegmentRoot == 0
            || !ctx.ClusterEntityIds.TryGetValue(archetype.Name, out var clusterIds)
            || !ctx.ClusterEntityLocations.TryGetValue(archetype.Name, out var clusterLocations))
        {
            ctx.Findings.NoteSkipped($"{EntriesResolve}, {SlotsAreReachable}",
                $"archetype '{archetype.Name}' has no readable cluster to compare its EntityMap against");
            return;
        }

        var orphaned = 0;
        long firstOrphan = 0;
        var misdirected = 0;
        long firstMisdirected = 0;

        foreach (var (id, location) in entries)
        {
            if (!clusterLocations.TryGetValue(id, out var actual))
            {
                if (orphaned++ == 0)
                {
                    firstOrphan = id;
                }

                continue;
            }

            // The entry names a real entity — but does it name where that entity actually lives? An entry pointing at
            // the wrong slot resolves to another entity's data rather than failing, so a lookup returns the wrong row
            // with no error anywhere. Set comparison alone cannot see this.
            if (location != actual && misdirected++ == 0)
            {
                firstMisdirected = id;
            }
        }

        if (orphaned > 0)
        {
            ctx.Report(EntriesResolve, IntegritySeverity.Divergence, "", locus,
                $"The EntityMap for '{archetype.Name}' names entities the cluster does not hold.",
                $"{orphaned} entry(ies) — the first is entity id {firstOrphan} — name identities that appear in no live "
                + "cluster slot. A lookup of one resolves to a location that is free or belongs to another entity. The map "
                + "is derived from the cluster, so rebuilding it costs nothing.",
                Repairability.Lossless);
        }

        if (misdirected > 0)
        {
            var (badChunk, badSlot) = ClusterLocation.Unpack(entries[firstMisdirected]);
            var (realChunk, realSlot) = ClusterLocation.Unpack(clusterLocations[firstMisdirected]);

            ctx.Report(EntriesResolve, IntegritySeverity.Divergence, "", locus,
                $"The EntityMap for '{archetype.Name}' points entities at the wrong cluster slot.",
                $"{misdirected} entry(ies) resolve to a slot other than the one holding that entity. Entity {firstMisdirected} "
                + $"is mapped to cluster {badChunk} slot {badSlot} but lives in cluster {realChunk} slot {realSlot}. A lookup "
                + "does not fail — it returns whatever occupies the named slot, so one entity's identity serves another's "
                + "data. Rebuilding the map from the cluster costs nothing.",
                Repairability.Lossless);
        }

        var unreachable = 0;
        long firstUnreachable = 0;
        foreach (var id in clusterIds)
        {
            if (!entries.ContainsKey(id))
            {
                if (unreachable++ == 0)
                {
                    firstUnreachable = id;
                }
            }
        }

        if (unreachable > 0)
        {
            ctx.Report(SlotsAreReachable, IntegritySeverity.Divergence, "RB-02", locus,
                $"Live entities of '{archetype.Name}' are absent from its EntityMap.",
                $"{unreachable} live cluster slot(s) — the first is entity id {firstUnreachable} — have no entry in the "
                + "map. Those entities are present and intact but unfindable by id: a scan reaches them, a lookup does "
                + "not. This is the shape a map rebuilt over pre-apply state leaves, covering only the checkpointed half "
                + "of the data.",
                Repairability.Lossless);
        }
    }

    private static void CollectOverflowDirectories(ScanContext ctx, ArchetypeView archetype, ChunkCursor cursor,
        Locus locus, int head, List<int> into, int declaredDirectories)
    {
        var next = head;
        var visited = new HashSet<int>();
        var chunk = new byte[cursor.Stride];

        // -1 is the documented end-of-chain sentinel; 0 means there is no overflow at all.
        while (next > 0 && visited.Add(next))
        {
            if (!Resolve(ctx, archetype, cursor, locus, next, "overflow directory index", chunk))
            {
                return;
            }

            for (var i = 0; i < DirectoryIdsPerOverflowChunk && into.Count < declaredDirectories; i++)
            {
                var id = MemoryMarshal.Read<int>(chunk.AsSpan(sizeof(int) + (i * sizeof(int))));
                if (id != 0)
                {
                    into.Add(id);
                }
            }

            next = MemoryMarshal.Read<int>(chunk);
        }

        if (next > 0)
        {
            ctx.Report(PointersResolve, IntegritySeverity.Fatal, "RB-01", locus,
                $"The EntityMap overflow chain for '{archetype.Name}' is circular.",
                $"Following it returns to chunk {next}, which the walk has already visited. A reader without its own cycle "
                + "guard does not return.");
        }
    }

    /// <summary>
    /// Walks one bucket and its overflow chain, collecting every entry as identity plus the location it names.
    /// </summary>
    /// <remarks>
    /// The bucket is <b>SoA</b>: keys form one dense array from offset 12, the value records another after them. So an
    /// entry's key and its value are at different places computed from the same index, and the split point depends on
    /// <paramref name="bucketCapacity"/> — get that wrong and the keys still decode perfectly while every value is read
    /// from the middle of another record.
    /// </remarks>
    private static void WalkBucketChain(ScanContext ctx, ArchetypeView archetype, ChunkCursor cursor, Locus locus,
        int bucketId, byte[] bucket, int bucketCapacity, int recordSize, Dictionary<long, int> entries,
        HashSet<int> walkedBuckets, List<HashSet<int>> referencedChainRoots)
    {
        var next = bucketId;
        var visited = new HashSet<int>();
        var valuesAt = BucketHeaderSize + (bucketCapacity * sizeof(long));

        while (next > 0 && visited.Add(next))
        {
            if (!Resolve(ctx, archetype, cursor, locus, next, "bucket", bucket))
            {
                return;
            }

            // EntryCount is a claim, and a damaged bucket can claim more than it holds. Bound by the capacity the
            // geometry supports rather than by what the header says.
            var count = bucket[BucketEntryCountOffset];
            for (var i = 0; i < count && i < bucketCapacity; i++)
            {
                var id = MemoryMarshal.Read<long>(bucket.AsSpan(BucketHeaderSize + (i * sizeof(long))));
                if (id == 0)
                {
                    continue;
                }

                var location = -1;
                var recordAt = valuesAt + (i * recordSize);
                if (recordSize > 0 && recordAt + ClusterEntityRecordAccessor.SlotIndexOffset < bucket.Length)
                {
                    var clusterChunk = MemoryMarshal.Read<int>(
                        bucket.AsSpan(recordAt + ClusterEntityRecordAccessor.ClusterChunkIdOffset));
                    var slotIndex = bucket[recordAt + ClusterEntityRecordAccessor.SlotIndexOffset];
                    location = ClusterLocation.Pack(clusterChunk, slotIndex);

                    // The record's tail is one chain-root chunk id per Versioned slot, in the archetype's component
                    // order. Collected rather than validated here — CHN-06 owns the comparison, because the authority
                    // on what a valid root looks like is the revision segment, not the map.
                    for (var v = 0; v < referencedChainRoots.Count; v++)
                    {
                        var at = recordAt + ClusterEntityRecordAccessor.CompRevOffset + (v * sizeof(int));
                        if (at + sizeof(int) > bucket.Length)
                        {
                            break;
                        }

                        var chainRoot = MemoryMarshal.Read<int>(bucket.AsSpan(at));
                        if (chainRoot > 0)
                        {
                            referencedChainRoots[v].Add(chainRoot);
                        }
                    }
                }

                if (!entries.TryAdd(id, location))
                {
                    ctx.Report(NoDuplicateIds, IntegritySeverity.Fatal, "", locus,
                        $"The EntityMap for '{archetype.Name}' holds one entity id twice.",
                        $"Entity id {id} appears more than once across its buckets. A lookup returns whichever the probe "
                        + "reaches first, so two locations can be served for one identity and a write through one is "
                        + "invisible through the other.");
                }
            }

            next = MemoryMarshal.Read<int>(bucket.AsSpan(BucketOverflowOffset));
            if (next > 0)
            {
                walkedBuckets.Add(next);
            }
        }

        if (next > 0)
        {
            ctx.Report(PointersResolve, IntegritySeverity.Fatal, "RB-01", locus,
                $"An EntityMap bucket chain for '{archetype.Name}' is circular.",
                $"Following bucket {bucketId} returns to chunk {next}, which the walk has already visited.");
        }
    }

    /// <summary>
    /// Reads a chunk the directory names, reporting only what is provably damage.
    /// </summary>
    /// <remarks>
    /// <b>Out of range or unreadable is damage; in-range-but-free is not.</b> Linear hashing splits incrementally, so on
    /// a clean database a published directory slot can name a chunk whose allocation has not caught up with where
    /// <c>Next</c> has reached — observed on a healthy fixture, not theorised. Reporting that as a finding puts damage on
    /// healthy databases, which is the failure mode this feature exists to replace. What is <i>not</i> softened is
    /// following it: an unallocated chunk is never dereferenced either way, which is MAP-04's actual requirement.
    /// </remarks>
    private static bool Resolve(ScanContext ctx, ArchetypeView archetype, ChunkCursor cursor, Locus locus, int chunkId,
        string what, Span<byte> chunk)
    {
        if (!cursor.TryRead(chunkId, chunk, out var allocated))
        {
            ctx.Report(PointersResolve, IntegritySeverity.Divergence, "RB-01", locus,
                $"The EntityMap for '{archetype.Name}' names a {what} chunk that does not exist.",
                $"Chunk {chunkId} is outside the segment or its page could not be read. It was NOT followed — a hash "
                + "directory holds chunk-id pointers, and dereferencing a damaged one turns a diagnosable database into a "
                + "crash (RB-01). The entities behind it are unfindable by id until the map is rebuilt from the cluster.",
                Repairability.Lossless);
            return false;
        }

        if (!allocated)
        {
            ctx.Findings.NoteCaveat(
                $"The EntityMap for '{archetype.Name}' names {what} chunk {chunkId}, which its segment marks free. This "
                + "walk cannot distinguish an incremental-split intermediate from a stale pointer, so it was not followed "
                + "and not reported as damage.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads chunks of one segment into caller-owned buffers, caching the current page across hops of a walk.
    /// </summary>
    /// <remarks>
    /// <b>It copies, and that is the point.</b> The obvious design hands back a <see cref="ReadOnlySpan{T}"/> over the
    /// cursor's own page buffer, which is free and correct for a flat walk — and silently wrong for a nested one. This
    /// walk is nested three deep, and the outer levels hold their chunk while inner reads run: a directory being
    /// iterated is rewritten under the loop the moment a bucket on another page is read, so slots decode as zero and the
    /// walk quietly returns a subset. That happened, and it was misread as a bucket-layout problem twice before the
    /// aliasing was seen. Copying costs one stride-sized memcpy per hop and makes the failure impossible to express.
    /// </remarks>
    private sealed class ChunkCursor(IPageSource source, SegmentView segment, ChunkGeometry geometry)
    {
        private readonly byte[] _page = new byte[IntegrityConstants.PageSize];
        private int _loadedPage = -1;

        /// <summary>The segment's chunk stride — the size a destination buffer must be.</summary>
        public int Stride => geometry.Stride;

        /// <summary>Copies one chunk by id. <c>false</c> when the id does not address a readable chunk at all.</summary>
        /// <param name="chunkId">Chunk to read.</param>
        /// <param name="destination">Receives the chunk's bytes. Must be at least <see cref="Stride"/> long.</param>
        /// <param name="allocated">Whether the segment's own bitmap marks the chunk allocated.</param>
        public bool TryRead(int chunkId, Span<byte> destination, out bool allocated)
        {
            allocated = false;

            if (chunkId < 0 || destination.Length < geometry.Stride)
            {
                return false;
            }

            if (!geometry.TryLocate(chunkId, out var ordinal, out var chunkInPage) || ordinal >= segment.Pages.Count)
            {
                return false;
            }

            var filePage = segment.Pages[ordinal];
            if (_loadedPage != filePage)
            {
                if (!source.TryReadPage(filePage, _page))
                {
                    _loadedPage = -1;
                    return false;
                }

                _loadedPage = filePage;
            }

            var at = geometry.OffsetInPage(ordinal, chunkInPage);
            if (at + geometry.Stride > IntegrityConstants.PageSize)
            {
                return false;
            }

            allocated = geometry.IsChunkAllocated(_page, ordinal == 0, chunkInPage);
            _page.AsSpan(at, geometry.Stride).CopyTo(destination);
            return true;
        }
    }
}
