using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;

namespace Typhon.Shell.Commands;

/// <summary>
/// <c>typhon backup create &lt;bundle&gt; [--store S] [--full] [--wait [--timeout T]]</c> — a point of a database. A running engine whose backup is
/// configured and accepts requests is asked through <c>backup.request</c> (07 §3, DL-24): it takes the point into its own store (<c>--store</c> is
/// ignored), and <c>--wait</c> follows its status until the point is published or failed. Otherwise the database is backed up offline (02 §8) into
/// <c>--store</c>; a holder that advertises it will yield (the Workbench, #621) is asked to release it first, any other is refused (exit 4).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BackupCreateCommand : Command<BackupCreateCommand.Settings>
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class Settings : BackupSettings
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        [CommandArgument(0, "<bundle>")]
        [Description("Path to the .typhon bundle directory.")]
        public string Bundle { get; set; }

        [CommandOption("--full")]
        [Description("Take a full point even when an incremental would do.")]
        public bool Full { get; set; }

        [CommandOption("--wal-dir")]
        [Description("The database's WAL directory, when its engine was configured with one other than {bundle}/wal.")]
        public string WalDir { get; set; }

        [CommandOption("--wait")]
        [Description("With a running engine: wait until the point is published (or failed); the exit code is its outcome.")]
        public bool Wait { get; set; }

        [CommandOption("--timeout")]
        [Description("With --wait: how long to wait (30d, 12h, 90m, 45s). Default 10m.")]
        public string Timeout { get; set; }

        /// <summary>How long a yieldable holder gets to release the database: the engine's own handoff wait by default. Not on the command line.</summary>
        public TimeSpan HandoffTimeout { get; set; } = new PagedMMFOptions().LockHandoffTimeout;

        /// <summary>How often <c>--wait</c> reads the engine's status. Not on the command line.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);
        // ReSharper restore UnusedAutoPropertyAccessor.Global
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
        => BackupCli.RejectLeftovers(context, settings) is var usage and >= 0 ? usage : Run(settings, AnsiConsole.Console, Console.Out, cancellationToken);

    internal static int Run(Settings settings, IAnsiConsole console, TextWriter json, CancellationToken ct)
    {
        if (!BackupCli.TryResolveFormat(settings.Format, out var asJson, out var formatError))
        {
            return new BackupOutput(console, json, false).Error(BackupCli.ExitUsage, formatError);
        }

        var output = new BackupOutput(console, json, asJson);
        var timeout = TimeSpan.FromMinutes(10);
        if (settings.Timeout != null && !BackupCli.TryParseDuration(settings.Timeout, out timeout, out var timeoutError))
        {
            return output.Error(BackupCli.ExitUsage, timeoutError);
        }

        // A running engine that takes requests backs itself up (DL-24).
        string live;
        try
        {
            live = OfflineBundlePageSource.ResolveBundleDirectory(settings.Bundle);
        }
        catch (Exception ex) when (BackupCli.ExitCodeFor(ex) >= 0 || output.AsJson)
        {
            return output.Error(ex);
        }

        if (BackupOperations.ReadStatus(live) is { AcceptsRequests: true } running)
        {
            return Request(live, running, settings, timeout, output, ct);
        }

        if (settings.Wait)
        {
            return output.Error(BackupCli.ExitUsage, "--wait follows a running engine's backup; this database has none that accepts requests.");
        }

        if (string.IsNullOrWhiteSpace(settings.Store))
        {
            return output.Error(BackupCli.ExitUsage, "--store is required: the store to back the database up into.");
        }

        // The bundle as the capture resolves it (a stem, a .typhon directory, or its data file), once: the handoff, the refusal and the request file
        // must all name the directory the capture will lock.
        string bundle;
        bool asked;
        string refusal;
        try
        {
            bundle = OfflineBundlePageSource.ResolveBundleDirectory(settings.Bundle);
            if (!AskAYieldableHolder(bundle, settings.HandoffTimeout, ct, out asked, out refusal))
            {
                return output.Error(BackupCli.ExitRefused, refusal);
            }
        }
        catch (Exception ex) when (BackupCli.ExitCodeFor(ex) >= 0 || output.AsJson)
        {
            return output.Error(ex);
        }

        BackupPointSummary point;
        try
        {
            point = BackupOperations.Create(bundle, settings.Store, OptionsFor(settings, ct));
        }
        catch (DatabaseLockedException ex)
        {
            return output.Error(BackupCli.ExitRefused, OnlineRefusal(bundle, ex));
        }
        catch (Exception ex) when (BackupCli.ExitCodeFor(ex) >= 0 || output.AsJson)
        {
            return output.Error(ex);
        }
        finally
        {
            // Retired only now, once the capture held the lock: while it exists, the holder that yielded must not take the database back (#621).
            if (asked)
            {
                DatabaseLockFile.DeleteRequest(bundle);
            }
        }

        var exit = point.Suspect ? BackupCli.ExitWarning : BackupCli.ExitOk;
        if (output.AsJson)
        {
            output.Json(new { exitCode = exit, point });
            return exit;
        }

        console.MarkupLine($"[green]Backed up[/] {Markup.Escape(bundle)} as point [bold]#{point.BackupId}[/] ({point.Kind}, C={point.CheckpointLsn}, "
                           + $"P={point.PointLsn}, {point.PageCount} page(s), {BackupCli.Bytes(point.PackBytes)}).");
        if (point.Suspect)
        {
            console.MarkupLine("[yellow]The database had damaged pages at capture; the point keeps them as they were. Run `typhon check` on it.[/]");
        }

        return exit;
    }

    /// <summary>What the command line asks of the capture. Offline, <c>--full</c> matters only to a database an engine has backed up before.</summary>
    internal static BackupCreateOptions OptionsFor(Settings settings, CancellationToken ct) =>
        new() { Full = settings.Full, WalDirectory = settings.WalDir, Cancellation = ct };

    private static string OnlineRefusal(string bundle, DatabaseLockedException ex) =>
        $"Database '{bundle}' is open in process {ex.OwnerPid} on {ex.OwnerMachine}, and its engine takes no backup requests (no BackupOptions, or "
        + "AcceptRequests off). Configure its backup (DatabaseEngineOptions.Backup) to ask it from here, back it up from inside the application with "
        + "engine.Backup.CreateAsync(), or stop the process and run this again.";

    /// <summary>
    /// The request path: <c>backup.request</c> written (the one writer, <see cref="BackupOperations.RequestBackup"/>); with <c>--wait</c>, the engine's
    /// status followed until its job for this request ended: 0 published (1 when suspect), 2 failed, deferred or timed out.
    /// </summary>
    private static int Request(string bundle, BackupStatusReport before, Settings settings, TimeSpan timeout, BackupOutput output, CancellationToken ct)
    {
        Guid id;
        try
        {
            id = BackupOperations.RequestBackup(bundle, settings.Full ? BackupRequestKind.Full : BackupRequestKind.Auto,
                $"typhon backup create (PID {Environment.ProcessId})");
        }
        catch (Exception ex) when (BackupCli.ExitCodeFor(ex) >= 0 || output.AsJson)
        {
            return output.Error(ex);
        }

        if (!settings.Wait)
        {
            if (output.AsJson)
            {
                output.Json(new { exitCode = BackupCli.ExitOk, requestId = id, store = before.StorePath });
            }
            else
            {
                output.Console.MarkupLine($"[green]Requested[/] a point of {Markup.Escape(bundle)} from its running engine (request {id}); it goes into "
                                          + $"{Markup.Escape(before.StorePath)}. Follow it with `typhon backup status {Markup.Escape(bundle)}`.");
            }

            return BackupCli.ExitOk;
        }

        var points = before.Status.PointsTotal;
        var failures = before.Status.FailuresTotal;
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (ct.WaitHandle.WaitOne(settings.PollInterval))
            {
                return output.Error(BackupCli.ExitFailed, $"Cancelled while waiting for request {id}; the engine may still take the point.");
            }

            var report = BackupOperations.ReadStatus(bundle);
            var status = report.Status;
            if (status != null && status.LastRequestId == id && status.State == BackupRunState.Idle)
            {
                if (status.PointsTotal > points && status.LastPoint != null)
                {
                    var exit = status.LastPoint.Suspect ? BackupCli.ExitWarning : BackupCli.ExitOk;
                    if (output.AsJson)
                    {
                        output.Json(new { exitCode = exit, requestId = id, point = status.LastPoint });
                    }
                    else
                    {
                        output.Console.MarkupLine($"[green]Backed up[/] {Markup.Escape(bundle)} as point [bold]#{status.LastPoint.BackupId}[/] "
                                                  + $"({status.LastPoint.Kind}, P={status.LastPoint.PointLsn}) by its running engine.");
                    }

                    return exit;
                }

                var why = status.FailuresTotal > failures ? status.LastFailure?.Message ?? "it failed"
                    : "the engine could not take it now (its store was held by another writer, or it was closing); ask again";
                return output.Error(BackupCli.ExitFailed, $"Request {id} did not produce a point: {why}");
            }

            if (!report.EngineLive)
            {
                return output.Error(BackupCli.ExitFailed, $"The engine stopped before request {id} was served.");
            }

            if (DateTime.UtcNow >= deadline)
            {
                return output.Error(BackupCli.ExitFailed, $"Request {id} was not served within {timeout}; the engine may still take the point.");
            }
        }
    }

    /// <summary>
    /// A live holder that advertised it will yield (#621) is asked to, the way a second engine's open asks: the request file, then a bounded wait for
    /// the lock to go. <c>false</c> when it did not release in time (the request is withdrawn). Any other holder is left to the capture, which refuses
    /// it. On success the request stays until the capture holds the lock.
    /// </summary>
    private static bool AskAYieldableHolder(string bundle, TimeSpan timeout, CancellationToken ct, out bool asked, out string refusal)
    {
        asked = false;
        refusal = null;
        if (!DatabaseLockFile.TryReadLock(bundle, out var holder) || !holder.Yieldable
            || !string.Equals(holder.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || !DatabaseLockFile.IsOwnerLive(holder.Pid, holder.MachineName))
        {
            return true;
        }

        DatabaseLockFile.WriteRequest(bundle);
        asked = true;
        var deadline = DateTime.UtcNow + timeout;
        while (DatabaseLockFile.IsHeldByLiveProcess(bundle))
        {
            if (DateTime.UtcNow >= deadline)
            {
                DatabaseLockFile.DeleteRequest(bundle);
                asked = false;
                refusal = $"Database '{bundle}' is held by process {holder.Pid}, which advertised that it would yield it but did not release it within "
                          + $"{(int)timeout.TotalMilliseconds} ms.";
                return false;
            }

            if (ct.WaitHandle.WaitOne(20))
            {
                DatabaseLockFile.DeleteRequest(bundle);
                asked = false;
                ct.ThrowIfCancellationRequested();
            }
        }

        return true;
    }
}

