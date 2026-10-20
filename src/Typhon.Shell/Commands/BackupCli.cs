using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Typhon.Shell.Commands;

/// <summary>What every <c>typhon backup</c> verb takes.</summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class BackupSettings : CommandSettings
{
    // ReSharper disable UnusedAutoPropertyAccessor.Global
    [CommandOption("-s|--store")]
    [Description("The backup store directory.")]
    public string Store { get; set; }

    [CommandOption("-f|--format")]
    [Description("text | json. Default: text on a terminal, json when piped.")]
    public string Format { get; set; }
    // ReSharper restore UnusedAutoPropertyAccessor.Global
}

/// <summary>
/// What the <c>typhon backup</c> verbs share: the exit codes (07 §4, cron and CI ready, in the spirit of <c>typhon check</c>), the output format, the
/// point selector, and the one mapping from what the store throws to the code a script sees.
/// </summary>
internal static class BackupCli
{
    /// <summary>OK.</summary>
    internal const int ExitOk = 0;

    /// <summary>Done, but something needs attention: a suspect point, a newest point that is not restorable, temps left behind.</summary>
    internal const int ExitWarning = 1;

    /// <summary>The operation failed (I/O, an aborted capture), or verify found damage while some point is still restorable.</summary>
    internal const int ExitFailed = 2;

    /// <summary>The store is damaged: no restorable point for the requested target.</summary>
    internal const int ExitStoreDamaged = 3;

    /// <summary>Refused: the target exists or is locked, the store belongs to another database, a format revision does not match.</summary>
    internal const int ExitRefused = 4;

