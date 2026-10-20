using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// An engine's configured backups (#1416, plan 1416 §3.4): the state of its backup operations, what each job does after its point (retention, the store's
/// refresh), and the published <see cref="BackupStatus"/>. Its tick (<c>BackupScheduler.Loop.cs</c>) decides when a point is due and keeps the status
/// file; the jobs themselves run on the coordinator's backup thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threads.</b> Two touch this object: the scheduler's own (the tick) and the backup thread (the job callbacks). Every mutation is one short critical
/// section on <c>_lock</c>; readers on any thread get the snapshot, an immutable record published with a release store (0 B per read).
/// </para>
/// <para>
/// <b>Never endangers the engine (BKP-16).</b> Nothing here throws to its caller: every failure becomes state and health. Nothing takes an engine lock: the
/// capture's own gate protocol is the coordinator's.
/// </para>
/// </remarks>
internal sealed partial class BackupScheduler : ResourceNode, IBackupJobObserver, IMetricSource
{
    private readonly DatabaseEngine _engine;
    private readonly BackupOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly BackupOpsState _state = new();
    private BackupStatus _status = BackupStatus.Off;

    public BackupScheduler(DatabaseEngine engine, BackupOptions options, IResource parent, ILogger logger) : base("BackupScheduler", ResourceType.Backup, parent)
    {
        _engine = engine;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _state.StartedUtc = DateTime.UtcNow;
    }

    /// <summary>The configuration.</summary>
    internal BackupOptions Options => _options;

    /// <summary>Test seam: the wall clock (start, display, ages in the status).</summary>
    internal Func<DateTime> UtcNow { get; set; } = static () => DateTime.UtcNow;

    /// <summary>Test seam: the monotonic clock the schedule runs on, in <see cref="Stopwatch"/> ticks.</summary>
    internal Func<long> MonoNow { get; set; } = Stopwatch.GetTimestamp;

    /// <summary>Test seam: the store's file system. Null in production.</summary>
    internal IStoreFileSystem StoreFileSystem { get; set; }

    /// <summary>The published snapshot: lock-free, 0 B per read.</summary>
    public BackupStatus Status => Volatile.Read(ref _status);

    /// <summary>Set by the close: nothing of the engine is touched after it (BKP-16).</summary>
    internal bool Stopped => Volatile.Read(ref _stopped);

    private bool _stopped;

    /// <summary>
    /// The close's first step, before the coordinator stops (BKP-16): no new job is started, the tick (when it runs) is joined within one tick, and a
    /// best-effort status says the engine stopped.
    /// </summary>
    internal void StopAndJoin()
    {
        if (Interlocked.Exchange(ref _stopped, true))
        {
            return;
        }

        StopLoop();
        lock (_lock)
        {
            _state.State = BackupRunState.Stopped;
            _state.Phase = BackupRunPhase.None;
            _state.Version++;
        }

        Publish();
        OnStopped();
    }

    partial void StopLoop();

    partial void OnStopped();

