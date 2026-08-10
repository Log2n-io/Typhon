using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// <c>IDX-03</c> and <c>IDX-04</c> — the index against the data it indexes, in both directions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both directions are required, and shipping only the forward one is the classic mistake</b>
/// (<c>03 §6</c>). Forward-only checking — every entry resolves to a slot whose field value equals the key — passes
/// completely on an index that is missing half its entries, and that is exactly what a rebuild over pre-apply state
/// produces (<c>RB-02</c>'s shape). The reverse direction is the one that catches it.
/// </para>
/// <para>
/// This is the check <c>G5</c> existed to make possible with a schema assembly. It needs none: the field's offset and
/// size are in <c>FieldR1</c>, and the cluster's slot count and per-component data offsets are a pure function of the
/// manifest — see <see cref="ClusterLayoutReader"/>.
/// </para>
/// </remarks>
internal static class IndexAgreementChecks
{
    /// <summary>Check code: every index entry resolves to a live slot whose field value equals the key.</summary>
    public const string EntriesMatchValues = "CHK-IDX-03";

    /// <summary>Check code: every live slot's indexed field has a matching index entry.</summary>
    public const string ValuesHaveEntries = "CHK-IDX-04";

    /// <summary>Runs both checks. Requires <see cref="ScanDepth.Deep"/> and a readable manifest.</summary>
    /// <param name="ctx">The scan context, after the cluster and index passes.</param>
    public static void Run(ScanContext ctx)
    {
        if (!ctx.AtLeast(ScanDepth.Deep))
        {
            ctx.Findings.NoteSkipped($"{EntriesMatchValues}, {ValuesHaveEntries}", "needs Deep depth");
            return;
        }

        if (ctx.Manifest is not { IsUsable: true })
        {
            ctx.Findings.NoteSkipped($"{EntriesMatchValues}, {ValuesHaveEntries}",
                "the schema manifest could not be read, so indexed fields cannot be located");
            return;
        }

        var reader = new IndexDirectoryReader(ctx.Source);
        var entries = new List<IndexTreeEntry>();
        var compared = 0;

        foreach (var archetype in ctx.Manifest.Archetypes.Values)
        {
            var layout = ClusterLayoutReader.TryDerive(ctx.Manifest, archetype);
            if (layout == null || archetype.ClusterSegmentRoot == 0)
            {
                continue;
            }

            foreach (var root in new[] { archetype.IndexRoot, archetype.String64IndexRoot })
            {
                if (root == 0 || !ctx.Segments.TryGetValue(root, out var indexSegment) || indexSegment.Pages.Count == 0)
                {
                    continue;
                }

                var page = new byte[IntegrityConstants.PageSize];
                if (!ctx.Source.TryReadPage(indexSegment.Pages[0], page))
                {
                    continue;
                }

                var indexGeometry = ChunkGeometry.FromPage(page);
                if (!indexGeometry.IsUsable || !reader.TryReadDirectory(indexSegment, indexGeometry, entries))
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (Compare(ctx, reader, archetype, layout, indexSegment, indexGeometry, entry))
                    {
                        compared++;
                    }
                }
            }
        }

