using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// <c>CLU-03</c> — the cluster's copy of a Versioned component against the head of its revision chain.
/// </summary>
/// <remarks>
/// <para>
/// A Versioned component is stored twice on purpose: the revision chain holds the authoritative history, and the
/// cluster holds a copy of the current value so a read costs one cache line instead of a chain walk (D1). Two copies
/// of one fact is exactly the situation a scanner exists for — <c>RB-03</c> lists <i>"cluster HEAD ≡ chain HEAD"</i> as
/// a postcondition, and nothing at runtime re-checks it.
/// </para>
/// <para>
/// <b>Which copy is right is not this check's business, and saying so matters.</b> The chain is authoritative, so a
/// disagreement is repaired by rewriting the cluster from the chain — which is what <c>RebuildClusterFromChains</c>
/// already does, losslessly. The finding says which value each side holds and stops there.
/// </para>
/// <para>
/// <b>Gated on a clean shutdown</b>, for the same reason <c>CHN-02</c> is. A chain is only guaranteed collapsed to a
/// single committed element after a consolidating checkpoint; on a crash-path file the head this check would compare
/// against has not been established yet, and the disagreement it found would be recovery's remaining work rather than
/// damage.
/// </para>
/// </remarks>
internal static class ClusterHeadChecks
{
    /// <summary>Check code: the cluster's component copy equals the chain's head value.</summary>
    public const string HeadMatchesChain = "CHK-CLU-03";

    /// <summary>Runs the check. Requires <see cref="ScanDepth.Deep"/>, a readable manifest and a clean shutdown.</summary>
    /// <param name="ctx">The scan context, after the chain and cluster passes.</param>
    public static void Run(ScanContext ctx)
    {
        if (!ctx.AtLeast(ScanDepth.Deep))
        {
            ctx.Findings.NoteSkipped(HeadMatchesChain, "needs Deep depth");
            return;
        }

        if (ctx.Manifest is not { IsUsable: true })
        {
            ctx.Findings.NoteSkipped(HeadMatchesChain, "the schema manifest could not be read, so cluster layouts are unknown");
            return;
        }

        var (_, cleanShutdown) = ctx.Bootstrap.ReadWatermarks();
        if (!cleanShutdown)
        {
            ctx.Findings.NoteSkipped(HeadMatchesChain,
                "the database was not closed cleanly, so its revision chains have not been consolidated to a single head "
                + "and the cluster copy is not yet expected to match");
            return;
        }

        var compared = 0;
        foreach (var archetype in ctx.Manifest.Archetypes.Values)
        {
            var layout = ClusterLayoutReader.TryDerive(ctx.Manifest, archetype);
            if (layout == null || archetype.ClusterSegmentRoot == 0)
            {
                continue;
            }

            compared += CompareArchetype(ctx, archetype, layout);
        }

        if (compared == 0)
        {
            ctx.Findings.NoteSkipped(HeadMatchesChain,
                "no archetype with a Versioned component and a readable chain was found, so no cluster copy could be "
                + "compared against a chain head");
        }
    }

    /// <summary>Compares every live slot of one archetype. Returns how many component slots were compared.</summary>
    private static int CompareArchetype(ScanContext ctx, ArchetypeView archetype, ClusterLayoutReader layout)
    {
        if (!ctx.Segments.TryGetValue(archetype.ClusterSegmentRoot, out var clusterSegment) || clusterSegment.Pages.Count == 0)
        {
            return 0;
        }

        var page = new byte[IntegrityConstants.PageSize];
        if (!ctx.Source.TryReadPage(clusterSegment.Pages[0], page))
        {
            return 0;
        }

        var clusterGeometry = ChunkGeometry.FromPage(page);
        if (!clusterGeometry.IsUsable || clusterGeometry.Stride != layout.Stride)
        {
            return 0;   // IndexAgreementChecks already caveats a stride disagreement
        }

        var compared = 0;

        for (var slot = 0; slot < layout.ComponentNames.Count; slot++)
        {
            var name = layout.ComponentNames[slot];
            if (!ctx.Manifest.Components.TryGetValue(name, out var component)
                || component.StorageMode != StorageMode.Versioned
                || component.ComponentSegmentRoot == 0
                || component.RevisionSegmentRoot == 0)
            {
                continue;
            }

            // ChainChecks published {chainRootChunkId -> owning entity key}; the comparison needs it the other way.
            if (!ctx.ChainRoots.TryGetValue(name, out var roots) || roots.Count == 0)
            {
                continue;
            }

            var byEntity = new Dictionary<long, int>(roots.Count);
            foreach (var (chunkId, entityPK) in roots)
            {
                byEntity[entityPK] = chunkId;
            }

            if (CompareComponent(ctx, archetype, layout, clusterSegment, clusterGeometry, slot, component, byEntity))
            {
                compared++;
            }
        }

        return compared;
    }