    /// <summary>The command line is wrong.</summary>
    internal const int ExitUsage = 64;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Resolves <c>--format</c>: what was asked for, else JSON when the output is not a terminal (as <c>typhon check</c> does).</summary>
    internal static bool TryResolveFormat(string format, out bool asJson, out string error)
    {
        error = null;
        asJson = CheckCommand.ResolveFormat(format, null);
        if (string.IsNullOrWhiteSpace(format) || format.Trim().Equals("json", StringComparison.OrdinalIgnoreCase)
            || format.Trim().Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        error = $"Unknown format '{format}'. Expected text or json.";
        return false;
    }

    /// <summary>Parses <c>--point</c>: <c>latest</c> (also the default) or a backup id, optionally written <c>#12</c>.</summary>
    internal static bool TryParsePoint(string value, out PointSelector point, out string error)
    {
        error = null;
        point = PointSelector.Latest;
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (uint.TryParse(value.Trim().TrimStart('#'), out var id) && id > 0)
        {
            point = PointSelector.ById(id);
            return true;
        }

        error = $"Unknown point '{value}'. Expected latest or a backup id.";
        return false;
    }

    /// <summary>
    /// The exit code for what an operation threw, or <c>-1</c> for an exception that is not an outcome but a defect. Verbs catch what maps
    /// (<c>catch … when (ExitCodeFor(ex) &gt;= 0 || AsJson)</c>): in text a defect reaches the shell's handler with its stack and whole chain; in JSON
    /// it is still a JSON error, exit 2, because the reader is a program.
    /// </summary>
    /// <remarks>
    /// Every exception the store throws maps: its typed ones; <see cref="NotSupportedException"/> when the file system does not enforce the store's
    /// lock (a refusal: the store will not run where it cannot exclude another writer); <see cref="InvalidOperationException"/> when it has no
    /// backup id left (and its misuse checks).
    /// </remarks>
    internal static int ExitCodeFor(Exception ex) => ex switch
    {
        RestoreRefusedException or DatabaseLockedException or BackupStoreMismatchException or BackupStoreLockedException
            or BackupFormatUnsupportedException or BackupPlanChangedException or NotSupportedException => ExitRefused,
        BackupChainBrokenException or BackupPackCorruptException => ExitStoreDamaged,
        OperationCanceledException or IOException or UnauthorizedAccessException or TyphonException or InvalidOperationException => ExitFailed,
        _ => -1,
    };

    /// <summary>
    /// Spectre parses leniently: an option no verb declares lands in <see cref="CommandContext.Remaining"/> instead of failing. For a verb whose exit
    /// code a script reads, a misspelt <c>--replace</c> must not run as a restore without it, so anything left over is a usage error (64).
    /// </summary>
    /// <returns><c>-1</c> when nothing is left over.</returns>
    internal static int RejectLeftovers(CommandContext context, BackupSettings settings)
    {
        var leftovers = context.Remaining.Parsed.Select(p => p.Key).Concat(context.Remaining.Raw).ToList();
        if (leftovers.Count == 0)
        {
            return -1;
        }

        return new BackupOutput(AnsiConsole.Console, System.Console.Out, CheckCommand.ResolveFormat(settings.Format, null))
            .Error(ExitUsage, $"Unknown option or argument: {string.Join(", ", leftovers)}.");
    }

    /// <summary>A duration: a whole number with a unit, <c>30d</c>, <c>12h</c>, <c>90m</c>, <c>45s</c>.</summary>
    internal static bool TryParseDuration(string value, out TimeSpan duration, out string error)
    {
        duration = TimeSpan.Zero;
        error = null;
        var v = value?.Trim() ?? "";
        if (v.Length >= 2 && long.TryParse(v.AsSpan(0, v.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            switch (char.ToLowerInvariant(v[^1]))
            {
                case 'd':
                    duration = TimeSpan.FromDays(n);
                    return true;
                case 'h':
                    duration = TimeSpan.FromHours(n);
                    return true;
                case 'm':
                    duration = TimeSpan.FromMinutes(n);
                    return true;
                case 's':
                    duration = TimeSpan.FromSeconds(n);
                    return true;
            }
        }

        error = $"Unknown duration '{value}'. Expected a whole number and a unit: 30d, 12h, 90m, 45s.";
        return false;
    }

    /// <summary>A size: bytes, or a number with KB/MB/GB/TB (powers of 1000) or KiB/MiB/GiB/TiB (powers of 1024).</summary>
    internal static bool TryParseBytes(string value, out long bytes, out string error)
    {
        bytes = 0;
        error = null;
        var v = value?.Trim() ?? "";
        var split = v.Length;
        while (split > 0 && char.IsAsciiLetter(v[split - 1]))
        {
            split--;
        }

        var unit = v[split..].ToUpperInvariant();
        long scale = unit switch
        {
            "" or "B" => 1,
            "KB" => 1000L,
            "MB" => 1000L * 1000,
            "GB" => 1000L * 1000 * 1000,
            "TB" => 1000L * 1000 * 1000 * 1000,
            "KIB" => 1L << 10,
            "MIB" => 1L << 20,
            "GIB" => 1L << 30,
            "TIB" => 1L << 40,
            _ => 0,
        };
        if (scale > 0 && decimal.TryParse(v.AsSpan(0, split), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) && n >= 0
            && n * scale <= long.MaxValue)
        {
            bytes = (long)(n * scale);
            return true;
        }

        error = $"Unknown size '{value}'. Expected bytes, or a number with KB, MB, GB, TB, KiB, MiB, GiB or TiB: 500GiB, 2TB.";
        return false;
    }

    /// <summary>A byte count for a person.</summary>
    internal static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MiB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB",
    };

    /// <summary>An age for a person.</summary>
    internal static string Age(TimeSpan age) => age.TotalSeconds switch
    {
        < 0 => "in the future",
        < 120 => $"{age.TotalSeconds:0} s",
        < 7200 => $"{age.TotalMinutes:0} min",
        < 172_800 => $"{age.TotalHours:0.#} h",
        _ => $"{age.TotalDays:0.#} d",
    };
}

/// <summary>Where a backup verb writes: a Spectre console for text, a writer for JSON. One verb, one shape per format.</summary>
internal sealed class BackupOutput(IAnsiConsole console, TextWriter json, bool asJson)
{
    public IAnsiConsole Console { get; } = console;

    public bool AsJson { get; } = asJson;

    /// <summary>Writes a JSON document.</summary>
    public void Json(object value) => json.WriteLine(JsonSerializer.Serialize(value, BackupCli.JsonOptions));

    /// <summary>Reports a failure in the chosen format and returns its exit code.</summary>
    public int Error(int exitCode, string message)
    {
        if (AsJson)
        {
            Json(new { error = new { exitCode, message } });
        }
        else
        {
            Console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        }

        return exitCode;
    }

    /// <summary>Reports what an operation threw (one <see cref="BackupCli.ExitCodeFor"/> maps) as its exit code.</summary>
    public int Error(Exception ex)
    {
        var message = ex.Message;
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
        {
            message += " Caused by: " + inner.Message;
        }

        var code = BackupCli.ExitCodeFor(ex);
        return Error(code >= 0 ? code : BackupCli.ExitFailed, message);
    }
}
