using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine;

/// <summary>
/// Continuous WAL archiving (#1418): the engine's side. Whether archiving is configured is read once, by the constructor, because the archive floor and
/// the open's reclaim must hold every segment from the first instant (BKP-10); the archiver itself is built later, by the backup scheduler (#1416), and
/// stopped by the close before its final checkpoint (WA-23).
/// </summary>
public partial class DatabaseEngine
{
    /// <summary>
    /// The archiving configuration this engine opened with (<see cref="BackupOptions.Archive"/>, written to <see cref="BackupOptions.StorePath"/>); null when
    /// archiving is off.
    /// </summary>
    internal ArchiveOptions ArchiveConfiguration => _options.Backup?.Archive;

    /// <summary>The archiver <see cref="CreateWalArchiver(ArchiveOptions, string)"/> built; null until then. The close stops it before its final checkpoint.</summary>
    internal WalArchiver WalArchiver { get; private set; }

    /// <summary>Refuses the open, before anything is written for the WAL, when archiving is configured with options that cannot be used.</summary>
    private void ValidateArchiveConfiguration()
    {
        var archive = ArchiveConfiguration;
        if (archive == null)
        {
            return;
        }

        if (!archive.Validate(_options.Wal, _options.Backup.StorePath, out var error))
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

    /// <summary>What the crash open's torn-tail seal did; default when it did not run.</summary>
    internal WalTornTailSealResult LastTornTailSeal { get; private set; }

    /// <summary>Test seam: the crash open skips the torn-tail seal (BKP-10's mutant). False in production.</summary>
    internal bool TornTailSealDisabledForTest { get; set; }

    /// <summary>Test seam: how long the crash open's seal waits for its checkpoint (a soft timeout). 30 s in production.</summary>
    internal TimeSpan RecoverySealWaitForTest { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// BKP-10's torn-tail seal (WA-9), on a crash open whose recovery stopped at a break, after the recovery's seal and before the session's first commit:
    /// with archiving configured the archive floor keeps the torn segment past this open, so its tear must not stop the next recovery before the segments
    /// this session writes. Archiving off, the seal's checkpoint reclaims the torn segment as it always did.
    /// </summary>
    /// <remarks>
    /// The sentinel hides the torn frame's valid fence chunks, which this recovery applied (fences apply without a commit) and a flush-only cycle may
    /// already have written to the data file. Hidden while CheckpointLSN is below them, a second recovery would replay the window without them: the fence
    /// half in the file. So the seal waits for the seal's cycle (soft, its timeout leaning on re-replay); when that cycle did not reach them, the open fails
    /// rather than seal, and the next open recovers, and seals, again.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The recovery's checkpoint did not consolidate the fence records the sentinel would hide.</exception>
    private void SealTornTailForArchive(string walDir)
    {
        if (ArchiveConfiguration == null || TornTailSealDisabledForTest)
        {
            return;
        }

        long consolidated;
        using (EpochGuard.Enter(EpochManager))
        {
            consolidated = DurabilityWatermarks.ReadCheckpointLsn(MMF);
        }

        var walIO = _injectedWalIo ?? new WalFileIO();
        try
        {
            var seal = WalTornTailSeal.Seal(walIO, walDir, consolidated);
            LastTornTailSeal = seal;
            if (seal.Sealed)
            {
                WalArchiveLog.TornTailSealed(Logger, seal.SegmentId, seal.Offset, seal.FenceLsn);
            }
            else if (seal.Unconsolidated)
            {
                throw new InvalidOperationException(
                    $"The crash open's checkpoint did not complete: WAL segment {seal.SegmentId} ends torn after fence records through LSN {seal.FenceLsn} "
                    + $"that recovery applied and CheckpointLSN ({consolidated}) does not cover, so its tear cannot be sealed. Nothing is lost: the next open "
                    + "recovers, and seals it, again.");
            }
            else if (seal.SegmentId != 0)
            {
                WalArchiveLog.MidLogBreakNotSealed(Logger, seal.SegmentId);
            }
        }
        finally
        {
            if (_injectedWalIo == null)
            {
                walIO.Dispose();
            }
        }
    }
}
