using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace Typhon.Engine;

/// <summary>What the first open of a restored bundle did with its <c>restore.json</c>.</summary>
internal enum RestoreAdoption : byte
{
    /// <summary>No marker: every normal open.</summary>
    None,

    /// <summary>The new timeline was written (one meta flip), then the marker deleted.</summary>
    Flipped,

    /// <summary>The image already carried the new timeline (a crash after the flip, before the delete): no flip, the marker deleted.</summary>
    AlreadyAdopted,
}

/// <summary>
/// The open of a bundle a restore wrote (plan 1413 §2.9, BKP-05): <c>restore.json</c> names a new WAL timeline, which the open adopts before the WAL
/// manager starts, then deletes the marker. A marker it cannot trust, one naming another database, or one beside a WAL directory other than the
/// bundle's refuses the open, writing nothing.
/// </summary>
public partial class DatabaseEngine
{
    private RestoreMarkerFile _restoreMarker;

    /// <summary>What this open did with a restore marker.</summary>
    internal RestoreAdoption LastOpenRestoreAdoption { get; private set; }

    /// <summary>Test seam: the file system the marker is probed through, for the calling thread. Null in production.</summary>
    [ThreadStatic]
    internal static IStoreFileSystem RestoreMarkerFileSystemForTest;

    /// <summary>Test seam: runs between the timeline flip and the marker delete (a crash there). Null in production.</summary>
    [ThreadStatic]
    internal static Action RestoreAdoptionProbe;

    /// <summary>Test seam: runs as the WAL manager starts, to observe what the open adopted before it. Null in production.</summary>
    [ThreadStatic]
    internal static Action<DatabaseEngine> BeforeWalManagerProbe;

    /// <summary>
    /// Before the WAL is scanned: reads <c>{bundle}/restore.json</c> — one existence probe on every normal open — and refuses the open when the marker
    /// is unreadable, names another database, or the WAL directory is not the bundle's own (DC-4: an engine configured elsewhere would replay a log
    /// that is not the restored tail). Nothing has been written to the data file yet, so a refusal leaves it untouched.
    /// </summary>
    private void CheckRestoreMarkerAtOpen()
    {
        var fs = RestoreMarkerFileSystemForTest ?? StoreFileSystem.Instance;
        var bundle = MMF.BundleDirectory;
        var state = RestoreMarker.TryRead(fs, bundle, out var marker, out var problem);
        if (state == RestoreMarkerState.Absent)
        {
            return;
        }

        var path = Path.Combine(bundle, RestoreMarker.FileName);
        if (state == RestoreMarkerState.Invalid)
        {
            throw new StorageException(TyphonErrorCode.InvalidDatabaseBundle,
                $"This bundle was restored from a backup, but its restore marker '{path}' cannot be read ({problem}). Opening it would continue the "
                + "original's WAL timeline; restore the point again.");
        }

        var databaseId = MMF.Bootstrap.TryGet(BK_DatabaseId, out var id) ? BackupImageMeta.UnpackGuid(id) : Guid.Empty;
        if (marker.DatabaseId != databaseId)
        {
            throw new StorageException(TyphonErrorCode.InvalidDatabaseBundle,
                $"The restore marker '{path}' names database {marker.DatabaseId}, but this data file is database {databaseId}: the bundle mixes two "
                + "databases. Restore the point again.");
        }

        var expected = Path.GetFullPath(Path.Combine(bundle, IntegrityConstants.WalDirectoryName));
        var walDir = _options.Wal?.WalDirectory;
        if (walDir == null || !string.Equals(Path.GetFullPath(walDir).TrimEnd('/', '\\'), expected.TrimEnd('/', '\\'),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new StorageException(TyphonErrorCode.InvalidDatabaseBundle,
                $"This bundle was restored from a backup and its WAL is in '{expected}', but the engine is configured to read '{walDir}': it would "
                + "replay that log instead of the restored one. Open it with WalDirectory unset (or set to the bundle's wal/).");
        }

        _restoreMarker = marker;
    }

    /// <summary>
    /// After the system schema loaded, before the WAL manager starts: adopts the marker's timeline (one fsynced meta flip, skipped when already
    /// adopted), then deletes the marker. A crash anywhere repeats both, so every WAL segment this engine writes belongs to the new timeline.
    /// </summary>
    private void AdoptRestoredTimeline()
    {
        if (_restoreMarker == null)
        {
            return;
        }

        var previous = MMF.Bootstrap.TryGet(BK_TimelineId, out var current) ? UnpackDatabaseId(current) : Guid.Empty;
        var flips = previous != _restoreMarker.NewTimelineId;
        AdoptTimeline(_restoreMarker.NewTimelineId);
        RestoreAdoptionProbe?.Invoke();

        var path = Path.Combine(MMF.BundleDirectory, RestoreMarker.FileName);
        try
        {
            (RestoreMarkerFileSystemForTest ?? StoreFileSystem.Instance).DeleteFile(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Harmless: the next open finds the timeline already adopted and only retries the delete.
            LogRestoreMarkerNotDeleted(path, e.Message);
        }

        LastOpenRestoreAdoption = flips ? RestoreAdoption.Flipped : RestoreAdoption.AlreadyAdopted;
        LogRestoredTimelineAdopted(previous, _restoreMarker.NewTimelineId, _restoreMarker.RestoredFrom.BackupId);
        _restoreMarker = null;
    }

    [LoggerMessage(LogLevel.Information, "Open: restored from backup point #{BackupId}; WAL timeline {Previous} → {Adopted}")]
    private partial void LogRestoredTimelineAdopted(Guid previous, Guid adopted, uint backupId);

    [LoggerMessage(LogLevel.Warning, "Open: the restore marker '{Path}' could not be deleted ({Reason}); the next open retries")]
    private partial void LogRestoreMarkerNotDeleted(string path, string reason);
}
