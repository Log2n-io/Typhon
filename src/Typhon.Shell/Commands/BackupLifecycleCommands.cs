using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Typhon.Shell.Commands;

/// <summary>
/// <c>typhon backup compact --store S [--point X]</c> — turns a point into a base inside the store (05 §2): the points after it rest on it, the packs before
/// it can go. Never touches the database, never deletes. Exit 0 compacted (or already a base), 2 failed (I/O, the compacted pack failed its verify),
/// 3 the point's chain is broken or a pack of it damaged, 4 the store is locked or another database's, 64 usage.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BackupCompactCommand : Command<BackupCompactCommand.Settings>
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class Settings : BackupSettings
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        [CommandOption("-p|--point")]
        [Description("latest (default) or a backup id.")]
        public string Point { get; set; }
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
            return output.Error(BackupCli.ExitUsage, "--store is required.");
        }

        if (!BackupCli.TryParsePoint(settings.Point, out var point, out var pointError))
        {
            return output.Error(BackupCli.ExitUsage, pointError);
        }

        BackupCompactionResult result;
        try
        {
            result = BackupOperations.Compact(settings.Store, point, new BackupLifecycleOptions { Cancellation = ct });
        }
        catch (BackupPackCorruptException ex) when (ex.Section == "Compaction")
        {
            // The pack this command wrote failed its verify: the operation failed, the store is as it was.
            return output.Error(BackupCli.ExitFailed, ex.Message);
        }
        catch (Exception ex) when (BackupCli.ExitCodeFor(ex) >= 0 || output.AsJson)
        {
            return output.Error(ex);
        }

        if (output.AsJson)
        {
            output.Json(new { exitCode = BackupCli.ExitOk, result });
            return BackupCli.ExitOk;
        }

        if (result.AlreadyBase)
        {
            console.MarkupLine($"Point [bold]#{result.Point.BackupId}[/] is already a base ({result.Point.Kind}): nothing to do.");
            return BackupCli.ExitOk;
        }

        console.MarkupLine($"[green]Compacted[/] point [bold]#{result.Point.BackupId}[/] from a chain of {result.ChainDepth} pack(s): {result.Pages} page(s) "
                           + $"({result.HolePages} never written), {BackupCli.Bytes(result.BytesRead)} read, {BackupCli.Bytes(result.BytesWritten)} written in "
                           + $"{result.Duration.TotalMilliseconds:0} ms.");
        console.MarkupLine("[grey]Nothing was deleted: `typhon backup prune` removes the packs no kept point needs.[/]");
        return BackupCli.ExitOk;
    }
}

