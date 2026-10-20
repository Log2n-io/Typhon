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

/// <summary>
/// One backup job (#1417 §3.5): queued → running(phase, completed, total) → done(result) | error(category, message) | cancelled. Every transition is made by
/// the job's own body thread, so <see cref="Changed"/> fires in order; a cancel only trips the token and the body reports how it ended.
/// </summary>
/// <remarks>
/// Progress is throttled at the source: a restore reports per page, so the counters are updated in place and <see cref="Changed"/> fires at most every
/// <see cref="ProgressInterval"/> (or on a phase change). <see cref="Snapshot"/> builds the DTO on demand.
/// </remarks>
public sealed class BackupJob : IDisposable
{
    /// <summary>The least time between two progress notifications.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _lock = new();
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
    private Func<BackupJobDto, BackupJobDto> _result;
    private long _lastNotifyTicks;

    internal BackupJob(Guid id, string kind, string store, string bundle, string target, string route, Func<DateTime> clock)
    {
        Id = id;
        Kind = kind;
        Store = store;
        StoreKey = BackupPaths.Key(store);
        Bundle = bundle;
        Target = target;
        Route = route;
        _clock = clock;
        StartedUtc = clock();
    }

    public Guid Id { get; }

    /// <summary>create | verify | drill | compact | retention | restore.</summary>
    public string Kind { get; }

    public string Store { get; }

    internal string StoreKey { get; }

    public string Bundle { get; }

    /// <summary>A restore's resolved target bundle (WB-12: no session may open it while the job runs); null otherwise.</summary>
    public string Target { get; }

    /// <summary>A create's route (WB-4).</summary>
    public string Route { get; }

    public DateTime StartedUtc { get; }

    /// <summary>Cancelled by <see cref="Cancel"/> (DELETE) or the shutdown's <c>CancelAll</c>.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>The body's task; completed when the job is terminal.</summary>
    internal Task Completion { get; set; } = Task.CompletedTask;

    /// <summary>Raised on the body's thread after a transition, outside the lock: progress (throttled) and the terminal one.</summary>
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

    public BackupJobDto Snapshot()
    {
        lock (_lock)
        {
            var dto = new BackupJobDto(Id, Kind, Store, Bundle, _state, _phase, _completed, _total, _unit, StartedUtc, _endedUtc, Route, _note, _error,
                null, null, null, null, null, null);
            return _result == null ? dto : _result(dto);
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
        return true;
    });

    /// <summary>Progress: <paramref name="total"/> 0 means indeterminate. Throttled; a phase change always notifies.</summary>
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

        if (notify)
        {
            Changed?.Invoke(Snapshot());
        }
    }

    /// <summary>Done; <paramref name="result"/> fills the kind's result slot of every snapshot from now on.</summary>
    internal void SetDone(Func<BackupJobDto, BackupJobDto> result, string note = null) => Transition(() =>
    {
        _state = BackupJobStates.Done;
        _result = result;
        _note = note;
        if (_total > 0)
        {
            _completed = _total;
        }

        _endedUtc = _clock();
        return true;
    });

    internal void SetError(string category, string message) => Transition(() =>
    {
        _state = BackupJobStates.Error;
        _error = new BackupJobErrorDto(category, message);
        _endedUtc = _clock();
        return true;
    });

    internal void SetCancelled(string note) => Transition(() =>
    {
        _state = BackupJobStates.Cancelled;
        _note = note;
        _endedUtc = _clock();
        return true;
    });

    private void Transition(Func<bool> apply)
    {
        lock (_lock)
        {
            if (BackupJobStates.IsTerminal(_state) || !apply())
            {
                return;
            }

            _lastNotifyTicks = Environment.TickCount64;
        }

        Changed?.Invoke(Snapshot());
    }

    public void Dispose() => _cts.Dispose();
}
