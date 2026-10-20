using JetBrains.Annotations;
using System;
using System.IO;

namespace Typhon.Engine;

/// <summary>
/// The engine's own backups (07 §1, #1416): where they go, how often, what is kept, and what the engine accepts from outside. Set
/// <see cref="DatabaseEngineOptions.Backup"/> (or <see cref="TyphonOptions.Backup"/>); null leaves backup off and the engine takes points only when the
/// application asks (<see cref="IBackupService.CreateAsync(string, BackupRequestKind, System.Threading.CancellationToken)"/>).
/// </summary>
/// <remarks>
/// The load-bearing values have no default: <see cref="StorePath"/>, <see cref="Interval"/> and <see cref="Retention"/> must be stated, the last one
/// as <c>null</c> when no automatic retention is wanted. 0 means "none" wherever a duration may be zero, never a sentinel.
/// </remarks>
[PublicAPI]
public sealed class BackupOptions
{
    /// <summary>The finest <see cref="Interval"/>: the scheduler decides once a second.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>The backup store, a directory the engine creates when absent. Points, compactions and retention write here and nowhere else.</summary>
    public required string StorePath { get; init; }

    /// <summary>
    /// How often the engine takes a point. <see cref="TimeSpan.Zero"/>: no schedule — points come from <see cref="IBackupService"/> and from
    /// <c>backup.request</c> only. Else at least <see cref="MinInterval"/>.
    /// </summary>
    public required TimeSpan Interval { get; init; }

    /// <summary>Applied after every point the engine takes; <c>null</c>: no automatic retention (stated, not defaulted).</summary>
    public required BackupRetentionPolicy Retention { get; init; }

    /// <summary>
    /// Verify each new pack in full (every byte, every page) after it is published, instead of its quick verify (headers, index, tail, chain). The point is
    /// always verified; this makes the check read the whole pack.
    /// </summary>
    public bool FullVerifyAfterBackup { get; init; }

    /// <summary>
    /// Honour <c>{bundle}/backup.request</c>, the file <c>typhon backup create &lt;bundle&gt;</c> writes to ask a running engine for a point.
    /// </summary>
    public bool AcceptRequests { get; init; } = true;

    /// <summary>
    /// How old the newest drill may be before health warns (<c>DrillStale</c>). <see cref="TimeSpan.Zero"/>: drills are not
    /// monitored.
    /// </summary>
    public TimeSpan DrillWindow { get; init; }

    /// <summary>Copy-before-write slots, 8 KiB each, writers can park while the backup thread is busy with the store (02 §4).</summary>
    public int CbwBufferPages { get; init; } = 256;

    /// <summary>The most a data-file writer waits in copy-before-write, per write; past it the capture aborts and the write proceeds (BKP-12).</summary>
    public TimeSpan CbwStallTimeout { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Continuous WAL archiving into the same store (Layer 2, #1418); <c>null</c>: off.</summary>
    public ArchiveOptions Archive { get; init; }

    /// <summary>Test seam: the scheduler builds no archiver; the test builds one with <c>CreateWalArchiver</c>'s seams and drives it itself.</summary>
    internal bool ArchiverByTest { get; init; }

    /// <summary>Test seam: the scheduler starts no thread; the test drives its ticks on logical clocks.</summary>
    internal bool ManualTickForTest { get; init; }

    /// <summary>Checks the values, and the retention policy; the archive options against nothing (the engine checks them against its WAL).</summary>
    /// <param name="error">What is wrong, or <c>null</c>.</param>
    public bool Validate(out string error)
    {
        error = string.IsNullOrWhiteSpace(StorePath) ? "BackupOptions.StorePath must be set: the backup store's directory."
            : Interval < TimeSpan.Zero || (Interval > TimeSpan.Zero && Interval < MinInterval)
                ? $"BackupOptions.Interval is {Interval}: zero (no schedule) or at least {MinInterval}."
            : DrillWindow < TimeSpan.Zero ? $"BackupOptions.DrillWindow is {DrillWindow}: zero (not monitored) or positive."
            : CbwBufferPages < 1 ? $"BackupOptions.CbwBufferPages is {CbwBufferPages}: at least one slot."
            : CbwStallTimeout <= TimeSpan.Zero ? $"BackupOptions.CbwStallTimeout is {CbwStallTimeout}: it must be positive."
            : null;
        if (error == null && Retention != null && !Retention.Validate(out var retention))
        {
            error = "BackupOptions.Retention: " + retention;
        }

        return error == null;
    }

    /// <summary><see cref="Validate(out string)"/>, then the archive options against the engine's WAL (#1418).</summary>
    internal bool Validate(WalWriterOptions wal, out string error)
    {
        if (!Validate(out error))
        {
            return false;
        }

        return Archive == null || wal == null || Archive.Validate(wal, StorePath, out error);
    }

    /// <summary>Whether <paramref name="path"/> names <see cref="StorePath"/> (full paths, the platform's case rule).</summary>
    internal bool IsStore(string path) =>
        string.Equals(Normalize(path), Normalize(StorePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
