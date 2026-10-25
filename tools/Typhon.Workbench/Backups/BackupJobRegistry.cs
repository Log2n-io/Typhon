using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Backups;

/// <summary>What a job's body is handed: its token, its progress, and its result.</summary>
public sealed class BackupJobContext
{
    private readonly BackupJob _job;

    internal BackupJobContext(BackupJob job) => _job = job;

    public CancellationToken Token => _job.Token;

    /// <summary>
    /// Progress: <paramref name="total"/> 0 means indeterminate (no percentage is shown); <paramref name="unit"/> pages | bytes | packs | null.
    /// </summary>
    public void Report(string phase, long completed, long total, string unit) => _job.Report(phase, completed, total, unit);

    /// <summary>The result, already mapped: applied to every snapshot once the body returns.</summary>
    internal BackupJobResult Result { get; private set; }

    /// <summary>A note the result card shows (e.g. a cancel that came after the restore's publish).</summary>
    internal string Note { get; private set; }

    public void SetResult(BackupJobResult result, string note = null)
    {
        Result = result;
        Note = note;
    }
}

/// <summary>A job to start: its kind, its store, and for a restore the bundle it writes.</summary>
public sealed record BackupJobSpec(string Kind, string Store, string Bundle = null, string Target = null, string Route = null);

/// <summary>Whether a start was admitted, and if not, what stood in its way.</summary>
public enum BackupJobAdmission
{
    Started,

    /// <summary>The store runs a job (any kind): one at a time per store.</summary>
    StoreBusy,

    /// <summary>A drill runs on any store: one at a time in the whole Workbench (each restores a whole image).</summary>
    DrillBusy,

    /// <summary>Another restore writes the same target.</summary>
    TargetBusy,

    /// <summary>A session of this Workbench is being opened on the target.</summary>
    TargetOpening,

    /// <summary>A session of this Workbench holds the target, open or paused (WB-12).</summary>
    TargetHeld,
}

/// <summary>The outcome of <see cref="BackupJobRegistry.TryStart"/>: the job started, the one in the way (busy), or the session holding the target.</summary>
public readonly record struct BackupJobStart(BackupJobAdmission Admission, BackupJob Job, Guid? HolderSessionId)
{
    public bool Started => Admission == BackupJobAdmission.Started;
}

/// <summary>
/// The Backups view's server-side jobs (#1417 §3.5, WB-1), and the admission policy they run under: at most one non-terminal job per store, at most one
/// drill in the whole Workbench, and no restore into a bundle a session of this Workbench holds or is opening (WB-12). Terminal jobs are kept 1 h and at
/// most 20 per store (so a reopened tab re-attaches to a running job and still reads a finished one's result); cancel by token, cancel-all at shutdown. In
/// memory: a Workbench restart loses them, and the panel says so.
/// </summary>
/// <remarks>
/// <para><b>One lock for every claim on a bundle.</b> A restore's target and a session open (<see cref="ClaimOpen"/>) are checked and claimed under the
/// same lock, and an open's claim is released only once its session is in the <see cref="SessionManager"/>: at every instant either the claim or the
/// session is visible to a restore's admission, so a restore and an open of the same bundle can never both pass.</para>
/// <para>A body runs on its own thread (<see cref="TaskCreationOptions.LongRunning"/>): a restore holds one for minutes and the thread pool serves Kestrel.
/// What it throws is classified the way <c>typhon backup</c> classifies it (<see cref="BackupOperations.Classify"/>).</para>
/// </remarks>
public sealed partial class BackupJobRegistry : IDisposable
{
    public static readonly TimeSpan KeepTerminal = TimeSpan.FromHours(1);
    public const int KeepPerStore = 20;

    private readonly object _lock = new();
    private readonly List<BackupJob> _jobs = [];
    private readonly Dictionary<string, int> _opening = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _clock;
    private readonly Func<string, Guid?> _holderOf;
    private readonly ILogger<BackupJobRegistry> _logger;
    private readonly Timer _evictTimer;

    public BackupJobRegistry(ILogger<BackupJobRegistry> logger, SessionManager sessions)
        : this(logger, static () => DateTime.UtcNow, TimeSpan.FromMinutes(5), bundle => BackupRouting.SessionOn(sessions, bundle)?.Id)
    {
    }

