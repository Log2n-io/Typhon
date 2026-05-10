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
    AABB2F = 0,
    AABB3F = 1,
    BSphere2F = 2,
    BSphere3F = 3,
    AABB2D = 4,
    AABB3D = 5,
    BSphere2D = 6,
    BSphere3D = 7,
}

/// <summary>
/// Describes the spatial index field on a component: its layout within the component data, the type of spatial bounds, and the index parameters (margin, cell size).
/// Stored in SpatialIndexState and set at component registration time.
/// </summary>
[PublicAPI]
public readonly struct SpatialFieldInfo
{
    public readonly int FieldOffset;
    public readonly int FieldSize;
    public readonly SpatialFieldType FieldType;
    public readonly float Margin;
    public readonly float CellSize;
    public readonly float InverseCellSize;
    public readonly SpatialMode Mode;

    /// <summary>
    /// Archetype-level category bitmask from <see cref="SpatialIndexAttribute.Category"/>. Used by the per-cell cluster spatial broadphase to skip clusters
    /// whose category does not intersect the query's category mask. Defaults to <see cref="uint.MaxValue"/> so archetypes without an explicit category
    /// remain queryable with any mask. Issue #230 Phase 3.
    /// </summary>
    public readonly uint Category;

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
