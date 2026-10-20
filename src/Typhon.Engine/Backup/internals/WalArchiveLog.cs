using Microsoft.Extensions.Logging;
using System;

namespace Typhon.Engine.Internals;

/// <summary>The WAL archive's log lines (impl/1418 §3.10). Never per record: per segment at most, and a failing store once per change of state.</summary>
internal static partial class WalArchiveLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "WAL archive: {Held} bytes held for the archive exceed the backlog limit {Limit}; the floor (LSN {Floor}) is released and the checkpoint "
                  + "reclaims up to LSN {Trim} un-archived. The archive will record a gap, and a backup point is requested")]
    public static partial void FloorReleased(ILogger logger, long held, long limit, long floor, long trim);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "WAL archive: segment {SegmentId} (LSN {FirstLsn}..{LastLsn}) archived, {RawBytes} bytes in a {FileBytes}-byte file")]
    public static partial void SegmentArchived(ILogger logger, long segmentId, long firstLsn, long lastLsn, long rawBytes, long fileBytes);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "WAL archive: timeline {Timeline} misses LSN {From}..{To} ({Reason}); no restore crosses it. A backup point is requested")]
    public static partial void GapRecorded(ILogger logger, Guid timeline, long from, long to, ArchiveGapReason reason);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "WAL archive: segment {SegmentId} breaks at file offset {Offset}; archived up to its last whole frame, LSN {From}..{To} unrecoverable")]
    public static partial void SegmentDamaged(ILogger logger, long segmentId, int offset, long from, long to);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "WAL archive: the crash open sealed segment {SegmentId}'s torn tail at file offset {Offset} (fence LSN {FenceLsn} applied from it)")]
    public static partial void TornTailSealed(ILogger logger, long segmentId, int offset, long fenceLsn);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "WAL archive: segment {SegmentId} breaks with data-bearing segments after it (mid-log corruption); left unsealed, recovery stops there")]
    public static partial void MidLogBreakNotSealed(ILogger logger, long segmentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "WAL archive: timeline {Timeline} diverged ({Why}); archiving stopped")]
    public static partial void ArchiveDiverged(ILogger logger, Guid timeline, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WAL archive: the store '{Store}' is failing ({Reason}); retrying, the WAL is held up to the backlog limit")]
    public static partial void StoreUnavailable(ILogger logger, string store, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "WAL archive refused for the store '{Store}': {Why}")]
    public static partial void ArchiverRefused(ILogger logger, string store, string why);

    [LoggerMessage(Level = LogLevel.Information, Message = "WAL archive: timeline {Timeline} archived through LSN {Through} in '{Store}'")]
    public static partial void ArchiverStarted(ILogger logger, Guid timeline, long through, string store);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WAL archive: the wall clock stepped back from {Previous:O} to {Now:O}; time samples are clamped")]
    public static partial void ClockSteppedBack(ILogger logger, DateTime previous, DateTime now);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "WAL archive: the close's final pass ran out of time, archived through LSN {Through} of {Durable}; the next open archives the rest")]
    public static partial void ClosePassIncomplete(ILogger logger, long through, long durable);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Restore: point #{BackupId} skipped as a base, its tail segment {SegmentId} disagrees with the archive of timeline {Timeline} (another history)")]
    public static partial void BaseSkipped(ILogger logger, uint backupId, long segmentId, Guid timeline);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WAL archive: a scheduler step failed ({Reason})")]
    public static partial void StepFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "WAL archive retention: timeline {Timeline} skipped this run, its archive.lock is held")]
    public static partial void PruneSkipped(ILogger logger, Guid timeline);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WAL archive '{Directory}': index cache not written ({Reason}); it is rebuilt from the segments")]
    public static partial void IndexNotWritten(ILogger logger, string directory, string reason);
}
