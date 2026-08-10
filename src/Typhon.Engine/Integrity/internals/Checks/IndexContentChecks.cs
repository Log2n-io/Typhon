using System;
using System.Collections.Generic;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// <c>IDX-02</c> and <c>IDX-05</c> — what an index's entries say, once the node layout is known.
/// </summary>
/// <remarks>
/// <para>
/// These are the checks that had to wait for the key width. Which node layout a tree uses follows from the indexed
/// field's type, which needed <c>FieldR1</c> — and the field a tree indexes is identified by its directory entry's
/// <c>StableId</c>, which is the field id, resolved through the archetype's component list at the entry's <c>Slot</c>.
/// Every link in that chain is persisted; none of it needs a schema assembly.
/// </para>
/// <para>
/// <b>Only per-archetype cluster index segments are checked for locations.</b> Their values are packed
/// <c>ClusterLocation</c>s — a cluster chunk id and a slot — which is what makes <c>IX-02</c>'s claim checkable against
/// cluster occupancy. A per-component index segment stores something else entirely, so it is walked for order and
/// uniqueness but not for location.
/// </para>
/// </remarks>
internal static class IndexContentChecks
{
    /// <summary>Check code: every index value names an occupied slot of its own archetype's cluster.</summary>
    public const string ValuesResolve = "CHK-IDX-02";

    /// <summary>Check code: keys are ordered within a node, bounded by its high key, and unique where declared unique.</summary>
    public const string KeyOrder = "CHK-IDX-05";

    /// <summary>Check code: a multi-value entry's buffer resolves, terminates, and holds live locations.</summary>
    public const string MultiValueBuffers = "CHK-IDX-07";

    /// <summary>Maximum nodes visited per tree, so a damaged level chain cannot turn into an unbounded walk.</summary>
    private const int MaxNodesPerTree = 1 << 20;

    /// <summary>Runs both checks. Requires <see cref="ScanDepth.Deep"/> and a readable manifest.</summary>
    /// <param name="ctx">The scan context, after the cluster pass has published occupancy.</param>
    public static void Run(ScanContext ctx)
    {
        if (!ctx.AtLeast(ScanDepth.Deep))
        {
            ctx.Findings.NoteSkipped($"{ValuesResolve}, {KeyOrder}", "needs Deep depth");
            return;
        }

        if (ctx.Manifest is not { IsUsable: true })
        {
            ctx.Findings.NoteSkipped($"{ValuesResolve}, {KeyOrder}",
                "the schema manifest could not be read, so index node layouts cannot be resolved");
            return;
        }

        var reader = new IndexDirectoryReader(ctx.Source);
        var entries = new List<IndexTreeEntry>();
        var walked = 0;

        foreach (var archetype in ctx.Manifest.Archetypes.Values)
        {
            foreach (var root in new[] { archetype.IndexRoot, archetype.String64IndexRoot })
            {
                if (root == 0 || !ctx.Segments.TryGetValue(root, out var segment) || segment.Pages.Count == 0)
                {
                    continue;
                }

                var page = new byte[IntegrityConstants.PageSize];
                if (!ctx.Source.TryReadPage(segment.Pages[0], page))
                {
                    continue;
                }

                var geometry = ChunkGeometry.FromPage(page);
                if (!geometry.IsUsable || !reader.TryReadDirectory(segment, geometry, entries))
                {
                    continue;   // IDX-06 already reported an unreadable directory
                }

                foreach (var entry in entries)
                {
                    if (WalkTree(ctx, reader, archetype, segment, geometry, entry))
                    {
                        walked++;
                    }
                }
            }
        }

        if (walked == 0)
        {
            ctx.Findings.NoteSkipped($"{ValuesResolve}, {KeyOrder}",
                "no per-archetype index tree could be matched to an indexed field, so no node layout could be resolved");
        }
    }

