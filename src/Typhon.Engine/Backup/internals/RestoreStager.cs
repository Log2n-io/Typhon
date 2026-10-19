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

    /// <summary>Runs a restore. Throws <see cref="RestoreRefusedException"/> (nothing written), <see cref="BackupChainBrokenException"/>,
    /// <see cref="BackupPackCorruptException"/> (staging discarded).</summary>
    public static RestoreOutcome Run(string storePath, PointSelector point, string target, RestoreOptions options = null, IStoreFileSystem fs = null)
    {
        options ??= RestoreOptions.Default;
        fs ??= StoreFileSystem.Instance;
        var log = options.Logger ?? NullLogger.Instance;
        var clock = Stopwatch.StartNew();

        // 1 RESOLVE — the chain, every header, footer and INDEX, and what the image says about itself.
        var view = StoreView.Open(storePath, fs, log);
        var chain = ChainResolver.Resolve(view, ChainResolver.Select(view, point));
        var packs = PackChainPageSource.OpenPacks(fs, view, chain);
        try
        {
            var meta = ReadImageMeta(fs, view, chain, packs);

            // 2 TARGET — the bundle the image can be opened as (DC-1), and a format this build opens (DC-12).
            target = ResolveTarget(target, meta.DatabaseName);
            if (meta.FormatRevision != PagedMMF.DatabaseFormatRevision && !options.AllowFormatMismatch)
            {
                Refuse(log, RestoreRefusalReason.FormatRevision, target,
                    $"the image is at on-disk format revision {meta.FormatRevision}, this build opens {PagedMMF.DatabaseFormatRevision}; restore it "
                    + "with a build of that revision, or pass AllowFormatMismatch to restore it for one");
            }

            // 3 GUARD — refusals come before any byte is written.
            var staging = target + StagingSuffix;
            var exists = Guard(fs, log, target, staging, options);

            // 4 STAGE
            var newTimeline = Guid.NewGuid();
            Staged staged;
            try
            {
                staged = Stage(fs, view, chain, packs, meta, staging, newTimeline, options);
            }
            catch
            {
                // A failed staging is ours to discard (it still carries restore.staging); a crash leaves it for the next restore to.
                TryDiscardStaging(fs, staging);
                throw;
            }

            BackupLog.RestoreStaged(log, chain.Point.BackupId, staged.Materialized.Pages, staged.TailSegments, clock.ElapsedMilliseconds);

            // 5 SWAP
            var displaced = Swap(fs, target, staging, meta.DatabaseName, exists, options);
            BackupLog.RestorePublished(log, target, displaced ?? "none");

            // 6 SETTLE
            options.OpenAndClose?.Invoke(target);

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

    /// <summary>Returns whether the target exists (and may be replaced).</summary>
    private static bool Guard(IStoreFileSystem fs, ILogger log, string target, string staging, RestoreOptions options)
    {
        var exists = fs.DirectoryExists(target) || fs.FileExists(target);
        if (exists)
        {
            // Both: a lock file naming a live process, and the OS refusing an exclusive open of the data file (DC-16). Checked before the
            // replace question: a live database is refused whatever the caller asked for.
            if (fs.IsBundleHeldByLiveProcess(target) || !fs.ProbeExclusive(Path.Combine(target, IntegrityConstants.DataFileName)))
            {
                Refuse(log, RestoreRefusalReason.TargetLocked, target, "a live process holds this database; close it first");
            }

            if (!options.Replace)
            {
                Refuse(log, RestoreRefusalReason.TargetExists, target, "it exists; pass Replace to move it aside (it is never deleted)");
            }
        }

        if (fs.DirectoryExists(staging) || fs.FileExists(staging))
        {
            if (!fs.FileExists(Path.Combine(staging, RestoreMarker.StagingFileName)))
            {
                Refuse(log, RestoreRefusalReason.ForeignStaging, target,
                    $"'{staging}' exists and was not left by a restore (no {RestoreMarker.StagingFileName}); move or delete it yourself");
            }

            // An interrupted restore's own leftover: the only directory a restore ever deletes.
            fs.DeleteDirectory(staging);
        }

        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent) && !fs.DirectoryExists(parent))
        {
            fs.CreateDirectory(parent);
        }

        return exists;
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

        // restore.json, then every file and directory flushed before the staging marker goes.
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

        fs.DeleteFile(stagingMarker);
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

    private static string Swap(IStoreFileSystem fs, string target, string staging, string databaseName, bool exists, RestoreOptions options)
    {
        string displaced = null;
        if (exists)
        {
            var parent = Path.GetDirectoryName(target) ?? "";
            var stamp = options.UtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            displaced = Path.Combine(parent, databaseName + IntegrityConstants.BundleExtension + DisplacedInfix + stamp);
            for (var i = 2; fs.DirectoryExists(displaced) || fs.FileExists(displaced); i++)
            {
                displaced = Path.Combine(parent, databaseName + IntegrityConstants.BundleExtension + DisplacedInfix + stamp + "-" + i);
            }

            StoreFileSystem.MoveDurable(fs, target, displaced, replace: false);
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
