using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// <c>IDX-01</c> and <c>ALO-03</c> — index-segment ownership, and the WAL sequence watermark.
/// </summary>
/// <remarks>
/// <para>
/// Two checks that need only what the manifest and the bootstrap already hand over, kept together because both are
/// about a <i>claimed</i> identity rather than about content: one index segment per archetype-and-kind, one log sequence
/// above everything the log holds.
/// </para>
/// <para>
/// The rest of the <c>IDX</c> family — key order, high-key bounds, sibling links, height, entry-to-field agreement —
/// needs the B+Tree node layout decoded, which is a separate piece of work and is declared unrun rather than guessed at.
/// </para>
/// </remarks>
internal static class IndexChecks
{
    /// <summary>Check code: exactly one B+Tree segment per (archetype, index kind).</summary>
    public const string OneTreePerField = "CHK-IDX-01";

    /// <summary>Check code: the WAL sequence watermark is above the log and at or above the checkpoint.</summary>
    public const string LsnWatermark = "CHK-ALO-03";

    /// <summary>Check code: node links resolve, sibling chains terminate, node counts fit the node.</summary>
    public const string TreeStructure = "CHK-IDX-06";

    /// <summary>Runs both checks. <c>IDX-01</c> needs the manifest; <c>ALO-03</c> needs only the bootstrap.</summary>
    /// <param name="ctx">The scan context.</param>
    public static void Run(ScanContext ctx)
    {
        CheckLsnWatermark(ctx);

        if (!ctx.AtLeast(ScanDepth.Standard))
        {
            ctx.Findings.NoteSkipped(OneTreePerField, "needs Standard depth or deeper");
        }
        else if (ctx.Manifest is not { IsUsable: true })
        {
            ctx.Findings.NoteSkipped(OneTreePerField, "the schema manifest could not be read, so index owners are unknown");
        }
        else
        {
            CheckIndexOwnership(ctx);
            CheckTreeStructure(ctx);
        }

        // What is still not decoded, and precisely why — each of these needs the node's KEY array, whose offset and
        // element width depend on which of the four node layouts the tree uses. That follows from the indexed field's
        // type, and reading a node through the wrong variant produces keys that decode perfectly and mean nothing, so
        // the width is not something to guess at.
    }

    /// <summary>
    /// <c>IDX-06</c> — the part of a tree's shape that can be read without knowing its key width.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three failure modes, all reachable from the 20-byte prefix every node layout shares: a link naming a chunk that
    /// is out of range, unreadable or freed; a sibling chain that returns to itself; and a node claiming more entries
    /// than any layout of its stride could hold. The first two are what turn an index walk into a crash or a hang, and
    /// <c>RB-01</c>'s reasoning about hash directories applies unchanged to node links.
    /// </para>
    /// <para>
    /// <b>The directory is also checked for #657's shape.</b> A tree's identity in a shared segment is the pair
    /// (<c>StableId</c>, <c>Slot</c>), and <c>StableId</c> alone is not unique — field ids restart at 0 for every
    /// component, so two components in one archetype that each index their field #0 both register as 0. When that pair
    /// repeats, lookup returns the first match for both trees and one component's index silently resolves the other's
    /// entities. That is a duplicate the directory can be asked about directly.
    /// </para>
    /// </remarks>
    private static void CheckTreeStructure(ScanContext ctx)
    {
        var reader = new IndexDirectoryReader(ctx.Source);
        var entries = new List<IndexTreeEntry>();
        var inspected = 0;

        foreach (var segment in IndexSegments(ctx))
        {
            var page = new byte[IntegrityConstants.PageSize];
            if (segment.Pages.Count == 0 || !ctx.Source.TryReadPage(segment.Pages[0], page))
            {
                continue;
            }

            var geometry = ChunkGeometry.FromPage(page);
            if (!geometry.IsUsable)
            {
                ctx.Findings.NoteCaveat($"The index segment rooted at page {segment.RootPageIndex} records no chunk stride, "
                    + "so its trees were not walked.");
                continue;
            }

            var locus = new Locus(segment.RootPageIndex, segment.RootPageIndex, segment.Kind);

            if (reader.DirectoryOverflows(segment, geometry, out var declared, out var capacity))
            {
                ctx.Report(TreeStructure, IntegritySeverity.Fatal, "RB-01", locus,
                    "An index segment's tree directory claims more trees than it can hold.",
                    $"The directory in chunk 0 of the segment rooted at page {segment.RootPageIndex} declares {declared} "
                    + $"entries, and the chunk holds at most {capacity}. Reading the declared count walks past the chunk and "
                    + "treats whatever follows as further tree roots. The directory was not read.",
                    Repairability.Lossless);
                continue;
            }

            if (!reader.TryReadDirectory(segment, geometry, entries))
            {
                ctx.Report(TreeStructure, IntegritySeverity.Divergence, "", locus,
                    "An index segment's tree directory could not be read.",
                    $"Chunk 0 of the segment rooted at page {segment.RootPageIndex} is unreadable, so the trees it registers "
                    + "cannot be located. Indexes are derived from cluster data, so rebuilding costs nothing.",
                    Repairability.Lossless);
                continue;
            }

            inspected++;
            CheckDirectoryIdentities(ctx, locus, segment, entries);
            CheckNodeChains(ctx, reader, locus, segment, geometry, entries);
        }

        if (inspected == 0)
        {
            ctx.Findings.NoteSkipped(TreeStructure, "no index segment with a readable tree directory was found");
        }
    }

