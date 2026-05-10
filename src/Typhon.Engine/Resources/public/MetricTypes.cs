using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Tracks byte allocations owned by a resource node.
/// </summary>
/// <param name="AllocatedBytes">Current live allocation in bytes.</param>
/// <param name="PeakBytes">Maximum allocation ever observed (high-water mark, resettable).</param>
[PublicAPI]
public readonly record struct MemoryMetrics(long AllocatedBytes, long PeakBytes);

/// <summary>
/// Tracks utilization of a bounded slot-based structure.
/// </summary>
/// <param name="Current">Slots/entries currently used.</param>
/// <param name="Maximum">Total slots available (capacity).</param>
[PublicAPI]
public readonly record struct CapacityMetrics(long Current, long Maximum)
{
    /// <summary>
    /// Utilization ratio (0.0–1.0), computed as Current / Maximum.
    /// Returns 0.0 if Maximum is 0.
    /// </summary>
    public double Utilization => Maximum > 0 ? (double)Current / Maximum : 0.0;
}

/// <summary>
/// Tracks read/write operations to persistent storage.
/// </summary>
/// <param name="ReadOps">Number of read operations (counter).</param>
/// <param name="WriteOps">Number of write operations (counter).</param>
/// <param name="ReadBytes">Total bytes read (counter).</param>
/// <param name="WriteBytes">Total bytes written (counter).</param>
[PublicAPI]
public readonly record struct DiskIOMetrics(
    long ReadOps,
    long WriteOps,
    long ReadBytes,
    long WriteBytes);

/// <summary>
/// A named throughput counter tracking monotonically increasing operations.
/// </summary>
/// <param name="Name">Counter name (e.g., "CacheHits", "Commits").</param>
/// <param name="Count">Total operations since startup.</param>
/// <remarks>
/// Rates (ops/sec) are derived by consumers by differencing two snapshots.
/// </remarks>
[PublicAPI]
public readonly record struct ThroughputMetric(string Name, long Count);

/// <summary>
/// A named duration metric tracking time cost of discrete operations.
/// </summary>
/// <param name="Name">Operation name (e.g., "Checkpoint", "Flush").</param>
/// <param name="LastUs">Duration of the most recent operation in microseconds.</param>
/// <param name="AvgUs">Simple average (sum/count) in microseconds.</param>
/// <param name="MaxUs">Longest operation observed (high-water mark, resettable).</param>
[PublicAPI]
public readonly record struct DurationMetric(string Name, long LastUs, long AvgUs, long MaxUs);
