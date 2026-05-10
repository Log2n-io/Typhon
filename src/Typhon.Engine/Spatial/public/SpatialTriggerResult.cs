using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Zero-allocation result of a trigger region evaluation. Contains EntityIds of entities that entered, left, or stayed.
/// Spans point into system-internal buffers and are valid only until the next <c>EvaluateRegion</c> call.
/// </summary>
[PublicAPI]
public readonly ref struct SpatialTriggerResult
{
    /// <summary>EntityIds of entities that entered the region since the last evaluation.</summary>
    public readonly ReadOnlySpan<long> Entered;

    /// <summary>EntityIds of entities that left the region since the last evaluation.</summary>
    public readonly ReadOnlySpan<long> Left;

    /// <summary>Count of entities still inside the region (IDs not materialized for performance).</summary>
    public readonly int StayCount;

    /// <summary>True if the region was actually evaluated this tick (false when skipped due to frequency gating).</summary>
    public readonly bool WasEvaluated;

    internal SpatialTriggerResult(ReadOnlySpan<long> entered, ReadOnlySpan<long> left, int stayCount)
    {
        Entered = entered;
        Left = left;
        StayCount = stayCount;
        WasEvaluated = true;
    }

    internal static SpatialTriggerResult Skipped => default;
}
