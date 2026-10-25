using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// The engine's backup health (<see cref="IBackupService.Status"/>) as an <see cref="ITyphonHealthCheck"/>: Off and Ok are Healthy, Warning and Degraded
/// are Degraded, Failing is Unhealthy. The description is the status's reason. Reads the published snapshot: nothing is computed or locked.
/// </summary>
/// <remarks>
/// Off is Healthy here and exit 3 for <c>typhon backup status</c>, deliberately: this check answers "is the service healthy" (an engine with no backup
/// configured is not failing), the CLI "does this database have a backup I can trust" (one with none does not).
/// </remarks>
[PublicAPI]
public sealed class BackupHealthCheck : ITyphonHealthCheck
{
    private readonly DatabaseEngine _engine;

    /// <summary>Creates the check over <paramref name="engine"/>.</summary>
    /// <param name="engine">The engine whose backups are judged.</param>
    public BackupHealthCheck(DatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <inheritdoc />
    public HealthStatus CheckHealth() => Map(_engine.Backup.Status.Health);

    /// <inheritdoc />
    public HealthCheckResult GetDetailedResult()
    {
        var status = _engine.Backup.Status;
        return new HealthCheckResult
        {
            Status = Map(status.Health),
            Description = $"Backup {status.Health}: {status.ReasonText}",
            Data = new Dictionary<string, object>
            {
                ["health"] = status.Health.ToString(),
                ["reason"] = status.Reason.ToString(),
                ["lastSuccessUtc"] = status.LastSuccessUtc,
                ["consecutiveFailures"] = status.ConsecutiveFailures,
                ["restorablePoints"] = status.RestorablePoints,
            },
        };
    }

    /// <summary>The health check's status for a backup health.</summary>
    public static HealthStatus Map(BackupHealth health) => health switch
    {
        BackupHealth.Off or BackupHealth.Ok => HealthStatus.Healthy,
        BackupHealth.Warning or BackupHealth.Degraded => HealthStatus.Degraded,
        _ => HealthStatus.Unhealthy,
    };
}