    /// <summary>Resolves a tree's indexed field, then walks every node of it. Returns whether the tree was walked.</summary>
    private static bool WalkTree(ScanContext ctx, IndexDirectoryReader reader, ArchetypeView archetype,
        SegmentView segment, ChunkGeometry geometry, IndexTreeEntry entry)
    {
        if (entry.RootChunkId <= 0 || !TryResolveField(ctx, archetype, entry, out var field))
        {
            return false;
        }

        var layout = IndexNodeLayout.ForFieldType(field.Type);
        if (!layout.IsUsable || layout.KeysOffset + (layout.Capacity * layout.KeySize) > geometry.Stride)
        {
            ctx.Findings.NoteCaveat($"The index on '{archetype.Name}.{field.Name}' uses a node layout that does not fit the "
                + $"segment's {geometry.Stride}-byte stride, so its entries were not read.");
            return false;
        }

        var locus = new Locus(segment.RootPageIndex, segment.RootPageIndex, segment.Kind);
        ctx.ClusterEntityLocations.TryGetValue(archetype.Name, out var occupied);

        // Level-by-level, following each level's sibling chain and collecting the next level's nodes from the internal
        // nodes' child values. A B-link tree is built to be traversed this way, and it needs no recursion depth.
        var level = new List<int> { entry.RootChunkId };
        var seen = new HashSet<int>();
        var uniqueKeys = field.IndexAllowMultiple ? null : new Dictionary<long, int>();
        var visitedNodes = 0;

        while (level.Count > 0 && visitedNodes < MaxNodesPerTree)
        {
            var next = new List<int>();

            foreach (var start in level)
            {
                for (var chunkId = start; chunkId > 0 && visitedNodes < MaxNodesPerTree; visitedNodes++)
                {
                    if (!seen.Add(chunkId))
                    {
                        break;   // IDX-06 owns cycle reporting; stopping here just avoids doing it twice
                    }

                    if (!reader.TryGetChunk(segment, geometry, chunkId, out var node)
                        || !reader.IsAllocated(segment, geometry, chunkId))
                    {
                        break;   // IDX-06 owns dangling-link reporting
                    }

                    var count = Math.Min(IndexDirectoryReader.CountOf(node), layout.Capacity);
                    var isLeaf = IndexNodeLayout.IsLeaf(node);

                    // Uniqueness is a property of the LEAF level only. An internal node's keys are separators copied
                    // up from the leaves below it, so counting them finds every key twice and reports a perfectly
                    // unique index as full of duplicates — which is exactly what the first run of this check did.
                    CheckKeyOrder(ctx, locus, archetype, field, layout, node, count, chunkId, isLeaf ? uniqueKeys : null);

                    if (isLeaf)
                    {
                        CheckValues(ctx, locus, archetype, field, layout, node, count, chunkId, occupied, segment, geometry);
                    }
                    else
                    {
                        // The leftmost child is NOT in the value array — N keys, N+1 children.
                        var leftmost = IndexDirectoryReader.LeftChildOf(node);
                        if (leftmost > 0 && !seen.Contains(leftmost))
                        {
                            next.Add(leftmost);
                        }

                        for (var i = 0; i < count; i++)
                        {
                            var child = layout.ValueAt(node, i);
                            if (child > 0 && !seen.Contains(child))
                            {
                                next.Add(child);
                            }
                        }
                    }

                    chunkId = IndexDirectoryReader.NextOf(node);
                }
            }

            // Only the leftmost node of each level needs seeding — the rest is reached through sibling links — so an
            // internal node's children are added wholesale and de-duplicated by `seen` on arrival.
            level = next;
        }

        return true;
    }

