using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Backups;

/// <summary>
/// The settle of a restored bundle (#1417 WB-10): open it — its WAL tail replays, its derived structures rebuild, its image is sealed — and close it
/// cleanly. With the application's schema assemblies when the panel's session loaded them, through the Workbench's own open; otherwise the engine's
/// schemaless settle (<see cref="BackupOperations.OpenAndCloseRestored"/>), which cannot replay records of archetypes it does not register (7012).
/// </summary>
public static class BackupSettle
{
    /// <summary>Opens and closes <paramref name="bundle"/>; throws what the open threw (the restore and the drill report it).</summary>
    public static BackupSettleResult OpenAndClose(string bundle, string[] schemaDllPaths, ILogger logger = null)
    {
        if (schemaDllPaths is not { Length: > 0 })
        {
            return BackupOperations.OpenAndCloseRestored(bundle, logger);
        }

        var clock = Stopwatch.StartNew();
        using var lifecycle = EngineLifecycle.OpenAsync(bundle, schemaDllPaths).GetAwaiter().GetResult();
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

        return BackupDrillRunner.SettleResultOf(lifecycle.Engine, clock.Elapsed);
    }

    /// <summary>
    /// The settle failed only because this process does not register the application's archetypes (7012, #1309): the point is not damaged, it needs
    /// its application's schema to settle.
    /// </summary>
    public static bool NeedsSchema(Exception e)
    {
        for (; e != null; e = e.InnerException)
        {
            if (e is TyphonException { ErrorCode: TyphonErrorCode.RecoveryArchetypeNotRegistered })
            {
                return true;
            }
        }

        return false;
    }
}
