using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Typhon.Engine.Internals;

/// <summary>Who asked for a backup job.</summary>
internal enum BackupJobSource : byte
{
    /// <summary><see cref="IBackupService.CreateAsync(BackupRequestKind, CancellationToken)"/> from the application.</summary>
    Api,

    /// <summary>The scheduler: the interval elapsed.</summary>
    Schedule,

    /// <summary>A <c>backup.request</c> file in the bundle.</summary>
    Request,

    /// <summary>The WAL archiver asked for a point (a gap, a release, a new timeline).</summary>
    Archive,
}

/// <summary>
/// What the engine's backup operations (#1416) see of each job, called on the backup thread: the job starting, its phases, its point (published and
/// verified, or failed), then the work after a point — retention and the store's refresh — which runs after the caller's task completed.
/// </summary>
internal interface IBackupJobObserver
{
    void JobStarted(BackupJobSource source, Guid requestId);

    /// <summary>
    /// The capture was refused before any job started (the engine closing, a job running, write tracking off, an engine fault): called on the caller's
    /// thread, outside the coordinator's lock, before the caller's task fails. Never throws.
    /// </summary>
    void JobRefused(BackupJobSource source, Guid requestId, Exception refusal);

    void PhaseChanged(BackupRunPhase phase);

    /// <summary>The capture and its verify ended: <paramref name="failure"/> null when the point is published and verified.</summary>
    void PointEnded(BackupJobSource source, IBackupPointStore store, in BackupPointInfo point, BackupCapture capture, Exception failure);

    /// <summary>Whether <see cref="AfterPoint"/> has work (a retention policy): the job then stays one job until it ends.</summary>
    bool HasWorkAfterPoint { get; }

    /// <summary>After a published, verified point: retention, then the store's refresh. Never throws.</summary>
    void AfterPoint(FileSystemBackupStore store, in BackupPointInfo point, CancellationToken ct);

    void JobEnded();
}

/// <summary>
/// Runs an engine's online backups, one at a time, on one lazily started background thread (<c>Typhon-Backup</c>), and owns the session's backup
/// lineage. Stopped and joined first thing at engine dispose, before any drain or final checkpoint, so an abandoned capture's page set is merged back
/// into the live map before the close persists it (BKP-07).
/// </summary>
internal sealed partial class BackupCoordinator : ResourceNode
{
    private readonly DatabaseEngine _engine;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly CancellationTokenSource _stopping = new();
    private Thread _thread;
    private Request _pending;
    private BackupCapture _current;
    private BackupCapture _progress;
    private bool _jobRunning;
    private bool _stopped;
    private bool _detached;

    private sealed record Request(IBackupPointStore Store, BackupKind Kind, CancellationToken Ct, TaskCompletionSource<BackupPointInfo> Completion,
        BackupJobSource Source, Guid RequestId);

