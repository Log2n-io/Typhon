using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Describes the current phase of an in-progress schema migration.
/// </summary>
[PublicAPI]
public enum MigrationPhase
{
    Analyzing,
    AllocatingSegments,
    MigratingEntities,
    RecreatingRevisionChain,
    BuildingNewIndexes,
    UpdatingMetadata,
    Flushing,
    Complete,
}

/// <summary>
/// Progress event data raised during schema migration for operational monitoring.
/// </summary>
[PublicAPI]
public class MigrationProgressEventArgs : EventArgs
{
    public string ComponentName { get; init; }
    public MigrationPhase Phase { get; init; }
    public long EntitiesMigrated { get; init; }
    public long TotalEntities { get; init; }
    public double PercentComplete { get; init; }
    public TimeSpan Elapsed { get; init; }
    public TimeSpan EstimatedRemaining { get; init; }
}
