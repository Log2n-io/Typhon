using System.Numerics;
using Typhon.Engine;

namespace SpaceBattle;

public static class SpaceBattleDatabase
{
    public static DatabaseEngine Open(
        SimulationDefinition definition,
        string databaseLocation)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseLocation);
        SpaceBattleProductionSettings.ResourceEnvelope.Validate();

        return DatabaseEngine.Open(databaseLocation, options => options
            .PageCacheSize(SpaceBattleProductionSettings.ResourceEnvelope.PageCacheSizeBytes)
            .Register<SimulationRunComponent>()
            .Register<SimulationRunStateComponent>()
            .Register<PositionComponent>()
            .Register<SpatialBoundsComponent>()
            .Register<MotionComponent>()
            .Register<HealthComponent>()
            .Register<BehaviorComponent>()
            .Register<TrackingComponent>()
            .Register<WeaponComponent>()
            .Register<AfterburnerComponent>()
            .Register<ShipRunMembershipComponent>()
            .Register<TargetLockComponent>()
            .Register<PauseShipCheckpointComponent>()
            .Register<PauseTargetLockCheckpointComponent>()
            .Register<PauseRunCheckpointComponent>()
            .ConfigureSpatialGrid(new SpatialGridConfig(
                Vector2.Zero,
                new Vector2(definition.WorldSize, definition.WorldSize),
                definition.SpatialCellSize)));
    }
}
