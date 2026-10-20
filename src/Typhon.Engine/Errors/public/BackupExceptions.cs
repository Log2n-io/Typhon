using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>Base class of every backup, restore and verify failure.</summary>
[PublicAPI]
public class BackupException : DurabilityException
{
    /// <summary>Creates a backup failure.</summary>
    /// <param name="errorCode">The failure's code (71xx).</param>
    /// <param name="message">What failed, and what to do about it.</param>
    public BackupException(TyphonErrorCode errorCode, string message) : base(errorCode, message)
    {
    }

    /// <summary>Creates a backup failure caused by another exception.</summary>
    /// <param name="errorCode">The failure's code (71xx).</param>
    /// <param name="message">What failed, and what to do about it.</param>
    /// <param name="innerException">The cause.</param>
    public BackupException(TyphonErrorCode errorCode, string message, Exception innerException) : base(errorCode, message, innerException)
    {
    }
}

/// <summary>Another writer holds the backup store's lock. Retrying later may succeed.</summary>
[PublicAPI]
public sealed class BackupStoreLockedException : BackupException
{
    /// <summary>Creates the failure.</summary>
    /// <param name="storePath">The store directory.</param>
    /// <param name="holderPid">The holder's process id, or <c>0</c> when it could not be read.</param>
    /// <param name="holderMachine">The holder's machine, or <c>null</c> when it could not be read.</param>
    /// <param name="holderSince">When the holder took the lock, or <see cref="DateTimeOffset.MinValue"/> when unknown.</param>
    /// <param name="holderPurpose">What the holder is doing (<c>backup</c>, <c>compact</c>, <c>prune</c>), or <c>null</c> when unknown.</param>
    public BackupStoreLockedException(string storePath, int holderPid, string holderMachine, DateTimeOffset holderSince, string holderPurpose = null)
        : base(TyphonErrorCode.BackupStoreLocked, holderPid > 0
            ? $"The backup store '{storePath}' is in use by PID {holderPid} on {holderMachine}{(holderPurpose == null ? "" : $" ({holderPurpose})")} "
              + $"since {holderSince:u}."
            : $"The backup store '{storePath}' is in use by another writer.")
    {
        StorePath = storePath;
        HolderPid = holderPid;
        HolderMachine = holderMachine;
        HolderSince = holderSince;
        HolderPurpose = holderPurpose;
    }

    /// <summary>The store directory.</summary>
    public string StorePath { get; }

    /// <summary>The holder's process id, or <c>0</c> when unknown.</summary>
    public int HolderPid { get; }

    /// <summary>The holder's machine, or <c>null</c> when unknown.</summary>
    public string HolderMachine { get; }

    /// <summary>When the holder took the lock, or <see cref="DateTimeOffset.MinValue"/> when unknown.</summary>
    public DateTimeOffset HolderSince { get; }

    /// <summary>What the holder is doing (<c>backup</c>, <c>compact</c>, <c>prune</c>), or <c>null</c> when unknown.</summary>
    public string HolderPurpose { get; }

    /// <inheritdoc />
    public override bool IsTransient => true;
}

/// <summary>
/// A retention approved from a preview would now delete a pack the preview did not list (a point landed, a drill passed, time moved past an age limit):
/// nothing was compacted or deleted. Preview again and approve the new plan.
/// </summary>
[PublicAPI]
public sealed class BackupPlanChangedException : BackupException
{
    /// <summary>Creates the failure.</summary>
    /// <param name="storePath">The store directory.</param>
    /// <param name="approvedFingerprint">The fingerprint of the plan that was approved.</param>
    /// <param name="currentFingerprint">The fingerprint of the plan the store makes now.</param>
    public BackupPlanChangedException(string storePath, string approvedFingerprint, string currentFingerprint)
        : base(TyphonErrorCode.BackupPlanChanged,
            $"The retention plan of backup store '{storePath}' changed since it was approved (plan {approvedFingerprint}, now {currentFingerprint}): it would "
            + "delete a pack the approved plan did not list. Nothing was deleted; preview it again.")
    {
        StorePath = storePath;
        ApprovedFingerprint = approvedFingerprint;
        CurrentFingerprint = currentFingerprint;
    }

    /// <summary>The store directory.</summary>
    public string StorePath { get; }

    /// <summary>The fingerprint of the plan that was approved.</summary>
    public string ApprovedFingerprint { get; }

    /// <summary>The fingerprint of the plan the store makes now.</summary>
    public string CurrentFingerprint { get; }

    /// <inheritdoc />
    public override bool IsTransient => true;
}

