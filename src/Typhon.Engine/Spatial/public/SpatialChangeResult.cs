using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Zero-allocation result of an interest management delta query. Contains EntityIds of entities that changed within the observer's interest region since
/// their last consumption tick.
/// Spans point into observer-internal buffers and are valid only until the next <c>GetSpatialChanges</c> call for that observer.
/// </summary>
[PublicAPI]
public readonly ref struct SpatialChangeResult
{
    /// <summary>EntityIds of entities with spatial changes in the observer's interest region.</summary>
    public readonly ReadOnlySpan<long> ChangedEntities;

    /// <summary>True if all entities in the region were returned (observer was too stale for delta query).</summary>
    public readonly bool IsFullSync;

    /// <summary>The tick this result covers up to. Observer's LastConsumedTick is updated to this value.</summary>
    public readonly long Tick;

    internal SpatialChangeResult(ReadOnlySpan<long> changed, bool isFullSync, long tick)
    {
        ChangedEntities = changed;
        IsFullSync = isFullSync;
        Tick = tick;
    }

    internal static SpatialChangeResult Empty(long tick) => new(ReadOnlySpan<long>.Empty, false, tick);
}
