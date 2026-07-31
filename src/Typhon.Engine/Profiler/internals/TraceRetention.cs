using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Typhon.Engine.Internals;

/// <summary>
/// Result of a pruning pass — the honest budget picture, for logging now and for a UI later (#616, design D-6).
/// </summary>
/// <param name="CaptureBytes">Bytes occupied by captures after pruning, pinned ones included.</param>
/// <param name="PinnedBytes">Of which pinned — the portion that can never be reclaimed.</param>
/// <param name="SidecarBytes">Bytes occupied by derived <c>-cache</c> sidecars, accounted separately and outside the budget.</param>
/// <param name="BudgetBytes">The budget in force.</param>
/// <param name="Evicted">Captures deleted by this pass.</param>
/// <param name="Skipped">Captures that were over budget but could not be deleted — in use, or permission-denied.</param>
internal readonly record struct RetentionReport(long CaptureBytes, long PinnedBytes, long SidecarBytes, long BudgetBytes, int Evicted, int Skipped)
{
    /// <summary>
    /// True when the pinned captures alone exceed the budget — the genuine "your policy no longer means anything" state, worth saying out loud rather than
    /// silently tolerating.
    /// </summary>
    public bool PinnedExceedBudget => BudgetBytes > 0 && PinnedBytes > BudgetBytes;
}

/// <summary>
/// Enforces a database's <see cref="RetentionPolicy"/> over its <c>profilings/</c> directory. Runs at capture start, in whatever process is writing the
/// capture, so a headless host bounds its own disk use without any tooling installed.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Never evict a capture currently open in a session" is enforced structurally, not by coordination.</b> A capture being read is held open, so its delete
/// fails and the pruner moves on. That is the only mechanism available to a writer that knows nothing about Workbench sessions — and it is the right one:
/// there is no registry to consult, nothing to keep in sync, and it works identically for a capture opened by any other tool.
/// </para>
/// <para>
/// Eviction order is oldest-first among unpinned captures outside the keep-latest floor. Failures never propagate: a profiling session must not fail to start
/// because a stale capture could not be removed.
/// </para>
/// </remarks>
internal static partial class TraceRetention
{
    private readonly record struct CaptureFile(string Path, string Name, long Length, DateTime LastWriteUtc, bool Pinned);

    /// <summary>
    /// Prunes <paramref name="profilingsDirectory"/> to <paramref name="policy"/>. Safe to call on a directory that does not exist yet (returns an empty
    /// report). Never throws.
    /// </summary>
    /// <param name="profilingsDirectory">The database's <c>profilings/</c> directory.</param>
    /// <param name="policy">The policy in force.</param>
    /// <param name="logger">Optional logger; every eviction and every skip is reported through it.</param>
    internal static RetentionReport Prune(string profilingsDirectory, RetentionPolicy policy, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Normalised once rather than guarded at every call site: [LoggerMessage]-generated methods dereference the logger without a null check, and the
        // engine's own Logger is legitimately null in some hosts (DatabaseEngine uses `Logger?.` throughout). Disk cleanup must not fault on the absence of
        // somewhere to write about it.
        logger ??= NullLogger.Instance;

        if (string.IsNullOrEmpty(profilingsDirectory) || !Directory.Exists(profilingsDirectory))
        {
            return new RetentionReport(0, 0, 0, policy.BudgetBytes, 0, 0);
        }

        var captures = new List<CaptureFile>();
        var captureBytes = 0L;
        var pinnedBytes = 0L;
        try
        {
            foreach (var path in Directory.EnumerateFiles(profilingsDirectory, "*" + TraceLocation.TraceExtension))
            {
                if (!TraceLocation.IsCapture(path))
                {
                    continue;
                }
                var info = new FileInfo(path);
                var pinned = policy.IsPinned(info.Name);
                captures.Add(new CaptureFile(path, info.Name, info.Length, info.LastWriteTimeUtc, pinned));
                captureBytes += info.Length;
                if (pinned)
                {
                    pinnedBytes += info.Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RetentionLog.EnumerationFailed(logger, profilingsDirectory, ex.GetType().Name, ex.Message);
            return new RetentionReport(0, 0, 0, policy.BudgetBytes, 0, 0);
        }

        // Sidecars first: orphans (whose capture is gone) are pure waste and go now; the rest are totalled separately so a large derived cache can never
        // evict a real capture. They are regenerable — captures are not.
        var sidecarBytes = ReclaimSidecars(profilingsDirectory, captures, logger);

        var report = PruneCaptures(captures, captureBytes, pinnedBytes, sidecarBytes, policy, logger);

        if (report.PinnedExceedBudget)
        {
            RetentionLog.PinnedExceedBudget(logger, report.PinnedBytes, report.BudgetBytes, profilingsDirectory);
        }

        return report;
    }

    /// <summary>Deletes sidecars whose capture no longer exists and returns the bytes the surviving ones occupy.</summary>
    private static long ReclaimSidecars(string profilingsDirectory, List<CaptureFile> captures, ILogger logger)
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < captures.Count; i++)
        {
            live.Add(captures[i].Name);
        }

        var bytes = 0L;
        try
        {
            foreach (var path in Directory.EnumerateFiles(profilingsDirectory, TraceLocation.SidecarSearchPattern))
            {
                var info = new FileInfo(path);
                var owner = Path.GetFileName(TraceLocation.CaptureOfSidecar(path));
                if (live.Contains(owner))
                {
                    bytes += info.Length;
                    continue;
                }
                if (TryDelete(path, logger))
                {
                    RetentionLog.OrphanSidecarReclaimed(logger, info.Name, info.Length);
                }
                else
                {
                    bytes += info.Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RetentionLog.EnumerationFailed(logger, profilingsDirectory, ex.GetType().Name, ex.Message);
        }
        return bytes;
    }

    /// <summary>Evicts oldest-first until the captures fit the budget, respecting pins and the keep-latest floor.</summary>
    private static RetentionReport PruneCaptures(List<CaptureFile> captures, long captureBytes, long pinnedBytes, long sidecarBytes, RetentionPolicy policy, 
        ILogger logger)
    {
        var budget = policy.BudgetBytes;
        if (budget <= 0 || captureBytes <= budget)
        {
            return new RetentionReport(captureBytes, pinnedBytes, sidecarBytes, budget, 0, 0);
        }

        // Newest first, so the keep-latest floor is simply the head of the list. The floor counts captures, pinned or not — "keep the latest 10" means ten
        // files, and a user who pinned some of the newest did not thereby ask to retain ten *more* on top.
        captures.Sort(static (a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc));

        var evicted = 0;
        var skipped = 0;
        var keepLatest = policy.KeepLatest;

        // Walk oldest → newest, stopping before the floor.
        for (var i = captures.Count - 1; i >= keepLatest && captureBytes > budget; i--)
        {
            var capture = captures[i];
            if (capture.Pinned)
            {
                continue; // counted in the total, never reclaimed — the whole point of a pin
            }

            if (!TryDelete(capture.Path, logger))
            {
                // In use by a session, or permission-denied. Not an error: the capture stays, the budget stays over, and the next capture start tries again.
                skipped++;
                continue;
            }

            captureBytes -= capture.Length;
            evicted++;
            RetentionLog.CaptureEvicted(logger, capture.Name, capture.Length, budget);

            // The sidecar is derived from a capture that no longer exists; take it with the capture rather than leaving an orphan for the next pass.
            var sidecar = TraceLocation.SidecarOf(capture.Path);
            if (File.Exists(sidecar))
            {
                var sidecarLength = new FileInfo(sidecar).Length;
                if (TryDelete(sidecar, logger))
                {
                    sidecarBytes -= sidecarLength;
                }
            }
        }

        if (captureBytes > budget)
        {
            RetentionLog.StillOverBudget(logger, captureBytes, budget, keepLatest, skipped);
        }

        return new RetentionReport(captureBytes, pinnedBytes, sidecarBytes, budget, evicted, skipped);
    }

    private static bool TryDelete(string path, ILogger logger)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RetentionLog.DeleteSkipped(logger, Path.GetFileName(path), ex.GetType().Name, ex.Message);
            return false;
        }
    }
}