/// <summary><c>typhon backup list --store S</c> — the store's points and chains.</summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BackupListCommand : Command<BackupListCommand.Settings>
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class Settings : BackupSettings
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        [CommandOption("-v|--verbose")]
        [Description("Also show C, image pages, WAL segments, timeline and pack file per point.")]
        public bool Verbose { get; set; }

        [CommandOption("--restorable")]
        [Description("Run a quick verify and mark the restorable points.")]
        public bool Restorable { get; set; }
        // ReSharper restore UnusedAutoPropertyAccessor.Global
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
        => BackupCli.RejectLeftovers(context, settings) is var usage and >= 0 ? usage : Run(settings, AnsiConsole.Console, Console.Out);

    internal static int Run(Settings settings, IAnsiConsole console, TextWriter json)
    {
        if (!BackupCli.TryResolveFormat(settings.Format, out var asJson, out var formatError))
        {
            return new BackupOutput(console, json, false).Error(BackupCli.ExitUsage, formatError);
        }

        var output = new BackupOutput(console, json, asJson);
        if (string.IsNullOrWhiteSpace(settings.Store))
        {
            return output.Error(BackupCli.ExitUsage, "--store is required.");
        }

        BackupStoreListing listing;
        try
        {
            listing = BackupOperations.List(settings.Store, settings.Restorable);
        }
        catch (Exception ex) when (BackupCli.ExitCodeFor(ex) >= 0 || output.AsJson)
        {
            return output.Error(ex);
        }

        if (output.AsJson)
        {
            output.Json(new { exitCode = BackupCli.ExitOk, listing });
            return BackupCli.ExitOk;
        }

        console.MarkupLine($"[grey]Store {listing.StoreId} of database {listing.DatabaseId}: {listing.Points.Count} point(s), "
                           + $"{BackupCli.Bytes(listing.Points.Sum(p => p.PackBytes))}.[/]");
        var table = new Table().AddColumns("Id", "Kind", "Chain", "Parent", "P", "Frozen (UTC)", "Pages", "Size", "Drill", "Notes");
        if (settings.Verbose)
        {
            table.AddColumns("C", "Image pages", "WAL segs", "Timeline", "Pack");
        }

        if (listing.RestorablePoints != null)
        {
            table.AddColumn("Restorable");
        }

        foreach (var p in listing.Points)
        {
            var notes = string.Join(", ", new[] { p.Offline ? "offline" : null, p.Suspect ? "suspect" : null }.Where(n => n != null));
            var row = new[]
            {
                p.BackupId.ToString(), p.Kind.ToString(), p.ChainId.ToString()[..8], p.ParentId == 0 ? "-" : p.ParentId.ToString(), p.PointLsn.ToString(),
                p.FreezeUtc.ToString("yyyy-MM-dd HH:mm:ss"), p.PageCount.ToString(), BackupCli.Bytes(p.PackBytes), Drill(listing, p.BackupId), notes,
            }.AsEnumerable();
            if (settings.Verbose)
            {
                row = row.Concat([p.CheckpointLsn.ToString(), p.ImagePages.ToString(), p.TailSegments.ToString(), p.TimelineId.ToString()[..8], p.PackFile]);
            }

            if (listing.RestorablePoints != null)
            {
                row = row.Append(listing.RestorablePoints.Contains(p.BackupId) ? "yes" : "[red]no[/]");
            }

            table.AddRow(row.Select(c => c.StartsWith("[red]", StringComparison.Ordinal) ? c : Markup.Escape(c)).ToArray());
        }

        console.Write(table);
        foreach (var c in listing.Chains)
        {
            console.MarkupLine($"Chain {c.ChainId.ToString()[..8]}: base #{c.BaseId}, head #{c.HeadBackupId}, {c.Depth} point(s), restore of the head reads "
                               + $"{c.HeadDepth} pack(s).");
        }

        foreach (var u in listing.UnreadablePacks)
        {
            console.MarkupLine($"[red]Unreadable pack {Markup.Escape(u.Key)}: {Markup.Escape(u.Value)}[/]");
        }

        foreach (var t in listing.TemporaryFiles)
        {
            console.MarkupLine($"[yellow]Temporary file {Markup.Escape(t)} (an interrupted or running backup).[/]");
        }

        return BackupCli.ExitOk;
    }

    /// <summary>The point's newest drill: <c>passed</c> or <c>failed</c> and its date; <c>-</c> when never drilled.</summary>
    private static string Drill(BackupStoreListing listing, uint id) => listing.Drills.TryGetValue(id, out var d)
        ? (d.Outcome == BackupDrillOutcome.Failed ? "[red]failed[/] " : d.Outcome == BackupDrillOutcome.PassedWithWarnings ? "passed (warnings) " : "passed ")
          + d.Utc.ToString("yyyy-MM-dd")
        : "-";
}

