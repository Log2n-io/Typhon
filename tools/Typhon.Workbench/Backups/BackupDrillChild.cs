using System.Text.Json;
using System.Text.Json.Serialization;
using Typhon.Workbench.Dtos.Backups;

namespace Typhon.Workbench.Backups;

/// <summary>
/// The Workbench's child-process modes (#1417 WB-13, WB-10), handled before any web host is built (by the Workbench's <c>Program</c> and by
/// <c>typhon</c>'s, which hosts the Workbench under <c>typhon ui</c>): every engine the Backups view opens on a restored image runs in a process of its own,
/// so it never shares the Workbench's process-global archetype registry with a live session.
/// <list type="bullet">
/// <item><c>--backup-drill --store S --point latest|N --scratch D [--schema dll]…</c> runs <see cref="BackupOperations.Drill"/>.</item>
/// <item><c>--backup-settle --bundle B [--schema dll]…</c> opens and closes a restored bundle (<see cref="BackupSettle"/>).</item>
/// </list>
/// Both report on stdout as JSON lines, each prefixed with <see cref="LinePrefix"/> so an engine log written to the console is never taken for one.
/// </summary>
/// <remarks>
/// <para>Lines: <c>{"event":"progress","stage":…,"done":…,"total":…}</c> (drill), then exactly one of <c>{"event":"report","report":{…}}</c> (drill,
/// exit 0), <c>{"event":"settle","settle":{…}}</c> (settle, exit 0) or <c>{"event":"error","category":…,"message":…,"needsSchema":…}</c> (exit 2); a
/// usage error exits 64.</para>
/// <para>With <c>--schema</c> a restored point is settled through the Workbench's own open with those assemblies, so a point whose WAL tail holds the
/// application's records replays; without, through the persisted schema alone, which reports such a point as needing its schema (7012), not as
/// damaged.</para>
/// <para><c>--watch-stdin</c>: the child exits (<see cref="ExitOrphaned"/>) when its stdin reaches its end. The parent keeps the pipe open for the child's
/// whole life, so a Workbench that dies, however it dies, takes its child with it — a drill never goes on restoring a whole image for nobody.</para>
/// </remarks>
public static class BackupDrillChild
{
    public const string Switch = "--backup-drill";

    public const string SettleSwitch = "--backup-settle";

    public const string WatchStdin = "--watch-stdin";

    public const string LinePrefix = "@@typhon-drill ";

    public const int ExitReport = 0;
    public const int ExitError = 2;
    public const int ExitOrphaned = 3;
    public const int ExitUsage = 64;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One protocol line.</summary>
    internal sealed record Line(string Event, string Stage, long Done, long Total, BackupDrillReportDto Report, string Category, string Message,
        BackupSettleReport Settle = null, bool NeedsSchema = false);

    /// <summary>
    /// Runs a child mode when <paramref name="args"/> asks for one; null otherwise (the caller goes on with its own command line).
    /// <paramref name="output"/> null = the console (the real child); a test passes a writer and is never watched.
    /// </summary>
    public static int? TryRun(string[] args, TextWriter output = null)
    {
        if (args is not { Length: > 0 } || (args[0] != Switch && args[0] != SettleSwitch))
        {
            return null;
        }

        var watch = output == null;
        output ??= Console.Out;
        string store = null, point = "latest", scratch = null, bundle = null;
        var schema = new List<string>();
        var watchStdin = false;
        for (var i = 1; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--store": store = value; i++; break;
                case "--point": point = value; i++; break;
                case "--scratch": scratch = value; i++; break;
                case "--bundle": bundle = value; i++; break;
                case "--schema": schema.Add(value); i++; break;
                case WatchStdin: watchStdin = true; break;
                default:
                    Write(output, new Line("error", null, 0, 0, null, "failed", $"Unknown child-mode argument '{args[i]}'."));
                    return ExitUsage;
            }
        }

        if (watch && watchStdin)
        {
            WatchParent(Console.In, static () => Environment.Exit(ExitOrphaned));
        }

        var schemaPaths = schema.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        return args[0] == SettleSwitch ? Settle(output, bundle, schemaPaths) : Drill(output, store, point, scratch, schemaPaths);
    }

    private static int Drill(TextWriter output, string store, string point, string scratch, string[] schemaPaths)
    {
        if (string.IsNullOrWhiteSpace(store) || string.IsNullOrWhiteSpace(scratch) || !TryParsePoint(point, out var selector))
        {
            Write(output, new Line("error", null, 0, 0, null, "failed", "Usage: --backup-drill --store S --point latest|N --scratch D [--schema dll]…"));
            return ExitUsage;
        }

        try
        {
            var needsSchema = false;
            var report = BackupOperations.Drill(store, selector, new BackupDrillOptions
            {
                ScratchDirectory = scratch,
                Progress = (stage, done, total) => Write(output, new Line("progress", stage.ToString(), done, total, null, null, null)),
                OpenAndClose = b =>
                {
                    try
                    {
                        // The drill's own guard checks the result against the image it restored (an unclean image no recovery ran on fails the open).
                        return BackupSettle.InThisProcess(b, schemaPaths).Result;
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

    private static int Settle(TextWriter output, string bundle, string[] schemaPaths)
    {
        if (string.IsNullOrWhiteSpace(bundle))
        {
            Write(output, new Line("error", null, 0, 0, null, "failed", "Usage: --backup-settle --bundle B [--schema dll]…"));
            return ExitUsage;
        }

        try
        {
            var (result, required) = BackupSettle.InThisProcess(bundle, schemaPaths);
            var settle = new BackupSettleReport(required, result.CrashRecoveryRan, BackupDtoMapper.Map(result.Recovery), result.Duration.TotalMilliseconds,
                Environment.ProcessId);
            Write(output, new Line("settle", null, 0, 0, null, null, null, settle));
            return ExitReport;
        }
        catch (Exception e)
        {
            Write(output, new Line("error", null, 0, 0, null, BackupDtoMapper.Category(e), e.Message, NeedsSchema: BackupSettle.NeedsSchema(e)));
            return ExitError;
        }
    }

    /// <summary>
    /// Calls <paramref name="orphaned"/> once <paramref name="input"/> reaches its end (or breaks): the parent closed the pipe, or died. On a background
    /// thread, so it never keeps the process alive by itself.
    /// </summary>
    internal static void WatchParent(TextReader input, Action orphaned)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var buffer = new char[256];
                while (input.Read(buffer, 0, buffer.Length) > 0)
                {
                    // The parent writes nothing; whatever comes is ignored.
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // A broken pipe is a parent gone too.
            }

            orphaned();
        })
        {
            IsBackground = true,
            Name = "Backup child: parent watch",
        };
        thread.Start();
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
