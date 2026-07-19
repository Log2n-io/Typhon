using System;
using System.ComponentModel;
using System.Threading;
using System.IO;
using System.Linq;
using Spectre.Console;
using Spectre.Console.Cli;
using Typhon.Engine;
using Typhon.Shell.Telemetry;

namespace Typhon.Shell.Commands;

/// <summary>
/// <c>typhon telemetry …</c> — author <c>typhon.telemetry.json</c> in the working directory without hand-editing
/// nested JSON. Scriptable verbs (list / enable / disable / reset / effective / preset) over the source-generated
/// <see cref="TelemetryFlagCatalog"/>. Feature #522 / T5.
/// </summary>
internal static class TelemetryCommandSupport
{
    public static string FilePath(string fileOption) =>
        !string.IsNullOrWhiteSpace(fileOption)
            ? Path.GetFullPath(fileOption)
            : Path.Combine(Directory.GetCurrentDirectory(), TelemetryFile.DefaultFileName);

    /// <summary>Resolve a user path, or print an error + near matches and return null.</summary>
    public static TelemetrySupport.Resolution? ResolveOrReport(string input)
    {
        var r = TelemetrySupport.Resolve(input);
        if (r.Ok)
        {
            return r;
        }
        AnsiConsole.MarkupLine($"[red]Unknown flag path:[/] {Markup.Escape(input ?? "")}");
        if (r.Suggestions is { Count: > 0 })
        {
            AnsiConsole.MarkupLine("[grey]Did you mean:[/]");
            foreach (var s in r.Suggestions)
            {
                AnsiConsole.MarkupLine("  [yellow]" + Markup.Escape(s) + "[/]");
            }
        }
        return null;
    }

    public static void PrintSaved(TelemetryFile model, string path, string action)
    {
        model.Save();
        AnsiConsole.MarkupLine($"[green]{action}[/] [grey]→ {Markup.Escape(model.Path)}[/]");
    }
}

internal class TelemetryFileSettings : CommandSettings
{
    [CommandOption("--file <FILE>")]
    [Description("Path to the telemetry config file (default: ./typhon.telemetry.json).")]
    public string File { get; set; }
}

internal sealed class TelemetryPathSettings : TelemetryFileSettings
{
    [CommandArgument(0, "<path>")]
    [Description("Flag path below the prefix, e.g. Concurrency:AccessControl:Contention (or 'profiler' for the master).")]
    public string Path { get; set; }
}

internal sealed class TelemetryListSettings : TelemetryFileSettings
{
    [CommandArgument(0, "[filter]")]
    [Description("Only show flags whose path contains this substring.")]
    public string Filter { get; set; }

    [CommandOption("--flat")]
    [Description("Flat listing with full paths (instead of an indented tree).")]
    public bool Flat { get; set; }
}

internal sealed class TelemetryListCommand : Command<TelemetryListSettings>
{
    protected override int Execute(CommandContext context, TelemetryListSettings settings, CancellationToken cancellationToken)
    {
        var file = TelemetryCommandSupport.FilePath(settings.File);
        var model = TelemetryFile.Load(file);
        var eff = model.ResolveEffective();
        var all = TelemetryFlagCatalog.All;
        bool flat = settings.Flat || !string.IsNullOrEmpty(settings.Filter);

        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(file)}{(File.Exists(file) ? "" : " (not created yet)")}[/]");
        AnsiConsole.MarkupLine("[grey]● effective on · ○ effective off · (on/off) explicit · (–) inherited[/]\n");

        for (int i = 0; i < all.Count; i++)
        {
            var d = all[i];
            if (!string.IsNullOrEmpty(settings.Filter) &&
                d.Path.IndexOf(settings.Filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }
            var dot = eff[i] ? "[green]●[/]" : "[grey]○[/]";
            var expl = model.TryGetExplicit(d.Path, out var ev) ? (ev ? "[green]on[/]" : "[red]off[/]") : "[grey]–[/]";
            var label = flat
                ? (d.Path.Length == 0 ? "Profiler" : d.Path)
                : new string(' ', d.Depth * 2) + (d.Path.Length == 0 ? "Profiler" : d.Name);
            AnsiConsole.MarkupLine($"{dot} {Markup.Escape(label)} ({expl})");
        }
        return 0;
    }
}

internal sealed class TelemetryEnableCommand : Command<TelemetryPathSettings>
{
    protected override int Execute(CommandContext context, TelemetryPathSettings settings, CancellationToken cancellationToken)
    {
        var r = TelemetryCommandSupport.ResolveOrReport(settings.Path);
        if (r is null)
        {
            return 1;
        }
        var model = TelemetryFile.Load(TelemetryCommandSupport.FilePath(settings.File));
        model.Set(r.Value.Path, true);
        TelemetryCommandSupport.PrintSaved(model, r.Value.Path, $"enabled {(r.Value.Path.Length == 0 ? "Profiler" : r.Value.Path)}");
        return 0;
    }
}

