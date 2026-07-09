namespace Typhon.Engine;

/// <summary>
/// Identifies the spatial index variant: dimensionality (2D/3D) and precision (f32/f64).
/// Determines node layout, capacity, and coordinate handling.
/// </summary>
public enum SpatialVariant : byte
{
    /// <summary>2D R-Tree, single-precision (f32) coordinates.</summary>
    R2Df32 = 0,

    /// <summary>3D R-Tree, single-precision (f32) coordinates.</summary>
    R3Df32 = 1,

    /// <summary>2D R-Tree, double-precision (f64) coordinates.</summary>
    R2Df64 = 2,

    /// <summary>3D R-Tree, double-precision (f64) coordinates.</summary>
    R3Df64 = 3,
}
