using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SpaceBattle;

[Component("SpaceBattle.SimulationRun", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct SimulationRunComponent
{
    [Field] public ulong Seed;
    [Field] public ulong CompletedTicks;
    [Field] public uint RulesetVersion;
    [Field] public uint InitialShipCount;
    [Field] public uint AliveShipCount;
}

[Component("SpaceBattle.SimulationRunState", 2)]
[StructLayout(LayoutKind.Sequential)]
public struct SimulationRunStateComponent
{
    [Field] public long WinnerEntityKey;
    [Field] public uint ProcessSegment;
    [Field] public byte Status;
    [Field] public byte Outcome;
}

[Component("SpaceBattle.Position", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct PositionComponent
{
    [Field] public float X;
    [Field] public float Y;
    [Field] public float Z;
}

[Component("SpaceBattle.SpatialBounds", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SpatialBoundsComponent
{
    [Field]
    [SpatialIndex(20f)]
    public AABB3F Bounds;
}

[Component("SpaceBattle.Motion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct MotionComponent
{
    [Field] public float DirectionX;
    [Field] public float DirectionY;
    [Field] public float DirectionZ;
    [Field] public float Speed;
}

[Component("SpaceBattle.Health", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct HealthComponent
{
    [Field] public uint Current;
}

[Component("SpaceBattle.Behavior", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct BehaviorComponent
{
    [Field] public ulong DecisionOrdinal;
    [Field] public ushort ModeTicksRemaining;
    [Field] public byte Mode;
}

[Component("SpaceBattle.Tracking", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct TrackingComponent
{
    [Field] public EntityLink<Ship> Target;
    [Field] public ushort TrackingTicksRemaining;
}

[Component("SpaceBattle.Weapon", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct WeaponComponent
{
    [Field] public ushort CooldownTicksRemaining;
}

[Component("SpaceBattle.Afterburner", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct AfterburnerComponent
{
    [Field] public ulong ActivatedTick;
}

[Component("SpaceBattle.ShipMembership", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
public struct ShipRunMembershipComponent
{
    [Field]
    [Index(AllowMultiple = true)]
    public long RunEntityKey;
}

[Component("SpaceBattle.TargetLock", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct TargetLockComponent
{
    [Field]
    [Index(AllowMultiple = true)]
    public EntityLink<Ship> Owner;

    [Field]
    [Index(AllowMultiple = true)]
    public EntityLink<Ship> Target;

    [Field] public ushort TicksRemaining;
    [Field] public byte Status;
}

[Component("SpaceBattle.PauseShipCheckpoint", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct PauseShipCheckpointComponent
{
    [Field] public byte IsValid;
    [Field] public float PositionX;
    [Field] public float PositionY;
    [Field] public float PositionZ;
    [Field] public float BoundsMinX;
    [Field] public float BoundsMinY;
    [Field] public float BoundsMinZ;
    [Field] public float BoundsMaxX;
    [Field] public float BoundsMaxY;
    [Field] public float BoundsMaxZ;
    [Field] public float DirectionX;
    [Field] public float DirectionY;
    [Field] public float DirectionZ;
    [Field] public float Speed;
    [Field] public uint Health;
    [Field] public ulong DecisionOrdinal;
    [Field] public ushort ModeTicksRemaining;
    [Field] public byte Mode;
    [Field] public long TrackingTargetEntityKey;
    [Field] public ushort TrackingTicksRemaining;
    [Field] public ushort CooldownTicksRemaining;
    [Field] public ulong AfterburnerActivatedTick;
    [Field] public byte WeaponEnabled;
    [Field] public byte AfterburnerEnabled;
}

[Component("SpaceBattle.PauseTargetLockCheckpoint", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct PauseTargetLockCheckpointComponent
{
    [Field] public byte IsValid;
    [Field] public long OwnerEntityKey;
    [Field] public long TargetEntityKey;
    [Field] public ushort TicksRemaining;
    [Field] public byte Status;
}

[Component("SpaceBattle.PauseRunCheckpoint", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct PauseRunCheckpointComponent
{
    [Field] public ulong CompletedTicks;
    [Field] public uint AliveShipCount;
    [Field] public byte IsValid;
}
