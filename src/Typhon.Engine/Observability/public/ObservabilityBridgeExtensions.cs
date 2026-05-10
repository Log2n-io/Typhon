using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Typhon.Engine;

/// <summary>
/// Extension methods for registering Typhon Observability Bridge services with dependency injection.
/// </summary>
[PublicAPI]
[ExcludeFromCodeCoverage]
public static class ObservabilityBridgeExtensions
{
    /// <summary>
    /// Adds the Typhon Observability Bridge to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional action to configure bridge options.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method registers:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><see cref="ObservabilityBridgeOptions"/> (singleton)</description></item>
    ///   <item><description><see cref="ResourceMetricsExporter"/> (singleton)</description></item>
    ///   <item><description><see cref="ResourceHealthChecker"/> as <see cref="ITyphonHealthCheck"/> (singleton)</description></item>
    ///   <item><description><see cref="ResourceAlertGenerator"/> (singleton)</description></item>
    ///   <item><description><see cref="ResourceMetricsService"/> (singleton)</description></item>
    /// </list>
    /// <para>
    /// <b>Prerequisites:</b> <see cref="IResourceGraph"/> must be registered in the service collection.
    /// </para>
    /// <example>
    /// <code>
    /// services.AddSingleton&lt;IResourceGraph&gt;(sp => ...);
    /// services.AddTyphonObservabilityBridge(options =>
    /// {
    ///     options.SnapshotInterval = TimeSpan.FromSeconds(10);
    ///     options.Thresholds["Storage/PageCache"] = new HealthThresholds(0.70, 0.90);
    /// });
    /// </code>
    /// </example>
    /// </remarks>
    public static IServiceCollection AddTyphonObservabilityBridge(this IServiceCollection services, Action<ObservabilityBridgeOptions> configure = null)
    {
        // Register options
        services.Configure<ObservabilityBridgeOptions>(options =>
        {
            configure?.Invoke(options);
        });

        // Also register a non-IOptions<T> version for direct injection
        services.TryAddSingleton(_ =>
        {
            var options = new ObservabilityBridgeOptions();
            configure?.Invoke(options);
            return options;
        });

        // Register metrics exporter
        services.TryAddSingleton(sp =>
        {
            var resourceGraph = sp.GetRequiredService<IResourceGraph>();
            var options = sp.GetRequiredService<ObservabilityBridgeOptions>();
            return new ResourceMetricsExporter(resourceGraph, options);
        });

        // Register health checker
        services.TryAddSingleton<ITyphonHealthCheck>(sp =>
        {
            var exporter = sp.GetRequiredService<ResourceMetricsExporter>();
            var options = sp.GetRequiredService<ObservabilityBridgeOptions>();
            return new ResourceHealthChecker(exporter, options);
        });

        // Also register the concrete type for internal use
        services.TryAddSingleton(sp =>
        {
            var exporter = sp.GetRequiredService<ResourceMetricsExporter>();
            var options = sp.GetRequiredService<ObservabilityBridgeOptions>();
            return new ResourceHealthChecker(exporter, options);
        });

        // Register alert generator
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<ObservabilityBridgeOptions>();
            return new ResourceAlertGenerator(options);
        });

        // Register background service
        services.TryAddSingleton(sp =>
        {
            var exporter = sp.GetRequiredService<ResourceMetricsExporter>();
            var healthChecker = sp.GetRequiredService<ResourceHealthChecker>();
            var alertGenerator = sp.GetRequiredService<ResourceAlertGenerator>();
            var options = sp.GetRequiredService<ObservabilityBridgeOptions>();
            return new ResourceMetricsService(exporter, healthChecker, alertGenerator, options);
        });

        return services;
    }
}
