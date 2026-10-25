using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Typhon.Workbench.Dtos.Backups;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Backups;

/// <summary>
/// What a settle did, as the child process reports it: whether the open had to take the crash path (<see cref="RecoveryRequired"/>: an unclean image, or
/// a restore past the image's own WAL), whether it did, what it replayed, and which process ran it.
/// </summary>
public sealed record BackupSettleReport(bool RecoveryRequired, bool CrashRecoveryRan, BackupRecoveryDto Recovery, double DurationMs, int ProcessId);

/// <summary>
/// The settle of a restored bundle (#1417 WB-10): open it — its WAL tail replays, its derived structures rebuild, its image is sealed — and close it
/// cleanly. With the application's schema assemblies when the panel's session loaded them, through the Workbench's own open; otherwise through
/// <see cref="DatabaseEngine.Open"/> with the persisted schema alone, which cannot replay records of archetypes it does not register (7012).
/// </summary>
/// <remarks>
/// <b>Runs only in the child process</b> (<see cref="BackupDrillChild"/>'s <c>--backup-settle</c> and <c>--backup-drill</c> modes): a schema'd open would
/// load the session's assemblies a second time, into another collectible context, through the process-global archetype registry a live session uses —
/// the hazard WB-13 moved the drill out of process for. Its engine takes the engine's default page cache, not a session's 1 GiB (R7).
/// </remarks>
public static class BackupSettle
{
    /// <summary>Opens and closes <paramref name="bundle"/>; throws what the open threw.</summary>
    /// <returns>The engine's settle result, and whether the open had to take the crash path.</returns>
    internal static (BackupSettleResult Result, bool RecoveryRequired) InThisProcess(string bundle, string[] schemaDllPaths)
    {
        var clock = Stopwatch.StartNew();
        BackupSettleResult result;
        bool required;
        if (schemaDllPaths is not { Length: > 0 })
        {
            // BackupOperations.OpenAndCloseRestored's open, keeping the engine's own word on whether the crash path was due.
            using var engine = DatabaseEngine.Open(bundle);
            result = BackupDrillRunner.SettleResultOf(engine, clock.Elapsed);
            required = engine.CrashRecoveryAtOpen;
        }
        else
        {
            using var lifecycle = EngineLifecycle.OpenAsync(bundle, schemaDllPaths, pageCacheBytes: 0).GetAwaiter().GetResult();
            if (lifecycle.InitializeArchetypesFailure is { } failure)
            {
                ExceptionDispatchInfo.Throw(failure);
            }

            if (lifecycle.OpenedOnCrashPathWithoutSchema)
            {
                throw new InvalidOperationException(
                    $"The schema assemblies ({string.Join(", ", schemaDllPaths.Select(Path.GetFileName))}) registered no archetype, so the open could not "
                    + "replay the restored database's WAL.");
            }

            result = BackupDrillRunner.SettleResultOf(lifecycle.Engine, clock.Elapsed);
            required = lifecycle.Engine.CrashRecoveryAtOpen;
        }

        // Open to close: the clean close is part of the settle.
        return (result with { Duration = clock.Elapsed }, required);
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
