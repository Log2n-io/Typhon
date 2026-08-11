using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using Typhon.Engine;

namespace Typhon.Shell.Commands;

/// <summary>
/// <c>typhon check &lt;bundle&gt;</c> — reports what is wrong with a database without changing a byte of it.
/// </summary>
/// <remarks>
/// Reads the bundle as bytes with no engine, no lock and no log replay, so it is always safe to run — including on the
/// database that will not open, which is the case that most justifies having it. The verdict is carried in the exit code
/// so the command drops into a cron job or a CI gate without anyone parsing anything.
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class CheckCommand : Command<CheckCommand.Settings>
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class Settings : CommandSettings
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        [CommandArgument(0, "<bundle>")]
        [Description("Path to the .typhon bundle directory.")]
        public string Bundle { get; set; }

        [CommandOption("-d|--depth")]
        [Description("spine | quick | standard | deep. Default: standard.")]
        public string Depth { get; set; }

        [CommandOption("-f|--format")]
        [Description("text | json. Default: text on a terminal, json when piped.")]
        public string Format { get; set; }

        [CommandOption("-o|--out")]
        [Description("Write the report to a file instead of standard output.")]
        public string Out { get; set; }

        [CommandOption("--checks")]
        [Description("Only run checks whose code starts with one of these prefixes, e.g. CHK-IDX.")]
        public string[] Checks { get; set; }

        [CommandOption("--skip")]
        [Description("Skip checks whose code starts with one of these prefixes.")]
        public string[] Skip { get; set; }
        // ReSharper restore UnusedAutoPropertyAccessor.Global
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!TryParseDepth(settings.Depth, out var depth, out var depthError))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(depthError)}[/]");
            return ScanFailedExitCode;
        }

        IntegrityReport report;
        try
        {
            using var source = new OfflineBundlePageSource(settings.Bundle);
            report = IntegrityScanner.Scan(source, new IntegrityOptions
            {
                Depth = depth,
                IncludeChecks = settings.Checks ?? [],
                ExcludeChecks = settings.Skip ?? [],
                Cancellation = cancellationToken
            });
        }
        catch (Exception ex) when (ex is IOException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]Could not read the bundle: {Markup.Escape(ex.Message)}[/]");
            return ScanFailedExitCode;
        }

        var asJson = ResolveFormat(settings.Format, settings.Out);
        var text = asJson ? IntegrityReportJson.Render(report) : IntegrityReportText.Render(report, colour: settings.Out == null && !Console.IsOutputRedirected);

        if (settings.Out != null)
        {
            File.WriteAllText(settings.Out, text);
            AnsiConsole.MarkupLine($"[grey]Report written to {Markup.Escape(settings.Out)} — verdict {report.Verdict}.[/]");
        }
        else
        {
            Console.Out.Write(text);
        }

        return report.ExitCode;
    }

    /// <summary>Exit code for "the scan itself could not run", kept clear of every verdict code.</summary>
    internal const int ScanFailedExitCode = 64;

    /// <summary>Parses the depth option.</summary>
    /// <param name="value">The raw option value, or <c>null</c> for the default.</param>
    /// <param name="depth">Receives the parsed depth.</param>
    /// <param name="error">Receives a message when the value is not recognised.</param>
    internal static bool TryParseDepth(string value, out ScanDepth depth, out string error)
    {
        error = null;
        depth = ScanDepth.Standard;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "spine": depth = ScanDepth.Spine; return true;
            case "quick": depth = ScanDepth.Quick; return true;
            case "standard": depth = ScanDepth.Standard; return true;
            case "deep": depth = ScanDepth.Deep; return true;
            default:
                error = $"Unknown depth '{value}'. Expected spine, quick, standard or deep.";
                return false;
        }
    }

    /// <summary>
    /// Chooses the output format: what was asked for, else JSON when the output is going anywhere but a terminal, because
    /// the consumer of a redirected report is a program.
    /// </summary>
    /// <param name="format">The raw format option, or <c>null</c>.</param>
    /// <param name="outPath">The <c>--out</c> path, or <c>null</c>.</param>
    internal static bool ResolveFormat(string format, string outPath)
    {
        if (!string.IsNullOrWhiteSpace(format))
        {
            return format.Trim().Equals("json", StringComparison.OrdinalIgnoreCase);
        }

        return outPath != null || Console.IsOutputRedirected;
    }
}
