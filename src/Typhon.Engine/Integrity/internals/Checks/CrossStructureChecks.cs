using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// The checks that need two families to have run first: <c>CHN-06</c> and <c>CLU-04</c>.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from the families they draw on because of an ordering constraint that is easy to get wrong silently.
/// <c>CHN-06</c> compares the chain roots the revision segments hold against the ones the EntityMap references, and
/// those two sets are produced by passes that run in that order — so the comparison cannot live in either of them
/// without one side reading a half-filled set and reporting the difference as damage.
/// </para>
/// <para>
/// Both checks are about <b>reachability</b> rather than about a structure being well-formed: storage that nothing
/// points at, and a slot array whose width nothing agrees on. Neither shows up in a walk of a single structure, which
/// is why they are the last two of the Tier-1 set to become checkable.
/// </para>
/// </remarks>
internal static class CrossStructureChecks
{
    /// <summary>Check code: every revision-chain root is referenced by an entity record.</summary>
    public const string ChainRootsReferenced = "CHK-CHN-06";

    /// <summary>Check code: the cluster's per-component enabled-bit words match the archetype's component count.</summary>
    public const string EnabledBitsWidth = "CHK-CLU-04";

    /// <summary>Runs both checks. Requires <see cref="ScanDepth.Deep"/> and a readable manifest.</summary>
    /// <param name="ctx">The scan context, after the chain, cluster and entity-map passes.</param>
    public static void Run(ScanContext ctx)
    {
        if (!ctx.AtLeast(ScanDepth.Deep))
        {
            ctx.Findings.NoteSkipped($"{ChainRootsReferenced}, {EnabledBitsWidth}", "needs Deep depth");
            return;
        }

        if (ctx.Manifest is not { IsUsable: true })
        {
            ctx.Findings.NoteSkipped($"{ChainRootsReferenced}, {EnabledBitsWidth}",
                "the schema manifest could not be read, so cross-structure reachability cannot be established");
            return;
        }

        CheckOrphanedChains(ctx);
        CheckEnabledBitsWidth(ctx);
    }

    /// <summary>
    /// <c>CHN-06</c> — a revision chain nothing references is storage no one will ever reclaim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The direction matters. An entity record naming a chain that does not exist is a dangling pointer, already caught
    /// by <c>CHN-03</c> when the chain is walked. This is the reverse: a well-formed chain, with a real owning entity
    /// key, that no live entity record points at. Nothing reads it, nothing frees it, and every future scan re-reports
    /// its contents as live history.
    /// </para>
    /// <para>
    /// Reported as a <c>Leak</c> rather than as data loss, and that is the honest classification: the entity's data is
    /// intact, it is the reference to it that is gone. Whether the entity itself still exists is <c>MAP-02</c>'s
    /// question, not this one.
    /// </para>
    /// </remarks>
    private static void CheckOrphanedChains(ScanContext ctx)
    {
        if (ctx.ChainRoots.Count == 0)
        {
            ctx.Findings.NoteSkipped(ChainRootsReferenced, "no revision segment was walked, so there are no chain roots to account for");
            return;
        }

        foreach (var (componentName, roots) in ctx.ChainRoots)
        {
            if (roots.Count == 0)
            {
                continue;
            }

            // No referenced set at all means no archetype holding this component had a readable EntityMap. Every root
            // would then look orphaned, which is a statement about the scan rather than about the database.
            if (!ctx.ReferencedChainRoots.TryGetValue(componentName, out var referenced))
            {
                ctx.Findings.NoteSkipped(ChainRootsReferenced,
                    $"no entity map referencing '{componentName}' could be read, so its chain roots could not be accounted for");
                continue;
            }

            var orphans = new List<int>();
            long firstOwner = 0;
            foreach (var (chunkId, owner) in roots)
            {
                if (referenced.Contains(chunkId))
                {
                    continue;
                }

                if (orphans.Count == 0)
                {
                    firstOwner = owner;
                }

                orphans.Add(chunkId);
            }

            if (orphans.Count == 0)
            {
                continue;
            }

            var locus = ctx.Manifest.Components.TryGetValue(componentName, out var component) && component.RevisionSegmentRoot != 0
                ? ctx.LocusForPage(component.RevisionSegmentRoot)
                : Locus.Database;

            ctx.Report(ChainRootsReferenced, IntegritySeverity.Divergence, "", locus,
                $"Revision chains of '{componentName}' are referenced by nothing.",
                $"{orphans.Count} chain root(s) — the first is chunk {orphans[0]}, owned by entity key {firstOwner} — carry a "
                + "live owning entity but appear in no entity record. Their storage is never read and never released: "
                + "recovery cannot reclaim a chain it cannot reach, so the space is held for the lifetime of the database. "
                + "Rebuilding the entity map restores the references when the entities are still live, and drops the chains "
                + "when they are not.",
                Repairability.Lossless);
        }
    }

    /// <summary>
    /// <c>CLU-04</c> — the cluster's slot array starts where the archetype's component count says it does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cluster is <c>[occupancy 8 B][enabledBits 8 B × componentCount][entity keys 8 B × 64][component SoA…]</c>, so
    /// the component count is not a description of the layout — it <i>is</i> the layout. Every other cluster check
    /// addresses entity keys through it, which makes this the one check whose failure invalidates the family that
    /// depends on it, and the reason it reports as <c>Fatal</c> rather than as a divergence.
    /// </para>
    /// <para>
    /// The count is corroborated against the archetype's own component-name list, which is stored separately in a VSBS
    /// buffer. Two independently persisted records of the same fact disagreeing is exactly the signal wanted here —
    /// a single field cannot be checked against itself.
    /// </para>
    /// </remarks>
    private static void CheckEnabledBitsWidth(ScanContext ctx)
    {
        var checkedAny = false;

        foreach (var archetype in ctx.Manifest.Archetypes.Values)
        {
            if (archetype.ComponentNames.Count == 0)
            {
                continue;   // unreadable name list is already a diagnostic from the manifest reader
            }

            checkedAny = true;
            if (archetype.ComponentNames.Count == archetype.ComponentCount)
            {
                continue;
            }

            var locus = archetype.ClusterSegmentRoot != 0 ? ctx.LocusForPage(archetype.ClusterSegmentRoot) : Locus.Database;

            ctx.Report(EnabledBitsWidth, IntegritySeverity.Fatal, "", locus,
                $"The cluster layout for '{archetype.Name}' is described two different ways.",
                $"Its row records {archetype.ComponentCount} components while its component-name list holds "
                + $"{archetype.ComponentNames.Count}. The count fixes the width of the per-component enabled-bit words, and "
                + "therefore the offset of the entity-key array and of every component's data behind it. Whichever value the "
                + "engine uses, one of the two records is wrong, and reading the cluster through the wrong one returns "
                + "another component's bytes as this one's — without failing.");
        }

        if (!checkedAny)
        {
            ctx.Findings.NoteSkipped(EnabledBitsWidth,
                "no archetype produced a readable component-name list to corroborate its component count against");
        }
    }
}
