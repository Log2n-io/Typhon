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

    /// <summary>The archiver <see cref="CreateWalArchiver(ArchiveOptions, string)"/> built; null until then. The close stops it before its final checkpoint.</summary>
    internal WalArchiver WalArchiver { get; private set; }

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
    /// Builds and starts the WAL archiver (impl/1418 §4.5): <c>null</c> when <paramref name="options"/> is null. The backup scheduler (#1416) calls it once
    /// the open completed; the archiver lives until the close stops it. Archiving must have been configured when this engine opened: its floor holds every
    /// segment from the open on, and an archiver started later would find segments the open already reclaimed.
    /// </summary>
    /// <param name="options">The archive options; null when archiving is off.</param>
    /// <param name="storePath">The backup store the archive is written to.</param>
    internal IWalArchiver CreateWalArchiver(ArchiveOptions options, string storePath) =>
        options == null ? null : CreateWalArchiver(options, storePath, null, startWorker: true);

    /// <summary>
    /// <see cref="CreateWalArchiver(ArchiveOptions, string)"/> with the test seams: the store's file system, and a worker left unstarted (the test then drives
    /// <see cref="Internals.WalArchiver.Step"/> and <see cref="Internals.WalArchiver.ArchivePending"/> on its own thread).
    /// </summary>
    internal WalArchiver CreateWalArchiver(ArchiveOptions options, string storePath, IStoreFileSystem fs, bool startWorker)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(storePath);
        if (ArchiveConfiguration == null)
        {
            throw new InvalidOperationException("WAL archiving was not configured when this database opened: its archive floor did not hold the WAL.");
        }

        if (WalArchiver != null)
        {
            throw new InvalidOperationException("This engine's WAL archiver already exists.");
        }

        var archiver = new WalArchiver(this, options, storePath, _durabilityNode, Logger, fs);
        WalArchiver = archiver;
        if (startWorker)
        {
            archiver.Start();
        }

        return archiver;
    }

    /// <summary>
    /// The close's archive step (WA-23), after the drains and the free quarantine's end, before the final checkpoint: the final pass archives what the
    /// session wrote and the final cycle sees the raised floor. A close the engine treats as a crash makes no pass.
    /// </summary>
    private void StopWalArchiverAtClose(bool asCrash)
    {
        var archiver = WalArchiver;
        if (archiver == null)
        {
            return;
        }

        archiver.Stop(asCrash ? TimeSpan.Zero : archiver.CloseTimeout);
        archiver.Dispose();
        WalArchiver = null;
    }

    /// <summary>What <c>wal/&lt;t&gt;/timeline.json</c> says about this database's current timeline (impl/1418 §3.6).</summary>
    internal TimelineFile DescribeTimeline(Guid storeId) => new()
    {
        FormatVersion = TimelineFile.CurrentFormatVersion,
        TimelineId = TimelineId,
        DatabaseId = DatabaseId,
        StoreId = storeId,
        ParentTimelineId = null,
        OriginLsn = 0,
        Reason = TimelineReason.Genesis,
        CreatedUtc = DateTime.UtcNow,
    };
}
