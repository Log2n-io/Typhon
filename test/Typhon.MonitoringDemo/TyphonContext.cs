using Microsoft.Extensions.DependencyInjection;
using Typhon.Engine;

namespace Typhon.MonitoringDemo;

/// <summary>
/// Provides access to Typhon database engine and related services for scenarios.
/// Uses singleton services - the same engine persists for the entire application lifetime.
/// </summary>
public sealed class TyphonContext : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private bool _initialized;

    public TyphonContext(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Gets the database engine instance.
    /// </summary>
    public DatabaseEngine Engine { get; private set; } = null!;

    /// <summary>
    /// Gets the resource graph for metrics.
    /// </summary>
    public IResourceGraph ResourceGraph { get; private set; } = null!;

    /// <summary>
    /// Gets the metrics exporter.
    /// </summary>
    public ResourceMetricsExporter MetricsExporter { get; private set; } = null!;

    /// <summary>
    /// Gets the metrics service (background timer).
    /// </summary>
    public ResourceMetricsService MetricsService { get; private set; } = null!;

    /// <summary>
    /// Initializes the Typhon database and registers game components.
    /// This is called once at startup - all scenarios share the same database.
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        // Get singleton services directly from the root provider
        Engine = _serviceProvider.GetRequiredService<DatabaseEngine>();
        ResourceGraph = _serviceProvider.GetRequiredService<IResourceGraph>();
        MetricsExporter = _serviceProvider.GetRequiredService<ResourceMetricsExporter>();
        MetricsService = _serviceProvider.GetRequiredService<ResourceMetricsService>();

        // Start the background metrics service
        MetricsService.Start();

        // Register all game components
        RegisterGameComponents();

        _initialized = true;
    }

    private void RegisterGameComponents()
    {
        // Touch all archetypes to trigger static initialization

        // Factory game components
        Engine.RegisterComponentFromAccessor<FactoryBuilding>();
        Engine.RegisterComponentFromAccessor<ConveyorBelt>();
        Engine.RegisterComponentFromAccessor<ItemStack>();
        Engine.RegisterComponentFromAccessor<Recipe>();
        Engine.RegisterComponentFromAccessor<ProductionQueue>();
        Engine.RegisterComponentFromAccessor<ResourceNode>();
        Engine.RegisterComponentFromAccessor<PowerGrid>();

        // RPG components
        Engine.RegisterComponentFromAccessor<Character>();
        Engine.RegisterComponentFromAccessor<Inventory>();
        Engine.RegisterComponentFromAccessor<Equipment>();
        Engine.RegisterComponentFromAccessor<Skill>();
        Engine.RegisterComponentFromAccessor<Quest>();
        Engine.RegisterComponentFromAccessor<WorldPosition>();
        Engine.RegisterComponentFromAccessor<CombatStats>();

        // Initialize archetype storage (LinearHash, etc.)
        Engine.InitializeArchetypes();
    }

    // Note: Engine is a singleton managed by the DI container, don't dispose it here
    public void Dispose() => MetricsService?.Stop();
}
