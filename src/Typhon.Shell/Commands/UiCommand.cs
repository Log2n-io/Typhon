using System;
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

        [CommandOption("--trace <PATH>")]
        [Description("Optional .typhon-trace file to auto-open in the profiler on launch.")]
        public string Trace { get; set; }

        [CommandOption("--open-latest")]
        [Description("Auto-open the newest *.typhon-trace capture under ./captures on launch (watch a profile).")]
        public bool OpenLatest { get; set; }

        [CommandOption("--open-db")]
        [Description("Auto-open the *.typhon database in the current directory on launch (investigate a database).")]
        public bool OpenDb { get; set; }

        [CommandOption("--schema <PATH>")]
        [Description("Schema assembly (.dll) or a directory containing it, used to interpret an opened database. " +
                     "When omitted, the app's built assembly is auto-discovered from ./bin.")]
        public string Schema { get; set; }

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

        // A Workbench session opens EITHER a database OR a profiler trace — never both at once. Reject the
        // combination up front (fail fast, before any filesystem discovery) with a clear message.
        var targetsError = ValidateOpenTargets(settings);
        if (targetsError is not null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(targetsError)}[/]");
            return 1;
        }

        // Resolve the optional database to an absolute path CLI-side so the SPA (which auto-opens it via the
        // launch fragment) is never dependent on the host process's working directory. Path comes from either the
        // positional [database] or --open-db (the sole *.typhon in the CWD); ResolveDbPath enforces they're not both.
        var dbPath = ResolveDbPath(settings, out var dbError);
        if (dbError is not null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(dbError)}[/]");
            return 1;
        }

        // Resolve the optional trace path the same way (absolute, CLI-side). Mirrors the db= auto-open: the SPA
        // opens it via the launch fragment (POST /api/sessions/trace) so it never depends on the host CWD.
        var tracePath = ResolveTracePath(settings, out var traceError);
        if (traceError is not null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(traceError)}[/]");
            return 1;
        }

        // Resolve the schema assembly used to interpret an opened database. A .typhon db records the assembly that
        // defined its schema; without that DLL the Workbench can only show engine internals (0 archetypes). Explicit
        // --schema wins; otherwise, when opening a db, auto-discover the app's built assembly from ./bin so
        // `typhon ui --open-db` shows the actual data with no extra flags.
        var schemaPath = ResolveSchemaPath(settings, dbPath, out var schemaError);
        if (schemaError is not null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(schemaError)}[/]");
            return 1;
        }
        if (schemaPath is not null)
        {
            AnsiConsole.MarkupLine($"[grey]Using schema assembly: {Markup.Escape(schemaPath)}[/]");
        }

        var options = new WorkbenchHostOptions
        {
            Url = url,
            DbPath = dbPath,
            TracePath = tracePath,
            SchemaPath = schemaPath,
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

    /// <summary>
    /// Validates the initial-session target flags. A Workbench session opens a database <b>or</b> a trace, never both,
    /// so a database intent (<c>[database]</c> / <c>--open-db</c>) combined with a trace intent (<c>--trace</c> /
    /// <c>--open-latest</c>) is rejected. <c>--schema</c> only means anything for a database, so it is rejected without
    /// one. Returns a human-readable error, or null when the combination is valid.
    /// </summary>
    internal static string ValidateOpenTargets(Settings settings)
    {
        var wantsDb = !string.IsNullOrWhiteSpace(settings.Database) || settings.OpenDb;
        var wantsTrace = !string.IsNullOrWhiteSpace(settings.Trace) || settings.OpenLatest;

        if (wantsDb && wantsTrace)
        {
            return "Open either a database or a trace, not both — a Workbench session holds one at a time.";
        }

        if (!string.IsNullOrWhiteSpace(settings.Schema) && !wantsDb)
        {
            return "--schema only applies when opening a database (--open-db or a positional [database]).";
        }

        return null;
    }

    /// <summary>
    /// Resolves the database to auto-open (if any) to an absolute path. Returns null with no error when neither the
    /// positional <c>[database]</c> nor <c>--open-db</c> is given. Sets <paramref name="error"/> (and returns null)
    /// when both are given, or when <c>--open-db</c> finds no — or more than one — <c>*.typhon</c> database in the CWD.
    /// </summary>
    private static string ResolveDbPath(Settings settings, out string error)
    {
        error = null;
        var hasArg = !string.IsNullOrWhiteSpace(settings.Database);

        if (hasArg && settings.OpenDb)
        {
            error = "[database] and --open-db are mutually exclusive; specify only one.";
            return null;
        }

        if (hasArg)
        {
            return Path.GetFullPath(settings.Database);
        }

        if (!settings.OpenDb)
        {
            return null;
        }

        // --open-db: the sole *.typhon database directory directly under the current directory.
        return FindDatabaseInDirectory(Directory.GetCurrentDirectory(), out error);
    }

    /// <summary>
    /// Returns the absolute path of the sole <c>*.typhon</c> database directory directly under
    /// <paramref name="dir"/>. A Typhon database is a directory (holding <c>data</c> + <c>wal/</c>), so this globs
    /// directories, not files. Sets <paramref name="error"/> and returns null when none — or more than one — exist
    /// (the CLI can't guess which), telling the user to pass the path explicitly.
    /// </summary>
    internal static string FindDatabaseInDirectory(string dir, out string error)
    {
        error = null;
        string[] matches = Directory.Exists(dir) ? Directory.GetDirectories(dir, "*.typhon") : [];

        if (matches.Length == 0)
        {
            error = $"No *.typhon database found in {dir}. Run the app once to create one, or pass its path: typhon ui <database>.";
            return null;
        }

        if (matches.Length > 1)
        {
            var names = string.Join(", ", Array.ConvertAll(matches, Path.GetFileName));
            error = $"Multiple *.typhon databases found in {dir}: {names}. Pass one explicitly: typhon ui <database>.";
            return null;
        }

        return Path.GetFullPath(matches[0]);
    }

    /// <summary>
    /// Resolves the schema assembly used to interpret an opened database, as an absolute <c>.dll</c> path (or null).
    /// Explicit <c>--schema</c> wins — a <c>.dll</c> file, or a directory searched for the app assembly. Otherwise,
    /// when a database is being opened, the app's built assembly is auto-discovered from <c>./bin</c> (best-effort:
    /// returns null and lets the Workbench show its incompatible banner when it can't be found, rather than failing).
    /// </summary>
    private static string ResolveSchemaPath(Settings settings, string dbPath, out string error)
    {
        error = null;

        if (!string.IsNullOrWhiteSpace(settings.Schema))
        {
            var s = Path.GetFullPath(settings.Schema);
            if (File.Exists(s))
            {
                return s;
            }
            if (Directory.Exists(s))
            {
                var inDir = FindSchemaAssembly(s, DeriveAssemblyName(Directory.GetCurrentDirectory()));
                if (inDir is not null)
                {
                    return inDir;
                }
            }
            error = $"--schema not found: {s} (expected a .dll file or a directory containing the schema assembly).";
            return null;
        }

        // Auto-discovery only applies when a database is being opened.
        if (dbPath is null)
        {
            return null;
        }

        var name = DeriveAssemblyName(Directory.GetCurrentDirectory());
        if (name is null)
        {
            return null;
        }

        return FindSchemaAssembly(Path.Combine(Directory.GetCurrentDirectory(), "bin"), name);
    }

    /// <summary>
    /// Derives the app's assembly name from the sole <c>*.csproj</c> directly under <paramref name="dir"/> (its file
    /// name without extension — the MSBuild default), or null when there is not exactly one project to key off.
    /// </summary>
    internal static string DeriveAssemblyName(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }
        var projects = Directory.GetFiles(dir, "*.csproj");
        return projects.Length == 1 ? Path.GetFileNameWithoutExtension(projects[0]) : null;
    }

    /// <summary>
    /// Finds the most-recently-built <c>{assemblyName}.dll</c> anywhere under <paramref name="root"/> (typically
    /// <c>./bin</c>), or null when none exists or inputs are missing. Reference assemblies (<c>…/ref/{name}.dll</c>)
    /// are skipped — they are metadata-only stubs that can't be loaded for schema reflection. Newest-wins so a fresh
    /// build is preferred over a stale one.
    /// </summary>
    internal static string FindSchemaAssembly(string root, string assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName) || !Directory.Exists(root))
        {
            return null;
        }

        string newest = null;
        var newestTime = DateTime.MinValue;
        foreach (var f in Directory.GetFiles(root, assemblyName + ".dll", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), "ref", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var t = File.GetLastWriteTimeUtc(f);
            if (newest is null || t > newestTime)
            {
                newest = f;
                newestTime = t;
            }
        }

        return newest is null ? null : Path.GetFullPath(newest);
    }

    /// <summary>
    /// Resolves the trace to auto-open (if any) to an absolute path. Returns null with no error when neither
    /// <c>--trace</c> nor <c>--open-latest</c> is given. On a user error (both flags, or no captures found) it
    /// returns null and sets <paramref name="error"/> to a human-readable message so <c>Execute</c> can print it
    /// and exit 1.
    /// </summary>
    private static string ResolveTracePath(Settings settings, out string error)
    {
        error = null;
        var hasTrace = !string.IsNullOrWhiteSpace(settings.Trace);

        if (hasTrace && settings.OpenLatest)
        {
            error = "--trace and --open-latest are mutually exclusive; specify only one.";
            return null;
        }

        if (hasTrace)
        {
            return Path.GetFullPath(settings.Trace);
        }

        if (!settings.OpenLatest)
        {
            return null;
        }

        // --open-latest: newest *.typhon-trace under <cwd>/captures.
        var capturesDir = Path.Combine(Directory.GetCurrentDirectory(), "captures");
        var latest = FindLatestTrace(capturesDir);
        if (latest is null)
        {
            error = $"No *.typhon-trace captures found under {capturesDir}. Record a trace first, or pass --trace <PATH>.";
            return null;
        }

        return latest;
    }

    /// <summary>Returns the absolute path of the most-recently-written <c>*.typhon-trace</c> in <paramref name="capturesDir"/>, or null when none exist.</summary>
    private static string FindLatestTrace(string capturesDir)
    {
        if (!Directory.Exists(capturesDir))
        {
            return null;
        }

        var files = new DirectoryInfo(capturesDir).GetFiles("*.typhon-trace");
        if (files.Length == 0)
        {
            return null;
        }

        var newest = files[0];
        for (var i = 1; i < files.Length; i++)
        {
            if (files[i].LastWriteTimeUtc > newest.LastWriteTimeUtc)
            {
                newest = files[i];
            }
        }

        return newest.FullName;
    }
}
