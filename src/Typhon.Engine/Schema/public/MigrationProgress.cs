using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Describes the current phase of an in-progress schema migration.
/// </summary>
[PublicAPI]
public enum MigrationPhase
{
    /// <summary>Computing the schema diff and planning the migration.</summary>
    Analyzing,

    /// <summary>Allocating storage segments for the new component layout.</summary>
    AllocatingSegments,

    /// <summary>Copying and transforming entity data into the new layout.</summary>
    MigratingEntities,

    /// <summary>Rebuilding the MVCC revision chain for the migrated component.</summary>
    RecreatingRevisionChain,

    /// <summary>Building indexes for the new layout.</summary>
    BuildingNewIndexes,

    /// <summary>Updating persisted schema metadata to the new revision.</summary>
    UpdatingMetadata,

    /// <summary>Flushing migrated data and metadata to durable storage.</summary>
    Flushing,

    /// <summary>Migration finished.</summary>
    Complete,
}

/// <summary>
/// Progress event data raised during schema migration for operational monitoring.
/// </summary>
[PublicAPI]
public class MigrationProgressEventArgs : EventArgs
{
    /// <summary>Component (schema) name being migrated.</summary>
    public string ComponentName { get; init; }

    /// <summary>Current migration phase.</summary>
    public MigrationPhase Phase { get; init; }

    /// <summary>Number of entities migrated so far.</summary>
    public long EntitiesMigrated { get; init; }

    /// <summary>Total number of entities to migrate.</summary>
    public long TotalEntities { get; init; }

    /// <summary>Overall completion as a percentage in the range <c>0</c> to <c>100</c>.</summary>
    public double PercentComplete { get; init; }

    /// <summary>Wall-clock time elapsed since the migration started.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Estimated wall-clock time remaining, extrapolated from progress so far.</summary>
    public TimeSpan EstimatedRemaining { get; init; }
}
