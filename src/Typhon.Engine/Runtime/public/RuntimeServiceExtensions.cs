using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;

namespace Typhon.Engine;

/// <summary>
/// Extension methods for registering the DAG scheduler in the DI container.
/// </summary>
[PublicAPI]
public static class RuntimeServiceExtensions
{
    /// <summary>
    /// Registers a <see cref="DagScheduler"/> singleton in the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="buildDag">Action to configure the <see cref="DagBuilder"/> with systems and edges.</param>
    /// <param name="configure">Optional action to configure <see cref="RuntimeOptions"/>.</param>
    public static IServiceCollection AddDagScheduler(this IServiceCollection services, Action<DagBuilder> buildDag, Action<RuntimeOptions> configure = null)
    {
        ArgumentNullException.ThrowIfNull(buildDag);

        services.AddOptions<RuntimeOptions>();
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RuntimeOptions>>().Value;
            var rr = sp.GetRequiredService<IResourceRegistry>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<DagScheduler>();
            var builder = new DagBuilder();
            buildDag(builder);
            var (systems, topo) = builder.Build();
            return new DagScheduler(systems, topo, options, rr.Runtime, logger: logger);
        });

        return services;
    }
}
