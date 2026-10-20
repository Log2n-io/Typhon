using JetBrains.Annotations;
using System;
using System.IO;

namespace Typhon.Engine;

/// <summary>
/// Continuous WAL archiving (Layer 2, #1418): every sealed WAL segment is copied to the backup store before the checkpoint may reclaim it, so a lost
/// database volume costs at most <see cref="MaxSegmentAge"/> of commits, and a restore can target any archived LSN or time.
/// </summary>
/// <remarks>
/// The two load-bearing values have no default: <see cref="MaxSegmentAge"/> is the RPO, and <see cref="BacklogLimitBytes"/> is the disk the engine may
/// spend holding WAL for an archive that cannot keep up. Past it the engine stops waiting for the archive (the gap is recorded, and a restorable point
/// is requested): archiving never endangers the database (BKP-10).
/// </remarks>
[PublicAPI]
public sealed class ArchiveOptions
{
    /// <summary>The shortest <see cref="MaxSegmentAge"/>: each switch seals a segment, so a shorter one only multiplies files.</summary>
    public static readonly TimeSpan MinMaxSegmentAge = TimeSpan.FromSeconds(5);

    /// <summary>The finest <see cref="TimeSampleInterval"/>: the archiver samples on the backup scheduler's 1 s tick.</summary>
    public static readonly TimeSpan MinTimeSampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>The coarsest <see cref="TimeSampleInterval"/>.</summary>
    public static readonly TimeSpan MaxTimeSampleInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long durable WAL may wait in the active segment before the archiver asks for a switch, so it can be archived: the recovery point objective
    /// after losing the database volume. At least <see cref="MinMaxSegmentAge"/>.
    /// </summary>
    public required TimeSpan MaxSegmentAge { get; init; }

    /// <summary>
    /// Bytes of sealed WAL the engine keeps on disk for the archive alone. Past it the checkpoint reclaims as if archiving were off and the archive
    /// records a gap. At least two segments (<see cref="WalWriterOptions.SegmentSize"/>), so one segment waiting for its copy never trips it.
    /// </summary>
    public required long BacklogLimitBytes { get; init; }

    /// <summary>The resolution of restore-to-time: how often the durable LSN is sampled against the clock. Between 1 s and 60 s.</summary>
    public TimeSpan TimeSampleInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a closing engine spends archiving what its last session wrote (a switch, then every sealed segment) before it lets go. Zero: no final
    /// pass, the next open archives what is left.
    /// </summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Checks the bounds against the WAL's configuration and the store's path.</summary>
    /// <param name="wal">The engine's WAL options (segment size).</param>
    /// <param name="storePath">The backup store the archive is written to.</param>
    /// <param name="error">What is wrong, or <c>null</c>.</param>
    /// <returns><c>true</c> when the options can be used.</returns>
    public bool Validate(WalWriterOptions wal, string storePath, out string error)
    {
        ArgumentNullException.ThrowIfNull(wal);
        if (MaxSegmentAge < MinMaxSegmentAge)
        {
            error = MaxSegmentAge == TimeSpan.Zero
                ? "ArchiveOptions.MaxSegmentAge must be set: it is the recovery point objective (at least 5 s)."
                : $"ArchiveOptions.MaxSegmentAge is {MaxSegmentAge}; it must be at least {MinMaxSegmentAge}.";
            return false;
        }

        var minBacklog = 2L * wal.SegmentSize;
        if (BacklogLimitBytes < minBacklog)
        {
            error = BacklogLimitBytes == 0
                ? $"ArchiveOptions.BacklogLimitBytes must be set: the WAL bytes the engine may hold for the archive (at least {minBacklog}, two segments)."
                : $"ArchiveOptions.BacklogLimitBytes is {BacklogLimitBytes}; it must be at least {minBacklog} (two {wal.SegmentSize}-byte segments), "
                  + "or a single segment waiting for its copy would release the archive.";
            return false;
        }

        if (TimeSampleInterval < MinTimeSampleInterval || TimeSampleInterval > MaxTimeSampleInterval)
        {
            error = $"ArchiveOptions.TimeSampleInterval is {TimeSampleInterval}; it must lie between {MinTimeSampleInterval} and {MaxTimeSampleInterval}.";
            return false;
        }

        if (CloseTimeout < TimeSpan.Zero)
        {
            error = $"ArchiveOptions.CloseTimeout is {CloseTimeout}; it cannot be negative (zero: no final pass at close).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(storePath))
        {
            error = "WAL archiving needs a backup store path.";
            return false;
        }

        if (wal.WalDirectory != null && IsUnder(Path.GetFullPath(storePath), Path.GetFullPath(wal.WalDirectory)))
        {
            error = $"The backup store '{storePath}' lies inside the WAL directory '{wal.WalDirectory}': an archive must not live beside what it copies.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsUnder(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var p = path.TrimEnd('/', '\\');
        var d = directory.TrimEnd('/', '\\');
        return string.Equals(p, d, comparison)
               || (p.Length > d.Length && p.StartsWith(d, comparison) && (p[d.Length] == '/' || p[d.Length] == '\\'));
    }
}
