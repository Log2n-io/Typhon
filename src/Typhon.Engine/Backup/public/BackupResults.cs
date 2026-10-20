using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine;

/// <summary>The kind of a backup point.</summary>
[PublicAPI]
public enum BackupPointKind
{
    /// <summary>Every page of the image; the base of a chain.</summary>
    Full = 1,

    /// <summary>The pages written since its parent's freeze, and the meta pair.</summary>
    Incremental = 2,

    /// <summary>A base built from a chain inside the store (#1415).</summary>
    Compacted = 3,
}

/// <summary>One backup point, as its pack's header and footer describe it.</summary>
/// <param name="BackupId">The point's id in its store; never reused.</param>
/// <param name="Kind">Full, incremental or compacted.</param>
/// <param name="ChainId">The chain it belongs to.</param>
/// <param name="ParentId">The point it extends; 0 for a base.</param>
/// <param name="BaseId">The base of its chain.</param>
/// <param name="CheckpointLsn"><c>C</c>: where the restored database's replay starts.</param>
/// <param name="PointLsn"><c>P</c>: the last LSN the restored database holds.</param>
/// <param name="ImagePages">The data file's length in pages at the freeze.</param>
/// <param name="PageCount">The pages the pack itself holds.</param>
/// <param name="TailSegments">The WAL segments it carries.</param>
/// <param name="Suspect">The live database had damaged pages at capture; the point keeps them faithfully.</param>
/// <param name="Offline">Captured from a closed database.</param>
/// <param name="FreezeUtc">The freeze instant.</param>
/// <param name="TailEndUtc">When its tail copy ended.</param>
/// <param name="TimelineId">The WAL timeline of the database it was taken from.</param>
/// <param name="FormatRevision">The image's on-disk format revision.</param>
/// <param name="PackFile">The pack's file name in <c>packs/</c>.</param>
/// <param name="PackBytes">The pack's size.</param>
[PublicAPI]
public sealed record BackupPointSummary(uint BackupId, BackupPointKind Kind, Guid ChainId, uint ParentId, uint BaseId, long CheckpointLsn, long PointLsn,
    int ImagePages, int PageCount, int TailSegments, bool Suspect, bool Offline, DateTime FreezeUtc, DateTime TailEndUtc, Guid TimelineId,
    int FormatRevision, string PackFile, long PackBytes);

/// <summary>Options of <see cref="BackupOperations.Create"/>.</summary>
[PublicAPI]
public sealed class BackupCreateOptions
{
    /// <summary>Force a full point even when an incremental would do.</summary>
    public bool Full { get; init; }

    /// <summary>The database's WAL directory when it is not <c>{bundle}/wal</c> (its engine was configured with an explicit one).</summary>
    public string WalDirectory { get; init; }

    /// <summary>Stops the capture; nothing is published.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>Where the store logs.</summary>
    public ILogger Logger { get; init; }
}

/// <summary>Options of <see cref="BackupOperations.Restore"/>.</summary>
[PublicAPI]
public sealed class BackupRestoreOptions
{
    /// <summary>Move an existing target aside to <c>{name}.typhon.pre-restore-{utc}</c> instead of refusing. A database is never deleted.</summary>
    public bool Replace { get; init; }

    /// <summary>Restore an image of another on-disk format revision, for another build's environment. Refused by default.</summary>
    public bool AllowFormatMismatch { get; init; }

    /// <summary>Forensics only: write pages that fail their check and list them. The open will typically refuse such a database.</summary>
    public bool AllowDamaged { get; init; }

    /// <summary>Called with the restored bundle once published, to open it (recovery runs) and close it.</summary>
    public Action<string> OpenAndClose { get; init; }

    /// <summary>Read-window memory, split across the chain's packs.</summary>
    public long ReadBudgetBytes { get; init; } = 64L << 20;

    /// <summary>Stops the restore before its publish; the staging is discarded.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>Called with the pages written so far and the image's total.</summary>
    public Action<long, long> Progress { get; init; }

    /// <summary>Where the restore logs.</summary>
    public ILogger Logger { get; init; }
}

