using JetBrains.Annotations;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Entry point for the resource graph. Provides tree traversal and snapshot collection.
/// </summary>
/// <remarks>
/// <para>
/// The resource graph maintains the hierarchical structure of all managed resources and provides
/// a pull-based API for metric collection. Consumers take snapshots on demand — typically every
/// 1-5 seconds for monitoring, or ad-hoc for debugging.
/// </para>
/// <para>
/// <b>Snapshot semantics:</b>
/// </para>
/// <list type="bullet">
/// <item><description><b>Per-node atomic</b>: Each node's ReadMetrics() reads all fields together</description></item>
/// <item><description><b>Cross-node approximate</b>: Different nodes may be read microseconds apart</description></item>
/// <item><description><b>No global lock</b>: Tree traversal doesn't block other threads</description></item>
/// </list>
/// <para>
/// The graph internally tracks the previous snapshot for rate computation. The first snapshot
/// has <see cref="ResourceSnapshot.Rates"/> = null.
/// </para>
/// </remarks>
[PublicAPI]
public interface IResourceGraph
{
    /// <summary>
    /// The root of the resource tree.
    /// </summary>
    IResource Root { get; }

    /// <summary>
    /// Walk the entire tree, read all <see cref="IMetricSource"/> nodes, return immutable snapshot.
    /// Throughput rates are auto-computed from the previous snapshot.
    /// </summary>
    /// <returns>Snapshot containing all metric values at approximately this instant.</returns>
    /// <remarks>
    /// <para>
    /// Cost: ~50ns per node × number of nodes. Typically 50-100 nodes = 2.5-5 μs.
    /// </para>
    /// <para>
    /// The graph internally tracks the previous snapshot for rate computation.
    /// First snapshot has <see cref="ResourceSnapshot.Rates"/> = null.
    /// </para>
    /// </remarks>
    ResourceSnapshot GetSnapshot();

    /// <summary>
    /// Read metrics for a single subtree only.
    /// </summary>
    /// <param name="subtreeRoot">The root of the subtree to snapshot.</param>
    /// <returns>Snapshot containing only nodes under subtreeRoot.</returns>
    /// <remarks>
    /// <para>
    /// Use when you know which subsystem to inspect. Faster than full snapshot.
    /// </para>
    /// <para>
    /// Note: Rates are computed only for nodes in the subtree.
    /// </para>
    /// </remarks>
    ResourceSnapshot GetSnapshot(IResource subtreeRoot);

    /// <summary>
    /// Find a resource by path.
    /// </summary>
    /// <param name="path">Slash-separated path (e.g., "Storage/PageCache"). Does not include "Root" prefix.</param>
    /// <returns>The resource, or null if not found.</returns>
    IResource FindByPath(string path);

    /// <summary>
    /// Find all resources of a specific type.
    /// </summary>
    /// <param name="type">The resource type to find.</param>
    /// <returns>All resources of the specified type.</returns>
    IEnumerable<IResource> FindByType(ResourceType type);

    /// <summary>
    /// Reset all peak/high-water mark values across all <see cref="IMetricSource"/> nodes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks the tree and calls <see cref="IMetricSource.ResetPeaks"/> on each metric source.
    /// </para>
    /// <para>
    /// Use after alert acknowledgment, periodic reset, or on operator request.
    /// This is a separate operation from snapshot collection (snapshots are read-only).
    /// </para>
    /// </remarks>
    void ResetAllPeaks();
}
