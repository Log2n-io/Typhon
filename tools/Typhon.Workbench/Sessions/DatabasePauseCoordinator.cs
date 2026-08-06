using System.Collections.Concurrent;
using Typhon.Workbench.DataBrowser;
using Typhon.Workbench.Hosting;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// Owns the paused-session lifecycle (#621): releasing a database to another process, watching for it to come back, and re-opening it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a coordinator rather than methods on the session.</b> Pausing has two halves. <see cref="OpenSession.Pause"/> can drop the engine and its own
/// memoised schema provider, but the engine-derived caches that matter most live in DI services the session cannot reach — above all
/// <see cref="DataBrowserService"/>, whose snapshots are keyed on the <i>session</i> and therefore outlive the engine by design. Leaving that flush to each
/// caller is how a resumed session ends up serving pre-pause entity lists at a pre-pause revision after the application has written to the database. One
/// place performs both halves, so there is no ordering to remember and no path that does half of it.
/// </para>
/// <para>
/// <b>The poll is the guarantee; the watcher is an optimisation.</b> <see cref="FileSystemWatcher"/> misses events on network paths, in containers, and
/// across some macOS configurations, and it cannot be made reliable by trying harder. A fixed low-frequency poll always closes the gap, so the watcher only
/// ever makes resume *faster*, never *possible*. A missed event costs a second; a missed poll would cost a session stuck paused forever.
/// </para>
/// </remarks>
public sealed partial class DatabasePauseCoordinator : IDisposable
{
    /// <summary>
    /// How often to re-check a paused session's database. One second is well below human patience for "my app exited, why is the Workbench still paused?"
    /// while costing a file-existence check plus, at most, one process lookup per paused session — of which there is realistically one.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly SessionManager _sessions;
    private readonly DataBrowserService _dataBrowser;
    private readonly OptionsStore _options;
    private readonly ILogger<DatabasePauseCoordinator> _logger;

    private readonly ConcurrentDictionary<Guid, Watch> _watches = new();
    private readonly Timer _poll;
    private int _pollRunning;
    private bool _disposed;

    public DatabasePauseCoordinator(SessionManager sessions, DataBrowserService dataBrowser, OptionsStore options, ILogger<DatabasePauseCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(dataBrowser);
        ArgumentNullException.ThrowIfNull(options);
        _sessions = sessions;
        _dataBrowser = dataBrowser;
        _options = options;
        _logger = logger;

        // One timer for every paused session rather than one per session: the work per tick is a file check, and a shared timer means the count of paused
        // sessions never becomes a count of OS timers.
        _poll = new Timer(_ => PollOnce(), null, PollInterval, PollInterval);
    }

    /// <summary>
    /// What a paused session needs in order to be re-opened later.
    /// </summary>
    /// <param name="RequestedSchemaDllPaths">
    /// The schema list <i>as the user requested it</i> — usually empty. Deliberately not the session's <c>SchemaDllPaths</c>, which holds the paths ADR-055
    /// resolution actually produced: replaying those as an explicit list would pin the session to whatever resolved on the first open and relabel its
    /// provenance "user-specified", so a schema DLL rebuilt while the app ran would be ignored on resume. Re-resolving is also what makes resume pick up a
    /// schema directory the user registered in the meantime.
    /// </param>
    private sealed record Watch(OpenSession Session, string[] RequestedSchemaDllPaths, FileSystemWatcher Fsw) : IDisposable
    {
        public void Dispose()
        {
            try { Fsw?.Dispose(); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Releases the database held by <paramref name="session"/> and begins watching for its return.
    /// </summary>
    /// <param name="session">The session to pause. Already-paused sessions are a no-op.</param>
    /// <param name="holder">Who claimed the database, for the banner; null when pausing on request.</param>
    /// <param name="requestedSchemaDllPaths">The user's explicit schema list from the original open, so resume reproduces it.</param>
    public void Pause(OpenSession session, DatabaseHolder holder, string[] requestedSchemaDllPaths)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.IsPaused)
        {
            return;
        }

        // Flush BEFORE the engine goes away. Afterwards the entries are indistinguishable from valid ones — they are keyed on a session that still exists.
        _dataBrowser.InvalidateSession(session);
        session.Pause(holder);

        LogPaused(session.Id, session.FilePath, holder?.Describe() ?? "(on request)");
        StartWatching(session, requestedSchemaDllPaths);
    }

    /// <summary>
    /// Registers a session that was <i>born</i> paused because its database was already held (#621, AC2), so the watcher promotes it to a real open as soon
    /// as the holder exits.
    /// </summary>
    public void TrackPausedSession(OpenSession session, string[] requestedSchemaDllPaths)
    {
        ArgumentNullException.ThrowIfNull(session);
        StartWatching(session, requestedSchemaDllPaths);
    }

