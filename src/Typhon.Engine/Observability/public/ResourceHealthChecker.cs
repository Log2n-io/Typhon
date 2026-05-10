using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine;

/// <summary>
/// Health checker that evaluates resource utilization against configured thresholds.
/// </summary>
/// <remarks>
/// <para>
/// The health checker uses a "worst-of-all" pattern: the overall status is determined by the
/// single most severe status across all resources. This ensures that one critical resource
/// doesn't get lost among many healthy ones.
/// </para>
/// <para>
/// <b>Default thresholds:</b>
/// </para>
/// <list type="bullet">
///   <item><description>General: 80% degraded, 95% unhealthy</description></item>
///   <item><description>WALRingBuffer: 60% degraded, 80% unhealthy (critical for durability)</description></item>
///   <item><description>TransactionPool: 60% degraded, 80% unhealthy (critical for throughput)</description></item>
/// </list>
/// </remarks>
[PublicAPI]
public sealed class ResourceHealthChecker : ITyphonHealthCheck
{
    private static readonly Dictionary<string, HealthThresholds> DefaultThresholds = new()
    {
        // Critical resources use stricter thresholds
        ["Durability/WALRingBuffer"] = HealthThresholds.Critical,
        ["DataEngine/TransactionPool"] = HealthThresholds.Critical,
    };

    private readonly ResourceMetricsExporter _exporter;
    private readonly ObservabilityBridgeOptions _options;

    /// <summary>
    /// Creates a new ResourceHealthChecker.
    /// </summary>
    /// <param name="exporter">The metrics exporter to read snapshots from.</param>
    /// <param name="options">Configuration options containing custom thresholds.</param>
    public ResourceHealthChecker(ResourceMetricsExporter exporter, ObservabilityBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(options);

        _exporter = exporter;
        _options = options;
    }

    /// <inheritdoc/>
    public HealthStatus CheckHealth()
    {
        var snapshot = _exporter.CurrentSnapshot;
        if (snapshot == null)
        {
            return HealthStatus.Healthy; // No data yet
        }

        var mostUtilized = snapshot.FindMostUtilized();
        if (mostUtilized == null || !mostUtilized.Capacity.HasValue)
        {
            return HealthStatus.Healthy; // No capacity metrics
        }

        var thresholds = GetThresholds(mostUtilized.Path);
        var utilization = mostUtilized.Capacity.Value.Utilization;

        if (utilization >= thresholds.UnhealthyThreshold)
        {
            return HealthStatus.Unhealthy;
        }

        if (utilization >= thresholds.DegradedThreshold)
        {
            return HealthStatus.Degraded;
        }

        return HealthStatus.Healthy;
    }

    /// <inheritdoc/>
    public HealthCheckResult GetDetailedResult()
    {
        var snapshot = _exporter.CurrentSnapshot;
        if (snapshot == null)
        {
            return HealthCheckResult.Healthy();
        }

        var data = new Dictionary<string, object>();
        var degradedResources = new List<string>();
        var unhealthyResources = new List<string>();
        var worstStatus = HealthStatus.Healthy;

        // Evaluate all nodes with capacity metrics
        foreach (var node in snapshot.Nodes.Values.Where(n => n.Capacity.HasValue))
        {
            var thresholds = GetThresholds(node.Path);
            var utilization = node.Capacity.Value.Utilization;

            if (utilization >= thresholds.UnhealthyThreshold)
            {
                unhealthyResources.Add(node.Path);
                worstStatus = HealthStatus.Unhealthy;
            }
            else if (utilization >= thresholds.DegradedThreshold)
            {
                degradedResources.Add(node.Path);
                if (worstStatus < HealthStatus.Degraded)
                {
                    worstStatus = HealthStatus.Degraded;
                }
            }
        }

        // Find most utilized for reporting
        var mostUtilized = snapshot.FindMostUtilized();
        if (mostUtilized?.Capacity != null)
        {
            data["most_utilized_path"] = mostUtilized.Path;
            data["most_utilized_percent"] = Math.Round(mostUtilized.Capacity.Value.Utilization * 100, 1);
        }

        if (degradedResources.Count > 0)
        {
            data["degraded_resources"] = degradedResources;
        }

        if (unhealthyResources.Count > 0)
        {
            data["unhealthy_resources"] = unhealthyResources;
        }

        // Build description
        var description = worstStatus switch
        {
            HealthStatus.Healthy => "All resources healthy",
            HealthStatus.Degraded when mostUtilized != null =>
                $"{mostUtilized.Path} at {mostUtilized.Capacity.Value.Utilization:P0} utilization (Degraded)",
            HealthStatus.Unhealthy when mostUtilized != null =>
                $"{mostUtilized.Path} at {mostUtilized.Capacity.Value.Utilization:P0} utilization (Unhealthy)",
            _ => $"Status: {worstStatus}"
        };

        return new HealthCheckResult
        {
            Status = worstStatus,
            Description = description,
            MostUtilizedNode = mostUtilized,
            Data = data
        };
    }

    /// <summary>
    /// Get thresholds for a specific resource path, falling back to defaults.
    /// </summary>
    private HealthThresholds GetThresholds(string path)
    {
        // Normalize path: remove "Root/" prefix for lookup
        var normalizedPath = path.StartsWith("Root/") ? path[5..] : path;

        // Check configured thresholds first
        if (_options.Thresholds.TryGetValue(normalizedPath, out var configured))
        {
            return configured;
        }

        // Check built-in defaults for critical resources
        if (DefaultThresholds.TryGetValue(normalizedPath, out var builtin))
        {
            return builtin;
        }

        // Use general default
        return HealthThresholds.Default;
    }
}
