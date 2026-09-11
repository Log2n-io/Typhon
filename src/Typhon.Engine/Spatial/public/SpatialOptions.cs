using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Tuning for the spatial layer's per-cell broadphase — the structure a cell uses to answer "which clusters overlap this box".
/// </summary>
/// <remarks>
/// <para><b>Every cell scans unless the application opts in.</b> Every cell starts on a linear SoA scan — six float compares per cluster over contiguous
/// memory, 64 clusters per SIMD batch, no pointer chasing — and by default it stays there. A per-cell R-Tree is built only once the application sets
/// <see cref="CellTreePromoteThreshold"/>, and then only for a cell half that is both dense (that many clusters) and packed
/// (<see cref="CellTreePromoteTightness"/>); a promoted half falls back when either lapses. There is no per-cell override: the gate reads each cell's own
/// density and shape.</para>
/// <para><b>Why it is exposed at all.</b> The crossover is a function of query selectivity and of the query-to-update ratio, and those are properties of the
/// application rather than of the engine. A workload that queries a dense, packed cell far more often than it moves things in it may win from a tree; one
/// that moves everything every tick loses, because the tree's upkeep runs in the fence's serial tail and is dearer per moved cluster than six float stores.
/// The shipped default is "never", because the one game-shaped workload measured lost the tick at every density — see
/// <see cref="DefaultCellTreePromoteThreshold"/> — and the guide says how to tell whether yours is different.</para>
/// </remarks>
[PublicAPI]
public sealed class SpatialOptions
{
    /// <summary>
    /// The default <see cref="CellTreePromoteThreshold"/>: <see cref="int.MaxValue"/> — no cell half is promoted to an R-Tree unless the application
    /// sets a count.
    /// </summary>
    /// <remarks>
    /// Above ~1 000 clusters a promoted cell's broadphase beats the linear scan (3–4× at 4 000+ on selective boxes), but the whole query gains ≤ 1.1×,
    /// because the per-entity narrowphase dominates it, and the tree's upkeep runs in the fence's serial tail: on #906's SWG Tatooine at 16× population a
    /// forced tree made the tick 1.43–1.75× slower. Setting a count opts in; the half then also has to pass <see cref="CellTreePromoteTightness"/>, and
    /// falls back at half the count.
    /// </remarks>
    public const int DefaultCellTreePromoteThreshold = int.MaxValue;

    /// <summary>
    /// Clusters in one cell half (Static or Dynamic) at which that half may be promoted from a linear scan to a per-cell R-Tree — it promotes once it also
    /// passes <see cref="CellTreePromoteTightness"/>. Defaults to <see cref="int.MaxValue"/>, which keeps every cell on the linear scan whatever its density.
    /// </summary>
    /// <remarks>
    /// <para>Counted in CLUSTERS, not entities. A cluster holds up to 64 entities of one archetype that share a cell, so a count of 1 024 corresponds to a cell
    /// carrying on the order of sixty thousand entities of one archetype before anything changes shape.</para>
    /// <para>Promotion is evaluated when a cluster is added to a cell, and it rebuilds that cell half in <c>O(C)</c>. The fall-back is at half this value, and
    /// the gap is what stops a cell hovering on the boundary from rebuilding itself twice per tick.</para>
    /// </remarks>
    public int CellTreePromoteThreshold { get; set; } = DefaultCellTreePromoteThreshold;

    /// <summary>
    /// The default <see cref="CellTreePromoteTightness"/>: the mean cluster extent, as a fraction of the cell edge, at or below which a cell's clusters
    /// are tight enough for a tree to prune between them.
    /// </summary>
    /// <remarks>
    /// <para><b>The count threshold was calibrated on a layout the engine does not produce.</b> The sweep that chose 1 024 lays its clusters at 1.5x
    /// perfect tiling — 3.8 % of the cell at 1 563 clusters — while a real cell runs at 63-103 %. Re-run with the cluster edge as a controlled fraction of
    /// the cell, the tree wins 1.47x at 0.038 and 1.00x at 0.10, and LOSES at every count and every selectivity from 0.25 upward: 0.24-0.46x at the
    /// packing target, 0.08x at the 0.90 the engine reaches under motion. Hits go as (query + cluster)^2 of the population, so a loose cell has every
    /// cluster hit by every query and the tree returns all of them after paying traversal — and pays the 20-50x update tax per moved cluster for it.</para>
    /// <para>Hence <b>0.10</b>, and hence a gate on tightness AND count rather than count alone: promote a cell only where the tree can prune, which is a
    /// cell the repair has packed. <c>1</c> disables the tightness half and restores count-only promotion. The fall-back sits at twice this value for the
    /// same reason the count fall-back sits at half the count: a cell hovering on the boundary must not rebuild itself in both directions every tick.</para>
    /// </remarks>
    public const float DefaultCellTreePromoteTightness = 0.10f;

    /// <summary>
    /// Mean cluster extent, as a fraction of the cell edge, at or below which a cell half may promote to a per-cell R-Tree. <c>1</c> promotes on
    /// <see cref="CellTreePromoteThreshold"/> alone.
    /// </summary>
    /// <remarks>
    /// Measured on the largest axis of each cluster's bound, averaged over the cell half, and evaluated both when a cluster joins the cell and at the
    /// fence once the tick's bounds are final — so a cell the repair has just packed promotes on that tick rather than waiting for its next arrival. A
    /// promoted half falls back when the mean reaches twice this value.
    /// </remarks>
    public float CellTreePromoteTightness { get; set; } = DefaultCellTreePromoteTightness;
}
