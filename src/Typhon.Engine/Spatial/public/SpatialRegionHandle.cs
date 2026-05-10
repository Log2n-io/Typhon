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

    public bool Equals(SpatialRegionHandle other) => Index == other.Index && Generation == other.Generation;
    public override bool Equals(object obj) => obj is SpatialRegionHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Index, Generation);
    public static bool operator ==(SpatialRegionHandle left, SpatialRegionHandle right) => left.Equals(right);
    public static bool operator !=(SpatialRegionHandle left, SpatialRegionHandle right) => !left.Equals(right);
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