    /// <summary>Walks one component's live slots, comparing each against its chain head.</summary>
    private static bool CompareComponent(ScanContext ctx, ArchetypeView archetype, ClusterLayoutReader layout,
        SegmentView clusterSegment, ChunkGeometry clusterGeometry, int componentSlot, ComponentView component,
        Dictionary<long, int> chainRootsByEntity)
    {
        var chains = new ChunkSource(ctx.Source, ctx.Segments, component.RevisionSegmentRoot);
        var values = new ChunkSource(ctx.Source, ctx.Segments, component.ComponentSegmentRoot);
        if (!chains.IsUsable || !values.IsUsable)
        {
            return false;
        }

        var clusterPage = new byte[IntegrityConstants.PageSize];
        var loadedPage = -1;
        var mismatched = 0;
        long firstEntity = 0;
        var missingHead = 0;
        var compared = 0;

        for (var chunkId = 0; chunkId < clusterGeometry.Capacity(clusterSegment.Pages.Count); chunkId++)
        {
            if (!clusterGeometry.TryLocate(chunkId, out var ordinal, out var chunkInPage) || ordinal >= clusterSegment.Pages.Count)
            {
                continue;
            }

            var filePage = clusterSegment.Pages[ordinal];
            if (loadedPage != filePage)
            {
                if (!ctx.Source.TryReadPage(filePage, clusterPage))
                {
                    loadedPage = -1;
                    continue;
                }

                loadedPage = filePage;
            }

            if (!clusterGeometry.IsChunkAllocated(clusterPage, ordinal == 0, chunkInPage))
            {
                continue;
            }

            var at = clusterGeometry.OffsetInPage(ordinal, chunkInPage);
            if (at + clusterGeometry.Stride > IntegrityConstants.PageSize)
            {
                continue;
            }

            var cluster = new ReadOnlySpan<byte>(clusterPage, at, clusterGeometry.Stride);
            var occupancy = MemoryMarshal.Read<ulong>(cluster);

            for (var slot = 0; slot < layout.ClusterSize; slot++)
            {
                if ((occupancy & (1UL << slot)) == 0)
                {
                    continue;
                }

                var keyAt = layout.EntityKeysOffset + (slot * sizeof(long));
                if (keyAt + sizeof(long) > cluster.Length)
                {
                    continue;
                }

                // The cluster stores a packed EntityId (routing id in the low 16 bits); a chain header's EntityPK may be
                // either form depending on the path that wrote it. Trying both is not sloppiness — picking one and
                // being wrong makes every join fail, and a check whose join fails reports nothing at all while looking
                // like it ran. That is exactly how this check first behaved.
                var raw = MemoryMarshal.Read<long>(cluster[keyAt..]);
                var entityKey = raw >> 16;
                if (raw == 0
                    || (!chainRootsByEntity.TryGetValue(entityKey, out var chainRoot)
                        && !chainRootsByEntity.TryGetValue(raw, out chainRoot)))
                {
                    missingHead++;
                    continue;
                }

                if (!TryReadHeadValue(chains, values, chainRoot, component, out var headValue))
                {
                    missingHead++;
                    continue;
                }

                var dataAt = layout.ComponentDataOffset(componentSlot, slot);
                if (dataAt + component.Size > cluster.Length)
                {
                    continue;
                }

                compared++;
                if (cluster.Slice(dataAt, component.Size).SequenceEqual(headValue))
                {
                    continue;
                }

                if (mismatched++ == 0)
                {
                    firstEntity = entityKey;
                }
            }
        }

        if (mismatched > 0)
        {
            ctx.Report(HeadMatchesChain, IntegritySeverity.Divergence, "RB-03",
                ctx.LocusForPage(clusterSegment.RootPageIndex),
                $"The cluster copy of '{archetype.Name}.{component.Name}' disagrees with the revision chain.",
                $"{mismatched} live slot(s) — the first belongs to entity {firstEntity} — hold component bytes that differ "
                + "from the head of that entity's chain. A Versioned component is stored twice on purpose: the chain is "
                + "authoritative and the cluster is a read-path copy, so a query served from the cluster returns a value "
                + "the chain says is not current. Rewriting the cluster from the chains restores agreement without loss.",
                Repairability.Lossless);
        }

        if (missingHead > 0)
        {
            ctx.Findings.NoteCaveat($"{missingHead} live slot(s) of '{archetype.Name}.{component.Name}' had no readable "
                + $"chain head, so their cluster copies were not compared ({compared} were).");
        }

        // Comparing nothing is not the same as agreeing, and the two are indistinguishable in a report unless one of
        // them says so. A join that silently matches no entity is the failure mode this check already had once.
        if (compared == 0)
        {
            ctx.Findings.NoteSkipped(HeadMatchesChain,
                $"no live slot of '{archetype.Name}.{component.Name}' could be matched to a chain head, so its cluster "
                + "copies were not verified against anything");
            return false;
        }

        return true;
    }