    /// <summary>Creates the coordinator under <paramref name="parent"/>; no thread runs until the first capture.</summary>
    public BackupCoordinator(DatabaseEngine engine, IResource parent, ILogger logger) : base("BackupCoordinator", ResourceType.Backup, parent)
    {
        _engine = engine;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The session's lineage: what the last point was, and whether the live change map is trusted (BKP-06).</summary>
    internal BackupLineage Lineage { get; } = new();

    /// <summary>Capture tuning.</summary>
    internal BackupCaptureOptions Options { get; } = new();

    /// <summary>Test hook, null in production: passed to every capture.</summary>
    internal Action<CapturePhase, int> Probe { get; set; }

    /// <summary>Test seam, null in production: the copy-before-write clock handed to every capture (<see cref="BackupCapture.CbwClock"/>).</summary>
    internal Func<long> CbwClock { get; set; }

    /// <summary>Test seam, null in production: the wall clock a capture stamps its point with (<see cref="BackupCapture.WallClock"/>).</summary>
    internal Func<DateTime> WallClock { get; set; }

    /// <summary>Test hook, null in production: called with each capture before it runs.</summary>
    internal Action<BackupCapture> CaptureCreated { get; set; }

    /// <summary>The capture of the last request, kept for inspection once it ended.</summary>
    internal BackupCapture LastCapture { get; private set; }

    /// <summary>Test hook, null in production: invoked by the backup thread when it wakes, before it takes the pending request.</summary>
    internal Action BeforeTakingRequest { get; set; }

    /// <summary>Whether the last stop gave up joining the backup thread (its capture and buffers were left to it).</summary>
    internal bool Detached => Volatile.Read(ref _detached);

    /// <summary>The engine's backup operations (#1416), told of every job; null when backup is not configured.</summary>
    internal IBackupJobObserver Observer { get; set; }

    /// <summary>Verify each new pack in full rather than quickly (<see cref="BackupOptions.FullVerifyAfterBackup"/>).</summary>
    internal bool FullVerifyAfterBackup { get; set; }

    /// <summary>A job is pending or running (capture, verify, or the retention after it): a new one is refused with <c>BackupInProgress</c>.</summary>
    internal bool Busy
    {
        get
        {
            lock (_lock)
            {
                return _pending != null || _jobRunning;
            }
        }
    }

    /// <summary>The capture running now, or null: its progress, for the scheduler's heartbeat (DL-31). Lock-free.</summary>
    internal BackupCapture Running => Volatile.Read(ref _progress);

    /// <summary>Test seam, null in production: called on the backup thread between a point's publish and its verify (a store damaged there).</summary>
    internal Action<BackupPointInfo> BeforePostVerify { get; set; }

    /// <summary>
    /// Takes a backup point into <paramref name="store"/>: full or incremental per admission, <paramref name="kind"/> = <see cref="BackupKind.Full"/>
    /// forcing a full one. Fails fast when another capture is running or the engine cannot take a consistent point.
    /// </summary>
    internal Task<BackupPointInfo> CaptureAsync(IBackupPointStore store, BackupKind kind, CancellationToken ct,
        BackupJobSource source = BackupJobSource.Api, Guid requestId = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var completion = new TaskCompletionSource<BackupPointInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception refused;
        lock (_lock)
        {
            refused = _stopped ? new BackupAbortedException(BackupAbortReason.EngineStopping, "the engine is closing.")
                : _pending != null || _jobRunning ? new TyphonException(TyphonErrorCode.BackupInProgress, "A backup is already running on this engine.")
                : !DataFileWriteTracking.GateEnabled ? new BackupAbortedException(BackupAbortReason.TrackingDisabled,
                    "TYPHON_DATAFILE_GATE=0: data-file writes bypass the write gate, so no consistent image can be frozen.")
                : _engine.BackupRefusal() is { } refusal ? new BackupAbortedException(BackupAbortReason.EngineFault, refusal)
                : null;
            if (refused == null)
            {
                _pending = new Request(store, kind, ct, completion, source, requestId);
                if (_thread == null)
                {
                    _thread = new Thread(ThreadLoop) { Name = "Typhon-Backup", IsBackground = true, Priority = ThreadPriority.BelowNormal };
                    _thread.Start();
                }

                _wake.Set();
            }
        }

        if (refused != null)
        {
            // An outcome like a failed job's (BKP-15): the observer classifies it before the caller sees the failed task.
            Observer?.JobRefused(source, requestId, refused);
            completion.SetException(refused);
        }

        return completion.Task;
    }

    private void ThreadLoop()
    {
        while (true)
        {
            _wake.Wait();
            BeforeTakingRequest?.Invoke();
            Request request;
            BackupCapture capture;
            lock (_lock)
            {
                _wake.Reset();
                if (_stopped && _pending == null)
                {
                    return;
                }

                request = _pending;
                _pending = null;
                if (request == null)
                {
                    continue;
                }

                capture = new BackupCapture(_engine, Options, this, _logger, Probe) { CbwClock = CbwClock, WallClock = WallClock };
                _current = capture;
                _jobRunning = true;
                LastCapture = capture;
            }

            RunJob(request, capture);
        }
    }

    /// <summary>
    /// One job (plan 1416 §3.4): capture → verify the new pack (DL-28) → the caller's task completes → retention and the store's refresh (the observer's).
    /// A point that fails its verify revokes the lineage's trust, so the next point is full rather than an incremental over a damaged parent (DL-26).
    /// </summary>
    private void RunJob(Request request, BackupCapture capture)
    {
        var observer = Observer;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Ct, _stopping.Token);
        BackupPointInfo point = default;
        var published = false;
        try
        {
            observer?.JobStarted(request.Source, request.RequestId);
            observer?.PhaseChanged(BackupRunPhase.Capture);
            Volatile.Write(ref _progress, capture);
            try
            {
                CaptureCreated?.Invoke(capture);
                if (_stopping.IsCancellationRequested)
                {
                    capture.Abort(BackupAbortReason.EngineStopping);
                }

                var span = TyphonEvent.BeginBackupCapture((byte)(request.Kind == BackupKind.Full ? 1 : 2));
                try
                {
                    point = capture.Run(request.Store, request.Kind, Lineage, linked.Token);
                    span.BackupId = point.BackupId;
                    span.Pages = point.PageCount;
                    span.CbwCopies = Volatile.Read(ref capture.CbwCopies);
                }
                finally
                {
                    span.Dispose();
                }

                observer?.PhaseChanged(BackupRunPhase.Verify);
                BeforePostVerify?.Invoke(point);
                VerifyPublished(request.Store as FileSystemBackupStore, point);
                published = true;
                observer?.PointEnded(request.Source, request.Store, point, capture, null);

                // The caller's task completes now; with no work after the point, the next job may start as soon as it sees it.
                EndJobUnless(observer is { HasWorkAfterPoint: true } && request.Store is FileSystemBackupStore);
                request.Completion.SetResult(point);
            }
            catch (Exception e)
            {
                published = false;
                observer?.PointEnded(request.Source, request.Store, point, capture, e);
                EndJobUnless(false);
                request.Completion.SetException(e);
            }
            finally
            {
                Volatile.Write(ref _progress, null);
                lock (_lock)
                {
                    _current = null;
                }

                capture.Dispose();
            }

            if (published && observer != null && request.Store is FileSystemBackupStore store)
            {
                observer.PhaseChanged(BackupRunPhase.Retention);
                observer.AfterPoint(store, point, linked.Token);
            }
        }
        finally
        {
            lock (_lock)
            {
                _jobRunning = false;
            }

            observer?.JobEnded();
        }
    }

