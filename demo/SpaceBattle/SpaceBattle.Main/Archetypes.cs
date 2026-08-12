using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

internal static class SpaceBattleSchemaIds
{
    public const ushort Ship = 3000;
    public const ushort SimulationRun = 3001;
    public const ushort TargetLock = 3002;
}

[Archetype(SpaceBattleSchemaIds.Ship)]
public sealed partial class Ship : Archetype<Ship>
{
    public static readonly Comp<PositionComponent> Position = Register<PositionComponent>();
    public static readonly Comp<SpatialBoundsComponent> SpatialBounds = Register<SpatialBoundsComponent>();
    public static readonly Comp<MotionComponent> Motion = Register<MotionComponent>();
    public static readonly Comp<HealthComponent> Health = Register<HealthComponent>();
    public static readonly Comp<BehaviorComponent> Behavior = Register<BehaviorComponent>();
    public static readonly Comp<TrackingComponent> Tracking = Register<TrackingComponent>();
    public static readonly Comp<WeaponComponent> Weapon = Register<WeaponComponent>();
    public static readonly Comp<AfterburnerComponent> Afterburner = Register<AfterburnerComponent>();
    public static readonly Comp<ShipRunMembershipComponent> RunMembership = Register<ShipRunMembershipComponent>();
    public static readonly Comp<PauseShipCheckpointComponent> PauseCheckpoint = Register<PauseShipCheckpointComponent>();
}

[Archetype(SpaceBattleSchemaIds.SimulationRun)]
public sealed partial class SimulationRunEntity : Archetype<SimulationRunEntity>
{
    public static readonly Comp<SimulationRunComponent> Run = Register<SimulationRunComponent>();
    public static readonly Comp<SimulationRunStateComponent> State = Register<SimulationRunStateComponent>();
    public static readonly Comp<PauseRunCheckpointComponent> PauseCheckpoint = Register<PauseRunCheckpointComponent>();
}

[Archetype(SpaceBattleSchemaIds.TargetLock)]
public sealed partial class TargetLock : Archetype<TargetLock>
{
    public static readonly Comp<TargetLockComponent> Data = Register<TargetLockComponent>();
    public static readonly Comp<PauseTargetLockCheckpointComponent> PauseCheckpoint = Register<PauseTargetLockCheckpointComponent>();
}
