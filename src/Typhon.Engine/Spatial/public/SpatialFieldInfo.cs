using JetBrains.Annotations;
using System;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Compact discriminator for the spatial field type on a component.
/// Maps to the Schema.Definition FieldType values but is a lightweight internal representation.
/// </summary>
[PublicAPI]
public enum SpatialFieldType : byte
{
    /// <summary>2D axis-aligned bounding box, single-precision (f32).</summary>
    AABB2F = 0,

    /// <summary>3D axis-aligned bounding box, single-precision (f32).</summary>
    AABB3F = 1,

    /// <summary>2D bounding sphere (circle), single-precision (f32).</summary>
    BSphere2F = 2,

    /// <summary>3D bounding sphere, single-precision (f32).</summary>
    BSphere3F = 3,

    /// <summary>2D axis-aligned bounding box, double-precision (f64).</summary>
    AABB2D = 4,

    /// <summary>3D axis-aligned bounding box, double-precision (f64).</summary>
    AABB3D = 5,

    /// <summary>2D bounding sphere (circle), double-precision (f64).</summary>
    BSphere2D = 6,

    /// <summary>3D bounding sphere, double-precision (f64).</summary>
    BSphere3D = 7,
}

/// <summary>
/// Describes the spatial index field on a component: its layout within the component data, the type of spatial bounds, and the index parameters (margin, cell size).
/// Stored in SpatialIndexState and set at component registration time.
/// </summary>
[PublicAPI]
public readonly struct SpatialFieldInfo
{
    /// <summary>Byte offset of the spatial bounds field within the component's data.</summary>
    public readonly int FieldOffset;

    /// <summary>Size, in bytes, of the spatial bounds field.</summary>
    public readonly int FieldSize;

    /// <summary>Kind of spatial bounds stored in the field (dimensionality + precision).</summary>
    public readonly SpatialFieldType FieldType;

    /// <summary>Fat-AABB margin, in world units, added around an entity's tight bounds so small movements don't force an index update.</summary>
    public readonly float Margin;

    /// <summary>Grid cell size, in world units, used to bucket this field's entities.</summary>
    public readonly float CellSize;

    /// <summary>Precomputed <c>1 / </c><see cref="CellSize"/>, or <c>0</c> when <see cref="CellSize"/> is not positive.</summary>
    public readonly float InverseCellSize;

    /// <summary>Whether the field is indexed as <see cref="SpatialMode.Dynamic"/> (moving entities) or <see cref="SpatialMode.Static"/>.</summary>
    public readonly SpatialMode Mode;

    /// <summary>
    /// Archetype-level category bitmask from <see cref="SpatialIndexAttribute.Category"/>. Used by the per-cell cluster spatial broadphase to skip clusters
    /// whose category does not intersect the query's category mask. Defaults to <see cref="uint.MaxValue"/> so archetypes without an explicit category
    /// remain queryable with any mask. Issue #230 Phase 3.
    /// </summary>
    public readonly uint Category;

    /// <summary>Describe a component's spatial bounds field and its index parameters.</summary>
    /// <param name="fieldOffset">Byte offset of the bounds field within the component data.</param>
    /// <param name="fieldSize">Size, in bytes, of the bounds field.</param>
    /// <param name="fieldType">Kind of bounds stored (dimensionality + precision).</param>
    /// <param name="margin">Fat-AABB margin in world units added around tight bounds.</param>
    /// <param name="cellSize">Grid cell size in world units; <see cref="InverseCellSize"/> is derived from it.</param>
    /// <param name="mode">Index mode: <see cref="SpatialMode.Dynamic"/> (default) or <see cref="SpatialMode.Static"/>.</param>
    /// <param name="category">Archetype-level category bitmask; defaults to <see cref="uint.MaxValue"/> (matches any query mask).</param>
    public SpatialFieldInfo(int fieldOffset, int fieldSize, SpatialFieldType fieldType, float margin, float cellSize, SpatialMode mode = SpatialMode.Dynamic, uint category = uint.MaxValue)
    {
        FieldOffset = fieldOffset;
        FieldSize = fieldSize;
        FieldType = fieldType;
        Margin = margin;
        CellSize = cellSize;
        InverseCellSize = cellSize > 0 ? 1.0f / cellSize : 0;
        Mode = mode;
        Category = category;
    }

    /// <summary>
    /// Map this field type to the corresponding R-Tree variant (dimensionality + precision).
    /// </summary>
    public SpatialVariant ToVariant() => FieldType switch
    {
        SpatialFieldType.AABB2F or SpatialFieldType.BSphere2F => SpatialVariant.R2Df32,
        SpatialFieldType.AABB3F or SpatialFieldType.BSphere3F => SpatialVariant.R3Df32,
        SpatialFieldType.AABB2D or SpatialFieldType.BSphere2D => SpatialVariant.R2Df64,
        SpatialFieldType.AABB3D or SpatialFieldType.BSphere3D => SpatialVariant.R3Df64,
        _ => throw new ArgumentOutOfRangeException(nameof(FieldType), FieldType, "Unknown spatial field type")
    };

    /// <summary>
    /// Returns true if this field type stores a bounding sphere (requires AABB conversion at tree update time).
    /// </summary>
    public bool IsSphere => FieldType is SpatialFieldType.BSphere2F or SpatialFieldType.BSphere3F or SpatialFieldType.BSphere2D or SpatialFieldType.BSphere3D;
}
