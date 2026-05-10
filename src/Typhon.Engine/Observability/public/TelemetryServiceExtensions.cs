// unset

using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Typhon.Engine;

/// <summary>
/// Extension methods for registering Typhon profiler services with dependency injection.
/// </summary>
[PublicAPI]
[ExcludeFromCodeCoverage]
public static class TelemetryServiceExtensions
{
    /// <summary>
    /// Forces early initialization of <see cref="TelemetryConfig"/> so the JIT sees the
    /// final <c>static readonly bool</c> field values before compiling hot paths.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Call this method early in your application startup, before building the service provider,
    /// to ensure profiler configuration is loaded before hot paths are JIT compiled.
    /// </para>
    /// <para>
    /// The static <see cref="TelemetryConfig"/> fields are initialized from
    /// <c>typhon.telemetry.json</c> and environment variables in the static constructor.
    /// They cannot be changed programmatically — to change a value, edit the file or
    /// set the corresponding <c>TYPHON__PROFILER__*</c> environment variable before launch.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddTyphonProfiler();
    /// </code>
    /// </example>
    public static IServiceCollection AddTyphonProfiler(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        TelemetryConfig.EnsureInitialized();
        return services;
    }

    /// <summary>
    /// Renamed to <see cref="AddTyphonProfiler(IServiceCollection)"/> in the Phase 0 namespace cleanup.
    /// This delegating shim is provided for one release and will be removed in the next minor.
    /// </summary>
    [Obsolete("Renamed to AddTyphonProfiler. The legacy name is provided for one release and will be removed in the next minor.")]
    public static IServiceCollection AddTyphonTelemetry(this IServiceCollection services)
        => AddTyphonProfiler(services);
}