/// <summary>What a restore did.</summary>
/// <param name="Point">The point restored.</param>
/// <param name="Target">The restored bundle.</param>
/// <param name="DisplacedBundle">Where the replaced bundle now is, or <c>null</c>.</param>
/// <param name="ChainDepth">The packs read.</param>
/// <param name="PagesWritten">The image's pages.</param>
/// <param name="ZeroPages">Pages written as zeros (never-written pages and all-zero ones).</param>
/// <param name="TailSegments">WAL segments recreated.</param>
/// <param name="BytesRead">Bytes read from the store.</param>
/// <param name="BytesWritten">Bytes written to the target.</param>
/// <param name="Duration">Wall time, settle excluded.</param>
/// <param name="SuspectPages">Pages the LIVE database had damaged at capture: a warning, not a defect of the backup.</param>
/// <param name="DamagedPages">Pages written despite failing their check (only with <see cref="BackupRestoreOptions.AllowDamaged"/>).</param>
/// <param name="NewTimelineId">The WAL timeline the restored database starts at its first open.</param>
/// <param name="SettleError">
/// What <see cref="BackupRestoreOptions.OpenAndClose"/> threw, or <c>null</c>. The restore is published either way: the target and the displaced
/// bundle are where this result says.
/// </param>
[PublicAPI]
public sealed record BackupRestoreResult(BackupPointSummary Point, string Target, string DisplacedBundle, int ChainDepth, int PagesWritten, int ZeroPages,
    int TailSegments, long BytesRead, long BytesWritten, TimeSpan Duration, IReadOnlyList<uint> SuspectPages, IReadOnlyList<PackPageDamage> DamagedPages,
    Guid NewTimelineId, string SettleError);

/// <summary>How deep a verify reads.</summary>
[PublicAPI]
public enum BackupVerifyLevel
{
    /// <summary>Headers, footers, indexes, tails and chains: milliseconds per pack plus its tail.</summary>
    Quick,

    /// <summary>Quick, plus every byte's file CRC, every page decoded and checked, and each point's meta pair against its header.</summary>
    Full,

    /// <summary>
    /// Full, plus the integrity scan of a point's image (of the point verified, or of the store's newest restorable one): an image the scan cannot
    /// traverse is not restorable; damage the image holds (the live database's) is a warning. The drill is what proves the point opens.
    /// </summary>
    Deep,
}

/// <summary>A verify's overall answer; <c>typhon backup verify</c> maps it to exit 0, 1, 2, 3.</summary>
[PublicAPI]
public enum BackupVerifyVerdict
{
    /// <summary>Nothing to report.</summary>
    Ok,

    /// <summary>Advisories only (an orphan temp, a suspect point): every point is restorable.</summary>
    Warning,

    /// <summary>Some pack is damaged or missing; some points are not restorable.</summary>
    Damaged,

    /// <summary>No point of the scope can be restored.</summary>
    NoRestorablePoint,
}

/// <summary>Whether a finding costs a restore.</summary>
[PublicAPI]
public enum BackupFindingSeverity
{
    /// <summary>Worth knowing; nothing is lost.</summary>
    Advisory,

    /// <summary>Bytes are wrong or missing: every point that needs them is not restorable.</summary>
    Damage,
}

/// <summary>A pack's state after a verify.</summary>
[PublicAPI]
public enum BackupPackStatus
{
    /// <summary>Every check passed (advisories aside).</summary>
    Ok,

    /// <summary>Some part of the pack failed its check.</summary>
    Damaged,

    /// <summary>Its header or footer cannot be read: nothing of it is usable.</summary>
    Unreadable,
}

/// <summary>One verify finding, coded <c>BKP-VER-nn</c>.</summary>
/// <param name="Code">The check that reported it.</param>
/// <param name="Severity">Advisory or damage.</param>
/// <param name="File">The pack (or temp) concerned.</param>
/// <param name="PageId">The page concerned, or <c>-1</c>.</param>
/// <param name="Message">What was found.</param>
[PublicAPI]
public sealed record BackupVerifyFinding(string Code, BackupFindingSeverity Severity, string File, long PageId, string Message);

/// <summary>One pack, verified.</summary>
/// <param name="File">Its file name.</param>
/// <param name="BackupId">Its point id.</param>
/// <param name="Status">Ok, damaged or unreadable.</param>
/// <param name="Findings">What was found.</param>
[PublicAPI]
public sealed record BackupPackVerification(string File, uint BackupId, BackupPackStatus Status, IReadOnlyList<BackupVerifyFinding> Findings);

/// <summary>One point, verified: restorable iff its chain resolves and every pack of it verified without damage.</summary>
/// <param name="BackupId">The point.</param>
/// <param name="Restorable">Whether a restore of it can succeed.</param>
/// <param name="BrokenBy">The points whose packs break its chain.</param>
[PublicAPI]
public sealed record BackupPointVerification(uint BackupId, bool Restorable, IReadOnlyList<uint> BrokenBy);

