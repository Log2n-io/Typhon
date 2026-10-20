using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Diagnostics;
using System.IO;

namespace Typhon.Engine.Internals;

/// <summary>
/// The drill (04 §6, plan 1416 §3.6): restore a point into a scratch directory, open it (recovery runs) and close it, check it deep, record what was found,
/// delete the scratch. The only check that proves a backup restores into a database. <see cref="BackupDrillOutcome.Failed"/> is the point's;
/// <see cref="BackupDrillOutcome.Error"/> is the drill's own (scratch I/O, cancelled) and says nothing about the point.
/// </summary>
internal static class BackupDrillRunner
{
    /// <summary>
    /// Runs a drill. Throws for what is not a drill result: <see cref="RestoreRefusedException"/> (a scratch inside the store, a point of another format
    /// revision), <see cref="BackupChainBrokenException"/> for a point the store does not hold. A point that does not restore, open or check is a report.
    /// </summary>
    public static BackupDrillReport Run(string store, PointSelector point, BackupDrillOptions options, IStoreFileSystem fs = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.ScratchDirectory, nameof(options));
        fs ??= StoreFileSystem.Instance;
        var log = options.Logger ?? NullLogger.Instance;
        var started = DateTime.UtcNow;

        // 1 RESOLVE — a point the store does not hold is no drill: there is nothing to record against.
        var view = StoreView.Open(store, fs, log, trustCatalog: false);
        var id = ChainResolver.Select(view, point);
        var (compacted, original) = view.Find(id);
        var summary = BackupOperations.Summarize(compacted ?? original);
        if (summary == null)
        {
            ChainResolver.Resolve(view, id);   // throws, naming the restorable points
        }

        // SCRATCH (DL-25) — refusals before anything is written.
        var scratch = fs.GetFullPath(options.ScratchDirectory);
        if (IsWithin(scratch, fs.GetFullPath(view.Paths.Root)))
        {
            BackupLog.RestoreRefused(log, RestoreRefusalReason.ScratchInsideStore, scratch);
            throw new RestoreRefusedException(RestoreRefusalReason.ScratchInsideStore, scratch,
                "a drill's scratch directory may not lie inside the store: a mirror of the store would copy the whole restored image; pick another");
        }

        var work = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        if (fs.DirectoryExists(work) || fs.FileExists(work))
        {
            throw new RestoreRefusedException(RestoreRefusalReason.TargetExists, work, "the drill's scratch directory exists; it is never reused");
        }

