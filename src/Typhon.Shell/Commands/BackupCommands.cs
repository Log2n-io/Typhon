using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using Typhon.Engine;

namespace Typhon.Shell.Commands;

/// <summary>
/// <c>typhon backup create &lt;bundle&gt; --store S</c> — backs up a closed database (an offline capture, 02 §8). A database a live engine holds is
/// refused (exit 4), pointing at <c>engine.Backup</c>: the CLI reaches a running engine only through <c>backup.request</c>, which arrives with #1416.
/// A holder that advertises it will yield (the Workbench, #621) is asked to release the database first.
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

        /// <summary>How long a yieldable holder gets to release the database: the engine's own handoff wait by default. Not on the command line.</summary>
        public TimeSpan HandoffTimeout { get; set; } = new PagedMMFOptions().LockHandoffTimeout;
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
        $"Database '{bundle}' is open in process {ex.OwnerPid} on {ex.OwnerMachine}. Back it up from inside the application with "
        + "engine.Backup.CreateAsync(store); the CLI will reach a running database through backup.request (#1416). Or stop the process and run this "
        + "again.";

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
        var table = new Table().AddColumns("Id", "Kind", "Chain", "Parent", "P", "Frozen (UTC)", "Pages", "Size", "Notes");
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
                p.FreezeUtc.ToString("yyyy-MM-dd HH:mm:ss"), p.PageCount.ToString(), BackupCli.Bytes(p.PackBytes), notes,
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
            console.MarkupLine($"Chain {c.ChainId.ToString()[..8]}: base #{c.BaseId}, head #{c.HeadBackupId}, {c.Depth} point(s).");
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
}

/// <summary>
/// <c>typhon backup verify --store S [--point X] [--level quick|full]</c> — checks packs and chains without restoring anything (04 §4). The verdict is the
/// exit code: 0 ok, 1 advisories, 2 damage with a restorable point left, 3 nothing restorable.
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
        [Description("quick | full. Default: quick.")]
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

        foreach (var s in report.SkippedChecks)
        {
            console.MarkupLine($"  [grey]Skipped: {Markup.Escape(s)}[/]");
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
                error = "--level deep (restore into scratch and run the integrity scan) arrives with the drill (#1416). Use quick or full.";
                return false;
            default:
                error = $"Unknown level '{value}'. Expected quick or full.";
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
/// <c>typhon backup status --store S</c> — the store's health from its packs' headers and footers and the chains they form, per chain, with the
/// newest point's age: cheap enough to poll. The exit code is the health (0 ok, 1 warning, 3 nothing restorable), for monitoring; <c>verify</c>
/// reads the rest. The engine's status file and its health thresholds arrive with #1416.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BackupStatusCommand : Command<BackupSettings>
{
    protected override int Execute(CommandContext context, BackupSettings settings, CancellationToken cancellationToken)
        => BackupCli.RejectLeftovers(context, settings) is var usage and >= 0 ? usage : Run(settings, AnsiConsole.Console, Console.Out, DateTime.UtcNow);

    internal static int Run(BackupSettings settings, IAnsiConsole console, TextWriter json, DateTime utcNow)
    {
        if (!BackupCli.TryResolveFormat(settings.Format, out var asJson, out var formatError))
        {
            return new BackupOutput(console, json, false).Error(BackupCli.ExitUsage, formatError);
        }

        var output = new BackupOutput(console, json, asJson);
        if (string.IsNullOrWhiteSpace(settings.Store))
        {
            return output.Error(BackupCli.ExitUsage, "--store is required (status of a bundle, from its engine's status file, arrives with #1416).");
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

        var exit = status.Health switch
        {
            BackupHealth.Ok => BackupCli.ExitOk,
            BackupHealth.Warning => BackupCli.ExitWarning,
            _ => BackupCli.ExitStoreDamaged,
        };
        var storeBytes = listing.Points.Sum(p => p.PackBytes);
        var chains = listing.Chains.Select(c =>
        {
            var head = listing.Points.LastOrDefault(p => p.BackupId == c.HeadBackupId);
            return new
            {
                c.ChainId, c.BaseId, c.HeadBackupId, c.Depth,
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
            var table = new Table().AddColumns("Chain", "Base", "Head", "Depth", "Head P", "Head age");
            foreach (var c in chains)
            {
                table.AddRow(c.ChainId.ToString()[..8], $"#{c.BaseId}", $"#{c.HeadBackupId}", c.Depth.ToString(), c.HeadPointLsn?.ToString() ?? "-",
                    c.HeadAgeSeconds.HasValue ? BackupCli.Age(TimeSpan.FromSeconds(c.HeadAgeSeconds.Value)) : "-");
            }

            console.Write(table);
        }

        return exit;
    }
}
