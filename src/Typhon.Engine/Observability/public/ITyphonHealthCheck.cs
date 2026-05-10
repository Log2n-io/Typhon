using JetBrains.Annotations;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Health status enumeration compatible with standard health check conventions.
/// </summary>
/// <remarks>
/// <para>
/// This enum follows the ASP.NET Core HealthCheckStatus convention, allowing easy mapping
/// to <c>Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus</c> by consumers.
/// </para>
/// </remarks>
[PublicAPI]
public enum HealthStatus
{
    /// <summary>
    /// All resources are operating within acceptable limits.
    /// </summary>
    Healthy = 0,

    /// <summary>
    /// Some resources are approaching limits but still functional.
    /// May indicate the need for scaling or investigation.
    /// </summary>
    Degraded = 1,

    /// <summary>
    /// One or more resources are at or beyond critical limits.
    /// Immediate attention required.
    /// </summary>
    Unhealthy = 2
}

/// <summary>
/// Interface for Typhon health checks, independent of ASP.NET Core.
/// </summary>
/// <remarks>
/// <para>
/// Consumers can provide a thin adapter to bridge this to <c>IHealthCheck</c> from
/// <c>Microsoft.Extensions.Diagnostics.HealthChecks</c> if using ASP.NET Core.
/// </para>
/// <example>
/// <code>
/// // ASP.NET Core adapter example:
/// public class TyphonHealthCheckAdapter : IHealthCheck
/// {
///     private readonly ITyphonHealthCheck _typhonCheck;
///
///     public Task&lt;HealthCheckResult&gt; CheckHealthAsync(...)
///     {
///         var result = _typhonCheck.GetDetailedResult();
///         return Task.FromResult(new HealthCheckResult(
///             (HealthStatus)(int)result.Status,
///             result.Description,
///             data: result.Data));
///     }
/// }
/// </code>
/// </example>
/// </remarks>
[PublicAPI]
public interface ITyphonHealthCheck
{
    /// <summary>
    /// Perform a quick health check, returning only the status.
    /// </summary>
    /// <returns>Current health status.</returns>
    /// <remarks>
    /// <para>
    /// This is optimized for frequent polling (e.g., Kubernetes liveness probes).
    /// Uses <see cref="ResourceSnapshot.FindMostUtilized"/> for O(n) complexity.
    /// </para>
    /// </remarks>
    HealthStatus CheckHealth();

    /// <summary>
    /// Perform a detailed health check with diagnostic information.
    /// </summary>
    /// <returns>Detailed health check result including metrics data.</returns>
    /// <remarks>
    /// <para>
    /// Provides additional context for debugging and dashboards:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>The resource with highest utilization</description></item>
    ///   <item><description>All resources above degraded threshold</description></item>
    ///   <item><description>Contention hotspots</description></item>
    /// </list>
    /// </remarks>
    HealthCheckResult GetDetailedResult();
}

/// <summary>
/// Detailed result from a Typhon health check.
/// </summary>
[PublicAPI]
public sealed class HealthCheckResult
{
    /// <summary>
    /// Overall health status.
    /// </summary>
    public HealthStatus Status { get; init; }

    /// <summary>
    /// Human-readable description of the health status.
    /// </summary>
    /// <example>
    /// "All resources healthy" or "Storage/PageCache at 92% utilization (Degraded)"
    /// </example>
    public string Description { get; init; }

    /// <summary>
    /// The resource node with highest utilization, if any have capacity metrics.
    /// Null if no capacity metrics exist or snapshot is empty.
    /// </summary>
    public NodeSnapshot MostUtilizedNode { get; init; }

    /// <summary>
    /// Additional diagnostic data for dashboards and debugging.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Common keys include:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>"most_utilized_path": Path of highest utilization node</description></item>
    ///   <item><description>"most_utilized_percent": Utilization percentage</description></item>
    ///   <item><description>"degraded_resources": List of degraded resource paths</description></item>
    ///   <item><description>"unhealthy_resources": List of unhealthy resource paths</description></item>
    ///   <item><description>"contention_hotspots": Resources with high wait times</description></item>
    /// </list>
    /// </remarks>
    public IReadOnlyDictionary<string, object> Data { get; init; }

    /// <summary>
    /// Creates a healthy result with no diagnostic data.
    /// </summary>
    public static HealthCheckResult Healthy() => new()
    {
        Status = HealthStatus.Healthy,
        Description = "All resources healthy",
        Data = new Dictionary<string, object>()
    };
}
