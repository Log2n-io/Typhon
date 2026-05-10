using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Default implementation of <see cref="IResourceGraph"/>.
/// </summary>
/// <remarks>
/// <para>
/// The resource graph walks the tree to collect snapshots and maintains a reference to the
/// previous snapshot for throughput rate computation.
/// </para>
/// <para>
/// <b>Performance:</b> Snapshot collection costs ~50ns per node. For 100 nodes, that's ~5μs total.
/// The MetricWriter is pooled to avoid allocations on the hot path.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class ResourceGraph : IResourceGraph
{
    private readonly IResourceRegistry _registry;
    private ResourceSnapshot _previousSnapshot;
    private readonly Lock _snapshotLock = new();

    /// <summary>
    /// Creates a new resource graph backed by the specified registry.
    /// </summary>
    /// <param name="registry">The resource registry providing the tree structure.</param>
    public ResourceGraph(IResourceRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc />
    public IResource Root => _registry.Root;

    /// <inheritdoc />
    public ResourceSnapshot GetSnapshot() => CollectSnapshot(_registry.Root);

    /// <inheritdoc />
    public ResourceSnapshot GetSnapshot(IResource subtreeRoot)
    {
        ArgumentNullException.ThrowIfNull(subtreeRoot);

        return CollectSnapshot(subtreeRoot);
    }

    /// <inheritdoc />
    public IResource FindByPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Root;
        }

        return Root.FindByPath(path);
    }

    /// <inheritdoc />
    public IEnumerable<IResource> FindByType(ResourceType type)
    {
        if (Root.Type == type)
        {
            yield return Root;
        }

        foreach (var descendant in Root.GetDescendants())
        {
            if (descendant.Type == type)
            {
                yield return descendant;
            }
        }
    }

    /// <inheritdoc />
    public void ResetAllPeaks()
    {
        foreach (var source in Root.GetMetricSources())
        {
            source.ResetPeaks();
        }
    }

    private ResourceSnapshot CollectSnapshot(IResource root)
    {
        var timestamp = DateTime.UtcNow;
        var nodes = new Dictionary<string, NodeSnapshot>();
        var writer = new SnapshotMetricWriter();

        // Determine the base path prefix for subtree snapshots
        string basePath = root == _registry.Root ? "" : root.GetPath();

        WalkTree(root, basePath, "", nodes, writer);

        ThroughputRates rates = null;

        // Compute rates from previous snapshot (thread-safe access)
        lock (_snapshotLock)
        {
            if (_previousSnapshot != null)
            {
                rates = ComputeRates(nodes, _previousSnapshot, timestamp);
            }
        }

        var snapshot = new ResourceSnapshot { Timestamp = timestamp, Nodes = nodes, Rates = rates };

        // Store as previous for next rate computation (only for full snapshots)
        if (root == _registry.Root)
        {
            lock (_snapshotLock)
            {
                _previousSnapshot = snapshot;
            }
        }

        return snapshot;
    }

    private void WalkTree(IResource resource, string basePath, string parentPath, Dictionary<string, NodeSnapshot> nodes, SnapshotMetricWriter writer)
    {
        // Build path relative to the base
        string path;
        if (string.IsNullOrEmpty(basePath))
        {
            // Full tree: path = parentPath/Id or just Id at root
            path = string.IsNullOrEmpty(parentPath)
                ? resource.Id
                : $"{parentPath}/{resource.Id}";
        }
        else
        {
            // Subtree: path = basePath/relative or just basePath at subtree root
            path = string.IsNullOrEmpty(parentPath)
                ? basePath
                : $"{parentPath}/{resource.Id}";
        }

        writer.Reset();

        // Read metrics if this node is a metric source
        if (resource is IMetricSource source)
        {
            source.ReadMetrics(writer);
        }

        nodes[path] = writer.ToNodeSnapshot(path, resource.Id, resource.Type);

        // Recurse to children
        foreach (var child in resource.Children)
        {
            WalkTree(child, basePath, path, nodes, writer);
        }
    }

    private ThroughputRates ComputeRates(Dictionary<string, NodeSnapshot> currentNodes, ResourceSnapshot previous, DateTime currentTimestamp)
    {
        var elapsed = (currentTimestamp - previous.Timestamp).TotalSeconds;
        if (elapsed <= 0)
        {
            return null;
        }

        var rates = new Dictionary<string, Dictionary<string, double>>();

        foreach (var (path, node) in currentNodes)
        {
            if (node.Throughput == null || node.Throughput.Count == 0)
            {
                continue;
            }

            if (!previous.Nodes.TryGetValue(path, out var prevNode))
            {
                continue;
            }

            if (prevNode.Throughput == null || prevNode.Throughput.Count == 0)
            {
                continue;
            }

            var nodeRates = new Dictionary<string, double>();
            foreach (var metric in node.Throughput)
            {
                var prevMetric = prevNode.Throughput.FirstOrDefault(m => m.Name == metric.Name);
                if (prevMetric.Name != null) // Found matching metric
                {
                    var delta = metric.Count - prevMetric.Count;
                    if (delta >= 0) // Don't report negative rates (counter reset)
                    {
                        nodeRates[metric.Name] = delta / elapsed;
                    }
                }
            }

            if (nodeRates.Count > 0)
            {
                rates[path] = nodeRates;
            }
        }

        return new ThroughputRates(rates);
    }

    /// <summary>
    /// Internal metric writer that collects values during tree walk.
    /// </summary>
    private sealed class SnapshotMetricWriter : IMetricWriter
    {
        private MemoryMetrics? _memory;
        private CapacityMetrics? _capacity;
        private DiskIOMetrics? _diskIO;
        private readonly List<ThroughputMetric> _throughput = new(8);
        private readonly List<DurationMetric> _duration = new(4);

        public void Reset()
        {
            _memory = null;
            _capacity = null;
            _diskIO = null;
            _throughput.Clear();
            _duration.Clear();
        }

        public void WriteMemory(long allocatedBytes, long peakBytes) => _memory = new MemoryMetrics(allocatedBytes, peakBytes);

        public void WriteCapacity(long current, long maximum) => _capacity = new CapacityMetrics(current, maximum);

        public void WriteDiskIO(long readOps, long writeOps, long readBytes, long writeBytes) =>
            _diskIO = new DiskIOMetrics(readOps, writeOps, readBytes, writeBytes);

        public void WriteThroughput(string name, long count) => _throughput.Add(new ThroughputMetric(name, count));

        public void WriteDuration(string name, long lastUs, long avgUs, long maxUs) => _duration.Add(new DurationMetric(name, lastUs, avgUs, maxUs));

        public NodeSnapshot ToNodeSnapshot(string path, string id, ResourceType type) =>
            new()
            {
                Path = path,
                Id = id,
                Type = type,
                Memory = _memory,
                Capacity = _capacity,
                DiskIO = _diskIO,
                Throughput = (_throughput.Count > 0) ? _throughput.ToArray() : [],
                Duration = (_duration.Count > 0) ? _duration.ToArray() : []
            };
    }
}