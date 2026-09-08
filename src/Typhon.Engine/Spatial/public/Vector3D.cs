using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// A three-component double-precision point, used for the spatial grid's <b>world frame</b> — its bounds, its cell size and the cell origins derived from
/// them (#914).
/// </summary>
/// <remarks>
/// <para><b>Why this type exists at all.</b> The BCL has no f64 <c>Vector3</c>: <see cref="Vector3"/> is f32 and <c>Vector&lt;double&gt;</c> is a
/// variable-width SIMD vector, not a 3-component point. The grid needs exactly three doubles with value semantics, so it declares them.</para>
/// <para><b>What it is NOT for.</b> It does not appear in stored bounds. <c>C15</c> keeps every stored bound — cluster AABBs, R-Tree node boxes, zone maps —
/// as <b>f32 and cell-relative</b>, and that is load-bearing rather than incidental: f64 node bounds would more than halve R-Tree fanout, attacking the
/// <c>O(log C)</c> the tree exists for. The floating-origin arrangement is what makes f32 storage deliver f64 world extent — a 24-bit mantissa spans a
/// 1 000-unit cell at ~6 × 10⁻⁵ resolution <i>independently of where that cell sits</i>, so world extent is bounded by the cell INDEX range (21 bits per axis
/// of block key ≈ 2 × 10⁹ units) rather than by float precision. This type is the f64 half of that arrangement: the origins the f32 bounds are measured
/// from.</para>
/// <para><b>The implicit conversion from <see cref="Vector3"/> is deliberate</b> — widening f32 to f64 is exact and lossless, so every existing f32 call site
/// keeps compiling and keeps meaning precisely what it meant. There is deliberately NO implicit conversion the other way: narrowing is lossy at the
/// magnitudes this type exists to support, and a silent one would reintroduce the f32 world frame this replaces.</para>
/// </remarks>
[PublicAPI]
public readonly struct Vector3D : IEquatable<Vector3D>
{
    /// <summary>The X component.</summary>
    public readonly double X;

    /// <summary>The Y component.</summary>
    public readonly double Y;

    /// <summary>The Z component.</summary>
    public readonly double Z;

    /// <summary>Construct from three components.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3D(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>Construct from a 2D corner plus an explicit Z — the shape a flat world builds with.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3D(Vector2 xy, double z)
        : this(xy.X, xy.Y, z)
    {
    }

    /// <summary>Widen an f32 point. Exact and lossless, which is why it is implicit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator Vector3D(Vector3 value) => new(value.X, value.Y, value.Z);

    /// <summary>
    /// Narrow to f32. <b>Explicit, and lossy by construction</b> — a world coordinate past ~1.7 × 10⁷ has more mantissa than f32 holds, which is the whole
    /// reason the world frame is f64. Use it only where the value is known to be small, such as a cell-relative offset.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static explicit operator Vector3(Vector3D value) => new((float)value.X, (float)value.Y, (float)value.Z);

    /// <inheritdoc/>
    public bool Equals(Vector3D other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    /// <inheritdoc/>
    public override bool Equals(object obj) => obj is Vector3D other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    /// <summary>Component-wise equality.</summary>
    public static bool operator ==(Vector3D left, Vector3D right) => left.Equals(right);

    /// <summary>Component-wise inequality.</summary>
    public static bool operator !=(Vector3D left, Vector3D right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => FormattableString.Invariant($"<{X}, {Y}, {Z}>");
}
