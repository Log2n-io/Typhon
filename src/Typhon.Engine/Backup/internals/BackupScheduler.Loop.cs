using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Internals;

/// <summary>
/// The scheduler's tick (plan 1416 §3.4, DL-17/18/30): one low-priority thread, <c>Typhon-BackupScheduler</c>, doing bounded steps once a second — the
/// request poll, the archiver's step, the due decision, the health, the status. Every long piece of work is a job on the coordinator's backup thread, and
/// the store's refresh (every minute, after a job) runs on the thread pool: the tick only starts them, and never touches the store (BKP-16).
/// </summary>
internal sealed partial class BackupScheduler
{
    /// <summary>The tick: also the scheduling resolution.</summary>
    public static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(1);

    /// <summary>How often the store's facts are re-read when no job ran.</summary>
    public static readonly TimeSpan RefreshPeriod = TimeSpan.FromSeconds(60);

    private readonly ManualResetEventSlim _wake = new(false);
    private readonly CancellationTokenSource _stopping = new();
    private ScheduleState _schedule;
    private BackupRequestFile _requests;
    private IWalArchiver _archiver;
    private Thread _thread;
    private bool _started;
    private bool _scheduleReady;
    private bool _refreshDue;
    private bool _immediatePending;
    private long _startMono;
    private long _nextRefreshMono;
    private Task<BackupPointInfo> _lastJob;
    private Task _refresh;

    /// <summary>
    /// How long after the start the schedule waits before its first decision (the requests and the archiver do not wait): an engine that just opened is
    /// left to settle. 30 s; a test seam.
    /// </summary>
    internal TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Test seam: builds the archiver instead of <see cref="DatabaseEngine.CreateWalArchiver(ArchiveOptions, string)"/>.</summary>
    internal Func<IWalArchiver> ArchiverFactory { get; set; }

    /// <summary>Test hook, null in production: called by the tick between its busy check and its submit.</summary>
    internal Action BeforeSubmitProbe { get; set; }

    /// <summary>The last job the tick started, for tests to wait on.</summary>
    internal Task<BackupPointInfo> LastJob => Volatile.Read(ref _lastJob);

