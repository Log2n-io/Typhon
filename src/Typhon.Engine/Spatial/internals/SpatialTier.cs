using System;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Effective cluster-storage tier: the combination of dimensionality and precision that defines the compatibility class for cluster spatial queries
/// (issue #230 Phase 2.5). Four tiers cover all 4 dim × precision combinations. Entity-level BSphere declarations collapse to the same tier as their
/// AABB equivalent because cluster storage is always rectangular — sphere entity fields are converted to their enclosing AABB at narrowphase time via
/// <see cref="SpatialMaintainer.ReadAndValidateBoundsFromPtr"/>, which is an internal detail the tier check does not need to model.
/// </summary>
internal enum SpatialTier : byte
{
    /// <summary>2D single-precision float. Storage for <see cref="SpatialFieldType.AABB2F"/> and <see cref="SpatialFieldType.BSphere2F"/>.</summary>
    Tier2F = 0,

    /// <summary>3D single-precision float. Storage for <see cref="SpatialFieldType.AABB3F"/> and <see cref="SpatialFieldType.BSphere3F"/>.</summary>
    Tier3F = 1,

    /// <summary>2D double-precision float. Storage for <see cref="SpatialFieldType.AABB2D"/> and <see cref="SpatialFieldType.BSphere2D"/>.</summary>
    Tier2D = 2,

    /// <summary>3D double-precision float. Storage for <see cref="SpatialFieldType.AABB3D"/> and <see cref="SpatialFieldType.BSphere3D"/>.</summary>
    Tier3D = 3,
}

/// <summary>
/// Helpers that map from <see cref="SpatialFieldType"/> (archetype-declared spatial field) and from <see cref="ISpatialBox"/> generic type parameters to the
/// common <see cref="SpatialTier"/> enum. Used by <see cref="ClusterSpatialQuery{TArch}"/>'s <c>AABB&lt;TBox&gt;</c> to enforce strict tier matching between a
/// query's box type and an archetype's cluster storage (issue #230 Phase 2.5).
/// </summary>
internal static class SpatialTierExtensions
{
    /// <summary>
    /// Collapse a <see cref="SpatialFieldType"/> to its corresponding rectangular storage tier. AABB and BSphere variants at the same dimensionality and
    /// precision map to the same tier because the per-cell cluster path always stores AABB-shaped cluster bounds regardless of whether the entity field was
    /// declared as a box or a sphere.
    /// </summary>
    public static SpatialTier ToTier(this SpatialFieldType fieldType) => fieldType switch
    {
        SpatialFieldType.AABB2F or SpatialFieldType.BSphere2F => SpatialTier.Tier2F,
        SpatialFieldType.AABB3F or SpatialFieldType.BSphere3F => SpatialTier.Tier3F,
        SpatialFieldType.AABB2D or SpatialFieldType.BSphere2D => SpatialTier.Tier2D,
        SpatialFieldType.AABB3D or SpatialFieldType.BSphere3D => SpatialTier.Tier3D,
        _ => throw new ArgumentOutOfRangeException(nameof(fieldType), fieldType, "Unknown SpatialFieldType — add a case when a new variant is introduced."),
    };

    /// <summary>
    /// Map a generic query box type <typeparamref name="TBox"/> to its tier. The JIT folds the <c>typeof(TBox) == typeof(Concrete)</c> comparisons into a
    /// compile-time constant per specialization, so each monomorphized caller pays exactly zero runtime cost.
    /// </summary>
    /// <remarks>
    /// Throws at runtime if called with an <see cref="ISpatialBox"/> implementer that does not yet have a dispatch branch — this is a safety net for future
    /// box types that get added without updating every dispatch site. The compiler cannot catch the miss, but the exception message names the offending
    /// type so the root cause is obvious.
    /// </remarks>
    public static SpatialTier TBoxToTier<TBox>() where TBox : struct, ISpatialBox
    {
        if (typeof(TBox) == typeof(AABB2F))
        {
            return SpatialTier.Tier2F;
        }
        if (typeof(TBox) == typeof(AABB3F))
        {
            return SpatialTier.Tier3F;
        }
        if (typeof(TBox) == typeof(AABB2D))
        {
            return SpatialTier.Tier2D;
        }
        if (typeof(TBox) == typeof(AABB3D))
        {
            return SpatialTier.Tier3D;
        }
        throw new NotSupportedException(
            $"SpatialTierExtensions.TBoxToTier: unknown ISpatialBox implementer '{typeof(TBox).FullName}'. " +
            "Add a case here and to every ClusterSpatialQuery dispatch site (search for 'typeof(TBox) ==' in Typhon.Engine/Data/SpatialIndex).");
    }
}
