using JetBrains.Annotations;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Snapshot of a single resource node's metrics at a point in time.
/// </summary>
/// <remarks>
/// <para>
/// Not all nodes declare all metric kinds. A pure grouping node has no metrics at all.
/// Check nullable properties before accessing values.
/// </para>
/// <example>
/// <code>
/// var storageNode = snapshot.Nodes["Storage"];
/// // storageNode.Memory is null (grouping only)
///
/// var pageCacheNode = snapshot.Nodes["Storage/PageCache"];
/// // pageCacheNode.Memory has value
/// // pageCacheNode.Capacity has value
/// </code>
/// </example>
/// </remarks>
[PublicAPI]
public sealed class NodeSnapshot
{
    /// <summary>
    /// Full path in the tree (e.g., "Storage/PageCache").
    /// </summary>
    public string Path { get; init; }

    /// <summary>
    /// Node identifier (e.g., "PageCache").
    /// </summary>
    public string Id { get; init; }

    /// <summary>
    /// Node type from <see cref="ResourceType"/> enum.
    /// </summary>
    public ResourceType Type { get; init; }

    /// <summary>
    /// Memory allocation metrics, if declared by this node.
    /// </summary>
    public MemoryMetrics? Memory { get; init; }

    /// <summary>
    /// Capacity utilization metrics, if declared by this node.
    /// </summary>
    public CapacityMetrics? Capacity { get; init; }

    /// <summary>
    /// Disk I/O metrics, if declared by this node.
    /// </summary>
    public DiskIOMetrics? DiskIO { get; init; }

    /// <summary>
    /// Named throughput counters, if declared by this node.
    /// Empty array if no throughput metrics.
    /// </summary>
    public IReadOnlyList<ThroughputMetric> Throughput { get; init; } = [];

    /// <summary>
    /// Named duration metrics, if declared by this node.
    /// Empty array if no duration metrics.
    /// </summary>
    public IReadOnlyList<DurationMetric> Duration { get; init; } = [];
}