/// <summary>Source-generated log messages for retention. Separate holder so the messages stay typed and allocation-free when the level is off.</summary>
internal static partial class RetentionLog
{
    [LoggerMessage(EventId = 6160, Level = LogLevel.Information,
        Message = "Profiling retention: evicted capture '{Name}' ({Bytes} bytes) — captures exceeded the {BudgetBytes}-byte budget.")]
    public static partial void CaptureEvicted(ILogger logger, string name, long bytes, long budgetBytes);

    [LoggerMessage(EventId = 6161, Level = LogLevel.Debug,
        Message = "Profiling retention: reclaimed orphaned sidecar '{Name}' ({Bytes} bytes) — its capture no longer exists.")]
    public static partial void OrphanSidecarReclaimed(ILogger logger, string name, long bytes);

    [LoggerMessage(EventId = 6162, Level = LogLevel.Debug,
        Message = "Profiling retention: could not delete '{Name}' ({ExceptionType}: {Reason}) — it is most likely open in a session. Left in place.")]
    public static partial void DeleteSkipped(ILogger logger, string name, string exceptionType, string reason);

    [LoggerMessage(EventId = 6163, Level = LogLevel.Warning,
        Message = "Profiling retention: pinned captures alone occupy {PinnedBytes} bytes, which exceeds the {BudgetBytes}-byte budget in '{Directory}'. "
                + "The budget can no longer be honoured — unpin something or raise it.")]
    public static partial void PinnedExceedBudget(ILogger logger, long pinnedBytes, long budgetBytes, string directory);

    [LoggerMessage(EventId = 6164, Level = LogLevel.Information,
        Message = "Profiling retention: captures still occupy {CaptureBytes} bytes against a {BudgetBytes}-byte budget after pruning — "
                + "{KeepLatest} newest are protected and {Skipped} could not be deleted.")]
    public static partial void StillOverBudget(ILogger logger, long captureBytes, long budgetBytes, int keepLatest, int skipped);

    [LoggerMessage(EventId = 6165, Level = LogLevel.Warning,
        Message = "Profiling retention: could not enumerate '{Directory}' ({ExceptionType}: {Reason}) — skipping this pruning pass.")]
    public static partial void EnumerationFailed(ILogger logger, string directory, string exceptionType, string reason);

    [LoggerMessage(EventId = 6166, Level = LogLevel.Warning,
        Message = "Profiling retention: '{Directory}/retention.json' could not be read ({Reason}) — falling back to the built-in default policy.")]
    public static partial void PolicyUnreadable(ILogger logger, string directory, string reason);
}

/// <summary>Source-generated log messages for the profiler bootstrap's capture-location resolution.</summary>
internal static partial class ProfilerBootstrapLog
{
    [LoggerMessage(EventId = 6167, Level = LogLevel.Warning,
        Message = "Profiling: could not prepare the capture directory under '{BundleDirectory}' ({ExceptionType}: {Reason}) — this session records no "
                + "capture file. Set Typhon:Profiler:Trace to a writable path to override.")]
    public static partial void CaptureDirectoryUnavailable(ILogger logger, string bundleDirectory, string exceptionType, string reason);
}
