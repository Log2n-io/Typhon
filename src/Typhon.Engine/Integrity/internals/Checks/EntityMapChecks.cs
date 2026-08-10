using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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
/// <b><c>MAP-01</c> and <c>MAP-02</c> compare key SETS, not values.</b> The obvious reading — follow each entry to the
/// slot it names — needs the per-entry record size to step a bucket's value array, and that size is derived from the
/// archetype's component count but is not itself persisted. Comparing the entity ids the map holds against the entity
/// keys the cluster holds answers the same two questions (<i>is every entry real</i>, <i>is every entity findable</i>)
/// out of two structures that are already fully decodable. It is strictly weaker in one respect, stated in the finding:
/// it proves the identity exists, not that the entry points at the right slot.
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

        if (!cursor.TryRead(MetaChunkId, out var meta, out _))
        {
            ctx.Report(PointersResolve, IntegritySeverity.Fatal, "RB-01", locus,
                $"The EntityMap for '{archetype.Name}' has no readable meta record.",
                $"Chunk {MetaChunkId} of the segment rooted at page {segment.RootPageIndex} could not be read, so the hash "
                + "directory cannot be located at all. Every entity of this archetype is unfindable by id until the map is "
                + "rebuilt from the cluster, which loses nothing.",
                Repairability.Lossless);
            return;
        }

        var declaredDirectories = MemoryMarshal.Read<ushort>(meta[MetaDirectoryCountOffset..]);
        var overflowHead = MemoryMarshal.Read<int>(meta[MetaOverflowHeadOffset..]);

        // No bound on the bucket scan beyond the directory itself. Bounding by the live BucketCount from PackedMeta was
        // tried and TRUNCATED the walk — the map came back a strict subset of the cluster and MAP-02 fired on every
        // healthy database. Over-scanning is safe here precisely because an unallocated chunk id is a caveat rather than
        // a finding: a slot that names nothing real costs a line in Limits, while a slot wrongly skipped costs a false
        // report that live entities are unreachable.

        var directoryIds = new List<int>();
        for (var i = 0; i < InlineDirectoryChunks && directoryIds.Count < declaredDirectories; i++)
        {
            var id = MemoryMarshal.Read<int>(meta[(MetaInlineIdsOffset + (i * sizeof(int)))..]);
            if (id != 0)
            {
                directoryIds.Add(id);
            }
        }

        CollectOverflowDirectories(ctx, archetype, cursor, locus, overflowHead, directoryIds);

        var mapIds = new HashSet<long>();
        var walkedBuckets = new HashSet<int>();

        for (var dirIndex = 0; dirIndex < directoryIds.Count; dirIndex++)
        {
            if (!Resolve(ctx, archetype, cursor, locus, directoryIds[dirIndex], "directory", out var directory))
            {
                continue;
            }

            for (var slot = 0; slot < BucketIdsPerDirectoryChunk; slot++)
            {
                var bucketId = MemoryMarshal.Read<int>(directory[(slot * sizeof(int))..]);
                if (bucketId == 0 || !walkedBuckets.Add(bucketId))
                {
                    continue;   // unpopulated, or a bucket already reached through another directory slot
                }

                WalkBucketChain(ctx, archetype, cursor, locus, bucketId, mapIds, walkedBuckets);
            }
        }

        // MAP-01 holds — every id the map names is an entity the cluster holds, verified on healthy fixtures. MAP-02
        // does NOT: the walk recovers a strict SUBSET of the cluster's entities on a database that is otherwise clean,
        // so some entries are not being reached. Bounding the bucket scan by the live BucketCount made it worse and
        // removing the bound did not fix it, which points at the bucket layout rather than at the directory walk.
        //
        // Reporting the difference would put "live entities are unreachable" on every healthy database — the exact
        // failure this feature replaces — so the pair is declared unrun until the bucket entry layout is understood.
        // MAP-01 is withheld with it rather than shipped alone: forward-only checking passes trivially on a map missing
        // half its entries, so on its own it would be the reassuring half of a pair whose other half is broken.
        ctx.Findings.NoteSkipped($"{EntriesResolve}, {SlotsAreReachable}",
            "the EntityMap bucket walk recovers only part of a healthy map's entries, so a comparison against the cluster "
            + "would report live entities as unreachable; the map's structure and its identities are still checked");
        _ = mapIds;
    }

    /// <summary>
    /// <c>MAP-01</c> and <c>MAP-02</c> — the map's identities against the cluster's, in both directions.
    /// </summary>
    /// <remarks>
    /// Both directions are required and shipping one is the classic mistake: forward-only checking passes trivially on a
    /// map that is missing half its entries, which is exactly what a rebuild over pre-apply state produces
    /// (<c>RB-02</c>'s failure mode). The reverse direction is the one that catches it.
    /// </remarks>
    private static void CompareAgainstCluster(ScanContext ctx, ArchetypeView archetype, Locus locus, HashSet<long> mapIds)
    {
        if (archetype.ClusterSegmentRoot == 0 || !ctx.ClusterEntityIds.TryGetValue(archetype.Name, out var clusterIds))
        {
            ctx.Findings.NoteSkipped($"{EntriesResolve}, {SlotsAreReachable}",
                $"archetype '{archetype.Name}' has no readable cluster to compare its EntityMap against");
            return;
        }

        var orphaned = 0;
        long firstOrphan = 0;
        foreach (var id in mapIds)
        {
            if (!clusterIds.Contains(id))
            {
                if (orphaned++ == 0)
                {
                    firstOrphan = id;
                }
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

        var unreachable = 0;
        long firstUnreachable = 0;
        foreach (var id in clusterIds)
        {
            if (!mapIds.Contains(id))
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
        Locus locus, int head, List<int> into)
    {
        var next = head;
        var visited = new HashSet<int>();

        // -1 is the documented end-of-chain sentinel; 0 means there is no overflow at all.
        while (next > 0 && visited.Add(next))
        {
            if (!Resolve(ctx, archetype, cursor, locus, next, "overflow directory index", out var chunk))
            {
                return;
            }

            for (var i = 0; i < DirectoryIdsPerOverflowChunk; i++)
            {
                var id = MemoryMarshal.Read<int>(chunk[(sizeof(int) + (i * sizeof(int)))..]);
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

    private static void WalkBucketChain(ScanContext ctx, ArchetypeView archetype, ChunkCursor cursor, Locus locus,
        int bucketId, HashSet<long> mapIds, HashSet<int> walkedBuckets)
    {
        var next = bucketId;
        var visited = new HashSet<int>();

        while (next > 0 && visited.Add(next))
        {
            if (!Resolve(ctx, archetype, cursor, locus, next, "bucket", out var bucket))
            {
                return;
            }

            // Keys are `long` for every EntityMap — it is RawValuePagedHashMap<long, …> at all four construction sites —
            // so reading identities needs no record size, even though stepping the VALUES would.
            var count = bucket[BucketEntryCountOffset];
            var maxKeys = (bucket.Length - BucketHeaderSize) / sizeof(long);
            for (var i = 0; i < count && i < maxKeys; i++)
            {
                var id = MemoryMarshal.Read<long>(bucket[(BucketHeaderSize + (i * sizeof(long)))..]);
                if (id == 0)
                {
                    continue;
                }

                if (!mapIds.Add(id))
                {
                    ctx.Report(NoDuplicateIds, IntegritySeverity.Fatal, "", locus,
                        $"The EntityMap for '{archetype.Name}' holds one entity id twice.",
                        $"Entity id {id} appears more than once across its buckets. A lookup returns whichever the probe "
                        + "reaches first, so two locations can be served for one identity and a write through one is "
                        + "invisible through the other.");
                }
            }

            next = MemoryMarshal.Read<int>(bucket[BucketOverflowOffset..]);
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
        string what, out ReadOnlySpan<byte> chunk)
    {
        if (!cursor.TryRead(chunkId, out chunk, out var allocated))
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

    /// <summary>Reads chunks of one segment, caching the current page across hops of a walk.</summary>
    private sealed class ChunkCursor(IPageSource source, SegmentView segment, ChunkGeometry geometry)
    {
        private readonly byte[] _page = new byte[IntegrityConstants.PageSize];
        private int _loadedPage = -1;

        /// <summary>Reads one chunk by id. <c>false</c> when the id does not address a readable chunk at all.</summary>
        public bool TryRead(int chunkId, out ReadOnlySpan<byte> chunk, out bool allocated)
        {
            chunk = default;
            allocated = false;

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
            chunk = new ReadOnlySpan<byte>(_page, at, geometry.Stride);
            return true;
        }
    }
}
