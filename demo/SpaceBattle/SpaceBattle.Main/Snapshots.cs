namespace SpaceBattle;

public enum SimulationRunStatus : byte
{
    Running = 1,
    Completed = 2,
    TimedOut = 3,
}

public enum SimulationRunOutcome : byte
{
    None = 0,
    Winner = 1,
    Draw = 2,
    TimedOut = 3,
}

public enum SimulationStartupAction : byte
{
    Initialized = 1,
    Resumed = 2,
}

public enum BehaviorMode : byte
{
    Staging = 1,
    Wandering = 2,
    Tracking = 3,
    Combat = 4,
    Disengaging = 5,
    Escaping = 6,
}

public enum TargetLockStatus : byte
{
    Acquiring = 1,
    Locked = 2,
    Releasing = 3,
}

public readonly record struct PositionSnapshot(float X, float Y, float Z);

public readonly record struct SpatialBoundsSnapshot(
    float MinX,
    float MinY,
    float MinZ,
    float MaxX,
    float MaxY,
    float MaxZ);

public readonly record struct MotionSnapshot(
    float DirectionX,
    float DirectionY,
    float DirectionZ,
    float Speed);

public sealed record SimulationRunSnapshot(
    ulong Seed,
    ulong CompletedTicks,
    uint RulesetVersion,
    uint InitialShipCount,
    uint AliveShipCount,
    uint ProcessSegment,
    SimulationRunStatus Status,
    SimulationRunOutcome Outcome,
    long? WinnerEntityKey);

public sealed record ShipSnapshot(
    long EntityKey,
    PositionSnapshot Position,
    SpatialBoundsSnapshot Bounds,
    MotionSnapshot Motion,
    uint Health,
    BehaviorMode Mode,
    ushort ModeTicksRemaining,
    bool TrackingTargetIsNull,
    bool WeaponEnabled,
    bool AfterburnerEnabled);

public sealed record TargetLockSnapshot(
    long EntityKey,
    long OwnerEntityKey,
    long TargetEntityKey,
    TargetLockStatus Status,
    ushort TicksRemaining);

public sealed record KillParticipationSnapshot(
    long AttackerEntityKey,
    long TargetEntityKey);

public sealed record InitialWorldSnapshot(
    int RunCount,
    SimulationRunSnapshot Run,
    IReadOnlyList<ShipSnapshot> Ships,
    IReadOnlyList<TargetLockSnapshot> TargetLocks,
    IReadOnlyList<KillParticipationSnapshot> KillParticipations);

public sealed record SpaceBattleRunResult(
    int ShipCount,
    TimeSpan InitializationDuration,
    SimulationStartupAction StartupAction);
