using System.Text.Json;
using System.Text.Json.Serialization;
using Typhon.Workbench.Dtos.Backups;

namespace Typhon.Workbench.Backups;

/// <summary>
/// The Workbench's drill-only mode (#1417 WB-13): <c>--backup-drill --store S --point latest|N --scratch D [--schema dll]…</c>, handled before any web host
/// is built (by the Workbench's <c>Program</c> and by <c>typhon</c>'s, which hosts the Workbench under <c>typhon ui</c>). It runs
/// <see cref="BackupOperations.Drill"/> in a process of its own — the drill's engine never shares the Workbench's process-global archetype registry with a
/// live session — and reports on stdout as JSON lines, each prefixed with <see cref="LinePrefix"/> so an engine log written to the console is never taken
/// for one.
/// </summary>
/// <remarks>
/// Lines: <c>{"event":"progress","stage":…,"done":…,"total":…}</c>, then exactly one of <c>{"event":"report","report":{…}}</c> (exit 0) or
/// <c>{"event":"error","category":…,"message":…}</c> (exit 2); a usage error exits 64. With <c>--schema</c> the restored point is settled through the
/// Workbench's own open with those assemblies, so a point whose WAL tail holds the application's records replays; without, through the engine's schemaless
/// settle, which reports such a point as an <c>Error</c> drill (7012), not a failed one.
/// </remarks>
public static class BackupDrillChild
{
    public const string Switch = "--backup-drill";

    public const string LinePrefix = "@@typhon-drill ";

    public const int ExitReport = 0;
    public const int ExitError = 2;
    public const int ExitUsage = 64;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One protocol line.</summary>
    internal sealed record Line(string Event, string Stage, long Done, long Total, BackupDrillReportDto Report, string Category, string Message);

    /// <summary>Runs the drill mode when <paramref name="args"/> asks for it; null otherwise (the caller goes on with its own command line).</summary>
    public static int? TryRun(string[] args, TextWriter output = null)
    {
        if (args is not { Length: > 0 } || args[0] != Switch)
        {
            return null;
        }

        output ??= Console.Out;
        string store = null, point = "latest", scratch = null;
        var schema = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--store": store = value; i++; break;
                case "--point": point = value; i++; break;
                case "--scratch": scratch = value; i++; break;
                case "--schema": schema.Add(value); i++; break;
                default:
                    Write(output, new Line("error", null, 0, 0, null, "failed", $"Unknown drill argument '{args[i]}'."));
                    return ExitUsage;
            }
        }

        if (string.IsNullOrWhiteSpace(store) || string.IsNullOrWhiteSpace(scratch) || !TryParsePoint(point, out var selector))
        {
            Write(output, new Line("error", null, 0, 0, null, "failed", "Usage: --backup-drill --store S --point latest|N --scratch D [--schema dll]…"));
            return ExitUsage;
        }

        try
        {
            var schemaPaths = schema.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            var needsSchema = false;
            var report = BackupOperations.Drill(store, selector, new BackupDrillOptions
            {
                ScratchDirectory = scratch,
                Progress = (stage, done, total) => Write(output, new Line("progress", stage.ToString(), done, total, null, null, null)),
                OpenAndClose = bundle =>
                {
                    try
                    {
                        return BackupSettle.OpenAndClose(bundle, schemaPaths);
                    }
                    catch (Exception e) when (BackupSettle.NeedsSchema(e))
                    {
                        needsSchema = true;
                        throw;
                    }
                },
            });
            Write(output, new Line("report", null, 0, 0, BackupDtoMapper.Map(report, needsSchema), null, null));
            return ExitReport;
        }
        catch (Exception e)
        {
            Write(output, new Line("error", null, 0, 0, null, BackupDtoMapper.Category(e), e.Message));
            return ExitError;
        }
    }

    internal static bool TryParsePoint(string value, out PointSelector point)
    {
        point = PointSelector.Latest;
        if (string.IsNullOrWhiteSpace(value) || value == "latest")
        {
            return true;
        }

        if (uint.TryParse(value, out var id) && id > 0)
        {
            point = PointSelector.ById(id);
            return true;
        }

        return false;
    }

    private static void Write(TextWriter output, Line line)
    {
        // One write per line, flushed: the parent reads them as they come.
        output.Write(LinePrefix + JsonSerializer.Serialize(line, Json) + "\n");
        output.Flush();
    }

    /// <summary>Parses a protocol line; null for any other line (an engine log, a blank one).</summary>
    internal static Line Parse(string text)
    {
        if (text == null || !text.StartsWith(LinePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Line>(text.AsSpan(LinePrefix.Length), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