/// <summary>The backup store belongs to another database: two databases never interleave in one store.</summary>
[PublicAPI]
public sealed class BackupStoreMismatchException : BackupException
{
    /// <summary>Creates the failure.</summary>
    /// <param name="storePath">The store directory.</param>
    /// <param name="storeDatabaseId">The database the store belongs to.</param>
    /// <param name="databaseId">The database the operation was for.</param>
    /// <param name="detail">Optional detail appended to the message.</param>
    public BackupStoreMismatchException(string storePath, Guid storeDatabaseId, Guid databaseId, string detail = null)
        : base(TyphonErrorCode.BackupStoreMismatch,
            $"The backup store '{storePath}' belongs to database {storeDatabaseId}, not {databaseId}." + (detail == null ? "" : " " + detail))
    {
        StorePath = storePath;
        StoreDatabaseId = storeDatabaseId;
        DatabaseId = databaseId;
    }

    /// <summary>The store directory.</summary>
    public string StorePath { get; }

    /// <summary>The database the store belongs to.</summary>
    public Guid StoreDatabaseId { get; }

    /// <summary>The database the operation was for.</summary>
    public Guid DatabaseId { get; }
}

/// <summary>A backup pack failed one of its checks. A restore of a point that needs it is refused rather than given wrong data.</summary>
[PublicAPI]
public sealed class BackupPackCorruptException : BackupException
{
    /// <summary>Creates the failure.</summary>
    /// <param name="packFile">The pack's path.</param>
    /// <param name="section">
    /// The part that failed: <c>Header</c>, <c>Footer</c>, <c>Index</c>, <c>TailDir</c>, <c>Suspect</c>, <c>Page</c>, <c>Tail</c>, <c>File</c>.
    /// </param>
    /// <param name="pageId">The page concerned, or <c>-1</c>.</param>
    /// <param name="detail">What was wrong.</param>
    public BackupPackCorruptException(string packFile, string section, long pageId, string detail)
        : base(TyphonErrorCode.BackupPackCorrupt,
            $"Backup pack '{packFile}' is damaged ({section}{(pageId >= 0 ? $", page {pageId}" : "")}): {detail}")
    {
        PackFile = packFile;
        Section = section;
        PageId = pageId;
    }

    /// <summary>The pack's path.</summary>
    public string PackFile { get; }

    /// <summary>The part of the pack that failed.</summary>
    public string Section { get; }

    /// <summary>The page concerned, or <c>-1</c>.</summary>
    public long PageId { get; }
}

/// <summary>A backup point cannot be restored because a pack of its chain is missing, or the chain's links disagree.</summary>
[PublicAPI]
public sealed class BackupChainBrokenException : BackupException
{
    /// <summary>Creates the failure.</summary>
    /// <param name="pointId">The point that was asked for.</param>
    /// <param name="missingId">The point whose pack is missing or inconsistent.</param>
    /// <param name="restorableAlternatives">The points that remain restorable, ascending.</param>
    /// <param name="detail">What was wrong with the link.</param>
    public BackupChainBrokenException(uint pointId, uint missingId, IReadOnlyList<uint> restorableAlternatives, string detail)
        : base(TyphonErrorCode.BackupChainBroken, FormatMessage(pointId, missingId, restorableAlternatives, detail))
    {
        PointId = pointId;
        MissingId = missingId;
        RestorableAlternatives = restorableAlternatives ?? [];
    }

    /// <summary>The point that was asked for.</summary>
    public uint PointId { get; }

    /// <summary>The point whose pack is missing or inconsistent.</summary>
    public uint MissingId { get; }

    /// <summary>The points that remain restorable, ascending.</summary>
    public IReadOnlyList<uint> RestorableAlternatives { get; }

    private static string FormatMessage(uint pointId, uint missingId, IReadOnlyList<uint> alternatives, string detail)
    {
        var restorable = alternatives == null || alternatives.Count == 0 ? "none" : FormatRanges(alternatives);
        return $"backup-{missingId:D4} is required for point #{pointId} ({detail}); restorable without it: {restorable}";
    }

    private static string FormatRanges(IReadOnlyList<uint> ids)
    {
        var sb = new System.Text.StringBuilder();
        var start = 0;
        for (var i = 1; i <= ids.Count; i++)
        {
            if (i < ids.Count && ids[i] == ids[i - 1] + 1)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(", ");
            }

            sb.Append('#').Append(ids[start]);
            if (i - 1 > start)
            {
                sb.Append("–#").Append(ids[i - 1]);
            }

            start = i;
        }

        return sb.ToString();
    }
}

