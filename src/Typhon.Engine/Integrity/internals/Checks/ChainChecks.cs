using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// <c>CHN</c> — revision-chain structure. The first family that reads inside chunks rather than around them.
/// </summary>
/// <remarks>
/// <para>
/// Every check here reads <c>CompRevStorageHeader</c>, which is <b>engine-defined</b> — <c>NextChunkId</c>,
/// <c>ItemCount</c>, <c>EntityPK</c>. None of them touches schema-shaped payload. That is the whole point of the Tier-1
/// re-classification in <c>09 §2</c>: what these checks were missing was never the schema, it was the arithmetic to find
/// the next chunk, and format revision 7 supplied it.
/// </para>
/// <para>
/// <b>Bounded walks, always.</b> A chain is a linked list read from a possibly-damaged file, so every hop is
/// range-checked, allocation-checked, and counted against a bound derived from the segment's own capacity. An unbounded
/// walk over a cyclic chain does not produce a finding; it produces a hung scanner on exactly the database somebody was
/// trying to diagnose.
/// </para>
/// </remarks>
internal static class ChainChecks
{
    /// <summary>Check code: exactly one committed HEAD per (entity, component).</summary>
    public const string SingleHead = "CHK-CHN-01";

    /// <summary>Check code: post-recovery chains are collapsed to their head.</summary>
    public const string Collapsed = "CHK-CHN-02";

    /// <summary>Check code: every <c>NextChunkId</c> resolves to an allocated chunk of the same segment.</summary>
    public const string PointerResolves = "CHK-CHN-03";

    /// <summary>Check code: no cycles in any chain.</summary>
    public const string NoCycles = "CHK-CHN-04";

    /// <summary>Check code: every chain TSN is below the restored allocator watermark.</summary>
    public const string TsnBelowWatermark = "CHK-CHN-05";

    /// <summary>Check code: the TSN allocator watermark exceeds every TSN in the file.</summary>
    public const string TsnWatermark = "CHK-ALO-01";

