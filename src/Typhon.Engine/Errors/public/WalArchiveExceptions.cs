using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>Why a restore to an LSN or a time cannot reach its target.</summary>
[PublicAPI]
public enum RestoreUnreachableReason
{
    /// <summary>No restorable point of the target's timeline lies at or below the target (or every one disagrees with the archive).</summary>
    NoBase,

    /// <summary>The archived WAL misses LSNs between the base and the target: a gap, or records a crash's recovery applied that the archive lost.</summary>
    Gap,

    /// <summary>The target lies past everything the archive and the base's WAL hold.</summary>
    BeyondArchive,

    /// <summary>The instant lies before the archive's first time sample.</summary>
    BeforeCoverage,

    /// <summary>The timeline, or the ancestor that holds the target, is not in the store.</summary>
    TimelineUnknown,
}

/// <summary>
/// A restore to an LSN or a time cannot reach its target (impl/1418 §3.10). Nothing was written. <see cref="NearestRestorable"/> names what can be
/// restored instead.
/// </summary>
[PublicAPI]
public sealed class RestoreTargetUnreachableException : BackupException
{
    /// <summary>Creates the refusal.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="timelineId">The timeline the target was resolved on; <see cref="Guid.Empty"/> when none.</param>
    /// <param name="fromLsn">The first LSN of the missing range (or the target), when it applies.</param>
    /// <param name="toLsn">The last LSN of the missing range, when it applies.</param>
    /// <param name="nearestRestorable">The restorable LSNs nearest the target, ascending.</param>
    /// <param name="detail">What was wrong, in a sentence.</param>
    public RestoreTargetUnreachableException(RestoreUnreachableReason reason, Guid timelineId, long fromLsn, long toLsn,
        IReadOnlyList<long> nearestRestorable, string detail)
        : base(TyphonErrorCode.RestoreTargetUnreachable, FormatMessage(reason, detail, nearestRestorable))
    {
        Reason = reason;
        TimelineId = timelineId;
        FromLsn = fromLsn;
        ToLsn = toLsn;
        NearestRestorable = nearestRestorable ?? [];
    }

    /// <summary>Why.</summary>
    public RestoreUnreachableReason Reason { get; }

    /// <summary>The timeline the target was resolved on; <see cref="Guid.Empty"/> when none.</summary>
    public Guid TimelineId { get; }

    /// <summary>The first LSN of the missing range, or the target.</summary>
    public long FromLsn { get; }

    /// <summary>The last LSN of the missing range; 0 when it does not apply.</summary>
    public long ToLsn { get; }

    /// <summary>The restorable LSNs nearest the target, ascending: what a restore can reach instead.</summary>
    public IReadOnlyList<long> NearestRestorable { get; }

    private static string FormatMessage(RestoreUnreachableReason reason, string detail, IReadOnlyList<long> nearest) =>
        $"The restore target cannot be reached ({reason}): {detail}."
        + (nearest is { Count: > 0 } ? $" Restorable instead: LSN {string.Join(", ", nearest)}." : " Nothing near it is restorable.");
}

/// <summary>
/// The WAL archive of a timeline holds another history than this database's (WA-14): a copy of the database archives to the same store and timeline.
/// Archiving of that timeline stopped; what was archived is untouched. Never thrown into the engine: the archiver's state reports it.
/// </summary>
[PublicAPI]
public sealed class BackupArchiveDivergedException : BackupException
{
    /// <summary>Creates the failure.</summary>
    /// <param name="timelineId">The timeline.</param>
    /// <param name="detail">What disagrees.</param>
    public BackupArchiveDivergedException(Guid timelineId, string detail)
        : base(TyphonErrorCode.BackupArchiveDiverged, $"The WAL archive of timeline {timelineId} holds another history: {detail}.")
    {
        TimelineId = timelineId;
    }

    /// <summary>The timeline.</summary>
    public Guid TimelineId { get; }
}
