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

internal sealed class TelemetryTraceSettings : TelemetryFileSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Profiler trace output file (e.g. captures/app.typhon-trace). Declaring it activates profiling.")]
    public string TracePath { get; set; }

    [CommandOption("--clear")]
    [Description("Remove the trace output path (stop writing a trace file).")]
    public bool Clear { get; set; }
}

/// <summary><c>typhon telemetry trace &lt;path&gt;</c> — set (or <c>--clear</c>) the <c>Typhon:Profiler:Trace</c> output
/// path, preserving the gate flags. Unlike the flag verbs, the argument is a file path, not a catalog flag path.</summary>
internal sealed class TelemetryTraceCommand : Command<TelemetryTraceSettings>
{
    protected override int Execute(CommandContext context, TelemetryTraceSettings settings, CancellationToken cancellationToken)
    {
        var model = TelemetryFile.Load(TelemetryCommandSupport.FilePath(settings.File));

        var path = settings.TracePath?.Trim();

        // Clear intent: the --clear flag, OR a bare verb like `trace clear` / `trace off`. Without this second case a
        // natural `typhon telemetry trace clear` would silently ARM a trace file literally named "clear" instead of
        // removing it — the exact footgun that made a cleared-looking config keep writing traces.
        if (settings.Clear || (path != null && IsClearWord(path)))
        {
            model.ClearTrace();
            var how = settings.Clear
                ? "cleared trace output"
                : $"cleared trace output (read '{Markup.Escape(path)}' as clear intent — to set a file with that name, give it an extension)";
            TelemetryCommandSupport.PrintSaved(model, null, how);
            return 0;
        }

        if (string.IsNullOrEmpty(path))
        {
            AnsiConsole.MarkupLine("[red]Give a trace file path[/], or use [yellow]--clear[/] to remove it.");
            AnsiConsole.MarkupLine("  [grey]typhon telemetry trace captures/app.typhon-trace[/]");
            return 1;
        }

        model.SetTrace(path);
        TelemetryCommandSupport.PrintSaved(model, path, $"trace → {Markup.Escape(path)}");
        return 0;
    }

    // Bare positional words that mean "remove the trace output", so `trace clear`/`trace off` behave like `--clear`
    // rather than arming a trace file literally named that.
    private static readonly string[] ClearWords = ["clear", "off", "none", "disable", "disabled", "remove", "false"];

    private static bool IsClearWord(string s) => Array.Exists(ClearWords, w => string.Equals(w, s, StringComparison.OrdinalIgnoreCase));
}

internal sealed class TelemetryLiveSettings : TelemetryFileSettings
{
    [CommandArgument(0, "[port]")]
    [Description("TCP port for live attach (e.g. 9100), or 'off' to remove it.")]
    public string Port { get; set; }

    [CommandOption("--wait <MS>")]
    [Description("Block startup up to this many ms waiting for the first viewer to connect.")]
    public int? WaitMs { get; set; }

    [CommandOption("--clear")]
    [Description("Remove the live channel.")]
    public bool Clear { get; set; }
}

/// <summary>
/// <c>typhon telemetry live &lt;port&gt;</c> — the live TCP channel (<c>Typhon:Profiler:Live</c>), sibling of
/// <see cref="TelemetryTraceCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// A separate verb rather than a node in <c>telemetry edit</c> on purpose: that editor is a tri-state tree over the
/// generated flag catalog, where every node is inherit / on / off. A port is none of those, and special-casing a typed
/// field into it would trade a uniform control over 200-odd flags for a form.
/// </para>
/// <para>
/// This exists because the port stopped being a private launch detail: the engine publishes it in the database's
/// <c>db.lock</c>, so a Workbench opening a bundle its application holds can discover where to watch. A value that two
/// processes agree on belongs in the tool that writes the file, not in hand-edited JSON.
/// </para>
/// </remarks>
internal sealed class TelemetryLiveCommand : Command<TelemetryLiveSettings>
{
    protected override int Execute(CommandContext context, TelemetryLiveSettings settings, CancellationToken cancellationToken)
    {
        var model = TelemetryFile.Load(TelemetryCommandSupport.FilePath(settings.File));
        var port = settings.Port?.Trim();

        // Same footgun as `trace`: a bare `live off` must remove the channel, not fail to parse and leave the previous
        // port armed. Handled here rather than left to int.TryParse so the two verbs behave identically.
        if (settings.Clear || (port != null && IsClearWord(port)))
        {
            model.ClearLive();
            TelemetryCommandSupport.PrintSaved(model, null, "cleared live attach");
            return 0;
        }

        if (string.IsNullOrEmpty(port))
        {
            // No argument reads as "show me", not as an error: it is the question someone types first.
            if (model.LivePort is { } current)
            {
                var wait = model.LiveWaitMs is { } w ? $", waits up to {w} ms for a viewer" : "";
                AnsiConsole.MarkupLine($"[green]live attach on port {current}[/][grey]{Markup.Escape(wait)}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[grey]live attach not configured[/]");
                AnsiConsole.MarkupLine("  [grey]typhon telemetry live 9100[/]");
            }
            return 0;
        }

        if (!int.TryParse(port, out var parsed) || parsed < 1 || parsed > 65535)
        {
            AnsiConsole.MarkupLine($"[red]Not a TCP port:[/] {Markup.Escape(port)} [grey](expected 1-65535, or 'off')[/]");
            return 1;
        }

        if (settings.WaitMs is { } ms && ms < 0)
        {
            AnsiConsole.MarkupLine("[red]--wait must be zero or positive.[/]");
            return 1;
        }

        model.SetLive(parsed, settings.WaitMs);
        var suffix = settings.WaitMs is { } set ? $" (wait {set} ms)" : "";
        TelemetryCommandSupport.PrintSaved(model, null, $"live → port {parsed}{suffix}");
        return 0;
    }

    private static readonly string[] ClearWords = ["clear", "off", "none", "disable", "disabled", "remove", "false"];

    private static bool IsClearWord(string s) => Array.Exists(ClearWords, w => string.Equals(w, s, StringComparison.OrdinalIgnoreCase));
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

        // "What would actually emit" has two halves, and the flags are only one of them: events that fire with nowhere
        // to go emit nothing at all. Reporting the output channels here is what makes the answer complete — and a
        // profiler with flags on but no Trace and no Live is a specific, easy mistake worth naming out loud.
        ReportOutputChannels(model);

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

    private static void ReportOutputChannels(TelemetryFile model)
    {
        var hasTrace = !string.IsNullOrEmpty(model.TracePath);
        var hasLive = model.LivePort.HasValue;

        if (hasTrace)
        {
            AnsiConsole.MarkupLine($"[green]trace file[/] → {Markup.Escape(model.TracePath)}");
        }
        if (hasLive)
        {
            var wait = model.LiveWaitMs is { } w ? $" (waits up to {w} ms for a viewer)" : "";
            AnsiConsole.MarkupLine($"[green]live attach[/] → port {model.LivePort.Value}{Markup.Escape(wait)}");
        }
        if (!hasTrace && !hasLive)
        {
            AnsiConsole.MarkupLine("[yellow]No output channel[/] [grey]— no trace file and no live port, so nothing is recorded anywhere.[/]");
            AnsiConsole.MarkupLine("  [grey]typhon telemetry trace captures/app.typhon-trace   ·   typhon telemetry live 9100[/]");
        }
        AnsiConsole.WriteLine();
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