    /// <summary>
    /// Runs the chain family. Requires <see cref="ScanDepth.Deep"/> and a readable manifest — without the manifest there
    /// is no way to know which segments hold revision chains rather than component data.
    /// </summary>
    /// <param name="ctx">The scan context, with the manifest read and segments walked.</param>
    public static void Run(ScanContext ctx)
    {
        if (!ctx.AtLeast(ScanDepth.Deep))
        {
            ctx.Findings.NoteSkipped("CHK-CHN-*", "needs Deep depth");
            return;
        }

        if (ctx.Manifest is not { IsUsable: true })
        {
            ctx.Findings.NoteSkipped("CHK-CHN-*", "the schema manifest could not be read, so revision segments cannot be identified");
            return;
        }

        // A clean shutdown is the precondition CHN-02 rests on, and it is not decoration. RB-03 describes the collapsed
        // shape as the SCRUB's postcondition, and the scrub runs on the crash path — but an ordinary consolidating
        // checkpoint leaves the same shape, which is what ChainShapeAtRestTests measured. On a file left by a crash
        // there has been no such consolidation, so asserting it there would report a divergence on a healthy database.
        var (_, cleanShutdown) = ctx.Bootstrap.ReadWatermarks();
        // The TSN watermark is only meaningful after a clean close. RB-05 is explicit that BK_NextFreeTSN is refreshed
        // ONLY on clean shutdown, and that recovery recomputes the resumption floor from three terms — so on a
        // crash-path file the persisted value is stale BY DESIGN and lags every revision the WAL window still holds.
        // Comparing against it there reports a fatal allocator fault on a database that is behaving exactly as
        // documented. Measured, not assumed: the G0 crash fixtures produced one CHN-05 per surviving revision.
        var nextFreeTsn = cleanShutdown ? ReadNextFreeTsn(ctx) : -1;
        var highestTsn = 0L;

        foreach (var component in ctx.Manifest.Components.Values)
        {
            if (component.RevisionSegmentRoot == 0)
            {
                continue;   // a non-Versioned component has no revision chain, which is not a defect
            }

            WalkSegment(ctx, component, cleanShutdown, nextFreeTsn, ref highestTsn);
        }

        if (!cleanShutdown)
        {
            ctx.Findings.NoteSkipped(Collapsed,
                "the database was not closed cleanly, so its chains have not been through a consolidating checkpoint");
        }

        if (nextFreeTsn < 0)
        {
            ctx.Findings.NoteSkipped($"{TsnBelowWatermark}, {TsnWatermark}",
                cleanShutdown
                    ? "the bootstrap records no NextFreeTSN, so no TSN watermark could be compared against"
                    : "the database was not closed cleanly, so its persisted TSN watermark is stale by design (RB-05) "
                      + "and recovery has not yet recomputed the resumption floor");
            return;
        }

        // Strictly greater, for the same reason as the entity-key watermark: NextFreeTSN records the last sequence
        // number issued, so the newest committed revision carrying exactly it is the normal resting state.
        if (highestTsn > nextFreeTsn)
        {
            // RB-05's third term, and the reason it is worth a check of its own: the failure it prevents is silent,
            // permanent, and leaves a database that passes every other check. A consolidating checkpoint can advance
            // committed TSNs into the data file WITHOUT leaving them in the WAL window, so a resumption floor computed
            // from the checkpoint and the replayed window alone lands BELOW the newest consolidated revision — and every
            // post-recovery reader then snapshots beneath it while MVCC quietly hides the latest value.
            ctx.Report(TsnWatermark, IntegritySeverity.Fatal, "RB-05", Locus.Database,
                "The transaction-sequence allocator is behind the newest revision in the file.",
                $"The bootstrap records NextFreeTSN = {nextFreeTsn}, but a surviving chain head carries TSN {highestTsn}. "
                + "Every reader opening this database snapshots below the newest committed revision, so the latest value "
                + "of those entities is invisible — and the next transaction reuses a sequence number that is already on "
                + "disk. Nothing surfaces an error; the data simply reads as older than it is.");
        }
    }

    /// <summary>Reads the persisted TSN watermark, or <c>-1</c> when the bootstrap does not record one.</summary>
    private static long ReadNextFreeTsn(ScanContext ctx)
    {
        if (!ctx.Bootstrap.TryGet("NextFreeTSN", out var value) || value.Type != BootstrapDictionary.ValueType.Long)
        {
            return -1;
        }

        return value.AsLong;
    }

    private static void WalkSegment(ScanContext ctx, ComponentView component, bool cleanShutdown, long nextFreeTsn,
        ref long highestTsn)
    {
        if (!ctx.Segments.TryGetValue(component.RevisionSegmentRoot, out var segment))
        {
            return;   // the manifest reader already reported an unresolvable pointer
        }

        var pages = segment.Pages;
        if (pages.Count == 0)
        {
            return;
        }

        var page = new byte[IntegrityConstants.PageSize];
        if (!ctx.Source.TryReadPage(pages[0], page))
        {
            return;
        }

        var geometry = ChunkGeometry.FromPage(page);
        if (!geometry.IsUsable)
        {
            ctx.Findings.NoteCaveat(
                $"The revision segment for '{component.Name}' (root page {segment.RootPageIndex}) records no chunk stride, "
                + "so its chains were not walked.");
            return;
        }

        var capacity = geometry.Capacity(pages.Count);
        var reader = new ChunkReader(ctx.Source, segment, geometry);

        // Walked once per chain root and remembered, so a chunk shared by two chains is seen rather than walked twice.
        var visitedGlobally = new HashSet<int>();

        for (var id = 0; id < capacity; id++)
        {
            // Only allocated chunks are candidate roots. A freed chunk still holds its old bytes, so scanning one would
            // resurrect a chain that was legitimately released and report findings about history nobody owns.
            if (!reader.TryRead(id, out var chunk, out var allocated) || !allocated)
            {
                continue;
            }

            var header = ReadHeader(chunk);

            // A chain ROOT carries an owning entity; a continuation chunk does not. Without the EntityMap this is the
            // available discriminator, and it is stated rather than dressed up: CHN-06 is what will confirm roots
            // against the map, and it is not in this increment.
            if (header.EntityPK == 0)
            {
                continue;
            }

            InspectElements(ctx, component, segment, geometry, chunk, id, header, nextFreeTsn, ref highestTsn);
            WalkChain(ctx, component, segment, geometry, reader, id, header, capacity, visitedGlobally, cleanShutdown);
        }
    }

