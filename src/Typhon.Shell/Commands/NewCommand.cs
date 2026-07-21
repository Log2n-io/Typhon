using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Typhon.Shell.Commands;

/// <summary>
/// <c>typhon new &lt;name&gt;</c> — scaffold a runnable Typhon starter project (the SWG Light sample + app template + a
/// pinned <c>Typhon</c> package reference + config-driven profiling). <c>cd &lt;name&gt; &amp;&amp; dotnet run</c> then builds
/// against the published package and writes a <c>.typhon-trace</c> with no manual edits (#532/F2).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class NewCommand : Command<NewCommand.Settings>
{
    // ReSharper disable once ClassNeverInstantiated.Global
    public sealed class Settings : CommandSettings
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        [CommandArgument(0, "<name>")]
        [Description("Name of the project to create (a directory of this name is created).")]
        public string Name { get; set; }

        [CommandOption("-o|--output <DIR>")]
        [Description("Parent directory to create the project in. Default: the current directory.")]
        public string Output { get; set; }

        [CommandOption("--force")]
        [Description("Scaffold even if the target directory already contains files (same-named files are overwritten).")]
        public bool Force { get; set; }
        // ReSharper restore UnusedAutoPropertyAccessor.Global
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var name = settings.Name?.Trim();
        if (!ProjectScaffolder.IsValidName(name))
        {
            AnsiConsole.MarkupLine("[red]Invalid project name.[/] Use letters, digits, '_', '.', or '-', starting with a letter or '_'.");
            return 1;
        }

        var parent = string.IsNullOrWhiteSpace(settings.Output)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(settings.Output);
        var targetDir = Path.Combine(parent, name);

        if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any() && !settings.Force)
        {
            AnsiConsole.MarkupLine($"[red]Directory already exists and is not empty:[/] {targetDir.EscapeMarkup()}");
            AnsiConsole.MarkupLine("Pick another name, or pass [yellow]--force[/] to scaffold into it anyway.");
            return 1;
        }

        ProjectScaffolder.Emit(targetDir, name);

        AnsiConsole.MarkupLine($"[green]Created[/] Typhon starter [bold]{name.EscapeMarkup()}[/] in {targetDir.EscapeMarkup()}");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Next steps:");
        AnsiConsole.MarkupLine($"  [grey]cd[/] {name.EscapeMarkup()}");
        AnsiConsole.MarkupLine("  [grey]dotnet run[/]                 # spawns drones, ticks the runtime, writes ./captures/*.typhon-trace");
        AnsiConsole.MarkupLine("  [grey]typhon ui --open-latest[/]    # open the trace in the Workbench");
        return 0;
    }
}
