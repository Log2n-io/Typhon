using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// <c>MAP</c> — the per-archetype EntityMap: a linear hash map whose bucket <c>b</c> is chunk <c>b + 1</c> and whose overflow chains are chunk-id pointers.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>MAP-04</c> is a rule about this code, not a finding it emits.</b> The catalogue says so in as many words:
/// <i>"a scanner-safety rule as much as a check ... the offline scanner must validate every pointer against the
/// allocation map before following it — a checker that crashes on the databases it was built to diagnose is worse than
/// useless"</i>. <c>RB-01</c> supplies the urgency: a torn EntityMap page holds chunk-id pointers, and trusting one
/// <i>"dereferences garbage into a hard process crash before any loud-fail can fire"</i>. So every hop is range- and
/// allocation-checked <b>before</b> the read; the finding is what survives the walk.
/// </para>
/// <para>
/// <b>No directory (#1205).</b> Buckets are found by position: the walk reads the bucket count from the meta — validated by the
/// same predicate the engine opens with (<see cref="PagedHashMapMeta.IsUsable"/>) — and visits chunks <c>1 … count</c>. The
/// pointers left to validate are the overflow links: each must end the chain (<c>-1</c>) or name a chunk past the buckets and
/// inside the segment, reached by no other chain, allocated, and naming its bucket as owner, which is what a split relies on
/// to move it. The walk then checks what EMAP-01 states as a whole: every allocated chunk past the buckets is reached by
/// exactly one chain — one reached by none would defer a split for good once the frontier reaches it — and, after a clean
/// close, the entries counted agree with the meta's.
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
/// <b>Every chunk is copied out before the next is read.</b> The walk nests — meta → bucket chain — and a cursor handing
/// back a span over one reused page buffer means a nested read silently rewrites the chunk the outer loop is still
/// reading. That defect shipped: it is what made an earlier version of this walk recover a strict subset of a healthy
/// map, which read as a layout problem for two rounds of debugging. Owned buffers per nesting level make it
/// unrepresentable rather than merely fixed.
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

    /// <summary>Check code: every chunk-id pointer the map holds resolves before it is dereferenced.</summary>
    public const string PointersResolve = "CHK-MAP-04";

    /// <summary>
    /// Check code: the map's structure as a whole — every bucket chunk allocated, every bucket's entry count within its capacity, every
    /// allocated chunk past the buckets reached by exactly one chain, and the entries counted equal to the meta's count.
    /// </summary>
    public const string StructureHolds = "CHK-MAP-05";

    /// <summary>The rule the map's structure findings enforce.</summary>
    private const string StructureRule = "EMAP-01";

    private const int BucketOwnerOffset = 0;            // an overflow chunk's owning bucket + 1, where a primary keeps its latch
    private const int BucketHeaderSize = 12;
    private const int BucketEntryCountOffset = 4;
    private const int BucketOverflowOffset = 8;

    /// <summary>
    /// The map's meta record lives in chunk <b>0</b> — the slot every other chunk-based segment reserves as its null
    /// sentinel (<c>PagedHashMapBase</c> reads it as <c>GetChunkReadOnly&lt;PagedHashMapMeta&gt;(0)</c>).
    /// </summary>
    /// <remarks>
    /// Worth stating rather than assuming, because assuming cost a debugging round: read from chunk 1 instead and the
    /// meta is a bucket record whose fields read as garbage, so a healthy database reports a meta the engine could not
    /// open.
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
                + "buckets were not walked.");
            return;
        }

        var cursor = new ChunkCursor(ctx.Source, segment, geometry);
        var locus = new Locus(segment.RootPageIndex, segment.RootPageIndex, segment.Kind);

        // One buffer per nesting level, owned by this frame. See the type remarks: sharing the cursor's page across a
        // nested read is what broke an earlier walk.
        var meta = new byte[Math.Max(geometry.Stride, Unsafe.SizeOf<PagedHashMapMeta>())];
        var bucket = new byte[geometry.Stride];

        // A file a clean close left is settled: its map is what the engine will serve. Any other — a crash's, or a live database — may
        // hold a map a checkpoint captured mid-split (a chunk claimed and not yet linked, a link and the free it precedes on pages
        // written at different moments), and the open after an unclean close rebuilds the map anyway (RB-01). Its structure is still
        // walked, with every pointer validated before it is followed, but not judged: what would be a finding is counted into a caveat.
        var judge = new StructureJudge(ctx, archetype, locus, ctx.Bootstrap?.ReadWatermarks().CleanShutdown == true);

        if (!cursor.TryRead(MetaChunkId, meta, out _))
        {
            judge.Report(PointersResolve, "RB-01",
                $"The EntityMap for '{archetype.Name}' has no readable meta record.",
                $"Chunk {MetaChunkId} of the segment rooted at page {segment.RootPageIndex} could not be read, so the map's "
                + "bucket count is unknown. Every entity of this archetype is unfindable by id until the map is rebuilt from "
                + "the cluster, which loses nothing.");
            judge.Close();
            return;
        }

        var capacity = geometry.Capacity(segment.Pages.Count);
        var header = MemoryMarshal.Read<PagedHashMapMeta>(meta);
        if (!PagedHashMapMeta.IsUsable(header, DatabaseEngine.EntityMapInitialBuckets, capacity, allowMultiple: false, out var reason))
        {
            judge.Report(PointersResolve, "RB-01",
                $"The EntityMap for '{archetype.Name}' has a meta record this engine cannot open.",
                $"Its meta has {reason}, over a segment of {capacity} chunks — the same test the engine's open applies, which "
                + "refuses it. A map written before the directory was removed (#1205), or a damaged meta: its buckets cannot be "
                + "located, so its entities are unfindable by id until the map is rebuilt from the cluster, which loses nothing.");
            judge.Close();
            return;
        }

        var declaredBuckets = (int)header.BucketCount;

        // Bucket capacity needs the value size, and with it the key array's true extent. Without it the walk can still
        // report structure (MAP-03/04/05) but cannot bound the keys, so the identity checks stand down rather than read
        // whatever lies past the key array.
        var recordSize = archetype.EntityRecordSize;
        var bucketCapacity = recordSize > 0 ? (geometry.Stride - BucketHeaderSize) / (sizeof(long) + recordSize) : 0;

        var entries = new Dictionary<long, int>((int)Math.Min(header.EntryCount, Math.Min((long)declaredBuckets * Math.Max(1, bucketCapacity), 1 << 24)));

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

        // One bit per chunk, set when a chain reaches it: what tells a cross-link or a cycle from a chain, and a leaked chunk from a linked one.
        var walk = new MapWalk(declaredBuckets, capacity);
        for (var b = 0; b < declaredBuckets; b++)
        {
            WalkBucketChain(ctx, judge, archetype, cursor, b, bucket, bucketCapacity, recordSize, entries, walk, referenced);
        }

        ReportStructure(judge, archetype, cursor, header, walk, entriesCountable: bucketCapacity > 0);
        judge.Close();

        if (bucketCapacity <= 0)
        {
            ctx.Findings.NoteSkipped($"{EntriesResolve}, {SlotsAreReachable}",
                $"the entity-record size for '{archetype.Name}' could not be derived from the manifest, so the map's "
                + "entries could not be located within their buckets");
            return;
        }

        CompareAgainstCluster(ctx, archetype, locus, entries);
    }

    /// <summary>What one map's walk accumulates: which chunks a chain reached, and what it counted on the way.</summary>
    private sealed class MapWalk(int declaredBuckets, int capacity)
    {
        private readonly ulong[] _reached = new ulong[((long)capacity + 63) >> 6];

        public int DeclaredBuckets { get; } = declaredBuckets;

        public int Capacity { get; } = capacity;

        public long EntriesCounted { get; set; }

        public int FreeBuckets { get; set; }

        public int FirstFreeBucket { get; set; } = -1;

        public int OverfullBuckets { get; private set; }

        public int FirstOverfull { get; private set; } = -1;

        public int BucketCapacity { get; private set; }

        public void NoteOverfull(int chunkId, int bucketCapacity)
        {
            if (OverfullBuckets++ == 0)
            {
                FirstOverfull = chunkId;
                BucketCapacity = bucketCapacity;
            }
        }

        /// <summary>Marks <paramref name="chunkId"/> reached; false when a chain had already reached it.</summary>
        public bool Reach(int chunkId)
        {
            ref var word = ref _reached[chunkId >> 6];
            var bit = 1UL << (chunkId & 63);
            if ((word & bit) != 0)
            {
                return false;
            }

            word |= bit;
            return true;
        }

        public bool WasReached(int chunkId) => (_reached[chunkId >> 6] & (1UL << (chunkId & 63))) != 0;
    }

    /// <summary>
    /// Reports one map's structure findings (<c>MAP-04</c>, <c>MAP-05</c>) — all <see cref="IntegritySeverity.Divergence"/>, the class of a derived
    /// structure regenerated losslessly — or, on a file that is not settled, counts them into one caveat.
    /// </summary>
    private sealed class StructureJudge(ScanContext ctx, ArchetypeView archetype, Locus locus, bool settled)
    {
        private int _unjudged;

        public void Report(string code, string ruleId, string summary, string detail)
        {
            if (!settled)
            {
                _unjudged++;
                return;
            }

            ctx.Report(code, IntegritySeverity.Divergence, ruleId, locus, summary, detail, Repairability.Lossless);
        }

        /// <summary>Whether findings are judged: the file was left by a clean close.</summary>
        public bool Settled => settled;

        /// <summary>Where this map's findings are reported.</summary>
        public Locus Locus => locus;

        /// <summary>Counts an observation an unsettled file does not support judging.</summary>
        public void SetAside() => _unjudged++;

        public void Close()
        {
            if (_unjudged > 0)
            {
                ctx.Findings.NoteCaveat(
                    $"The EntityMap for '{archetype.Name}' was walked but its structure not judged: this file was not left by a clean close, so a "
                    + $"checkpoint may have captured it mid-split, and the open after such a close rebuilds the map (RB-01). {_unjudged} "
                    + $"observation(s) ({PointersResolve}/{StructureHolds}/{NoDuplicateIds}) were set aside; re-check after a clean close.");
            }
        }
    }

    /// <summary>
    /// <c>MAP-05</c> — EMAP-01 as a whole: the bucket chunks allocated, every allocated chunk past the buckets reached by a chain,
    /// and, after a clean close, the entries counted equal to the meta's count.
    /// </summary>
    /// <remarks>
    /// A chunk allocated and reached by no chain is invisible to lookups, but the bucket frontier reaching it makes every split
    /// defer: the map stops growing and its chains lengthen without bound. The count is checked only after a clean close: the
    /// checkpoint persists it, so a file left by a crash may lag the chains it carries, and the crash path rebuilds the map anyway.
    /// </remarks>
    private static void ReportStructure(StructureJudge judge, ArchetypeView archetype, ChunkCursor cursor, PagedHashMapMeta header, MapWalk walk,
        bool entriesCountable)
    {
        if (walk.FreeBuckets > 0)
        {
            judge.Report(StructureHolds, StructureRule,
                $"The EntityMap for '{archetype.Name}' declares buckets whose chunks are free.",
                $"{walk.FreeBuckets} bucket chunk(s) — the first is bucket {walk.FirstFreeBucket} — are free although the meta counts "
                + $"{walk.DeclaredBuckets} buckets. A split takes a bucket's chunk before it publishes the count (EMAP-01), so a healthy map "
                + "never has one. The entries hashed there are unfindable; rebuilding the map from the cluster costs nothing.");
        }

        if (walk.OverfullBuckets > 0)
        {
            judge.Report(StructureHolds, StructureRule,
                $"The EntityMap for '{archetype.Name}' has buckets that claim more entries than a chunk holds.",
                $"{walk.OverfullBuckets} chunk(s) of its chains — the first is chunk {walk.FirstOverfull} — record an entry count above the "
                + $"bucket capacity ({walk.BucketCapacity}). The engine reads that many keys, past the key array into the values; the walk "
                + "counted the capacity only. Rebuilding the map from the cluster costs nothing.");
        }

        var leaked = 0;
        var firstLeaked = -1;
        for (var chunkId = walk.DeclaredBuckets + 1; chunkId < walk.Capacity; chunkId++)
        {
            if (!walk.WasReached(chunkId) && cursor.IsAllocated(chunkId) && leaked++ == 0)
            {
                firstLeaked = chunkId;
            }
        }

        if (leaked > 0)
        {
            judge.Report(StructureHolds, StructureRule,
                $"The EntityMap for '{archetype.Name}' holds allocated chunks no chain reaches.",
                $"{leaked} chunk(s) past the buckets — the first is chunk {firstLeaked} — are allocated and linked to nothing. Lookups "
                + "never see them, but a split that needs one for a new bucket cannot claim it and defers for good: the map stops "
                + "growing. Rebuilding the map from the cluster frees them, losing nothing.");
        }

        if (!entriesCountable)
        {
            return;   // the bucket capacity is unknown, so the entries could not be counted (noted with MAP-01/02)
        }

        // After a crash the checkpointed count trails the replayed entries by design: not a finding, and set aside with the rest.
        if (header.EntryCount != walk.EntriesCounted)
        {
            judge.Report(StructureHolds, StructureRule,
                $"The EntityMap for '{archetype.Name}' miscounts its entries.",
                $"Its meta records {header.EntryCount} entries and its chains hold {walk.EntriesCounted}. The count drives when the map "
                + "splits, so it grows too early or too late until the count is right. Rebuilding the map from the cluster costs nothing.");
        }
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

    /// <summary>
    /// Walks one bucket and its overflow chain, collecting every entry as identity plus the location it names.
    /// </summary>
    /// <remarks>
    /// The bucket is <b>SoA</b>: keys form one dense array from offset 12, the value records another after them. So an
    /// entry's key and its value are at different places computed from the same index, and the split point depends on
    /// <paramref name="bucketCapacity"/> — get that wrong and the keys still decode perfectly while every value is read
    /// from the middle of another record.
    /// </remarks>
    private static void WalkBucketChain(ScanContext ctx, StructureJudge judge, ArchetypeView archetype, ChunkCursor cursor,
        int bucketIndex, byte[] bucket, int bucketCapacity, int recordSize, Dictionary<long, int> entries,
        MapWalk walk, List<HashSet<int>> referencedChainRoots)
    {
        var bucketId = bucketIndex + 1;
        var valuesAt = BucketHeaderSize + (bucketCapacity * sizeof(long));
        var misowned = 0;
        var firstMisowned = 0;
        walk.Reach(bucketId);

        if (!cursor.TryRead(bucketId, bucket, out var allocated))
        {
            judge.Report(PointersResolve, "RB-01",
                $"The EntityMap for '{archetype.Name}' declares a bucket its segment cannot hold.",
                $"Bucket {bucketIndex} is chunk {bucketId}, outside the segment or on a page that could not be read. The entities "
                + "hashed there are unfindable by id until the map is rebuilt from the cluster.");
            return;
        }

        if (!allocated)
        {
            if (walk.FreeBuckets++ == 0)
            {
                walk.FirstFreeBucket = bucketIndex;
            }

            return;   // a free chunk's bytes are nobody's: not followed
        }

        var chunkId = bucketId;
        while (true)
        {
            // An overflow chunk names the bucket that owns it (PagedHashMapBase.TagOverflowOwner): what a split reads to move it
            // out of the chunk the next bucket takes. A wrong owner makes that move defer for good — the map stops growing.
            if (chunkId != bucketId && MemoryMarshal.Read<int>(bucket.AsSpan(BucketOwnerOffset)) != bucketIndex + 1 && misowned++ == 0)
            {
                firstMisowned = chunkId;
            }

            // EntryCount is a claim, and a damaged bucket can claim more than it holds. Bound by the capacity the
            // geometry supports rather than by what the header says.
            var count = bucket[BucketEntryCountOffset];
            walk.EntriesCounted += Math.Min(count, Math.Max(bucketCapacity, 0));
            if (bucketCapacity > 0 && count > bucketCapacity)
            {
                walk.NoteOverfull(chunkId, bucketCapacity);
            }

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
                    // A split rewrites the old bucket and writes the new one, on different pages: a checkpoint can capture one and not the other,
                    // leaving the moved entries in both. So, like the structure, only judged on a settled file.
                    if (!judge.Settled)
                    {
                        judge.SetAside();
                    }
                    else
                    {
                        ctx.Report(NoDuplicateIds, IntegritySeverity.Fatal, "", judge.Locus,
                            $"The EntityMap for '{archetype.Name}' holds one entity id twice.",
                            $"Entity id {id} appears more than once across its buckets. A lookup returns whichever the probe "
                            + "reaches first, so two locations can be served for one identity and a write through one is "
                            + "invisible through the other.");
                    }
                }
            }

            var next = MemoryMarshal.Read<int>(bucket.AsSpan(BucketOverflowOffset));
            if (next == -1)
            {
                break;
            }

            // Validated before it is followed (MAP-04): an overflow link names a chunk past the buckets and inside the segment. 0 is the
            // meta, a value below -1 is before the chunk area, and one into 1 … BucketCount is another bucket's primary — a cross-link.
            if (next <= walk.DeclaredBuckets || next >= walk.Capacity)
            {
                judge.Report(PointersResolve, "RB-01",
                    $"An EntityMap overflow link for '{archetype.Name}' points where no overflow chunk can be.",
                    $"Bucket {bucketIndex}'s chain links chunk {chunkId} to {next}: "
                    + (next < -1 || next == 0 ? "not a chunk any chain can name" : next >= walk.Capacity ? "past the segment's end"
                        : "a bucket's own chunk") + $" (overflow chunks lie in ({walk.DeclaredBuckets}, {walk.Capacity})). It was NOT "
                    + "followed — dereferencing a damaged chunk-id pointer turns a diagnosable database into a crash (RB-01). The "
                    + "entries behind it are unfindable by id until the map is rebuilt from the cluster.");
                break;
            }

            // Exactly one chain per chunk (MAP-05): the one bitmap for every chain bounds the walk's hops by the segment's capacity, so
            // it always ends.
            if (!walk.Reach(next))
            {
                judge.Report(StructureHolds, StructureRule,
                    $"An EntityMap bucket chain for '{archetype.Name}' reaches a chunk twice.",
                    $"Bucket {bucketIndex}'s chain links chunk {chunkId} to {next}, which this chain or another has already reached — a "
                    + "cycle, or two chains sharing a tail. It was NOT followed. A lookup that walks a cycle never ends; rebuilding the map "
                    + "from the cluster costs nothing.");
                break;
            }

            var readable = cursor.TryRead(next, bucket, out allocated);
            if (!readable || !allocated)
            {
                judge.Report(PointersResolve, "RB-01",
                    $"An EntityMap overflow link for '{archetype.Name}' names a chunk that holds nothing.",
                    $"Bucket {bucketIndex}'s chain links chunk {chunkId} to {next}, which "
                    + (readable ? "its segment marks free" : "could not be read") + ". It was NOT followed; the entries behind it "
                    + "are unfindable by id until the map is rebuilt from the cluster.");
                break;
            }

            chunkId = next;
        }

        if (misowned > 0)
        {
            judge.Report(PointersResolve, StructureRule,
                $"An EntityMap overflow chunk for '{archetype.Name}' does not name its bucket.",
                $"{misowned} overflow chunk(s) of bucket {bucketIndex} — the first is chunk {firstMisowned} — record another owner. "
                + "A split that needs that chunk for a new bucket cannot move it, and defers for good: the map stops growing "
                + "and its chains lengthen. Rebuilding the map from the cluster costs nothing.");
        }
    }

    /// <summary>
    /// Reads chunks of one segment into caller-owned buffers, caching the two pages read last across the hops of a walk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It copies, and that is the point.</b> The obvious design hands back a <see cref="ReadOnlySpan{T}"/> over the
    /// cursor's own page buffer, which is free and correct for a flat walk — and silently wrong for a nested one. An earlier
    /// walk held a chunk while inner reads ran, so slots decoded as zero and the walk quietly returned a subset — misread as a
    /// bucket-layout problem twice before the aliasing was seen. Copying costs one stride-sized memcpy per hop and makes the
    /// failure impossible to express.
    /// </para>
    /// <para>
    /// <b>Two pages, not one.</b> The walk reads the buckets in order and leaves for an overflow chunk whenever a bucket chains:
    /// with one cached page, every chain read the bucket's page back on return. The second slot keeps it.
    /// </para>
    /// </remarks>
    private sealed class ChunkCursor(IPageSource source, SegmentView segment, ChunkGeometry geometry)
    {
        private readonly byte[][] _pages = [new byte[IntegrityConstants.PageSize], new byte[IntegrityConstants.PageSize]];
        private readonly int[] _loaded = [-1, -1];
        private int _lastUsed;

        /// <summary>The segment's chunk stride — the size a destination buffer must be.</summary>
        public int Stride => geometry.Stride;

        /// <summary>Copies one chunk by id. <c>false</c> when the id does not address a readable chunk at all.</summary>
        /// <param name="chunkId">Chunk to read.</param>
        /// <param name="destination">Receives the chunk's bytes. Must be at least <see cref="Stride"/> long.</param>
        /// <param name="allocated">Whether the segment's own bitmap marks the chunk allocated.</param>
        public bool TryRead(int chunkId, Span<byte> destination, out bool allocated)
        {
            allocated = false;
            if (destination.Length < geometry.Stride || !TryPage(chunkId, out var page, out var ordinal, out var chunkInPage))
            {
                return false;
            }

            var at = geometry.OffsetInPage(ordinal, chunkInPage);
            if (at + geometry.Stride > IntegrityConstants.PageSize)
            {
                return false;
            }

            allocated = geometry.IsChunkAllocated(page, ordinal == 0, chunkInPage);
            page.AsSpan(at, geometry.Stride).CopyTo(destination);
            return true;
        }

        /// <summary>Whether the segment's bitmap marks <paramref name="chunkId"/> allocated; false when it cannot be read.</summary>
        public bool IsAllocated(int chunkId)
            => TryPage(chunkId, out var page, out var ordinal, out var chunkInPage) && geometry.IsChunkAllocated(page, ordinal == 0, chunkInPage);

        private bool TryPage(int chunkId, out byte[] page, out int ordinal, out int chunkInPage)
        {
            page = null;
            if (chunkId < 0 || !geometry.TryLocate(chunkId, out ordinal, out chunkInPage) || ordinal >= segment.Pages.Count)
            {
                ordinal = -1;
                chunkInPage = -1;
                return false;
            }

            var filePage = segment.Pages[ordinal];
            for (var slot = 0; slot < 2; slot++)
            {
                if (_loaded[slot] == filePage)
                {
                    _lastUsed = slot;
                    page = _pages[slot];
                    return true;
                }
            }

            var victim = 1 - _lastUsed;
            if (!source.TryReadPage(filePage, _pages[victim]))
            {
                _loaded[victim] = -1;
                return false;
            }

            _loaded[victim] = filePage;
            _lastUsed = victim;
            page = _pages[victim];
            return true;
        }
    }
}
