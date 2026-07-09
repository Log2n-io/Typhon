using JetBrains.Annotations;

namespace Typhon.Schema.Definition;

// Float32 AABB types

/// <summary>Single-precision 2D axis-aligned bounding box, defined by its minimum and maximum corners.</summary>
[PublicAPI]
public struct AABB2F : ISpatialBox
{
    /// <summary>Minimum X coordinate.</summary>
    public float MinX;
    /// <summary>Minimum Y coordinate.</summary>
    public float MinY;
    /// <summary>Maximum X coordinate.</summary>
    public float MaxX;
    /// <summary>Maximum Y coordinate.</summary>
    public float MaxY;
}

/// <summary>Single-precision 3D axis-aligned bounding box, defined by its minimum and maximum corners.</summary>
[PublicAPI]
public struct AABB3F : ISpatialBox
{
    /// <summary>Minimum X coordinate.</summary>
    public float MinX;
    /// <summary>Minimum Y coordinate.</summary>
    public float MinY;
    /// <summary>Minimum Z coordinate.</summary>
    public float MinZ;
    /// <summary>Maximum X coordinate.</summary>
    public float MaxX;
    /// <summary>Maximum Y coordinate.</summary>
    public float MaxY;
    /// <summary>Maximum Z coordinate.</summary>
    public float MaxZ;
}

// Float32 BoundingSphere types

/// <summary>Single-precision 2D bounding sphere (circle), defined by its center and radius.</summary>
[PublicAPI]
public struct BSphere2F
{
    /// <summary>Center X coordinate.</summary>
    public float CenterX;
    /// <summary>Center Y coordinate.</summary>
    public float CenterY;
    /// <summary>Radius.</summary>
    public float Radius;
}

/// <summary>Single-precision 3D bounding sphere, defined by its center and radius.</summary>
[PublicAPI]
public struct BSphere3F
{
    /// <summary>Center X coordinate.</summary>
    public float CenterX;
    /// <summary>Center Y coordinate.</summary>
    public float CenterY;
    /// <summary>Center Z coordinate.</summary>
    public float CenterZ;
    /// <summary>Radius.</summary>
    public float Radius;
}

// Float64 AABB types

/// <summary>Double-precision 2D axis-aligned bounding box, defined by its minimum and maximum corners.</summary>
[PublicAPI]
public struct AABB2D : ISpatialBox
{
    /// <summary>Minimum X coordinate.</summary>
    public double MinX;
    /// <summary>Minimum Y coordinate.</summary>
    public double MinY;
    /// <summary>Maximum X coordinate.</summary>
    public double MaxX;
    /// <summary>Maximum Y coordinate.</summary>
    public double MaxY;
}

/// <summary>Double-precision 3D axis-aligned bounding box, defined by its minimum and maximum corners.</summary>
[PublicAPI]
public struct AABB3D : ISpatialBox
{
    /// <summary>Minimum X coordinate.</summary>
    public double MinX;
    /// <summary>Minimum Y coordinate.</summary>
    public double MinY;
    /// <summary>Minimum Z coordinate.</summary>
    public double MinZ;
    /// <summary>Maximum X coordinate.</summary>
    public double MaxX;
    /// <summary>Maximum Y coordinate.</summary>
    public double MaxY;
    /// <summary>Maximum Z coordinate.</summary>
    public double MaxZ;
}

// Float64 BoundingSphere types

/// <summary>Double-precision 2D bounding sphere (circle), defined by its center and radius.</summary>
[PublicAPI]
public struct BSphere2D
{
    /// <summary>Center X coordinate.</summary>
    public double CenterX;
    /// <summary>Center Y coordinate.</summary>
    public double CenterY;
    /// <summary>Radius.</summary>
    public double Radius;
}

/// <summary>Double-precision 3D bounding sphere, defined by its center and radius.</summary>
[PublicAPI]
public struct BSphere3D
{
    /// <summary>Center X coordinate.</summary>
    public double CenterX;
    /// <summary>Center Y coordinate.</summary>
    public double CenterY;
    /// <summary>Center Z coordinate.</summary>
    public double CenterZ;
    /// <summary>Radius.</summary>
    public double Radius;
}