/// <summary>
/// <c>typhon backup verify --store S [--point X] [--level quick|full|deep]</c> — checks packs and chains without restoring anything (04 §4); deep also
/// scans the image of the point (or of the newest restorable one). The verdict is the exit code: 0 ok, 1 advisories, 2 damage with a restorable point
/// left, 3 nothing restorable.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BackupVerifyCommand : Command<BackupVerifyCommand.Settings>
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class Settings : BackupSettings
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        [CommandOption("-p|--point")]
        [Description("One point (latest or an id) and its chain. Default: the whole store.")]
        public string Point { get; set; }

        [CommandOption("-l|--level")]
        [Description("quick | full | deep (full, plus the integrity scan of the point's image). Default: quick.")]
        public string Level { get; set; }
        // ReSharper restore UnusedAutoPropertyAccessor.Global
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
        => BackupCli.RejectLeftovers(context, settings) is var usage and >= 0 ? usage : Run(settings, AnsiConsole.Console, Console.Out);

    internal static int Run(Settings settings, IAnsiConsole console, TextWriter json)
    {
        if (!BackupCli.TryResolveFormat(settings.Format, out var asJson, out var formatError))
        {
            return new BackupOutput(console, json, false).Error(BackupCli.ExitUsage, formatError);
        }

        var output = new BackupOutput(console, json, asJson);
        if (string.IsNullOrWhiteSpace(settings.Store))
        {
            return output.Error(BackupCli.ExitUsage, "--store is required.");
        }

        if (!TryParseLevel(settings.Level, out var level, out var levelError))
        {
            return output.Error(BackupCli.ExitUsage, levelError);
        }

        PointSelector point = default;
        if (settings.Point != null && !BackupCli.TryParsePoint(settings.Point, out point, out var pointError))
        {
            return output.Error(BackupCli.ExitUsage, pointError);
        }

        BackupVerifyReport report;
        try
        {
            report = settings.Point == null ? BackupOperations.Verify(settings.Store, level) : BackupOperations.Verify(settings.Store, point, level);
        }
        catch (Exception ex) when (BackupCli.ExitCodeFor(ex) >= 0 || output.AsJson)
        {
            return output.Error(ex);
        }

        var exit = ExitCodeOf(report.Verdict);
        if (output.AsJson)
        {
            output.Json(new { exitCode = exit, report });
            return exit;
        }

        var colour = exit switch { BackupCli.ExitOk => "green", BackupCli.ExitWarning => "yellow", _ => "red" };
        console.MarkupLine($"[{colour}]{report.Verdict}[/] — {report.Level} verify of {report.Packs.Count} pack(s), "
                           + $"{report.Points.Count(p => p.Restorable)} of {report.Points.Count} point(s) restorable, {BackupCli.Bytes(report.BytesRead)} read "
                           + $"in {report.Duration.TotalMilliseconds:0} ms.");
        if (report.RemovedPoints > 0)
        {
            console.MarkupLine($"[grey]{report.RemovedPoints} point(s) were deleted by a retention while the verify ran; they are left out.[/]");
        }

        foreach (var f in report.StoreFindings.Concat(report.Packs.SelectMany(p => p.Findings)))
        {
            var page = f.PageId >= 0 ? $" page {f.PageId}" : "";
            console.MarkupLine($"  [{(f.Severity == BackupFindingSeverity.Damage ? "red" : "yellow")}]{Markup.Escape(f.Code)}[/] "
                               + $"{Markup.Escape(f.File ?? "store")}{page}: {Markup.Escape(f.Message)}");
        }

        foreach (var p in report.Points.Where(p => !p.Restorable))
        {
            console.MarkupLine($"  [red]Point #{p.BackupId} is not restorable[/] (broken by #{string.Join(", #", p.BrokenBy)}).");
        }

        return exit;
    }

    internal static int ExitCodeOf(BackupVerifyVerdict verdict) => verdict switch
    {
        BackupVerifyVerdict.Ok => BackupCli.ExitOk,
        BackupVerifyVerdict.Warning => BackupCli.ExitWarning,
        BackupVerifyVerdict.Damaged => BackupCli.ExitFailed,
        _ => BackupCli.ExitStoreDamaged,
    };

    private static bool TryParseLevel(string value, out BackupVerifyLevel level, out string error)
    {
        error = null;
        level = BackupVerifyLevel.Quick;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "quick":
                return true;
            case "full":
                level = BackupVerifyLevel.Full;
                return true;
            case "deep":
                level = BackupVerifyLevel.Deep;
                return true;
            default:
                error = $"Unknown level '{value}'. Expected quick, full or deep.";
                return false;
        }
    }
}