/// <summary>What a verify found.</summary>
/// <param name="Level">Quick or full.</param>
/// <param name="Verdict">The overall answer.</param>
/// <param name="Packs">Every pack verified.</param>
/// <param name="Points">Every point in scope.</param>
/// <param name="StoreFindings">Findings about the store or about chain links.</param>
/// <param name="Duration">Wall time.</param>
/// <param name="BytesRead">Bytes read from the store.</param>
/// <param name="RemovedPoints">Points a concurrent retention deleted while the verify ran: left out, not damage.</param>
[PublicAPI]
public sealed record BackupVerifyReport(BackupVerifyLevel Level, BackupVerifyVerdict Verdict, IReadOnlyList<BackupPackVerification> Packs,
    IReadOnlyList<BackupPointVerification> Points, IReadOnlyList<BackupVerifyFinding> StoreFindings, TimeSpan Duration, long BytesRead,
    int RemovedPoints);

/// <summary>A chain's head in a store.</summary>
/// <param name="ChainId">The chain.</param>
/// <param name="BaseId">Its base point (0 when the base pack is gone).</param>
/// <param name="HeadBackupId">Its newest point.</param>
/// <param name="Depth">Its points in the store (a compacted point counts once).</param>
/// <param name="HeadDepth">The packs a restore of its head reads, a compacted base preferred; 0 when the head does not resolve.</param>
[PublicAPI]
public sealed record BackupChainSummary(Guid ChainId, uint BaseId, uint HeadBackupId, int Depth, int HeadDepth);

/// <summary>What a store holds.</summary>
/// <param name="StoreId">The store's identity.</param>
/// <param name="DatabaseId">The database it belongs to.</param>
/// <param name="Points">Every readable point, ascending id.</param>
/// <param name="Chains">Every chain's head.</param>
/// <param name="UnreadablePacks">Pack files whose header or footer cannot be read, with why.</param>
/// <param name="TemporaryFiles">In-progress or abandoned temps.</param>
/// <param name="RestorablePoints">The restorable points by a quick verify, or <c>null</c> when not asked for.</param>
/// <param name="Drills">Each drilled point's newest drill (a drill that could not run is not one), by point id.</param>
[PublicAPI]
public sealed record BackupStoreListing(Guid StoreId, Guid DatabaseId, IReadOnlyList<BackupPointSummary> Points, IReadOnlyList<BackupChainSummary> Chains,
    IReadOnlyList<KeyValuePair<string, string>> UnreadablePacks, IReadOnlyList<string> TemporaryFiles, IReadOnlyList<uint> RestorablePoints,
    IReadOnlyDictionary<uint, BackupDrillSummary> Drills);

/// <summary>
/// Backup health, one verdict (07 §6): a store's from its packs (<see cref="BackupOperations.Status"/>), an engine's from its schedule, its store and its
/// drills (<see cref="BackupStatus.Health"/>). <c>typhon backup status</c> maps it to its exit code: Ok 0, Warning 1, Degraded 1, Failing 3, Off 3.
/// </summary>
[PublicAPI]
public enum BackupHealth
{
    /// <summary>Backup is not configured.</summary>
    Off = 0,

    /// <summary>The newest point is restorable and nothing needs attention.</summary>
    Ok,

    /// <summary>
    /// Restorable, but something needs attention: a suspect newest point, a newest point that is not restorable while an older one is, temps left, retention
    /// blocked, a drill overdue.
    /// </summary>
    Warning,

    /// <summary>
    /// Every point is still restorable, with reduced coverage or assurance: the backups are stale, the archive lags or has a gap, a drill failed.
    /// </summary>
    Degraded,

    /// <summary>No point can be restored, or backups keep failing.</summary>
    Failing,
}

/// <summary>A store's state in one line.</summary>
/// <param name="Health">The verdict.</param>
/// <param name="Reason">Why, in one sentence.</param>
/// <param name="Points">Points in the store.</param>
/// <param name="Latest">The newest point, or <c>null</c>.</param>
/// <param name="LatestRestorable">The newest point whose chain resolves (from the packs' headers and footers), or <c>null</c>.</param>
[PublicAPI]
public sealed record BackupStoreStatus(BackupHealth Health, string Reason, int Points, BackupPointSummary Latest, BackupPointSummary LatestRestorable);
