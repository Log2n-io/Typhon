using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// What an operation targets: a backup point (the newest one, or one by its id), or — for a restore only — an LSN or an instant of an archived WAL
/// timeline (#1418), which restores the newest point at or below it and replays the archive up to it.
/// </summary>
/// <remarks>
/// Verify, list and the other point-scoped operations take a point and throw <see cref="ArgumentException"/> for an archive target: an LSN names no pack.
/// </remarks>
[PublicAPI]
public readonly struct PointSelector : IEquatable<PointSelector>
{
    private enum Target : byte
    {
        Point = 0,
        Lsn = 1,
        Time = 2,
    }

    private readonly Target _target;

    private PointSelector(bool latest, uint backupId)
    {
        IsLatest = latest;
        BackupId = backupId;
    }

    private PointSelector(Target target, long lsn, DateTime timeUtc, Guid? timeline)
    {
        _target = target;
        Lsn = lsn;
        TimeUtc = timeUtc;
        TimelineId = timeline;
    }

    /// <summary>The point with the highest id in the store.</summary>
    public static PointSelector Latest => new(true, 0);

    /// <summary>The point whose id is <paramref name="backupId"/>.</summary>
    /// <param name="backupId">A point id (1 or more).</param>
    public static PointSelector ById(uint backupId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(backupId);
        return new PointSelector(false, backupId);
    }

    /// <summary>
    /// The archived WAL up to <paramref name="lsn"/>: the restore ends at the last frame (a transaction, or one fence emission) ending at or below it.
    /// </summary>
    /// <param name="lsn">The target LSN (1 or more).</param>
    /// <param name="timeline">The WAL timeline; <c>null</c> = the newest one, walking back to the ancestor that holds <paramref name="lsn"/>.</param>
    public static PointSelector AtLsn(long lsn, Guid? timeline = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lsn);
        return new PointSelector(Target.Lsn, lsn, default, timeline);
    }

    /// <summary>
    /// The archived WAL as it was durable at <paramref name="utc"/>, to the archive's time-sample resolution: no transaction durable after it is
    /// included, and one durable up to a sample interval before it may be left out.
    /// </summary>
    /// <param name="utc">The instant; its <see cref="DateTime.Kind"/> must be <see cref="DateTimeKind.Utc"/>.</param>
    /// <param name="timeline">The WAL timeline; <c>null</c> = the newest one, walking back to the ancestor that holds the instant.</param>
    public static PointSelector AtTime(DateTime utc, Guid? timeline = null)
    {
        if (utc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException($"A restore instant must be UTC (its Kind is {utc.Kind}): a local time is ambiguous at a clock change.", nameof(utc));
        }

        return new PointSelector(Target.Time, 0, utc, timeline);
    }

    /// <summary>Whether this selects the newest point.</summary>
    public bool IsLatest { get; }

    /// <summary>The selected id; <c>0</c> for <see cref="Latest"/> and for an archive target.</summary>
    public uint BackupId { get; }

    /// <summary>Whether this targets the WAL archive (<see cref="AtLsn"/>, <see cref="AtTime"/>) rather than a point.</summary>
    public bool IsArchiveTarget => _target != Target.Point;

    /// <summary>Whether this is an <see cref="AtTime"/> target.</summary>
    public bool IsTimeTarget => _target == Target.Time;

    /// <summary>The target LSN of <see cref="AtLsn"/>; <c>0</c> otherwise.</summary>
    public long Lsn { get; }

    /// <summary>The target instant of <see cref="AtTime"/>; <see cref="DateTime.MinValue"/> otherwise.</summary>
    public DateTime TimeUtc { get; }

    /// <summary>The WAL timeline of an archive target; <c>null</c> = the newest.</summary>
    public Guid? TimelineId { get; }

    /// <summary>Throws <see cref="ArgumentException"/> for an archive target: <paramref name="operation"/> works on points only.</summary>
    internal void RequirePoint(string operation)
    {
        if (IsArchiveTarget)
        {
            throw new ArgumentException($"{operation} works on backup points; {this} is a WAL archive target, which only a restore takes.");
        }
    }

    /// <inheritdoc />
    public bool Equals(PointSelector other) => _target == other._target && IsLatest == other.IsLatest && BackupId == other.BackupId && Lsn == other.Lsn
                                               && TimeUtc == other.TimeUtc && TimelineId == other.TimelineId;

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is PointSelector other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_target, IsLatest, BackupId, Lsn, TimeUtc, TimelineId);

    /// <inheritdoc />
    public override string ToString() => _target switch
    {
        Target.Lsn => TimelineId == null ? $"LSN {Lsn}" : $"LSN {Lsn} on timeline {TimelineId}",
        Target.Time => TimelineId == null ? $"time {TimeUtc:O}" : $"time {TimeUtc:O} on timeline {TimelineId}",
        _ => IsLatest ? "latest" : $"#{BackupId}",
    };
}