/// <summary>
/// <c>typhon backup restore --store S [--point X] --to &lt;dir or bundle&gt;</c> — rebuilds a point's database (04 §2). It opens through the crash path
/// to exactly the point's committed state; an existing bundle is moved aside with <c>--replace</c>, never deleted.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BackupRestoreCommand : Command<BackupRestoreCommand.Settings>
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class Settings : BackupSettings
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        [CommandOption("-p|--point")]
        [Description("latest (default) or a backup id.")]
        public string Point { get; set; }

        [CommandOption("-t|--to")]
        [Description("The bundle to create ({name}.typhon, the database's own name) or the directory to create it in.")]
        public string To { get; set; }

        [CommandOption("--replace")]
        [Description("Move an existing bundle aside (never deleted) and restore in its place.")]
        public bool Replace { get; set; }

        [CommandOption("--allow-damaged")]
        [Description("Forensics: write pages that fail their check instead of refusing, and list them.")]
        public bool AllowDamaged { get; set; }

        [CommandOption("--allow-format-mismatch")]
        [Description("Restore an image of another on-disk format revision (it will not open in this build).")]
        public bool AllowFormatMismatch { get; set; }
        // ReSharper restore UnusedAutoPropertyAccessor.Global
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
        => BackupCli.RejectLeftovers(context, settings) is var usage and >= 0 ? usage : Run(settings, AnsiConsole.Console, Console.Out, cancellationToken);

    internal static int Run(Settings settings, IAnsiConsole console, TextWriter json, CancellationToken ct)
    {
        if (!BackupCli.TryResolveFormat(settings.Format, out var asJson, out var formatError))
        {
            return new BackupOutput(console, json, false).Error(BackupCli.ExitUsage, formatError);
        }

        var output = new BackupOutput(console, json, asJson);
        if (string.IsNullOrWhiteSpace(settings.Store) || string.IsNullOrWhiteSpace(settings.To))
        {
            return output.Error(BackupCli.ExitUsage, "--store and --to are required.");
        }

        if (!BackupCli.TryParsePoint(settings.Point, out var point, out var pointError))
        {
            return output.Error(BackupCli.ExitUsage, pointError);
        }

        BackupRestoreResult result;
        try
        {
            result = BackupOperations.Restore(settings.Store, point, settings.To, new BackupRestoreOptions
            {
                Replace = settings.Replace,
                AllowDamaged = settings.AllowDamaged,
                AllowFormatMismatch = settings.AllowFormatMismatch,
                Cancellation = ct,
            });
        }
        catch (Exception ex) when (BackupCli.ExitCodeFor(ex) >= 0 || output.AsJson)
        {
            return output.Error(ex);
        }

        // Published, but its settle (an open and close of the restored database) failed: the outcome still says where everything is.
        var exit = result.SettleError != null
            ? BackupCli.ExitFailed
            : result.SuspectPages.Count > 0 || result.DamagedPages.Count > 0 ? BackupCli.ExitWarning : BackupCli.ExitOk;
        if (output.AsJson)
        {
            output.Json(new { exitCode = exit, result });
            return exit;
        }

        console.MarkupLine($"[green]Restored[/] point [bold]#{result.Point.BackupId}[/] ({result.Point.Kind}, P={result.Point.PointLsn}) to "
                           + $"{Markup.Escape(result.Target)}: {result.PagesWritten} page(s), {result.TailSegments} WAL segment(s), "
                           + $"{BackupCli.Bytes(result.BytesWritten)} in {result.Duration.TotalMilliseconds:0} ms.");
        if (result.DisplacedBundle != null)
        {
            console.MarkupLine($"The bundle that was there is now {Markup.Escape(result.DisplacedBundle)}.");
        }

        if (result.SuspectPages.Count > 0)
        {
            console.MarkupLine($"[yellow]{result.SuspectPages.Count} page(s) were already damaged in the live database at capture; they are restored as "
                               + "they were. Run `typhon check` on the restored database.[/]");
        }

        if (result.DamagedPages.Count > 0)
        {
            console.MarkupLine($"[yellow]{result.DamagedPages.Count} page(s) failed their check and were written anyway (--allow-damaged).[/]");
        }

        if (result.SettleError != null)
        {
            console.MarkupLine($"[red]The restored database did not open: {Markup.Escape(result.SettleError)}[/]");
        }

        console.MarkupLine("[grey]Its first open replays the WAL to the point and starts a new timeline.[/]");
        return exit;
    }
}