        if (compared == 0)
        {
            ctx.Findings.NoteSkipped($"{EntriesMatchValues}, {ValuesHaveEntries}",
                "no indexed field could be matched to a readable cluster, so index contents were not compared against data");
        }
    }

    /// <summary>Builds both sides for one tree and reports each direction. Returns whether a comparison happened.</summary>
    private static bool Compare(ScanContext ctx, IndexDirectoryReader reader, ArchetypeView archetype,
        ClusterLayoutReader layout, SegmentView indexSegment, ChunkGeometry indexGeometry, IndexTreeEntry entry)
    {
        if (entry.RootChunkId <= 0 || entry.StableId < 0 || entry.Slot < 0 || entry.Slot >= layout.ComponentNames.Count)
        {
            return false;
        }

        if (!ctx.Manifest.Components.TryGetValue(layout.ComponentNames[entry.Slot], out var component))
        {
            return false;
        }

        FieldView field = null;
        foreach (var candidate in component.Fields)
        {
            if (candidate.FieldId == entry.StableId && candidate.HasIndex && !candidate.IndexAllowMultiple)
            {
                field = candidate;
                break;
            }
        }

        if (field == null)
        {
            return false;   // multi-value indexes store buffer ids, not locations — IDX-07's territory
        }

        var nodeLayout = IndexNodeLayout.ForFieldType(field.Type);
        if (!nodeLayout.IsUsable || field.Offset < 0 || field.Offset + nodeLayout.KeySize > component.Size)
        {
            return false;
        }

        var expected = ReadFieldValuesFromCluster(ctx, archetype, layout, entry.Slot, field, nodeLayout);
        if (expected == null)
        {
            return false;
        }

        var actual = ReadEntriesFromTree(reader, indexSegment, indexGeometry, nodeLayout, entry.RootChunkId);
        var locus = new Locus(indexSegment.RootPageIndex, indexSegment.RootPageIndex, indexSegment.Kind);
        var label = $"'{archetype.Name}.{field.Name}'";

        ReportForward(ctx, locus, label, nodeLayout, expected, actual);
        ReportReverse(ctx, locus, label, expected, actual);
        return true;
    }

    /// <summary>Reads the indexed field's value out of every live cluster slot, keyed by packed location.</summary>
    private static Dictionary<int, byte[]> ReadFieldValuesFromCluster(ScanContext ctx, ArchetypeView archetype,
        ClusterLayoutReader layout, int componentSlot, FieldView field, IndexNodeLayout nodeLayout)
    {
        if (!ctx.Segments.TryGetValue(archetype.ClusterSegmentRoot, out var segment) || segment.Pages.Count == 0)
        {
            return null;
        }

        var page = new byte[IntegrityConstants.PageSize];
        if (!ctx.Source.TryReadPage(segment.Pages[0], page))
        {
            return null;
        }

        var geometry = ChunkGeometry.FromPage(page);
        if (!geometry.IsUsable)
        {
            return null;
        }

        // The derived stride must match the persisted one, or the derivation is describing a different layout than the
        // file holds and every offset below it is wrong. Corroboration, not decoration: this is a re-derived quantity,
        // and a re-derivation that drifts is worse than none.
        if (layout.Stride != geometry.Stride)
        {
            ctx.Findings.NoteCaveat($"The cluster layout derived for '{archetype.Name}' implies a {layout.Stride}-byte "
                + $"stride, but its segment records {geometry.Stride}. Index-to-data comparison was skipped for it.");
            return null;
        }

        var values = new Dictionary<int, byte[]>();
        var loadedPage = -1;

        for (var chunkId = 0; chunkId < geometry.Capacity(segment.Pages.Count); chunkId++)
        {
            if (!geometry.TryLocate(chunkId, out var ordinal, out var chunkInPage) || ordinal >= segment.Pages.Count)
            {
                continue;
            }

            var filePage = segment.Pages[ordinal];
            if (loadedPage != filePage)
            {
                if (!ctx.Source.TryReadPage(filePage, page))
                {
                    loadedPage = -1;
                    continue;
                }

                loadedPage = filePage;
            }

            if (!geometry.IsChunkAllocated(page, ordinal == 0, chunkInPage))
            {
                continue;
            }

            var at = geometry.OffsetInPage(ordinal, chunkInPage);
            if (at + geometry.Stride > IntegrityConstants.PageSize)
            {
                continue;
            }

            var cluster = new ReadOnlySpan<byte>(page, at, geometry.Stride);
            var occupancy = System.Runtime.InteropServices.MemoryMarshal.Read<ulong>(cluster);

            for (var slot = 0; slot < layout.ClusterSize; slot++)
            {
                if ((occupancy & (1UL << slot)) == 0)
                {
                    continue;
                }

                var valueAt = layout.ComponentDataOffset(componentSlot, slot) + field.Offset;
                if (valueAt + nodeLayout.KeySize > cluster.Length)
                {
                    continue;
                }

                values[ClusterLocation.Pack(chunkId, slot)] = cluster.Slice(valueAt, nodeLayout.KeySize).ToArray();
            }
        }

        return values;
    }

    /// <summary>Collects every leaf entry of a tree as location to key bytes.</summary>
    private static Dictionary<int, byte[]> ReadEntriesFromTree(IndexDirectoryReader reader, SegmentView segment,
        ChunkGeometry geometry, IndexNodeLayout layout, int rootChunkId)
    {
        var found = new Dictionary<int, byte[]>();
        var seen = new HashSet<int>();
        var level = new List<int> { rootChunkId };

        while (level.Count > 0)
        {
            var next = new List<int>();

            foreach (var start in level)
            {
                for (var chunkId = start; chunkId > 0;)
                {
                    if (!seen.Add(chunkId) || !reader.IsAllocated(segment, geometry, chunkId)
                        || !reader.TryGetChunk(segment, geometry, chunkId, out var node))
                    {
                        break;
                    }

                    var count = Math.Min(IndexDirectoryReader.CountOf(node), layout.Capacity);
                    if (IndexNodeLayout.IsLeaf(node))
                    {
                        for (var i = 0; i < count; i++)
                        {
                            found[layout.ValueAt(node, i)] = layout.KeyAt(node, i).ToArray();
                        }
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

            level = next;
        }

        return found;
    }

    /// <summary><c>IDX-03</c> — an entry whose key disagrees with the value at the slot it names.</summary>
    private static void ReportForward(ScanContext ctx, Locus locus, string label, IndexNodeLayout layout,
        Dictionary<int, byte[]> expected, Dictionary<int, byte[]> actual)
    {
        var mismatched = 0;
        var firstLocation = 0;

        foreach (var (location, key) in actual)
        {
            // A location the cluster does not hold is IDX-02's finding, not this one — reporting it here too would
            // make one fault produce two findings an operator has to correlate.
            if (!expected.TryGetValue(location, out var value) || key.AsSpan().SequenceEqual(value))
            {
                continue;
            }

            if (mismatched++ == 0)
            {
                firstLocation = location;
            }
        }

        if (mismatched == 0)
        {
            return;
        }

        var (cluster, slot) = ClusterLocation.Unpack(firstLocation);
        ctx.Report(EntriesMatchValues, IntegritySeverity.Divergence, "", locus,
            $"The index on {label} holds keys that disagree with the data.",
            $"{mismatched} entry(ies) name a live slot whose field value is not the key they are filed under. The first is "
            + $"cluster {cluster} slot {slot}, indexed as {layout.Describe(actual[firstLocation])} while the entity holds "
            + $"{layout.Describe(expected[firstLocation])}. A query on the indexed value returns the wrong entity, and a "
            + "query on the entity's real value does not find it. Indexes are derived from cluster data, so rebuilding "
            + "costs nothing.",
            Repairability.Lossless);
    }

    /// <summary><c>IDX-04</c> — a live entity whose indexed field has no entry at all.</summary>
    private static void ReportReverse(ScanContext ctx, Locus locus, string label,
        Dictionary<int, byte[]> expected, Dictionary<int, byte[]> actual)
    {
        var missing = 0;
        var firstLocation = 0;

        foreach (var location in expected.Keys)
        {
            if (actual.ContainsKey(location))
            {
                continue;
            }

            if (missing++ == 0)
            {
                firstLocation = location;
            }
        }

        if (missing == 0)
        {
            return;
        }

        var (cluster, slot) = ClusterLocation.Unpack(firstLocation);
        ctx.Report(ValuesHaveEntries, IntegritySeverity.Divergence, "RB-02", locus,
            $"Live entities are missing from the index on {label}.",
            $"{missing} live slot(s) — the first is cluster {cluster} slot {slot} — have no entry in the index. Those "
            + "entities exist and are intact: a scan reaches them, a query on the indexed field does not. This is the "
            + "shape a rebuild over pre-apply state leaves, covering only the checkpointed half of the data, and it is "
            + "invisible to forward-only checking. Rebuilding the index restores them.",
            Repairability.Lossless);
    }
}
