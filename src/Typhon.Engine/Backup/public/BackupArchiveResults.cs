using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// What a restore would do, worked out without doing any of it (<see cref="BackupOperations.PlanRestore"/>): the point (or, for an LSN or a time, the
/// base and the archived WAL), the target as the restore resolves it, what would stop it, and the fingerprint
/// <see cref="BackupRestoreOptions.ApprovedPlan"/> binds a restore to.
/// </summary>
/// <param name="Fingerprint">Identifies what would be restored and where; it changes when the chain, the archived WAL used, the cut or the target change.</param>
/// <param name="ResolvedTarget">The bundle the restore writes; null when no target was given or it is refused by name.</param>
/// <param name="DisplacedBundle">Where Replace moves the existing target; null when there is none.</param>
/// <param name="Chain">The points whose packs are read, base first.</param>
/// <param name="BytesToRead">The packs' and archived segments' bytes.</param>
/// <param name="BaseBackupId">The point restored (the base, for an LSN or a time); 0 when unreachable.</param>
/// <param name="CheckpointLsn">The base's <c>C</c>.</param>
/// <param name="PointLsn">The restored database's last LSN: the point's <c>P</c>, or <c>L'</c> for an LSN or a time.</param>
/// <param name="TargetLsn">The LSN asked for, or the one an instant resolved to; 0 for a point.</param>
/// <param name="TimelineId">The timeline restored from.</param>
/// <param name="ArchivedSegments">Archived WAL segments the restore reads.</param>
/// <param name="ArchivedBytes">Their file bytes.</param>
/// <param name="SampleLsn">For an instant, the LSN of the time sample used; 0 otherwise.</param>
/// <param name="SampleUtc">For an instant, the sample's time; null otherwise.</param>
/// <param name="Refusal">Why the restore would be refused; null when it would not.</param>
/// <param name="Unreachable">Why an LSN or a time cannot be reached; null when it can.</param>
/// <param name="RefusalMessage">The refusal's message; null when none.</param>
/// <param name="NearestRestorable">For an unreachable target, the restorable LSNs nearest it.</param>
[PublicAPI]
public sealed record BackupRestorePlan(string Fingerprint, string ResolvedTarget, string DisplacedBundle, IReadOnlyList<uint> Chain, long BytesToRead,
    uint BaseBackupId, long CheckpointLsn, long PointLsn, long TargetLsn, Guid TimelineId, int ArchivedSegments, long ArchivedBytes, long SampleLsn,
    DateTime? SampleUtc, RestoreRefusalReason? Refusal, RestoreUnreachableReason? Unreachable, string RefusalMessage, IReadOnlyList<long> NearestRestorable)
{
    /// <summary>Whether the restore would proceed as planned.</summary>
    public bool CanRestore => Refusal == null && Unreachable == null;
}

/// <summary>Why a timeline exists.</summary>
[PublicAPI]
public enum BackupTimelineReason
{
    /// <summary>The database's first timeline.</summary>
    Genesis,

    /// <summary>A restore started it.</summary>
    Restore,

    /// <summary>A schema change forked it: no restore replays the WAL across it.</summary>
    SchemaChange,

    /// <summary>A completed bulk load forked it: no restore replays the WAL across it.</summary>
    BulkLoad,

    /// <summary>Not recorded.</summary>
    Unknown,
}

/// <summary>Why a range of LSNs is missing from a timeline's archive: no restore crosses it.</summary>
[PublicAPI]
public enum BackupArchiveGapReason
{
    /// <summary>The archive fell behind past the backlog limit and the engine reclaimed the WAL it held.</summary>
    BacklogReleased,

    /// <summary>The database ran without archiving.</summary>
    ArchivingOff,

    /// <summary>A hole the archive cannot explain.</summary>
    Unknown,

    /// <summary>Fence records a crash's recovery applied from a torn frame the archive does not hold.</summary>
    TornAtCrash,

    /// <summary>A segment broken in the middle: the archive holds it up to the break.</summary>
    CorruptSegment,
}

/// <summary>What is known of an archived segment beyond its range.</summary>
[PublicAPI]
[Flags]
public enum BackupArchivedSegmentFlags
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Archived from a previous engine session's segment, adopted at an open.</summary>
    Adopted = 1,

    /// <summary>A crash open sealed its torn tail: archived up to the seal.</summary>
    SealedAtCrash = 2,

    /// <summary>A gap precedes it.</summary>
    GapBefore = 4,

    /// <summary>It breaks in the middle: archived up to the break.</summary>
    Corrupt = 8,
}

/// <summary>One archived WAL segment.</summary>
/// <param name="SegmentId">Its id.</param>
/// <param name="FirstLsn">Its first LSN.</param>
/// <param name="LastLsn">The archive's end once it is in: its last LSN, or its last record's when that lies further.</param>
/// <param name="SealedUtc">When the engine sealed it; null when not recorded (a segment adopted at an open).</param>
/// <param name="ArchivedUtc">When it was archived.</param>
/// <param name="Bytes">Its file's bytes.</param>
/// <param name="RawBytes">The WAL bytes it holds.</param>
/// <param name="Flags">What else is known of it.</param>
/// <param name="SessionStart">The first segment of an engine session.</param>
[PublicAPI]
public sealed record BackupArchivedSegment(long SegmentId, long FirstLsn, long LastLsn, DateTime? SealedUtc, DateTime ArchivedUtc, long Bytes, long RawBytes,
    BackupArchivedSegmentFlags Flags, bool SessionStart);

