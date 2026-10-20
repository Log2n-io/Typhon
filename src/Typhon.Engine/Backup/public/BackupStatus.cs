using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>Why <see cref="BackupStatus.Health"/> is what it is: the worst condition that holds (plan 1416 §3.5).</summary>
[PublicAPI]
public enum BackupHealthReason
{
    /// <summary>Nothing needs attention.</summary>
    None = 0,

    /// <summary>Backup is not configured (<see cref="BackupHealth.Off"/>).</summary>
    NotConfigured,

    /// <summary>The store holds no restorable point, and a backup of this session has been attempted.</summary>
    NoRestorablePoint,

    /// <summary>Three or more backups in a row failed.</summary>
    ConsecutiveFailures,

    /// <summary>The store has been failing for longer than the interval.</summary>
    StoreUnreachable,

    /// <summary>The engine refuses to capture (write tracking switched off, a fault): no retry helps until that changes.</summary>
    CaptureRefused,

    /// <summary>The newest point of this timeline is older than twice the interval.</summary>
    Stale,

    /// <summary>The WAL archive diverged from this database's history, or refused to start.</summary>
    ArchiveFailed,

    /// <summary>The WAL archive misses a range no later point covers, or the backlog limit released it.</summary>
    ArchiveGap,

    /// <summary>The WAL archive is stalled, or lags more than twice its maximum segment age.</summary>
    ArchiveLag,

    /// <summary>The newest drill failed, and no drill ever passed.</summary>
    DrillFailed,

    /// <summary>This timeline has no point yet: a fresh or just-restored database.</summary>
    FirstPointPending,

    /// <summary>The newest point holds pages the live database had damaged, on pages its own occupancy marks allocated.</summary>
    LastPointSuspect,

    /// <summary>The last retention could not meet its policy without breaking MinKeep or KeepLastDrilled, or a chain is damaged.</summary>
    RetentionBlocked,

    /// <summary>The last retention failed.</summary>
    RetentionFailed,

    /// <summary>The newest drill is older than <see cref="BackupOptions.DrillWindow"/>, or there is none.</summary>
    DrillStale,

    /// <summary>The newest drill failed, but an older one passed.</summary>
    DrillFailedOlderOk,
}

/// <summary>Whether the engine's backup thread is working.</summary>
[PublicAPI]
public enum BackupRunState
{
    /// <summary>No backup job runs.</summary>
    Idle = 0,

    /// <summary>A job runs: see <see cref="BackupStatus.Phase"/>.</summary>
    Running,

    /// <summary>The engine closed.</summary>
    Stopped,
}

/// <summary>What a running backup job is doing.</summary>
[PublicAPI]
public enum BackupRunPhase
{
    /// <summary>Nothing runs.</summary>
    None = 0,

    /// <summary>Capturing the point: freeze, scan, tail.</summary>
    Capture,

    /// <summary>Verifying the new pack.</summary>
    Verify,

    /// <summary>Applying retention.</summary>
    Retention,

    /// <summary>Compacting a point, inside retention.</summary>
    Compaction,
}

/// <summary>How a drill ended (04 §6).</summary>
[PublicAPI]
public enum BackupDrillOutcome
{
    /// <summary>Restored, opened, closed, and the integrity check found it sound.</summary>
    Passed = 1,

    /// <summary>Passed, with something to note: leaked space, or suspect pages the live database had damaged.</summary>
    PassedWithWarnings,

    /// <summary>The point does not restore, open, or check sound: the backup is not trustworthy.</summary>
    Failed,

    /// <summary>The drill itself could not run (scratch I/O, cancelled): nothing is known about the point.</summary>
    Error,
}

/// <summary>Where a drill stopped (04 §6).</summary>
[PublicAPI]
public enum BackupDrillStage
{
    /// <summary>It did not stop: it ran to its end.</summary>
    None = 0,

    /// <summary>Preparing the scratch directory.</summary>
    Scratch,

    /// <summary>Restoring the point into the scratch bundle.</summary>
    Restore,

    /// <summary>Opening the restored bundle (recovery runs) and closing it.</summary>
    Open,

    /// <summary>The integrity check of the result.</summary>
    Check,
}

/// <summary>A backup failure, as the status reports it.</summary>
/// <param name="Utc">When it ended.</param>
/// <param name="Kind">The exception's type name, or the capture's abort reason.</param>
/// <param name="Message">What failed.</param>
[PublicAPI]
public sealed record BackupFailure(DateTime Utc, string Kind, string Message);

/// <summary>One drill, as the status and the listing report it.</summary>
/// <param name="BackupId">The point drilled.</param>
/// <param name="Utc">When the drill started.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Error">Why it failed, or <c>null</c>.</param>
[PublicAPI]
public sealed record BackupDrillSummary(uint BackupId, DateTime Utc, BackupDrillOutcome Outcome, string Error);

/// <summary>What the WAL archive is doing (#1418): the public mirror of the archiver's condition.</summary>
[PublicAPI]
public enum BackupArchiveState
{
    /// <summary>Archiving is not configured.</summary>
    Off = 0,

    /// <summary>Starting: no state from the store yet.</summary>
    Starting,

    /// <summary>Every durable segment is archived, or being archived.</summary>
    Archiving,

    /// <summary>The store is failing; the archiver retries while the backlog limit holds the segments.</summary>
    Stalled,

    /// <summary>The backlog limit released the floor: segments are reclaimed un-archived, and a gap follows.</summary>
    Released,

    /// <summary>The store holds another history under this timeline (a copied database): archiving stopped.</summary>
    Diverged,

