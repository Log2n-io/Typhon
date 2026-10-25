using Microsoft.Extensions.Logging;
using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

public static partial class BackupOperations
{
    /// <summary>
    /// Drills a point (04 §6): restores it into <see cref="BackupDrillOptions.ScratchDirectory"/>, opens it (its WAL tail replays) and closes it, checks
    /// it deep, records the result in the store's <c>drills/</c> and deletes the scratch. The only check that proves a backup restores into a database;
    /// what it records feeds the engine's health and <see cref="BackupRetentionPolicy.KeepLastDrilled"/>. Takes no store lock: it only reads the packs.
    /// </summary>
    /// <param name="store">The store directory.</param>
    /// <param name="point">Which point.</param>
    /// <param name="options">Scratch directory, opener, progress, cancellation.</param>
    /// <returns>
    /// The outcome: <see cref="BackupDrillOutcome.Failed"/> when the point does not restore, open or check sound (the backup is not trustworthy);
    /// <see cref="BackupDrillOutcome.Error"/> when the drill itself could not run (scratch I/O, cancelled).
    /// </returns>
    /// <exception cref="RestoreRefusedException">The scratch directory lies inside the store, or the point is of another on-disk format revision.</exception>
    /// <exception cref="BackupChainBrokenException">The store holds no such point.</exception>
    public static BackupDrillReport Drill(string store, PointSelector point, BackupDrillOptions options) => BackupDrillRunner.Run(store, point, options);

    /// <summary>
    /// Opens a restored bundle with no application schema and closes it cleanly: the drill's default opener, and a restore's settle. The open runs
    /// <c>InitializeArchetypes</c> from the persisted schema, which rebuilds the derived structures and seals the image; the WAL tail replays there too,
    /// but only records of archetypes this process registers. A tail holding the application's records fails the open
    /// (<see cref="TyphonErrorCode.RecoveryArchetypeNotRegistered"/>): a point with no record past its checkpoint settles, most online points do not.
    /// For those, the application opens with its own registrations: <see cref="OpenAndCloseRestored(string, Action{TyphonOptions}, ILogger)"/>.
    /// </summary>
    /// <param name="bundle">The restored <c>{name}.typhon</c> bundle.</param>
    /// <param name="logger">The engine's log; <c>null</c> = none.</param>
    /// <returns>Whether the crash path ran, and what its recovery did.</returns>
    public static BackupSettleResult OpenAndCloseRestored(string bundle, ILogger logger = null) =>
        BackupDrillRunner.OpenAndCloseRestored(bundle, null, logger, 0);

    /// <summary>
    /// Opens a restored bundle with the application's registrations and closes it cleanly, so its WAL tail replays whatever archetypes it holds: the
    /// in-process drill's opener. <c>OpenAndClose = b =&gt; BackupOperations.OpenAndCloseRestored(b, o =&gt; o.Register&lt;Position&gt;())</c>.
    /// </summary>
    /// <param name="bundle">The restored <c>{name}.typhon</c> bundle.</param>
    /// <param name="configure">The registrations (and any option) the application opens its database with; passed to <see cref="DatabaseEngine.Open"/>.</param>
    /// <param name="logger">The engine's log; <c>null</c> = none.</param>
    /// <returns>Whether the crash path ran, and what its recovery did — what the drill's guard checks.</returns>
    public static BackupSettleResult OpenAndCloseRestored(string bundle, Action<TyphonOptions> configure, ILogger logger = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return BackupDrillRunner.OpenAndCloseRestored(bundle, configure, logger, 0);
    }
}