/// <summary>
/// <c>typhon backup prune --store S --min-keep N [--max-age D] [--max-count N] [--max-bytes B] [--compact-at D] [--keep-drilled] [--dry-run]</c> —
/// applies a retention policy (05 §3): every point it promises stays restorable; what no kept point needs goes, newest first. The plan is shown, then
/// applied only if the store still plans exactly that. Exit 0 applied (or nothing to do), 1 a limit could not be met without breaking MinKeep or
/// KeepLastDrilled, 2 failed, 3 a chain is damaged (it keeps everything), 4 the store is locked or the plan changed under it, 64 usage.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BackupPruneCommand : Command<BackupPruneCommand.Settings>
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class Settings : BackupSettings
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        [CommandOption("--min-keep")]
        [Description("Never keep fewer restorable points than this (required, at least 1).")]
        public int? MinKeep { get; set; }

        [CommandOption("--max-age")]
        [Description("Keep points no older than this: 30d, 12h, 90m, 45s.")]
        public string MaxAge { get; set; }

        [CommandOption("--max-count")]
        [Description("Keep at most this many restorable points.")]
        public int? MaxCount { get; set; }

        [CommandOption("--max-bytes")]
        [Description("Keep the points whose packs fit in this size: 500GiB, 2TB, 1048576.")]
        public string MaxBytes { get; set; }

        [CommandOption("--compact-at")]
        [Description("Compact a chain's head when a restore of it would read more packs than this (at least 2).")]
        public int? CompactAt { get; set; }

        [CommandOption("--keep-drilled")]
        [Description("Never delete the newest point whose drill passed, nor any point after it.")]
        public bool KeepDrilled { get; set; }

        [CommandOption("--dry-run")]
        [Description("Show the plan; change nothing.")]
        public bool DryRun { get; set; }
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
            return output.Error(BackupCli.ExitUsage, "--store is required.");
        }

        if (!TryPolicy(settings, out var policy, out var policyError))
        {
            return output.Error(BackupCli.ExitUsage, policyError);
        }

        BackupRetentionPlan plan;
        BackupRetentionResult result = null;
        try
        {
            plan = BackupOperations.PlanRetention(settings.Store, policy);
            if (!settings.DryRun)
            {
                // The preview is the approval: applied only if the store still plans exactly these deletions (else 7109, exit 4).
                result = BackupOperations.ApplyRetention(settings.Store, policy, plan, new BackupLifecycleOptions { Cancellation = ct });
                plan = result.Plan;
            }
        }
        catch (Exception ex) when (BackupCli.ExitCodeFor(ex) >= 0 || output.AsJson)
        {
            return output.Error(ex);
        }

        var exit = ExitCodeOf(plan.Blocked);
        if (output.AsJson)
        {
            output.Json(new { exitCode = exit, dryRun = settings.DryRun, plan, result });
            return exit;
        }

        var verb = settings.DryRun ? "Would keep" : "Kept";
        console.MarkupLine($"{verb} {plan.Kept.Count} point(s){(plan.CutoffId == 0 ? "" : $" from #{plan.CutoffId}")}; "
                           + $"{BackupCli.Bytes(plan.BytesBefore)} in the store, at most {BackupCli.Bytes(plan.BytesAfterAtMost)} after.");
        if (plan.Compactions.Count > 0)
        {
            var compacted = result == null ? "" : $" ({result.Compactions.Count} done)";
            console.MarkupLine($"{(settings.DryRun ? "Would compact" : "Compacted")} #{string.Join(", #", plan.Compactions)}{compacted}.");
        }

        var deleted = result?.Deleted ?? plan.PackDeletions;
        console.MarkupLine(deleted.Count == 0
            ? "Nothing to delete."
            : $"{(settings.DryRun ? "Would delete" : "Deleted")} {deleted.Count} pack(s): {Markup.Escape(string.Join(", ", deleted))}"
              + (result == null ? "." : $"; {BackupCli.Bytes(Math.Max(0, result.BytesFreed))} freed in {result.Duration.TotalMilliseconds:0} ms."));
        if (plan.Blocked != BackupRetentionBlock.None)
        {
            console.MarkupLine($"[{(exit == BackupCli.ExitStoreDamaged ? "red" : "yellow")}]{plan.Blocked}: {Markup.Escape(plan.BlockedReason)}.[/]");
        }

        return exit;
    }

    internal static int ExitCodeOf(BackupRetentionBlock blocked) => blocked switch
    {
        BackupRetentionBlock.None => BackupCli.ExitOk,
        BackupRetentionBlock.DamagedChain => BackupCli.ExitStoreDamaged,
        _ => BackupCli.ExitWarning,
    };

    /// <summary>The policy the options describe. Every limit is the operator's: none has a default (0 = no limit), and at least one is set.</summary>
    internal static bool TryPolicy(Settings settings, out BackupRetentionPolicy policy, out string error)
    {
        policy = null;
        if (settings.MinKeep is not { } minKeep)
        {
            error = "--min-keep is required: the fewest restorable points retention may leave.";
            return false;
        }

        var maxAge = TimeSpan.Zero;
        if (settings.MaxAge != null && !BackupCli.TryParseDuration(settings.MaxAge, out maxAge, out error))
        {
            return false;
        }

        long maxBytes = 0;
        if (settings.MaxBytes != null && !BackupCli.TryParseBytes(settings.MaxBytes, out maxBytes, out error))
        {
            return false;
        }

        policy = new BackupRetentionPolicy
        {
            MinKeep = minKeep,
            MaxAge = maxAge,
            MaxCount = settings.MaxCount ?? 0,
            MaxTotalBytes = maxBytes,
            CompactThreshold = settings.CompactAt ?? 0,
            KeepLastDrilled = settings.KeepDrilled,
        };
        if (!policy.Validate(out error))
        {
            error = error.Replace("MaxAge", "--max-age").Replace("MaxCount", "--max-count").Replace("MaxTotalBytes", "--max-bytes")
                .Replace("MinKeep", "--min-keep").Replace("CompactThreshold", "--compact-at");
            policy = null;
            return false;
        }

        return true;
    }
}
