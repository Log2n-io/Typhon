using Typhon.Workbench.Dtos.Backups;

namespace Typhon.Workbench.Backups;

/// <summary>What a job's body is handed: its token, its progress, and its result.</summary>
public sealed class BackupJobContext
{
    private readonly BackupJob _job;

    internal BackupJobContext(BackupJob job) => _job = job;

    public CancellationToken Token => _job.Token;

    /// <summary>Progress: <paramref name="total"/> 0 means indeterminate (no percentage is shown); <paramref name="unit"/> pages | bytes | packs | null.</summary>
    public void Report(string phase, long completed, long total, string unit) => _job.Report(phase, completed, total, unit);

    /// <summary>The kind's result slot, applied to every snapshot once the body returns.</summary>
    internal Func<BackupJobDto, BackupJobDto> Result { get; private set; }

    /// <summary>A note the result card shows (e.g. a cancel that came after the restore's publish).</summary>
    internal string Note { get; private set; }

    public void SetResult(Func<BackupJobDto, BackupJobDto> result, string note = null)
    {
        Result = result;
        Note = note;
    }
}

/// <summary>
/// The Backups view's server-side jobs (#1417 §3.5, WB-1): at most one non-terminal job per store, terminal jobs kept 1 h and at most 20 per store (so a
/// reopened tab re-attaches to a running job and still reads a finished one's result), cancel by token, cancel-all at shutdown. In memory: a Workbench
/// restart loses them, and the panel says so.
/// </summary>
/// <remarks>
/// A body runs on its own thread (<see cref="TaskCreationOptions.LongRunning"/>): a restore holds one for minutes and the thread pool serves Kestrel. What it
/// throws is classified the way <c>typhon backup</c> classifies it (<see cref="BackupOperations.Classify"/>).
/// </remarks>
public sealed partial class BackupJobRegistry : IDisposable
{
    public static readonly TimeSpan KeepTerminal = TimeSpan.FromHours(1);
    public const int KeepPerStore = 20;

    private readonly object _lock = new();
    private readonly List<BackupJob> _jobs = [];
    private readonly Func<DateTime> _clock;
    private readonly ILogger<BackupJobRegistry> _logger;
    private readonly Timer _evictTimer;

    public BackupJobRegistry(ILogger<BackupJobRegistry> logger) : this(logger, static () => DateTime.UtcNow, TimeSpan.FromMinutes(5))
    {
    }

    /// <summary>A registry on <paramref name="clock"/>; <paramref name="evictEvery"/> <see cref="Timeout.InfiniteTimeSpan"/> = eviction on start only.</summary>
    internal BackupJobRegistry(ILogger<BackupJobRegistry> logger, Func<DateTime> clock, TimeSpan evictEvery)
    {
        _logger = logger;
        _clock = clock;
        _evictTimer = new Timer(_ => Evict(), null, evictEvery, evictEvery);
    }