    /// <summary>Refused at start.</summary>
    Failed,
}

/// <summary>The WAL archive's state (#1418).</summary>
/// <param name="State">What it is doing.</param>
/// <param name="TimelineId">The timeline being archived.</param>
/// <param name="ArchivedThroughLsn">Every LSN up to this one is archived; 0 = nothing yet.</param>
/// <param name="LagBytes">Durable WAL bytes not archived yet.</param>
/// <param name="BacklogBytes">WAL bytes held on disk for the archive alone.</param>
/// <param name="LagSeconds">The RPO: 0 when everything durable is archived.</param>
/// <param name="GapsTotal">Gaps recorded this session.</param>
/// <param name="LastGapFromLsn">The last gap's first LSN; 0 = none.</param>
/// <param name="LastGapToLsn">The last gap's last LSN; 0 = none.</param>
/// <param name="LastArchivedSealUtc">When the newest archived segment was sealed; <see cref="DateTime.MinValue"/> when unknown.</param>
/// <param name="ConsecutiveFailures">Store failures in a row.</param>
/// <param name="LastError">The last failure, or <c>null</c>.</param>
[PublicAPI]
public sealed record BackupArchiveStatus(BackupArchiveState State, Guid TimelineId, long ArchivedThroughLsn, long LagBytes, long BacklogBytes,
    double LagSeconds, long GapsTotal, long LastGapFromLsn, long LastGapToLsn, DateTime LastArchivedSealUtc, int ConsecutiveFailures, string LastError)
{
    /// <summary>Archiving off.</summary>
    public static BackupArchiveStatus Off { get; } = new(BackupArchiveState.Off, Guid.Empty, 0, 0, 0, 0, 0, 0, 0, DateTime.MinValue, 0, null);
}

/// <summary>
/// An engine's backups at one instant (<see cref="IBackupService.Status"/>, <c>backup.status.json</c>): a published snapshot, read without a lock.
/// <see cref="DateTime.MinValue"/> means "none" for every time.
/// </summary>
/// <param name="Health">The verdict.</param>
/// <param name="Reason">Why.</param>
/// <param name="ReasonText">Why, in one sentence.</param>
/// <param name="State">Idle, running, stopped.</param>
/// <param name="Phase">What the running job does.</param>
/// <param name="ProgressPages">Pages the running capture has read.</param>
/// <param name="ProgressOfPages">Pages it will read.</param>
/// <param name="ProgressBytes">Bytes the running compaction has read.</param>
/// <param name="LastPoint">The newest point this engine knows of on its timeline, or <c>null</c>.</param>
/// <param name="LastSuccessUtc">When that point's tail copy ended.</param>
/// <param name="NextDueUtc">When the schedule next takes a point (an estimate: the schedule runs on a monotonic clock).</param>
/// <param name="ConsecutiveFailures">Failed backups in a row (refusals that only defer are not counted).</param>
/// <param name="LastFailure">The last failure, or <c>null</c>.</param>
/// <param name="PointsTotal">Points this session published.</param>
/// <param name="FailuresTotal">Backups this session that failed.</param>
/// <param name="CbwCopiesTotal">Copy-before-write copies this session.</param>
/// <param name="CbwAbortsTotal">Captures this session abandoned because a writer could not wait.</param>
/// <param name="SuspectPagesTotal">Suspect pages in the points this session published.</param>
/// <param name="ChainDepth">The packs a restore of the newest point reads.</param>
/// <param name="StoreBytes">The store's pack bytes.</param>
/// <param name="RestorablePoints">The store's restorable points.</param>
/// <param name="FreePageSuspects">Suspect pages of the newest point on pages its occupancy marks free (no warning: nothing there is data).</param>
/// <param name="LastDrill">The newest drill, or <c>null</c>.</param>
/// <param name="LastPassedDrill">The newest drill that passed, or <c>null</c>.</param>
/// <param name="RetentionBlocked">What the last retention could not meet.</param>
/// <param name="Archive">The WAL archive.</param>
/// <param name="LastRequestId">The last <c>backup.request</c> whose job started.</param>
/// <param name="UpdatedUtc">When this snapshot was published.</param>
[PublicAPI]
public sealed record BackupStatus(BackupHealth Health, BackupHealthReason Reason, string ReasonText,
    BackupRunState State, BackupRunPhase Phase, long ProgressPages, long ProgressOfPages, long ProgressBytes,
    BackupPointSummary LastPoint, DateTime LastSuccessUtc, DateTime NextDueUtc,
    int ConsecutiveFailures, BackupFailure LastFailure,
    long PointsTotal, long FailuresTotal, long CbwCopiesTotal, long CbwAbortsTotal, long SuspectPagesTotal,
    int ChainDepth, long StoreBytes, int RestorablePoints, int FreePageSuspects,
    BackupDrillSummary LastDrill, BackupDrillSummary LastPassedDrill, BackupRetentionBlock RetentionBlocked,
    BackupArchiveStatus Archive, Guid LastRequestId, DateTime UpdatedUtc)
{
    /// <summary>Backup not configured.</summary>
    public static BackupStatus Off { get; } = new(BackupHealth.Off, BackupHealthReason.NotConfigured, "backup is not configured", BackupRunState.Idle,
        BackupRunPhase.None, 0, 0, 0, null, DateTime.MinValue, DateTime.MinValue, 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null,
        BackupRetentionBlock.None, BackupArchiveStatus.Off, Guid.Empty, DateTime.MinValue);
}