    /// <summary>
    /// A registry on <paramref name="clock"/>; <paramref name="evictEvery"/> <see cref="Timeout.InfiniteTimeSpan"/> = eviction on start only;
    /// <paramref name="holderOf"/> names the session holding a bundle (null: none ever does).
    /// </summary>
    internal BackupJobRegistry(ILogger<BackupJobRegistry> logger, Func<DateTime> clock, TimeSpan evictEvery, Func<string, Guid?> holderOf = null)
    {
        _logger = logger;
        _clock = clock;
        _holderOf = holderOf ?? (static _ => null);
        _evictTimer = new Timer(_ => Evict(), null, evictEvery, evictEvery);
    }

    /// <summary>
    /// Starts <paramref name="body"/> as the job <paramref name="spec"/> describes, unless the admission policy refuses it: then nothing starts, and the
    /// result names the job in the way (the client attaches to a busy store's job) or the session holding the target.
    /// </summary>
    public BackupJobStart TryStart(BackupJobSpec spec, Action<BackupJobContext> body)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(body);
        var key = BackupPaths.Key(spec.Store);
        var targetKey = spec.Target == null ? null : BackupPaths.Key(spec.Target);
        BackupJob job;
        lock (_lock)
        {
            EvictLocked();
            foreach (var j in _jobs)
            {
                if (j.IsTerminal)
                {
                    continue;
                }

                if (j.StoreKey == key)
                {
                    return new BackupJobStart(BackupJobAdmission.StoreBusy, j, null);
                }

                if (spec.Kind == BackupJobKinds.Drill && j.Kind == BackupJobKinds.Drill)
                {
                    return new BackupJobStart(BackupJobAdmission.DrillBusy, j, null);
                }

                if (targetKey != null && j.TargetKey == targetKey)
                {
                    return new BackupJobStart(BackupJobAdmission.TargetBusy, j, null);
                }
            }

            if (targetKey != null)
            {
                if (_opening.ContainsKey(targetKey))
                {
                    return new BackupJobStart(BackupJobAdmission.TargetOpening, null, null);
                }

                if (_holderOf(spec.Target) is { } holder)
                {
                    return new BackupJobStart(BackupJobAdmission.TargetHeld, null, holder);
                }
            }

            var target = spec.Target == null ? null : BackupPaths.Full(spec.Target);
            job = new BackupJob(Guid.NewGuid(), spec.Kind, BackupPaths.Full(spec.Store), spec.Bundle, target, spec.Route, _clock);
            _jobs.Add(job);

            // Under the lock: CancelAll must never see a job without its body's task.
            var started = job;
            job.Completion = Task.Factory.StartNew(() => Run(started, body), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        LogJobStarted(job.Id, job.Kind, job.Store);
        return new BackupJobStart(BackupJobAdmission.Started, job, null);
    }

    /// <summary>
    /// Claims <paramref name="bundle"/> for a session open (WB-12): null, with the restore in <paramref name="restoring"/>, when a running restore writes
    /// it. Dispose the claim once the session is in the <see cref="SessionManager"/> (or the open failed); until then no restore into it is admitted.
    /// </summary>
    public IDisposable ClaimOpen(string bundle, out BackupJob restoring)
    {
        var key = BackupPaths.Key(bundle);
        lock (_lock)
        {
            restoring = _jobs.Find(j => j.TargetKey == key && !j.IsTerminal);
            if (restoring != null)
            {
                return null;
            }

            _opening[key] = _opening.GetValueOrDefault(key) + 1;
        }

        return new OpenClaim(this, key);
    }

    private void ReleaseOpen(string key)
    {
        lock (_lock)
        {
            if (_opening.TryGetValue(key, out var n) && n > 1)
            {
                _opening[key] = n - 1;
            }
            else
            {
                _opening.Remove(key);
            }
        }
    }

    private sealed class OpenClaim(BackupJobRegistry registry, string key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                registry.ReleaseOpen(key);
            }
        }
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
            var list = new List<BackupJob>();
            for (var i = _jobs.Count - 1; i >= 0; i--)
            {
                if (_jobs[i].StoreKey == key)
                {
                    list.Add(_jobs[i]);
                }
            }

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
            running = _jobs.FindAll(j => !j.IsTerminal);
        }

        var completions = new Task[running.Count];
        for (var i = 0; i < running.Count; i++)
        {
            running[i].Cancel();
            completions[i] = running[i].Completion;
        }

        // A body still running at the deadline is left to the process exit: a staged restore leaves only *.restoring, a capture publishes nothing, a child
        // process sees its stdin close and exits.
        return running.Count == 0 || Task.WaitAll(completions, timeout);
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

        LogJobEnded(job.Id, job.Summary().State);
    }

    public void Dispose()
    {
        _evictTimer.Dispose();
        lock (_lock)
        {
            foreach (var j in _jobs)
            {
                if (j.IsTerminal)
                {
                    j.Dispose();
                }
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