    /// <summary>
    /// <c>CHN-01</c> and <c>CHN-05</c> — what the root chunk's own revision elements say.
    /// </summary>
    /// <remarks>
    /// Only the root chunk's elements are read. A continuation chunk's are reachable, but a chain that still has
    /// continuations has already drawn <see cref="Collapsed"/>, and walking history to re-report the same fact per
    /// element would bury the finding that matters under one per revision.
    /// </remarks>
    private static void InspectElements(ScanContext ctx, ComponentView component, SegmentView segment,
        ChunkGeometry geometry, ReadOnlySpan<byte> chunk, int chunkId, CompRevStorageHeader header, long nextFreeTsn,
        ref long highestTsn)
    {
        var elementSize = Unsafe.SizeOf<CompRevStorageElement>();
        var headerSize = Unsafe.SizeOf<CompRevStorageHeader>();
        var capacity = (chunk.Length - headerSize) / elementSize;
        if (capacity <= 0)
        {
            return;
        }

        var committed = 0;
        var live = 0;

        // ItemCount is a claim about the chain, and a damaged one can claim more elements than the chunk holds. Bound by
        // what is actually there rather than by what the header says.
        var count = Math.Min((int)header.ItemCount, capacity);
        for (var i = 0; i < count; i++)
        {
            // The chain is a circular buffer, so the i-th item is FirstItemIndex + i modulo the chunk's capacity.
            var slot = ((header.FirstItemIndex + i) % capacity + capacity) % capacity;
            var element = MemoryMarshal.Read<CompRevStorageElement>(chunk.Slice(headerSize + (slot * elementSize), elementSize));

            if (element.IsVoid)
            {
                continue;
            }

            live++;
            if (!element.IsolationFlag)
            {
                committed++;
            }

            if (element.TSN > highestTsn)
            {
                highestTsn = element.TSN;
            }

            if (nextFreeTsn >= 0 && element.TSN > nextFreeTsn)
            {
                ctx.Report(TsnBelowWatermark, IntegritySeverity.Divergence, "RB-05", LocusFor(segment, chunkId, geometry),
                    $"A revision of '{component.Name}' carries a sequence number at or above the allocator's watermark.",
                    $"Chunk {chunkId} holds a revision at TSN {element.TSN}, while the bootstrap records NextFreeTSN = "
                    + $"{nextFreeTsn}. The watermark is meant to sit above every sequence number ever issued, so this "
                    + "revision is invisible to every reader that opens the database and its number will be issued again.",
                    Repairability.Lossless);
            }
        }

        if (live > 0 && committed == 0)
        {
            ctx.Report(SingleHead, IntegritySeverity.DataLoss, "RB-03", LocusFor(segment, chunkId, geometry),
                $"A revision chain for '{component.Name}' holds no committed revision.",
                $"Chunk {chunkId} carries {live} revision(s), every one of them still flagged as isolated — the state a "
                + "revision is in before its transaction commits. No reader can see any value for this entity's "
                + "component, and a scrub has nothing to promote to head.",
                Repairability.NotRepairable,
                new LossEstimate
                {
                    Kind = LossKind.Values,
                    EntityCount = 1,
                    BoundedMin = 1,
                    BoundedMax = 1,
                    Component = component.Name,
                    Explanation = $"The committed value of one entity's '{component.Name}'."
                });
        }
        else if (committed > 1 && header.NextChunkId == 0 && header.ItemCount == 1)
        {
            // Only reachable when ItemCount disagrees with the elements actually present, which is itself the finding.
            ctx.Report(SingleHead, IntegritySeverity.Divergence, "RB-03", LocusFor(segment, chunkId, geometry),
                $"A collapsed revision chain for '{component.Name}' holds more than one committed revision.",
                $"Chunk {chunkId} declares ItemCount=1 and no continuation, but {committed} committed revisions are "
                + "present in it. Whichever the reader picks depends on where it starts, so two readers can legitimately "
                + "disagree about the entity's current value.",
                Repairability.Lossless);
        }
    }

