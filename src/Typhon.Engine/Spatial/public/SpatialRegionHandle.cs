using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Opaque handle to a registered trigger region. Validated via generation counter to detect use-after-destroy.
/// </summary>
[PublicAPI]
public readonly struct SpatialRegionHandle : IEquatable<SpatialRegionHandle>
{
    internal readonly int Index;
    internal readonly int Generation;

    internal SpatialRegionHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    /// <summary>Indicates whether this handle equals <paramref name="other"/> (same slot index and generation).</summary>
    /// <param name="other">Handle to compare against.</param>
    /// <returns><c>true</c> if both refer to the same region registration.</returns>
    public bool Equals(SpatialRegionHandle other) => Index == other.Index && Generation == other.Generation;

    /// <summary>Indicates whether <paramref name="obj"/> is a <see cref="SpatialRegionHandle"/> equal to this one.</summary>
    /// <param name="obj">Object to compare against.</param>
    /// <returns><c>true</c> if <paramref name="obj"/> is an equal handle.</returns>
    public override bool Equals(object obj) => obj is SpatialRegionHandle other && Equals(other);

    /// <summary>Returns a hash code combining the slot index and generation.</summary>
    public override int GetHashCode() => HashCode.Combine(Index, Generation);

    /// <summary>Equality operator; see <see cref="Equals(SpatialRegionHandle)"/>.</summary>
    /// <param name="left">Left handle.</param>
    /// <param name="right">Right handle.</param>
    /// <returns><c>true</c> if the handles are equal.</returns>
    public static bool operator ==(SpatialRegionHandle left, SpatialRegionHandle right) => left.Equals(right);

    /// <summary>Inequality operator; see <see cref="Equals(SpatialRegionHandle)"/>.</summary>
    /// <param name="left">Left handle.</param>
    /// <param name="right">Right handle.</param>
    /// <returns><c>true</c> if the handles differ.</returns>
    public static bool operator !=(SpatialRegionHandle left, SpatialRegionHandle right) => !left.Equals(right);

    /// <summary>Returns a string of the form <c>Region(index:generation)</c>.</summary>
    public override string ToString() => $"Region({Index}:{Generation})";
}

/// <summary>
/// Controls which R-Tree(s) a trigger region evaluates against.
/// </summary>
[PublicAPI]
public enum TargetTreeMode : byte
{
    /// <summary>Query only the dynamic tree (default). Best for regions tracking moving entities.</summary>
    DynamicOnly = 0,
    /// <summary>Query both static and dynamic trees. Static results are cached and only re-queried on tree mutation.</summary>
    Both = 1,
    /// <summary>Query only the static tree. Fully cached after first evaluation.</summary>
    StaticOnly = 2,
}
