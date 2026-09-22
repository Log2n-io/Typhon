using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// A cluster's tight bounds in <b>world</b> coordinates, f64 — what <see cref="ClusterRef{TArch}.SpatialBounds"/> hands back (#914).
/// </summary>
/// <remarks>
/// <para><b>Why this is not a <see cref="ClusterSpatialAabb"/>.</b> That type is the STORED form, and its own documentation is emphatic about what that
/// means: six f32 components measured from the cell's origin, per <c>C15</c>. Returning world coordinates in it worked by writing world values into
/// cell-relative fields — the same struct meaning two different things depending on which side of a property you were on, with nothing in the type to say
/// which you were holding. A caller that passed the result back into anything expecting stored bounds would have been wrong by the whole distance to the
/// cell origin, silently.</para>
/// <para><b>Why f64.</b> The world frame is f64 since #914 and this is a world coordinate. At 10⁹ an f32 resolves to ~64-unit steps — coarser than most
/// clusters are wide — so an f32 world box would have reported every cluster in a distant region as the same box. The stored bounds stay f32 and stay
/// cell-relative; that is the floating-origin bargain, and this type is where the two frames meet.</para>
/// <para>For a 2D archetype <see cref="MinZ"/>/<see cref="MaxZ"/> carry the ±∞ empty sentinel through unchanged (∞ + a finite origin is ∞), exactly as the
/// stored form leaves them — read X and Y only.</para>
/// </remarks>
[PublicAPI]
public readonly struct ClusterWorldAabb
{
    /// <summary>Minimum X, in world units.</summary>
    public readonly double MinX;

    /// <summary>Minimum Y, in world units.</summary>
    public readonly double MinY;

    /// <summary>Minimum Z, in world units. The <c>+∞</c> empty sentinel for a 2D archetype.</summary>
    public readonly double MinZ;

    /// <summary>Maximum X, in world units.</summary>
    public readonly double MaxX;

    /// <summary>Maximum Y, in world units.</summary>
    public readonly double MaxY;

    /// <summary>Maximum Z, in world units. The <c>-∞</c> empty sentinel for a 2D archetype.</summary>
    public readonly double MaxZ;

    /// <summary>OR of every entity category mask in the cluster, carried through from the stored bounds.</summary>
    public readonly uint CategoryMask;

    /// <summary>Construct from six world-space bounds and a category mask.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ClusterWorldAabb(double minX, double minY, double minZ, double maxX, double maxY, double maxZ, uint categoryMask)
    {
        MinX = minX;
        MinY = minY;
        MinZ = minZ;
        MaxX = maxX;
        MaxY = maxY;
        MaxZ = maxZ;
        CategoryMask = categoryMask;
    }

    /// <summary>The empty sentinel — min at <c>+∞</c>, max at <c>-∞</c> on every axis, the seed a union starts from.</summary>
    public static ClusterWorldAabb Empty => new(
        double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
        double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, 0u);

    /// <summary>True when no entity has been unioned in — <see cref="MinX"/> is still the <c>+∞</c> seed.</summary>
    public bool IsEmpty => double.IsPositiveInfinity(MinX);

    /// <summary>Convert a stored <c>C15</c> cell-relative box to world space, given its cell's origin.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ClusterWorldAabb FromCellRelative(in ClusterSpatialAabb box, double originX, double originY, double originZ) => new(
        ClusterSpatialAabb.ToWorldExact(box.MinX, originX),
        ClusterSpatialAabb.ToWorldExact(box.MinY, originY),
        ClusterSpatialAabb.ToWorldExact(box.MinZ, originZ),
        ClusterSpatialAabb.ToWorldExact(box.MaxX, originX),
        ClusterSpatialAabb.ToWorldExact(box.MaxY, originY),
        ClusterSpatialAabb.ToWorldExact(box.MaxZ, originZ),
        box.CategoryMask);
}
