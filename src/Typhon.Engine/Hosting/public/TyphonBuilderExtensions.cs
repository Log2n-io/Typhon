using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;

namespace Typhon.Engine;

[PublicAPI]
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryAllocator(this IServiceCollection services, Action<MemoryAllocatorOptions> configure = null)
    {
        ConfigureMemoryAllocatorOptions(services, configure);
        services.Add(ServiceDescriptor.Singleton<IMemoryAllocator, MemoryAllocator>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MemoryAllocatorOptions>>();
            var rr = sp.GetRequiredService<IResourceRegistry>();
            return new MemoryAllocator(rr, options.Value);
        }));
        return services;
    }

    public static IServiceCollection AddScopedMemoryAllocator(this IServiceCollection services, Action<MemoryAllocatorOptions> configure = null)
    {
        ConfigureMemoryAllocatorOptions(services, configure);
        services.Add(ServiceDescriptor.Scoped(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MemoryAllocatorOptions>>();
            var rr = sp.GetRequiredService<IResourceRegistry>();
            return new MemoryAllocator(rr, options.Value);
        }));
        return services;
    }

    public static IServiceCollection AddTransientMemoryAllocator(this IServiceCollection services, Action<MemoryAllocatorOptions> configure = null)
    {
        ConfigureMemoryAllocatorOptions(services, configure);
        services.Add(ServiceDescriptor.Transient(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MemoryAllocatorOptions>>();
            var rr = sp.GetRequiredService<IResourceRegistry>();
            return new MemoryAllocator(rr, options.Value);
        }));
        return services;
    }

    private static void ConfigureMemoryAllocatorOptions(IServiceCollection services, Action<MemoryAllocatorOptions> configure)
    {
        var optionsBuilder = services.AddOptions<MemoryAllocatorOptions>();

        if (configure != null)
        {
            optionsBuilder.Configure(configure);

            optionsBuilder.Validate(_ =>
            {
                // TODO Add validation logic
                return true;
            });
        }
    }

    public static IServiceCollection AddResourceRegistry(this IServiceCollection services, Action<ResourceRegistryOptions> configure = null)
    {
        ConfigureResourceRegistryOptions(services, configure);
        services.Add(ServiceDescriptor.Singleton<IResourceRegistry, ResourceRegistry>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ResourceRegistryOptions>>();
            return new ResourceRegistry(options.Value);
        }));
        return services;
    }

    public static IServiceCollection AddScopedResourceRegistry(this IServiceCollection services, Action<ResourceRegistryOptions> configure = null)
    {
        ConfigureResourceRegistryOptions(services, configure);
        services.Add(ServiceDescriptor.Scoped<IResourceRegistry, ResourceRegistry>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ResourceRegistryOptions>>();
            return new ResourceRegistry(options.Value);
        }));
        return services;
    }

    public static IServiceCollection AddTransientResourceRegistry(this IServiceCollection services, Action<ResourceRegistryOptions> configure = null)
    {
        ConfigureResourceRegistryOptions(services, configure);
        services.Add(ServiceDescriptor.Transient<IResourceRegistry, ResourceRegistry>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ResourceRegistryOptions>>();
            return new ResourceRegistry(options.Value);
        }));
        return services;
    }

    private static void ConfigureResourceRegistryOptions(IServiceCollection services, Action<ResourceRegistryOptions> configure)
    {
        var optionsBuilder = services.AddOptions<ResourceRegistryOptions>();

        if (configure != null)
        {
            optionsBuilder.Configure(configure);

            optionsBuilder.Validate(_ =>
            {
                // TODO Add validation logic
                return true;
            });
        }
    }

    public static IServiceCollection AddHighResolutionSharedTimer(this IServiceCollection services)
    {
        services.Add(ServiceDescriptor.Singleton(sp =>
        {
            var rr = sp.GetRequiredService<IResourceRegistry>();
            var logger = sp.GetService<ILogger<HighResolutionSharedTimerService>>();
            return new HighResolutionSharedTimerService(rr.Timer, logger);
        }));
        return services;
    }

    public static IServiceCollection AddDeadlineWatchdog(this IServiceCollection services)
    {
        services.Add(ServiceDescriptor.Singleton(sp =>
        {
            var rr = sp.GetRequiredService<IResourceRegistry>();
            var sharedTimer = sp.GetRequiredService<HighResolutionSharedTimerService>();
            return new DeadlineWatchdog(rr, sharedTimer);
        }));
        return services;
    }

    public static IServiceCollection AddEpochManager(this IServiceCollection services)
    {
        services.Add(ServiceDescriptor.Singleton(sp =>
        {
            var rr = sp.GetRequiredService<IResourceRegistry>();
            return new EpochManager("EpochManager", rr.Synchronization);
        }));
        return services;
    }

    public static IServiceCollection AddScopedEpochManager(this IServiceCollection services)
    {
        services.Add(ServiceDescriptor.Scoped(sp =>
        {
            var rr = sp.GetRequiredService<IResourceRegistry>();
            return new EpochManager("EpochManager", rr.Synchronization);
        }));
        return services;
    }

    public static IServiceCollection AddTransientEpochManager(this IServiceCollection services)
    {
        services.Add(ServiceDescriptor.Transient(sp =>
        {
            var rr = sp.GetRequiredService<IResourceRegistry>();
            return new EpochManager("EpochManager", rr.Synchronization);
        }));
        return services;
    }

    public static IServiceCollection AddPagedMemoryMappedFiled(
        this IServiceCollection services,
        Action<PagedMMFOptions> configure = null) =>
        services.AddPagedMMF<PagedMMF, PagedMMFOptions>(ServiceLifetime.Singleton, configure);

    public static IServiceCollection AddScopedPagedMemoryMappedFile(
        this IServiceCollection services,
        Action<PagedMMFOptions> configure = null) =>
        services.AddPagedMMF<PagedMMF, PagedMMFOptions>(ServiceLifetime.Scoped, configure);

    public static IServiceCollection AddTransientPagedMemoryMappedFile(
        this IServiceCollection services,
        Action<PagedMMFOptions> configure = null) =>
        services.AddPagedMMF<PagedMMF, PagedMMFOptions>(ServiceLifetime.Transient, configure);

    public static IServiceCollection AddManagedPagedMMF(
        this IServiceCollection services,
        Action<ManagedPagedMMFOptions> configure = null) =>
        services.AddPagedMMF<ManagedPagedMMF, ManagedPagedMMFOptions>(ServiceLifetime.Singleton, configure);

    public static IServiceCollection AddScopedManagedPagedMemoryMappedFile(
        this IServiceCollection services,
        Action<ManagedPagedMMFOptions> configure = null) =>
        services.AddPagedMMF<ManagedPagedMMF, ManagedPagedMMFOptions>(ServiceLifetime.Scoped, configure);

    public static IServiceCollection AddTransientManagedPagedMemoryMappedFile(
        this IServiceCollection services,
        Action<ManagedPagedMMFOptions> configure = null) =>
        services.AddPagedMMF<ManagedPagedMMF, ManagedPagedMMFOptions>(ServiceLifetime.Transient, configure);

    public static IServiceCollection AddDatabaseEngine(
        this IServiceCollection services,
        Action<DatabaseEngineOptions> configure = null) =>
        AddDatabaseEngine(services, ServiceLifetime.Singleton, configure);

    public static IServiceCollection AddScopedDatabaseEngine(
        this IServiceCollection services,
        Action<DatabaseEngineOptions> configure = null) =>
        AddDatabaseEngine(services, ServiceLifetime.Scoped, configure);

    public static IServiceCollection AddTransientDatabaseEngine(
        this IServiceCollection services,
        Action<DatabaseEngineOptions> configure = null) =>
        AddDatabaseEngine(services, ServiceLifetime.Transient, configure);

    private static IServiceCollection AddPagedMMF<TS, TO>(
        this IServiceCollection services,
        ServiceLifetime lifetime,
        Action<TO> configure = null) where TS : PagedMMF where TO : PagedMMFOptions
    {
        // services.AddOptions<TO>();
        if (configure != null)
        {
            var optionsBuilder = services.AddOptions<TO>();
            optionsBuilder.Configure(configure);

            optionsBuilder.Validate(_ =>
            {
                
                // TODO Add validation logic for PagedMemoryMappedFileOptions
                return true;
            });
        }

        var serviceDescriptor = lifetime switch
        {
            ServiceLifetime.Singleton => ServiceDescriptor.Singleton(CreatePagedMemoryMappedFile<TS, TO>),
            ServiceLifetime.Scoped => ServiceDescriptor.Scoped(CreatePagedMemoryMappedFile<TS, TO>),
            ServiceLifetime.Transient => ServiceDescriptor.Transient(CreatePagedMemoryMappedFile<TS, TO>),
            _ => throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "Invalid service lifetime specified.")
        };

        services.Add(serviceDescriptor);
        return services;
    }

    private static TS CreatePagedMemoryMappedFile<TS, TO>(IServiceProvider serviceProvider) where TS : PagedMMF where TO : PagedMMFOptions
    {
        try
        {
            var options = serviceProvider.GetRequiredService<IOptions<TO>>();
            var logger = serviceProvider.GetRequiredService<ILogger<PagedMMF>>();
            var memoryAllocator = serviceProvider.GetRequiredService<IMemoryAllocator>();

            // Directly instantiate ManagedPagedMMF which requires IResourceRegistry
            if (typeof(TS) == typeof(ManagedPagedMMF))
            {
                var resourceRegistry = serviceProvider.GetRequiredService<IResourceRegistry>();
                var epochManager = serviceProvider.GetRequiredService<EpochManager>();
                return (TS)(object)new ManagedPagedMMF(resourceRegistry, epochManager, memoryAllocator, options.Value, resourceRegistry.Storage, options.Value.DatabaseName, logger);
            }

            // For base PagedMMF - doesn't require IResourceRegistry
            if (typeof(TS) == typeof(PagedMMF))
            {
                var resourceRegistry = serviceProvider.GetRequiredService<IResourceRegistry>();
                var epochManager = serviceProvider.GetRequiredService<EpochManager>();
                return (TS)new PagedMMF(memoryAllocator, epochManager, options.Value, resourceRegistry.Storage, options.Value.DatabaseName, logger);
            }

            // Fallback to Activator for other derived types (if any)
            return (TS)Activator.CreateInstance(typeof(TS), serviceProvider, options.Value, logger);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    private static IServiceCollection AddDatabaseEngine(IServiceCollection services, ServiceLifetime lifetime, Action<DatabaseEngineOptions> configure)
    {
        var optionsBuilder = services.AddOptions<DatabaseEngineOptions>();

        if (configure != null)
        {
            optionsBuilder.Configure(configure);

            optionsBuilder.Validate(_ =>
            {
                
                // TODO Add validation logic for PagedMemoryMappedFileOptions
                return true;
            });
        }

        var serviceDescriptor = lifetime switch
        {
            ServiceLifetime.Singleton => ServiceDescriptor.Singleton(CreateDatabaseEngine),
            ServiceLifetime.Scoped => ServiceDescriptor.Scoped(CreateDatabaseEngine),
            ServiceLifetime.Transient => ServiceDescriptor.Transient(CreateDatabaseEngine),
            _ => throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "Invalid service lifetime specified.")
        };

        services.Add(serviceDescriptor);
        return services;
    }

    private static DatabaseEngine CreateDatabaseEngine(IServiceProvider serviceProvider)
    {
        try
        {
            var options = serviceProvider.GetRequiredService<IOptions<DatabaseEngineOptions>>();
            var mpmmf = serviceProvider.GetRequiredService<ManagedPagedMMF>();
            var logger = serviceProvider.GetRequiredService<ILogger<DatabaseEngine>>();
            var resourceRegistry = serviceProvider.GetRequiredService<IResourceRegistry>();
            var epochManager = serviceProvider.GetRequiredService<EpochManager>();
            var watchdog = serviceProvider.GetRequiredService<DeadlineWatchdog>();
            var memoryAllocator = serviceProvider.GetRequiredService<IMemoryAllocator>();
            var walFileIO = serviceProvider.GetService<IWalFileIO>();

            return new DatabaseEngine(resourceRegistry, epochManager, watchdog, mpmmf, memoryAllocator, options.Value, logger, walFileIO);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }
    
    public static void EnsureFileDeleted<TO>(this IServiceProvider provider) where TO : PagedMMFOptions
    {
        using var scope = provider.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<TO>>().Value;
        options.EnsureFileDeleted();
    }
}