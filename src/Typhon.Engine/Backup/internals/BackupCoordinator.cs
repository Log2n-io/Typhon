using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Typhon.Engine.Internals;

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
    private bool _stopped;

    private sealed record Request(IBackupPointStore Store, BackupKind Kind, CancellationToken Ct, TaskCompletionSource<BackupPointInfo> Completion);

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

    /// <summary>Test hook, null in production: called with each capture before it runs.</summary>
    internal Action<BackupCapture> CaptureCreated { get; set; }

    /// <summary>The capture of the last request, kept for inspection once it ended.</summary>
    internal BackupCapture LastCapture { get; private set; }

    /// <summary>
    /// Takes a backup point into <paramref name="store"/>: full or incremental per admission, <paramref name="kind"/> = <see cref="BackupKind.Full"/>
    /// forcing a full one. Fails fast when another capture is running or the engine cannot take a consistent point.
    /// </summary>
    internal Task<BackupPointInfo> CaptureAsync(IBackupPointStore store, BackupKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var completion = new TaskCompletionSource<BackupPointInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_stopped)
            {
                completion.SetException(new BackupAbortedException(BackupAbortReason.EngineStopping, "the engine is closing."));
                return completion.Task;
            }

            if (_pending != null || _current != null)
            {
                completion.SetException(new TyphonException(TyphonErrorCode.BackupInProgress, "A backup is already running on this engine."));
                return completion.Task;
            }

            if (!DataFileWriteTracking.GateEnabled)
            {
                completion.SetException(new BackupAbortedException(BackupAbortReason.TrackingDisabled,
                    "TYPHON_DATAFILE_GATE=0: data-file writes bypass the write gate, so no consistent image can be frozen."));
                return completion.Task;
            }

            var refusal = _engine.BackupRefusal();
            if (refusal != null)
            {
                completion.SetException(new BackupAbortedException(BackupAbortReason.EngineFault, refusal));
                return completion.Task;
            }

            _pending = new Request(store, kind, ct, completion);
            if (_thread == null)
            {
                _thread = new Thread(ThreadLoop) { Name = "Typhon-Backup", IsBackground = true, Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }

            _wake.Set();
        }

        return completion.Task;
    }

    private void ThreadLoop()
    {
        while (true)
        {
            _wake.Wait();
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

                capture = new BackupCapture(_engine, Options, this, _logger, Probe);
                _current = capture;
                LastCapture = capture;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(request.Ct, _stopping.Token);
            try
            {
                CaptureCreated?.Invoke(capture);
                if (_stopping.IsCancellationRequested)
                {
                    capture.Abort(BackupAbortReason.EngineStopping);
                }

                request.Completion.SetResult(capture.Run(request.Store, request.Kind, Lineage, linked.Token));
            }
            catch (Exception e)
            {
                request.Completion.SetException(e);
            }
            finally
            {
                lock (_lock)
                {
                    _current = null;
                }

                capture.Dispose();
            }
        }
    }

    /// <summary>
    /// Stops the coordinator: aborts a running capture (which runs its failure path: map merged back, floor released, hook removed, point abandoned) and
    /// joins the thread. If the thread does not finish in <paramref name="timeout"/> (a store stuck in I/O), the hook is removed from here and the
    /// lineage stops being trusted: the abandoned page set may not be merged back before the close persists the map.
    /// </summary>
    internal void StopAndJoin(TimeSpan timeout)
    {
        BackupCapture running;
        Thread thread;
        lock (_lock)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            running = _current;
            thread = _thread;
        }

        running?.Abort(BackupAbortReason.EngineStopping);
        _stopping.Cancel();
        _wake.Set();
        if (thread != null && !thread.Join(timeout))
        {
            LogStopTimedOut(timeout.TotalSeconds);
            Lineage.Trusted = false;
            running?.Uninstall();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopAndJoin(TimeSpan.FromSeconds(10));
            Parent?.RemoveChild(this);
        }

        base.Dispose(disposing);
    }

    [LoggerMessage(LogLevel.Error, "Backup thread did not stop within {Seconds} s; its copy-before-write hook was removed from the closing thread")]
    private partial void LogStopTimedOut(double seconds);
}
