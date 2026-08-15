namespace Typhon.Workbench.Sessions;

/// <summary>
/// Identifies the process holding a database this session wants but does not have (#621).
///
/// <para>Populated from <see cref="Typhon.Engine.DatabaseLockedException"/>'s own <c>OwnerPid</c> / <c>OwnerMachine</c> / <c>StartedAt</c> rather than by
/// re-reading <c>db.lock</c>. Re-reading would race: between the failed open and the second read the holder may have exited and a third process taken the
/// lock, so the banner would name a process that never blocked us. The exception carries the identity that was actually observed at the moment of refusal —
/// that is the one worth showing a human.</para>
/// </summary>
/// <param name="Pid">Process id of the holder, as recorded in its lock file.</param>
/// <param name="MachineName">Machine the holder runs on. A different machine cannot be probed for liveness, so such a lock is always treated as live.</param>
/// <param name="StartedAt">When the holder acquired the database.</param>
/// <param name="ProfilerEndpoint">
/// <c>host:port</c> of the holder's live profiler, or <c>null</c> when it advertises none. This is the one field read
/// from <c>db.lock</c> rather than taken from the refusal, and deliberately so: it is not <i>identity</i>. The reason
/// identity must come from the exception — that a re-read could name a process which never blocked us — does not apply
/// to an address, where the cost of being stale is a refused connect that the caller already has to handle.
/// </param>
public sealed record DatabaseHolder(int Pid, string MachineName, DateTimeOffset StartedAt, string ProfilerEndpoint = null)
{
    /// <summary>True when the holder advertises a live profiler this session could watch.</summary>
    public bool IsWatchable => !string.IsNullOrWhiteSpace(ProfilerEndpoint);

    /// <summary>
    /// Reads the holder's advertised profiler endpoint from <c>db.lock</c>, or <c>null</c> if it advertises none or the
    /// lock cannot be read. Best-effort: an unreadable lock means "cannot watch", never an error.
    /// </summary>
    public static string ReadProfilerEndpoint(string bundleOrDatabasePath) =>
        Typhon.Engine.DatabaseLockFile.TryReadLock(bundleOrDatabasePath, out var info) ? info.ProfilerEndpoint : null;

    /// <summary>
    /// A one-line description for the paused banner — "PID 12345 on DESKTOP-ABC (since 2026-08-03 14:02:11Z)". Naming the holder is the difference between a
    /// user closing the right process and guessing.
    /// </summary>
    public string Describe() => $"PID {Pid} on {MachineName} (since {StartedAt:u})";
}
