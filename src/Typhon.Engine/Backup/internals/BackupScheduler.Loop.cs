using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Internals;

/// <summary>
/// The scheduler's tick (plan 1416 §3.4, DL-17/18/30): one low-priority thread, <c>Typhon-BackupScheduler</c>, doing bounded steps once a second — the
/// request poll, the archiver's step, the due decision, the store's refresh (every minute or after a job), the health, the status. Every long piece of work
/// is a job on the coordinator's backup thread; the tick only starts it.
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
    private bool _initialized;
    private bool _refreshDue;
    private long _startMono;
    private long _nextRefreshMono;
    private Task<BackupPointInfo> _lastJob;

    /// <summary>
    /// How long after the start the schedule waits before its first decision (the requests and the archiver do not wait): an engine that just opened is
    /// left to settle. 30 s; a test seam.
    /// </summary>
    internal TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Test seam: builds the archiver instead of <see cref="DatabaseEngine.CreateWalArchiver(ArchiveOptions, string)"/>.</summary>
    internal Func<IWalArchiver> ArchiverFactory { get; set; }

    /// <summary>Test seam: start no thread; the test calls <see cref="TickForTest"/>.</summary>
    internal bool ManualTicks { get; set; }

    /// <summary>The last job the tick started, for tests to wait on.</summary>
    internal Task<BackupPointInfo> LastJob => Volatile.Read(ref _lastJob);

    /// <summary>
    /// Starts the schedule once the database's open completed (DL-30; idempotent, an engine's repeated <c>InitializeArchetypes</c> starts it once). Nothing
    /// is read on the open's thread: the tick's first run reads the store.
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
            _state.Version++;
        }

        _startMono = MonoNow();
        _timeline = _engine.TimelineId;
        _schedule = new ScheduleState(_options.Interval, Stopwatch.Frequency);
        _requests = _options.AcceptRequests ? new BackupRequestFile(_engine.MMF.BundleDirectory) : null;
        _archiver = ArchiverFactory != null ? ArchiverFactory() : _engine.CreateWalArchiver(_options.Archive, _options.StorePath);
        OnStarted();
        if (!ManualTicks && !_engine.BackupOptions.ManualTickForTest)
        {
            _thread = new Thread(Loop) { Name = "Typhon-BackupScheduler", IsBackground = true, Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    partial void OnStarted();

    /// <summary>One tick on the caller's thread, with the scheduler's thread not started (logical-clock tests).</summary>
    internal void TickForTest() => Tick();

    private void Loop()
    {
        while (!Stopped)
        {
            Tick();
            _wake.Wait(TickPeriod);
            _wake.Reset();
        }
    }

    /// <summary>
    /// One tick. Never throws (BKP-16): a failure of any step is logged and the next tick runs. Allocates nothing when nothing changed: the snapshot is
    /// republished, and the status file rewritten, only on a change, and every tick while a job runs.
    /// </summary>
    private void Tick()
    {
        if (Stopped)
        {
            return;
        }

        try
        {
            var mono = MonoNow();
            var utc = UtcNow();
            if (!_initialized)
            {
                // First tick: the store's facts, then the schedule anchored on the newest point of this timeline (DL-19).
                _initialized = true;
                RefreshStoreFacts();
                _nextRefreshMono = mono + Ticks(RefreshPeriod);
                lock (_lock)
                {
                    _schedule.Initialize(utc, mono, _state.Facts.LastPoint?.TailEndUtc ?? DateTime.MinValue);
                    _state.NextDueUtc = DueUtc(utc, mono);
                    _state.Version++;
                }
            }

            BackupRequest request = default;
            var requested = _requests != null && _requests.TryPoll(out request);
            var archiver = _archiver;
            var immediate = false;
            if (archiver != null)
            {
                archiver.Step(utc, mono, _stopping.Token);
                immediate = archiver.ConsumeImmediatePointRequest();
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

            bool scheduled;
            lock (_lock)
            {
                scheduled = mono - _startMono >= Ticks(Grace) && _schedule.IsDue(mono);
            }

            if ((requested || immediate || scheduled) && !Stopped && !_engine.BackupCoordinator.Busy)
            {
                var source = requested ? BackupJobSource.Request : immediate ? BackupJobSource.Archive : BackupJobSource.Schedule;
                var kind = requested && request.Kind == BackupRequestKind.Full ? BackupKind.Full : BackupKind.Incremental;
                if (requested)
                {
                    _requests.Delete();
                }

                Volatile.Write(ref _lastJob, _engine.BackupCoordinator.CaptureAsync(CreateStore(), kind, _stopping.Token, source, request.RequestId));
            }

            if (_refreshDue || mono >= _nextRefreshMono)
            {
                _refreshDue = false;
                _nextRefreshMono = mono + Ticks(RefreshPeriod);
                RefreshStoreFacts();
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
            LogUnexpected("tick", e.Message);
        }
    }

    private DateTime DueUtc(DateTime utc, long mono) => _options.Interval > TimeSpan.Zero ? _schedule.NextDueUtc(utc, mono) : DateTime.MinValue;

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
        _refreshDue = true;
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
