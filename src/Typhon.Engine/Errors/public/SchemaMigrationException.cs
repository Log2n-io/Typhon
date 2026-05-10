using System;
using System.Collections.Generic;
using System.Text;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Records a single entity that failed during migration, including its primary key, a hex dump of the old data, and the exception.
/// </summary>
[PublicAPI]
public readonly struct MigrationFailure
{
    /// <summary>ChunkId (logical entity identifier) of the failed entity.</summary>
    public int ChunkId { get; init; }

    /// <summary>Hex dump of the old component bytes (for diagnostic inspection).</summary>
    public string OldDataHex { get; init; }

    /// <summary>The exception thrown by the migration function.</summary>
    public Exception Exception { get; init; }
}

/// <summary>
/// Thrown when one or more entities fail during schema migration.
/// Old segments remain untouched — the user can fix the migration function and re-run.
/// </summary>
[PublicAPI]
public class SchemaMigrationException : TyphonException
{
    /// <summary>The component schema name being migrated.</summary>
    public string ComponentName { get; }

    /// <summary>Total number of entities that failed migration.</summary>
    public int FailedEntityCount => Failures.Count;

    /// <summary>Detailed failure records for each entity.</summary>
    public IReadOnlyList<MigrationFailure> Failures { get; }

    public SchemaMigrationException(string componentName, IReadOnlyList<MigrationFailure> failures) : 
        base(TyphonErrorCode.SchemaMigration, FormatMessage(componentName, failures))
    {
        ComponentName = componentName;
        Failures = failures;
    }

    private static string FormatMessage(string componentName, IReadOnlyList<MigrationFailure> failures)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Schema migration for '{componentName}' failed: {failures.Count} entity/entities could not be migrated.");
        sb.AppendLine("Old segments are untouched — fix the migration function and restart.");
        sb.AppendLine();

        var displayCount = Math.Min(failures.Count, 10);
        for (int i = 0; i < displayCount; i++)
        {
            var f = failures[i];
            sb.AppendLine($"  ChunkId={f.ChunkId}: {f.Exception.GetType().Name}: {f.Exception.Message}");
            sb.AppendLine($"    Old data: {f.OldDataHex}");
        }

        if (failures.Count > displayCount)
        {
            sb.AppendLine($"  ... and {failures.Count - displayCount} more failures.");
        }

        return sb.ToString();
    }
}
