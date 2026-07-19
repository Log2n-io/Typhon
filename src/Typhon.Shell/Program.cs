using System;
using System.IO;
using System.Reflection;
using System.Threading;
using PrettyPrompt;
using PrettyPrompt.Highlighting;
using Spectre.Console;
using Spectre.Console.Cli;
using Typhon.Shell.Commands;
using Typhon.Shell.Parsing;
using Typhon.Shell.Session;

namespace Typhon.Shell;

internal static class Program
{
    /// <summary>
    /// The tool version, single-sourced from the assembly's informational version (stamped by MinVer at build
    /// time, e.g. <c>0.0.0-alpha.0.114</c>). The trailing <c>+&lt;sha&gt;</c> build metadata is trimmed for display.
    /// Surfaced by <c>typhon --version</c> and the interactive banner so both always reflect the real package version.
    /// </summary>
    internal static readonly string Version = ResolveVersion();

    private static string ResolveVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }

        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational.Substring(0, plus) : informational;
    }

    private static int Main(string[] args)
    {
        try
        {
            var app = new CommandApp<TSHCommand>();
            app.Configure(config =>
            {
                config.SetApplicationName("typhon");
                config.SetApplicationVersion(Version);

                // `typhon ui [database]` launches the Workbench UI (#429). The REPL remains the default command,
                // so `typhon`, `typhon <db>`, `typhon -c …` are unchanged; only the literal `ui` verb branches here.
                config.AddCommand<UiCommand>("ui")
                    .WithDescription("Launch the Typhon Workbench UI in your browser.")
                    .WithExample(["ui"])
                    .WithExample(["ui", "mydb.typhon"]);

                // `typhon telemetry …` authors typhon.telemetry.json in the working directory (#522). Scriptable
                // verbs over the source-generated flag catalog; the interactive tree lives under `edit`.
                config.AddBranch("telemetry", tel =>
                {
                    tel.SetDescription("Create/update typhon.telemetry.json (telemetry gate flags).");
                    tel.AddCommand<TelemetryListCommand>("list").WithDescription("List flags with default / explicit / effective state.");
                    tel.AddCommand<TelemetryEnableCommand>("enable").WithDescription("Set a flag (or subtree root) explicitly on.");
                    tel.AddCommand<TelemetryDisableCommand>("disable").WithDescription("Set a flag explicitly off.");
                    tel.AddCommand<TelemetryResetCommand>("reset").WithDescription("Remove an explicit flag (back to inherit).");
                    tel.AddCommand<TelemetryEffectiveCommand>("effective").WithDescription("Show what would actually emit.");
                    tel.AddCommand<TelemetryPresetCommand>("preset").WithDescription("Apply a curated preset bundle.");
                    tel.AddCommand<TelemetryEditCommand>("edit").WithDescription("Interactive tri-state tree editor (full-screen).");
                });
            });

            return app.Run(args);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Fatal: {Markup.Escape(ex.Message)}[/]");
#if DEBUG
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.GetType().FullName)}[/]");
            Console.WriteLine(ex.StackTrace);
            if (ex.InnerException != null)
            {
                AnsiConsole.MarkupLine($"[red]Inner: {Markup.Escape(ex.InnerException.Message)}[/]");
                Console.WriteLine(ex.InnerException.StackTrace);
            }
#endif
            return 10;
        }
    }
}