    /// <summary>
    /// Registers a <b>live</b> session so this coordinator can honour the promise its lock file makes.
    /// </summary>
    /// <remarks>
    /// The Workbench opens every database with <c>YieldableLock</c>, which tells any application starting afterwards
    /// "ask and I will let go". This is the half that keeps that promise: the same watcher now also looks for an incoming
    /// claim on a live session, not only for a departing holder on a paused one. Without it the advertisement would be a
    /// lie that costs the application a wait before failing exactly as it would have anyway.
    /// </remarks>
    /// <param name="session">The live session to watch.</param>
    /// <param name="requestedSchemaDllPaths">The user's explicit schema list from the open, so a later resume reproduces it.</param>
    public void TrackLiveSession(OpenSession session, string[] requestedSchemaDllPaths)
    {
        ArgumentNullException.ThrowIfNull(session);
        StartWatching(session, requestedSchemaDllPaths);
    }

    /// <summary>Stops watching a session — call on session removal so a disposed session is never resumed into.</summary>
    public void Forget(Guid sessionId)
    {
        if (_watches.TryRemove(sessionId, out var watch))
        {
            watch.Dispose();
        }
    }

    private void StartWatching(OpenSession session, string[] requestedSchemaDllPaths)
    {
        FileSystemWatcher fsw = null;
        try
        {
            // Watch both protocol files: "db.lock*" matches the lock itself and the claim request beside it. A live
            // session cares about the request appearing; a paused one cares about the lock disappearing. Purely an
            // accelerator — see the class remarks.
            fsw = new FileSystemWatcher(session.FilePath, DatabaseLockFile.FileName + "*")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            fsw.Created += (_, _) => PollOnce();
            fsw.Deleted += (_, _) => PollOnce();
            fsw.Changed += (_, _) => PollOnce();
        }
        catch (Exception ex)
        {
            // A missing directory, an exhausted watch handle budget, a filesystem that cannot watch — none of it matters, because the poll is the guarantee.
            LogWatcherUnavailable(session.FilePath, ex.Message);
            fsw?.Dispose();
            fsw = null;
        }

        var watch = new Watch(session, requestedSchemaDllPaths ?? [], fsw);
        if (_watches.TryRemove(session.Id, out var previous))
        {
            previous.Dispose();
        }
        _watches[session.Id] = watch;
    }

    /// <summary>
    /// One sweep over every watched session. Re-entrancy is gated rather than locked: the watcher can fire this concurrently with the timer, and a resume is
    /// slow enough (a full engine open) that overlapping sweeps would try to open the same database twice.
    /// </summary>
    private void PollOnce()
    {
        if (_disposed || Interlocked.Exchange(ref _pollRunning, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var kv in _watches.ToArray())
            {
                var watch = kv.Value;

                // Dropped or replaced out from under us — stop watching rather than resurrect a dead session.
                if (!_sessions.TryGet(kv.Key, out var current) || !ReferenceEquals(current, watch.Session))
                {
                    Forget(kv.Key);
                    continue;
                }

                var claimInFlight = DatabaseLockFile.HasLiveRequest(watch.Session.FilePath, DateTimeOffset.UtcNow);

                if (!watch.Session.IsPaused)
                {
                    // LIVE session. The one thing to watch for is somebody asking for the database — which they only do
                    // because our own lock file advertised that we would yield.
                    if (claimInFlight)
                    {
                        Yield(watch);
                    }
                    continue;
                }

                // PAUSED session, waiting for its turn back.
                if (claimInFlight)
                {
                    // A claim is still in flight, so the claimant has not acquired yet. Re-taking the database now would
                    // win the race against the very process we stepped aside for. The request file exists precisely so a
                    // holder knows not to re-acquire into that gap; the claimant deletes it once it holds the lock, and an
                    // abandoned one is retired by its TTL inside HasLiveRequest.
                    continue;
                }

                if (DatabaseLockFile.IsHeldByLiveProcess(watch.Session.FilePath))
                {
                    // Still held. If it changed hands since we paused, re-point the banner: telling the user to close a process that already exited is worse
                    // than saying nothing, and this is the "third process grabbed the window" case from the design's failure table.
                    RefreshHolder(watch.Session);
                    continue;
                }

                TryResume(watch);
            }
        }
        catch (Exception ex)
        {
            // A sweep must never take the timer down — the next tick gets a fresh attempt.
            LogPollFailed(ex.Message);
        }
        finally
        {
            Volatile.Write(ref _pollRunning, 0);
        }
    }

