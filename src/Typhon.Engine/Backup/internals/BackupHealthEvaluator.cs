using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// What the health verdict is computed from (plan 1416 §3.5): the schedule, the store as last read, the session's attempts, the drills, the archive. The
/// engine fills it each tick; a reader of a stopped engine's status file rebuilds it from the file (DL-23), so time rules are judged at read time.
/// <see cref="DateTime.MinValue"/> means "none" for every time.
/// </summary>
internal readonly record struct HealthInputs
{
    /// <summary>Backup is configured.</summary>
    public bool Configured { get; init; }

    /// <summary>The schedule; zero = none, and the age rules are off.</summary>
    public TimeSpan Interval { get; init; }

    /// <summary>Zero = drills not monitored.</summary>
    public TimeSpan DrillWindow { get; init; }

    /// <summary>When the scheduler started: the age of "no point yet".</summary>
    public DateTime StartedUtc { get; init; }

    /// <summary>The store was read at least once: the store facts below mean something.</summary>
    public bool StoreKnown { get; init; }

    /// <summary>The store's restorable points (any timeline).</summary>
    public int RestorablePoints { get; init; }

    /// <summary>The newest restorable point of the engine's timeline: its tail end; none = MinValue.</summary>
    public DateTime LastSuccessUtc { get; init; }

    /// <summary>That point's CheckpointLSN, for archive gap coverage.</summary>
    public long LastSuccessCheckpointLsn { get; init; }

    /// <summary>A backup attempt of this session has ended (success or failure).</summary>
    public bool AttemptEnded { get; init; }

    public int ConsecutiveFailures { get; init; }

    /// <summary>Since when the store has been failing (I/O on capture, refresh, retention); none = MinValue.</summary>
    public DateTime StoreFailingSinceUtc { get; init; }

    /// <summary>The engine refuses to capture until its state changes (write tracking off, an engine fault).</summary>
    public bool CaptureRefused { get; init; }

    public WalArchiveCondition ArchiveCondition { get; init; }

    public long ArchiveLastGapToLsn { get; init; }

    public double ArchiveLagSeconds { get; init; }

    /// <summary>The archive's maximum segment age; zero when archiving is off.</summary>
    public TimeSpan ArchiveMaxSegmentAge { get; init; }

    /// <summary>The newest drill's start; none = MinValue.</summary>
    public DateTime LastDrillUtc { get; init; }

    /// <summary>The newest drill failed because of the point (restore, open or check), not because the drill could not run.</summary>
    public bool LastDrillFailed { get; init; }

    /// <summary>Some drill passed.</summary>
    public bool AnyDrillPassed { get; init; }

    /// <summary>The newest point has suspect pages its own occupancy marks allocated.</summary>
    public bool LastPointSuspectAllocated { get; init; }

    public BackupRetentionBlock RetentionBlocked { get; init; }

    public bool RetentionFailed { get; init; }

    /// <summary>The last retention's kept-point check failed (R1): a kept point no longer resolves.</summary>
    public bool KeptPointBroken { get; init; }

    /// <summary>A compaction of the last retention found a source pack of a kept chain damaged.</summary>
    public bool KeptPointDamaged { get; init; }
}

/// <summary>
/// The backup health verdict (07 §6 as corrected by plan 1416 §3.5, BKP-15): the worst condition that holds, with its reason. Pure: the engine, the
/// status-file reader and the Workbench all judge with it.
/// </summary>
internal static class BackupHealthEvaluator
{
    /// <summary>Failures in a row that make health Failing.</summary>
    public const int FailingAfterFailures = 3;

    /// <summary>A point older than this many intervals is stale; an archive lagging this many segment ages lags.</summary>
    public const double StaleFactor = 2;

