using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// <c>ALO-04</c> — the variable-sized buffer handle table: nothing dangling, nothing stranded.
/// </summary>
/// <remarks>
/// <para>
/// A <c>ComponentCollection&lt;T&gt;</c> field is a four-byte buffer id and nothing else, so the whole correctness of
/// collection storage rests on those handles resolving and on every allocated buffer still being named by one. This is
/// the check the alpha plan called <i>"one cheap, high-value fix"</i> for <b>#389</b>, the bufferId leak.
/// </para>
/// <para>
/// <b>The two halves have very different failure modes, and only one of them is always safe to report.</b> A handle
/// that does not resolve is unambiguous: the elements behind it are gone. A buffer that nothing appears to reference is
/// only a leak if the scan can see <i>every</i> reference — and references live wherever a component declares a
/// collection or variable-length string field, which means inside per-entity component data. When any user component
/// declares one, this scan cannot enumerate them, and the leak half stands down rather than reporting every legitimate
/// user buffer as stranded. That restraint is the difference between a check and a nuisance.
/// </para>
/// </remarks>
internal static class BufferChecks
{
    /// <summary>Check code: every buffer handle resolves, and no allocated buffer is unreferenced.</summary>
    public const string HandleTable = "CHK-ALO-04";

    /// <summary>Runs the buffer-handle check. Requires <see cref="ScanDepth.Deep"/> and a readable manifest.</summary>
    /// <param name="ctx">The scan context, with the manifest read.</param>
    public static void Run(ScanContext ctx)
    {
        if (!ctx.AtLeast(ScanDepth.Deep))
        {
            ctx.Findings.NoteSkipped(HandleTable, "needs Deep depth");
            return;
        }

        if (ctx.Manifest is not { IsUsable: true })
        {
            ctx.Findings.NoteSkipped(HandleTable, "the schema manifest could not be read, so buffer handles cannot be resolved");
            return;
        }

        if (ctx.Manifest.CollectionSegments.Count == 0)
        {
            return;   // a database with no collection storage has no handle table to check
        }

        var reader = new VsbsReader(ctx.Source);
        var reachable = new Dictionary<int, HashSet<int>>();
        foreach (var stride in ctx.Manifest.CollectionSegments.Keys)
        {
            reachable[stride] = [];
        }

        CheckManifestHandles(ctx, reader, reachable);
        CheckForStrandedBuffers(ctx, reachable);
    }

    /// <summary>
    /// The forward half: every handle the manifest records resolves to a readable, allocated buffer.
    /// </summary>
    private static void CheckManifestHandles(ScanContext ctx, VsbsReader reader, Dictionary<int, HashSet<int>> reachable)
    {
        foreach (var component in ctx.Manifest.Components.Values)
        {
            Follow(ctx, reader, reachable, component.FieldsBufferId, VsbsReader.StrideForElementSize(FieldRowSize),
                $"component '{component.Name}'", "field descriptors");
        }

        foreach (var archetype in ctx.Manifest.Archetypes.Values)
        {
            Follow(ctx, reader, reachable, archetype.ComponentNamesBufferId, VsbsReader.StrideForElementSize(NameRowSize),
                $"archetype '{archetype.Name}'", "component names");
        }
    }

    private static int FieldRowSize => Unsafe.SizeOf<FieldR1>();

    private static int NameRowSize => Unsafe.SizeOf<String64>();

    private static void Follow(ScanContext ctx, VsbsReader reader, Dictionary<int, HashSet<int>> reachable, int bufferId,
        int stride, string owner, string what)
    {
        if (bufferId == 0)
        {
            return;   // never allocated
        }

        if (!ctx.Manifest.CollectionSegments.TryGetValue(stride, out var target))
        {
            ctx.Report(HandleTable, IntegritySeverity.DataLoss, "RB-06", Locus.Database,
                $"The {what} of {owner} point at buffer storage that does not exist.",
                $"Buffer {bufferId} belongs in a component-collection segment of stride {stride}, and no such segment is in "
                + "the file. The elements behind the handle cannot be recovered from anywhere else — a collection is "
                + "primary data, not a derived index.",
                Repairability.NotRepairable,
                new LossEstimate { Kind = LossKind.Collection, EntityCount = -1, BoundedMin = 1, BoundedMax = 1,
                    Explanation = $"The {what} of {owner}." });
            return;
        }

        // Walking the chain is what resolves the handle: a buffer id that addresses a free chunk, or one whose chain
        // does not terminate, fails here rather than being taken on trust from the root header alone.
        var chunks = new List<int>();
        if (!reader.TryWalkChunkIds(target.Segment, target.Geometry, bufferId, chunks))
        {
            ctx.Report(HandleTable, IntegritySeverity.DataLoss, "RB-06", ctx.LocusForPage(target.Segment.RootPageIndex),
                $"The {what} of {owner} name a buffer that cannot be read.",
                $"Buffer {bufferId} in the segment rooted at page {target.Segment.RootPageIndex} does not resolve to a "
                + "readable, terminating chunk chain. Its elements are unreachable, and a collection is primary data with "
                + "no second copy to rebuild from.",
                Repairability.NotRepairable,
                new LossEstimate { Kind = LossKind.Collection, EntityCount = -1, BoundedMin = 1, BoundedMax = 1,
                    Explanation = $"The {what} of {owner}." });
            return;
        }

        foreach (var chunkId in chunks)
        {
            reachable[stride].Add(chunkId);
        }
    }