    /// <summary>
    /// Releases the database to a claimant that asked for it (#621 §2).
    /// </summary>
    /// <remarks>
    /// <para><b>Fail toward yielding.</b> The claimant's identity is read only to name it in the banner — the decision to
    /// release was already made by the request file's existence. A request that is present but unreadable still causes a
    /// release, because the alternative is ignoring a claim we advertised we would honour.</para>
    /// <para>The request file is deliberately left alone. The claimant deletes it after acquiring, which is what stops
    /// this session from re-taking the database in the window between our release and its acquisition.</para>
    /// </remarks>
    private void Yield(Watch watch)
    {
        DatabaseLockFile.TryReadRequest(watch.Session.FilePath, out var request);
        var claimant = request is { } claim
            ? new DatabaseHolder(claim.Pid, claim.MachineName ?? "unknown", claim.RequestedAt)
            : null;

        LogYielding(watch.Session.Id, watch.Session.FilePath, claimant?.Describe() ?? "an unidentified process");

        // Same flush-then-dispose as any pause: the Data Browser's snapshots are keyed on the session, which survives.
        _dataBrowser.InvalidateSession(watch.Session);
        watch.Session.Pause(claimant);
    }

    private void RefreshHolder(OpenSession session)
    {
        if (DatabaseLockFile.TryReadHolder(session.FilePath, out var pid, out var machine, out var startedAt)
            && session.PausedBy is { } current
            && current.Pid != pid)
        {
            session.Pause(new DatabaseHolder(pid, machine, startedAt)); // already paused — this only re-stamps the holder
        }
    }

    private void TryResume(Watch watch)
    {
        var session = watch.Session;
        try
        {
            // Registered schema directories are re-read now rather than captured at pause: the user may have pointed the Workbench at a rebuilt schema while
            // the application was running, which is exactly the dev loop this feature exists to serve.
            var registeredSchemaDirs = _options.Get().Schema?.Directories ?? [];
            var engine = EngineLifecycle.OpenAsync(session.FilePath, watch.RequestedSchemaDllPaths, registeredSchemaDirs).GetAwaiter().GetResult();

            // Re-check AFTER the open, not just before it. Opening a database takes hundreds of milliseconds — schema
            // load, archetype init, WAL recovery — and the session can be closed during that window (a user closing a
            // paused session is a perfectly ordinary thing to do). Handing the engine to a disposed session would leave
            // nobody holding a reference to it, so nothing would ever dispose it: the file handle and db.lock would be
            // held until the Workbench exits, and the application this feature exists to unblock could never reopen its
            // own database. Found by the browser walk, where a spec closed sessions between cases.
            if (!_sessions.TryGet(session.Id, out var current) || !ReferenceEquals(current, session) || !session.IsPaused)
            {
                engine.Dispose();
                Forget(session.Id);
                return;
            }

            // Flush again on the way back in. The paused window is exactly when a stale snapshot could have been re-created by a request that raced the pause.
            _dataBrowser.InvalidateSession(session);
            session.Resume(engine);

            Forget(session.Id);
            LogResumed(session.Id, session.FilePath);
        }
        catch (DatabaseLockedException)
        {
            // Lost the race — somebody took it between our check and our open. Stay paused; the next tick tries again.
        }
        catch (WorkbenchException ex) when (ex.ErrorCode == "file_locked")
        {
            // Same race, surfaced as an OS sharing violation instead of an advisory lock.
        }
        catch (Exception ex)
        {
            // A real open failure (corrupt database, missing schema). Keep watching rather than spin: the next tick retries, and the session stays paused with
            // its profiles usable, which is strictly better than tearing it down.
            LogResumeFailed(session.Id, ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _poll.Dispose();
        foreach (var kv in _watches.ToArray())
        {
            kv.Value.Dispose();
        }
        _watches.Clear();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} paused — released '{FilePath}' to {Holder}")]
    private partial void LogPaused(Guid sessionId, string filePath, string holder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} resumed — reopened '{FilePath}'")]
    private partial void LogResumed(Guid sessionId, string filePath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId} resume attempt failed, staying paused: {Error}")]
    private partial void LogResumeFailed(Guid sessionId, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Lock watcher unavailable for '{FilePath}' ({Error}) — falling back to polling only")]
    private partial void LogWatcherUnavailable(string filePath, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Paused-session poll failed: {Error}")]
    private partial void LogPollFailed(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} yielding '{FilePath}' — {Claimant} asked for the database")]
    private partial void LogYielding(Guid sessionId, string filePath, string claimant);
}
