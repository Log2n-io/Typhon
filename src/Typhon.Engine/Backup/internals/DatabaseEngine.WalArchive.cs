using System;

namespace Typhon.Engine;

/// <summary>
/// Continuous WAL archiving (#1418): the engine's side, as the backup scheduler (#1416) consumes it. A stand-in on the #1416 branch: #1418 owns this
/// file, and its version (the archiver, its floor, its close-time stop) replaces this one at the merge.
/// </summary>
public partial class DatabaseEngine
{
    /// <summary>
    /// Builds the WAL archiver (impl/1418 §4.5): <c>null</c> when <paramref name="options"/> is null. The backup scheduler (#1416) calls it once the open
    /// completed; the archiver lives until the close stops it. Null on this branch: the archiver is #1418's.
    /// </summary>
    /// <param name="options">The archive options; null when archiving is off.</param>
    /// <param name="storePath">The backup store the archive is written to.</param>
    internal IWalArchiver CreateWalArchiver(ArchiveOptions options, string storePath)
    {
        if (options == null)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrEmpty(storePath);
        return null;
    }

    /// <summary>
    /// Refuses the open when <see cref="BackupOptions.Archive"/> is set: until #1418's archiver is in this build, configured archiving would silently do
    /// nothing (impl/1418 §4.5: "<c>Archive != null</c> fails validation" until the archiver lands).
    /// </summary>
    private void ValidateArchiveConfiguration()
    {
        if (_options.Backup?.Archive != null)
        {
            throw new ArgumentException("BackupOptions.Archive is set, but continuous WAL archiving (#1418) is not part of this build.");
        }
    }
}
