namespace SpaceBattle;

public sealed record SimulationDefinition
{
    public const ulong DefaultSeed = 0x5350414345424154UL;
    public const int FixedTickRate = 25;
    public const ulong ObservationLogIntervalTicks = 25;
    public const ulong ResourceSnapshotIntervalTicks = 125;
    public const float FixedSimulationDeltaSeconds = 0.04f;
    public const float BaseMaximumSpeed = 50f;
    public const float MaximumWanderingSpeed = BaseMaximumSpeed * 0.75f;
    public const ulong DefaultMaximumCompletedTicks = 45_000;

    public static SimulationDefinition Default { get; } = new(
        runName: "default",
        shipCount: 50_000,
        seed: DefaultSeed,
        rulesetVersion: 1,
        worldSize: 1_000f,
        maximumHealth: 1_000,
        stagingTicks: 250,
        spatialCellSize: 100f,
        spatialMargin: 20f,
        maximumCompletedTicks: DefaultMaximumCompletedTicks);

    public SimulationDefinition(
        string runName,
        int shipCount,
        ulong seed,
        uint rulesetVersion,
        float worldSize,
        uint maximumHealth,
        ushort stagingTicks,
        float spatialCellSize,
        float spatialMargin,
        ulong maximumCompletedTicks = DefaultMaximumCompletedTicks)
    {
        RunName = runName;
        ShipCount = shipCount;
        Seed = seed;
        RulesetVersion = rulesetVersion;
        WorldSize = worldSize;
        MaximumHealth = maximumHealth;
        StagingTicks = stagingTicks;
        SpatialCellSize = spatialCellSize;
        SpatialMargin = spatialMargin;
        ArgumentOutOfRangeException.ThrowIfZero(maximumCompletedTicks);
        MaximumCompletedTicks = maximumCompletedTicks;
    }

    public string RunName { get; }
    public int ShipCount { get; }
    public ulong Seed { get; }
    public uint RulesetVersion { get; }
    public float WorldSize { get; }
    public uint MaximumHealth { get; }
    public ushort StagingTicks { get; }
    public float SpatialCellSize { get; }
    public float SpatialMargin { get; }
    public ulong MaximumCompletedTicks { get; }
}
