using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>How to back up a closed database (plan 1413 §2.12; the public shape is the facade's, #1444).</summary>
internal sealed class CreateOptions
{
    public static readonly CreateOptions Default = new();

    /// <summary>Force a full point even when an incremental would do.</summary>
    public bool Full { get; init; }

    /// <summary>The database's WAL directory when it is not <c>{bundle}/wal</c> (an engine configured with an explicit one, DC-4).</summary>
    public string WalDirectory { get; init; }

    public CancellationToken Cancellation { get; init; }

    public ILogger Logger { get; init; }

    /// <summary>Test seam: the store's file system. Null in production.</summary>
    internal IStoreFileSystem StoreFileSystem { get; init; }
}

/// <summary>
/// Backs up a database no engine holds (02 §8, store side): the data file IS the frozen image, so the capture is a plain read under an exclusion an engine
/// cannot break — the advisory lock taken atomically and the data file held open with no sharing (DC-17), which keeps an engine's open out on Windows and
/// Linux alike. The tail is every WAL segment whose records may reach <c>C</c>, and always the newest one, so an unclean image carries what its recovery
/// needs (DC-3). A clean image with a backup-state keeps its lineage: the next point, offline or online, can be incremental.
/// </summary>
internal static class OfflineCapture
{
    private const int RunPages = 32;

    /// <summary>Test seam: opens the data file (path → handle) in place of the exclusive open. Null in production.</summary>
    [ThreadStatic]
    internal static Func<string, SafeFileHandle> OpenDataFileForTest;

    /// <summary>
    /// Captures one point of the bundle at <paramref name="bundle"/> into the store at <paramref name="storePath"/>. Throws
    /// <see cref="DatabaseLockedException"/> when a process holds the database, <see cref="StorageException"/> when the bundle cannot be backed up as
    /// configured, and the store's exceptions.
    /// </summary>
    public static BackupPointInfo Create(string bundle, string storePath, CreateOptions options = null)
    {
        options ??= CreateOptions.Default;
        var log = options.Logger ?? NullLogger.Instance;
        bundle = OfflineBundlePageSource.ResolveBundleDirectory(bundle);

        // 1 LOCK — advisory, atomic; then the data file with no sharing: the OS-enforced exclusion (an engine opens it ReadWrite).
        if (!DatabaseLockFile.TryAcquire(bundle, yieldable: false, out var lockInfo))
        {
            throw new DatabaseLockedException(bundle, lockInfo.Pid, lockInfo.MachineName, lockInfo.StartedAt);
        }

        try
        {
            // A restored bundle adopts its new timeline at its first open (#1440); captured before, its point would carry the old one.
            if (File.Exists(Path.Combine(bundle, RestoreMarker.FileName)))
            {
                throw new StorageException(TyphonErrorCode.InvalidDatabaseBundle,
                    $"'{bundle}' is a restore not opened yet ({RestoreMarker.FileName}): open it once with this build so it adopts its new timeline, "
                    + "then back it up.");
            }

            SafeFileHandle data;
            var dataPath = Path.Combine(bundle, IntegrityConstants.DataFileName);
            try
            {
                data = OpenDataFileForTest?.Invoke(dataPath) ?? File.OpenHandle(dataPath, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException e) when (StoreFileSystem.IsSharingViolation(e))
            {
                // Only another handle on the data file means "locked" (an engine with no db.lock, A9); any other failure is the I/O failure it is.
                throw new DatabaseLockedException(bundle, 0, "unknown", DateTimeOffset.MinValue, e);
            }

            using (data)
            {
                return Capture(bundle, storePath, data, options, log);
            }
        }
        finally
        {
            DatabaseLockFile.Release(bundle, lockInfo);
        }
    }

    private static BackupPointInfo Capture(string bundle, string storePath, SafeFileHandle data, CreateOptions options, ILogger log)
    {
        // 2 IMAGE — what the data file says about itself.
        var length = RandomAccess.GetLength(data);
        if (length % PackFormat.PageSize != 0)
        {
            throw new StorageException(TyphonErrorCode.InvalidDatabaseBundle, $"'{bundle}': the data file is {length} bytes, not whole pages.");
        }

        var n = (int)(length / PackFormat.PageSize);
        using var source = new HandlePageSource(data, n, bundle);
        var view = BootstrapReader.Read(source);
        if (!view.IsUsable)
        {
            throw new StorageException(TyphonErrorCode.InvalidDatabaseBundle, $"'{bundle}': its meta pair is unusable; check it with 'typhon check'.");
        }

        var (c, clean) = view.ReadWatermarks();
        var databaseId = view.TryGet(DatabaseEngine.BK_DatabaseId, out var id) ? BackupImageMeta.UnpackGuid(id) : Guid.Empty;
        var timelineId = view.TryGet(DatabaseEngine.BK_TimelineId, out var tl) ? BackupImageMeta.UnpackGuid(tl) : Guid.Empty;
        var timelineParent = view.TryGet(DatabaseEngine.BK_TimelineParent, out var tp) ? BackupImageMeta.UnpackGuid(tp) : Guid.Empty;
        var nonce = view.TryGet(DurabilityWatermarks.BackupStateNonceKey, out var nv) ? (ulong)nv.AsLong : 0UL;
        if (databaseId == Guid.Empty)
        {
            throw new StorageException(TyphonErrorCode.InvalidDatabaseBundle, $"'{bundle}' records no database id: open it once with this build first.");
        }

        // 3 WAL — DC-4: an unclean image needs its log; an engine configured elsewhere kept it elsewhere.
        var walDir = options.WalDirectory ?? Path.Combine(bundle, IntegrityConstants.WalDirectoryName);
        var walIo = new WalFileIO();
        if (!clean && (!Directory.Exists(walDir) || walIo.EnumerateSegmentPaths(walDir).Count == 0))
        {
            throw new StorageException(TyphonErrorCode.InvalidDatabaseBundle,
                $"'{bundle}' was not closed cleanly and needs its WAL to be restorable, but '{walDir}' holds none. If its engine used another "
                + "WalDirectory, pass it (typhon backup create --wal-dir).");
        }

        // 4 STORE + ADMISSION — a trusted lineage only from a backup-state this very image's meta vouches for (its nonce), at this length.
        var statePath = BackupStateFile.PathIn(bundle);
        BackupStateContents state = null;
        if (clean && nonce != 0)
        {
            state = BackupStateFile.TryRead(statePath, nonce, out _);
            if (state != null && (state.DatabaseId != databaseId || state.TimelineId != timelineId || state.DataFilePages != n))
            {
                state = null;
            }
        }

        var lineage = new BackupLineage();
        if (state != null)
        {
            lineage.Trusted = true;
            lineage.StoreId = state.StoreId;
            lineage.ChainId = state.ChainId;
            lineage.LastBackupId = state.LastBackupId;
            lineage.LastPointLsn = state.LastPointLsn;
        }

        using var session = BackupStore.OpenForWrite(storePath, databaseId, createIfMissing: true, options.StoreFileSystem, log);
        var kind = BackupAdmission.Decide(options.Full ? BackupKind.Full : BackupKind.Incremental, lineage, session, timelineId, timelineParent,
            out var parent);
        var plan = new BackupPointPlan(databaseId, timelineId, kind, kind == BackupKind.Incremental ? lineage.ChainId : Guid.Empty,
            kind == BackupKind.Incremental ? parent.BackupId : 0, view.FormatRevision, $"Typhon {typeof(OfflineCapture).Assembly.GetName().Version}",
            Offline: true);

        // 5 PACK — the image's pages (all, or the map's plus the meta pair), then the tail, then P.
        var sink = session.BeginPoint(plan);
        BackupPointInfo point;
        try
        {
            sink.SetImage(n, c, DateTime.UtcNow.Ticks);
            AddPages(sink, data, n, kind == BackupKind.Incremental ? state.Map : null, options.Cancellation);
            using var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "OfflineCapture" });
            using var allocator = new MemoryAllocator(registry, new MemoryAllocatorOptions { Name = "OfflineCapture" });
            var tail = WalTailInspector.CaptureTailOffline(walIo, walDir, c, allocator, registry.Allocation, sink, options.Cancellation);
            point = sink.Complete(tail.PointLsn, DateTime.UtcNow.Ticks);
        }
        catch
        {
            sink.Abort();
            throw;
        }

        // 6 LINEAGE — a clean image keeps a backup-state its meta vouches for, now naming this point and an empty map: the file is unchanged since.
        if (clean && nonce != 0)
        {
            var previous = state ?? new BackupStateContents { Nonce = nonce, DatabaseId = databaseId, TimelineId = timelineId, DataFilePages = n };
            BackupStateFile.Rewrite(statePath, previous, session.StoreId, point.ChainId, point.BackupId, point.PointLsn, flushToDisk: true);
        }

        return point;
    }

    /// <summary>Reads pages in runs of up to 32 and hands each to the sink; <paramref name="map"/> null means every page.</summary>
    private static void AddPages(IBackupPointSink sink, SafeFileHandle data, int n, ulong[] map, CancellationToken ct)
    {
        var buffer = new byte[RunPages * PackFormat.PageSize];
        var p = 0;
        while (p < n)
        {
            ct.ThrowIfCancellationRequested();
            if (map != null && p >= 2 && !IsSet(map, p))
            {
                p++;
                continue;
            }

            var run = 1;
            while (run < RunPages && p + run < n && (map == null || p + run < 2 || IsSet(map, p + run)))
            {
                run++;
            }

            var bytes = buffer.AsSpan(0, run * PackFormat.PageSize);
            ReadExactly(data, bytes, (long)p * PackFormat.PageSize);
            for (var i = 0; i < run; i++)
            {
                var page = bytes.Slice(i * PackFormat.PageSize, PackFormat.PageSize);
                var suspect = page.IndexOfAnyExcept((byte)0) >= 0 && !PagedMMF.VerifyPageImage(page, out _);
                sink.AddPage(p + i, page, suspect);
            }

            p += run;
        }
    }

    private static bool IsSet(ulong[] map, int page) => (page >> 6) < map.Length && (map[page >> 6] & (1UL << (page & 63))) != 0;

    private static void ReadExactly(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(handle, destination[total..], offset + total);
            if (read == 0)
            {
                throw new IOException($"The data file ended at {offset + total} bytes while it was being backed up.");
            }

            total += read;
        }
    }

    /// <summary>The data file through the exclusive handle the capture holds (a second open, even ours, would be refused).</summary>
    private sealed class HandlePageSource(SafeFileHandle handle, int pageCount, string describe) : IPageSource
    {
        public int PageCount => pageCount;

        public bool TryReadPage(int index, Span<byte> destination)
        {
            if ((uint)index >= (uint)pageCount)
            {
                return false;
            }

            ReadExactly(handle, destination[..PackFormat.PageSize], (long)index * PackFormat.PageSize);
            return true;
        }

        public string Describe() => describe;

        public void Dispose()
        {
        }
    }
}