    /// <summary>The verdict and its reason. Allocates nothing: the scheduler judges every second.</summary>
    public static (BackupHealth Health, BackupHealthReason Reason) Evaluate(in HealthInputs i, DateTime utcNow)
    {
        if (!i.Configured)
        {
            return (BackupHealth.Off, BackupHealthReason.NotConfigured);
        }

        var scheduled = i.Interval > TimeSpan.Zero;

        // Failing.
        if (i.StoreKnown && i.RestorablePoints == 0 && i.AttemptEnded)
        {
            return (BackupHealth.Failing, BackupHealthReason.NoRestorablePoint);
        }

        if (i.ConsecutiveFailures >= FailingAfterFailures)
        {
            return (BackupHealth.Failing, BackupHealthReason.ConsecutiveFailures);
        }

        if (scheduled && i.StoreFailingSinceUtc != DateTime.MinValue && utcNow - i.StoreFailingSinceUtc > i.Interval)
        {
            return (BackupHealth.Failing, BackupHealthReason.StoreUnreachable);
        }

        if (i.CaptureRefused)
        {
            return (BackupHealth.Failing, BackupHealthReason.CaptureRefused);
        }

        if (i.ArchiveCondition is WalArchiveCondition.Diverged or WalArchiveCondition.Failed)
        {
            return (BackupHealth.Failing, BackupHealthReason.ArchiveFailed);
        }

        // DL-7: a retention that broke a point it promised to keep is a defect.
        if (i.KeptPointBroken)
        {
            return (BackupHealth.Failing, BackupHealthReason.KeptPointBroken);
        }

        // Degraded: every point still restorable, with reduced coverage or assurance.
        if (scheduled)
        {
            var since = StaleSince(i);
            if (since != DateTime.MinValue && utcNow - since > i.Interval * StaleFactor)
            {
                return (BackupHealth.Degraded, BackupHealthReason.Stale);
            }
        }

        // A gap is covered once a point of this timeline has its CheckpointLSN past the gap's end (the archiver keeps no "gap open" flag, §6).
        if (i.ArchiveCondition == WalArchiveCondition.Released || (i.ArchiveLastGapToLsn > 0 && i.ArchiveLastGapToLsn >= i.LastSuccessCheckpointLsn))
        {
            return (BackupHealth.Degraded, BackupHealthReason.ArchiveGap);
        }

        if (i.ArchiveCondition == WalArchiveCondition.Stalled
            || (i.ArchiveMaxSegmentAge > TimeSpan.Zero && i.ArchiveLagSeconds > i.ArchiveMaxSegmentAge.TotalSeconds * StaleFactor))
        {
            return (BackupHealth.Degraded, BackupHealthReason.ArchiveLag);
        }

        if (i.LastDrillFailed && !i.AnyDrillPassed)
        {
            return (BackupHealth.Degraded, BackupHealthReason.DrillFailed);
        }

        if (i.KeptPointDamaged)
        {
            return (BackupHealth.Degraded, BackupHealthReason.KeptPointDamaged);
        }

        // Warning: restorable, something needs attention. Never Ok while this timeline has no point (BKP-15), attempted or not.
        if (i.LastSuccessUtc == DateTime.MinValue)
        {
            return (BackupHealth.Warning, BackupHealthReason.FirstPointPending);
        }

        if (i.LastPointSuspectAllocated)
        {
            return (BackupHealth.Warning, BackupHealthReason.LastPointSuspect);
        }

        if (i.RetentionFailed)
        {
            return (BackupHealth.Warning, BackupHealthReason.RetentionFailed);
        }

        if (i.RetentionBlocked != BackupRetentionBlock.None)
        {
            return (BackupHealth.Warning, BackupHealthReason.RetentionBlocked);
        }

        if (i.DrillWindow > TimeSpan.Zero && (i.LastDrillUtc == DateTime.MinValue || utcNow - i.LastDrillUtc > i.DrillWindow))
        {
            return (BackupHealth.Warning, BackupHealthReason.DrillStale);
        }

        if (i.LastDrillFailed)
        {
            return (BackupHealth.Warning, BackupHealthReason.DrillFailedOlderOk);
        }

        return (BackupHealth.Ok, BackupHealthReason.None);
    }

    /// <summary>The reason in one sentence, for the status and the CLI.</summary>
    public static string Explain(in HealthInputs i, BackupHealthReason reason, DateTime utcNow) => reason switch
    {
        BackupHealthReason.None => "the newest point is restorable and current",
        BackupHealthReason.NotConfigured => "backup is not configured",
        BackupHealthReason.NoRestorablePoint => "the store holds no restorable point",
        BackupHealthReason.ConsecutiveFailures => $"{i.ConsecutiveFailures} backups in a row failed",
        BackupHealthReason.StoreUnreachable => $"the store has been failing since {i.StoreFailingSinceUtc:u}",
        BackupHealthReason.CaptureRefused => "the engine refuses to capture a point (write tracking off, or an engine fault)",
        BackupHealthReason.ArchiveFailed => $"the WAL archive is {i.ArchiveCondition}",
        BackupHealthReason.Stale => i.LastSuccessUtc == DateTime.MinValue
            ? $"no point of this timeline since the engine started ({StaleSince(i):u})"
            : $"the newest point of this timeline is from {i.LastSuccessUtc:u}, older than {StaleFactor} × the interval",
        BackupHealthReason.ArchiveGap => i.ArchiveCondition == WalArchiveCondition.Released
            ? "the WAL archive's backlog limit released it: segments are reclaimed un-archived"
            : $"the WAL archive misses LSNs up to {i.ArchiveLastGapToLsn}, and no later point covers them",
        BackupHealthReason.ArchiveLag => i.ArchiveCondition == WalArchiveCondition.Stalled
            ? "the WAL archive is stalled: its store is failing"
            : $"the WAL archive lags {i.ArchiveLagSeconds:0} s",
        BackupHealthReason.DrillFailed => "the newest drill failed and none ever passed",
        BackupHealthReason.FirstPointPending => "this timeline has no point yet",
        BackupHealthReason.LastPointSuspect => "the newest point holds pages the live database had damaged",
        BackupHealthReason.RetentionFailed => "the last retention failed",
        BackupHealthReason.RetentionBlocked => $"the last retention could not meet its policy ({i.RetentionBlocked})",
        BackupHealthReason.DrillStale => i.LastDrillUtc == DateTime.MinValue
            ? "no drill has run"
            : $"the newest drill is from {i.LastDrillUtc:u}, older than the drill window",
        BackupHealthReason.DrillFailedOlderOk => "the newest drill failed; an older one passed",
        BackupHealthReason.KeptPointBroken => "the last retention left a point it keeps unrestorable: a defect, report it",
        BackupHealthReason.KeptPointDamaged => "a compaction found a pack of a kept point's chain damaged: that point may not restore (verify --level full)",
        _ => reason.ToString(),
    };

    /// <summary>The age a stale check measures from: the newest point of this timeline, or the scheduler's start when there is none.</summary>
    private static DateTime StaleSince(in HealthInputs i) => i.LastSuccessUtc != DateTime.MinValue ? i.LastSuccessUtc : i.StartedUtc;
}
