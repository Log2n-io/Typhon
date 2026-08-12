using Typhon.Engine;

namespace SpaceBattle;

public interface ISpaceBattleObservationSink
{
    void Publish(SpaceBattleObservation observation);
}

public abstract record SpaceBattleObservation;

public sealed record InitializationProgress(
    int CreatedShips,
    int TotalShips) : SpaceBattleObservation;

public sealed record InitializationCompleted(
    int ShipCount,
    TimeSpan Duration) : SpaceBattleObservation;

public readonly record struct SpaceBattleModeCounts(
    int Staging,
    int Wandering,
    int Tracking,
    int Combat,
    int Disengaging,
    int Escaping)
{
    public int Total => Staging + Wandering + Tracking + Combat + Disengaging + Escaping;
}

public readonly record struct SpaceBattleCounters(
    uint AliveShipCount,
    int ActiveLockCount,
    ulong ShotsFired,
    ulong Hits,
    ulong Deaths);

public readonly record struct SpaceBattleTickPerformance(
    int SampleCount,
    double P50ActualDurationMilliseconds,
    double P95ActualDurationMilliseconds,
    double P99ActualDurationMilliseconds,
    double MaximumActualDurationMilliseconds,
    int OverrunCount,
    double MaximumOverrunRatio,
    double LastActualDurationMilliseconds,
    double LastTargetDurationMilliseconds,
    double LastOverrunRatio);

public sealed record SpaceBattleLogSnapshot(
    ulong CompletedTicks,
    uint ProcessSegment,
    SimulationRunSnapshot Run,
    SpaceBattleModeCounts Modes,
    SpaceBattleCounters Counters,
    SpaceBattleTickPerformance Performance) : SpaceBattleObservation;

public sealed record SpaceBattleResourceSnapshot(
    ulong CompletedTicks,
    uint ProcessSegment,
    ResourceSnapshot Snapshot) : SpaceBattleObservation;
