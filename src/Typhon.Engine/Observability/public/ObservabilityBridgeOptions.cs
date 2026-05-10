using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Configuration options for the Observability Bridge that exports Resource System metrics to OpenTelemetry format.
/// </summary>
/// <remarks>
/// <para>
/// The Observability Bridge periodically captures <see cref="ResourceSnapshot"/> instances and exposes them
/// as OTel-compatible metrics via <see cref="System.Diagnostics.Metrics.Meter"/>.
/// </para>
/// <para>
/// Consumers can attach their preferred OTel exporters (Prometheus, OTLP, Console, etc.) to capture these metrics.
/// </para>
/// </remarks>
[PublicAPI]
public class ObservabilityBridgeOptions
{
    /// <summary>
    /// The configuration section name for observability bridge settings.
    /// </summary>
    public const string SectionName = "Typhon:ObservabilityBridge";

    /// <summary>
    /// How often to capture a new <see cref="ResourceSnapshot"/> from the <see cref="IResourceGraph"/>.
    /// Default: 5 seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A 5-second interval balances freshness against overhead. Snapshots are lightweight (~50ns per node),
    /// so shorter intervals are feasible for high-resolution monitoring.
    /// </para>
    /// </remarks>
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Prefix for all OTel metric names.
    /// Default: "typhon.resource".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Metric names follow the pattern: <c>{prefix}.{path}.{kind}.{sub}</c>
    /// </para>
    /// <example>
    /// <code>
    /// // With default prefix:
    /// // Storage/PageCache Memory.AllocatedBytes → typhon.resource.storage.page_cache.memory.allocated_bytes
    /// </code>
    /// </example>
    /// </remarks>
    public string MetricNamePrefix { get; set; } = "typhon.resource";

    /// <summary>
    /// Export <see cref="MemoryMetrics"/> (AllocatedBytes, PeakBytes) to OTel.
    /// Default: true.
    /// </summary>
    public bool ExportMemoryMetrics { get; set; } = true;

    /// <summary>
    /// Export <see cref="CapacityMetrics"/> (Current, Maximum, Utilization) to OTel.
    /// Default: true.
    /// </summary>
    public bool ExportCapacityMetrics { get; set; } = true;

    /// <summary>
    /// Export <see cref="DiskIOMetrics"/> (ReadOps, WriteOps, ReadBytes, WriteBytes) to OTel.
    /// Default: true.
    /// </summary>
    public bool ExportDiskIOMetrics { get; set; } = true;

    /// <summary>
    /// Export <see cref="ThroughputMetric"/> counters to OTel.
    /// Default: true.
    /// </summary>
    public bool ExportThroughputMetrics { get; set; } = true;

    /// <summary>
    /// Export <see cref="DurationMetric"/> values (LastUs, AvgUs, MaxUs) to OTel.
    /// Default: true.
    /// </summary>
    public bool ExportDurationMetrics { get; set; } = true;

    /// <summary>
    /// Health check thresholds per resource path.
    /// Key: normalized path (e.g., "Storage/PageCache"), Value: threshold configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Paths not in this dictionary use default thresholds:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Degraded: 80% utilization</description></item>
    ///   <item><description>Unhealthy: 95% utilization</description></item>
    /// </list>
    /// </remarks>
    public Dictionary<string, HealthThresholds> Thresholds { get; set; } = new();
}

/// <summary>
/// Thresholds for determining health status based on utilization.
/// </summary>
/// <param name="DegradedThreshold">Utilization (0.0–1.0) above which status is <see cref="HealthStatus.Degraded"/>.</param>
/// <param name="UnhealthyThreshold">Utilization (0.0–1.0) above which status is <see cref="HealthStatus.Unhealthy"/>.</param>
[PublicAPI]
public readonly record struct HealthThresholds(double DegradedThreshold, double UnhealthyThreshold)
{
    /// <summary>
    /// Default thresholds: 80% degraded, 95% unhealthy.
    /// </summary>
    public static HealthThresholds Default => new(0.80, 0.95);

    /// <summary>
    /// Stricter thresholds for critical resources: 60% degraded, 80% unhealthy.
    /// </summary>
    public static HealthThresholds Critical => new(0.60, 0.80);
}