    /// <summary>
    /// Matches a directory entry to the field it indexes.
    /// </summary>
    /// <remarks>
    /// <c>StableId</c> is the field id and <c>Slot</c> is the component's position in the archetype, which is exactly
    /// why the pair is the key (#657): field ids restart at 0 per component, so the slot is what disambiguates them.
    /// A primary-key tree (<c>StableId == -1</c>) indexes no user field and is skipped rather than guessed at.
    /// </remarks>
    private static bool TryResolveField(ScanContext ctx, ArchetypeView archetype, IndexTreeEntry entry, out FieldView field)
    {
        field = null;

        if (entry.StableId < 0 || entry.Slot < 0 || entry.Slot >= archetype.ComponentNames.Count)
        {
            return false;
        }

        if (!ctx.Manifest.Components.TryGetValue(archetype.ComponentNames[entry.Slot], out var component))
        {
            return false;
        }

        foreach (var candidate in component.Fields)
        {
            if (candidate.FieldId == entry.StableId && candidate.HasIndex)
            {
                field = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>IDX-05</c> — keys ascend within a node, stay under its high key, and do not repeat in a unique index.
    /// </summary>
    private static void CheckKeyOrder(ScanContext ctx, Locus locus, ArchetypeView archetype, FieldView field,
        IndexNodeLayout layout, ReadOnlySpan<byte> node, int count, int chunkId, Dictionary<long, int> uniqueKeys)
    {
        for (var i = 1; i < count; i++)
        {
            var previous = layout.KeyAt(node, i - 1);
            var current = layout.KeyAt(node, i);

            if (layout.Compare(previous, current) <= 0)
            {
                continue;
            }

            ctx.Report(KeyOrder, IntegritySeverity.Divergence, "IX-01", locus,
                $"The index on '{archetype.Name}.{field.Name}' has keys out of order.",
                $"In node {chunkId}, entry {i - 1} holds {layout.Describe(previous)} and entry {i} holds "
                + $"{layout.Describe(current)}. A B+Tree lookup is a binary search over this array, so it stops at the "
                + "first key that compares wrongly and reports a present entry as absent. Indexes are derived from "
                + "cluster data, so rebuilding costs nothing.",
                Repairability.Lossless);
            break;   // one finding per node; a shuffled node would otherwise produce one per entry
        }

        if (uniqueKeys == null || count == 0)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var encoded = layout.IsString ? HashOf(layout.KeyAt(node, i)) : layout.TryEncodeKey(layout.KeyAt(node, i));
            if (uniqueKeys.TryAdd(encoded, chunkId))
            {
                continue;
            }

            ctx.Report(KeyOrder, IntegritySeverity.Divergence, "RB-07", locus,
                $"A unique index on '{archetype.Name}.{field.Name}' holds one key twice.",
                $"Key {layout.Describe(layout.KeyAt(node, i))} appears in node {chunkId} and in node "
                + $"{uniqueKeys[encoded]}. The field is declared unique, so a lookup returns whichever entry the descent "
                + "reaches and the other entity is unreachable through this index. RB-07 records the same count under "
                + "LastOpenUniqueIndexRebuildConflicts when a rebuild meets it.",
                Repairability.Lossless);
            return;   // one is enough to characterise the tree
        }
    }

    /// <summary>
    /// <c>IDX-02</c> — every leaf value names an occupied slot of this archetype's cluster.
    /// </summary>
    /// <remarks>
    /// <c>IX-02</c>'s failure shape at entry granularity. A per-archetype index stores packed
    /// <c>ClusterLocation</c>s, and <c>RB-04</c>'s note is explicit that decoding one against the wrong cluster is an
    /// access violation rather than a wrong row — so an entry pointing at a free slot is not a stale result, it is a
    /// read of memory that belongs to nothing.
    /// </remarks>
    private static void CheckValues(ScanContext ctx, Locus locus, ArchetypeView archetype, FieldView field,
        IndexNodeLayout layout, ReadOnlySpan<byte> node, int count, int chunkId, Dictionary<long, int> occupied,
        SegmentView segment, ChunkGeometry geometry)
    {
        if (occupied == null || occupied.Count == 0)
        {
            return;   // no cluster occupancy to compare against; ClusterChecks already said why
        }

        var live = new HashSet<int>(occupied.Values);

        if (field.IndexAllowMultiple)
        {
            CheckMultiValueBuffers(ctx, locus, archetype, field, layout, node, count, chunkId, live, segment, geometry);
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var packed = layout.ValueAt(node, i);
            if (packed >= 0 && live.Contains(packed))
            {
                continue;
            }

            var (cluster, slot) = ClusterLocation.Unpack(packed);
            ctx.Report(ValuesResolve, IntegritySeverity.Divergence, "IX-02", locus,
                $"The index on '{archetype.Name}.{field.Name}' points at a slot that holds no entity.",
                $"Entry {i} of node {chunkId} — key {layout.Describe(layout.KeyAt(node, i))} — resolves to cluster "
                + $"{cluster} slot {slot}, which the cluster's occupancy word marks free. A lookup on that key does not "
                + "fail: it decodes whatever the slot contains. RB-04 records that decoding a ClusterLocation against a "
                + "slot that is not live is an access violation rather than a wrong row. Rebuilding the index from "
                + "cluster data costs nothing.",
                Repairability.Lossless);
            return;   // one per node keeps a rebuilt-stale index from producing a finding per entry
        }
    }

    /// <summary>
    /// <c>IDX-07</c> — a multi-value entry's buffer resolves, terminates, and every element is a live location.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On a non-unique index a leaf value is not a location at all — it is a <b>buffer id</b>, and the entities sharing
    /// that key live in a variable-sized buffer. Two facts make it decodable, and both were misread when this check was
    /// first declared unrun: the element type is always <c>int</c> (a packed <c>ClusterLocation</c>), not something
    /// recorded per index; and the buffer lives in the <b>index segment itself</b> rather than in a pooled
    /// component-collection segment. <c>L64MultipleNodeStorage</c> builds it as
    /// <c>VariableSizedBufferSegment&lt;int&gt;</c> over the tree's own segment.
    /// </para>
    /// <para>
    /// A dangling buffer id is the more dangerous half. Every entity filed under that key becomes unreachable through
    /// the index at once — not one row, the whole bucket — and the index still looks structurally perfect, because
    /// nothing about the node is wrong.
    /// </para>
    /// </remarks>
    private static void CheckMultiValueBuffers(ScanContext ctx, Locus locus, ArchetypeView archetype, FieldView field,
        IndexNodeLayout layout, ReadOnlySpan<byte> node, int count, int chunkId, HashSet<int> live,
        SegmentView segment, ChunkGeometry geometry)
    {
        var reader = new VsbsReader(ctx.Source);
        var elements = new List<int>();

        for (var i = 0; i < count; i++)
        {
            var bufferId = layout.ValueAt(node, i);
            if (bufferId <= 0)
            {
                continue;   // a key with no buffer yet is not damage
            }

            if (!reader.TryReadBuffer(segment, geometry, bufferId, elements))
            {
                ctx.Report(MultiValueBuffers, IntegritySeverity.Divergence, "IX-06", locus,
                    $"A key of the index on '{archetype.Name}.{field.Name}' names an unreadable value buffer.",
                    $"Entry {i} of node {chunkId} — key {layout.Describe(layout.KeyAt(node, i))} — points at buffer "
                    + $"{bufferId}, whose chunk chain does not resolve or does not terminate. Every entity filed under "
                    + "that key is unreachable through this index at once, and nothing about the node itself is wrong so "
                    + "no structural check sees it. Rebuilding the index from cluster data costs nothing.",
                    Repairability.Lossless);
                return;   // one per node
            }

            for (var e = 0; e < elements.Count; e++)
            {
                if (live.Contains(elements[e]))
                {
                    continue;
                }

                var (cluster, slot) = ClusterLocation.Unpack(elements[e]);
                ctx.Report(MultiValueBuffers, IntegritySeverity.Divergence, "IX-06", locus,
                    $"A value buffer of the index on '{archetype.Name}.{field.Name}' names a slot that holds no entity.",
                    $"Buffer {bufferId}, reached from key {layout.Describe(layout.KeyAt(node, i))} in node {chunkId}, "
                    + $"holds a location resolving to cluster {cluster} slot {slot}, which the occupancy word marks free. "
                    + "A query on that key decodes whatever occupies the slot; RB-04 records that as an access violation "
                    + "rather than a wrong row.",
                    Repairability.Lossless);
                return;
            }
        }
    }

    /// <summary>A stable 64-bit hash of a fixed-width string key, for duplicate detection only.</summary>
    private static long HashOf(ReadOnlySpan<byte> key)
    {
        // FNV-1a. Never persisted and never compared for order — collisions could only under-report a duplicate, which
        // is the safe direction for a check that would otherwise need the whole key set in memory.
        unchecked
        {
            var hash = 1469598103934665603L;
            for (var i = 0; i < key.Length; i++)
            {
                hash = (hash ^ key[i]) * 1099511628211L;
            }

            return hash;
        }
    }
}
