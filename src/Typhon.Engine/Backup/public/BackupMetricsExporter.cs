using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace Typhon.Engine;

/// <summary>
/// The engine's backup metrics for OpenTelemetry (07 §6, plan 1416 §4.4, DL-21): gauges and counters on a <see cref="Meter"/> named
/// <see cref="MeterName"/>, every one observable — read from the published <see cref="IBackupService.Status"/> when a consumer polls, never on an engine
/// path. A gauge is exported as a gauge: the resource tree's <see cref="IMetricSource"/> carries only counters and durations, which an exporter would
/// turn every age and depth into a monotonic counter.
/// </summary>
[PublicAPI]
[ExcludeFromCodeCoverage]
public sealed class BackupMetricsExporter : IDisposable
{
    /// <summary>The meter's name.</summary>
    public const string MeterName = "Typhon.Backup";

    /// <summary>The meter's version.</summary>
    public const string MeterVersion = "1.0.0";

    private readonly DatabaseEngine _engine;
    private readonly Meter _meter;

    /// <summary>Creates the exporter and registers the instruments.</summary>
    /// <param name="engine">The engine whose backups are reported.</param>
    public BackupMetricsExporter(DatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _meter = new Meter(MeterName, MeterVersion);
        Register();
    }

    /// <summary>The meter owning the instruments.</summary>
    public Meter Meter => _meter;

    /// <summary>A function of the status, to read on demand.</summary>
    private BackupStatus Status => _engine.Backup.Status;

    private void Register()
    {
        _meter.CreateObservableGauge("typhon.backup.health", () => (int)Status.Health, "{state}", "Backup health: 0 off, 1 ok, 2 warning, 3 degraded, 4 failing");
        _meter.CreateObservableGauge("typhon.backup.last_success_age_seconds", () => Age(Status.LastSuccessUtc), "s",
            "Seconds since the newest point of this database's timeline (none: no measurement)");
        _meter.CreateObservableGauge("typhon.backup.last_duration_ms", () => Last(m => m.DurationUs / 1000.0), "ms", "The last point's duration");
        _meter.CreateObservableGauge("typhon.backup.last_bytes", () => Last(m => m.Bytes), "By", "The last point's pack size");
        _meter.CreateObservableGauge("typhon.backup.last_pages", () => Last(m => m.Pages), "{pages}", "The pages the last point's pack holds");
        _meter.CreateObservableGauge("typhon.backup.consecutive_failures", () => Status.ConsecutiveFailures, "{failures}", "Failed backups in a row");
        _meter.CreateObservableGauge("typhon.backup.chain_depth", () => Status.ChainDepth, "{packs}", "The packs a restore of the newest point reads");
        _meter.CreateObservableGauge("typhon.backup.store_bytes", () => Status.StoreBytes, "By", "The store's pack bytes");
        _meter.CreateObservableGauge("typhon.backup.restorable_points", () => Status.RestorablePoints, "{points}", "The store's restorable points");
        _meter.CreateObservableGauge("typhon.backup.last_drill_age_seconds", () => Age(Status.LastDrill?.Utc ?? DateTime.MinValue), "s",
            "Seconds since the newest drill (none: no measurement)");
        _meter.CreateObservableGauge("typhon.backup.last_drill_ok", LastDrillOk, "1", "1 when the newest drill passed, 0 when it failed (none: no measurement)");
        _meter.CreateObservableGauge("typhon.backup.archive.lag_bytes", () => Archive(a => a.LagBytes), "By", "Durable WAL bytes not archived yet");
        _meter.CreateObservableGauge("typhon.backup.archive.lag_seconds", () => Archive(a => a.LagSeconds), "s", "The archive's lag: the RPO with archiving on");
        _meter.CreateObservableGauge("typhon.backup.archive.archived_through_lsn", () => Archive(a => a.ArchivedThroughLsn), "{lsn}",
            "Every LSN up to this one is archived");
        _meter.CreateObservableGauge("typhon.backup.rpo_seconds", Rpo, "s",
            "The recovery point objective now: the archive's lag with archiving on, else the age of the newest point's tail");
        _meter.CreateObservableCounter("typhon.backup.points_total", () => Status.PointsTotal, "{points}", "Points published this session");
        _meter.CreateObservableCounter("typhon.backup.failures_total", () => Status.FailuresTotal, "{failures}", "Backups failed this session");
        _meter.CreateObservableCounter("typhon.backup.archive.gaps_total", () => Status.Archive.GapsTotal, "{gaps}", "Archive gaps recorded this session");
    }

    private static IEnumerable<Measurement<double>> Age(DateTime utc) =>
        utc == DateTime.MinValue ? [] : [new Measurement<double>((DateTime.UtcNow - utc).TotalSeconds)];

    private IEnumerable<Measurement<double>> Last(Func<(long DurationUs, long Bytes, long Pages), double> value)
    {
        var scheduler = _engine.BackupScheduler;
        return scheduler == null || Status.PointsTotal == 0 ? [] : [new Measurement<double>(value(scheduler.LastPointMetrics))];
    }

    private IEnumerable<Measurement<int>> LastDrillOk()
    {
        var drill = Status.LastDrill;
        return drill == null ? [] : [new Measurement<int>(drill.Outcome is BackupDrillOutcome.Passed or BackupDrillOutcome.PassedWithWarnings ? 1 : 0)];
    }

    private IEnumerable<Measurement<double>> Archive(Func<BackupArchiveStatus, double> value)
    {
        var archive = Status.Archive;
        return archive.State == BackupArchiveState.Off ? [] : [new Measurement<double>(value(archive))];
    }

    private IEnumerable<Measurement<double>> Rpo()
    {
        var status = Status;
        if (status.Archive.State != BackupArchiveState.Off)
        {
            return [new Measurement<double>(status.Archive.LagSeconds)];
        }

        return status.LastPoint == null ? [] : [new Measurement<double>((DateTime.UtcNow - status.LastPoint.TailEndUtc).TotalSeconds)];
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
