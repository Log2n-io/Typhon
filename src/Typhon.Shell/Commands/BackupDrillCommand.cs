using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.ComponentModel;
using System.IO;
using System.Threading;

namespace Typhon.Shell.Commands;

/// <summary>
/// <c>typhon backup drill --store S --scratch D [--point X] [--keep-scratch]</c> — proves a point restores into a database (04 §6): restore into
/// <c>{scratch}/{guid}</c>, open (recovery runs) and close, check deep, record the result in the store, delete the scratch. Exit 0 passed, 1 passed with
/// warnings (leaks, suspect pages), 2 the drill could not run (scratch I/O, cancelled, an archetype this process cannot replay), 3 the point does not
/// restore, open or check sound, 4 refused (a scratch inside the store, a point of another format revision), 64 usage.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BackupDrillCommand : Command<BackupDrillCommand.Settings>
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class Settings : BackupSettings
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        [CommandOption("--scratch")]
        [Description("Where the point is restored for the drill (required; not inside the store; needs the space of the whole image).")]
        public string Scratch { get; set; }

        [CommandOption("-p|--point")]
        [Description("latest (default) or a backup id.")]
        public string Point { get; set; }

        [CommandOption("--keep-scratch")]
        [Description("Leave the restored bundle in the scratch directory.")]
        public bool KeepScratch { get; set; }
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
        if (string.IsNullOrWhiteSpace(settings.Store) || string.IsNullOrWhiteSpace(settings.Scratch))
        {
            return output.Error(BackupCli.ExitUsage, "--store and --scratch are required.");
        }

        if (!BackupCli.TryParsePoint(settings.Point, out var point, out var pointError))
        {
            return output.Error(BackupCli.ExitUsage, pointError);
        }

        BackupDrillReport report;
        try
        {
            report = BackupOperations.Drill(settings.Store, point, new BackupDrillOptions
            {
                ScratchDirectory = settings.Scratch,
                KeepScratch = settings.KeepScratch,
                Cancellation = ct,
            });
        }
        catch (Exception ex) when (BackupCli.ExitCodeFor(ex) >= 0 || output.AsJson)
        {
            return output.Error(ex);
        }

        var exit = ExitCodeOf(report.Outcome);
        if (output.AsJson)
        {
            var r = report.Recovery;
            output.Json(new
            {
                exitCode = exit,
                report = new
                {
                    report.Point, report.Outcome, report.FailedStage, report.Error, restoreMs = (long)report.RestoreTime.TotalMilliseconds,
                    openMs = (long)report.OpenTime.TotalMilliseconds, checkMs = (long)report.CheckTime.TotalMilliseconds,
                    recovery = new { r.SegmentsScanned, r.RecordsScanned, r.RecordsReplayed, r.UowsPromoted, r.UowsVoided },
                    report.Verdict, report.Findings, report.SuspectPages, report.RecordFile, report.ScratchBundle,
                },
            });
            return exit;
        }

        var times = $"restore {report.RestoreTime.TotalMilliseconds:0} ms, open {report.OpenTime.TotalMilliseconds:0} ms, "
                    + $"check {report.CheckTime.TotalMilliseconds:0} ms";
        var at = $"point [bold]#{report.Point.BackupId}[/] ({report.Point.Kind}, P={report.Point.PointLsn})";
        switch (report.Outcome)
        {
            case BackupDrillOutcome.Passed:
                console.MarkupLine($"[green]Drill passed[/] for {at}: {report.Recovery.RecordsReplayed} WAL record(s) replayed, {report.Verdict}; {times}.");
                break;
            case BackupDrillOutcome.PassedWithWarnings:
                console.MarkupLine($"[yellow]Drill passed with warnings[/] for {at}: {report.Verdict}, {report.Findings} finding(s), "
                                   + $"{report.SuspectPages} suspect page(s); {times}.");
                break;
            case BackupDrillOutcome.Failed:
                console.MarkupLine($"[red]Drill FAILED[/] for {at} at {report.FailedStage}: {Markup.Escape(report.Error ?? "")}");
                console.MarkupLine("[red]This backup is not trustworthy.[/] Keep this report, verify every kept point (`typhon backup verify --level deep`).");
                break;
            default:
                console.MarkupLine($"[yellow]The drill could not run[/] for {at} ({report.FailedStage}): {Markup.Escape(report.Error ?? "")}");
                break;
        }

        if (report.ScratchBundle != null)
        {
            console.MarkupLine($"[grey]The restored bundle is kept at {Markup.Escape(report.ScratchBundle)}.[/]");
        }

        console.MarkupLine(report.RecordFile == null
            ? "[yellow]The drill's record was not written to the store.[/]"
            : $"[grey]Recorded: {Markup.Escape(report.RecordFile)}[/]");
        return exit;
    }

    internal static int ExitCodeOf(BackupDrillOutcome outcome) => outcome switch
    {
        BackupDrillOutcome.Passed => BackupCli.ExitOk,
        BackupDrillOutcome.PassedWithWarnings => BackupCli.ExitWarning,
        BackupDrillOutcome.Failed => BackupCli.ExitStoreDamaged,
        _ => BackupCli.ExitFailed,
    };
}