    /// <summary>
    /// Starts <paramref name="body"/> as a job on <paramref name="store"/>, unless the store already runs one: then returns false with that job in
    /// <paramref name="job"/> (the client attaches to it).
    /// </summary>
    public bool TryStart(string kind, string store, string bundle, string target, string route, Action<BackupJobContext> body, out BackupJob job)
    {
        ArgumentNullException.ThrowIfNull(body);
        var key = BackupPaths.Key(store);
        lock (_lock)
        {
            EvictLocked();
            foreach (var j in _jobs)
            {
                if (j.StoreKey == key && !j.IsTerminal)
                {
                    job = j;
                    return false;
                }
            }

            var started = new BackupJob(Guid.NewGuid(), kind, BackupPaths.Full(store), bundle, target, route, _clock);
            _jobs.Add(started);

            // Under the lock: CancelAll must never see a job without its body's task.
            started.Completion = Task.Factory.StartNew(() => Run(started, body), CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            job = started;
        }

        LogJobStarted(job.Id, kind, job.Store);
        return true;
    }

    public BackupJob Get(Guid id)
    {
        lock (_lock)
        {
            return _jobs.Find(j => j.Id == id);
        }
    }

    /// <summary>The store's jobs, newest first: its running one (if any) and the kept terminal ones.</summary>
    public IReadOnlyList<BackupJob> List(string store)
    {
        var key = BackupPaths.Key(store);
        lock (_lock)
        {
            EvictLocked();
            var list = _jobs.Where(j => j.StoreKey == key).ToList();
            list.Reverse();
            return list;
        }
    }

    /// <summary>The store's running job, or null.</summary>
    public BackupJob Running(string store)
    {
        var key = BackupPaths.Key(store);
        lock (_lock)
        {
            return _jobs.Find(j => j.StoreKey == key && !j.IsTerminal);
        }
    }

    /// <summary>A running job of <paramref name="kind"/> on any store, or null.</summary>
    public BackupJob AnyRunning(string kind)
    {
        lock (_lock)
        {
            return _jobs.Find(j => j.Kind == kind && !j.IsTerminal);
        }
    }

    /// <summary>A running restore whose target is <paramref name="bundle"/> (WB-12), or null.</summary>
    public BackupJob ActiveRestoreInto(string bundle)
    {
        lock (_lock)
        {
            return _jobs.Find(j => j.Kind == "restore" && j.Target != null && !j.IsTerminal && BackupPaths.Same(j.Target, bundle));
        }
    }

    /// <summary>Cancels a job (idempotent). False when no such job is known.</summary>
    public bool Cancel(Guid id)
    {
        var job = Get(id);
        if (job == null)
        {
            return false;
        }

        job.Cancel();
        return true;
    }

    /// <summary>Shutdown: cancels every running job and waits up to <paramref name="timeout"/> for their bodies. True when every body ended.</summary>
    public bool CancelAll(TimeSpan timeout)
    {
        List<BackupJob> running;
        lock (_lock)
        {
            running = _jobs.Where(j => !j.IsTerminal).ToList();
        }

        foreach (var j in running)
        {
            j.Cancel();
        }

        // A body still running at the deadline is left to the process exit: a staged restore leaves only *.restoring, a capture publishes nothing.
        return running.Count == 0 || Task.WaitAll(running.Select(j => j.Completion).ToArray(), timeout);
    }

    /// <summary>Drops terminal jobs older than <see cref="KeepTerminal"/>, then all but the newest <see cref="KeepPerStore"/> per store.</summary>
    public void Evict()
    {
        lock (_lock)
        {
            EvictLocked();
        }
    }

    private void EvictLocked()
    {
        var now = _clock();
        var perStore = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = _jobs.Count - 1; i >= 0; i--)
        {
            var job = _jobs[i];
            if (job.EndedUtc is not { } ended)
            {
                continue;
            }

            perStore.TryGetValue(job.StoreKey, out var kept);
            if (now - ended >= KeepTerminal || kept >= KeepPerStore)
            {
                _jobs.RemoveAt(i);
                job.Dispose();
                continue;
            }

            perStore[job.StoreKey] = kept + 1;
        }
    }

    private void Run(BackupJob job, Action<BackupJobContext> body)
    {
        var context = new BackupJobContext(job);
        try
        {
            job.SetRunning("");
            body(context);
            job.SetDone(context.Result, context.Note);
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            job.SetCancelled(context.Note);
        }
        catch (BackupJobException e)
        {
            job.SetError(e.Category, e.Message);
        }
        catch (Exception e)
        {
            var category = BackupDtoMapper.Category(e);
            if (BackupOperations.Classify(e) == BackupFailureKind.Defect)
            {
                LogJobDefect(job.Id, job.Kind, e);
                job.SetError(category, $"{e.GetType().Name}: {e.Message}");
            }
            else
            {
                job.SetError(category, e.Message);
            }
        }

        LogJobEnded(job.Id, job.Snapshot().State);
    }

    public void Dispose()
    {
        _evictTimer.Dispose();
        lock (_lock)
        {
            foreach (var j in _jobs.Where(j => j.IsTerminal))
            {
                j.Dispose();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup job {JobId} ({Kind}) started on {Store}")]
    private partial void LogJobStarted(Guid jobId, string kind, string store);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup job {JobId} ended: {State}")]
    private partial void LogJobEnded(Guid jobId, string state);

    [LoggerMessage(Level = LogLevel.Error, Message = "Backup job {JobId} ({Kind}) failed on a defect")]
    private partial void LogJobDefect(Guid jobId, string kind, Exception exception);
}
