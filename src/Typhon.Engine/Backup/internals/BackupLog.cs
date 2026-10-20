using Microsoft.Extensions.Logging;

namespace Typhon.Engine.Internals;

/// <summary>The backup store, restore and verify log lines. Never per page.</summary>
internal static partial class BackupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Backup point #{BackupId} ({Kind}) published: {Pages} pages, {Bytes} bytes, {Milliseconds} ms")]
    public static partial void PackPublished(ILogger logger, uint backupId, BackupKind kind, int pages, long bytes, long milliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup point #{BackupId} abandoned: {Reason}")]
    public static partial void PackAborted(ILogger logger, uint backupId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the temporary file '{Path}' ({Reason}); the next writer of the store deletes it")]
    public static partial void TempNotDeleted(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup store '{Store}': deleted {Count} abandoned temporary files")]
    public static partial void AbandonedTempsDeleted(ILogger logger, string store, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Backup store '{Store}': catalog rebuilt ({Reason})")]
    public static partial void CatalogRebuilt(ILogger logger, string store, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup store '{Store}': catalog not written ({Reason}); it is a cache and will be rebuilt")]
    public static partial void CatalogWriteFailed(ILogger logger, string store, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Backup store '{Store}': its id high-water '{Path}' is missing or damaged ({Reason}); ids continue from the packs present, so an id "
                  + "whose pack was deleted could be reused")]
    public static partial void HighWaterDegraded(ILogger logger, string store, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup store '{Store}': pack '{File}' is unreadable and left out of the catalog ({Reason})")]
    public static partial void PackUnreadable(ILogger logger, string store, string file, string reason);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Restore of point #{BackupId} staged: {Pages} pages, {TailSegments} WAL segments, {Milliseconds} ms")]
    public static partial void RestoreStaged(ILogger logger, uint backupId, int pages, int tailSegments, long milliseconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restore published to '{Target}' (previous bundle kept at '{Displaced}')")]
    public static partial void RestorePublished(ILogger logger, string target, string displaced);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restore refused ({Reason}): '{Target}'")]
    public static partial void RestoreRefused(ILogger logger, RestoreRefusalReason reason, string target);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Backup point #{BackupId} compacted from a chain of {Depth} pack(s): {Pages} pages ({Holes} holes), {BytesRead} bytes read, "
                  + "{BytesWritten} written, {Milliseconds} ms")]
    public static partial void PointCompacted(ILogger logger, uint backupId, int depth, int pages, int holes, long bytesRead, long bytesWritten,
        long milliseconds);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Backup point #{BackupId}: its compacted pack failed the verify before publish ({Code}: {Detail}); it was not published")]
    public static partial void CompactionRejected(ILogger logger, uint backupId, string code, string detail);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Backup store '{Store}': retention kept {Kept} point(s) from #{Cutoff}, compacted {Compactions}, deleted {Deleted} pack(s) "
                  + "({Bytes} bytes) in {Milliseconds} ms")]
    public static partial void RetentionApplied(ILogger logger, string store, int kept, uint cutoff, int compactions, int deleted, long bytes,
        long milliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup store '{Store}': retention blocked ({Block}): {Reason}")]
    public static partial void RetentionBlocked(ILogger logger, string store, BackupRetentionBlock block, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Backup store '{Store}': compaction of point #{BackupId} failed ({Reason}); its chain keeps every pack this run")]
    public static partial void RetentionCompactionFailed(ILogger logger, string store, uint backupId, string reason);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Backup store '{Store}': after retention, kept point #{BackupId} does not resolve ({Reason}). This is a defect: report it")]
    public static partial void RetentionBrokeAKeptPoint(ILogger logger, string store, uint backupId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Drill of backup point #{BackupId}: {Outcome} (stage {Stage}: {Problem}), {Milliseconds} ms")]
    public static partial void DrillEnded(ILogger logger, uint backupId, BackupDrillOutcome outcome, BackupDrillStage stage, string problem,
        long milliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup store '{Store}': the record of the drill of point #{BackupId} was not written: {Reason}")]
    public static partial void DrillRecordFailed(ILogger logger, string store, uint backupId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The drill's scratch directory '{Directory}' could not be deleted: {Reason}")]
    public static partial void DrillScratchLeft(ILogger logger, string directory, string reason);
}
