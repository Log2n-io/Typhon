using System;

namespace Typhon.Engine.Internals;

/// <summary>What the scheduler knows of the store, as of its last read (plan 1416 §3.4 "store facts"): the only thing it reads the store for.</summary>
internal sealed class StoreFacts
{
    public static readonly StoreFacts Unknown = new();

    /// <summary>The store was read: the members below mean something.</summary>
    public bool Known { get; init; }

    public int RestorablePoints { get; init; }

    public long StoreBytes { get; init; }

    /// <summary>The newest restorable point of the engine's timeline, or null (DL-19: never another timeline's).</summary>
    public BackupPointSummary LastPoint { get; init; }

    /// <summary>The packs a restore of <see cref="LastPoint"/> reads.</summary>
    public int ChainDepth { get; init; }

    /// <summary>Suspects of <see cref="LastPoint"/> on pages its occupancy marks allocated / free.</summary>
    public int AllocatedSuspects { get; init; }

    public int FreeSuspects { get; init; }

    /// <summary>The newest drill and the newest passed one (any point), or null.</summary>
    public BackupDrillSummary LastDrill { get; init; }

    public BackupDrillSummary LastPassedDrill { get; init; }

    /// <summary>The points whose newest drill passed: KeepLastDrilled's input.</summary>
    public uint[] DrillPassed { get; init; } = [];
}

/// <summary>
/// The engine's backup operations state (plan 1416 §3.4): mutated by the scheduler thread and the backup thread under the scheduler's lock, one change at a
/// time; read by others only through the immutable <see cref="BackupStatus"/> snapshot the scheduler publishes.
/// </summary>
internal sealed class BackupOpsState
{
    public DateTime StartedUtc;
    public bool AttemptEnded;
    public int ConsecutiveFailures;
    public BackupFailure LastFailure;
    public long PointsTotal;
    public long FailuresTotal;
    public long CbwCopiesTotal;
    public long CbwAbortsTotal;
    public long SuspectPagesTotal;
    public long CompactionsTotal;
    public long PacksDeletedTotal;

    /// <summary>Since when the store has been failing; MinValue = it is not.</summary>
    public DateTime StoreFailingSinceUtc = DateTime.MinValue;

    /// <summary>The engine refused to capture for a reason no retry changes (write tracking off, an engine fault).</summary>
    public bool CaptureRefused;

    public BackupRetentionBlock RetentionBlocked;
    public bool RetentionFailed;

    public StoreFacts Facts = StoreFacts.Unknown;

    public BackupRunState State = BackupRunState.Idle;
    public BackupRunPhase Phase;
    public Guid LastRequestId;
    public DateTime NextDueUtc = DateTime.MinValue;

    /// <summary>The last point's duration and size, for the metrics.</summary>
    public long LastDurationUs;
    public long EmaDurationUs;
    public long MaxDurationUs;
    public long LastBytes;
    public long LastPages;

    /// <summary>The archive's state at the last tick (#1418).</summary>
    public WalArchiveState Archive;

    /// <summary>Bumped on every change: the scheduler republishes the snapshot and rewrites the status file when it moved.</summary>
    public long Version;
}
