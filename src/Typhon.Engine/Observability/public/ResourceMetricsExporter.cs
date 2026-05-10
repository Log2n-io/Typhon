using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace Typhon.Engine;

/// <summary>
/// Exports Typhon resource metrics to OpenTelemetry via <see cref="System.Diagnostics.Metrics.Meter"/>.
/// </summary>
/// <remarks>
/// <para>
/// The exporter uses the "observable" pattern: OTel callbacks read from a cached snapshot rather than
/// pushing metrics. This design has zero overhead when no OTel consumer is listening.
/// </para>
/// <para>
/// <b>Usage pattern:</b>
/// </para>
/// <list type="number">
///   <item><description>Create exporter with <see cref="IResourceGraph"/> and options</description></item>
///   <item><description>Periodically call <see cref="UpdateSnapshot"/> (via <see cref="ResourceMetricsService"/>)</description></item>
///   <item><description>OTel exporters (Prometheus, OTLP) automatically read from callbacks</description></item>
/// </list>
/// <example>
/// <code>
/// var exporter = new ResourceMetricsExporter(resourceGraph, options);
///
/// // Periodic update
/// exporter.UpdateSnapshot();
///
/// // OTel callbacks automatically return latest values
/// </code>
/// </example>
/// </remarks>
[PublicAPI]
[ExcludeFromCodeCoverage]
public sealed class ResourceMetricsExporter : IDisposable
{
    /// <summary>
    /// The Meter name used for all Typhon resource metrics.
    /// </summary>
    public const string MeterName = "Typhon.Resources";

    /// <summary>
    /// The Meter version.
    /// </summary>
    public const string MeterVersion = "1.0.0";

    private readonly IResourceGraph _resourceGraph;
    private readonly ObservabilityBridgeOptions _options;
    private readonly Meter _meter;
    private readonly List<IDisposable> _instruments = [];

    private ResourceSnapshot _currentSnapshot;
    private readonly object _snapshotLock = new();

    /// <summary>
    /// Creates a new ResourceMetricsExporter.
    /// </summary>
    /// <param name="resourceGraph">The resource graph to export metrics from.</param>
    /// <param name="options">Configuration options.</param>
    public ResourceMetricsExporter(IResourceGraph resourceGraph, ObservabilityBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(resourceGraph);
        ArgumentNullException.ThrowIfNull(options);

        _resourceGraph = resourceGraph;
        _options = options;
        _meter = new Meter(MeterName, MeterVersion);

        // Take initial snapshot
        _currentSnapshot = _resourceGraph.GetSnapshot();

        // Register all observable instruments
        RegisterInstruments();
    }

    /// <summary>
    /// The OTel Meter used by this exporter.
    /// </summary>
    public Meter Meter => _meter;