    private void EndJobUnless(bool moreToDo)
    {
        if (!moreToDo)
        {
            lock (_lock)
            {
                _jobRunning = false;
            }
        }
    }

    /// <summary>
    /// DL-28: the new pack alone, quick (headers, index, tail, suspects) or full, and its chain's resolution from the headers — the older packs were verified
    /// when written and never change (BKP-08). A failure leaves the point published but marks the lineage untrusted (DL-26) and fails the job.
    /// </summary>
    private void VerifyPublished(FileSystemBackupStore store, in BackupPointInfo point)
    {
        if (store == null)
        {
            return;
        }

        StoreView view;
        try
        {
            view = StoreView.Open(store.Root, store.FileSystem, _logger);
        }
        catch (Exception e) when (e is BackupException or System.IO.IOException or UnauthorizedAccessException)
        {
            // The store cannot be read back right now: the point is published and durable, and nothing says it is damaged. Unverified, not failed.
            LogPostVerifySkipped(point.BackupId, e.Message);
            return;
        }

        string problem;
        var span = TyphonEvent.BeginBackupVerify(point.BackupId, (byte)(FullVerifyAfterBackup ? 1 : 0));
        try
        {
            var pack = BackupVerifier.VerifyPackFile(store.FileSystem, view, System.IO.Path.GetFileName(point.PackPath),
                FullVerifyAfterBackup ? VerifyLevel.Full : VerifyLevel.Quick);
            var damage = pack.Findings.Find(f => f.Severity == VerifySeverity.Damage);
            problem = pack.Status != PackVerifyStatus.Ok ? $"{damage.Code}: {damage.Message}"
                : !ChainResolver.TryResolve(view, point.BackupId, out _, out _, out var why) ? "its chain does not resolve: " + why
                : null;
        }
        catch (Exception e) when (e is BackupException or System.IO.IOException or UnauthorizedAccessException)
        {
            problem = e.Message;
        }
        finally
        {
            span.Dispose();
        }

        if (problem == null)
        {
            return;
        }

        Lineage.Trusted = false;
        LogPostVerifyFailed(point.BackupId, problem);
        throw new BackupPackCorruptException(point.PackPath, "PostVerify", -1, $"point #{point.BackupId} failed its verify after publish ({problem}); "
                                                                              + "the next point will be full");
    }

    /// <summary>
    /// Stops the coordinator: fails a request no capture has taken yet, aborts a running capture (which runs its failure path: map merged back, floor
    /// released, hook removed, point abandoned), and joins the thread. Every wait of a capture observes the cancellation but a store call and the gate's
    /// bounded acquires, so the join waits <see cref="BackupCaptureOptions.StopTimeout"/>. If the thread does not finish in it (a store stuck in I/O),
    /// the hook is removed from here, the lineage is revoked (the abandoned page set may never be merged back), and the capture is detached: its
    /// native memory, and the coordinator's, is left to the still-running thread rather than freed under it.
    /// </summary>
    internal void StopAndJoin()
    {
        BackupCapture running;
        Thread thread;
        Request pending;
        lock (_lock)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            running = _current;
            thread = _thread;
            pending = _pending;
            _pending = null;
        }

        pending?.Completion.TrySetException(new BackupAbortedException(BackupAbortReason.EngineStopping, "the engine is closing."));
        running?.Abort(BackupAbortReason.EngineStopping);
        _stopping.Cancel();
        _wake.Set();
        var timeout = Options.StopTimeout;
        if (thread != null && !thread.Join(timeout))
        {
            LogStopTimedOut(timeout.TotalSeconds);
            Lineage.Revoke();
            running?.Detach();
            Volatile.Write(ref _detached, true);
            running?.Uninstall();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopAndJoin();
            Parent?.RemoveChild(this);
            if (Detached)
            {
                // The backup thread still runs and owns blocks parented here: disposing this node would free them under it.
                return;
            }

            _wake.Dispose();
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }

    [LoggerMessage(LogLevel.Error,
        "Backup thread did not stop within {Seconds} s; its copy-before-write hook was removed from the closing thread and its buffers left to it")]
    private partial void LogStopTimedOut(double seconds);

    [LoggerMessage(LogLevel.Error, "Backup point #{BackupId} failed its verify after publish ({Problem}); the next point will be full")]
    private partial void LogPostVerifyFailed(uint backupId, string problem);

    [LoggerMessage(LogLevel.Warning, "Backup point #{BackupId} is published but could not be verified: the store is unreadable ({Reason})")]
    private partial void LogPostVerifySkipped(uint backupId, string reason);
}
