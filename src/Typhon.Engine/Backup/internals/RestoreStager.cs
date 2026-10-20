using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>How to restore (plan 1413 §2.13; the public shape is the facade's, #1444).</summary>
internal sealed class RestoreOptions
{
    public static readonly RestoreOptions Default = new();

    /// <summary>Move an existing target aside to <c>{name}.typhon.pre-restore-{utc}</c> instead of refusing. Never deletes it.</summary>
    public bool Replace { get; init; }

    /// <summary>Restore an image of another on-disk format revision (for another build's environment); refused by default (DC-12).</summary>
    public bool AllowFormatMismatch { get; init; }

    /// <summary>Forensics only: write pages that fail their check and list them (04 §5).</summary>
    public bool AllowDamaged { get; init; }

    /// <summary>Settle: called with the restored bundle once it is published, to open it (recovery runs) and close it.</summary>
    public Action<string> OpenAndClose { get; init; }

    public long ReadBudgetBytes { get; init; } = 64L << 20;

    public CancellationToken Cancellation { get; init; }

    /// <summary>Pages written so far, out of the image's.</summary>
    public Action<long, long> Progress { get; init; }

    public ILogger Logger { get; init; }

    /// <summary>The clock naming a displaced bundle; a test seam.</summary>
    public Func<DateTime> UtcNow { get; init; } = static () => DateTime.UtcNow;
}

/// <summary>What a restore did.</summary>
internal sealed class RestoreOutcome
{
    public uint PointId { get; init; }

    public BackupKind Kind { get; init; }

    public long CheckpointLsn { get; init; }

    public long PointLsn { get; init; }

    public string Target { get; init; }

    /// <summary>Where the replaced bundle now is, or <c>null</c>.</summary>
    public string DisplacedBundle { get; init; }

    public int ChainDepth { get; init; }

    public int PagesWritten { get; init; }

    public int ZeroPages { get; init; }

    public int TailSegments { get; init; }

    public long BytesRead { get; init; }

    public long BytesWritten { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>Pages the LIVE database had damaged at capture (a warning: the backup faithfully holds the damage).</summary>
    public IReadOnlyList<uint> SuspectPages { get; init; }

    /// <summary>Pages written despite failing their check (only with <see cref="RestoreOptions.AllowDamaged"/>).</summary>
    public IReadOnlyList<DamagedPage> DamagedPages { get; init; }

    public Guid NewTimelineId { get; init; }

    /// <summary>
    /// What <see cref="RestoreOptions.OpenAndClose"/> threw, or <c>null</c>. The restore itself is published by then: the outcome, the displaced
    /// bundle included, is still the caller's to act on.
    /// </summary>
    public Exception SettleFailure { get; init; }
}

/// <summary>
/// Restores a point into a bundle (plan 1413 §2.8, 04 §2): resolve, name the target as the image names itself (DC-1), refuse a live, foreign
/// or format-mismatched target, stage the bundle privately, then publish it by a durable rename. A restore never deletes a database: an
/// existing target is moved aside, and only a staging directory carrying the tool's own marker is ever removed.
/// </summary>
internal static class RestoreStager
{
    public const string StagingSuffix = ".restoring";
    public const string DisplacedInfix = ".pre-restore-";

    /// <summary>Beside the staging: held by the one restore working on a target, from before its guard to after its settle.</summary>
    public const string LockSuffix = ".restoring.lock";

    /// <summary>Runs a restore. Throws <see cref="RestoreRefusedException"/> (nothing written), <see cref="BackupChainBrokenException"/>,
    /// <see cref="BackupPackCorruptException"/> (staging discarded).</summary>
    public static RestoreOutcome Run(string storePath, PointSelector point, string target, RestoreOptions options = null, IStoreFileSystem fs = null)
    {
        options ??= RestoreOptions.Default;
        fs ??= StoreFileSystem.Instance;
        var log = options.Logger ?? NullLogger.Instance;
        var clock = Stopwatch.StartNew();

        // 1 RESOLVE — the chain, every header, footer and INDEX, and what the image says about itself.
        // The packs are the authority (D7): a restore resolves from their headers, never from the catalog cache.
        var view = StoreView.Open(storePath, fs, log, trustCatalog: false);
        var chain = ChainResolver.Resolve(view, ChainResolver.Select(view, point));
        var packs = PackChainPageSource.OpenPacks(fs, view, chain);
        try
        {
            var meta = ReadImageMeta(fs, view, chain, packs);

            // 2 TARGET — the bundle the image can be opened as (DC-1), and a format this build opens (DC-12). Absolute, so no parent is ever "".
            target = ResolveTarget(fs.GetFullPath(target), meta.DatabaseName);
            if (meta.FormatRevision != PagedMMF.DatabaseFormatRevision && !options.AllowFormatMismatch)
            {
                Refuse(log, RestoreRefusalReason.FormatRevision, target,
                    $"the image is at on-disk format revision {meta.FormatRevision}, this build opens {PagedMMF.DatabaseFormatRevision}; restore it "
                    + "with a build of that revision, or pass AllowFormatMismatch to restore it for one");
            }

            // 3 GUARD — refusals come before any byte is written. One restore per target: the lock beside the staging is held to the end, so a
            // staging found with no holder is a dead restore's, the only directory a restore ever deletes.
            var staging = target + StagingSuffix;
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent) && !fs.DirectoryExists(parent))
            {
                fs.CreateDirectory(parent);
            }

            using var restoreLock = fs.TryLockTransient(target + LockSuffix);
            if (restoreLock == null)
            {
                Refuse(log, RestoreRefusalReason.ForeignStaging, target, "another restore to this target is running");
            }

            using var hold = Guard(fs, log, target, staging, options);

            // 4 STAGE
            var newTimeline = Guid.NewGuid();
            Staged staged;
            try
            {
                staged = Stage(fs, view, chain, packs, meta, staging, newTimeline, options);
                Recheck(fs, log, target, hold);
            }
            catch
            {
                // A failed staging is ours to discard (it still carries restore.staging); a crash leaves it for the next restore to.
                TryDiscardStaging(fs, staging);
                throw;
            }

            BackupLog.RestoreStaged(log, chain.Point.BackupId, staged.Materialized.Pages, staged.TailSegments, clock.ElapsedMilliseconds);

            // 5 SWAP, then the staging marker goes: until the bundle is published, a crash leaves a staging the next restore recognises (04 §2).
            var displaced = Swap(fs, target, staging, meta.DatabaseName, hold, options);
            fs.DeleteFile(Path.Combine(target, RestoreMarker.StagingFileName));
            fs.FlushDirectory(target);
            BackupLog.RestorePublished(log, target, displaced ?? "none");

            // 6 SETTLE — the restore is published: a failure to open it is reported with the outcome, never instead of it.
            Exception settleFailure = null;
            try
            {
                options.OpenAndClose?.Invoke(target);
            }
            catch (Exception ex)
            {
                settleFailure = ex;
            }

            return new RestoreOutcome
            {
                PointId = chain.Point.BackupId,
                Kind = chain.Point.Kind,
                CheckpointLsn = chain.Point.CheckpointLsn,
                PointLsn = chain.Point.PointLsn,
                Target = target,
                DisplacedBundle = displaced,
                ChainDepth = chain.Depth,
                PagesWritten = staged.Materialized.Pages,
                ZeroPages = staged.Materialized.ZeroPages,
                TailSegments = staged.TailSegments,
                BytesRead = staged.Materialized.BytesRead,
                BytesWritten = staged.Materialized.BytesWritten + staged.TailBytes,
                Duration = clock.Elapsed,
                SuspectPages = staged.Suspects,
                DamagedPages = staged.Materialized.Damaged,
                NewTimelineId = newTimeline,
                SettleFailure = settleFailure,
            };
        }
        finally
        {
            foreach (var p in packs)
            {
                p.Dispose();
            }
        }
    }

    /// <summary>The bundle a restore of an image named <paramref name="databaseName"/> goes to: <paramref name="target"/> itself when it is a
    /// <c>.typhon</c> path (its stem must be the name), else <c>{target}/{name}.typhon</c>.</summary>
    public static string ResolveTarget(string target, string databaseName)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        var trimmed = target.TrimEnd('/', '\\');
        if (trimmed.EndsWith(IntegrityConstants.BundleExtension, StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(trimmed);
            if (!string.Equals(stem, databaseName, StringComparison.Ordinal))
            {
                throw new RestoreRefusedException(RestoreRefusalReason.NameMismatch, trimmed,
                    $"the image is database '{databaseName}', and an open refuses a bundle named otherwise; restore to "
                    + $"'{databaseName}{IntegrityConstants.BundleExtension}' or to a directory");
            }

            return trimmed;
        }

        return Path.Combine(trimmed, databaseName + IntegrityConstants.BundleExtension);
    }

    private static BackupImageMeta ReadImageMeta(IStoreFileSystem fs, StoreView view, ResolvedChain chain, PackReader[] packs)
    {
        BackupImageMeta meta;
        using (var source = PackChainPageSource.Open(fs, view, chain))
        {
            meta = BackupImageMeta.Read(source);
        }

        var point = packs[^1];
        if (!meta.Usable)
        {
            PackFormat.ThrowCorrupt(point.Path, "Page", 0, $"the image's meta pair is unusable: {meta.Problem}");
        }

        if (meta.DatabaseId != view.Identity.DatabaseId)
        {
            PackFormat.ThrowCorrupt(point.Path, "Page", 0, $"the image belongs to database {meta.DatabaseId}, the store to {view.Identity.DatabaseId}");
        }

        if (meta.CheckpointLsn != point.Header.CheckpointLsn)
        {
            PackFormat.ThrowCorrupt(point.Path, "Page", 0, $"the image records CheckpointLSN {meta.CheckpointLsn}, the pack {point.Header.CheckpointLsn}");
        }

        return meta;
    }

    /// <summary>
    /// What a restore holds of an existing target while it works: the bundle's advisory lock (an engine refuses to open it) and its data file with no
    /// sharing (anything else is kept out). Nothing for a target that does not exist yet.
    /// </summary>
    private sealed class TargetHold(IStoreFileSystem fs, string target) : IDisposable
    {
        public bool Exists { get; init; }

        public DatabaseLockFile.LockInfo Lock { get; init; }

        public IDisposable DataFile { get; set; }

        /// <summary>Where the bundle, and the lock in it, now is: the displaced path once moved aside.</summary>
        public string LockedAt { get; set; } = target;

        /// <summary>A directory with a file open under it cannot be renamed on Windows: the data file is let go just before the swap.</summary>
        public void ReleaseDataFile()
        {
            DataFile?.Dispose();
            DataFile = null;
        }

        public void Dispose()
        {
            ReleaseDataFile();
            if (Exists)
            {
                fs.ReleaseBundleLock(LockedAt, Lock);
            }
        }
    }

    /// <summary>Refuses a live, unreplaceable or foreign target and removes a dead restore's staging; holds an existing target until the swap.</summary>
    private static TargetHold Guard(IStoreFileSystem fs, ILogger log, string target, string staging, RestoreOptions options)
    {
        var data = Path.Combine(target, IntegrityConstants.DataFileName);
        TargetHold hold;
        if (fs.DirectoryExists(target) || fs.FileExists(target))
        {
            // Both, for the whole restore: the advisory lock an engine honours, and the OS refusing a second exclusive open of the data file (DC-16).
            // Checked before the replace question: a live database is refused whatever the caller asked for.
            if (!fs.TryAcquireBundleLock(target, out var acquired))
            {
                Refuse(log, RestoreRefusalReason.TargetLocked, target, "a live process holds this database; close it first");
            }

            hold = new TargetHold(fs, target) { Exists = true, Lock = acquired };
            if (fs.FileExists(data))
            {
                hold.DataFile = fs.TryLockExclusive(data);
                if (hold.DataFile == null)
                {
                    hold.Dispose();
                    Refuse(log, RestoreRefusalReason.TargetLocked, target, "a live process holds this database; close it first");
                }
            }

            if (!options.Replace)
            {
                hold.Dispose();
                Refuse(log, RestoreRefusalReason.TargetExists, target, "it exists; pass Replace to move it aside (it is never deleted)");
            }
        }
        else
        {
            hold = new TargetHold(fs, target);
        }

        if (fs.DirectoryExists(staging) || fs.FileExists(staging))
        {
            if (!fs.FileExists(Path.Combine(staging, RestoreMarker.StagingFileName)))
            {
                hold.Dispose();
                Refuse(log, RestoreRefusalReason.ForeignStaging, target,
                    $"'{staging}' exists and was not left by a restore (no {RestoreMarker.StagingFileName}); move or delete it yourself");
            }

            // A dead restore's leftover (a live one would hold the lock): the only directory a restore ever deletes.
            fs.DeleteDirectory(staging);
        }

        return hold;
    }

    /// <summary>
    /// Right before the swap, with the data file let go: the target is still what the guard decided on. One that appeared during staging is not
    /// replaced; one whose data file another process has since opened is refused.
    /// </summary>
    private static void Recheck(IStoreFileSystem fs, ILogger log, string target, TargetHold hold)
    {
        hold.ReleaseDataFile();
        var exists = fs.DirectoryExists(target) || fs.FileExists(target);
        if (exists && !hold.Exists)
        {
            Refuse(log, RestoreRefusalReason.TargetExists, target, "it appeared while the restore was staging; nothing was replaced");
        }

        if (exists && !fs.ProbeExclusive(Path.Combine(target, IntegrityConstants.DataFileName)))
        {
            Refuse(log, RestoreRefusalReason.TargetLocked, target, "a process opened this database while the restore was staging; close it first");
        }
    }

    private sealed class Staged
    {
        public MaterializeResult Materialized;
        public int TailSegments;
        public long TailBytes;
        public IReadOnlyList<uint> Suspects;
    }

    private static Staged Stage(IStoreFileSystem fs, StoreView view, ResolvedChain chain, PackReader[] packs, in BackupImageMeta meta, string staging,
        Guid newTimeline, RestoreOptions options)
    {
        fs.CreateDirectory(staging);
        var stagingMarker = Path.Combine(staging, RestoreMarker.StagingFileName);
        WriteFile(fs, stagingMarker, JsonSerializer.SerializeToUtf8Bytes(
            new RestoreStagingFile { RestoreId = Guid.NewGuid(), StartedUtc = DateTime.UtcNow, Pid = Environment.ProcessId },
            RestoreJsonContext.Default.RestoreStagingFile));

        // data ← the image, N × 8 KiB exactly (DC-11).
        var point = packs[^1];
        var n = point.Header.ImagePages;
        MaterializeResult materialized;
        using (var data = fs.OpenCreateNew(Path.Combine(staging, IntegrityConstants.DataFileName), (long)n * PackFormat.PageSize))
        {
            materialized = ChainMaterializer.Materialize(packs, data, new MaterializeOptions
            {
                ReadBudgetBytes = options.ReadBudgetBytes,
                AllowDamaged = options.AllowDamaged,
                Cancellation = options.Cancellation,
                Progress = options.Progress == null ? null : written => options.Progress(written, n),
            });
        }

        // wal/ ← the point's tail. A point's tail is its own: older packs' tails belong to older points.
        var walDir = Path.Combine(staging, IntegrityConstants.WalDirectoryName);
        fs.CreateDirectory(walDir);
        var tail = point.LoadTailDirectory();
        if (tail.Segments.Length == 0 && !meta.CleanShutdown)
        {
            // DC-3: with no *.wal the open would take the clean path over an image that needs replay.
            PackFormat.ThrowCorrupt(point.Path, "Tail", -1, "the image needs crash recovery but its point carries no WAL segment");
        }

        long tailBytes = 0;
        for (var i = 0; i < tail.Segments.Length; i++)
        {
            WriteWalSegment(fs, point, tail, i, walDir);
            tailBytes += tail.Segments[i].RawLength;
        }

        // restore.json, then every file and directory flushed. The staging marker stays until the bundle is published.
        var marker = new RestoreMarkerFile
        {
            FormatVersion = RestoreMarkerFile.CurrentFormatVersion,
            DatabaseId = view.Identity.DatabaseId,
            NewTimelineId = newTimeline,
            RestoredUtc = DateTime.UtcNow,
            Tool = $"Typhon {typeof(RestoreStager).Assembly.GetName().Version}",
            RestoredFrom = new RestoredFrom
            {
                StoreId = view.Identity.StoreId,
                ChainId = chain.Point.ChainId,
                BackupId = chain.Point.BackupId,
                Kind = chain.Point.Kind,
                CheckpointLsn = chain.Point.CheckpointLsn,
                PointLsn = chain.Point.PointLsn,
                TimelineId = chain.Point.TimelineId,
            },
        };
        WriteFile(fs, Path.Combine(staging, RestoreMarker.FileName), RestoreMarker.Serialize(marker));
        fs.FlushDirectory(walDir);
        fs.FlushDirectory(staging);

        var suspects = new SortedSet<uint>();
        foreach (var p in packs)
        {
            if (p.Footer.SuspectCount > 0)
            {
                suspects.UnionWith(p.LoadSuspects());
            }
        }

        return new Staged { Materialized = materialized, TailSegments = tail.Segments.Length, TailBytes = tailBytes, Suspects = [.. suspects] };
    }

    /// <summary>
    /// Recreates tail segment <paramref name="index"/> as <c>{SegmentId:D16}.wal</c>: its copied bytes (RawCrc and WAL header checked), then
    /// zeros up to the size its header declares (DC-2) — the reader reads the declared region and ignores a short read, so a shorter file would
    /// hand it a stale buffer past end of file.
    /// </summary>
    public static void WriteWalSegment(IStoreFileSystem fs, PackReader point, TailDirectory tail, int index, string walDir)
    {
        var segment = tail.Segments[index];
        var path = Path.Combine(walDir, segment.SegmentId.ToString("D16", CultureInfo.InvariantCulture) + ".wal");
        using var file = fs.OpenCreateNew(path);
        var declared = point.ReadTailSegment(tail, index, file);
        file.SetLength(declared);
        file.Flush();
    }

    private static string Swap(IStoreFileSystem fs, string target, string staging, string databaseName, TargetHold hold, RestoreOptions options)
    {
        string displaced = null;
        if (hold.Exists)
        {
            var parent = Path.GetDirectoryName(target);
            var stamp = options.UtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            displaced = Path.Combine(parent, databaseName + IntegrityConstants.BundleExtension + DisplacedInfix + stamp);
            for (var i = 2; fs.DirectoryExists(displaced) || fs.FileExists(displaced); i++)
            {
                displaced = Path.Combine(parent, databaseName + IntegrityConstants.BundleExtension + DisplacedInfix + stamp + "-" + i);
            }

            StoreFileSystem.MoveDurable(fs, target, displaced, replace: false);
            hold.LockedAt = displaced;
        }

        // A crash between the two moves leaves both directories and no target: the outcome and the runbook name them; nothing guesses (R6).
        StoreFileSystem.MoveDurable(fs, staging, target, replace: false);
        return displaced;
    }

    private static void TryDiscardStaging(IStoreFileSystem fs, string staging)
    {
        try
        {
            if (fs.FileExists(Path.Combine(staging, RestoreMarker.StagingFileName)))
            {
                fs.DeleteDirectory(staging);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next restore, which recognises it by its marker.
        }
    }

    private static void WriteFile(IStoreFileSystem fs, string path, ReadOnlySpan<byte> bytes)
    {
        using var file = fs.OpenCreateNew(path);
        file.Write(bytes, 0);
        file.Flush();
    }

    private static void Refuse(ILogger log, RestoreRefusalReason reason, string target, string detail)
    {
        BackupLog.RestoreRefused(log, reason, target);
        throw new RestoreRefusedException(reason, target, detail);
    }
}
