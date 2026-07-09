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

    /// <summary>Indicates whether this handle equals <paramref name="other"/> (same slot index and generation).</summary>
    /// <param name="other">Handle to compare against.</param>
    /// <returns><c>true</c> if both refer to the same observer registration.</returns>
    public bool Equals(SpatialObserverHandle other) => Index == other.Index && Generation == other.Generation;

    /// <summary>Indicates whether <paramref name="obj"/> is a <see cref="SpatialObserverHandle"/> equal to this one.</summary>
    /// <param name="obj">Object to compare against.</param>
    /// <returns><c>true</c> if <paramref name="obj"/> is an equal handle.</returns>
    public override bool Equals(object obj) => obj is SpatialObserverHandle other && Equals(other);

    /// <summary>Returns a hash code combining the slot index and generation.</summary>
    public override int GetHashCode() => HashCode.Combine(Index, Generation);

    /// <summary>Equality operator; see <see cref="Equals(SpatialObserverHandle)"/>.</summary>
    /// <param name="left">Left handle.</param>
    /// <param name="right">Right handle.</param>
    /// <returns><c>true</c> if the handles are equal.</returns>
    public static bool operator ==(SpatialObserverHandle left, SpatialObserverHandle right) => left.Equals(right);

    /// <summary>Inequality operator; see <see cref="Equals(SpatialObserverHandle)"/>.</summary>
    /// <param name="left">Left handle.</param>
    /// <param name="right">Right handle.</param>
    /// <returns><c>true</c> if the handles differ.</returns>
    public static bool operator !=(SpatialObserverHandle left, SpatialObserverHandle right) => !left.Equals(right);

    /// <summary>Returns a string of the form <c>Observer(index:generation)</c>.</summary>
    public override string ToString() => $"Observer({Index}:{Generation})";
}