/// <summary>
/// The single Spectre.Console.Cli command that handles all `typhon` invocation modes:
/// interactive REPL, single-command (-c), script (--exec), and pipe mode.
/// </summary>
internal sealed class TSHCommand : Command<TSHCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[database]")]
        public string Database { get; set; }

        [CommandOption("-s|--schema")]
        public string[] Schema { get; set; }

        [CommandOption("-e|--exec")]
        public string ExecScript { get; set; }

        [CommandOption("-c")]
        public string SingleCommand { get; set; }

        [CommandOption("-f|--format")]
        public string Format { get; set; }

        [CommandOption("-l|--log-level")]
        public string LogLevel { get; set; }

        [CommandOption("--nowal")]
        public bool NoWal { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var session = new ShellSession();

        // Apply format if specified
        if (!string.IsNullOrEmpty(settings.Format))
        {
            session.Format = settings.Format;
        }

        // Apply log level if specified
        if (!string.IsNullOrEmpty(settings.LogLevel) && Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(settings.LogLevel, true, out var logLevel))
        {
            session.LogLevel = logLevel;
        }

        // Apply nowal flag
        session.NoWal = settings.NoWal;

        var executor = new CommandExecutor(session);

        // Pre-load schema assemblies
        if (settings.Schema != null)
        {
            foreach (var schemaPath in settings.Schema)
            {
                // Escape backslashes so the tokenizer doesn't treat them as C-style escapes
                var escaped = schemaPath.Replace("\\", "\\\\");
                var result = executor.Execute($"load-schema \"{escaped}\"");
                WriteResult(result);

                if (!result.Success)
                {
                    return 1;
                }
            }
        }

        // Pre-open database
        if (!string.IsNullOrEmpty(settings.Database))
        {
            var escaped = settings.Database.Replace("\\", "\\\\");
            var result = executor.Execute($"open \"{escaped}\"");
            WriteResult(result);

            if (!result.Success)
            {
                return 2;
            }
        }

        // Execute .typhonrc files: global (~/.typhonrc) first, then local (./.typhonrc)
        var rcFiles = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".typhonrc"),
            Path.Combine(Directory.GetCurrentDirectory(), ".typhonrc"),
        };

        foreach (var rcFile in rcFiles)
        {
            if (File.Exists(rcFile))
            {
                AnsiConsole.MarkupLine($"[grey]Loading {Markup.Escape(rcFile)}[/]");
                var parser = new ScriptParser(session, executor);
                var (success, error) = parser.ExecuteFile(rcFile);

                if (!success)
                {
                    AnsiConsole.MarkupLine($"[yellow]Warning: .tshrc failed: {Markup.Escape(error)}[/]");
                    AnsiConsole.MarkupLine("[yellow](continuing anyway)[/]");
                }
            }
        }

        // Mode precedence: -c → --exec → pipe → interactive
        if (!string.IsNullOrEmpty(settings.SingleCommand))
        {
            return ExecuteSingleCommand(session, executor, settings.SingleCommand);
        }

        if (!string.IsNullOrEmpty(settings.ExecScript))
        {
            return ExecuteScript(session, executor, settings.ExecScript);
        }

        if (!Console.IsInputRedirected)
        {
            return RunInteractive(session, executor);
        }

        return ExecutePipeMode(session, executor);
    }

    private static int ExecuteSingleCommand(ShellSession session, CommandExecutor executor, string command)
    {
        var result = executor.Execute(command);
        WriteResult(result);
        return result.Success ? 0 : 1;
    }

    private static int ExecuteScript(ShellSession session, CommandExecutor executor, string scriptPath)
    {
        var parser = new ScriptParser(session, executor);
        var (success, error) = parser.ExecuteFile(scriptPath);

        if (!success)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            AnsiConsole.MarkupLine("[red](script halted)[/]");
            return 1;
        }

        return 0;
    }

    private static int ExecutePipeMode(ShellSession session, CommandExecutor executor)
    {
        var lines = Console.In.ReadToEnd().Split('\n');
        var parser = new ScriptParser(session, executor);
        var (success, error) = parser.ExecuteLines(lines);

        if (!success)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            return 1;
        }

        return 0;
    }

    private static int RunInteractive(ShellSession session, CommandExecutor executor)
    {
        AnsiConsole.MarkupLine($"[grey]Typhon Shell v{Program.Version}[/]");
        AnsiConsole.MarkupLine("[grey]Type 'help' for commands, 'exit' to quit.[/]");
        Console.WriteLine();

        var callbacks = new TshPromptCallbacks(session);

        var historyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".typhon_history");

        var config = new PromptConfiguration(
            prompt: new FormattedString("typhon> ",
                new FormatSpan(0, 6, AnsiColor.BrightCyan))
        );

        var prompt = new Prompt(
            persistentHistoryFilepath: historyPath,
            callbacks: callbacks,
            configuration: config
        );

        try
        {
            while (true)
            {
                // Update prompt to reflect current session state (db name, transaction, dirty flag)
                var promptText = PromptBuilder.Build(session);
                config.Prompt = new FormattedString(promptText,
                    new FormatSpan(0, 6, AnsiColor.BrightCyan));

                var response = prompt.ReadLineAsync().GetAwaiter().GetResult();
                if (!response.IsSuccess)
                {
                    // Ctrl+C on empty line or Ctrl+D
                    continue;
                }

                var input = response.Text.Trim();
                if (string.IsNullOrEmpty(input))
                {
                    continue;
                }

                CommandResult result;
                try
                {
                    result = executor.Execute(input);
                }
                catch (Exception ex)
                {
                    WriteException(ex, session.Verbose);
                    continue;
                }

                try
                {
                    WriteResult(result);
                }
                catch (Exception ex)
                {
                    // Markup rendering failure — fall back to plain text
                    AnsiConsole.MarkupLine($"[red]Output rendering error: {Markup.Escape(ex.Message)}[/]");
                    if (session.Verbose)
                    {
                        Console.WriteLine(ex.StackTrace);
                    }

                    if (!string.IsNullOrEmpty(result.Output))
                    {
                        Console.WriteLine(result.Output.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                    }
                }

                if (result.ShouldExit)
                {
                    if (session.HasTransaction && session.IsDirty)
                    {
                        AnsiConsole.MarkupLine("[yellow]Warning: Active transaction with uncommitted changes.[/]");
                    }

                    break;
                }
            }
        }
        finally
        {
            prompt.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return 0;
    }

    private static void WriteResult(CommandResult result)
    {
        if (string.IsNullOrEmpty(result.Output))
        {
            return;
        }

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Output)}[/]");
        }
        else if (result.UseMarkup)
        {
            AnsiConsole.MarkupLine(result.Output);
        }
        else
        {
            // PrettyPrompt enables VT100 mode where \n is a pure LF (cursor stays in column).
            // Normalize to \r\n so plain-text output renders correctly.
            Console.WriteLine(result.Output.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        }
    }

    private static void WriteException(Exception ex, bool verbose)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.GetType().Name)}: {Markup.Escape(ex.Message)}[/]");

        if (!verbose)
        {
#if DEBUG
            // Always show stack trace in debug builds
            Console.WriteLine(ex.StackTrace);
#else
            AnsiConsole.MarkupLine("[dim]  (use 'set verbose on' for full stack trace)[/]");
#endif
            return;
        }

        Console.WriteLine(ex.StackTrace);
        var inner = ex.InnerException;
        while (inner != null)
        {
            AnsiConsole.MarkupLine($"[red]  Inner: {Markup.Escape(inner.GetType().Name)}: {Markup.Escape(inner.Message)}[/]");
            Console.WriteLine(inner.StackTrace);
            inner = inner.InnerException;
        }
    }
}