    private static void WalkChain(ScanContext ctx, ComponentView component, SegmentView segment, ChunkGeometry geometry,
        ChunkReader reader, int rootChunkId, CompRevStorageHeader rootHeader, int capacity, HashSet<int> visitedGlobally,
        bool cleanShutdown)
    {
        if (cleanShutdown && (rootHeader.ItemCount != 1 || rootHeader.NextChunkId != 0))
        {
            ctx.Report(Collapsed, IntegritySeverity.Divergence, "RB-03", LocusFor(segment, rootChunkId, geometry),
                $"A revision chain for '{component.Name}' still holds history after a clean close.",
                $"Chunk {rootChunkId} of the revision segment rooted at page {segment.RootPageIndex} reports "
                + $"ItemCount={rootHeader.ItemCount} and NextChunkId={rootHeader.NextChunkId}; a consolidating checkpoint "
                + "collapses every chain to a single committed head, so a chain that still has a tail was either never "
                + "consolidated or was left mid-scrub. The head itself is intact — this is retained MVCC history, not "
                + "lost data.",
                Repairability.Lossless);
        }

        // The bound is the segment's own capacity: a chain cannot legitimately visit more chunks than exist. Anything
        // longer is a cycle by the pigeonhole principle, whether or not the visited set catches it first.
        var visitedThisChain = new HashSet<int> { rootChunkId };
        var next = rootHeader.NextChunkId;
        var steps = 0;

        while (next != 0 && steps++ <= capacity)
        {
            if (next < 0 || next >= capacity)
            {
                ctx.Report(PointerResolves, IntegritySeverity.DataLoss, "", LocusFor(segment, rootChunkId, geometry),
                    $"A revision chain for '{component.Name}' points outside its own segment.",
                    $"Chunk {rootChunkId} leads to chunk id {next}, but the segment rooted at page {segment.RootPageIndex} "
                    + $"holds only {capacity} chunks. The chain truncates there, so every revision beyond that point is "
                    + "unreachable.",
                    Repairability.NotRepairable,
                    new LossEstimate
                    {
                        Kind = LossKind.Unknown,
                        EntityCount = 1,
                        BoundedMin = 0,
                        BoundedMax = 1,
                        Explanation = $"The revision history of one '{component.Name}' beyond chunk {rootChunkId}. The "
                            + "published head is not affected."
                    });
                return;
            }

            if (!visitedThisChain.Add(next))
            {
                ctx.Report(NoCycles, IntegritySeverity.Fatal, "", LocusFor(segment, rootChunkId, geometry),
                    $"A revision chain for '{component.Name}' is circular.",
                    $"Following chunk {rootChunkId} returns to chunk {next}, which the walk has already visited. Any "
                    + "reader that follows this chain without its own cycle guard does not return.");
                return;
            }

            if (!reader.TryRead(next, out var chunk, out var allocated))
            {
                ctx.Report(PointerResolves, IntegritySeverity.DataLoss, "", LocusFor(segment, rootChunkId, geometry),
                    $"A revision chain for '{component.Name}' leads to a chunk that cannot be read.",
                    $"Chunk {rootChunkId} leads to chunk {next}, whose page could not be read from the segment rooted at "
                    + $"page {segment.RootPageIndex}.",
                    Repairability.NotRepairable);
                return;
            }

            if (!allocated)
            {
                ctx.Report(PointerResolves, IntegritySeverity.DataLoss, "", LocusFor(segment, rootChunkId, geometry),
                    $"A revision chain for '{component.Name}' leads to a freed chunk.",
                    $"Chunk {rootChunkId} leads to chunk {next}, which the segment's own occupancy bitmap marks as free. "
                    + "A freed chunk can be handed to another chain at any time, so the history beyond this point is "
                    + "either gone or about to be overwritten by an unrelated entity's revisions.",
                    Repairability.NotRepairable,
                    new LossEstimate
                    {
                        Kind = LossKind.Unknown,
                        EntityCount = 1,
                        BoundedMin = 0,
                        BoundedMax = 1,
                        Explanation = $"The revision history of one '{component.Name}' beyond chunk {rootChunkId}."
                    });
                return;
            }

            visitedGlobally.Add(next);
            next = MemoryMarshal.Read<int>(chunk);   // a continuation chunk's first field is its own NextChunkId
        }

        if (next != 0)
        {
            ctx.Report(NoCycles, IntegritySeverity.Fatal, "", LocusFor(segment, rootChunkId, geometry),
                $"A revision chain for '{component.Name}' does not terminate.",
                $"The walk from chunk {rootChunkId} exceeded the segment's own capacity of {capacity} chunks without "
                + "reaching a terminator, which it can only do by revisiting chunks.");
        }
    }

