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
        }

        ctx.Findings.NoteSkipped("CHK-IDX-02, CHK-IDX-03, CHK-IDX-04, CHK-IDX-05, CHK-IDX-06, CHK-IDX-07",
            "B+Tree node structure is not decoded by this build, so index contents and shape were not inspected");
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
