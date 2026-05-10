using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine;

/// <summary>
/// Immutable snapshot of all resource metrics at a point in time.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot provides a consistent-enough reading of all metric values across the resource tree.
/// </para>
/// <list type="bullet">
/// <item><description><b>Per-node atomic</b>: Each node's ReadMetrics() reads all fields together</description></item>
/// <item><description><b>Cross-node approximate</b>: Different nodes may be read microseconds apart</description></item>
/// <item><description><b>No global lock</b>: Tree traversal doesn't block other threads</description></item>
/// </list>
/// <para>
/// Query methods operate on the frozen data, making them safe to call from any thread.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class ResourceSnapshot
{
    /// <summary>
    /// Default threshold for high utilization detection in cascade analysis.
    /// Components above this threshold are considered "under pressure".
    /// </summary>
    public const double DefaultHighUtilizationThreshold = 0.8;

    /// <summary>
    /// Known wait dependencies based on architectural knowledge.
    /// Key = component that waits, Value = components it may block on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These dependencies are based on how Typhon's subsystems interact architecturally,
    /// not runtime tracking. They enable <see cref="FindRootCause"/> to trace back from
    /// a symptomatic node to the underlying cause.
    /// </para>
    /// <para>
    /// Paths use the format "Subsystem/Component" without the "Root/" prefix.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string[]> WaitDependencies = new()
    {
        // Commits wait for WAL ring buffer space
        ["DataEngine/TransactionPool"] = ["Durability/WALRingBuffer"],

        // WAL ring drains to WAL segments (disk I/O)
        ["Durability/WALRingBuffer"] = ["Durability/WALSegments"],

        // Page cache eviction waits for dirty page flush
        ["Storage/PageCache"] = ["Storage/ManagedPagedMMF"],

        // Shadow buffer waits for backup writer to consume
        ["Backup/ShadowBuffer"] = ["Backup/SnapshotStore"],
    };
    /// <summary>
    /// When this snapshot was taken.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// All nodes with their metric values, keyed by node path.
    /// </summary>
    /// <example>
    /// <code>
    /// var utilization = snapshot.Nodes["Storage/PageCache"].Capacity?.Utilization;
    /// </code>
    /// </example>
    public IReadOnlyDictionary<string, NodeSnapshot> Nodes { get; init; }

    /// <summary>
    /// Throughput rates (ops/sec) computed from the previous snapshot.
    /// Null for the first snapshot (no previous to compare against).
    /// </summary>
    /// <example>
    /// <code>
    /// var hitRate = snapshot.Rates?["Storage/PageCache"]["CacheHits"];
    /// </code>
    /// </example>
    public ThroughputRates Rates { get; init; }

    /// <summary>
    /// Sum <see cref="MemoryMetrics.AllocatedBytes"/> across all descendants of the given node.
    /// </summary>
    /// <param name="nodePath">Path to the subtree root (e.g., "DataEngine").</param>
    /// <returns>Total bytes allocated by all nodes under the path.</returns>
    /// <remarks>
    /// <para>
    /// Useful for memory attribution: "How much does each subsystem use?"
    /// </para>
    /// <example>
    /// <code>
    /// var dataEngineMemory = snapshot.GetSubtreeMemory("DataEngine");
    /// var storageMemory = snapshot.GetSubtreeMemory("Storage");
    /// </code>
    /// </example>
    /// </remarks>
    public long GetSubtreeMemory(string nodePath) =>
        Nodes.Values
            .Where(n => n.Path == nodePath || n.Path.StartsWith(nodePath + "/"))
            .Where(n => n.Memory.HasValue)
            .Sum(n => n.Memory.Value.AllocatedBytes);

    /// <summary>
    /// Find the node with highest <see cref="CapacityMetrics.Utilization"/> in the tree.
    /// </summary>
    /// <returns>The most utilized node, or null if no nodes have Capacity metrics.</returns>
    /// <remarks>
    /// <para>
    /// Useful for finding the bottleneck: "Which resource is about to run out?"
    /// </para>
    /// </remarks>
    public NodeSnapshot FindMostUtilized() =>
        Nodes.Values
            .Where(n => n.Capacity.HasValue)
            .OrderByDescending(n => n.Capacity.Value.Utilization)
            .FirstOrDefault();

    /// <summary>
    /// Find the node with highest <see cref="CapacityMetrics.Utilization"/> above a threshold.
    /// </summary>
    /// <param name="threshold">Minimum utilization (0.0 to 1.0) to include in results.</param>
    /// <returns>Nodes above the threshold, sorted by utilization descending.</returns>
    public IEnumerable<NodeSnapshot> FindMostUtilized(double threshold) =>
        Nodes.Values
            .Where(n => n.Capacity.HasValue)
            .Where(n => n.Capacity.Value.Utilization >= threshold)
            .OrderByDescending(n => n.Capacity.Value.Utilization);

    /// <summary>
    /// Get a specific node by path.
    /// </summary>
    /// <param name="nodePath">Path to the node.</param>
    /// <returns>The node snapshot, or null if not found.</returns>
    public NodeSnapshot GetNode(string nodePath) => Nodes.TryGetValue(nodePath, out var node) ? node : null;

    /// <summary>
    /// Find all nodes of a specific type.
    /// </summary>
    /// <param name="type">The resource type to find.</param>
    /// <returns>All nodes of the specified type.</returns>
    public IEnumerable<NodeSnapshot> FindByType(ResourceType type) => Nodes.Values.Where(n => n.Type == type);

    /// <summary>
    /// Get all nodes under a subtree path.
    /// </summary>
    /// <param name="nodePath">Path to the subtree root.</param>
    /// <returns>All nodes under the path, including the root itself.</returns>
    public IEnumerable<NodeSnapshot> GetSubtree(string nodePath) =>
        Nodes.Values
            .Where(n => n.Path == nodePath || n.Path.StartsWith(nodePath + "/"));

    /// <summary>
    /// Traces back from a symptomatic node to find the root cause of resource pressure.
    /// </summary>
    /// <param name="symptomPath">
    /// Path to the node showing symptoms (e.g., "DataEngine/TransactionPool" or "Root/DataEngine/TransactionPool").
    /// </param>
    /// <param name="highUtilizationThreshold">
    /// Utilization threshold (0.0-1.0) above which a node is considered "under pressure".
    /// Defaults to <see cref="DefaultHighUtilizationThreshold"/> (0.8).
    /// </param>
    /// <returns>
    /// The node at the end of the high-utilization chain (the root cause),
    /// or the symptom node itself if no dependencies are found or the node doesn't exist.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method uses <b>hardcoded wait dependencies</b> based on architectural knowledge
    /// to follow the causal chain. It is a heuristic, not runtime tracking.
    /// </para>
    /// <para>
    /// <b>Algorithm:</b>
    /// </para>
    /// <list type="number">
    /// <item><description>Start at the symptom node</description></item>
    /// <item><description>If utilization > threshold and has known dependencies, follow to most utilized dependency</description></item>
    /// <item><description>Repeat until no further high-utilization dependencies found</description></item>
    /// <item><description>Return the final node (the root cause)</description></item>
    /// </list>
    /// <example>
    /// <code>
    /// // Symptom: TransactionPool shows high utilization (0.95)
    /// var rootCause = snapshot.FindRootCause("DataEngine/TransactionPool");
    /// // Returns: WALRingBuffer (if it's the end of the high-utilization chain)
    /// </code>
    /// </example>
    /// </remarks>
    public NodeSnapshot FindRootCause(string symptomPath, double highUtilizationThreshold = DefaultHighUtilizationThreshold)
    {
        ArgumentNullException.ThrowIfNull(symptomPath);

        // Normalize path: remove "Root/" prefix if present for dependency lookup
        var normalizedPath = symptomPath.StartsWith("Root/") ? symptomPath[5..] : symptomPath;
        var fullPath = symptomPath.StartsWith("Root/") ? symptomPath : "Root/" + symptomPath;

        var visited = new HashSet<string>();
        var currentNormalized = normalizedPath;
        var currentFull = fullPath;

        while (currentNormalized != null && !visited.Contains(currentNormalized))
        {
            visited.Add(currentNormalized);

            if (!Nodes.TryGetValue(currentFull, out var node))
            {
                break;
            }

            // If this node is highly utilized, check what it waits on
            if (node.Capacity.HasValue && node.Capacity.Value.Utilization > highUtilizationThreshold)
            {
                if (WaitDependencies.TryGetValue(currentNormalized, out var dependencies))
                {
                    // Find the most utilized dependency that's also under pressure
                    string nextCauseNormalized = null;
                    double maxUtilization = 0;

                    foreach (var dep in dependencies)
                    {
                        var depFullPath = "Root/" + dep;
                        if (Nodes.TryGetValue(depFullPath, out var depNode) &&
                            depNode.Capacity.HasValue &&
                            depNode.Capacity.Value.Utilization > highUtilizationThreshold &&
                            depNode.Capacity.Value.Utilization > maxUtilization)
                        {
                            maxUtilization = depNode.Capacity.Value.Utilization;
                            nextCauseNormalized = dep;
                        }
                    }

                    if (nextCauseNormalized != null)
                    {
                        currentNormalized = nextCauseNormalized;
                        currentFull = "Root/" + nextCauseNormalized;
                        continue;
                    }
                }
            }

            // No further dependencies — this is the root cause
            return node;
        }

        // Fallback: return the symptom node itself (or null if not found)
        return Nodes.GetValueOrDefault(fullPath);
    }
}
