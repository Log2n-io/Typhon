using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// One archetype's spatial state in one realm: everything keyed by the realm's cell keys or expressed in its frame. An archetype present in N realms
/// has N of these; cluster-indexed state (cell map, AABBs, dirty and migration bookkeeping) stays on <see cref="ArchetypeClusterState"/>, because a
/// chunk id is unique across realms while a cell key is not (claude/design/Realms/01-spatial-ecs.md §1.3).
/// </summary>
/// <remarks>
/// The repair queue and the maintenance budget deliberately do NOT live here: they stay per archetype, their candidates keyed by (realm, cell), so the
/// total budget does not multiply with the realm count (decision D-6).
/// </remarks>
internal sealed class RealmArchetypeSpatial
{
    internal readonly ArchetypeClusterState Owner;
    internal readonly RealmId Realm;

    /// <summary>The realm's grid, copied from the realm so hot paths pay one load.</summary>
    internal readonly SpatialGrid Grid;

    /// <summary>Per-cell list of this archetype's clusters (issue #229 Q10). See <see cref="ArchetypeClusterState.CellClusterPool"/>.</summary>
    internal CellClusterPool CellClusterPool;

    /// <summary>Per-cell cluster spatial index, indexed by cell key. See <see cref="ArchetypeClusterState.PerCellIndex"/>.</summary>
    internal PerCellSpatialSlot[] PerCellIndex;

    /// <summary>How far any cluster's box reaches past its cell. See <see cref="ArchetypeClusterState.ClusterReach"/>.</summary>
    internal float ClusterReach;

    /// <summary>Clusters reaching further than <see cref="ClusterReach"/>, named. See <see cref="ArchetypeClusterState.EscapedClusters"/>.</summary>
    internal EscapedClusterSet EscapedClusters = EscapedClusterSet.Empty;

    /// <summary>
    /// Cells whose linear half holds enough clusters to promote and whose clusters are still too loose for a tree to prune between them.
    /// </summary>
    /// <remarks>
    /// The count gate is evaluated when a cluster joins a cell, which is the only moment the count changes; the TIGHTNESS gate has no such moment — a
    /// repair re-packs a cell without adding a cluster to it, and the cell would then wait for an unrelated arrival to notice it now qualifies. This
    /// list is that missing moment: <c>ArchetypeClusterState.MaybePromoteCellHalf</c> records the cell it turned down on tightness alone, and
    /// <c>ArchetypeClusterState.EvaluateCellTreeTightnessTransitions</c> re-reads it once per fence, when the tick's bounds are final. It holds only
    /// cells at or above the count threshold, so it is empty in every database that never fills one, and the fence-time pass is one null check there.
    /// </remarks>
    internal List<int> TightnessBlockedCells;

    /// <summary>
    /// Cell keys whose half currently holds a tree. Kept so the fence's demote pass costs <c>O(promoted)</c> rather than a scan of every cell that exists.
    /// </summary>
    /// <remarks>
    /// Lazily compacted rather than maintained exactly: <c>ArchetypeClusterState.DemoteCellHalf</c> has four call sites and only two know their cell
    /// key, so an entry whose tree has gone is dropped by the pass that next walks past it. A stale entry costs one null check; a missing one cannot happen, because the
    /// only producer of a tree is the promotion that appends here.
    /// </remarks>
    internal List<int> PromotedCells;

    /// <summary>Cells currently served by a tree, counted. See <see cref="ArchetypeClusterState.PromotedCellCount"/>.</summary>
    internal int PromotedCellCount;

    /// <summary>
    /// The state of no realm: no grid, no pool, no index, zero reach, no escapes. What <see cref="ArchetypeClusterState.SpatialOf"/> returns for a
    /// non-spatial archetype or a missing grid, so a caller's null tests on the members read exactly as they did on the fields. Never populated.
    /// </summary>
    internal static readonly RealmArchetypeSpatial None = new();

    private RealmArchetypeSpatial() => Realm = RealmId.None;

    internal RealmArchetypeSpatial(ArchetypeClusterState owner, RealmId realm, SpatialGrid grid)
    {
        Owner = owner;
        Realm = realm;
        Grid = grid;
        CellClusterPool = CellClusterPool.ForGrid(grid);
    }
}
