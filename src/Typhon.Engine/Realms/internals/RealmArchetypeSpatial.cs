using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// One archetype's spatial state in one realm: everything keyed by the realm's cell keys or expressed in its frame. An archetype present in N realms
/// has N of these; cluster-indexed state (cell map, AABBs, dirty and migration bookkeeping) stays on <see cref="ArchetypeClusterState"/>, because a
/// chunk id is unique across realms while a cell key is not (claude/design/Realms/01-spatial-ecs.md §1.3).
/// </summary>
/// <remarks>
/// The repair queue and the maintenance budget deliberately do NOT live here: they stay per archetype, so the total budget does not multiply with the
/// realm count (decision D-6). Until the queue's candidates are keyed by (realm, cell), repair runs in the primary realm only (Realms C1, §12).
/// </remarks>
internal sealed class RealmArchetypeSpatial
{
    internal readonly ArchetypeClusterState Owner;
    internal readonly RealmId Realm;

    /// <summary>The realm's grid, copied from the realm so hot paths pay one load.</summary>
    internal readonly SpatialGrid Grid;

    /// <summary>
    /// Per-archetype per-cell cluster claim list (issue #229 Q10 resolution): the cluster chunk IDs of THIS archetype's clusters attached to each cell of
    /// this realm's grid. Before Q10 the pool was owned by <see cref="SpatialGrid"/> and shared across archetypes, so two spatial archetypes couldn't
    /// coexist on one grid (their cluster chunk IDs collided at the cell level); each archetype owns its own, so queries and spawn-time "find a free slot
    /// in this cell" scans see only clusters of the current archetype.
    /// </summary>
    internal CellClusterPool CellClusterPool;

    /// <summary>
    /// Per-archetype per-cell spatial slot, indexed by cellKey. Null entries for cells where this archetype has no clusters. Lazy-allocated:
    /// the <see cref="PerCellSpatialSlot"/> is created on first cluster insertion into that cell. The DynamicIndex inside is also lazy (created on first
    /// <see cref="CellSpatialIndex.Add"/>). Null entirely for non-spatial archetypes or before grid opt-in.
    /// </summary>
    internal PerCellSpatialSlot[] PerCellIndex;

    /// <summary>
    /// How far past its own cell every cell-walking query reaches for this archetype's clusters, in world units: the largest IN-WORLD overhang of any
    /// cluster not named in <see cref="EscapedClusters"/>. Recomputed at every fence from the live index (<see
    /// cref="ArchetypeClusterState.RefreshClusterReach"/>), so it falls again once the cluster that raised it is fixed; between fences only a spawn raises
    /// it (<see cref="ArchetypeClusterState.RaiseClusterReachForSpawn"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>Why a cluster can leave its cell at all.</b> Cell membership is decided by an entity's CENTRE — <c>SpatialGrid.ReadSpatialCenter3D</c>, and the
    /// migration check in <c>DetectClusterMigrations</c> uses the same point — so an entity with extent protrudes past its cell by up to its own
    /// half-extent, a drifter inside the migration hysteresis band by that band as well, and the cluster box that unions them protrudes with them.
    /// <c>C13</c> makes a cluster belong to exactly one cell; it does not make its geometry fit inside one. Every cell-walking query therefore grows its cell
    /// range by this much (SQ-01), and kNN's stopping rule subtracts it (<see cref="ArchetypeClusterState.CoveredRadiusSq"/>).</para>
    /// <para><b>In-world only.</b> A side of an EDGE cell faces no cell, and every query's cell range is clamped into the grid, so the part of a box beyond
    /// the grid can never make a query miss its cluster — a query reaching for it lands in that same edge cell. It is not counted. On the SWG Tatooine
    /// world that part was the whole of the ~930 m every Creature and Lair query used to be widened by: lairs and their creatures placed outside the playable
    /// area are filed in edge cells, and their boxes reach out of the world, never into a neighbour (2026-09-13).</para>
    /// <para><b>Why it may fall, when it used to be a running maximum.</b> "Too large merely widens a search" held while only kNN read it. Once box, radius,
    /// ray and frustum queries widened by it too, one transient outlier — an entity teleported across the map and not yet migrated, a box a migration left
    /// stale — cost every later query of the archetype ~50x its cells for the rest of the process: SWG's whole-run slow mode and its x128 multi-second
    /// ticks. So it is recomputed at each fence from the cluster boxes, which bound what the coming tick's queries read, and the outliers above it are
    /// named instead.</para>
    /// <para><b>Between fences only a spawn can raise it.</b> A move grows <see cref="ArchetypeClusterState.ClusterAabbs"/> at once, but reaches the
    /// per-cell index — what queries read — at the fence, or earlier only through a spawn's widen or a tree demotion that republish the cluster's current
    /// box; a moved entity is therefore reachable from the fence after its write, as it always was. A spawn widens the index at once, so it raises the
    /// reach by the spawned entity's own in-world overhang first. Nothing structural runs during the fence (EW-01: a spawn's EntityMap insert throws
    /// inside the fence window), and queries do not either, so the recompute's stores cannot race a raise or be half-seen by a query.</para>
    /// </remarks>
    internal float ClusterReach;

