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
    public BackupStoreLockedException(string storePath, int holderPid, string holderMachine, DateTimeOffset holderSince)
        : base(TyphonErrorCode.BackupStoreLocked, holderPid > 0
            ? $"The backup store '{storePath}' is in use by PID {holderPid} on {holderMachine} since {holderSince:u}."
            : $"The backup store '{storePath}' is in use by another writer.")
    {
        StorePath = storePath;
        HolderPid = holderPid;
        HolderMachine = holderMachine;
        HolderSince = holderSince;
    }

    /// <summary>The store directory.</summary>
    public string StorePath { get; }

    /// <summary>The holder's process id, or <c>0</c> when unknown.</summary>
    public int HolderPid { get; }

    /// <summary>The holder's machine, or <c>null</c> when unknown.</summary>
    public string HolderMachine { get; }

    /// <summary>When the holder took the lock, or <see cref="DateTimeOffset.MinValue"/> when unknown.</summary>
    public DateTimeOffset HolderSince { get; }

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
    /// <param name="section">The part that failed: <c>Header</c>, <c>Footer</c>, <c>Index</c>, <c>TailDir</c>, <c>Suspect</c>, <c>Page</c>, <c>Tail</c>, <c>File</c>.</param>
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