    /// <summary>
    /// The most recent snapshot. Used by health checks and alert generators.
    /// </summary>
    public ResourceSnapshot CurrentSnapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return _currentSnapshot;
            }
        }
    }

    /// <summary>
    /// Update the cached snapshot from the resource graph.
    /// Called periodically by <see cref="ResourceMetricsService"/>.
    /// </summary>
    /// <returns>The new snapshot.</returns>
    public ResourceSnapshot UpdateSnapshot()
    {
        var snapshot = _resourceGraph.GetSnapshot();
        lock (_snapshotLock)
        {
            _currentSnapshot = snapshot;
        }
        return snapshot;
    }

    private void RegisterInstruments()
    {
        if (_options.ExportMemoryMetrics)
        {
            RegisterMemoryInstruments();
        }

        if (_options.ExportCapacityMetrics)
        {
            RegisterCapacityInstruments();
        }

        if (_options.ExportDiskIOMetrics)
        {
            RegisterDiskIOInstruments();
        }

        if (_options.ExportThroughputMetrics)
        {
            RegisterThroughputInstruments();
        }

        if (_options.ExportDurationMetrics)
        {
            RegisterDurationInstruments();
        }
    }

    private void RegisterMemoryInstruments()
    {
        // Memory.AllocatedBytes - Gauge (current allocation)
        _meter.CreateObservableGauge(
            BuildMetricName("memory.allocated_bytes"),
            EnumerateMemoryAllocatedBytes,
            "bytes",
            "Current memory allocation in bytes");

        // Memory.PeakBytes - Gauge (high-water mark)
        _meter.CreateObservableGauge(
            BuildMetricName("memory.peak_bytes"),
            EnumerateMemoryPeakBytes,
            "bytes",
            "Peak memory allocation (high-water mark) in bytes");
    }

    private void RegisterCapacityInstruments()
    {
        // Capacity.Current - Gauge
        _meter.CreateObservableGauge(
            BuildMetricName("capacity.current"),
            EnumerateCapacityCurrent,
            "slots",
            "Current slots/entries in use");

        // Capacity.Maximum - Gauge
        _meter.CreateObservableGauge(
            BuildMetricName("capacity.maximum"),
            EnumerateCapacityMaximum,
            "slots",
            "Maximum slots/entries available");

        // Capacity.Utilization - Gauge (0.0-1.0)
        _meter.CreateObservableGauge(
            BuildMetricName("capacity.utilization"),
            EnumerateCapacityUtilization,
            "ratio",
            "Capacity utilization ratio (0.0-1.0)");
    }

    private void RegisterDiskIOInstruments()
    {
        // DiskIO.ReadOps - Counter
        _meter.CreateObservableCounter(
            BuildMetricName("disk_io.read_ops"),
            EnumerateDiskIOReadOps,
            "ops",
            "Total disk read operations");

        // DiskIO.WriteOps - Counter
        _meter.CreateObservableCounter(
            BuildMetricName("disk_io.write_ops"),
            EnumerateDiskIOWriteOps,
            "ops",
            "Total disk write operations");

        // DiskIO.ReadBytes - Counter
        _meter.CreateObservableCounter(
            BuildMetricName("disk_io.read_bytes"),
            EnumerateDiskIOReadBytes,
            "bytes",
            "Total bytes read from disk");

        // DiskIO.WriteBytes - Counter
        _meter.CreateObservableCounter(
            BuildMetricName("disk_io.write_bytes"),
            EnumerateDiskIOWriteBytes,
            "bytes",
            "Total bytes written to disk");
    }

    private void RegisterThroughputInstruments() =>
        // Throughput metrics are dynamic (named counters per node)
        // We use a single observable that yields all throughput counters
        _meter.CreateObservableCounter(
            BuildMetricName("throughput.count"),
            EnumerateThroughputCounts,
            "ops",
            "Total operations (throughput counter)");

    private void RegisterDurationInstruments()
    {
        // Duration.LastUs - Gauge
        _meter.CreateObservableGauge(
            BuildMetricName("duration.last_us"),
            EnumerateDurationLastUs,
            "us",
            "Duration of most recent operation");

        // Duration.AvgUs - Gauge
        _meter.CreateObservableGauge(
            BuildMetricName("duration.avg_us"),
            EnumerateDurationAvgUs,
            "us",
            "Average operation duration");

        // Duration.MaxUs - Gauge (high-water mark)
        _meter.CreateObservableGauge(
            BuildMetricName("duration.max_us"),
            EnumerateDurationMaxUs,
            "us",
            "Maximum operation duration observed");
    }

    private string BuildMetricName(string suffix) => $"{_options.MetricNamePrefix}.{suffix}";

    // Memory metric enumerators
    private IEnumerable<Measurement<long>> EnumerateMemoryAllocatedBytes()
    {
        var snapshot = CurrentSnapshot;
        foreach (var node in snapshot.Nodes.Values)
        {
            if (node.Memory.HasValue)
            {
                yield return new Measurement<long>(
                    node.Memory.Value.AllocatedBytes,
                    new KeyValuePair<string, object>("resource_path", node.Path));
            }
        }
    }

    private IEnumerable<Measurement<long>> EnumerateMemoryPeakBytes()
    {
        var snapshot = CurrentSnapshot;
        foreach (var node in snapshot.Nodes.Values)
        {
            if (node.Memory.HasValue)
            {
                yield return new Measurement<long>(
                    node.Memory.Value.PeakBytes,
                    new KeyValuePair<string, object>("resource_path", node.Path));
            }
        }
    }

    // Capacity metric enumerators
    private IEnumerable<Measurement<long>> EnumerateCapacityCurrent()
    {
        var snapshot = CurrentSnapshot;
        foreach (var node in snapshot.Nodes.Values)
        {
            if (node.Capacity.HasValue)
            {
                yield return new Measurement<long>(
                    node.Capacity.Value.Current,
                    new KeyValuePair<string, object>("resource_path", node.Path));
            }
        }
    }

    private IEnumerable<Measurement<long>> EnumerateCapacityMaximum()
    {
        var snapshot = CurrentSnapshot;
        foreach (var node in snapshot.Nodes.Values)
        {
            if (node.Capacity.HasValue)
            {
                yield return new Measurement<long>(
                    node.Capacity.Value.Maximum,
                    new KeyValuePair<string, object>("resource_path", node.Path));
            }
        }
    }

    private IEnumerable<Measurement<double>> EnumerateCapacityUtilization()
    {
        var snapshot = CurrentSnapshot;
        foreach (var node in snapshot.Nodes.Values)
        {
            if (node.Capacity.HasValue)
            {
                yield return new Measurement<double>(
                    node.Capacity.Value.Utilization,
                    new KeyValuePair<string, object>("resource_path", node.Path));
            }
        }
    }

    // DiskIO metric enumerators
    private IEnumerable<Measurement<long>> EnumerateDiskIOReadOps()
    {
        var snapshot = CurrentSnapshot;
        foreach (var node in snapshot.Nodes.Values)
        {
            if (node.DiskIO.HasValue)
            {
                yield return new Measurement<long>(
                    node.DiskIO.Value.ReadOps,
                    new KeyValuePair<string, object>("resource_path", node.Path));
            }
        }
    }

    private IEnumerable<Measurement<long>> EnumerateDiskIOWriteOps()
    {
        var snapshot = CurrentSnapshot;
        foreach (var node in snapshot.Nodes.Values)
        {
            if (node.DiskIO.HasValue)
            {
                yield return new Measurement<long>(
                    node.DiskIO.Value.WriteOps,
                    new KeyValuePair<string, object>("resource_path", node.Path));
            }
        }
    }

    private IEnumerable<Measurement<long>> EnumerateDiskIOReadBytes()
    {
        var snapshot = CurrentSnapshot;
        foreach (var node in snapshot.Nodes.Values)
        {
            if (node.DiskIO.HasValue)
            {
                yield return new Measurement<long>(
                    node.DiskIO.Value.ReadBytes,
                    new KeyValuePair<string, object>("resource_path", node.Path));
            }
        }
    }

    private IEnumerable<Measurement<long>> EnumerateDiskIOWriteBytes()
    {
        var snapshot = CurrentSnapshot;
        foreach (var node in snapshot.Nodes.Values)
        {
            if (node.DiskIO.HasValue)
            {
                yield return new Measurement<long>(
                    node.DiskIO.Value.WriteBytes,
                    new KeyValuePair<string, object>("resource_path", node.Path));
            }
        }
    }

    // Throughput metric enumerators
    private IEnumerable<Measurement<long>> EnumerateThroughputCounts()
    {
        var snapshot = CurrentSnapshot;
        foreach (var node in snapshot.Nodes.Values)
        {
            foreach (var throughput in node.Throughput)
            {
                yield return new Measurement<long>(
                    throughput.Count,
                    new KeyValuePair<string, object>("resource_path", node.Path),
                    new KeyValuePair<string, object>("metric_name", throughput.Name));
            }
        }
    }

    // Duration metric enumerators
    private IEnumerable<Measurement<long>> EnumerateDurationLastUs()
    {
        var snapshot = CurrentSnapshot;
        foreach (var node in snapshot.Nodes.Values)
        {
            foreach (var duration in node.Duration)
            {
                yield return new Measurement<long>(
                    duration.LastUs,
                    new KeyValuePair<string, object>("resource_path", node.Path),
                    new KeyValuePair<string, object>("metric_name", duration.Name));
            }
        }
    }

    private IEnumerable<Measurement<long>> EnumerateDurationAvgUs()
    {
        var snapshot = CurrentSnapshot;
        foreach (var node in snapshot.Nodes.Values)
        {
            foreach (var duration in node.Duration)
            {
                yield return new Measurement<long>(
                    duration.AvgUs,
                    new KeyValuePair<string, object>("resource_path", node.Path),
                    new KeyValuePair<string, object>("metric_name", duration.Name));
            }
        }
    }

    private IEnumerable<Measurement<long>> EnumerateDurationMaxUs()
    {
        var snapshot = CurrentSnapshot;
        foreach (var node in snapshot.Nodes.Values)
        {
            foreach (var duration in node.Duration)
            {
                yield return new Measurement<long>(
                    duration.MaxUs,
                    new KeyValuePair<string, object>("resource_path", node.Path),
                    new KeyValuePair<string, object>("metric_name", duration.Name));
            }
        }
    }

    /// <summary>
    /// Disposes the meter and all instruments.
    /// </summary>
    public void Dispose()
    {
        _meter.Dispose();
        foreach (var instrument in _instruments)
        {
            instrument.Dispose();
        }
        _instruments.Clear();
    }
}
