using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Backups;

/// <summary>How "Back up now" reaches a bundle (#1417 WB-4), with the session that holds it in-process.</summary>
internal sealed record BackupRoute(string Kind, string Reason, OpenSession Session)
{
    public const string InProcess = "inProcess";
    public const string Request = "request";
    public const string Offline = "offline";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// The decisions the Backups view takes against the Workbench's own sessions (#1417 §3.3, §3.4): which route "Back up now" takes, and whether a restore
/// target is held by a session in any state (WB-12). Path identity is <see cref="BackupPaths"/>'.
/// </summary>
internal static class BackupRouting
{
    /// <summary>The Workbench session (open or paused) whose bundle is <paramref name="bundle"/>, or null.</summary>
    public static OpenSession SessionOn(SessionManager sessions, string bundle)
    {
        if (string.IsNullOrWhiteSpace(bundle))
        {
            return null;
        }

        foreach (var s in sessions.Snapshot())
        {
            if (s is OpenSession os && BackupPaths.Same(os.FilePath, bundle))
            {
                return os;
            }
        }

        return null;
    }

    /// <summary>
    /// The route for <paramref name="bundle"/> into <paramref name="store"/>: <c>inProcess</c> when a live session of this Workbench holds it,
    /// <c>request</c> when another live engine does and accepts requests into this store, <c>offline</c> when nobody does; otherwise <c>unavailable</c>,
    /// with the reason.
    /// </summary>
    public static BackupRoute Decide(string bundle, string store, SessionManager sessions, IBackupOperations ops)
    {
        if (string.IsNullOrWhiteSpace(bundle))
        {
            return new BackupRoute(BackupRoute.Unavailable,
                "No database bundle: enter the path of the .typhon bundle to back up (an attach session names an endpoint, not a file).", null);
        }

        var session = SessionOn(sessions, bundle);
        if (session?.Engine is { } lifecycle)
        {
            if (lifecycle.OpenedOnCrashPathWithoutSchema)
            {
                return new BackupRoute(BackupRoute.Unavailable,
                    "This session opened the database on its crash path without its application schema: it shows the image before its WAL replay, and "
                    + "its engine cannot take a backup point. Close the session and back it up offline (the WAL tail is captured), or open it with its "
                    + "schema.", session);
            }

            if (lifecycle.InitializeArchetypesFailure is { } failure)
            {
                return new BackupRoute(BackupRoute.Unavailable,
                    $"This session's open did not complete (InitializeArchetypes failed: {failure.Message}); its engine cannot take a backup point.", session);
            }

            return new BackupRoute(BackupRoute.InProcess, "This Workbench's session holds the database: the point is taken online by its engine.", session);
        }

        if (session != null || DatabaseLockFile.IsHeldByLiveProcess(bundle))
        {
            BackupStatusReport report;
            try
            {
                report = ops.ReadStatus(bundle);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new BackupRoute(BackupRoute.Unavailable, $"Another process holds the database, and its backup status cannot be read: {e.Message}",
                    session);
            }

            if (!report.EngineLive)
            {
                return new BackupRoute(BackupRoute.Unavailable,
                    "Another process holds the database, and no engine with backup configured runs on it: back it up from that application, or close it "
                    + "and back it up here (offline).", session);
            }

            if (!report.AcceptsRequests)
            {
                return new BackupRoute(BackupRoute.Unavailable,
                    $"The engine holding the database (PID {report.Pid}) refuses backup requests (BackupOptions.AcceptRequests is off).", session);
            }

            if (!BackupPaths.Same(report.StorePath, store))
            {
                return new BackupRoute(BackupRoute.Unavailable,
                    $"The engine holding the database backs up into '{report.StorePath}', not this store: a request would land there. Pick that store to "
                    + "ask it for a point.", session);
            }

            return new BackupRoute(BackupRoute.Request,
                $"The engine holding the database (PID {report.Pid}) is asked for a point (backup.request); it writes to its own store.", session);
        }

        if (!Directory.Exists(bundle))
        {
            return new BackupRoute(BackupRoute.Unavailable, $"No database bundle at '{bundle}'.", null);
        }

        return new BackupRoute(BackupRoute.Offline, "Nobody holds the database: the point is taken from the closed bundle.", null);
    }
}