    /// <summary>Every registered tree has a distinct (StableId, Slot) — the #657 collision.</summary>
    private static void CheckDirectoryIdentities(ScanContext ctx, Locus locus, SegmentView segment, List<IndexTreeEntry> entries)
    {
        var seen = new Dictionary<(short, short), IndexTreeEntry>();

        foreach (var entry in entries)
        {
            if (seen.TryGetValue((entry.StableId, entry.Slot), out var first))
            {
                ctx.Report(TreeStructure, IntegritySeverity.Divergence, "IX-02", locus,
                    "Two B+Trees in one index segment claim the same identity.",
                    $"The directory of the segment rooted at page {segment.RootPageIndex} registers {entry.Identity} twice — "
                    + $"once rooted at chunk {first.RootChunkId} and once at chunk {entry.RootChunkId}. A lookup finds the "
                    + "first match for both, so one tree is unreachable and its owner's queries are answered from the "
                    + "other's entries. This is #657's shape. Indexes are derived, so rebuilding resolves it.",
                    Repairability.Lossless);
                continue;
            }

            seen[(entry.StableId, entry.Slot)] = entry;
        }
    }

    /// <summary>Every tree's node links resolve, terminate, and claim counts their nodes could hold.</summary>
    private static void CheckNodeChains(ScanContext ctx, IndexDirectoryReader reader, Locus locus, SegmentView segment,
        ChunkGeometry geometry, List<IndexTreeEntry> entries)
    {
        var visited = new HashSet<int>();

        foreach (var entry in entries)
        {
            if (entry.RootChunkId == 0)
            {
                continue;   // a registered but empty tree
            }

            if (!reader.IsAllocated(segment, geometry, entry.RootChunkId))
            {
                ctx.Report(TreeStructure, IntegritySeverity.Divergence, "IX-01", locus,
                    $"An index registered for {entry.Identity} has no root node.",
                    $"Its directory entry names chunk {entry.RootChunkId} in the segment rooted at page "
                    + $"{segment.RootPageIndex}, which is outside the segment or marked free. The tree cannot be opened; it "
                    + "is derived from cluster data, so rebuilding it costs nothing.",
                    Repairability.Lossless);
                continue;
            }

            var outcome = reader.WalkSiblingChain(segment, geometry, entry.RootChunkId, visited, out var failedAt);
            if (outcome == IndexDirectoryReader.ChainOutcome.Terminated)
            {
                continue;
            }

            var (severity, what, why) = outcome switch
            {
                IndexDirectoryReader.ChainOutcome.Cyclic =>
                    (IntegritySeverity.Fatal, "returns to a node it has already visited",
                        $"Following the level from chunk {entry.RootChunkId} arrives back at chunk {failedAt}. A reader "
                        + "without its own cycle guard does not return — a range scan over this index hangs rather than "
                        + "failing."),
                IndexDirectoryReader.ChainOutcome.Overfull =>
                    (IntegritySeverity.Divergence, "contains a node claiming more entries than it can hold",
                        $"Chunk {failedAt} declares an entry count larger than any node layout of this segment's "
                        + $"{geometry.Stride}-byte stride could store. Reading that many entries walks past the node into "
                        + "the next one's bytes."),
                _ =>
                    (IntegritySeverity.Divergence, "has a sibling link that resolves to nothing",
                        $"Chunk {failedAt} is outside the segment, unreadable, or marked free, and it was NOT followed. A "
                        + "scan of this index stops early at best; a reader that trusts the link dereferences a freed "
                        + "chunk.")
            };

            ctx.Report(TreeStructure, severity, "IX-01", locus,
                $"The index for {entry.Identity} {what}.",
                why + " Indexes are derived from cluster data, so rebuilding costs nothing.",
                Repairability.Lossless);
        }
    }

