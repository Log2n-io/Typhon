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
    /// Opens a restored bundle with no application schema and closes it cleanly: the open runs <c>InitializeArchetypes</c> from the persisted schema, which
    /// is where the WAL tail replays, the derived structures rebuild and the image is sealed. The drill's default opener, and a restore's settle.
    /// </summary>
    /// <param name="bundle">The restored <c>{name}.typhon</c> bundle.</param>
    /// <param name="logger">The engine's log; <c>null</c> = none.</param>
    /// <returns>Whether the crash path ran, and what its recovery did.</returns>
    public static BackupSettleResult OpenAndCloseRestored(string bundle, ILogger logger = null) => BackupDrillRunner.OpenAndCloseRestored(bundle, logger, 0);
}