/// <summary>A range of LSNs missing from a timeline's archive.</summary>
/// <param name="FromLsn">Its first missing LSN.</param>
/// <param name="ToLsn">Its last missing LSN.</param>
/// <param name="RecordedUtc">When the archive recorded it: the archiving time of the segment that carries it.</param>
/// <param name="Reason">Why.</param>
/// <param name="Covered">Whether a restorable point of the timeline lies past it, so that the LSNs after it are restorable again.</param>
[PublicAPI]
public sealed record BackupArchiveGap(long FromLsn, long ToLsn, DateTime RecordedUtc, BackupArchiveGapReason Reason, bool Covered);

/// <summary>A time sample: the WAL was durable through <paramref name="Lsn"/> at <paramref name="Utc"/>.</summary>
[PublicAPI]
public readonly record struct BackupTimeSample(long Lsn, DateTime Utc);

/// <summary>One timeline of a store's WAL archive.</summary>
/// <param name="TimelineId">Its id.</param>
/// <param name="ParentTimelineId">The timeline it forked from; null for a genesis timeline or when not recorded.</param>
/// <param name="OriginLsn">The parent's last LSN this timeline continues; 0 for a genesis timeline.</param>
/// <param name="Reason">Why it exists.</param>
/// <param name="ArchivedThroughLsn">The archive's end; 0 when it holds no segment.</param>
/// <param name="Segments">Its segments, ascending LSN.</param>
/// <param name="Gaps">The ranges missing from it.</param>
/// <param name="Samples">Time samples, at most one per segment (its last).</param>
/// <param name="Unreadable">Files of it that cannot be read, with why.</param>
[PublicAPI]
public sealed record BackupArchiveTimeline(Guid TimelineId, Guid? ParentTimelineId, long OriginLsn, BackupTimelineReason Reason, long ArchivedThroughLsn,
    IReadOnlyList<BackupArchivedSegment> Segments, IReadOnlyList<BackupArchiveGap> Gaps, IReadOnlyList<BackupTimeSample> Samples,
    IReadOnlyList<KeyValuePair<string, string>> Unreadable);

/// <summary>A store's WAL archive (<see cref="BackupOperations.ListArchive"/>).</summary>
/// <param name="StoreId">The store's id.</param>
/// <param name="DatabaseId">The database it backs up.</param>
/// <param name="Timelines">Every timeline, parents before their children.</param>
[PublicAPI]
public sealed record BackupArchiveListing(Guid StoreId, Guid DatabaseId, IReadOnlyList<BackupArchiveTimeline> Timelines);

/// <summary>An instant resolved to an LSN (<see cref="BackupOperations.ResolveTime"/>): the highest LSN sampled durable at or before it.</summary>
/// <param name="Lsn">That LSN.</param>
/// <param name="SampleUtc">The sample's time.</param>
/// <param name="TimelineId">The timeline holding it (the asked timeline, or an ancestor).</param>
[PublicAPI]
public sealed record BackupTimeResolution(long Lsn, DateTime SampleUtc, Guid TimelineId);

/// <summary>What a WAL frame holds.</summary>
[PublicAPI]
public enum BackupLogFrameKind
{
    /// <summary>A committed transaction.</summary>
    Transaction,

    /// <summary>A fence emission.</summary>
    Fence,

    /// <summary>A bulk load's manifest.</summary>
    Bulk,

    /// <summary>Records of no known kind.</summary>
    Other,
}

/// <summary>One WAL frame of the archive.</summary>
/// <param name="FirstLsn">Its lowest LSN.</param>
/// <param name="LastLsn">Its highest LSN.</param>
/// <param name="Tsn">Its transaction's TSN; 0 when none.</param>
/// <param name="Kind">What it holds.</param>
/// <param name="Records">Its records.</param>
/// <param name="CommitStamp">Its commit stamp; 0 when none.</param>
/// <param name="ApproxUtc">When it became durable, interpolated between the time samples around it; null with no sample.</param>
/// <param name="SegmentId">The segment holding it.</param>
[PublicAPI]
public sealed record BackupLogFrame(long FirstLsn, long LastLsn, long Tsn, BackupLogFrameKind Kind, int Records, long CommitStamp, DateTime? ApproxUtc,
    long SegmentId);

/// <summary>A range of a timeline's archive (<see cref="BackupOperations.ReadLog(string, Guid, long, long, bool, int)"/>).</summary>
/// <param name="TimelineId">The timeline read.</param>
/// <param name="FromLsn">The range's first LSN.</param>
/// <param name="ToLsn">The range's last LSN.</param>
/// <param name="Segments">The segments overlapping it.</param>
/// <param name="Gaps">The gaps overlapping it.</param>
/// <param name="Samples">The time samples inside it.</param>
/// <param name="Frames">With commits, every frame ending inside it, up to the limit; empty otherwise.</param>
/// <param name="Truncated">Whether the limit stopped the frame list.</param>
[PublicAPI]
public sealed record BackupArchiveLog(Guid TimelineId, long FromLsn, long ToLsn, IReadOnlyList<BackupArchivedSegment> Segments,
    IReadOnlyList<BackupArchiveGap> Gaps, IReadOnlyList<BackupTimeSample> Samples, IReadOnlyList<BackupLogFrame> Frames, bool Truncated);

/// <summary>What a timeline's archive makes restorable, for <see cref="BackupOperations.Status"/>.</summary>
/// <param name="TimelineId">The timeline.</param>
/// <param name="FromLsn">The oldest restorable point's <c>P</c> on it; 0 when it has none.</param>
/// <param name="ThroughLsn">The archive's end.</param>
/// <param name="LastArchivedUtc">When its newest segment was archived; null when none.</param>
/// <param name="UncoveredGaps">Its gaps no restorable point lies past.</param>
[PublicAPI]
public sealed record BackupArchiveCoverage(Guid TimelineId, long FromLsn, long ThroughLsn, DateTime? LastArchivedUtc, IReadOnlyList<BackupArchiveGap> UncoveredGaps);