    partial void DisposeLoop();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopAndJoin();
            Parent?.RemoveChild(this);
            DisposeLoop();
        }

        base.Dispose(disposing);
    }

    /// <summary>The store the engine's points go into, its write buffers children of the coordinator.</summary>
    internal FileSystemBackupStore CreateStore() =>
        new(_options.StorePath, StoreFileSystem, writerOptions: new PackWriterOptions { Owner = new BufferOwner(_engine.MemoryAllocator, _engine.BackupCoordinator) });

    // ── Jobs (on the backup thread) ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    void IBackupJobObserver.JobStarted(BackupJobSource source, Guid requestId)
    {
        lock (_lock)
        {
            _state.State = BackupRunState.Running;
            _state.Phase = BackupRunPhase.Capture;
            if (requestId != Guid.Empty)
            {
                _state.LastRequestId = requestId;
            }

            _jobStartedMono = MonoNow();
            _state.Version++;
        }

        Publish();
    }

    void IBackupJobObserver.PhaseChanged(BackupRunPhase phase)
    {
        lock (_lock)
        {
            _state.Phase = phase;
            _state.Version++;
        }

        Publish();
    }

    void IBackupJobObserver.PointEnded(BackupJobSource source, IBackupPointStore store, in BackupPointInfo point, BackupCapture capture, Exception failure)
    {
        var now = UtcNow();
        var mono = MonoNow();
        var failureClass = failure == null ? FailureClass.None : Classify(failure);
        lock (_lock)
        {
            if (failure == null)
            {
                var summary = BackupOperations.Summarize((store as FileSystemBackupStore)?.LastPublished);
                _state.AttemptEnded = true;
                _state.ConsecutiveFailures = 0;
                _state.CaptureRefused = false;
                _state.StoreFailingSinceUtc = DateTime.MinValue;
                _state.PointsTotal++;
                _state.CbwCopiesTotal += Volatile.Read(ref capture.CbwCopies);
                _state.SuspectPagesTotal += point.SuspectCount;
                _state.LastDurationUs = (long)((mono - _jobStartedMono) * 1_000_000.0 / Stopwatch.Frequency);
                _state.MaxDurationUs = Math.Max(_state.MaxDurationUs, _state.LastDurationUs);
                _state.EmaDurationUs = _state.EmaDurationUs == 0 ? _state.LastDurationUs : ((_state.EmaDurationUs * 7) + _state.LastDurationUs) / 8;
                _state.LastBytes = point.PackBytes;
                _state.LastPages = point.PageCount;
                if (summary != null && summary.TimelineId == _timeline)
                {
                    // The store will say so too at the refresh; until then the point just published is this timeline's newest.
                    _state.Facts = WithLastPoint(_state.Facts, summary);
                }
            }
            else
            {
                if (failure is BackupAbortedException { Reason: BackupAbortReason.CbwStall })
                {
                    _state.CbwAbortsTotal++;
                }

                if (failureClass != FailureClass.Deferred)
                {
                    _state.AttemptEnded = true;
                    _state.FailuresTotal++;
                    _state.ConsecutiveFailures++;
                    _state.LastFailure = new BackupFailure(now, KindOf(failure), failure.Message);
                    _state.CaptureRefused |= failureClass == FailureClass.Permanent;
                    if (IsStoreFailure(failure) && _state.StoreFailingSinceUtc == DateTime.MinValue)
                    {
                        _state.StoreFailingSinceUtc = now;
                    }
                }
            }

            _state.Version++;
        }

        if (failure != null)
        {
            LogJobFailed(source, failureClass, failure.Message);
        }

        OnJobOutcome(failureClass, mono);
        Publish();
    }

    bool IBackupJobObserver.HasWorkAfterPoint => _options.Retention != null;

    void IBackupJobObserver.AfterPoint(FileSystemBackupStore store, in BackupPointInfo point, CancellationToken ct)
    {
        try
        {
            ApplyRetention(store, ct);
            RefreshStoreFacts();
        }
        catch (Exception e)
        {
            // BKP-16: nothing leaves the job.
            LogUnexpected("after point", e.Message);
        }
    }

    void IBackupJobObserver.JobEnded()
    {
        lock (_lock)
        {
            if (_state.State != BackupRunState.Stopped)
            {
                _state.State = BackupRunState.Idle;
            }

            _state.Phase = BackupRunPhase.None;
            _state.Version++;
        }

        Publish();
        OnJobEnded();
        JobEndedProbe?.Invoke();
    }

    /// <summary>Test hook, null in production: called on the backup thread when a job, retention included, has ended.</summary>
    internal Action JobEndedProbe { get; set; }

    /// <summary>Test hook, null in production: called on the backup thread before a job's retention.</summary>
    internal Action BeforeRetentionProbe { get; set; }

    private long _jobStartedMono;

    /// <summary>The engine's timeline, read once at <c>Start</c> (a restored bundle adopts its new one at construction, before the open completes).</summary>
    private Guid _timeline;

    /// <summary>The O4 schedule's view of an outcome (deferred failures retry soon, counted ones back off, permanent ones wait).</summary>
    partial void OnJobOutcome(FailureClass failureClass, long monoNow);

    /// <summary>The tick is woken when a job ends, so the status and the store facts follow at once.</summary>
    partial void OnJobEnded();

    /// <summary>
    /// The policy after every point the engine takes (DL-6), under the store's lock: compactions, then deletes (BKP-14). A store another writer holds defers it
    /// to the next point; a failure is health, never the job's.
    /// </summary>
    private void ApplyRetention(FileSystemBackupStore store, CancellationToken ct)
    {
        var policy = _options.Retention;
        if (policy == null)
        {
            return;
        }

        try
        {
            BeforeRetentionProbe?.Invoke();
            var drillPassed = ReadDrillPassed(store.FileSystem);
            using var session = BackupStore.OpenForMaintenance(store.Root, "retention", store.FileSystem, _logger);
            var result = RetentionExecutor.Apply(session, policy, null, drillPassed, UtcNow(), _timeline,
                new BackupLifecycleOptions { Cancellation = ct, Logger = _logger, Progress = (done, _) => Volatile.Write(ref _progressBytes, done) });
            lock (_lock)
            {
                _state.RetentionBlocked = result.Plan.Blocked;
                _state.RetentionFailed = false;
                _state.CompactionsTotal += result.Compactions.Count;
                _state.PacksDeletedTotal += result.Deleted.Count;
                _state.Version++;
            }
        }
        catch (BackupStoreLockedException)
        {
            // Another writer (a prune) holds the store: the next point's retention runs the policy again.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            LogRetentionFailed(e.Message);
            lock (_lock)
            {
                _state.RetentionFailed = true;
                _state.Version++;
            }
        }
        finally
        {
            Volatile.Write(ref _progressBytes, 0);
        }
    }

    private long _progressBytes;

    /// <summary>
    /// Reads what the scheduler needs of the store (DL-19): the newest restorable point of the engine's timeline, the restorable count, the bytes, the depth
    /// of the newest point's chain, its suspects by occupancy, the drills. The only step that reads the store and allocates; a failure marks the store failing.
    /// </summary>
    internal void RefreshStoreFacts()
    {
        StoreFacts facts = null;
        Exception failure = null;
        try
        {
            facts = ReadStoreFacts(StoreFileSystem ?? Internals.StoreFileSystem.Instance);
        }
        catch (Exception e) when (e is BackupException or IOException or UnauthorizedAccessException)
        {
            failure = e;
        }

        lock (_lock)
        {
            if (failure == null)
            {
                _state.Facts = facts;
                _state.StoreFailingSinceUtc = DateTime.MinValue;
            }
            else if (_state.StoreFailingSinceUtc == DateTime.MinValue)
            {
                _state.StoreFailingSinceUtc = UtcNow();
            }

            _state.Version++;
        }

        if (failure != null)
        {
            LogStoreUnreadable(failure.Message);
        }
    }

    private StoreFacts ReadStoreFacts(IStoreFileSystem fs)
    {
        var root = _options.StorePath;
        if (!fs.FileExists(Path.Combine(root, "store.json")))
        {
            // No point was ever taken: an empty store, not an unreadable one.
            return new StoreFacts { Known = true };
        }

        var view = StoreView.Open(root, fs, _logger);
        var restorable = ChainResolver.RestorablePoints(view);
        var newest = NewestOnTimeline(view, restorable, _timeline);

        long bytes = 0;
        foreach (var p in view.Catalog.Packs)
        {
            bytes += p.Size;
        }

        var (allocated, free) = newest is { Point.Suspect: true } ? SuspectClassifier.Classify(fs, view, newest.Point.BackupId) : (0, 0);
        var drills = DrillRecords.ReadNewestPerPoint(fs, view.Paths.Root);
        return new StoreFacts
        {
            Known = true,
            RestorablePoints = restorable.Count,
            StoreBytes = bytes,
            LastPoint = BackupOperations.Summarize(newest?.Point),
            ChainDepth = newest?.Depth ?? 0,
            AllocatedSuspects = allocated,
            FreeSuspects = free,
            LastDrill = drills.Newest,
            LastPassedDrill = drills.NewestPassed,
            DrillPassed = drills.Passed,
        };
    }

    /// <summary>
    /// The newest restorable point of <paramref name="timeline"/> (DL-19): the last success the schedule and the health count from. Never another
    /// timeline's: after a restore, the old timeline's newest point would make the new one wait a whole interval for its first point.
    /// </summary>
    internal static ResolvedChain NewestOnTimeline(StoreView view, System.Collections.Generic.List<uint> restorable, Guid timeline)
    {
        for (var i = restorable.Count - 1; i >= 0; i--)
        {
            var chain = ChainResolver.Resolve(view, restorable[i]);
            if (chain.Point.TimelineId == timeline)
            {
                return chain;
            }
        }

        return null;
    }

    private uint[] ReadDrillPassed(IStoreFileSystem fs)
    {
        try
        {
            return DrillRecords.ReadNewestPerPoint(fs, _options.StorePath).Passed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Volatile.Read(ref _status).LastPassedDrill is { } d ? [d.BackupId] : [];
        }
    }

    private static StoreFacts WithLastPoint(StoreFacts facts, BackupPointSummary point) => new()
    {
        Known = facts.Known,
        RestorablePoints = facts.RestorablePoints,
        StoreBytes = facts.StoreBytes,
        LastPoint = point,
        ChainDepth = facts.ChainDepth,
        AllocatedSuspects = facts.AllocatedSuspects,
        FreeSuspects = facts.FreeSuspects,
        LastDrill = facts.LastDrill,
        LastPassedDrill = facts.LastPassedDrill,
        DrillPassed = facts.DrillPassed,
    };

    // ── Metrics (plan 1416 §4.4) ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The last point's duration (µs), pack bytes and pages, for the gauges of <see cref="BackupMetricsExporter"/>.</summary>
    internal (long DurationUs, long Bytes, long Pages) LastPointMetrics
    {
        get
        {
            lock (_lock)
            {
                return (_state.LastDurationUs, _state.LastBytes, _state.LastPages);
            }
        }
    }

    /// <inheritdoc />
    public void ReadMetrics(IMetricWriter writer)
    {
        var s = Status;
        long compactions, deleted, last, ema, max;
        lock (_lock)
        {
            (compactions, deleted, last, ema, max) =
                (_state.CompactionsTotal, _state.PacksDeletedTotal, _state.LastDurationUs, _state.EmaDurationUs, _state.MaxDurationUs);
        }

        writer.WriteThroughput("Points", s.PointsTotal);
        writer.WriteThroughput("Failures", s.FailuresTotal);
        writer.WriteThroughput("CbwCopies", s.CbwCopiesTotal);
        writer.WriteThroughput("CbwAborts", s.CbwAbortsTotal);
        writer.WriteThroughput("SuspectPages", s.SuspectPagesTotal);
        writer.WriteThroughput("Compactions", compactions);
        writer.WriteThroughput("PacksDeleted", deleted);
        writer.WriteDuration("BackupPoint", last, ema, max);
    }

    /// <inheritdoc />
    public void ResetPeaks()
    {
        lock (_lock)
        {
            _state.MaxDurationUs = 0;
        }
    }

    // ── The snapshot ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What the health verdict is judged on, from the state (under <c>_lock</c>).</summary>
    private HealthInputs Inputs()
    {
        var facts = _state.Facts;
        var archive = _state.Archive;
        return new HealthInputs
        {
            Configured = true,
            Interval = _options.Interval,
            DrillWindow = _options.DrillWindow,
            StartedUtc = _state.StartedUtc,
            StoreKnown = facts.Known,
            RestorablePoints = facts.RestorablePoints,
            LastSuccessUtc = facts.LastPoint?.TailEndUtc ?? DateTime.MinValue,
            LastSuccessCheckpointLsn = facts.LastPoint?.CheckpointLsn ?? 0,
            AttemptEnded = _state.AttemptEnded,
            ConsecutiveFailures = _state.ConsecutiveFailures,
            StoreFailingSinceUtc = _state.StoreFailingSinceUtc,
            CaptureRefused = _state.CaptureRefused,
            ArchiveCondition = archive.Condition,
            ArchiveLastGapToLsn = archive.LastGapToLsn,
            ArchiveLagSeconds = archive.LagSeconds,
            ArchiveMaxSegmentAge = _options.Archive?.MaxSegmentAge ?? TimeSpan.Zero,
            LastDrillUtc = facts.LastDrill?.Utc ?? DateTime.MinValue,
            LastDrillFailed = facts.LastDrill?.Outcome == BackupDrillOutcome.Failed,
            AnyDrillPassed = facts.LastPassedDrill != null,
            LastPointSuspectAllocated = facts.AllocatedSuspects > 0,
            RetentionBlocked = _state.RetentionBlocked,
            RetentionFailed = _state.RetentionFailed,
        };
    }

    /// <summary>Builds and publishes the snapshot (allocates: called on a change, and every tick while a job runs).</summary>
    internal void Publish()
    {
        var now = UtcNow();
        // A tick left behind by a close (BKP-16) touches nothing of the engine: no progress to read.
        var running = Stopped ? null : _engine.BackupCoordinator?.Running;
        var (added, expected) = running?.Progress ?? (0, 0);
        BackupStatus status;
        lock (_lock)
        {
            var inputs = Inputs();
            var (health, reason) = BackupHealthEvaluator.Evaluate(inputs, now);
            _publishedHealth = (health, reason);
            _publishedInputs = inputs;
            _publishedVersion = _state.Version;
            var facts = _state.Facts;
            status = new BackupStatus(health, reason, BackupHealthEvaluator.Explain(inputs, reason, now), _state.State, _state.Phase, added, expected,
                Volatile.Read(ref _progressBytes), facts.LastPoint, facts.LastPoint?.TailEndUtc ?? DateTime.MinValue, _state.NextDueUtc,
                _state.ConsecutiveFailures, _state.LastFailure, _state.PointsTotal, _state.FailuresTotal, _state.CbwCopiesTotal, _state.CbwAbortsTotal,
                _state.SuspectPagesTotal, facts.ChainDepth, facts.StoreBytes, facts.RestorablePoints, facts.FreeSuspects, facts.LastDrill, facts.LastPassedDrill,
                _state.RetentionBlocked, ArchiveStatus(_state.Archive), _state.LastRequestId, now);
        }

        Volatile.Write(ref _status, status);
    }

    private (BackupHealth, BackupHealthReason) _publishedHealth;
    private long _publishedVersion = -1;

    private static BackupArchiveStatus ArchiveStatus(in WalArchiveState a) => a.Condition == WalArchiveCondition.Off
        ? BackupArchiveStatus.Off
        : new BackupArchiveStatus((BackupArchiveState)a.Condition, a.TimelineId, a.ArchivedThroughLsn, a.LagBytes, a.BacklogBytes, a.LagSeconds, a.GapsTotal,
            a.LastGapFromLsn, a.LastGapToLsn, a.LastArchivedSealUtc, 0, a.LastError);

    // ── Failures ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>How a failed job counts (DL-20).</summary>
    internal enum FailureClass : byte
    {
        /// <summary>No failure.</summary>
        None,

        /// <summary>Not a failure: another writer holds the store, a job is already running, the engine is closing, the caller cancelled. Retried soon.</summary>
        Deferred,

        /// <summary>No retry helps until the engine's state changes: write tracking off, an engine fault. Health Failing (CaptureRefused).</summary>
        Permanent,

        /// <summary>A failure: counted, retried on a backoff.</summary>
        Counted,
    }

    internal static FailureClass Classify(Exception e) => e switch
    {
        BackupStoreLockedException => FailureClass.Deferred,
        TyphonException { ErrorCode: TyphonErrorCode.BackupInProgress } => FailureClass.Deferred,
        BackupAbortedException { Reason: BackupAbortReason.EngineStopping or BackupAbortReason.Cancelled } => FailureClass.Deferred,
        BackupAbortedException { Reason: BackupAbortReason.TrackingDisabled or BackupAbortReason.EngineFault } => FailureClass.Permanent,
        _ => FailureClass.Counted,
    };

    private static bool IsStoreFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or BackupAbortedException { Reason: BackupAbortReason.SinkFailed }
        || e.InnerException is IOException or UnauthorizedAccessException;

    private static string KindOf(Exception e) => e is BackupAbortedException a ? a.Reason.ToString() : e.GetType().Name;

    [LoggerMessage(LogLevel.Warning, "Backup ({Source}) failed, {Class}: {Message}")]
    private partial void LogJobFailed(BackupJobSource source, FailureClass @class, string message);

    [LoggerMessage(LogLevel.Warning, "Backup retention failed: {Message}")]
    private partial void LogRetentionFailed(string message);

    [LoggerMessage(LogLevel.Warning, "Backup store unreadable: {Message}")]
    private partial void LogStoreUnreadable(string message);

    [LoggerMessage(LogLevel.Error, "Backup scheduler: unexpected failure in {Step}: {Message}")]
    private partial void LogUnexpected(string step, string message);
}
