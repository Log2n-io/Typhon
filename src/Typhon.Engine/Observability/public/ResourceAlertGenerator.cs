using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine;

/// <summary>
/// Alert severity levels.
/// </summary>
[PublicAPI]
public enum AlertSeverity
{
    /// <summary>
    /// Warning: resource is degraded but still functional.
    /// </summary>
    Warning = 0,

    /// <summary>
    /// Critical: resource is at or beyond critical limits.
    /// </summary>
    Critical = 1
}

/// <summary>
/// An alert generated when a resource crosses health thresholds.
/// </summary>
/// <remarks>
/// <para>
/// Alerts include root cause analysis using <see cref="ResourceSnapshot.FindRootCause"/> and
/// cascading effect detection using <see cref="ResourceSnapshot.FindContentionHotspots"/>.
/// </para>
/// <example>
/// <code>
/// // Alert example:
/// // Severity: Critical
/// // Title: DataEngine/TransactionPool at 95% utilization
/// // Root Cause: Durability/WALRingBuffer (98% utilization)
/// // Cascading Effects: [Storage/PageCache, DataEngine/IndexMaintainer]
/// </code>
/// </example>
/// </remarks>
[PublicAPI]
public sealed class ResourceAlert
{
    /// <summary>
    /// Alert severity level.
    /// </summary>
    public AlertSeverity Severity { get; init; }

    /// <summary>
    /// Human-readable alert title.
    /// </summary>
    /// <example>"DataEngine/TransactionPool at 95% utilization"</example>
    public string Title { get; init; }

    /// <summary>
    /// Path of the symptomatic resource that triggered the alert.
    /// </summary>
    public string SymptomPath { get; init; }

    /// <summary>
    /// Utilization of the symptomatic resource (0.0-1.0).
    /// </summary>
    public double SymptomUtilization { get; init; }

    /// <summary>
    /// Path of the root cause resource (may be same as SymptomPath if no upstream cause found).
    /// </summary>
    public string RootCausePath { get; init; }

    /// <summary>
    /// Utilization of the root cause resource (0.0-1.0).
    /// </summary>
    public double RootCauseUtilization { get; init; }

    /// <summary>
    /// When the alert was generated.
    /// </summary>
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Generates alerts for resources that cross health thresholds.
/// </summary>
/// <remarks>
/// <para>
/// The generator uses architectural knowledge from <see cref="ResourceSnapshot.FindRootCause"/> to
/// trace back from symptomatic nodes to underlying causes. This helps operators understand
/// the true source of problems rather than just symptoms.
/// </para>
/// <para>
/// <b>Example scenario:</b>
/// </para>
/// <list type="bullet">
///   <item><description>Symptom: TransactionPool at 95% → commits are backing up</description></item>
///   <item><description>Cause: WALRingBuffer at 98% → WAL writes are slow</description></item>
///   <item><description>Root cause: WALSegments disk I/O saturated → physical disk limitation</description></item>
/// </list>
/// </remarks>
[PublicAPI]
public sealed class ResourceAlertGenerator
{
    private readonly ObservabilityBridgeOptions _options;

    /// <summary>
    /// Creates a new ResourceAlertGenerator.
    /// </summary>
    /// <param name="options">Configuration options containing thresholds.</param>
    public ResourceAlertGenerator(ObservabilityBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Generate an alert for a specific symptomatic resource.
    /// </summary>
    /// <param name="snapshot">Current resource snapshot.</param>
    /// <param name="symptomPath">Path of the resource showing symptoms.</param>
    /// <returns>Alert with root cause analysis, or null if the resource is healthy.</returns>
    public ResourceAlert GenerateAlert(ResourceSnapshot snapshot, string symptomPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(symptomPath);

        var node = snapshot.GetNode(symptomPath);
        if (node == null || !node.Capacity.HasValue)
        {
            return null;
        }

        var thresholds = GetThresholds(symptomPath);
        var utilization = node.Capacity.Value.Utilization;

        if (utilization < thresholds.DegradedThreshold)
        {
            return null; // Healthy, no alert needed
        }

        var severity = utilization >= thresholds.UnhealthyThreshold
            ? AlertSeverity.Critical
            : AlertSeverity.Warning;

        // Find root cause
        var rootCauseNode = snapshot.FindRootCause(symptomPath, thresholds.DegradedThreshold);
        var rootCausePath = rootCauseNode?.Path ?? symptomPath;
        var rootCauseUtilization = rootCauseNode?.Capacity?.Utilization ?? utilization;

        return new ResourceAlert
        {
            Severity = severity,
            Title = $"{NormalizePath(symptomPath)} at {utilization:P0} utilization",
            SymptomPath = symptomPath,
            SymptomUtilization = utilization,
            RootCausePath = rootCausePath,
            RootCauseUtilization = rootCauseUtilization,
            Timestamp = snapshot.Timestamp
        };
    }

    /// <summary>
    /// Generate alerts for all resources above threshold.
    /// </summary>
    /// <param name="snapshot">Current resource snapshot.</param>
    /// <returns>Alerts for all unhealthy or degraded resources.</returns>
    public IEnumerable<ResourceAlert> GenerateAlerts(ResourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        foreach (var node in snapshot.Nodes.Values.Where(n => n.Capacity.HasValue))
        {
            var thresholds = GetThresholds(node.Path);
            if (node.Capacity != null && node.Capacity.Value.Utilization >= thresholds.DegradedThreshold)
            {
                var alert = GenerateAlert(snapshot, node.Path);
                if (alert != null)
                {
                    yield return alert;
                }
            }
        }
    }

    /// <summary>
    /// Get thresholds for a specific resource path.
    /// </summary>
    private HealthThresholds GetThresholds(string path)
    {
        var normalizedPath = path.StartsWith("Root/") ? path[5..] : path;

        if (_options.Thresholds.TryGetValue(normalizedPath, out var configured))
        {
            return configured;
        }

        return HealthThresholds.Default;
    }

    /// <summary>
    /// Normalize a path for display (remove Root/ prefix).
    /// </summary>
    private static string NormalizePath(string path) => path.StartsWith("Root/") ? path[5..] : path;
}
