using JetBrains.Annotations;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine;

/// <summary>What <see cref="IBackupService.CreateAsync(string, BackupRequestKind, System.Threading.CancellationToken)"/> is asked for.</summary>
[PublicAPI]
public enum BackupRequestKind
{
    /// <summary>Admission decides: an incremental point when the store's chain head continues this database's lineage, a full one otherwise.</summary>
    Auto = 0,

    /// <summary>A full point, the base of a new chain, whatever admission would allow.</summary>
    Full = 1,
}

/// <summary>
/// Online backup of a running database (<see cref="DatabaseEngine.Backup"/>): a consistent point taken while the engine keeps committing, one at a
/// time, on the engine's backup thread. With <see cref="DatabaseEngineOptions.Backup"/> set, the engine also schedules points, verifies and retains them,
/// and reports its health (#1416).
/// </summary>
/// <remarks>
/// Failures: a <see cref="BackupAbortedException"/> when the capture was abandoned, its <see cref="BackupAbortedException.Reason"/> saying why
/// (cancelled, the engine closing, a writer the backup could not keep up with, the store failing). An abandoned point publishes nothing and forgets no
/// page: the next point covers it. A <see cref="TyphonException"/> with <see cref="TyphonErrorCode.BackupInProgress"/> when a backup is already
/// running, or <see cref="TyphonErrorCode.BackupNotConfigured"/> when the store asked for is not the one configured. The store's own refusals are the typed
/// <see cref="BackupStoreLockedException"/> and <see cref="BackupStoreMismatchException"/>.
/// </remarks>
[PublicAPI]
public interface IBackupService
{
    /// <summary>Takes a backup point into the configured store (<see cref="BackupOptions.StorePath"/>).</summary>
    /// <param name="kind">Auto (admission decides) or a forced full point.</param>
    /// <param name="ct">Abandons the capture: nothing is published.</param>
    /// <returns>The published point, once it is published and verified.</returns>
    /// <exception cref="TyphonException"><see cref="TyphonErrorCode.BackupNotConfigured"/>: this engine has no <see cref="BackupOptions"/>.</exception>
    Task<BackupPointSummary> CreateAsync(BackupRequestKind kind = BackupRequestKind.Auto, CancellationToken ct = default);

    /// <summary>
    /// Takes a backup point of the running database into the store at <paramref name="storePath"/> (created when absent). An engine with
    /// <see cref="BackupOptions"/> accepts only its configured store: a point elsewhere would cost that store its incremental chain.
    /// </summary>
    /// <param name="storePath">The store directory; it belongs to this database once it holds a point.</param>
    /// <param name="kind">Auto (admission decides) or a forced full point.</param>
    /// <param name="ct">Abandons the capture: nothing is published.</param>
    /// <returns>The published point, once it is published and verified.</returns>
    Task<BackupPointSummary> CreateAsync(string storePath, BackupRequestKind kind = BackupRequestKind.Auto, CancellationToken ct = default);
}