        var run = new DrillState(options, summary);
        string bundle = null;
        try
        {
            try
            {
                fs.CreateDirectory(work);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Finish(fs, view, run.Error(BackupDrillStage.Scratch, $"the scratch directory '{work}' could not be created: {e.Message}"), started,
                    options, log, null);
            }

            bundle = Restore(fs, store, id, work, run);
            if (bundle != null && run.Outcome == 0)
            {
                Check(bundle, run);
            }

            return Finish(fs, view, run, started, options, log, options.KeepScratch ? bundle : null);
        }
        finally
        {
            if (!options.KeepScratch)
            {
                DeleteScratch(log, work);
            }
        }
    }

    /// <summary>Restores and settles; returns the bundle, or null when the restore failed (the outcome says why).</summary>
    private static string Restore(IStoreFileSystem fs, string store, uint id, string work, DrillState run)
    {
        var options = run.Options;
        var opener = options.OpenAndClose ?? (b => OpenAndCloseRestored(b, options.Logger, options.PageCacheBytes));
        var clock = Stopwatch.StartNew();
        var imageClean = false;
        BackupSettleResult settle = null;
        RestoreOutcome outcome;
        try
        {
            outcome = RestoreStager.Run(store, PointSelector.ById(id), work, new RestoreOptions
            {
                Cancellation = options.Cancellation,
                Progress = options.Progress == null ? null : (done, total) => options.Progress(BackupDrillStage.Restore, done, total),
                Logger = options.Logger,
                OpenAndClose = b =>
                {
                    run.RestoreTime = clock.Elapsed;
                    options.Progress?.Invoke(BackupDrillStage.Open, 0, 0);
                    imageClean = ReadCleanShutdown(b);
                    options.Cancellation.ThrowIfCancellationRequested();
                    settle = opener(b);
                },
            }, fs);
        }
        catch (OperationCanceledException)
        {
            run.Error(BackupDrillStage.Restore, "cancelled");
            return null;
        }
        catch (Exception e) when (e is BackupChainBrokenException or BackupPackCorruptException)
        {
            run.Fail(BackupDrillStage.Restore, e.Message);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            run.Error(BackupDrillStage.Restore, e.Message);
            return null;
        }

        // The restore calls the opener once it has published the bundle, whatever else happens.
        run.OpenTime = clock.Elapsed - run.RestoreTime;
        run.SuspectPages = outcome.SuspectPages?.Count ?? 0;

        // GUARD — whoever the opener was, the check runs only on a bundle a recovery settled: (a) an unclean image the open ran no recovery on is the
        // image at C, not the database at P; (b) a bundle its close left unclean was not recovered (or not closed) either.
        if (outcome.SettleFailure is OperationCanceledException)
        {
            run.Error(BackupDrillStage.Open, "cancelled");
        }
        else if (Find(outcome.SettleFailure, TyphonErrorCode.RecoveryArchetypeNotRegistered) is { } unregistered)
        {
            // The opener's limit, not the point's: a process without the application's archetypes cannot replay their WAL records (#1309).
            run.Error(BackupDrillStage.Open, $"{unregistered.Message} Drill it in a process that registers the application's archetypes "
                                             + "(BackupDrillOptions.OpenAndClose).");
        }
        else if (outcome.SettleFailure != null)
        {
            run.Fail(BackupDrillStage.Open, $"the restored database did not open: {Describe(outcome.SettleFailure)}");
        }
        else if (settle == null)
        {
            run.Fail(BackupDrillStage.Open, "the opener returned no result");
        }
        else
        {
            run.Recovery = settle.Recovery;
            if (!imageClean && !settle.CrashRecoveryRan)
            {
                run.Fail(BackupDrillStage.Open, "the restored image was unclean and its open ran no crash recovery: the WAL tail was never replayed");
            }
            else if (!ReadCleanShutdown(outcome.Target))
            {
                run.Fail(BackupDrillStage.Open, "the restored database was not left cleanly closed: its open did not recover it, or did not close it");
            }
        }

        return outcome.Target;
    }

    private static void Check(string bundle, DrillState run)
    {
        var options = run.Options;
        options.Progress?.Invoke(BackupDrillStage.Check, 0, 0);
        var clock = Stopwatch.StartNew();
        IntegrityReport report;
        try
        {
            using var source = new OfflineBundlePageSource(bundle);
            report = IntegrityScanner.Scan(source, new IntegrityOptions
            {
                Depth = ScanDepth.Deep,
                Cancellation = options.Cancellation,
                Progress = options.Progress == null ? null : (_, done, total) => options.Progress(BackupDrillStage.Check, done, total),
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            run.CheckTime = clock.Elapsed;
            run.Error(BackupDrillStage.Check, e.Message);
            return;
        }

        run.CheckTime = clock.Elapsed;
        if (options.Cancellation.IsCancellationRequested)
        {
            // A cancelled scan returns a partial report: it proves nothing either way.
            run.Error(BackupDrillStage.Check, "cancelled");
            return;
        }

        run.Verdict = report.Verdict;
        run.Findings = report.Findings.Count;
        switch (report.Verdict)
        {
            case IntegrityVerdict.Sound:
                run.Outcome = run.SuspectPages > 0 ? BackupDrillOutcome.PassedWithWarnings : BackupDrillOutcome.Passed;
                break;
            case IntegrityVerdict.SoundWithLeaks:
                run.Outcome = BackupDrillOutcome.PassedWithWarnings;
                break;
            default:
                run.Fail(BackupDrillStage.Check, $"the restored database checks {report.Verdict} ({report.Findings.Count} finding(s))");
                break;
        }
    }

    private static BackupDrillReport Finish(IStoreFileSystem fs, StoreView view, DrillState run, DateTime started, BackupDrillOptions options, ILogger log,
        string keptBundle)
    {
        string recordFile = null;
        if (options.Record)
        {
            try
            {
                recordFile = DrillRecords.Write(fs, view.Paths.Root, new DrillRecordFile
                {
                    FormatVersion = DrillRecordFile.CurrentFormatVersion,
                    DrillId = Guid.NewGuid(),
                    BackupId = run.Point.BackupId,
                    ChainId = run.Point.ChainId,
                    TimelineId = run.Point.TimelineId,
                    PointLsn = run.Point.PointLsn,
                    StartedUtc = started,
                    Outcome = run.Outcome,
                    FailedStage = run.FailedStage,
                    Error = run.Problem,
                    RestoreMs = (long)run.RestoreTime.TotalMilliseconds,
                    OpenMs = (long)run.OpenTime.TotalMilliseconds,
                    CheckMs = (long)run.CheckTime.TotalMilliseconds,
                    RecordsReplayed = run.Recovery.RecordsReplayed,
                    UowsPromoted = run.Recovery.UowsPromoted,
                    UowsVoided = run.Recovery.UowsVoided,
                    Verdict = run.Verdict,
                    Findings = run.Findings,
                    Host = Environment.MachineName,
                    ToolVersion = $"Typhon {typeof(BackupDrillRunner).Assembly.GetName().Version}",
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The drill's result stands; only its trace in the store is missing (health will not count it).
                BackupLog.DrillRecordFailed(log, view.Paths.Root, run.Point.BackupId, e.Message);
            }
        }

        BackupLog.DrillEnded(log, run.Point.BackupId, run.Outcome, run.FailedStage, run.Problem ?? "none",
            (long)(run.RestoreTime + run.OpenTime + run.CheckTime).TotalMilliseconds);
        return new BackupDrillReport(run.Point, run.Outcome, run.FailedStage, run.Problem, run.RestoreTime, run.OpenTime, run.CheckTime, run.Recovery,
            run.Verdict, run.Findings, run.SuspectPages, recordFile, keptBundle);
    }

    /// <summary>
    /// Opens a restored bundle with no application schema through <see cref="DatabaseEngine.Open"/>, which runs <c>InitializeArchetypes</c> (where the WAL
    /// replays, the derived structures rebuild and the image is sealed), then disposes it: a clean close.
    /// </summary>
    public static BackupSettleResult OpenAndCloseRestored(string bundle, ILogger logger, ulong pageCacheBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundle);
        var clock = Stopwatch.StartNew();
        using var factory = logger == null ? null : new OneLoggerFactory(logger);
        BackupSettleResult settled;
        using (var engine = DatabaseEngine.Open(bundle, o =>
               {
                   if (pageCacheBytes != 0)
                   {
                       o.PageCacheSize(pageCacheBytes);
                   }
               }, factory))
        {
            settled = SettleResultOf(engine, clock.Elapsed);
        }

        return settled with { Duration = clock.Elapsed };
    }

    /// <summary>What an engine's open recovered, for an opener that opened it (read before its dispose).</summary>
    internal static BackupSettleResult SettleResultOf(DatabaseEngine engine, TimeSpan duration)
    {
        var recovery = engine.LastRecoveryResult;

        // The scan's figures are WalRecovery's; the replay is RecoveryDriver's, run later in InitializeArchetypes.
        recovery.RecordsReplayed = engine.LastWalV2RecoveryResult.RecordsApplied;
        return new BackupSettleResult(engine.LastOpenRanCrashRecovery, recovery, duration);
    }

    private static TyphonException Find(Exception e, TyphonErrorCode code)
    {
        for (; e != null; e = e.InnerException)
        {
            if (e is TyphonException t && t.ErrorCode == code)
            {
                return t;
            }
        }

        return null;
    }

    /// <summary>The bundle's meta pair says it was closed cleanly (the pair the open selects, read by <see cref="BootstrapReader"/>).</summary>
    private static bool ReadCleanShutdown(string bundle)
    {
        using var source = new OfflineBundlePageSource(bundle);
        var view = BootstrapReader.Read(source);
        return view.IsUsable && view.ReadWatermarks().CleanShutdown;
    }

    /// <summary>
    /// <paramref name="path"/> is <paramref name="directory"/> or lies under it (case-insensitive: the conservative answer on any file system).
    /// </summary>
    internal static bool IsWithin(string path, string directory)
    {
        var p = Path.TrimEndingDirectorySeparator(path);
        var d = Path.TrimEndingDirectorySeparator(directory);
        return p.Equals(d, StringComparison.OrdinalIgnoreCase)
               || (p.Length > d.Length && p.StartsWith(d, StringComparison.OrdinalIgnoreCase)
                                       && (p[d.Length] == Path.DirectorySeparatorChar || p[d.Length] == Path.AltDirectorySeparatorChar));
    }

    private static void DeleteScratch(ILogger log, string work)
    {
        try
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            BackupLog.DrillScratchLeft(log, work, e.Message);
        }
    }

    private static string Describe(Exception e)
    {
        var message = e.Message;
        for (var inner = e.InnerException; inner != null; inner = inner.InnerException)
        {
            message += " Caused by: " + inner.Message;
        }

        return message;
    }

    /// <summary>The drill's state as it goes.</summary>
    private sealed class DrillState(BackupDrillOptions options, BackupPointSummary point)
    {
        public BackupDrillOptions Options { get; } = options;

        public BackupPointSummary Point { get; } = point;

        public BackupDrillOutcome Outcome { get; set; }

        public BackupDrillStage FailedStage { get; private set; }

        public string Problem { get; private set; }

        public TimeSpan RestoreTime { get; set; }

        public TimeSpan OpenTime { get; set; }

        public TimeSpan CheckTime { get; set; }

        public WalRecoveryResult Recovery { get; set; }

        public IntegrityVerdict Verdict { get; set; }

        public int Findings { get; set; }

        public int SuspectPages { get; set; }

        public DrillState Fail(BackupDrillStage stage, string problem) => End(BackupDrillOutcome.Failed, stage, problem);

        public DrillState Error(BackupDrillStage stage, string problem) => End(BackupDrillOutcome.Error, stage, problem);

        private DrillState End(BackupDrillOutcome outcome, BackupDrillStage stage, string problem)
        {
            (Outcome, FailedStage, Problem) = (outcome, stage, problem);
            return this;
        }
    }

    /// <summary>Hands one logger to every category: the drill's engine logs where the drill does.</summary>
    private sealed class OneLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }
}
