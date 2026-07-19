using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using Typhon.Engine.internals;

namespace Typhon.Engine;

/// <summary>
/// Dependency-injection registration for the Typhon engine and its subsystems. <see cref="AddTyphon"/> is the one-line
/// entry point most hosts want — it composes the whole service graph and leaves a ready-to-use <see cref="DatabaseEngine"/>.
/// The individual <c>Add*</c> methods, each offered in Singleton / Scoped / Transient lifetime variants, compose that graph
/// piece by piece for callers who need finer control over lifetimes or want to substitute a subsystem.
/// </summary>
[PublicAPI]
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the engine's pinned-memory provider — <see cref="IMemoryAllocator"/>, backed by <see cref="MemoryAllocator"/> — as a singleton. Supplies
    /// GC-stable memory blocks to every other subsystem; depends on <see cref="IResourceRegistry"/>.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="MemoryAllocatorOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
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

    /// <summary>Scoped-lifetime variant of <see cref="AddMemoryAllocator"/>.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="MemoryAllocatorOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
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

    /// <summary>Transient-lifetime variant of <see cref="AddMemoryAllocator"/>.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="MemoryAllocatorOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
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
            // No validator: MemoryAllocatorOptions carries only a diagnostic Name — nothing that can be misconfigured. #148
            optionsBuilder.Configure(configure);
        }
    }

    /// <summary>
    /// Registers <see cref="IResourceRegistry"/> — backed by <see cref="ResourceRegistry"/> — as a singleton. The registry owns the engine's shared runtime
    /// resources (timer wheel, synchronization, storage bookkeeping) that the allocator, timers, epoch manager, and storage all draw on.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="ResourceRegistryOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
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

    /// <summary>Scoped-lifetime variant of <see cref="AddResourceRegistry"/>.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="ResourceRegistryOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
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

    /// <summary>Transient-lifetime variant of <see cref="AddResourceRegistry"/>.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="ResourceRegistryOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
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
            // No validator: ResourceRegistryOptions carries only a diagnostic Name — nothing that can be misconfigured. #148
            optionsBuilder.Configure(configure);
        }
    }

    /// <summary>
    /// Registers the singleton <see cref="HighResolutionSharedTimerService"/> — one shared high-resolution timer that the deadline/timeout machinery drives,
    /// rather than each component running its own. Depends on <see cref="IResourceRegistry"/>.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
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

    /// <summary>
    /// Registers the singleton <see cref="DeadlineWatchdog"/> — enforces per-operation deadlines by watching the shared timer and tripping work that overruns.
    /// Depends on <see cref="IResourceRegistry"/> and <see cref="HighResolutionSharedTimerService"/>.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
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

    /// <summary>
    /// Registers the singleton <see cref="EpochManager"/> — the epoch-based reclamation coordinator that keeps a memory page alive while any reader still holds
    /// a raw pointer into it, so eviction or reuse can never pull memory out from under an in-flight read. Depends on <see cref="IResourceRegistry"/>.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddEpochManager(this IServiceCollection services)
    {
        services.Add(ServiceDescriptor.Singleton(sp =>
        {
            var rr = sp.GetRequiredService<IResourceRegistry>();
            return new EpochManager("EpochManager", rr.Synchronization);
        }));
        return services;
    }

    /// <summary>Scoped-lifetime variant of <see cref="AddEpochManager"/>.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddScopedEpochManager(this IServiceCollection services)
    {
        services.Add(ServiceDescriptor.Scoped(sp =>
        {
            var rr = sp.GetRequiredService<IResourceRegistry>();
            return new EpochManager("EpochManager", rr.Synchronization);
        }));
        return services;
    }

    /// <summary>Transient-lifetime variant of <see cref="AddEpochManager"/>.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddTransientEpochManager(this IServiceCollection services)
    {
        services.Add(ServiceDescriptor.Transient(sp =>
        {
            var rr = sp.GetRequiredService<IResourceRegistry>();
            return new EpochManager("EpochManager", rr.Synchronization);
        }));
        return services;
    }

    /// <summary>
    /// Registers <see cref="PagedMMF"/> — the low-level paged memory-mapped file store (raw pages, page cache, checkpoint) — as a singleton, configured via
    /// <paramref name="configure"/>. Most callers want <see cref="AddManagedPagedMMF"/> or <see cref="AddTyphon"/> instead.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="PagedMMFOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddPagedMemoryMappedFile(
        this IServiceCollection services,
        Action<PagedMMFOptions> configure = null) =>
        services.AddPagedMMF<PagedMMF, PagedMMFOptions>(ServiceLifetime.Singleton, configure);

    /// <summary>Scoped-lifetime variant of <see cref="AddPagedMemoryMappedFile"/>.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="PagedMMFOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddScopedPagedMemoryMappedFile(
        this IServiceCollection services,
        Action<PagedMMFOptions> configure = null) =>
        services.AddPagedMMF<PagedMMF, PagedMMFOptions>(ServiceLifetime.Scoped, configure);

    /// <summary>Transient-lifetime variant of <see cref="AddPagedMemoryMappedFile"/>.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="PagedMMFOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddTransientPagedMemoryMappedFile(
        this IServiceCollection services,
        Action<PagedMMFOptions> configure = null) =>
        services.AddPagedMMF<PagedMMF, PagedMMFOptions>(ServiceLifetime.Transient, configure);

    /// <summary>
    /// Registers <see cref="ManagedPagedMMF"/> — the segment-aware store the engine builds on, layering logical segments, occupancy tracking, and epoch
    /// integration over <see cref="PagedMMF"/> — as a singleton, configured via <paramref name="configure"/>.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="ManagedPagedMMFOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddManagedPagedMMF(
        this IServiceCollection services,
        Action<ManagedPagedMMFOptions> configure = null) =>
        services.AddPagedMMF<ManagedPagedMMF, ManagedPagedMMFOptions>(ServiceLifetime.Singleton, configure);

    /// <summary>Scoped-lifetime variant of <see cref="AddManagedPagedMMF"/>.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="ManagedPagedMMFOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddScopedManagedPagedMemoryMappedFile(
        this IServiceCollection services,
        Action<ManagedPagedMMFOptions> configure = null) =>
        services.AddPagedMMF<ManagedPagedMMF, ManagedPagedMMFOptions>(ServiceLifetime.Scoped, configure);

    /// <summary>Transient-lifetime variant of <see cref="AddManagedPagedMMF"/>.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="ManagedPagedMMFOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddTransientManagedPagedMemoryMappedFile(
        this IServiceCollection services,
        Action<ManagedPagedMMFOptions> configure = null) =>
        services.AddPagedMMF<ManagedPagedMMF, ManagedPagedMMFOptions>(ServiceLifetime.Transient, configure);

    /// <summary>
    /// Registers the top-level <see cref="DatabaseEngine"/> — Typhon's ACID/ECS engine — as a singleton, configured via <paramref name="configure"/>. This is
    /// the power-user path: unlike <see cref="AddTyphon"/>, the caller owns component/archetype registration and must call
    /// <see cref="DatabaseEngine.InitializeArchetypes"/> before the engine is used.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="DatabaseEngineOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddDatabaseEngine(
        this IServiceCollection services,
        Action<DatabaseEngineOptions> configure = null) =>
        AddDatabaseEngine(services, ServiceLifetime.Singleton, configure);

    /// <summary>Scoped-lifetime variant of <see cref="AddDatabaseEngine"/>.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="DatabaseEngineOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddScopedDatabaseEngine(
        this IServiceCollection services,
        Action<DatabaseEngineOptions> configure = null) =>
        AddDatabaseEngine(services, ServiceLifetime.Scoped, configure);

    /// <summary>Transient-lifetime variant of <see cref="AddDatabaseEngine"/>.</summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional configuration of <see cref="DatabaseEngineOptions"/>. May be <see langword="null"/> to accept the defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddTransientDatabaseEngine(
        this IServiceCollection services,
        Action<DatabaseEngineOptions> configure = null) =>
        AddDatabaseEngine(services, ServiceLifetime.Transient, configure);

    /// <summary>
    /// One-line Typhon setup: composes the full engine service graph (memory allocator, resource registry, timers, epoch manager, storage, engine) and, at
    /// first resolve, applies the configured component + archetype registrations and runs <c>InitializeArchetypes</c> — so the resolved
    /// <see cref="DatabaseEngine"/> is ready to use. All from a single <see cref="TyphonOptions"/> fluent block. The individual <c>Add*</c> methods remain
    /// available for power users who need finer control (they own <c>InitializeArchetypes</c> themselves).
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddTyphon(o => o.DatabaseFile("game.typhon").Register&lt;Position&gt;()); // archetypes self-register at assembly load (#514)
    /// </code>
    /// </example>
    public static IServiceCollection AddTyphon(this IServiceCollection services, Action<TyphonOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TyphonOptions();
        configure(options);

        // Self-heal the most common DI footgun: the engine's storage/engine services take a required ILogger<T>, so
        // resolving DatabaseEngine without a logging backend throws an opaque "No service for type 'ILogger<PagedMMF>'".
        // AddLogging registers a no-op LoggerFactory via TryAdd, so it satisfies those loggers WITHOUT overriding a
        // caller who already configured logging (their providers win). Mirrors DatabaseEngine.Open, which does the same.
        services.AddLogging();

        services
            .AddResourceRegistry()
            .AddMemoryAllocator(options.MemoryAllocatorConfigurator)
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddManagedPagedMMF(options.StorageConfigurator)
            .AddDatabaseEngine(options.EngineConfigurator);

        // Always decorate: the hook applies any Register<T> calls AND runs InitializeArchetypes, so the resolved engine is
        // fully open and ready to use — the point of the one-line setup. Even with zero user components the engine still
        // needs InitializeArchetypes (its own system components must be wired). The power-user AddDatabaseEngine path is
        // untouched — callers there run InitializeArchetypes themselves.
        DecorateDatabaseEngineForInitialization(services, options);

        return services;
    }

    // Wraps the DatabaseEngine service descriptor so, at first resolve, the fully-constructed engine has its Register<T>
    // components applied and then InitializeArchetypes() run — leaving a ready-to-use engine. Kept local to AddTyphon so
    // the power-user AddDatabaseEngine path (no TyphonOptions in the container) is untouched. Both steps run exactly once
    // (singleton first-resolve); register-once is load-bearing — RegisterComponentFromAccessor builds a ComponentTable and
    // persists schema and is not idempotent, and InitializeArchetypes wires per-engine archetype state once.
    private static void DecorateDatabaseEngineForInitialization(IServiceCollection services, TyphonOptions options)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != typeof(DatabaseEngine))
            {
                continue;
            }

            var innerFactory = descriptor.ImplementationFactory;
            if (innerFactory == null)
            {
                // AddDatabaseEngine always registers via a factory; nothing to decorate otherwise.
                return;
            }

            services[i] = ServiceDescriptor.Describe(
                typeof(DatabaseEngine),
                sp =>
                {
                    var engine = (DatabaseEngine)innerFactory(sp);
                    try
                    {
                        // Order matters: register component storage on this engine, apply the spatial-grid config (needed by
                        // spatial archetypes), THEN wire per-archetype state — InitializeArchetypes only wires archetypes that
                        // are in the registry and whose components are all registered, and it consumes the pending spatial-grid
                        // config while building per-archetype spatial state (so ConfigureSpatialGrid MUST land before it).
                        // Archetype shapes are registered automatically at assembly load by the generated [ModuleInitializer]
                        // barrier (feature #514), so there is no explicit archetype-registration step here anymore.
                        options.ApplyComponentRegistrations(engine);
                        options.ApplySpatialGridConfig(engine);
                        engine.InitializeArchetypes();
                        RunPendingSeedsIfNeeded(engine, options);
                        return engine;
                    }
                    catch
                    {
                        // Post-construction init failed — dispose the engine we built (releases its MMF handle) before the
                        // exception unwinds: the DI container never took ownership of it (this factory hasn't returned). The
                        // cleanup dispose is itself guarded so a teardown throw can't mask the original init failure.
                        try
                        {
                            engine.Dispose();
                        }
                        catch
                        {
                            // ignored — the original init exception is the diagnostic one
                        }

                        throw;
                    }
                },
                descriptor.Lifetime);
            return;
        }
    }

    // ── Seeding ──
    // Applies every registered seed step whose revision > the database's committed seed revision, in ascending order, each in its own durable transaction; the
    // committed revision is then recorded in the bootstrap key/value store. A fresh database (committed 0) runs every step; an existing one runs only the steps
    // it has not applied yet — bringing each instance up to date as new steps ship. A step whose transaction never commits re-runs on the next open.
    //
    // Durability note: the committed-revision write is a separate meta-page fsync, NOT part of the step's WAL commit, so it cannot be atomic with the step's
    // data. We record it AFTER the step commits, so a crash in the narrow window between a step's commit and this write re-runs THAT step next open — and since
    // steps are forward-only, the worst case is a duplicate of that one step's data, never data loss. A fully-atomic marker would require an engine-owned ECS
    // entity, a new architectural category we deliberately avoid for a setup-time convenience (#506).
    private static void RunPendingSeedsIfNeeded(DatabaseEngine engine, TyphonOptions options)
    {
        var steps = options.SeedSteps; // sorted ascending by revision
        if (steps.Length == 0)
        {
            return; // no seed steps configured
        }

        int committed = engine.MMF.Bootstrap.GetInt(DatabaseEngine.BK_SeedRevision);
        foreach (var (revision, step) in steps)
        {
            if (revision <= committed)
            {
                continue; // already applied
            }

            using (var tx = engine.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                step(tx);
                tx.Commit();
            }

            engine.MMF.Bootstrap.SetInt(DatabaseEngine.BK_SeedRevision, revision);
            engine.MMF.SaveBootstrap();
            committed = revision;
        }
    }

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

            // Fail fast at DI resolution by delegating to PagedMMFOptions' own Validate() (the single source of truth for
            // storage-config rules), surfacing its specific rule message. #148
            services.AddSingleton<IValidateOptions<TO>>(new PagedMMFOptionsValidator<TO>());
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

    private static IServiceCollection AddDatabaseEngine(IServiceCollection services, ServiceLifetime lifetime, Action<DatabaseEngineOptions> configure)
    {
        var optionsBuilder = services.AddOptions<DatabaseEngineOptions>();

        if (configure != null)
        {
            optionsBuilder.Configure(configure);

            // Range-check the wired Resources knobs at DI resolution (see DatabaseEngineOptionsValidator). #148
            services.AddSingleton<IValidateOptions<DatabaseEngineOptions>>(new DatabaseEngineOptionsValidator());
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
        var options = serviceProvider.GetRequiredService<IOptions<DatabaseEngineOptions>>();
        var mpmmf = serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var logger = serviceProvider.GetRequiredService<ILogger<DatabaseEngine>>();
        var resourceRegistry = serviceProvider.GetRequiredService<IResourceRegistry>();
        var epochManager = serviceProvider.GetRequiredService<EpochManager>();
        var watchdog = serviceProvider.GetRequiredService<DeadlineWatchdog>();
        var memoryAllocator = serviceProvider.GetRequiredService<IMemoryAllocator>();

        // Optional WAL file-IO backend (e.g. an in-memory implementation for tests). Absent in production → engine builds its own WalFileIO.
        var walIo = serviceProvider.GetService<IWalFileIO>();

        return new DatabaseEngine(resourceRegistry, epochManager, watchdog, mpmmf, memoryAllocator, options.Value, logger, injectedWalIo: walIo);
    }
    
    /// <summary>
    /// Register a host-supplied profiler-launch override.
    /// </summary>
    /// <remarks>
    /// The runtime self-wires the profiler from <c>typhon.telemetry.json</c> by default — a host needs <b>zero code</b> to get profiling. Call this only to
    /// adjust the resolved <see cref="ProfilerLaunchConfig"/> in code (e.g. to layer CLI args on top, or compute a trace path at runtime).
    /// <paramref name="configure"/> receives the config resolved from file + environment and returns the effective config; precedence is JSON file →
    /// environment → this delegate. For the override to be seen, the host must pass its <see cref="IServiceProvider"/> to <see cref="TyphonRuntime.Create"/>.
    /// </remarks>
    public static IServiceCollection AddTyphonProfiler(this IServiceCollection services, Func<ProfilerLaunchConfig, ProfilerLaunchConfig> configure = null)
    {
        services.AddSingleton(new ProfilerLaunchOverride(configure));
        return services;
    }

    /// <summary>
    /// Resolves the <typeparamref name="TO"/> storage options from <paramref name="provider"/> and deletes the entire database bundle they point at (data file,
    /// lock, and WAL). Intended for tests and tooling that reset a database between runs; the database must be <b>closed</b> first — see
    /// <see cref="PagedMMFOptions.EnsureFileDeleted"/>.
    /// </summary>
    /// <typeparam name="TO">The storage options type (a <see cref="PagedMMFOptions"/> subtype) whose bundle location is deleted.</typeparam>
    /// <param name="provider">The service provider to resolve the options from; a scope is created internally.</param>
    public static void EnsureFileDeleted<TO>(this IServiceProvider provider) where TO : PagedMMFOptions
    {
        using var scope = provider.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<TO>>().Value;
        options.EnsureFileDeleted();
    }
}