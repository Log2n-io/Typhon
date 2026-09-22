using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-archetype per-cell spatial slot holding one cluster index for each of the static/dynamic splits. Lazily allocated — an entry in
/// <c>ArchetypeClusterState.PerCellIndex</c> is null for any cell where this archetype has no clusters (issue #230, Decision Q10).
/// </summary>
/// <remarks>
/// <para>
/// Both dynamic and static halves are populated independently. An archetype's <see cref="SpatialFieldInfo.Mode"/> determines which one gets written at spawn
/// time (Dynamic → dynamic half, Static → static half), but a single cell may contain clusters of different archetypes with different modes, so both are
/// available. Queries check both and union results. This used to mirror a <c>StaticTree</c> / <c>DynamicTree</c> pair on <c>SpatialIndexState</c> at the
/// non-cluster level; those fields are gone with the entity-level index (#872 step 13, rule SH-01), and the two halves here are now the only place the
/// static/dynamic split exists.
/// </para>
/// <para>
/// Issue #230 Phase 3 activated <see cref="StaticIndex"/> as part of closing the issue-body acceptance criterion 7 ("Static/dynamic split: static clusters
/// skip fence updates, queries check both"). Static archetype entities never move, so the tick-fence recompute pass skips static entries entirely — the index
/// is written on spawn, updated on destroy, and otherwise read-only.
/// </para>
/// <para>
/// <b>Each half is EITHER a linear index OR a tree, never both (#872 step 9).</b> Above a per-cell cluster-count threshold the linear index is replaced by a
/// <see cref="CellClusterTree"/> and the <see cref="CellSpatialIndex"/> reference is dropped; below it, the reverse. Keeping both live would mean paying the
/// tree's maintenance cost AND the scan's on every update, which is the worst of both — and the whole reason the threshold exists is that the two structures
/// win in different regimes. <see cref="HasDynamicTree"/> / <see cref="HasStaticTree"/> is the discriminator every reader must consult; a reader that assumes
/// the linear index is present will see an empty cell rather than a wrong one, which is <c>SQ-01</c>'s silent direction, so the discriminator is not optional.
/// </para>
/// <para>
/// <b>Why a threshold rather than the wholesale replacement the design specified.</b> Measured, not assumed: below ~512 clusters in a cell the linear scan
/// beats the tree on selective queries — by 6x at 80 clusters, which is what AntHill's densest zones actually hold — and the tree's update path is 22-38x
/// dearer per moved cluster. Above ~1500 the tree wins by 3.6x and by 25.8x at 15625. Neither structure is right for both ends, and a real world contains
/// both: the clumped sweep puts the mean at 1.8 clusters per cell and the worst cell at 102 for the same population.
/// </para>
/// </remarks>
internal sealed class PerCellSpatialSlot
{
    /// <summary>Dynamic-mode linear cluster index. Null when this cell's dynamic half has been promoted to <see cref="DynamicTree"/>.</summary>
    public CellSpatialIndex DynamicIndex;

    /// <summary>Static-mode linear cluster index. Null when this cell's static half has been promoted to <see cref="StaticTree"/>.</summary>
    public CellSpatialIndex StaticIndex;

    /// <summary>Dynamic-mode R-Tree, present only above the promotion threshold. Mutually exclusive with <see cref="DynamicIndex"/>.</summary>
    public CellClusterTree DynamicTree;

    /// <summary>Static-mode R-Tree, present only above the promotion threshold. Mutually exclusive with <see cref="StaticIndex"/>.</summary>
    public CellClusterTree StaticTree;

    /// <summary>
    /// Publish a freshly-built tree into the dynamic half with release semantics, and drop the linear index it replaces.
    /// </summary>
    /// <remarks>
    /// <para><b>Release, because the tree is fully built before it is published.</b> A plain reference store lets a reader on a weakly-ordered machine observe
    /// the reference before the writes that populated the tree — arm64 is a supported target, so "it works on x64" is not the test. The paired acquire is in
    /// <see cref="ReadDynamicTree"/>, <see cref="HasDynamicTree"/> and <see cref="DynamicClusterCount"/>; every reader must go through one of those rather
    /// than touching the field.</para>
    /// <para><b>Tree FIRST, then clear the index, and both stores release.</b> The obvious order — clear the index, publish the tree — is not a race that
    /// might not happen, it is the ordering the release store GUARANTEES: the null lands first, so a reader in that window sees no tree and no index and
    /// reads the cell as EMPTY. <see cref="DynamicClusterCount"/> returns 0 and the whole cell half drops out of the query, which is the silent SQ-01
    /// direction this type's discriminator exists to prevent. Publishing the tree first inverts it: a reader either sees the tree, or sees no tree and
    /// therefore (by the same ordering) still sees the old index. The second store is a release too, which is what orders it AFTER the first — a plain store
    /// there could be made visible before the tree and reopen the window.</para>
    /// </remarks>
    public void PublishDynamicTree(CellClusterTree tree)
    {
        Volatile.Write(ref DynamicTree, tree);
        Volatile.Write(ref DynamicIndex, null);
    }

    /// <inheritdoc cref="PublishDynamicTree"/>
    public void PublishStaticTree(CellClusterTree tree)
    {
        Volatile.Write(ref StaticTree, tree);
        Volatile.Write(ref StaticIndex, null);
    }

    /// <summary>Replace the dynamic half's tree with a linear index — the demotion direction.</summary>
    public void PublishDynamicIndex(CellSpatialIndex index)
    {
        DynamicIndex = index;
        Volatile.Write(ref DynamicTree, null);
    }

    /// <inheritdoc cref="PublishDynamicIndex"/>
    public void PublishStaticIndex(CellSpatialIndex index)
    {
        StaticIndex = index;
        Volatile.Write(ref StaticTree, null);
    }

    /// <summary>
    /// The dynamic half's tree, acquired. <b>The only sanctioned way for a reader to obtain it</b> — see <see cref="PublishDynamicTree"/> for why a plain
    /// field read is not equivalent, and why the reader must load the tree BEFORE the index rather than the other way round.
    /// </summary>
    public CellClusterTree ReadDynamicTree() => Volatile.Read(ref DynamicTree);

    /// <inheritdoc cref="ReadDynamicTree"/>
    public CellClusterTree ReadStaticTree() => Volatile.Read(ref StaticTree);

    /// <summary>The half's tree when promoted, or null — <paramref name="isStatic"/> selects which half.</summary>
    public CellClusterTree ReadTree(bool isStatic) => isStatic ? Volatile.Read(ref StaticTree) : Volatile.Read(ref DynamicTree);

    /// <summary>The half's linear index when not promoted, or null. Read only AFTER <see cref="ReadTree"/> has returned null for the same half.</summary>
    public CellSpatialIndex ReadIndex(bool isStatic) => isStatic ? StaticIndex : DynamicIndex;

    /// <summary>True when the dynamic half is served by a tree rather than a linear scan.</summary>
    public bool HasDynamicTree => Volatile.Read(ref DynamicTree) != null;

    /// <summary>True when the static half is served by a tree rather than a linear scan.</summary>
    public bool HasStaticTree => Volatile.Read(ref StaticTree) != null;

    /// <summary>Clusters held in the dynamic half, whichever structure is serving it.</summary>
    public int DynamicClusterCount => Volatile.Read(ref DynamicTree)?.ClusterCount ?? DynamicIndex?.ClusterCount ?? 0;

    /// <summary>Clusters held in the static half, whichever structure is serving it.</summary>
    public int StaticClusterCount => Volatile.Read(ref StaticTree)?.ClusterCount ?? StaticIndex?.ClusterCount ?? 0;
}
