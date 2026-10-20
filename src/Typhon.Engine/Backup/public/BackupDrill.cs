using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;

namespace Typhon.Engine;

/// <summary>Options of <see cref="BackupOperations.Drill"/> (04 §6, plan 1416 §4.5).</summary>
[PublicAPI]
public sealed class BackupDrillOptions
{
    /// <summary>
    /// Where the point is restored: <c>{ScratchDirectory}/{guid}/{name}.typhon</c>, deleted when the drill ends. Required (DL-25), and refused inside the
    /// store: a mirror of the store would copy a whole image mid-drill. It needs the space of the whole image.
    /// </summary>
    public required string ScratchDirectory { get; init; }

    /// <summary>Leave the scratch bundle in place, for a look at what the drill opened.</summary>
    public bool KeepScratch { get; init; }

    /// <summary>The page cache of the drill's engine, in bytes; 0 = the engine's default.</summary>
    public ulong PageCacheBytes { get; init; }

    /// <summary>
    /// Opens the restored bundle (recovery runs) and closes it cleanly; <c>null</c> = <see cref="BackupOperations.OpenAndCloseRestored"/>. A host whose
    /// database cannot be opened without its own component assemblies passes its own. Whatever it does, the drill checks the result itself: a bundle not
    /// left clean, or an unclean image no recovery ran on, fails the open (the check never runs on an unrecovered image).
    /// </summary>
    public Func<string, BackupSettleResult> OpenAndClose { get; init; }

    /// <summary>Stage, done, total (0, 0 = indeterminate): the restore's pages, the open, the check's own progress.</summary>
    public Action<BackupDrillStage, long, long> Progress { get; init; }

    /// <summary>Write the drill's record into the store's <c>drills/</c> (the history health and retention read).</summary>
    public bool Record { get; init; } = true;

    /// <summary>Stops the drill: an <see cref="BackupDrillOutcome.Error"/>, which says nothing about the point.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>The drill's log, and its engine's.</summary>
    public ILogger Logger { get; init; }
}

/// <summary>What a drill found (04 §6).</summary>
/// <param name="Point">The point drilled.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="FailedStage">Where it stopped (<see cref="BackupDrillStage.None"/> when it ran to its end).</param>
/// <param name="Error">Why it failed, or <c>null</c>.</param>
/// <param name="RestoreTime">The restore into the scratch directory.</param>
/// <param name="OpenTime">The open (recovery) and close.</param>
/// <param name="CheckTime">The deep integrity check.</param>
/// <param name="Recovery">What the open's WAL recovery did; default when none ran.</param>
/// <param name="Verdict">The check's verdict; meaningless unless the check ran.</param>
/// <param name="Findings">The check's findings.</param>
/// <param name="SuspectPages">Pages the live database had damaged at capture, which the point faithfully holds.</param>
/// <param name="RecordFile">The record written, or <c>null</c>.</param>
/// <param name="ScratchBundle">The scratch bundle, when kept; <c>null</c> once deleted.</param>
[PublicAPI]
public sealed record BackupDrillReport(BackupPointSummary Point, BackupDrillOutcome Outcome, BackupDrillStage FailedStage, string Error,
    TimeSpan RestoreTime, TimeSpan OpenTime, TimeSpan CheckTime, WalRecoveryResult Recovery, IntegrityVerdict Verdict, int Findings, int SuspectPages,
    string RecordFile, string ScratchBundle);

/// <summary>What an open and close of a restored bundle did (<see cref="BackupOperations.OpenAndCloseRestored"/>).</summary>
/// <param name="CrashRecoveryRan">The open took the crash path: the WAL was replayed, derived structures rebuilt, the image sealed.</param>
/// <param name="Recovery">The WAL recovery's figures; default when none ran.</param>
/// <param name="Duration">Open to close.</param>
[PublicAPI]
public sealed record BackupSettleResult(bool CrashRecoveryRan, WalRecoveryResult Recovery, TimeSpan Duration);