/// <summary>
/// <c>typhon backup status &lt;bundle&gt; | --store S [--stale-after T]</c> — backup health, cheap enough to poll; the exit code is the health for
/// monitoring (DL-22 e): Ok 0, Warning and Degraded 1, Failing, Off and unknown 3. A bundle's comes from its engine's <c>backup.status.json</c> (a
/// stopped engine's time rules judged again now, DL-23) and its store; a store's from its packs' headers and footers per chain, with
/// <c>--stale-after</c> for the stores of databases backed up offline (DL-33).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BackupStatusCommand : Command<BackupStatusCommand.Settings>
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class Settings : BackupSettings
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        [CommandArgument(0, "[bundle]")]
        [Description("A .typhon bundle: its running (or last) engine's backup status. Or give --store.")]
        public string Bundle { get; set; }

        [CommandOption("--stale-after")]
        [Description("With --store: Degraded when the newest restorable point is older than this (30d, 12h, 90m, 45s).")]
        public string StaleAfter { get; set; }
        // ReSharper restore UnusedAutoPropertyAccessor.Global
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
        => BackupCli.RejectLeftovers(context, settings) is var usage and >= 0 ? usage : Run(settings, AnsiConsole.Console, Console.Out, DateTime.UtcNow);

    /// <summary>A bundle's status: its engine's status file (judged again now) and its store.</summary>
    private static int BundleStatus(string bundle, BackupOutput output, DateTime utcNow)
    {
        BackupStatusReport report;
        try
        {
            report = BackupOperations.ReadStatus(OfflineBundlePageSource.ResolveBundleDirectory(bundle), utcNow);
        }
        catch (Exception ex) when (BackupCli.ExitCodeFor(ex) >= 0 || output.AsJson)
        {
            return output.Error(ex);
        }

        var exit = BackupCli.ExitCodeOf(report.Health);
        if (output.AsJson)
        {
            output.Json(new { exitCode = exit, report });
            return exit;
        }

        var colour = exit switch { BackupCli.ExitOk => "green", BackupCli.ExitWarning => "yellow", _ => "red" };
        output.Console.MarkupLine($"[{colour}]{report.Health}[/] — {Markup.Escape(report.Reason)}.");
        var s = report.Status;
        if (s != null)
        {
            output.Console.MarkupLine(report.EngineLive
                ? $"Engine PID {report.Pid} on {Markup.Escape(report.Machine)}, {s.State}{(s.State == BackupRunState.Running ? " (" + s.Phase + ")" : "")}, "
                  + $"store {Markup.Escape(report.StorePath)}{(report.AcceptsRequests ? ", takes requests" : "")}."
                : $"No engine runs on it (last: PID {report.Pid} on {Markup.Escape(report.Machine)}); store {Markup.Escape(report.StorePath)}.");
            output.Console.MarkupLine(s.LastPoint == null
                ? "No point of this database's timeline yet."
                : $"Newest point #{s.LastPoint.BackupId} ({s.LastPoint.Kind}), {BackupCli.Age(utcNow - s.LastSuccessUtc)} old; "
                  + $"{s.RestorablePoints} restorable point(s), {BackupCli.Bytes(s.StoreBytes)}; {s.ConsecutiveFailures} failure(s) in a row.");
            if (s.LastFailure != null)
            {
                output.Console.MarkupLine($"[grey]Last failure {s.LastFailure.Utc:u}: {Markup.Escape(s.LastFailure.Message)}[/]");
            }
        }

        return exit;
    }

    internal static int Run(Settings settings, IAnsiConsole console, TextWriter json, DateTime utcNow)
    {
        if (!BackupCli.TryResolveFormat(settings.Format, out var asJson, out var formatError))
        {
            return new BackupOutput(console, json, false).Error(BackupCli.ExitUsage, formatError);
        }

        var output = new BackupOutput(console, json, asJson);
        if (!string.IsNullOrWhiteSpace(settings.Bundle))
        {
            return string.IsNullOrWhiteSpace(settings.Store) && settings.StaleAfter == null
                ? BundleStatus(settings.Bundle, output, utcNow)
                : output.Error(BackupCli.ExitUsage, "Give a bundle or --store, not both (--stale-after goes with --store).");
        }

        if (string.IsNullOrWhiteSpace(settings.Store))
        {
            return output.Error(BackupCli.ExitUsage, "Give a bundle (its engine's backup status) or --store (a store's).");
        }

        var staleAfter = TimeSpan.Zero;
        if (settings.StaleAfter != null && !BackupCli.TryParseDuration(settings.StaleAfter, out staleAfter, out var staleError))
        {
            return output.Error(BackupCli.ExitUsage, staleError);
        }

        BackupStoreStatus status;
        BackupStoreListing listing;
        try
        {
            status = BackupOperations.Status(settings.Store);
            listing = BackupOperations.List(settings.Store);
        }
        catch (Exception ex) when (BackupCli.ExitCodeFor(ex) >= 0 || output.AsJson)
        {
            return output.Error(ex);
        }

        // DL-33: a store of an offline-backed database has no engine to judge staleness; --stale-after does.
        if (staleAfter > TimeSpan.Zero && status.Health is BackupHealth.Ok or BackupHealth.Warning && status.LatestRestorable != null
            && utcNow - status.LatestRestorable.TailEndUtc > staleAfter)
        {
            status = status with
            {
                Health = BackupHealth.Degraded,
                Reason = $"the newest restorable point #{status.LatestRestorable.BackupId} is older than {BackupCli.Age(staleAfter)}",
            };
        }

        var exit = BackupCli.ExitCodeOf(status.Health);
        var storeBytes = listing.Points.Sum(p => p.PackBytes);
        var chains = listing.Chains.Select(c =>
        {
            var head = listing.Points.LastOrDefault(p => p.BackupId == c.HeadBackupId);
            return new
            {
                c.ChainId, c.BaseId, c.HeadBackupId, c.Depth, c.HeadDepth,
                HeadPointLsn = head?.PointLsn, HeadAgeSeconds = head == null ? (double?)null : Math.Round((utcNow - head.TailEndUtc).TotalSeconds),
            };
        }).ToList();
        var latestAge = status.Latest == null ? (TimeSpan?)null : utcNow - status.Latest.TailEndUtc;

        if (output.AsJson)
        {
            output.Json(new
            {
                exitCode = exit, status.Health, status.Reason, status.Points, storeBytes, latest = status.Latest,
                latestAgeSeconds = latestAge.HasValue ? Math.Round(latestAge.Value.TotalSeconds) : (double?)null, latestRestorable = status.LatestRestorable,
                chains,
            });
            return exit;
        }

        var colour = exit switch { BackupCli.ExitOk => "green", BackupCli.ExitWarning => "yellow", _ => "red" };
        console.MarkupLine($"[{colour}]{status.Health}[/] — {Markup.Escape(status.Reason)}.");
        console.MarkupLine($"{status.Points} point(s), {BackupCli.Bytes(storeBytes)}.");
        if (status.Latest != null)
        {
            console.MarkupLine($"Newest point #{status.Latest.BackupId} ({status.Latest.Kind}), {BackupCli.Age(latestAge.Value)} old; newest restorable: "
                               + (status.LatestRestorable == null ? "[red]none[/]" : $"#{status.LatestRestorable.BackupId}") + ".");
        }

        if (chains.Count > 0)
        {
            var table = new Table().AddColumns("Chain", "Base", "Head", "Points", "Head depth", "Head P", "Head age");
            foreach (var c in chains)
            {
                table.AddRow(c.ChainId.ToString()[..8], $"#{c.BaseId}", $"#{c.HeadBackupId}", c.Depth.ToString(), c.HeadDepth.ToString(),
                    c.HeadPointLsn?.ToString() ?? "-",
                    c.HeadAgeSeconds.HasValue ? BackupCli.Age(TimeSpan.FromSeconds(c.HeadAgeSeconds.Value)) : "-");
            }

            console.Write(table);
        }

        return exit;
    }
}
