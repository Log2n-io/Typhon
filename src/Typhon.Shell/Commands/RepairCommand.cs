using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using Typhon.Engine;

namespace Typhon.Shell.Commands;

/// <summary>
/// <c>typhon repair &lt;bundle&gt;</c> — produces a reviewable repair plan, and applies one.
/// </summary>
/// <remarks>
/// Two steps, deliberately. The natural product instinct is one command that does the right thing; that is rejected here
/// because the cost of a wrong automatic repair is unbounded and unrecoverable while the cost of one extra command is
/// thirty seconds. <c>--plan</c> writes a file and mutates nothing; <c>--apply</c> re-scans first and refuses if the
/// database changed, because repairing against a stale diagnosis is how a repair tool damages a healthy database.
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class RepairCommand : Command<RepairCommand.Settings>
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class Settings : CommandSettings
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        [CommandArgument(0, "<bundle>")]
        [Description("Path to the .typhon bundle directory.")]
        public string Bundle { get; set; }

        [CommandOption("--plan")]
        [Description("Produce a plan and a loss manifest. Mutates nothing. This is the default.")]
        public bool PlanOnly { get; set; }

        [CommandOption("--apply")]
        [Description("Execute the plan. The only mutating mode.")]
        public bool Apply { get; set; }

        [CommandOption("--allow-loss")]
        [Description("Consent to steps that destroy data that is already unreadable.")]
        public bool AllowLoss { get; set; }

        [CommandOption("--no-backup-first")]
        [Description("Do not copy the bundle before the first mutation. A copy is taken by default.")]
        public bool NoBackupFirst { get; set; }

        [CommandOption("--dry-run")]
        [Description("With --apply: describe every step and execute none.")]
        public bool DryRun { get; set; }

        [CommandOption("-o|--out")]
        [Description("Write the plan to this file instead of standard output.")]
        public string Out { get; set; }
        // ReSharper restore UnusedAutoPropertyAccessor.Global
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        IntegrityReport report;
        try
        {
            using var source = new OfflineBundlePageSource(settings.Bundle);
            if (source.LockHeld)
            {
                AnsiConsole.MarkupLine(
                    "[red]This database is open in another process.[/] Repair needs exclusive access — close it and retry.");
                return CheckCommand.ScanFailedExitCode;
            }

            report = IntegrityScanner.Scan(source, new IntegrityOptions { Depth = ScanDepth.Deep, Cancellation = cancellationToken });
        }
        catch (Exception ex) when (ex is IOException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]Could not read the bundle: {Markup.Escape(ex.Message)}[/]");
            return CheckCommand.ScanFailedExitCode;
        }

        var plan = DatabaseRepair.Plan(report);

        if (!settings.Apply)
        {
            var rendered = RenderPlan(plan, report);
            if (settings.Out != null)
            {
                File.WriteAllText(settings.Out, rendered);
                AnsiConsole.MarkupLine($"[grey]Plan written to {Markup.Escape(settings.Out)}.[/]");
            }
            else
            {
                Console.Out.Write(rendered);
            }

            return report.ExitCode;
        }

        if (plan.IsEmpty)
        {
            AnsiConsole.MarkupLine("[green]Nothing to repair.[/]");
            return report.ExitCode;
        }

        RepairOutcome outcome;
        try
        {
            outcome = DatabaseRepair.Apply(settings.Bundle, plan, settings.AllowLoss, !settings.NoBackupFirst, settings.DryRun);
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Refused: {Markup.Escape(ex.Message)}[/]");
            return CheckCommand.ScanFailedExitCode;
        }

        Console.Out.Write(RenderOutcome(outcome));
        return outcome.VerificationReport?.ExitCode ?? (outcome.Succeeded ? 0 : CheckCommand.ScanFailedExitCode);
    }

    /// <summary>Renders a plan for an operator to read before consenting.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="report">The report it was derived from.</param>
    internal static string RenderPlan(RepairPlan plan, IntegrityReport report)
    {
        var sb = new StringBuilder(2048);
        sb.Append("\n  REPAIR PLAN for ").Append(plan.Source).Append('\n');
        sb.Append("  diagnosis: ").Append(plan.Verdict).Append(", ").Append(report.Findings.Count).Append(" finding(s)\n");
        sb.Append("  fingerprint: ").Append(plan.DatabaseFingerprint).Append('\n').Append('\n');

        if (plan.IsEmpty)
        {
            sb.Append("  No repairable problems were found.\n");
        }

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            sb.Append("  ").Append(step.Order).Append(". [").Append(step.Class).Append("] ").Append(step.Description).Append('\n');
            sb.Append("     ").Append(step.Rationale).Append('\n');
            sb.Append("     addresses: ").Append(string.Join(", ", step.Addresses)).Append('\n').Append('\n');
        }

        if (plan.Unaddressed.Count > 0)
        {
            sb.Append("  NOT ADDRESSED BY THIS PLAN\n");
            for (var i = 0; i < plan.Unaddressed.Count; i++)
            {
                sb.Append("    · ").Append(plan.Unaddressed[i]).Append('\n');
            }

            sb.Append('\n');
        }

        if (!plan.Loss.IsEmpty)
        {
            sb.Append("  LOSS IF APPLIED\n");
            for (var i = 0; i < plan.Loss.Entries.Count; i++)
            {
                var loss = plan.Loss.Entries[i];
                sb.Append("    ").Append(loss.CountText).Append(' ').Append(loss.Kind).Append(" — ").Append(loss.Explanation).Append('\n');
            }

            sb.Append('\n');
        }

        if (!plan.IsEmpty)
        {
            sb.Append("  Next: typhon repair ").Append(plan.Source).Append(" --apply");
            sb.Append(plan.RequiresLossyConsent ? " --allow-loss\n" : "\n");
        }

        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Renders the receipt for an applied plan.</summary>
    /// <param name="outcome">The outcome.</param>
    internal static string RenderOutcome(RepairOutcome outcome)
    {
        var sb = new StringBuilder(2048);
        sb.Append("\n  REPAIR ").Append(outcome.Succeeded ? "COMPLETE" : "FAILED").Append('\n');

        if (outcome.BackupPath != null)
        {
            sb.Append("  pre-repair copy: ").Append(outcome.BackupPath).Append('\n');
        }

        sb.Append('\n');
        for (var i = 0; i < outcome.Results.Count; i++)
        {
            var r = outcome.Results[i];
            sb.Append("  ").Append(r.Step.Order).Append(". ").Append(r.Outcome.ToString().ToUpperInvariant()).Append(" — ").Append(r.Step.Description).Append('\n');
            sb.Append("     ").Append(r.Detail).Append('\n');
        }

        if (outcome.VerificationReport != null)
        {
            sb.Append('\n').Append("  VERIFICATION: ").Append(outcome.VerificationReport.Verdict);
            sb.Append(" (").Append(outcome.VerificationReport.Findings.Count).Append(" finding(s) remaining)\n");
        }

        sb.Append('\n');
        return sb.ToString();
    }
}
