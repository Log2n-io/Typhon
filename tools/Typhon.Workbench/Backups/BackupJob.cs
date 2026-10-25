using Typhon.Workbench.Dtos.Backups;

namespace Typhon.Workbench.Backups;

/// <summary>The states of a <see cref="BackupJob"/>, as the wire names them.</summary>
public static class BackupJobStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Error = "error";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string state) => state is Done or Error or Cancelled;
}

/// <summary>The kinds of a <see cref="BackupJob"/>, as the wire names them.</summary>
public static class BackupJobKinds
{
    public const string Create = "create";
    public const string Verify = "verify";
    public const string Drill = "drill";
    public const string Compact = "compact";
    public const string Retention = "retention";
    public const string Restore = "restore";
}

/// <summary>
/// A finished job's result: one slot per kind, mapped to its DTO once, when the body ends. The engine's own report is not kept: a store keeps up to 20
/// finished jobs for an hour, and a verify report's findings or a restore's page lists are paid for once, not on every snapshot.
/// </summary>
public sealed record BackupJobResult(
    BackupCreateResultDto Create = null,
    BackupVerifyReportDto Verify = null,
    BackupDrillReportDto Drill = null,
    BackupCompactionResultDto Compact = null,
    BackupRetentionResultDto Retention = null,
    BackupRestoreResultDto Restore = null);

/// <summary>
/// One backup job (#1417 §3.5): queued → running(phase, completed, total) → done(result) | error(category, message) | cancelled. A cancel only trips the
/// token; the body reports how it ended.
/// </summary>
/// <remarks>
/// <para>Progress may come from any thread — a drill's arrives on the child process's stdout reader — and the terminal transition from the body's thread.
/// Notifications are serialized, each with the snapshot taken under that serialization, and none follows the terminal one: a progress report that loses
/// the race to the terminal transition notifies nothing. So <see cref="Changed"/> sees states in order whichever thread reports them.</para>
/// <para>Progress is throttled at the source: a restore reports per page, so the counters are updated in place and <see cref="Changed"/> fires at most every
/// <see cref="ProgressInterval"/> (or on a phase change). <see cref="Snapshot"/> builds the DTO on demand.</para>
/// </remarks>
public sealed class BackupJob : IDisposable
{
    /// <summary>The least time between two progress notifications.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _lock = new();
    private readonly object _notify = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<DateTime> _clock;
    private string _state = BackupJobStates.Queued;
    private string _phase = "";
    private long _completed;
    private long _total;
    private string _unit;
    private DateTime? _endedUtc;
    private string _note;
    private BackupJobErrorDto _error;
    private BackupJobResult _result;
    private long _lastNotifyTicks;

    internal BackupJob(Guid id, string kind, string store, string bundle, string target, string route, Func<DateTime> clock)
    {
        Id = id;
        Kind = kind;
        Store = store;
        StoreKey = BackupPaths.Key(store);
        Bundle = bundle;
        Target = target;
        TargetKey = target == null ? null : BackupPaths.Key(target);
        Route = route;
        _clock = clock;
        StartedUtc = clock();
    }

    public Guid Id { get; }

    /// <summary>One of <see cref="BackupJobKinds"/>.</summary>
    public string Kind { get; }

    public string Store { get; }

    internal string StoreKey { get; }

    public string Bundle { get; }

    /// <summary>A restore's resolved target bundle (WB-12: no session may open it while the job runs); null otherwise.</summary>
    public string Target { get; }

    internal string TargetKey { get; }

    /// <summary>A create's route (WB-4).</summary>
    public string Route { get; }

    public DateTime StartedUtc { get; }

    /// <summary>Cancelled by <see cref="Cancel"/> (DELETE) or the shutdown's <c>CancelAll</c>.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>The body's task; completed when the job is terminal.</summary>
    internal Task Completion { get; set; } = Task.CompletedTask;