    /// <summary>
    /// The reverse half: an allocated buffer chunk that no handle reaches — <b>#389</b>'s shape.
    /// </summary>
    /// <remarks>
    /// Held back entirely when any component declares a collection or variable-length string field, because those
    /// handles live in per-entity component data that this scan does not decode. Reporting under those conditions would
    /// call every legitimate user buffer a leak, which is worse than not checking: a report that cries wolf on healthy
    /// databases trains its reader to ignore it.
    /// </remarks>
    private static void CheckForStrandedBuffers(ScanContext ctx, Dictionary<int, HashSet<int>> reachable)
    {
        var unenumerable = FindUnenumerableReferenceSource(ctx);
        if (unenumerable != null)
        {
            ctx.Findings.NoteSkipped(HandleTable,
                $"{unenumerable} stores buffer handles in per-entity data, which this build does not decode, so allocated "
                + "buffers could not be accounted for (handles the manifest itself records were still resolved)");
            return;
        }

        foreach (var (stride, target) in ctx.Manifest.CollectionSegments)
        {
            var found = reachable[stride];
            var stranded = new List<int>();
            var page = new byte[IntegrityConstants.PageSize];
            var loadedPage = -1;

            for (var chunkId = 1; chunkId < target.Geometry.Capacity(target.Segment.Pages.Count); chunkId++)
            {
                if (!target.Geometry.TryLocate(chunkId, out var ordinal, out var chunkInPage)
                    || ordinal >= target.Segment.Pages.Count)
                {
                    continue;
                }

                var filePage = target.Segment.Pages[ordinal];
                if (loadedPage != filePage)
                {
                    if (!ctx.Source.TryReadPage(filePage, page))
                    {
                        loadedPage = -1;
                        continue;
                    }

                    loadedPage = filePage;
                }

                if (!target.Geometry.IsChunkAllocated(page, ordinal == 0, chunkInPage) || found.Contains(chunkId))
                {
                    continue;
                }

                stranded.Add(chunkId);
            }

            if (stranded.Count == 0)
            {
                continue;
            }

            ctx.Report(HandleTable, IntegritySeverity.Divergence, "RB-06", ctx.LocusForPage(target.Segment.RootPageIndex),
                "Buffer storage is allocated but referenced by nothing.",
                $"{stranded.Count} chunk(s) of the component-collection segment rooted at page {target.Segment.RootPageIndex} "
                + $"— the first is chunk {stranded[0]} — are marked allocated, and no handle in the database reaches them. "
                + "Nothing reads them and nothing frees them, so the space is held for the lifetime of the database. This is "
                + "the shape #389 describes: a buffer id dropped without releasing its buffer.",
                Repairability.Lossless);
        }
    }

    /// <summary>
    /// Names the first component whose fields would put buffer handles somewhere this scan cannot enumerate.
    /// </summary>
    /// <remarks>
    /// The system catalogs are excluded by name and not by heuristic: their handles are exactly the ones
    /// <see cref="CheckManifestHandles"/> follows, so counting them here would stand the check down on every database.
    /// </remarks>
    private static string FindUnenumerableReferenceSource(ScanContext ctx)
    {
        foreach (var component in ctx.Manifest.Components.Values)
        {
            if (component.Name == ComponentR1.SchemaName || component.Name == ArchetypeR1.SchemaName)
            {
                continue;
            }

            foreach (var field in component.Fields)
            {
                if (field.Type is FieldType.Collection or FieldType.String)
                {
                    return $"component '{component.Name}' field '{field.Name}'";
                }
            }
        }

        return null;
    }
}