internal sealed class TelemetryDisableCommand : Command<TelemetryPathSettings>
{
    protected override int Execute(CommandContext context, TelemetryPathSettings settings, CancellationToken cancellationToken)
    {
        var r = TelemetryCommandSupport.ResolveOrReport(settings.Path);
        if (r is null)
        {
            return 1;
        }
        var model = TelemetryFile.Load(TelemetryCommandSupport.FilePath(settings.File));
        model.Set(r.Value.Path, false);
        TelemetryCommandSupport.PrintSaved(model, r.Value.Path, $"disabled {(r.Value.Path.Length == 0 ? "Profiler" : r.Value.Path)}");
        return 0;
    }
}

internal sealed class TelemetryResetCommand : Command<TelemetryPathSettings>
{
    protected override int Execute(CommandContext context, TelemetryPathSettings settings, CancellationToken cancellationToken)
    {
        var r = TelemetryCommandSupport.ResolveOrReport(settings.Path);
        if (r is null)
        {
            return 1;
        }
        var model = TelemetryFile.Load(TelemetryCommandSupport.FilePath(settings.File));
        model.Reset(r.Value.Path);
        TelemetryCommandSupport.PrintSaved(model, r.Value.Path, $"reset {(r.Value.Path.Length == 0 ? "Profiler" : r.Value.Path)} (inherits)");
        return 0;
    }
}

internal sealed class TelemetryEffectiveSettings : TelemetryFileSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Optional subtree to restrict the output to.")]
    public string Path { get; set; }
}

internal sealed class TelemetryEffectiveCommand : Command<TelemetryEffectiveSettings>
{
    protected override int Execute(CommandContext context, TelemetryEffectiveSettings settings, CancellationToken cancellationToken)
    {
        var model = TelemetryFile.Load(TelemetryCommandSupport.FilePath(settings.File));
        var eff = model.ResolveEffective();
        var all = TelemetryFlagCatalog.All;
        string scope = null;
        if (!string.IsNullOrWhiteSpace(settings.Path))
        {
            var r = TelemetryCommandSupport.ResolveOrReport(settings.Path);
            if (r is null)
            {
                return 1;
            }
            scope = r.Value.Path;
        }

        var on = Enumerable.Range(0, all.Count)
            .Where(i => eff[i] && all[i].Field != null)
            .Where(i => scope == null || all[i].Path == scope || all[i].Path.StartsWith(scope + ":", StringComparison.Ordinal) || scope.Length == 0)
            .Select(i => all[i].Path.Length == 0 ? "Profiler" : all[i].Path)
            .ToList();

        if (on.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No telemetry effectively enabled.[/]");
            return 0;
        }
        AnsiConsole.MarkupLine($"[green]{on.Count}[/] flag(s) effectively ON:");
        foreach (var p in on)
        {
            AnsiConsole.MarkupLine("  [green]" + Markup.Escape(p) + "[/]");
        }
        return 0;
    }
}

internal sealed class TelemetryPresetSettings : TelemetryFileSettings
{
    [CommandArgument(0, "[name]")]
    [Description("Preset bundle to apply (omit to list available presets).")]
    public string Name { get; set; }
}

internal sealed class TelemetryPresetCommand : Command<TelemetryPresetSettings>
{
    protected override int Execute(CommandContext context, TelemetryPresetSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            AnsiConsole.MarkupLine("[grey]Available presets:[/]");
            foreach (var kv in TelemetrySupport.Presets)
            {
                var targets = string.Join(", ", kv.Value.Select(p => p.Length == 0 ? "Profiler" : p));
                AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(kv.Key)}[/] [grey]→ {Markup.Escape(targets)}[/]");
            }
            return 0;
        }
        if (!TelemetrySupport.Presets.TryGetValue(settings.Name, out var paths))
        {
            AnsiConsole.MarkupLine($"[red]Unknown preset:[/] {Markup.Escape(settings.Name)}");
            AnsiConsole.MarkupLine("[grey]Run 'typhon telemetry preset' to list them.[/]");
            return 1;
        }
        var model = TelemetryFile.Load(TelemetryCommandSupport.FilePath(settings.File));
        foreach (var p in paths)
        {
            model.Set(p, true);
        }
        TelemetryCommandSupport.PrintSaved(model, settings.Name, $"applied preset '{settings.Name}'");
        return 0;
    }
}

internal sealed class TelemetryEditCommand : Command<TelemetryFileSettings>
{
    protected override int Execute(CommandContext context, TelemetryFileSettings settings, CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            AnsiConsole.MarkupLine("[red]`typhon telemetry edit` needs an interactive terminal.[/]");
            AnsiConsole.MarkupLine("[grey]Use list / enable / disable / reset / effective / preset for scripting.[/]");
            return 1;
        }
        var file = TelemetryCommandSupport.FilePath(settings.File);
        var model = TelemetryFile.Load(file);
        var saved = new TelemetryEditor(model).Run();
        AnsiConsole.MarkupLine(saved
            ? $"[green]saved[/] [grey]→ {Markup.Escape(file)}[/]"
            : "[grey]cancelled — no changes written[/]");
        return 0;
    }
}