    /// <summary>Raised after a transition, outside the state lock, serialized (see the remarks): progress (throttled) and the terminal one.</summary>
    public event Action<BackupJobDto> Changed;

    public bool IsTerminal
    {
        get
        {
            lock (_lock)
            {
                return BackupJobStates.IsTerminal(_state);
            }
        }
    }

    internal DateTime? EndedUtc
    {
        get
        {
            lock (_lock)
            {
                return _endedUtc;
            }
        }
    }

    /// <summary>The job with its result (the snapshot GET and the stream).</summary>
    public BackupJobDto Snapshot() => Build(withResult: true);

    /// <summary>The job without its result slots (the store's job list: what re-attaching needs, not 20 results per refresh).</summary>
    public BackupJobDto Summary() => Build(withResult: false);

    private BackupJobDto Build(bool withResult)
    {
        lock (_lock)
        {
            var r = withResult ? _result : null;
            return new BackupJobDto(Id, Kind, Store, Bundle, _state, _phase, _completed, _total, _unit, StartedUtc, _endedUtc, Route, _note, _error,
                r?.Create, r?.Verify, r?.Drill, r?.Compact, r?.Retention, r?.Restore);
        }
    }

    /// <summary>Trips the job's token. Idempotent; false when the job had already ended.</summary>
    public bool Cancel()
    {
        lock (_lock)
        {
            if (BackupJobStates.IsTerminal(_state))
            {
                return false;
            }
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed by eviction after it ended: nothing left to cancel.
        }

        return true;
    }

    internal void SetRunning(string phase) => Transition(() =>
    {
        _state = BackupJobStates.Running;
        _phase = phase ?? "";
    });

    /// <summary>Progress: <paramref name="total"/> 0 means indeterminate. Throttled; a phase change always notifies. Any thread.</summary>
    internal void Report(string phase, long completed, long total, string unit)
    {
        bool notify;
        lock (_lock)
        {
            if (BackupJobStates.IsTerminal(_state))
            {
                return;
            }

            var phaseChanged = !string.Equals(_phase, phase, StringComparison.Ordinal);
            _state = BackupJobStates.Running;
            _phase = phase ?? "";
            _completed = completed;
            _total = total;
            _unit = unit;
            var now = Environment.TickCount64;
            notify = phaseChanged || now - _lastNotifyTicks >= (long)ProgressInterval.TotalMilliseconds || (total > 0 && completed >= total);
            if (notify)
            {
                _lastNotifyTicks = now;
            }
        }

        if (!notify)
        {
            return;
        }

        lock (_notify)
        {
            var dto = Snapshot();

            // Lost the race to the terminal transition, which has notified (or is about to, behind this lock) with the newer state.
            if (!BackupJobStates.IsTerminal(dto.State))
            {
                Changed?.Invoke(dto);
            }
        }
    }

    /// <summary>Done; <paramref name="result"/> fills the kind's result slot of every snapshot from now on.</summary>
    internal void SetDone(BackupJobResult result, string note = null) => Transition(() =>
    {
        _state = BackupJobStates.Done;
        _result = result;
        _note = note;
        if (_total > 0)
        {
            _completed = _total;
        }

        _endedUtc = _clock();
    });

    internal void SetError(string category, string message) => Transition(() =>
    {
        _state = BackupJobStates.Error;
        _error = new BackupJobErrorDto(category, message);
        _endedUtc = _clock();
    });

    internal void SetCancelled(string note) => Transition(() =>
    {
        _state = BackupJobStates.Cancelled;
        _note = note;
        _endedUtc = _clock();
    });

    private void Transition(Action apply)
    {
        lock (_notify)
        {
            lock (_lock)
            {
                if (BackupJobStates.IsTerminal(_state))
                {
                    return;
                }

                apply();
                _lastNotifyTicks = Environment.TickCount64;
            }

            Changed?.Invoke(Snapshot());
        }
    }

    public void Dispose() => _cts.Dispose();
}