    /// <summary>Every segment an archetype or a field names as an index, deduplicated.</summary>
    private static IEnumerable<SegmentView> IndexSegments(ScanContext ctx)
    {
        var roots = new HashSet<int>();

        foreach (var archetype in ctx.Manifest.Archetypes.Values)
        {
            roots.Add(archetype.IndexRoot);
            roots.Add(archetype.String64IndexRoot);
        }

        foreach (var component in ctx.Manifest.Components.Values)
        {
            foreach (var field in component.Fields)
            {
                roots.Add(field.IndexRoot);
            }
        }

        foreach (var root in roots)
        {
            if (root != 0 && ctx.Segments.TryGetValue(root, out var segment))
            {
                yield return segment;
            }
        }
    }

    /// <summary>
    /// <c>IDX-01</c> — an index segment belongs to exactly one archetype.
    /// </summary>
    /// <remarks>
    /// <c>IX-02</c>'s failure shape stated at the segment level: two archetypes naming one tree means entries from one
    /// resolve to slots in the other, and every value in a per-archetype index is a <c>ClusterLocation</c> — a pointer
    /// into a cluster SoA. <c>RB-04</c>'s note is explicit that decoding one of those against the wrong cluster is an
    /// access violation rather than a wrong row, so shared ownership is not a divergence that reads oddly, it is a crash.
    /// </remarks>
    private static void CheckIndexOwnership(ScanContext ctx)
    {
        var owners = new Dictionary<int, string>();

        foreach (var archetype in ctx.Manifest.Archetypes.Values)
        {
            Claim(ctx, owners, archetype.IndexRoot, archetype.Name, "secondary-index");
            Claim(ctx, owners, archetype.String64IndexRoot, archetype.Name, "String64 index");
        }
    }

    private static void Claim(ScanContext ctx, Dictionary<int, string> owners, int root, string archetype, string what)
    {
        if (root == 0)
        {
            return;   // "not persisted, rebuild from cluster data" is a documented, legitimate state
        }

        if (!ctx.Segments.TryGetValue(root, out var segment))
        {
            ctx.Report(OneTreePerField, IntegritySeverity.Divergence, "IX-01", new Locus(root),
                $"Archetype '{archetype}' names a {what} segment that does not exist.",
                $"Its row points at page {root}, where the physical sweep found no segment root. The index cannot be "
                + "opened; it is derived from cluster data, so rebuilding it costs nothing.",
                Repairability.Lossless);
            return;
        }

        if (owners.TryGetValue(root, out var first))
        {
            ctx.Report(OneTreePerField, IntegritySeverity.Divergence, "IX-02",
                new Locus(root, root, segment.Kind),
                $"Two archetypes share one {what} segment.",
                $"'{first}' and '{archetype}' both name the segment rooted at page {root}. A per-archetype index stores "
                + "ClusterLocations — pointers into one archetype's cluster — so entries written by one archetype decode "
                + "against the other's cluster. RB-04 records that this is an access violation on first decode rather "
                + "than a wrong row.",
                Repairability.Lossless);
            return;
        }

        owners[root] = archetype;
    }

    /// <summary>
    /// <c>ALO-03</c> — the log sequence watermark sits above the log and at or above the checkpoint.
    /// </summary>
    /// <remarks>
    /// Only the checkpoint half is compared here. The stronger form — <c>NextLsn</c> above every LSN present in
    /// <c>wal/</c> — needs the log's records walked, which is <c>WAL-02</c>'s territory and is not decoded by this
    /// build. The half that IS checked is the one whose violation is unrecoverable: a watermark below the checkpoint
    /// means the next record written reuses a sequence number the data file has already consolidated.
    /// </remarks>
    private static void CheckLsnWatermark(ScanContext ctx)
    {
        var (checkpointLsn, _) = ctx.Bootstrap.ReadWatermarks();
        if (checkpointLsn <= 0)
        {
            return;   // a database that has never checkpointed has nothing to compare against
        }

        if (!ctx.Bootstrap.TryGet("NextLsn", out var value) || value.Type != BootstrapDictionary.ValueType.Long)
        {
            ctx.Findings.NoteSkipped(LsnWatermark, "the bootstrap records no NextLsn to compare against the checkpoint");
            return;
        }

        var nextLsn = value.AsLong;
        if (nextLsn >= checkpointLsn)
        {
            return;
        }

        ctx.Report(LsnWatermark, IntegritySeverity.Fatal, "RB-06", Locus.Database,
            "The log sequence allocator is behind the checkpoint it has already covered.",
            $"The bootstrap records NextLsn = {nextLsn} but a checkpoint LSN of {checkpointLsn}. The next record appended "
            + "to the log takes a sequence number the data file has already consolidated past, so recovery cannot order "
            + "the two and a replay either skips the record or applies it twice.");
    }
}