/// <summary>A store or pack was written in a format this build does not read.</summary>
[PublicAPI]
public sealed class BackupFormatUnsupportedException : BackupException
{
    /// <summary>Creates the failure.</summary>
    /// <param name="file">The file.</param>
    /// <param name="what">The field that is not supported (e.g. <c>FormatVersion</c>, <c>Compression</c>).</param>
    /// <param name="found">The value found.</param>
    /// <param name="supported">The value this build supports.</param>
    public BackupFormatUnsupportedException(string file, string what, int found, int supported)
        : base(TyphonErrorCode.BackupFormatUnsupported, $"'{file}' has {what} {found}; this build reads {what} {supported}. Use a newer build.")
    {
        File = file;
        Found = found;
        Supported = supported;
    }

    /// <summary>The file.</summary>
    public string File { get; }

    /// <summary>The value found.</summary>
    public int Found { get; }

    /// <summary>The value this build supports.</summary>
    public int Supported { get; }
}

/// <summary>Why a restore refused to write its target.</summary>
[PublicAPI]
public enum RestoreRefusalReason
{
    /// <summary>The target bundle exists and the restore was not asked to replace it.</summary>
    TargetExists,

    /// <summary>A live process holds the target bundle.</summary>
    TargetLocked,

    /// <summary>The target bundle's name differs from the database name recorded in the image; an open would refuse it.</summary>
    NameMismatch,

    /// <summary>The image's on-disk format revision differs from this build's.</summary>
    FormatRevision,

    /// <summary>A staging directory the restore did not create is in the way.</summary>
    ForeignStaging,
}

/// <summary>A restore refused to write its target. Nothing was written.</summary>
[PublicAPI]
public sealed class RestoreRefusedException : BackupException
{
    /// <summary>Creates the refusal.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="target">The target bundle.</param>
    /// <param name="detail">What to do about it.</param>
    public RestoreRefusedException(RestoreRefusalReason reason, string target, string detail)
        : base(TyphonErrorCode.RestoreRefused, $"Restore to '{target}' refused ({reason}): {detail}")
    {
        Reason = reason;
        Target = target;
    }

    /// <summary>Why.</summary>
    public RestoreRefusalReason Reason { get; }

    /// <summary>The target bundle.</summary>
    public string Target { get; }
}

/// <summary>Why an online backup (<see cref="IBackupService.CreateAsync"/>) was abandoned.</summary>
[PublicAPI]
public enum BackupAbortReason : byte
{
    /// <summary>No reason recorded: the capture has not aborted.</summary>
    None = 0,

    /// <summary>
    /// A write to the data file had to wait for the backup to preserve the page it overwrites, longer than the capture allows: the backup could not
    /// keep up with the writers. Transient: a later attempt, at a quieter moment, may succeed.
    /// </summary>
    CbwStall = 1,

    /// <summary>The freeze could not drain the data-file writes in flight within its budget. Transient.</summary>
    GateTimeout = 2,

    /// <summary>The caller cancelled the backup.</summary>
    Cancelled = 3,

    /// <summary>The engine began to close, or is not open.</summary>
    EngineStopping = 4,

    /// <summary>The backup store failed; the store's exception is the inner one.</summary>
    SinkFailed = 5,

    /// <summary>The WAL did not become durable up to the point being frozen within the budget, or failed. Transient.</summary>
    WalFlushTimeout = 6,

    /// <summary>
    /// The engine cannot take a consistent point right now: its open has not completed, recovery is running, or its commit pipeline or checkpointer has
    /// stopped. The message says which.
    /// </summary>
    EngineFault = 7,

    /// <summary>A WAL segment the backup needs could not be read, or did not hold a valid header.</summary>
    TailUnreadable = 8,

    /// <summary>
    /// Data-file write tracking is switched off for this process (<c>TYPHON_DATAFILE_GATE=0</c>), so no consistent image can be frozen. An offline
    /// backup (<see cref="BackupOperations.Create"/>) still works.
    /// </summary>
    TrackingDisabled = 9,
}

/// <summary>
/// An online backup was abandoned (<see cref="TyphonErrorCode.BackupAborted"/>). Nothing was published, and no page is forgotten: the next backup
/// covers everything this one would have.
/// </summary>
[PublicAPI]
public sealed class BackupAbortedException : BackupException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="reason">Why the backup was abandoned.</param>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public BackupAbortedException(BackupAbortReason reason, string message, Exception innerException = null)
        : base(TyphonErrorCode.BackupAborted, $"Backup aborted ({reason}): {message}", innerException)
    {
        Reason = reason;
    }

    /// <summary>Why the backup was abandoned.</summary>
    public BackupAbortReason Reason { get; }

    /// <inheritdoc />
    public override bool IsTransient => Reason is BackupAbortReason.CbwStall or BackupAbortReason.GateTimeout or BackupAbortReason.WalFlushTimeout;
}