    /// <summary>Reads the chain header from a root chunk.</summary>
    /// <remarks>
    /// Copied into a full-size buffer rather than read in place: the persisted row can be smaller than the CLR struct
    /// when trailing alignment padding is not stored, and a direct read would then take bytes from the next chunk — or
    /// past the page, where it throws. <c>SchemaCatalogReader.ReadRow</c> documents the case that proved it.
    /// </remarks>
    private static CompRevStorageHeader ReadHeader(ReadOnlySpan<byte> chunk)
    {
        Span<byte> buffer = stackalloc byte[Unsafe.SizeOf<CompRevStorageHeader>()];
        buffer.Clear();
        chunk[..Math.Min(chunk.Length, buffer.Length)].CopyTo(buffer);
        return MemoryMarshal.Read<CompRevStorageHeader>(buffer);
    }

    private static Locus LocusFor(SegmentView segment, int chunkId, ChunkGeometry geometry)
    {
        if (!geometry.TryLocate(chunkId, out var ordinal, out _) || ordinal >= segment.Pages.Count)
        {
            return new Locus(-1, segment.RootPageIndex, segment.Kind);
        }

        return new Locus(segment.Pages[ordinal], segment.RootPageIndex, segment.Kind);
    }

    /// <summary>Reads chunks of one segment, caching the current page so a chain walk does not re-read it per hop.</summary>
    private sealed class ChunkReader(IPageSource source, SegmentView segment, ChunkGeometry geometry)
    {
        private readonly byte[] _page = new byte[IntegrityConstants.PageSize];
        private int _loadedPage = -1;

        /// <summary>Reads one chunk by id, reporting whether the segment's bitmap marks it allocated.</summary>
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

            // Readability and allocation are reported separately on purpose. Collapsing them into the return value makes
            // "the chain points at a freed chunk" indistinguishable from "the page could not be read", and CHN-03 needs
            // to say which — a freed chunk is a live hazard (it can be handed to another chain), an unreadable page is a
            // different fault entirely.
            allocated = geometry.IsChunkAllocated(_page, ordinal == 0, chunkInPage);
            chunk = new ReadOnlySpan<byte>(_page, at, geometry.Stride);
            return true;
        }
    }
}
