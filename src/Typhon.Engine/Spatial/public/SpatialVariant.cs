namespace Typhon.Engine;

/// <summary>
/// Identifies the spatial index variant: dimensionality (2D/3D) and precision (f32/f64).
/// Determines node layout, capacity, and coordinate handling.
/// </summary>
public enum SpatialVariant : byte
{
    R2Df32 = 0,
    R3Df32 = 1,
    R2Df64 = 2,
    R3Df64 = 3,
}