    /// <summary>
    /// Starts the schedule once the database's open completed (DL-30; idempotent, an engine's repeated <c>InitializeArchetypes</c> starts it once). Nothing
    /// is read on the open's thread: the tick's first run starts the store's first refresh, whose facts anchor the schedule.
    /// </summary>
    internal void Start()
    {
        lock (_lock)
        {
            if (_started || Stopped)
            {
                return;
            }

            _started = true;
            _state.StartedUtc = UtcNow();
            _schedule = new ScheduleState(_options.Interval, Stopwatch.Frequency);
            _startMono = MonoNow();
            _state.Version++;
        }

        _timeline = _engine.TimelineId;
        _requests = _options.AcceptRequests ? new BackupRequestFile(_engine.MMF.BundleDirectory) : null;
        _archiver = ArchiverFactory != null ? ArchiverFactory()
            : _options.ArchiverByTest ? null
            : _engine.CreateWalArchiver(_options.Archive, _options.StorePath);
        OnStarted();
        if (!_options.ManualTickForTest)
        {
            _thread = new Thread(Loop) { Name = "Typhon-BackupScheduler", IsBackground = true, Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    partial void OnStarted();

    /// <summary>
    /// One tick on the caller's thread, with the scheduler's thread not started (logical-clock tests). <paramref name="awaitRefresh"/>: a store refresh the
    /// tick starts is waited for inside it, before the health and the status (the order a test reads them in); it still runs on its own thread.
    /// </summary>
    internal void TickForTest(bool awaitRefresh = true) => Tick(awaitRefresh);

    private void Loop()
    {
        while (!Stopped)
        {
            Tick(false);
            _wake.Wait(TickPeriod);
            _wake.Reset();
        }
    }

    /// <summary>
    /// One tick. Never throws (BKP-16): a failure of any step is logged and the next tick runs. No store I/O: the store's facts are read by a refresh off the
    /// tick, so a store call parked on a dead share never stalls the request poll, the archiver's step or the status. Allocates nothing when nothing
    /// changed: the snapshot is republished, and the status file rewritten, only on a change, and every tick while a job runs.
    /// </summary>
    private void Tick(bool awaitRefresh)
    {
        if (Stopped)
        {
            return;
        }

        try
        {
            var mono = MonoNow();
            var utc = UtcNow();

            BackupRequest request = default;
            var requested = _requests != null && _requests.TryPoll(out request);
            var archiver = _archiver;
            if (archiver != null)
            {
                archiver.Step(utc, mono, _stopping.Token);
                // Latched until a job takes it: one asked while a job runs is not dropped.
                _immediatePending |= archiver.ConsumeImmediatePointRequest();
                var archive = archiver.State;
                lock (_lock)
                {
                    if (!archive.Equals(_state.Archive))
                    {
                        _state.Archive = archive;
                        _state.Version++;
                    }
                }
            }

            // The first tick (its facts anchor the schedule), after a job, every minute; one refresh at a time (one parked on a dead share is not doubled).
            if ((Volatile.Read(ref _refreshDue) || mono >= _nextRefreshMono) && _refresh is not { IsCompleted: false })
            {
                Volatile.Write(ref _refreshDue, false);
                _nextRefreshMono = mono + Ticks(RefreshPeriod);
                _refresh = Task.Run(RefreshStoreFacts);
                if (awaitRefresh)
                {
                    _refresh.Wait();
                }
            }

            bool scheduled;
            lock (_lock)
            {
                scheduled = _scheduleReady && mono - _startMono >= Ticks(Grace) && _schedule.IsDue(mono);
            }

            var immediate = _immediatePending;
            if ((requested || immediate || scheduled) && !Stopped && !_engine.BackupCoordinator.Busy)
            {
                BeforeSubmitProbe?.Invoke();
                var source = requested ? BackupJobSource.Request : immediate ? BackupJobSource.Archive : BackupJobSource.Schedule;
                var kind = requested && request.Kind == BackupRequestKind.Full ? BackupKind.Full : BackupKind.Incremental;
                var job = _engine.BackupCoordinator.CaptureAsync(CreateStore(), kind, _stopping.Token, source, request.RequestId);
                Volatile.Write(ref _lastJob, job);

                // A refused submit fails before it returns, its outcome already recorded (JobRefused). A deferred one (another job took the slot) leaves the
                // request and the archiver's ask for a later tick; any other answers them. An accepted one retires the request when its job starts.
                if (!job.IsFaulted || Classify(job.Exception.InnerException) != FailureClass.Deferred)
                {
                    _immediatePending = false;
                    if (requested && job.IsFaulted)
                    {
                        _requests.Delete();
                    }
                }
            }

            bool publish;
            bool running;
            lock (_lock)
            {
                var next = DueUtc(utc, mono);
                if (next != _state.NextDueUtc && Math.Abs((next - _state.NextDueUtc).TotalSeconds) >= 1)
                {
                    _state.NextDueUtc = next;
                    _state.Version++;
                }

                var inputs = Inputs();
                running = _state.State == BackupRunState.Running;
                publish = _state.Version != _publishedVersion || running || BackupHealthEvaluator.Evaluate(inputs, utc) != _publishedHealth;
            }

            if (publish)
            {
                Publish();
            }

            WriteStatusFileIfDue(running);
        }
        catch (Exception e)
        {
            LogUnexpected("tick", e);
        }
    }

    /// <summary>
    /// The wall-clock estimate of the next point (under <c>_lock</c>): none before the schedule is anchored or with no schedule; never before the grace's end.
    /// Overdue, it is the instant the point became due, not now: it does not move every tick.
    /// </summary>
    private DateTime DueUtc(DateTime utc, long mono) => _scheduleReady && _options.Interval > TimeSpan.Zero
        ? _schedule.NextDueUtc(utc, mono, _startMono + Ticks(Grace))
        : DateTime.MinValue;

    /// <summary>
    /// The first refresh's facts anchor the schedule on the newest point of this timeline (DL-19), whatever the refresh's outcome (an unreadable store:
    /// due after the grace). Under <c>_lock</c>.
    /// </summary>
    private void AnchorScheduleOnce(DateTime utc, long mono)
    {
        if (_scheduleReady || _schedule == null)
        {
            return;
        }

        _schedule.Initialize(utc, mono, _state.Facts.LastPoint?.TailEndUtc ?? DateTime.MinValue);
        _scheduleReady = true;
        _state.NextDueUtc = DueUtc(utc, mono);
    }

    partial void OnJobOutcome(FailureClass failureClass, long monoNow)
    {
        lock (_lock)
        {
            if (_schedule == null)
            {
                return;
            }

            if (failureClass == FailureClass.None)
            {
                _schedule.Succeeded(monoNow);
            }
            else
            {
                _schedule.Failed(failureClass, monoNow);
            }

            _state.NextDueUtc = DueUtc(UtcNow(), monoNow);
            _state.Version++;
        }
    }

    partial void OnJobEnded()
    {
        Volatile.Write(ref _refreshDue, true);
        _wake.Set();
    }

    /// <summary>
    /// Stops the tick (BKP-16): cancelled, woken, joined within one tick. A thread that does not stop in time (a store call parked on a dead share) is
    /// left detached: it checks <see cref="Stopped"/> before every engine access, and touches nothing of the engine after this returns.
    /// </summary>
    partial void StopLoop()
    {
        _stopping.Cancel();
        _wake.Set();
        var thread = _thread;
        if (thread != null && thread != Thread.CurrentThread && !thread.Join(TickPeriod))
        {
            Volatile.Write(ref _detached, true);
            LogStopTimedOut();
        }
    }

    private bool _detached;

    /// <summary>Whether the stop gave up joining the tick.</summary>
    internal bool Detached => Volatile.Read(ref _detached);

    partial void DisposeLoop()
    {
        if (Detached)
        {
            // The tick still runs and waits on these: left to it.
            return;
        }

        _wake.Dispose();
        _stopping.Dispose();
    }

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    [LoggerMessage(LogLevel.Warning, "Backup scheduler did not stop within one tick; its thread is left behind and touches nothing of the engine")]
    private partial void LogStopTimedOut();
}
