using System.ComponentModel;
using System.IO;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Cli;
using Typhon.Workbench.Hosting;

namespace Typhon.Shell.Commands;

/// <summary>
/// <c>typhon ui [database]</c> — launches the Typhon Workbench in-process (Kestrel on loopback) and opens it in
/// the browser. Serves the pre-built SPA, so no Node is required at runtime (#429, decision D-6). The loopback +
/// bootstrap-token threat model is preserved: the token is handed to the browser via the launch-URL fragment
/// (see <see cref="WorkbenchHost"/>), never over the wire.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class UiCommand : Command<UiCommand.Settings>
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class Settings : CommandSettings
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        [CommandArgument(0, "[database]")]
        [Description("Optional .typhon database to open in the initial Workbench session.")]
        public string Database { get; set; }

        [CommandOption("--url <URL>")]
        [Description("Full loopback URL to bind (advanced). Default http://127.0.0.1:5200.")]
        public string Url { get; set; }

        [CommandOption("--port <PORT>")]
        [Description("Loopback port to bind. Default 5200. Ignored when --url is given.")]
        public int? Port { get; set; }

        [CommandOption("--no-browser")]
        [Description("Start the host without opening a browser (prints the URL to open manually).")]
        public bool NoBrowser { get; set; }
        // ReSharper restore UnusedAutoPropertyAccessor.Global
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var url = ResolveUrl(settings);

        // Resolve the optional database to an absolute path CLI-side so the SPA (which auto-opens it via the
        // launch fragment) is never dependent on the host process's working directory.
        string dbPath = null;
        if (!string.IsNullOrWhiteSpace(settings.Database))
        {
            dbPath = Path.GetFullPath(settings.Database);
        }

        var options = new WorkbenchHostOptions
        {
            Url = url,
            DbPath = dbPath,
            OpenBrowser = !settings.NoBrowser,
        };

        AnsiConsole.MarkupLine($"[grey]Starting Typhon Workbench at {Markup.Escape(url)} — press Ctrl+C to stop.[/]");

        // WorkbenchHost.Run blocks on the Kestrel host until shutdown (Ctrl+C), then returns the exit code.
        return WorkbenchHost.Run(options);
    }

    private static string ResolveUrl(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Url))
        {
            return settings.Url;
        }

        var port = settings.Port ?? 5200;
        return $"http://127.0.0.1:{port}";
    }
}