    /// <summary>Reads the value the chain's head element points at, from the component data segment.</summary>
    /// <remarks>
    /// The head element's <c>ComponentChunkId</c> names a chunk of the component segment, whose overhead — the entity
    /// PK for non-Versioned storage, plus one back-reference per multi-value index — sits at the <b>front</b>, so the
    /// data starts at <c>CompOverhead</c>. Both quantities are persisted on the <c>ComponentR1</c> row.
    /// </remarks>
    private static bool TryReadHeadValue(ChunkSource chains, ChunkSource values, int chainRoot, ComponentView component,
        out ReadOnlySpan<byte> value)
    {
        value = default;

        if (!chains.TryRead(chainRoot, out var chunk))
        {
            return false;
        }

        var header = MemoryMarshal.Read<CompRevStorageHeader>(chunk);
        var headerSize = Unsafe.SizeOf<CompRevStorageHeader>();
        var elementSize = Unsafe.SizeOf<CompRevStorageElement>();
        var capacity = (chunk.Length - headerSize) / elementSize;

        if (header.ItemCount <= 0 || header.FirstItemIndex < 0 || header.FirstItemIndex >= capacity)
        {
            return false;
        }

        var element = MemoryMarshal.Read<CompRevStorageElement>(
            chunk.Slice(headerSize + (header.FirstItemIndex * elementSize), elementSize));

        // 0 is a delete entry — the entity has no current value for this component, which is not a mismatch.
        if (element.ComponentChunkId <= 0)
        {
            return false;
        }

        if (!values.TryRead(element.ComponentChunkId, out var data)
            || component.Overhead + component.Size > data.Length)
        {
            return false;
        }

        value = data.Slice(component.Overhead, component.Size);
        return true;
    }

    /// <summary>Reads chunks of one segment into an owned buffer, caching the current page.</summary>
    /// <remarks>
    /// Copies rather than aliasing a shared page, because two of these are live at once — a chain chunk and the value
    /// chunk it points at — and they can sit on the same page. Handing back spans over one buffer would make the second
    /// read silently rewrite the first.
    /// </remarks>
    private sealed class ChunkSource
    {
        private readonly IPageSource _source;
        private readonly SegmentView _segment;
        private readonly ChunkGeometry _geometry;
        private readonly byte[] _page = new byte[IntegrityConstants.PageSize];
        private readonly byte[] _chunk;
        private int _loadedPage = -1;

        public ChunkSource(IPageSource source, IReadOnlyDictionary<int, SegmentView> segments, int root)
        {
            _source = source;

            if (!segments.TryGetValue(root, out _segment) || _segment.Pages.Count == 0
                || !source.TryReadPage(_segment.Pages[0], _page))
            {
                return;
            }

            _geometry = ChunkGeometry.FromPage(_page);
            if (!_geometry.IsUsable)
            {
                return;
            }

            _loadedPage = _segment.Pages[0];
            _chunk = new byte[_geometry.Stride];
            IsUsable = true;
        }

        public bool IsUsable { get; }

        public bool TryRead(int chunkId, out ReadOnlySpan<byte> chunk)
        {
            chunk = default;

            if (!IsUsable || chunkId < 0 || !_geometry.TryLocate(chunkId, out var ordinal, out var chunkInPage)
                || ordinal >= _segment.Pages.Count)
            {
                return false;
            }

            var filePage = _segment.Pages[ordinal];
            if (_loadedPage != filePage)
            {
                if (!_source.TryReadPage(filePage, _page))
                {
                    _loadedPage = -1;
                    return false;
                }

                _loadedPage = filePage;
            }

            if (!_geometry.IsChunkAllocated(_page, ordinal == 0, chunkInPage))
            {
                return false;
            }

            var at = _geometry.OffsetInPage(ordinal, chunkInPage);
            if (at + _geometry.Stride > IntegrityConstants.PageSize)
            {
                return false;
            }

            _page.AsSpan(at, _geometry.Stride).CopyTo(_chunk);
            chunk = _chunk;
            return true;
        }
    }
}
