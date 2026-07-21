using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Typhon.Engine;
using Typhon.Samples.Swg;
// Disambiguate the SWG schema struct from Typhon.Engine's ResourceType enum (both are in scope via the global usings).
using ResourceType = Typhon.Samples.Swg.ResourceType;

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
        // Register the 20 components of the shared SWG Full schema (Typhon.Samples.Swg). These populate the 9 Full
        // archetypes: Guild, ResourceType, Recipe, Player, ResourceDeposit, Structure(←Harvester/Factory), Item.

        // Social family
        Engine.RegisterComponentFromAccessor<Guild>();
        Engine.RegisterComponentFromAccessor<Membership>();
        Engine.RegisterComponentFromAccessor<Player>();
        Engine.RegisterComponentFromAccessor<Wallet>();
        Engine.RegisterComponentFromAccessor<Session>();

        // Industry family
        Engine.RegisterComponentFromAccessor<ResourceType>();
        Engine.RegisterComponentFromAccessor<Recipe>();
        Engine.RegisterComponentFromAccessor<Deposit>();
        Engine.RegisterComponentFromAccessor<Structure>();
        Engine.RegisterComponentFromAccessor<StructureOwner>();
        Engine.RegisterComponentFromAccessor<Hopper>();
        Engine.RegisterComponentFromAccessor<HarvesterTarget>();
        Engine.RegisterComponentFromAccessor<MaintenanceState>();
        Engine.RegisterComponentFromAccessor<FactoryConfig>();
        Engine.RegisterComponentFromAccessor<PowerSupply>();

        // Item family
        Engine.RegisterComponentFromAccessor<Item>();
        Engine.RegisterComponentFromAccessor<ItemOwner>();

        // World family (spatial positions)
        Engine.RegisterComponentFromAccessor<PlayerPosition>();
        Engine.RegisterComponentFromAccessor<DepositPosition>();
        Engine.RegisterComponentFromAccessor<StructurePosition>();

        // Spatial archetypes (Player/Deposit/Harvester/Factory positions) are cluster-eligible, so a configured grid
        // is REQUIRED before InitializeArchetypes (see FixtureDatabase.cs). All positions are placed within [0, 10000].
        Engine.ConfigureSpatialGrid(new SpatialGridConfig(
            new Vector2(0f, 0f), new Vector2(10000f, 10000f), cellSize: 100f));

        // Initialize archetype storage (LinearHash, etc.)
        Engine.InitializeArchetypes();
    }

    // Note: Engine is a singleton managed by the DI container, don't dispose it here
    public void Dispose() => MetricsService?.Stop();
}