    /// <summary>
    /// The clusters whose in-world overhang exceeds <see cref="ClusterReach"/>, named so that no query has to widen to reach them. Published by
    /// <see cref="ArchetypeClusterState.RefreshClusterReach"/> with a release store; never null.
    /// </summary>
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

    /// <summary>Cells currently served by a tree, counted for telemetry and for the tests that assert promotion did or did not happen.</summary>
    internal int PromotedCellCount;

    /// <summary>
    /// This realm's share of the multi-realm reach walk (<c>ArchetypeClusterState.RefreshClusterReachAcrossRealms</c>): its kept overhangs and reject
    /// bounds while one pass scores every realm's clusters. Allocated the first time the archetype is in two realms at once; reused every tick after.
    /// </summary>
    internal ReachScan ReachScratch;

    /// <summary>A realm's running state in the multi-realm reach walk. Fence-only, one archetype at a time.</summary>
    internal sealed class ReachScan
    {
        internal readonly double[] TopReach = new double[EscapedClusterSet.Capacity + 1];
        internal readonly int[] TopId = new int[EscapedClusterSet.Capacity + 1];
        internal readonly int[] TopCell = new int[EscapedClusterSet.Capacity + 1];
        internal int Kept;
        internal double Admit;
        internal double Cell;
        internal float Lo;
        internal float Hi;
        internal bool Valid;
    }

    /// <summary>
    /// The state of no realm: no grid, no pool, no index, zero reach, no escapes. What <see cref="ArchetypeClusterState.SpatialOf"/> returns for a
    /// non-spatial archetype or a missing grid, so a caller's null tests on the members read exactly as they did on the fields. Never populated.
    /// </summary>
    internal static readonly RealmArchetypeSpatial None = new();

    private RealmArchetypeSpatial() => Realm = RealmId.None;

    /// <summary>
    /// Debug guard at every path that WRITES realm state: <see cref="None"/> is shared by every non-spatial archetype and missing grid in the process, so a
    /// write into it would leak one archetype's cells into all of them. Unreachable today (the paths run only for spatial archetypes, whose state exists).
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    internal static void AssertNotNone(RealmArchetypeSpatial rs) =>
        System.Diagnostics.Debug.Assert(!ReferenceEquals(rs, None), "a write reached RealmArchetypeSpatial.None — the shared state of no realm");

    internal RealmArchetypeSpatial(ArchetypeClusterState owner, RealmId realm, SpatialGrid grid)
    {
        Owner = owner;
        Realm = realm;
        Grid = grid;
        CellClusterPool = CellClusterPool.ForGrid(grid);
    }
}
