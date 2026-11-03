using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Typhon.Workbench.Dtos.Backups;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Backups;

/// <summary>
/// What a settle did, as the child process reports it: whether the open had to take the crash path (<see cref="RecoveryRequired"/>: an unclean image, or
/// a restore past the image's own WAL), whether it did, what it replayed, and which process ran it.
/// </summary>
public sealed record BackupSettleReport(bool RecoveryRequired, bool CrashRecoveryRan, BackupRecoveryDto Recovery, double DurationMs, int ProcessId);

/// <summary>
/// The settle of a restored bundle (#1417 WB-10): open it — its WAL tail replays, its derived structures rebuild, its image is sealed — and close it
/// cleanly, through the Workbench's own open with the application's schema assemblies: the ones given (files, or directories to search), else the ones the
/// bundle's manifest names, found the way a session finds them. The open is exact (#1485, <see cref="SchemaMatch.Exact"/>): an assembly the manifest names
/// that is missing or another build, or a schema that is not the database's, is refused (<see cref="SchemaMismatchException"/>) before anything is
/// loaded or written. A database whose manifest is empty opens with the persisted schema alone, which cannot replay records of archetypes it does not
/// register (7012).
/// </summary>
/// <remarks>
/// <b>Runs only in a process of its own</b> (<see cref="BackupDrillChild"/>'s <c>--backup-settle</c> and <c>--backup-drill</c> modes, or
/// <c>typhon backup drill</c>), never in a Workbench with a live session: a schema'd open would
/// load the session's assemblies a second time, into another collectible context, through the process-global archetype registry a live session uses —
/// the hazard WB-13 moved the drill out of process for. Its engine takes the engine's default page cache, not a session's 1 GiB (R7).
/// </remarks>
public static class BackupSettle
{
    /// <summary>
    /// Opens and closes <paramref name="bundle"/> exactly. <paramref name="schema"/> names where the application's schema assemblies are: files, or
    /// directories, searched first, before the default order; none: the default order alone. Every assembly the manifest names is taken at its exact
    /// identity from the first place holding it — a file named here is no exception, so a directory and a file mix. Throws what the open threw; an
    /// assembly that cannot be loaded is a <see cref="SchemaMismatchException"/> too: the drill could not run with the build it was given.
    /// </summary>
    /// <param name="bundle">The restored bundle.</param>
    /// <param name="schema">Schema assembly files and directories.</param>
    /// <param name="consoleLog">Whether the engine logs to the console (the child's stdout carries prefixed protocol lines; the CLI's is its report).</param>
    /// <returns>The engine's settle result, and whether the open had to take the crash path.</returns>
    /// <exception cref="ArgumentException">An entry of <paramref name="schema"/> is neither a file nor a directory.</exception>
    public static (BackupSettleResult Result, bool RecoveryRequired) InThisProcess(string bundle, string[] schema, bool consoleLog = true)
    {
        if (!File.Exists(Path.Combine(bundle, "data")))
        {
            throw new SchemaMismatchException("The database", [$"'{bundle}' holds no database: an exact open reproduces one as written, it creates none"]);
        }

        // A file the caller names is the build it means: another build of a manifest assembly is refused here, even when the search order would find the
        // exact one elsewhere.
        var manifest = BackupOperations.ReadSchemaManifest(bundle);
        var named = new List<string>();
        foreach (var s in schema ?? [])
        {
            if (File.Exists(s) && SchemaAssemblyResolver.CheckNamedFile(manifest, s) is { } mismatch)
            {
                named.Add($"assembly {mismatch.Name}: the database was written by {mismatch.Expected}, {mismatch.Path} is {mismatch.Found}");
            }
        }

        if (named.Count > 0)
        {
            throw new SchemaMismatchException("The schema assemblies", named);
        }

        var dirs = SearchDirectories(schema);
        var clock = Stopwatch.StartNew();
        BackupSettleResult result;
        bool required;
        EngineLifecycle opened;
        try
        {
            opened = EngineLifecycle.OpenAsync(bundle, [], dirs, pageCacheBytes: 0, exactSchema: true, consoleLog: consoleLog).GetAwaiter().GetResult();
        }
        catch (WorkbenchException e) when (e.ErrorCode.StartsWith("schema_", StringComparison.Ordinal))
        {
            throw new SchemaMismatchException("The schema assemblies", [$"could not be loaded: {e.Message}"]);
        }

        using (var lifecycle = opened)
        {
            if (lifecycle.InitializeArchetypesFailure is { } failure)
            {
                ExceptionDispatchInfo.Throw(failure);
            }

            result = BackupDrillRunner.SettleResultOf(lifecycle.Engine, clock.Elapsed);
            required = lifecycle.Engine.CrashRecoveryAtOpen;
        }

        // Open to close: the clean close is part of the settle.
        return (result with { Duration = clock.Elapsed }, required);
    }

    /// <summary>
    /// The directories <paramref name="schema"/> names — a file stands for its directory — in order, each once. Checked before anything is restored or
    /// opened: <c>typhon backup drill</c> and the child refuse a wrong path as a usage error.
    /// </summary>
    /// <exception cref="ArgumentException">An entry is neither a file nor a directory.</exception>
    public static string[] SearchDirectories(string[] schema)
    {
        var dirs = new List<string>();
        foreach (var s in schema ?? [])
        {
            var dir = Directory.Exists(s) ? Path.GetFullPath(s)
                : File.Exists(s) ? Path.GetDirectoryName(Path.GetFullPath(s))
                : throw new ArgumentException($"--schema '{s}' is neither a schema assembly nor a directory.", nameof(schema));
            if (!dirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
            {
                dirs.Add(dir);
            }
        }

        return [.. dirs];
    }

    /// <summary>
    /// WB-10's guard: the settle is no settle when the open had to replay and did not — the bundle then holds the image at <c>C</c>, not the database at
    /// <c>P</c>. Null when the settle stands.
    /// </summary>
    public static string GuardError(BackupSettleReport settle) =>
        settle.RecoveryRequired && !settle.CrashRecoveryRan
            ? "The settle's open ran no crash recovery on a restored image that needs one: the bundle holds the image as captured, not the restored point."
            : null;

    /// <summary>
    /// The settle failed only because this process does not register the application's archetypes (7012, #1309): the point is not damaged, it needs
    /// its application's schema to settle.
    /// </summary>
    public static bool NeedsSchema(Exception e)
    {
        for (; e != null; e = e.InnerException)
        {
            if (e is TyphonException { ErrorCode: TyphonErrorCode.RecoveryArchetypeNotRegistered } or BackupJobException { NeedsSchema: true })
            {
                return true;
            }
        }

        return false;
    }
}
