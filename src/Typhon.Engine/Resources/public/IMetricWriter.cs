using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Writer interface for zero-allocation metric collection during snapshot operations.
/// </summary>
/// <remarks>
/// <para>
/// Implementations buffer values internally in pre-allocated structures. The resource graph
/// provides a writer instance during snapshot collection, and metric sources write their
/// current values to it.
/// </para>
/// <para>
/// The writer pattern avoids allocations by having sources write <em>to</em> a buffer rather
/// than <em>returning</em> metric objects. This keeps the snapshot path allocation-free.
/// </para>
/// <para>
/// For methods that can be called multiple times (WriteThroughput, WriteDuration), each call
/// appends to an internal list. For fixed methods (WriteMemory, WriteCapacity, etc.), the
/// last call wins if called multiple times.
/// </para>
/// </remarks>
[PublicAPI]
public interface IMetricWriter
{
    /// <summary>
    /// Reports memory allocation metrics for this node.
    /// </summary>
    /// <param name="allocatedBytes">Current live allocation in bytes.</param>
    /// <param name="peakBytes">Maximum allocation ever observed (high-water mark).</param>
    void WriteMemory(long allocatedBytes, long peakBytes);

    /// <summary>
    /// Reports capacity utilization metrics for this node.
    /// </summary>
    /// <param name="current">Slots/entries currently used.</param>
    /// <param name="maximum">Total slots available (capacity).</param>
    /// <remarks>
    /// Utilization (current / maximum) is computed by the snapshot builder,
    /// not by the writer. This keeps ReadMetrics fast (no division).
    /// </remarks>
    void WriteCapacity(long current, long maximum);

    /// <summary>
    /// Reports disk I/O metrics for this node.
    /// </summary>
    /// <param name="readOps">Number of read operations (counter).</param>
    /// <param name="writeOps">Number of write operations (counter).</param>
    /// <param name="readBytes">Total bytes read (counter).</param>
    /// <param name="writeBytes">Total bytes written (counter).</param>
    void WriteDiskIO(long readOps, long writeOps, long readBytes, long writeBytes);

    /// <summary>
    /// Reports a named throughput counter.
    /// </summary>
    /// <param name="name">Counter name (e.g., "CacheHits", "Commits"). Must be a static string.</param>
    /// <param name="count">Total operations since startup (monotonically increasing).</param>
    /// <remarks>
    /// Call multiple times for multiple counters (e.g., "Lookups", "Inserts").
    /// Use static strings only to maintain zero-allocation — avoid string interpolation.
    /// </remarks>
    void WriteThroughput(string name, long count);

    /// <summary>
    /// Reports a named duration metric.
    /// </summary>
    /// <param name="name">Operation name (e.g., "Checkpoint", "GroupCommit"). Must be a static string.</param>
    /// <param name="lastUs">Duration of the most recent operation in microseconds.</param>
    /// <param name="avgUs">Exponential moving average in microseconds.</param>
    /// <param name="maxUs">Longest operation observed (high-water mark).</param>
    /// <remarks>
    /// Call multiple times for multiple duration types.
    /// Use static strings only to maintain zero-allocation — avoid string interpolation.
    /// </remarks>
    void WriteDuration(string name, long lastUs, long avgUs, long maxUs);
}
