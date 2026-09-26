using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Where one archetype's spatial field sits in a cluster, and how the narrowphase reads it. Fixed per archetype: every query of it, and every member of a
/// batch, shares one.
/// </summary>
internal readonly struct ClusterFieldLayout
{
    /// <summary>The spatial field's storage tier, which picks the narrowphase's bounds reader.</summary>
    public readonly SpatialFieldType FieldType;

    /// <summary>Bytes from a cluster's base to slot 0's spatial field.</summary>
    public readonly int FieldsOffset;

    /// <summary>Bytes from one slot's spatial field to the next: the whole component's size, since the column is the component's SoA array.</summary>
    public readonly int Stride;

    /// <summary>Bytes from a cluster's base to its entity-id column.</summary>
    public readonly int IdsOffset;

    /// <summary>
    /// Whole 16-entity blocks per cluster when <see cref="NarrowphaseAabb2F"/>'s block kernel applies, else 0 — another tier, a component holding more than
    /// its AABB2F, no kernel on this machine, or <see cref="SpatialQueryTuning.SimdNarrowphase"/> off when the layout was taken.
    /// </summary>
    public readonly int Aabb2FBlocks;

    /// <summary>Bytes from a cluster's base to slot 0's <c>[RealmKey]</c> (same stride), or <c>-1</c>: the narrowphase's realm filter runs only when
    /// set.</summary>
    public readonly int RealmKeyColumn;

    /// <summary>Bytes from one entity's <c>[RealmKey]</c> to the next — its own component's size, which need not be <see cref="Stride"/>.</summary>
    public readonly int RealmKeyStride;

    internal ClusterFieldLayout(ArchetypeClusterState state)
    {
        RealmKeyColumn = state.RealmKeyColumn;
        RealmKeyStride = state.RealmKeyStride;
        // ref readonly, not a copy: ClusterSpatialSlot is ~104 bytes, and copying it to read four fields was a memcpy on every query (#916 O3).
        ref readonly var ss = ref state.SpatialSlot;
        FieldType = ss.FieldInfo.FieldType;
        FieldsOffset = state.Layout.ComponentOffset(ss.Slot) + ss.FieldOffset;
        Stride = state.Layout.ComponentSize(ss.Slot);
        IdsOffset = state.Layout.EntityIdsOffset;
        Aabb2FBlocks = SpatialQueryTuning.SimdNarrowphase && NarrowphaseAabb2F.Best != NarrowphaseAabb2F.Kernel.None
                       && FieldType == SpatialFieldType.AABB2F && Stride == NarrowphaseAabb2F.Stride
            ? state.Layout.ClusterSize / NarrowphaseAabb2F.BlockSize
            : 0;
    }

    /// <summary>A layout stated directly, for a test that lays a column out in memory of its own.</summary>
    internal ClusterFieldLayout(SpatialFieldType fieldType, int fieldsOffset, int stride, int idsOffset, int aabb2FBlocks)
    {
        FieldType = fieldType;
        FieldsOffset = fieldsOffset;
        Stride = stride;
        IdsOffset = idsOffset;
        Aabb2FBlocks = aabb2FBlocks;
        RealmKeyColumn = -1;
        RealmKeyStride = 0;
    }
}

/// <summary>
/// One query's shape in WORLD coordinates: its box, and for a radius query the sphere the box encloses.
/// </summary>
/// <remarks>
/// <para><b>f64, not f32</b>, because this is a WORLD coordinate and the world frame is f64 (#914, SQ-06). The narrowphase compares against it directly, in
/// double, matching the doubles the tier readers widen entity bounds to — widening an f32 bound is exact. The cell walk narrows it into each cell's frame
/// once per cell (<c>SetCellQueryFrame</c>), where the broadphase's f32 SoA scan meets it.</para>
/// <para>A 2D query carries ±Infinity on Z, which trivially passes the Z overlap test against 2D cluster storage.</para>
/// <para><b>The radius filter</b> applies when <see cref="RadiusSq"/> is positive: an entity passes only if the closest point of its box to the centre is
/// within the radius. The box must then be the sphere's enclosing box, so the cell range and the broadphase cover every candidate.</para>
/// </remarks>
internal readonly struct QueryGeometry
{
    public readonly double MinX;
    public readonly double MinY;
    public readonly double MinZ;
    public readonly double MaxX;
    public readonly double MaxY;
    public readonly double MaxZ;
    public readonly double RadiusSq;
    public readonly double CenterX;
    public readonly double CenterY;
    public readonly double CenterZ;

    internal QueryGeometry(double minX, double minY, double minZ, double maxX, double maxY, double maxZ, double radiusSq, double centerX, double centerY,
        double centerZ)
    {
        MinX = minX;
        MinY = minY;
        MinZ = minZ;
        MaxX = maxX;
        MaxY = maxY;
        MaxZ = maxZ;
        RadiusSq = radiusSq;
        CenterX = centerX;
        CenterY = centerY;
        CenterZ = centerZ;
    }
}
