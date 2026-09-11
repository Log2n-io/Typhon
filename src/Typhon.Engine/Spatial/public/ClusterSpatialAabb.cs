using System;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Per-cluster tight AABB plus category mask, used by the per-cell cluster spatial index (issue #230).
/// One instance per spatially-active cluster, indexed by clusterChunkId. Stored in-memory only on
/// <see cref="ArchetypeClusterState"/> and rebuilt at startup via <c>RebuildClusterAabbs</c> from
/// entity positions (Q2/Q6 transient-state decision).
/// </summary>
/// <remarks>
/// <para><b>The six bounds are CELL-RELATIVE, not world-space</b> (#872 step 9, decision <c>C15</c>). They are offsets from the world-space minimum corner of
/// the cell the cluster belongs to, which <c>SpatialGrid.CellOrigin</c> derives from the cluster's entry in <c>ClusterCellMap</c>. A cluster lives wholly
/// inside one cell (<c>C13</c>), so that origin is unambiguous — and it is why a cluster's bounds must be REBASED when the cluster migrates to another cell.
/// A bound left un-rebased is off by exactly one cell size, which is a silent <c>SQ-01</c> false negative rather than an error.</para>
/// <para><b>Why not world space.</b> f32 across a ±10⁹ world resolves to ~64 units; measured against a cell the magnitude is bounded by the cell size, and
/// the same 24 mantissa bits resolve ~6 × 10⁻⁵. Note the limit this does NOT lift: an <b>f32 tier's</b> own spatial component is world-space f32, so at
/// extreme magnitudes the source coordinate is already coarse and cell-relative storage cannot recover precision the input never carried. <c>C15</c> buys
/// resolution for DERIVED bounds — this AABB and the R-Tree's node bounds — not for the component field they are computed from. The answer to that limit is
/// not to widen this struct: it is to declare the archetype at an <b>f64 tier</b> (<c>AABB2D</c>/<c>AABB3D</c>), whose component carries the magnitude and
/// whose query box and result carry it back out (#914). The stored bound stays exactly as wide as it is here either way.</para>
/// <para>Conversion goes through <see cref="ToCellRelativeMin"/> / <see cref="ToCellRelativeMax"/>, never a bare subtraction: the narrowing to f32 rounds,
/// and rounding the wrong way puts a bound inside the entity it must contain.</para>
/// <para>
/// <b>Storage shape.</b> 28 bytes: six f32 bounds components (XYZ min/max) plus a 4-byte category mask.
/// 2D archetypes leave <see cref="MinZ"/>/<see cref="MaxZ"/> at the <see cref="Empty"/> sentinel (+inf/-inf);
/// 2D queries use an infinite Z range which trivially passes the Z overlap test, so 2D clusters match
/// correctly. 3D archetypes populate all six bounds. The unified 3D storage adds ~8 bytes per cluster
/// versus a 2D-only design, in exchange for a single cluster-index code path that handles both tiers.
/// The f64 tiers (AABB2D/AABB3D and their BSphere forms) are supported since #914 and are stored here unchanged — as f32 CELL-RELATIVE bounds, which is
/// the point of C15 rather than a compromise with it.
/// </para>
/// <para>
/// The <see cref="CategoryMask"/> is the OR of all entity category masks in the cluster — it lets the
/// per-cell broadphase skip entire clusters when the query's category mask does not intersect. Maintained
/// incrementally on spawn and on migration. The fence's full recompute does NOT tighten it: the category is an
/// archetype constant rather than per-entity geometry, so there is nothing to re-derive, and the recompute
/// deliberately reads the stored value back and preserves it (<c>ArchetypeClusterState.ReadStoredCategoryMask</c>,
/// asserted by <c>ClusterSpatialAabbRecomputeTests.TickFence_CategoryMaskPreservedAcrossRecompute</c>). The bounds
/// beside it are re-derived; this field is carried through.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
[JetBrains.Annotations.PublicAPI]
public struct ClusterSpatialAabb
{
    /// <summary>Minimum X bound of the cluster's tight AABB, RELATIVE to its cell's world-space minimum corner (<c>C15</c>).</summary>
    public float MinX;

    /// <summary>Minimum Y bound of the cluster's tight AABB, RELATIVE to its cell's world-space minimum corner (<c>C15</c>).</summary>
    public float MinY;

    /// <summary>Minimum Z bound, RELATIVE to its cell's world-space minimum corner (<c>C15</c>). Left at the <see cref="Empty"/> sentinel (+inf) for
    /// 2D archetypes.</summary>
    public float MinZ;

    /// <summary>Maximum X bound of the cluster's tight AABB, RELATIVE to its cell's world-space minimum corner (<c>C15</c>).</summary>
    public float MaxX;

    /// <summary>Maximum Y bound of the cluster's tight AABB, RELATIVE to its cell's world-space minimum corner (<c>C15</c>).</summary>
    public float MaxY;

    /// <summary>Maximum Z bound, RELATIVE to its cell's world-space minimum corner (<c>C15</c>). Left at the <see cref="Empty"/> sentinel (-inf) for
    /// 2D archetypes.</summary>
    public float MaxZ;

    /// <summary>
    /// OR of every entity category mask in the cluster. Lets the per-cell broadphase skip the whole cluster when the query's category mask does not intersect.
    /// </summary>
    public uint CategoryMask;

    /// <summary>Static empty sentinel for ref-returning properties when no spatial data exists.</summary>
    internal static ClusterSpatialAabb s_empty = new()
    {
        MinX = float.PositiveInfinity, MinY = float.PositiveInfinity, MinZ = float.PositiveInfinity,
        MaxX = float.NegativeInfinity, MaxY = float.NegativeInfinity, MaxZ = float.NegativeInfinity,
        CategoryMask = 0u,
    };

    /// <summary>Create an empty AABB suitable as the seed for incremental unions (min = +inf, max = -inf on all axes).</summary>
    public static ClusterSpatialAabb Empty => new()
    {
        MinX = float.PositiveInfinity,
        MinY = float.PositiveInfinity,
        MinZ = float.PositiveInfinity,
        MaxX = float.NegativeInfinity,
        MaxY = float.NegativeInfinity,
        MaxZ = float.NegativeInfinity,
        CategoryMask = 0u,
    };

    /// <summary>
    /// Union a 2D entity's tight AABB + category mask into this cluster AABB in place. Leaves <see cref="MinZ"/>/<see cref="MaxZ"/> at their initial
    /// values; 2D cluster archetypes never populate Z bounds, and 2D queries against those clusters use an infinite Z range that trivially passes the Z
    /// overlap test regardless of the stored Z values.
    /// </summary>
    public void Union2F(float entityMinX, float entityMinY, float entityMaxX, float entityMaxY, uint entityCategoryMask)
    {
        if (entityMinX < MinX) MinX = entityMinX;
        if (entityMinY < MinY) MinY = entityMinY;
        if (entityMaxX > MaxX) MaxX = entityMaxX;
        if (entityMaxY > MaxY) MaxY = entityMaxY;
        CategoryMask |= entityCategoryMask;
    }

    /// <summary>
    /// Union a 3D entity's tight AABB + category mask into this cluster AABB in place. Updates all six bounds components.
    /// </summary>
    public void Union3F(float entityMinX, float entityMinY, float entityMinZ, float entityMaxX, float entityMaxY, float entityMaxZ, uint entityCategoryMask)
    {
        if (entityMinX < MinX) MinX = entityMinX;
        if (entityMinY < MinY) MinY = entityMinY;
        if (entityMinZ < MinZ) MinZ = entityMinZ;
        if (entityMaxX > MaxX) MaxX = entityMaxX;
        if (entityMaxY > MaxY) MaxY = entityMaxY;
        if (entityMaxZ > MaxZ) MaxZ = entityMaxZ;
        CategoryMask |= entityCategoryMask;
    }

    /// <summary>
    /// <see cref="Union2F"/> for a box other threads are widening at the same time: every axis is a CAS loop that only ever moves a min down or a
    /// max up, so two spawns into one cluster cannot lose each other's widening the way the plain read-modify-write could (step 15 review, CA-01).
    /// </summary>
    public static void WidenCas2F(ref ClusterSpatialAabb box, float entityMinX, float entityMinY, float entityMaxX, float entityMaxY, uint entityCategoryMask)
    {
        CasMin(ref box.MinX, entityMinX);
        CasMin(ref box.MinY, entityMinY);
        CasMax(ref box.MaxX, entityMaxX);
        CasMax(ref box.MaxY, entityMaxY);
        Interlocked.Or(ref box.CategoryMask, entityCategoryMask);
    }

    /// <summary><see cref="Union3F"/> as a per-axis CAS — see <see cref="WidenCas2F"/>.</summary>
    public static void WidenCas3F(ref ClusterSpatialAabb box, float entityMinX, float entityMinY, float entityMinZ, float entityMaxX, float entityMaxY,
        float entityMaxZ, uint entityCategoryMask)
    {
        CasMin(ref box.MinX, entityMinX);
        CasMin(ref box.MinY, entityMinY);
        CasMin(ref box.MinZ, entityMinZ);
        CasMax(ref box.MaxX, entityMaxX);
        CasMax(ref box.MaxY, entityMaxY);
        CasMax(ref box.MaxZ, entityMaxZ);
        Interlocked.Or(ref box.CategoryMask, entityCategoryMask);
    }

    /// <summary>CAS-loop float min update: write <paramref name="candidate"/> if it is still less than the stored value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CasMin(ref float storedRef, float candidate)
    {
        while (true)
        {
            var current = storedRef;
            if (candidate >= current)
            {
                return;
            }

            var currentBits = BitConverter.SingleToInt32Bits(current);
            var candidateBits = BitConverter.SingleToInt32Bits(candidate);
            ref var storedAsInt = ref Unsafe.As<float, int>(ref storedRef);
            if (Interlocked.CompareExchange(ref storedAsInt, candidateBits, currentBits) == currentBits)
            {
                return;
            }
            // Another thread updated; retry the comparison against the new value.
        }
    }

    /// <summary>CAS-loop float max update: write <paramref name="candidate"/> if it is still greater than the stored value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CasMax(ref float storedRef, float candidate)
    {
        while (true)
        {
            var current = storedRef;
            if (candidate <= current)
            {
                return;
            }

            var currentBits = BitConverter.SingleToInt32Bits(current);
            var candidateBits = BitConverter.SingleToInt32Bits(candidate);
            ref var storedAsInt = ref Unsafe.As<float, int>(ref storedRef);
            if (Interlocked.CompareExchange(ref storedAsInt, candidateBits, currentBits) == currentBits)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Convert a world-space LOWER bound to cell-relative space, rounding AWAY from the entity so the result can only ever be too low (<c>C15</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>The subtraction is done in double and the narrowing is what rounds.</b> That is not defensive symmetry — it is where the error actually is.
    /// The bounds reaching this method come from <c>SpatialMaintainer.ReadAndValidateBoundsFromPtr</c>, which produces <see cref="double"/>, and even for an
    /// f32 source the exact difference need not be representable in f32: with an origin of 0.1 and a coordinate of 10⁷ the true offset needs more mantissa
    /// than f32 has, so <c>(float)</c> rounds it — and round-to-nearest rounds UP half the time, putting the lower bound INSIDE the entity it is supposed to
    /// contain.</para>
    /// <para>That is a <c>CA-01</c> violation, and <c>CA-01</c>'s own <c>on_violation</c> says what it looks like from outside: <i>"AABB too tight → per-cell
    /// cluster spatial queries miss entities (false negatives, silent)"</i>. One conditional <see cref="MathF.BitDecrement"/> removes the whole class. The
    /// test that pins it is an ablation to plain <c>(float)</c> narrowing at large magnitudes.</para>
    /// </remarks>
    public static float ToCellRelativeMin(double worldValue, double cellOrigin)
    {
        double relative = worldValue - cellOrigin;
        float narrowed = (float)relative;

        // Only when the narrowing moved the bound the WRONG way. Widening unconditionally would also be correct, but it would compound: a bound that is read
        // and re-stored every tick would drift outward by an ULP each time, decaying the tightness this design exists to buy with nothing to show for it.
        return narrowed > relative ? MathF.BitDecrement(narrowed) : narrowed;
    }

    /// <summary>Convert a world-space UPPER bound to cell-relative space, rounding away from the entity. See <see cref="ToCellRelativeMin"/>.</summary>
    public static float ToCellRelativeMax(double worldValue, double cellOrigin)
    {
        double relative = worldValue - cellOrigin;
        float narrowed = (float)relative;
        return narrowed < relative ? MathF.BitIncrement(narrowed) : narrowed;
    }

    /// <summary>
    /// Convert a cell-relative bound to an <b>f64</b> world coordinate — the conversion the f64 world frame wants, and the one every world-space read-back
    /// uses since #914.
    /// </summary>
    /// <remarks>
    /// <para><b>One method covers both min and max, and the reason is a size argument rather than a claim of perfection.</b> The f32-returning variants
    /// this replaced needed a direction because the NARROWING is what rounds, and rounding the wrong way moves a bound INSIDE the entity it must contain
    /// (<c>CA-01</c>). Here nothing narrows: the sum is formed and returned in double.</para>
    /// <para><b>How exact, precisely.</b> The addition is exact whenever the result fits in 53 bits, and for a bound produced by this engine it does: the
    /// offset is an f32 bounded by one cell (24 bits of mantissa) and the origin is <c>worldMin + cellIndex × cellSize</c> with the index bounded to 21 bits
    /// by <c>VdbBlockKey</c> — 21 + 24 = 45, and the cell size cancels out of that sum, so it holds at any cell size. What is NOT covered is a
    /// <c>worldMin</c> that itself spends the mantissa (an origin of 2³⁶ + 12345.678, say); there the sum rounds to nearest by up to half a double ULP.</para>
    /// <para><b>Why that residual cannot break <c>CA-01</c> or <c>SQ-01</c>.</b> Half a double ULP at 2³⁶ is ~4 × 10⁻⁶, while the stored bound reaching this
    /// method was already rounded OUTWARD by up to one f32 ULP in the cell frame — ~6 × 10⁻⁵ across a 1 000-unit cell, an order of magnitude larger. So a
    /// converted bound stays outside the entity it contains even in the worst case, which is what the ray's face test and kNN's lower bound rely on.</para>
    /// <para><b>This is the only conversion out of the stored frame.</b> Three f32-returning variants stood here until #919 — an undirected <c>ToWorld</c>
    /// for display and a directed <c>ToWorldMin</c>/<c>ToWorldMax</c> pair for the ray and kNN paths. All three lost their last production caller when the
    /// query shapes went f64, and leaving them would have left three ways to quantise a world coordinate to ~64-unit steps at 10⁹ on the public surface of
    /// the type whose whole job is the opposite. The compounding hazard the old <c>ToWorld</c> warned about does not arise here either: these values are
    /// consumed and discarded, never re-stored.</para>
    /// </remarks>
    public static double ToWorldExact(float cellRelativeValue, double cellOrigin) => cellRelativeValue + cellOrigin;
}
