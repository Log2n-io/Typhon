using System.ComponentModel;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Cli;
using Typhon.Workbench.Hosting;

namespace Typhon.Shell.Commands;

/// <summary>
/// <c>typhon docs [path]</c> — opens the Typhon documentation site (<c>https://doc.typhondb.io/latest/</c>) in the
/// user's default browser. An optional <c>[path]</c> deep-links to a page under <c>/latest/</c>; <c>--print</c> emits
/// the URL instead of launching a browser (headless / SSH sessions). Reuses <see cref="BrowserLauncher"/>, the same
/// best-effort cross-platform launcher <c>typhon ui</c> uses, so a missing browser never crashes the command.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class DocsCommand : Command<DocsCommand.Settings>
{
    /// <summary>Root of the published documentation. <c>latest</c> always redirects to the current release's docs.</summary>
    internal const string DocsBaseUrl = "https://doc.typhondb.io/latest/";

    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class Settings : CommandSettings
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        [CommandArgument(0, "[path]")]
        [Description("Optional page under /latest/ to deep-link to, e.g. \"guide/getting-started\".")]
        public string Path { get; set; }

        [CommandOption("--print")]
        [Description("Print the URL instead of opening a browser (headless / SSH sessions).")]
        public bool Print { get; set; }
        // ReSharper restore UnusedAutoPropertyAccessor.Global
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var url = BuildUrl(settings.Path);

        if (settings.Print)
        {
            AnsiConsole.WriteLine(url);
            return 0;
        }

        if (BrowserLauncher.TryOpen(url))
        {
            AnsiConsole.MarkupLine($"[grey]Opened documentation: {Markup.Escape(url)}[/]");
            return 0;
        }

        // Headless / no default browser — surface the URL so the user can open it manually (never an error).
        AnsiConsole.MarkupLine($"[yellow]Could not open a browser.[/] Documentation: {Markup.Escape(url)}");
        return 0;
    }

    /// <summary>
    /// Builds the docs URL: <see cref="DocsBaseUrl"/> plus an optional page <paramref name="path"/> (its leading
    /// slash trimmed so the join never doubles the separator). A null/blank path yields the site root.
    /// </summary>
    internal static string BuildUrl(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DocsBaseUrl;
        }

        return DocsBaseUrl + path.Trim().TrimStart('/');
    }
}
