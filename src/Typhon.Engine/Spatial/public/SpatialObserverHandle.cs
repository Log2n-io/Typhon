using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Opaque handle to a registered spatial observer. Validated via generation counter to detect use-after-destroy.
/// </summary>
[PublicAPI]
public readonly struct SpatialObserverHandle : IEquatable<SpatialObserverHandle>
{
    internal readonly int Index;
    internal readonly int Generation;

    internal SpatialObserverHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    public bool Equals(SpatialObserverHandle other) => Index == other.Index && Generation == other.Generation;
    public override bool Equals(object obj) => obj is SpatialObserverHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Index, Generation);
    public static bool operator ==(SpatialObserverHandle left, SpatialObserverHandle right) => left.Equals(right);
    public static bool operator !=(SpatialObserverHandle left, SpatialObserverHandle right) => !left.Equals(right);
    public override string ToString() => $"Observer({Index}:{Generation})";
}
