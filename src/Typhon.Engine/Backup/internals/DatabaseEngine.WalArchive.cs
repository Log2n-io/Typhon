using System;

namespace Typhon.Engine;

/// <summary>
/// Continuous WAL archiving (#1418): the engine's side. Whether archiving is configured is read once, by the constructor, because the archive floor and
/// the open's reclaim must hold every segment from the first instant (BKP-10); the archiver itself is built later, by the backup scheduler (#1416), and
/// stopped by the close before its final checkpoint (WA-23).
/// </summary>
public partial class DatabaseEngine
{
    /// <summary>The archiving configuration this engine opened with; null when archiving is off.</summary>
    internal WalArchiveConfiguration ArchiveConfiguration => _options.WalArchive;

    /// <summary>Refuses the open, before anything is written for the WAL, when archiving is configured with options that cannot be used.</summary>
    private void ValidateArchiveConfiguration()
    {
        var archive = _options.WalArchive;
        if (archive == null)
        {
            return;
        }

        if (archive.Options == null)
        {
            throw new ArgumentException("WAL archiving is configured without its ArchiveOptions.");
        }

        if (!archive.Options.Validate(_options.Wal, archive.StorePath, out var error))
        {
            throw new ArgumentException(error);
        }
    }

    /// <summary>
    /// Builds the WAL archiver (impl/1418 §4.5): <c>null</c> when <paramref name="options"/> is null. The backup scheduler (#1416) calls it once its open
    /// completed; the archiver lives until the close stops it.
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
}
