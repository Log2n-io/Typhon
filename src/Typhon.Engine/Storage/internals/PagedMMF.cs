using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

[PublicAPI]
public partial class PagedMMF : ResourceNode, IMemoryResource
{
    public const int DefaultMemPageCount = 256;

    #region Events

    internal event EventHandler CreatingEvent;
    internal event EventHandler LoadingEvent;

    #endregion

    #region Constants

    internal const int PageHeaderSize           = 192;                                  // Base Header + Metadata
    internal const int PageBaseHeaderSize       = 64;
    internal const int PageMetadataSize         = 128;
    internal const int PageSize                 = 8192;                                 // Base Header + Metadata + RawData
    internal const int PageRawDataSize          = PageSize - PageHeaderSize;
    internal const int PageSizePow2             = 13;                                   // 2^( PageSizePow2 = PageSize
    internal const int DatabaseFormatRevision   = 1;
    internal const ulong MinimumCacheSize       = DefaultMemPageCount * PageSize;
    internal const int WriteCachePageSize       = 1024 * 1024;

    #endregion

    #region Profiler async-completion wiring

    // Per-call state passed to the ContinueWith static handlers as a boxed struct. Boxing is the only allocation per tracked completion on top
    // of what ContinueWith itself already costs (the generated Task + continuation closure). We capture the begin-side SpanId + StartTimestamp
    // so the completion event can correlate back to the kickoff span and compute the full async duration as (completionTs - beginTs).
    private readonly record struct PageCacheReadCompletionState(ulong SpanId, long BeginTs, int FilePageIndex);
    private readonly record struct PageCacheWriteCompletionState(ulong SpanId, long BeginTs, int FilePageIndex);

    // Static delegates — one per completion kind. Cached in readonly static fields so ContinueWith doesn't allocate a delegate per call site;
    // only the state box is per-call. The `static` lambda modifier forbids captures, enforcing the "no closure" guarantee at compile time.
    // Func<Task<int>, object, int> rather than Action<Task<int>, object> because the wrapping continuation must preserve the int result
    // (the byte count from RandomAccess.ReadAsync) so callers awaiting the returned ValueTask<int> get the original value. Returning
    // task.Result re-throws any exception the read faulted with, propagating faults through the wrapper transparently.
    private static readonly Func<Task<int>, object, int> SReadCompletionHandler = static (task, stateObj) =>
    {
        var state = (PageCacheReadCompletionState)stateObj;
        TyphonEvent.EmitPageCacheDiskReadCompleted(state.SpanId, state.BeginTs, state.FilePageIndex, Stopwatch.GetTimestamp());
        return task.Result;
    };

    private static readonly Action<Task, object> SWriteCompletionHandler = static (_, stateObj) =>
    {
        var state = (PageCacheWriteCompletionState)stateObj;
        TyphonEvent.EmitPageCacheDiskWriteCompleted(state.SpanId, state.BeginTs, state.FilePageIndex, Stopwatch.GetTimestamp());
    };

    #endregion

    #region Debug Info


    [ExcludeFromCodeCoverage]
    private void GetMemPageExtraInfo(out Metrics.MemPageExtraInfo res)
    {
        int free = 0;
        int allocating = 0;
        int idleCount = 0;
        int exclusiveCount = 0;
        int dirtyCount = 0;
        int lockedByThreadCount = 0;
        int pendingIOReadCount = 0;
        int epochProtectedCount = 0;
        int slotRefPageCount = 0;
        int minClockSweepCounter = int.MaxValue;
        int maxClockSweepCounter = int.MinValue;

        var minActive = EpochManager?.MinActiveEpoch ?? long.MaxValue;

        foreach (var pi in _memPagesInfo)
        {
            switch (pi.PageState)
            {
                case PageState.Free:
                    free++;
                    break;
                case PageState.Allocating:
                    allocating++;
                    break;
                case PageState.Idle:
                    idleCount++;
                    break;
                case PageState.Exclusive:
                    exclusiveCount++;
                    break;
            }
            if (pi.DirtyCounter > 0)
            {
                dirtyCount++;
            }
            if (pi.PageExclusiveLatch.LockedByThreadId != 0)
            {
                lockedByThreadCount++;
            }
            if (pi.IOReadTask != null && pi.IOReadTask.IsCompleted == false)
            {
                pendingIOReadCount++;
            }
            if (pi.AccessEpoch >= minActive)
            {
                epochProtectedCount++;
            }
            if (pi.SlotRefCount > 0)
            {
                slotRefPageCount++;
            }
            if (pi.ClockSweepCounter < minClockSweepCounter)
            {
                minClockSweepCounter = pi.ClockSweepCounter;
            }
            if (pi.ClockSweepCounter > maxClockSweepCounter)
            {
                maxClockSweepCounter = pi.ClockSweepCounter;
            }
        }

        res = new Metrics.MemPageExtraInfo
        {
            FreeMemPageCount = free,
            AllocatingMemPageCount = allocating,
            IdleMemPageCount = idleCount,
            ExclusiveMemPageCount = exclusiveCount,
            DirtyPageCount = dirtyCount,
            LockedByThreadCount = lockedByThreadCount,
            PendingIOReadCount = pendingIOReadCount,
            MinClockSweepCounter = minClockSweepCounter,
            MaxClockSweepCounter = maxClockSweepCounter,
            BackpressureWaitCount = _metrics.BackpressureWaitCount,
            EpochProtectedPageCount = epochProtectedCount,
            SlotRefPageCount = slotRefPageCount
        };
    }

    private Metrics _metrics;

    internal Metrics GetMetrics() => _metrics;

    /// <summary>
    /// Produce a mutually-exclusive bucket classification of the page cache for the profiler's per-tick gauge snapshot.
    /// Called from <c>DagScheduler</c>'s end-of-tick hook when <c>TelemetryConfig.ProfilerGaugesActive</c> is <c>true</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Single linear pass over <see cref="_memPagesInfo"/> — O(MemPagesCount). At the default 256-page cache this is a few microseconds; at a 64K-page cache
    /// it runs in a fraction of a millisecond, well within the tick budget. Zero allocations (returns a struct by value). Branches ordered by expected
    /// frequency: Free → Idle-clean → Idle-dirty → Exclusive/Allocating.
    /// </para>
    /// <para>
    /// Uses plain (non-volatile) reads on purpose — snapshots have sampling semantics and microsecond-scale staleness on concurrent state transitions is
    /// acceptable for visualization. Invariant that matters: every page contributes to exactly one of the four buckets, so the stacked-area viewer never
    /// double-counts. The epoch/IO overlay counts are tracked separately and may add on top of the bucket totals.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PageCacheGaugeSnapshot GetGaugeSnapshot()
    {
        int free = 0;
        int cleanUsed = 0;
        int dirtyUsed = 0;
        int exclusive = 0;
        int epochProtected = 0;
        int pendingIoReads = 0;

        var minActive = EpochManager?.MinActiveEpoch ?? long.MaxValue;
        var pages = _memPagesInfo;
        if (pages == null)
        {
            return default;
        }

        for (var i = 0; i < pages.Length; i++)
        {
            var pi = pages[i];
            // Mutually-exclusive bucket classification — first match wins.
            switch (pi.PageState)
            {
                case PageState.Free:
                    free++;
                    break;
                case PageState.Idle:
                    if (pi.DirtyCounter > 0)
                    {
                        dirtyUsed++;
                    }
                    else
                    {
                        cleanUsed++;
                    }
                    break;
                case PageState.Exclusive:
                case PageState.Allocating:
                    exclusive++;
                    break;
            }

            // Overlay counts — independent of bucket, so a dirty page may also be epoch-protected.
            if (pi.AccessEpoch >= minActive)
            {
                epochProtected++;
            }
            var ioTask = pi.IOReadTask;
            if (ioTask != null && !ioTask.IsCompleted)
            {
                pendingIoReads++;
            }
        }

        return new PageCacheGaugeSnapshot(pages.Length, free, cleanUsed, dirtyUsed, exclusive, epochProtected, pendingIoReads);
    }

    #endregion

    internal enum PageState : ushort
    {
        Free         = 0,   // The page is free, yet to be allocated.
        Allocating   = 1,   // The page is being allocating by a call to AllocateMemoryPage.
        Idle         = 2,   // The page is allocated but idle. Protected from eviction by epoch tag and/or DirtyCounter > 0.
        Exclusive    = 4,   // The page is allocated and accessed exclusively by a given thread via PageExclusiveLatch.
    }

    protected readonly PagedMMFOptions Options;
    protected readonly ILogger<PagedMMF> Logger;
    
    private protected readonly PinnedMemoryBlock MemPages;
    private unsafe byte* _memPagesAddr;

    protected readonly int MemPagesCount;
    private CacheLinePaddedInt _clockSweepCurrentIndex;
    private PageInfo[] _memPagesInfo;
    
    private SafeFileHandle _fileHandle;
    private long _fileSize;
    private string _lockFilePath;
    private readonly IPageCacheBackpressureStrategy _backpressureStrategy;

    /// <summary>
    /// Callback invoked when page cache backpressure is detected.
    /// Set by <see cref="DatabaseEngine"/> to trigger <see cref="CheckpointManager.ForceCheckpoint"/> so dirty pages are flushed immediately instead of
    /// waiting for the timer-based checkpoint cycle.
    /// </summary>
    internal Action OnBackpressure { get; set; }

    /// <summary>Stable identifier of the backing file path (hash of the path string), recorded on Storage:FileHandle events.</summary>
    private int _filePathId;

    /// <summary>
    /// Atomically advances <see cref="_fileSize"/> to at least <paramref name="newSize"/>.
    /// No-op if the tracked size is already &gt;= <paramref name="newSize"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TrackFileGrowth(long newSize)
    {
        long oldSize;
        do
        {
            oldSize = _fileSize;
            if (newSize <= oldSize)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref _fileSize, newSize, oldSize) != oldSize);
    }

    /// <summary>
    /// Current backing-file size in bytes (last value tracked by <see cref="TrackFileGrowth"/>). Read by the per-tick gauge collector;
    /// no synchronization needed because <see cref="long"/> reads are atomic on x64.
    /// </summary>
    public long FileSize => _fileSize;

    private readonly ConcurrentDictionary<int, int> _memPageIndexByFilePageIndex;
    public EpochManager EpochManager { get; private set; }

    // FPI (Full-Page Image) support — null until EnableFpiCapture() is called (WAL disabled = no FPI)
    private FpiBitmap _fpiBitmap;
    private WalManager _walManager;
    private bool _enableFpiCompression;

    // CRC verification mode — defaults to RecoveryOnly to avoid on-load checks during recovery itself.
    // Set to OnLoad after recovery completes via SetPageChecksumVerification().
    private PageChecksumVerification _pageChecksumVerification = PageChecksumVerification.RecoveryOnly;

    /// <summary>
    /// Sets the page CRC verification mode. Called by <see cref="DatabaseEngine"/> after recovery completes
    /// to enable on-load verification during normal operation.
    /// </summary>
    internal void SetPageChecksumVerification(PageChecksumVerification mode) => _pageChecksumVerification = mode;

    /// <summary>
    /// Enables FPI capture by creating an <see cref="FpiBitmap"/> sized to the page cache and linking to the WAL manager.
    /// Called by <see cref="DatabaseEngine"/> after WAL initialization.
    /// </summary>
    /// <param name="walManager">The WAL manager to write FPI records to.</param>
    /// <param name="enableFpiCompression">When true, FPI page payloads are LZ4-compressed before writing to the WAL.</param>
    internal void EnableFpiCapture(WalManager walManager, bool enableFpiCompression = false)
    {
        _fpiBitmap = new FpiBitmap(MemPagesCount);
        _walManager = walManager;
        _enableFpiCompression = enableFpiCompression;
    }

    /// <summary>
    /// The FPI tracking bitmap. Exposed for <see cref="CheckpointManager"/> to reset at checkpoint start.
    /// Null when FPI capture is not enabled.
    /// </summary>
    internal FpiBitmap FpiBitmap => _fpiBitmap;

    unsafe internal PagedMMF(IMemoryAllocator memoryAllocator, EpochManager epochManager, PagedMMFOptions options, IResource parent, string resourceName,
        ILogger<PagedMMF> logger) : base(resourceName, ResourceType.File, parent)
    {
        if (!options.Validate(true, out var errors))
        {
            throw new ArgumentException("Invalid PagedMMF options", nameof(options), new AggregateException(errors));
        }
        
        EpochManager = epochManager;
        Options = options;
        Logger = logger;

        // Create the cache of the page, pin it and keeps its address
        var cacheSize = Options.DatabaseCacheSize;
        MemPages = memoryAllocator.AllocatePinned("PageCache", this, (int)cacheSize, true, 64);
        _memPagesAddr = MemPages.DataAsPointer;

        // Create the Memory Page info table
        MemPagesCount = (int)(cacheSize >> PageSizePow2);
        var pageCount = MemPagesCount;
        _memPagesInfo = new PageInfo[pageCount];
        _clockSweepCurrentIndex.Value = 0;

        for (int i = 0; i < pageCount; i++)
        {
            _memPagesInfo[i] = new PageInfo(i);
        }
        
        _memPageIndexByFilePageIndex = new ConcurrentDictionary<int, int>();

        _metrics = new Metrics (this, MemPagesCount);
        _backpressureStrategy = options.BackpressureStrategyFactory();

        try
        {
            // Acquire advisory lock file before opening the database
            _lockFilePath = BuildLockFilePath();
            AcquireLockFile();

            // Init or load the file
            var filePathName = Options.BuildDatabasePathFileName();
            var fi = new FileInfo(filePathName);
            IsDatabaseFileCreating = fi.Exists == false;
            if (IsDatabaseFileCreating)
            {
                CreateFile();
            }
            else
            {
                LoadFile();
            }
            Logger.LogInformation("Virtual Disk Manager service initialized successfully");
        }
        catch (DatabaseLockedException)
        {
            // Lock violation — propagate without wrapping for clear diagnostics
            ReleaseLockFile();
            throw;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Virtual Disk Manager service initialization failed");
            Dispose();
            throw new Exception("Virtual Disk Manager initialization error, check inner exception.", e);
        }
    }

    public void DeleteDatabaseFile()
    {
        var fi = new FileInfo(Options.BuildDatabasePathFileName());
        if (fi.Exists)
        {
            fi.Delete();
        }
    }

    #region Lock File

    private string BuildLockFilePath() => Path.Combine(Options.DatabaseDirectory, $"{Options.DatabaseName}.lock");

    /// <summary>
    /// Checks for an existing advisory lock file and creates a new one.
    /// If a stale lock file is found (dead PID), it is deleted with a warning.
    /// If a live lock file is found, throws <see cref="DatabaseLockedException"/>.
    /// </summary>
    private void AcquireLockFile()
    {
        if (File.Exists(_lockFilePath))
        {
            try
            {
                var json = File.ReadAllText(_lockFilePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var pid = root.GetProperty("pid").GetInt32();
                var machineName = root.GetProperty("machineName").GetString() ?? "unknown";
                var startedAt = root.TryGetProperty("startedAt", out var ts) ? 
                    DateTimeOffset.Parse(ts.GetString() ?? string.Empty) : DateTimeOffset.MinValue;

                if (!string.Equals(machineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    // Different machine — cannot verify PID remotely, treat as live
                    ThrowHelper.ThrowDatabaseLocked(Options.BuildDatabasePathFileName(), pid, machineName, startedAt);
                }

                if (IsProcessAlive(pid))
                {
                    ThrowHelper.ThrowDatabaseLocked(Options.BuildDatabasePathFileName(), pid, machineName, startedAt);
                }

                // Stale lock — process is dead, delete and proceed
                Logger.LogWarning("Stale lock file detected for PID {Pid} (started {StartedAt:u}). Previous process may have crashed. Removing lock file",
                    pid, startedAt);
                DeleteFileAndWait(_lockFilePath);
            }
            catch (DatabaseLockedException)
            {
                throw; // Re-throw lock exceptions
            }
            catch (Exception ex)
            {
                // Corrupt or unreadable lock file — delete and proceed with a warning
                Logger.LogWarning(ex, "Lock file '{LockFilePath}' is corrupt or unreadable. Removing it", _lockFilePath);
                try { DeleteFileAndWait(_lockFilePath); } catch { /* best effort */ }
            }
        }

        // Write new lock file
        try
        {
            var lockContent = JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                startedAt = DateTimeOffset.UtcNow.ToString("o"),
                machineName = Environment.MachineName
            });
            File.WriteAllText(_lockFilePath, lockContent);
        }
        catch (Exception ex)
        {
            // Lock file creation failed — log warning but proceed (OS file share is the real protection)
            Logger.LogWarning(ex, "Failed to create lock file '{LockFilePath}'. OS-level file sharing will still prevent concurrent access", _lockFilePath);
        }
    }

    /// <summary>
    /// Deletes the advisory lock file if it exists.
    /// </summary>
    private void ReleaseLockFile()
    {
        try
        {
            if (_lockFilePath != null)
            {
                DeleteFileAndWait(_lockFilePath);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to delete lock file '{LockFilePath}'", _lockFilePath);
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // Process does not exist
            return false;
        }
    }

    /// <summary>
    /// Deletes a file and polls until the NTFS pending-delete completes.
    /// On Windows, <see cref="File.Delete"/> returns immediately but the directory entry removal is deferred — <see cref="File.Exists"/> can return true
    /// briefly after deletion.
    /// Without polling, a subsequent <see cref="File.WriteAllText"/> to the same path can fail with <see cref="IOException"/>.
    /// </summary>
    private static void DeleteFileAndWait(string path, int maxWaitMs = 500)
    {
        if (!File.Exists(path))
        {
            return;
        }

        File.Delete(path);
        var sw = Stopwatch.StartNew();
        while (File.Exists(path) && sw.ElapsedMilliseconds < maxWaitMs)
        {
            Thread.Sleep(1);
        }
    }

    #endregion

    private void CreateFile()
    {
        // Create the Files
        var filePathName = Options.BuildDatabasePathFileName();

        _fileHandle = File.OpenHandle(filePathName, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
        _fileSize = 0L;
        _filePathId = filePathName.GetHashCode(StringComparison.Ordinal);

        TyphonEvent.EmitStorageFileHandle(0, _filePathId, (byte)FileMode.Create);

        Logger.LogInformation("Create Database '{DatabaseName}' in file '{FilePathName}'", Options.DatabaseName, filePathName);

        OnFileCreating();
    }

    protected virtual void OnFileCreating()
    {
        var handler = CreatingEvent;
        handler?.Invoke(this, null!);
    }

    private void LoadFile()
    {
        // Create the Files
        var filePathName = Options.BuildDatabasePathFileName();
        _fileHandle = File.OpenHandle(filePathName, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
        {
            var fi = new FileInfo(filePathName);
            _fileSize = fi.Length;
        }
        _filePathId = filePathName.GetHashCode(StringComparison.Ordinal);

        TyphonEvent.EmitStorageFileHandle(0, _filePathId, (byte)FileMode.Open);

        OnFileLoading();
    }

    protected virtual void OnFileLoading()
    {
        var handler = LoadingEvent;
        handler?.Invoke(this, null!);
    }
    
    public bool IsDatabaseFileCreating { get; }

    public bool IsDisposed { get; private set; }

    protected unsafe override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            Logger.LogInformation("Disposing Virtual Disk Manager");
            if (_fileHandle != null)
            {
                TyphonEvent.EmitStorageFileHandle(1, _filePathId, 0);
                _fileHandle.Dispose();
                _fileHandle = null;
            }

            ReleaseLockFile();

            _memPagesInfo = null;
            _memPagesAddr = null;
            _backpressureStrategy.Dispose();

            Logger.LogInformation("Virtual Disk Manager disposed");
        }
        IsDisposed = true;
        base.Dispose(disposing);
    }
    
    /// <summary>
    /// Request epoch-tagged shared access to a page. The page is protected from eviction
    /// by its AccessEpoch tag rather than by ref-counting. Caller must be inside an
    /// <see cref="EpochGuard"/> scope.
    /// </summary>
    internal bool RequestPageEpoch(int filePageIndex, long currentEpoch, out int memPageIndex)
    {
        while (true)
        {
            if (!FetchPageToMemory(filePageIndex, out memPageIndex))
            {
                return false;
            }

            var pi = _memPagesInfo[memPageIndex];

            // Tag the page with the current epoch (atomic max — never go backward)
            long existing;
            do
            {
                existing = pi.AccessEpoch;
                if (currentEpoch <= existing)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref pi.AccessEpoch, currentEpoch, existing) != existing);

            // Handle Allocating state from cache miss — transition to Idle
            // (must come AFTER epoch tag so the page is protected before becoming evictable)
            if (pi.PageState == PageState.Allocating)
            {
                pi.PageState = PageState.Idle;
                Interlocked.Increment(ref _metrics.FreeMemPageCount);
            }

            // Race detection: page may have been evicted between FetchPageToMemory and epoch tag
            if (pi.FilePageIndex != filePageIndex)
            {
                continue;  // Retry
            }

            // Ensure data is ready (wait for pending I/O)
            var ioTask = pi.IOReadTask;
            if (ioTask != null && !ioTask.IsCompletedSuccessfully)
            {
                ioTask.GetAwaiter().GetResult();
                pi.ResetIOCompletionTask();
            }

            pi.IncrementClockSweepCounter();
            EnsurePageVerified(memPageIndex);
            return true;
        }
    }

    /// <summary>
    /// Like <see cref="RequestPageEpoch"/> but skips CRC verification. Used during segment growth where pages are immediately overwritten (cleared + header
    /// initialized), making CRC verification unnecessary. In WAL mode, evicted pages may have stale CRCs with no FPI available because the growth path does
    /// not write WAL records — skipping CRC avoids false corruption exceptions.
    /// </summary>
    internal bool RequestPageEpochUnchecked(int filePageIndex, long currentEpoch, out int memPageIndex)
    {
        while (true)
        {
            if (!FetchPageToMemory(filePageIndex, out memPageIndex))
            {
                return false;
            }

            var pi = _memPagesInfo[memPageIndex];

            long existing;
            do
            {
                existing = pi.AccessEpoch;
                if (currentEpoch <= existing)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref pi.AccessEpoch, currentEpoch, existing) != existing);

            if (pi.PageState == PageState.Allocating)
            {
                pi.PageState = PageState.Idle;
                Interlocked.Increment(ref _metrics.FreeMemPageCount);
            }

            if (pi.FilePageIndex != filePageIndex)
            {
                continue;
            }

            var ioTask = pi.IOReadTask;
            if (ioTask != null && !ioTask.IsCompletedSuccessfully)
            {
                ioTask.GetAwaiter().GetResult();
                pi.ResetIOCompletionTask();
            }

            pi.IncrementClockSweepCounter();
            // Skip EnsurePageVerified — caller will overwrite the page content
            pi.CrcVerified = true;
            return true;
        }
    }

    /// <summary>
    /// Fetch the requested File Page to memory, allocating a Memory Page if needed.
    /// </summary>
    /// <param name="filePageIndex">Index of the File Page to fetch</param>
    /// <param name="memPageIndex"></param>
    /// <param name="timeout">The time (in tick) the method should wait to return successfully.</param>
    /// <param name="cancellationToken">An optional cancellation token for the user to cancel the call.</param>
    /// <returns><c>true</c> if the call succeeded, <paramref name="memPageIndex"/> will be valid. <c>false</c> if the operation was cancelled or time out
    /// <paramref name="memPageIndex"/> won't be valid.</returns>
    /// <remarks>
    /// This method will enter a wait cycle if the Memory Page is not allocated and there are no free Memory Pages available.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool FetchPageToMemory(int filePageIndex, out int memPageIndex, long timeout = Timeout.Infinite, CancellationToken cancellationToken = default)
    {
        // Hot path: cache hit. Kept EH-free + small so the JIT inlines this into RequestPageEpoch / RequestPageEpochUnchecked.
        // The cache-miss branch lives in FetchPageToMemoryOnMiss to keep its `using var` (try/finally) out of this method's IL —
        // see claude/scratch/jit-using.md for the EH-region-defeats-inlining mechanism.
        if (_memPageIndexByFilePageIndex.TryGetValue(filePageIndex, out memPageIndex))
        {
            ++_metrics.MemPageCacheHit;
            return true;
        }

        return FetchPageToMemoryOnMiss(filePageIndex, out memPageIndex, timeout, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool FetchPageToMemoryOnMiss(int filePageIndex, out int memPageIndex, long timeout, CancellationToken cancellationToken)
    {
        ++_metrics.MemPageCacheMiss;

        // Synchronous span brackets only the kickoff, not the async disk-read tail. Same tradeoff as PageCacheDiskWrite in SavePageInternal:
        // the raw async wait isn't captured, but in return we get (a) zero allocations on the fetch path, (b) no closure/display-class
        // capture of scopes, (c) no cross-thread TLS leak (Dispose always runs on the begin thread, so PublishEvent restores
        // CurrentOpenSpanId cleanly). If someone needs true async-tail attribution, it should come from a dedicated instant-event emit
        // on the completion thread, not from a span whose scope straddles an await.
        using var fetchScope = TyphonEvent.BeginPageCacheFetch(filePageIndex);

        // Page is not cached, we assign an available Memory Page to it
        if (!AllocateMemoryPage(filePageIndex, out memPageIndex, timeout, cancellationToken))
        {
            return false;
        }

        // Reset CRC verification flag — page is freshly loaded, needs re-verification
        _memPagesInfo[memPageIndex].CrcVerified = false;

        // Load the page from disk, if it's stored there already. (won't be the case for new pages)
        // The load is async and not part of the returned task but stored in the PageInfo
        var pageOffset = filePageIndex * (long)PageSize;
        var loadPage = (pageOffset + PageSize) <= _fileSize;
        if (loadPage)
        {
            ++_metrics.ReadFromDiskCount;

            using var diskReadScope = TyphonEvent.BeginPageCacheDiskRead(filePageIndex);

            var pi = _memPagesInfo[memPageIndex];
            var readTask = RandomAccess.ReadAsync(_fileHandle, MemPages.DataAsMemory.Slice(memPageIndex * PageSize, PageSize), pageOffset, cancellationToken);

            // Async-completion tracking: opt-in via UnsuppressKind(PageCacheDiskReadCompleted). When the DiskRead kickoff span was itself
            // suppressed (SpanId == 0), there's nothing to correlate with, so skip the wrap. When the completion kind is suppressed,
            // skip the wrap — producer hot path stays allocation-free by default.
            if (diskReadScope.Header.SpanId != 0 && !TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheDiskReadCompleted))
            {
                var state = new PageCacheReadCompletionState(diskReadScope.Header.SpanId, diskReadScope.Header.StartTimestamp, filePageIndex);
                var wrapped = readTask.AsTask().ContinueWith(SReadCompletionHandler, state, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                pi.SetIOReadTask(new ValueTask<int>(wrapped));
            }
            else
            {
                pi.SetIOReadTask(readTask);
            }
        }

        return true;
    }

    private int AdvanceClockHand()
    {
        var curValue = _clockSweepCurrentIndex.Value;
        var newValue = (curValue + 1) % MemPagesCount;
        while (Interlocked.CompareExchange(ref _clockSweepCurrentIndex.Value, newValue, curValue) != curValue)
        {
            curValue = _clockSweepCurrentIndex.Value;
            newValue = (curValue + 1) % MemPagesCount;
        }

        return curValue;
    }

    /// <summary>
    /// Allocate a Memory Page for the given File Page Index.
    /// </summary>
    /// <param name="filePageIndex">The file page index to mount to memory</param>
    /// <param name="memPageIndex">The index of the memory page for the requested file page if the call is successful.</param>
    /// <param name="timeout">The time (in tick) the method should wait to return successfully.</param>
    /// <param name="cancellationToken">An optional cancellation token for the user to cancel the call.</param>
    /// <returns><c>true</c> if the call succeeded, <paramref name="memPageIndex"/> will be valid. <c>false</c> if the operation was cancelled or time out
    /// <paramref name="memPageIndex"/> won't be valid.</returns>
    /// <remarks>
    /// This method will enter a wait cycle if no Memory Page is available, it will wait and loop until it finds one.
    /// Use the clock-sweep algorithm to find a free Memory Page.
    /// </remarks>
    private bool AllocateMemoryPage(int filePageIndex, out int memPageIndex, long timeout = Timeout.Infinite, CancellationToken cancellationToken = default)
    {
        using var scope = TyphonEvent.BeginPageCacheAllocatePage(filePageIndex);
        return AllocateMemoryPageCore(filePageIndex, out memPageIndex, timeout, cancellationToken);
    }

    private bool AllocateMemoryPageCore(int filePageIndex, out int memPageIndex, long timeout = Timeout.Infinite, CancellationToken cancellationToken = default)
    {
        var bpCtx = new BackpressureContext("Storage/PagedMMF/AllocateMemoryPage", TimeoutOptions.Current.PageCacheBackpressureTimeout);

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                memPageIndex = -1;
                return false;
            }

            // Refresh each iteration so committed transactions release their epoch protection
            var minActiveEpoch = EpochManager?.MinActiveEpoch ?? long.MaxValue;

            bool found = false;
            PageInfo pi = null;
            memPageIndex = -1;
            int evictedFilePageIndex = -1;

            // If we already have a MemPage fetch for the FilePage just before the one we allocate, then we try to take the MemPage that follows
            // We request FilePage 123, there's a FilePage 122 allocated to MemPage 34, then we try to allocate 35 for 123, which will allow, if needed,
            //  one file write operation for both pages
            if (filePageIndex > 0 && _memPageIndexByFilePageIndex.TryGetValue(filePageIndex - 1, out var prevMemPageIndex) && ((prevMemPageIndex + 1) < MemPagesCount))
            {
                memPageIndex = prevMemPageIndex + 1;
                pi = _memPagesInfo[memPageIndex];
                evictedFilePageIndex = pi.FilePageIndex;
                if (TryAcquire(pi, minActiveEpoch))
                {
                    found = true;
                }
            }

            // Parse the PageInfo array following the clock-sweep algorithm
            // Basically it's a circular parsing that find the first entry with a counter equals to 0, if the entry is not, then it's decremented (until it reaches
            //  0). When a page is access, its counter is incremented but capped to PageInfo.ClockSweepMaxValue.
            // If we can't find a page fitting this conditions, we do one more loop finding the first available page
            if (found == false)
            {
                int attempts = 0;
                int maxAttempts = MemPagesCount * 2;

                while (attempts < maxAttempts)
                {
                    memPageIndex = AdvanceClockHand();
                    pi = _memPagesInfo[memPageIndex];

                    // If the counter is 0, the page is candidate for eviction, try to acquire it
                    if (pi.ClockSweepCounter == 0)
                    {
                        evictedFilePageIndex = pi.FilePageIndex;
                        if (TryAcquire(pi, minActiveEpoch))
                        {
                            found = true;
                            break;
                        }
                    }

                    // Decrement the counter for this page and loop
                    pi.DecrementClockSweepCounter();
                    attempts++;
                }

                // Should almost never happen, right. ...right?
                // But if it is, loop one more time, same thing, but ignoring the ClockSweepCounter, take the first page available
                if (found == false)
                {
                    attempts = 0;
                    maxAttempts = MemPagesCount;

                    while (attempts < maxAttempts)
                    {
                        memPageIndex = AdvanceClockHand();
                        pi = _memPagesInfo[memPageIndex];

                        // If the counter is 0, the page is candidate for eviction, try to acquire it
                        evictedFilePageIndex = pi.FilePageIndex;
                        if (TryAcquire(pi, minActiveEpoch))
                        {
                            found = true;
                            break;
                        }

                        // Decrement the counter for this page and loop
                        pi.DecrementClockSweepCounter();
                        attempts++;
                    }
                }

                if (!found)
                {
                    // Backpressure span wraps the diagnostics collection + strategy wait. Suppressed by default alongside
                    // the other PageCache.* kinds, so zero cost unless the user explicitly opts in for cache-pressure analysis.
                    var bpScope = TyphonEvent.BeginPageCacheBackpressure();
                    try
                    {
                        // Collect pressure diagnostics for the strategy
                        var dirtyCount = 0;
                        var epochCount = 0;
                        for (var i = 0; i < MemPagesCount; i++)
                        {
                            var p = _memPagesInfo[i];
                            if (p.PageState == PageState.Free)
                            {
                                continue;
                            }

                            if (p.DirtyCounter > 0)
                            {
                                dirtyCount++;
                            }

                            if (p.AccessEpoch >= minActiveEpoch)
                            {
                                epochCount++;
                            }
                        }

                        bpScope.RetryCount = bpCtx.RetryCount;
                        bpScope.DirtyCount = dirtyCount;
                        bpScope.EpochCount = epochCount;

                        ++_metrics.BackpressureWaitCount;

                        Logger.LogWarning(
                            "Page cache backpressure: wait#{WaitCount} dirty={DirtyCount} epoch={EpochCount} retry={RetryCount} remaining={RemainingMs}ms",
                            _metrics.BackpressureWaitCount, dirtyCount, epochCount, bpCtx.RetryCount, bpCtx.WaitContext.Remaining.TotalMilliseconds);

                        // Demand-driven flush: wake the checkpoint manager immediately so dirty pages get written to
                        // disk → DecrementDirty → SignalPageAvailable → waiter wakes.
                        OnBackpressure?.Invoke();

                        if (!_backpressureStrategy.OnPressure(ref bpCtx, dirtyCount, epochCount))
                        {
                            ThrowHelper.ThrowPageCacheBackpressureTimeout(
                                dirtyCount, epochCount,
                                TimeoutOptions.Current.PageCacheBackpressureTimeout - bpCtx.WaitContext.Remaining);
                        }
                    }
                    finally
                    {
                        bpScope.Dispose();
                    }

                    continue;
                }
            }

            pi.FilePageIndex = filePageIndex;

            ++_metrics.TotalMemPageAllocatedCount;

            // Record the eviction as a zero-duration marker span, parented under the enclosing PageCacheAllocatePage scope via TLS. Default-
            // suppressed alongside the other PageCache.* kinds — when the profiler is off or this kind is suppressed the whole call
            // dead-code-eliminates in Tier 1. evictedFilePageIndex < 0 means we claimed a slot that was previously Free (no displacement).
            if (evictedFilePageIndex >= 0)
            {
                // Phase 5: dirtyBit reflects whether the displaced page was dirty at eviction time (still under the lock that gates clean reuse).
                var dirtyBit = (byte)(pi.DirtyCounter > 0 ? 1 : 0);
                TyphonEvent.EmitPageEvicted(evictedFilePageIndex, dirtyBit);
            }

            if (Options.PagesDebugPattern)
            {
                var pageAddr = MemPages.DataAsMemory.Slice(memPageIndex * PageSize).Span.Cast<byte, int>();
                int i;
                for (i = 0; i < PageHeaderSize >> 2; i++)
                {
                    pageAddr[i] = (filePageIndex << 16) | 0xFF00 | i;
                }

                for (int j = 0; j < PageRawDataSize >> 2; j++, i++)
                {
                    pageAddr[i] = (filePageIndex << 16) | j;
                }
            }

            // There might have been a concurrent allocation for this FilePage, so we Get or Add and check which MemPage is set
            var newMemPageIndex = _memPageIndexByFilePageIndex.GetOrAdd(filePageIndex, memPageIndex);

            // If the returned one is different, another thread beat us, we need to clean up what we did here and consider the other one
            if (newMemPageIndex != memPageIndex)
            {
                // Undo the page allocation, we are not going to use it
                pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
                pi.FilePageIndex = -1;
                pi.PageState = PageState.Free;
                pi.ResetIOCompletionTask();
                pi.ResetClockSweepCounter();
                pi.StateSyncRoot.ExitExclusiveAccess();

                memPageIndex = newMemPageIndex;
                _metrics.TotalMemPageAllocatedCount--;
            }

            return true;
        }
    }

    private bool TryAcquire(PageInfo info, long minActiveEpoch)
    {
        // First pass, check without locking (we won't bother to acquire the lock if the page is not in Free or Idle state)
        var state = info.PageState;
        if (state != PageState.Free && state != PageState.Idle)
        {
            return false;
        }

        // Don't evict pages that are slot-referenced, actively written, still dirty, or epoch-protected.
        // Two-layer protection: SlotRefCount prevents eviction of pages with live accessor slots (short-term),
        // EBR epoch protection prevents eviction of recently-accessed pages (long-term, bounded by re-stamp).
        if (state == PageState.Idle)
        {
            if (info.SlotRefCount > 0 || info.ActiveChunkWriters > 0 || info.DirtyCounter > 0)
            {
                return false;
            }
            if (info.AccessEpoch >= minActiveEpoch)
            {
                return false;
            }
        }

        // Second pass, under lock
        try
        {
            var wc = WaitContext.FromTimeout(TimeoutOptions.Current.PageCacheLockTimeout);
            if (!info.StateSyncRoot.EnterExclusiveAccess(ref wc))
            {
                ThrowHelper.ThrowLockTimeout("PageCache/TryAcquire", TimeoutOptions.Current.PageCacheLockTimeout);
            }

            // Reset the IOMode from read to none for a loading page if the IO read task completed successfully.
            if (info.IOReadTask!=null && info.IOReadTask.IsCompletedSuccessfully)
            {
                info.ResetIOCompletionTask();
            }

            // We need to check the state again, because another thread might have changed between the first and second pass
            if (info.PageState is PageState.Free or PageState.Idle)
            {
                // Re-check all protection layers under lock (may have changed since first pass)
                if (info.PageState == PageState.Idle &&
                    (info.SlotRefCount > 0 || info.ActiveChunkWriters > 0 || info.DirtyCounter > 0 || info.AccessEpoch >= minActiveEpoch))
                {
                    return false;
                }

                // Idle page is still referenced in the cache directory, so we remove it
                if (info.PageState == PageState.Idle)
                {
                    _memPageIndexByFilePageIndex.TryRemove(info.FilePageIndex, out _);
                }
                info.ResetClockSweepCounter();
                info.FilePageIndex = -1;
                info.AccessEpoch = 0;  // Clear epoch tag on reallocation
                _fpiBitmap?.Clear(info.MemPageIndex);  // Clear stale FPI bit so the new occupant gets a fresh capture
                info.PageState = PageState.Allocating;
                Interlocked.Decrement(ref _metrics.FreeMemPageCount);
                Debug.Assert(info.ExclusiveLatchDepth == 0);
                Debug.Assert(info.SlotRefCount == 0, $"Page evicted with SlotRefCount={info.SlotRefCount}");
                return true;
            }
            else
            {
                return false;
            }
        }
        finally
        {
            info.StateSyncRoot.ExitExclusiveAccess();
        }
    }
    
    public ChangeSet CreateChangeSet() => new(this);

    /// <summary>
    /// Acquire exclusive latch on an epoch-protected page (Idle → Exclusive).
    /// Re-entrant: if already exclusively held by the current thread, increments
    /// a counter and returns true. This is needed because multiple chunks on the
    /// same page may be latched independently (e.g., in VariableSizedBufferAccessor.NextChunk).
    /// </summary>
    internal bool TryLatchPageExclusive(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];

        // Re-entrant fast path: already latched by this thread — skip StateSyncRoot entirely
        if (pi.PageExclusiveLatch.IsLockedByCurrentThread)
        {
            pi.ExclusiveLatchDepth++;
            return true;
        }

        // New acquisition: check page state under StateSyncRoot
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.PageCacheLockTimeout);
        if (!pi.StateSyncRoot.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("PageCache/LatchPageExclusive", TimeoutOptions.Current.PageCacheLockTimeout);
        }

        try
        {
            if (pi.PageState != PageState.Idle)
            {
                return false;
            }

            pi.PageState = PageState.Exclusive;
        }
        finally
        {
            pi.StateSyncRoot.ExitExclusiveAccess();
        }

        // Acquire the latch (records thread ownership atomically)
        pi.PageExclusiveLatch.EnterExclusiveAccess(ref WaitContext.Null);
        pi.ExclusiveLatchDepth = 0;

        // Seqlock: signal modification in progress (even -> odd)
        unsafe
        {
            var headerAddr = (PageBaseHeader*)(_memPagesAddr + (memPageIndex * (long)PageSize));
            ++headerAddr->ModificationCounter;

            // FPI: capture full-page before-image on first dirty per checkpoint cycle.
            // FPI is best-effort: if the WAL buffer is full or an error occurs, the page will still be dirty and the next checkpoint cycle will re-capture.
            // Exceptions must NOT propagate here because ModificationCounter is already odd — an unhandled exception would leave the seqlock permanently
            // stuck, causing CopyPageWithSeqlock to spin forever.
            if (_walManager != null && !_fpiBitmap.TestAndSet(memPageIndex))
            {
                try
                {
                    WriteFpiRecord(memPageIndex, pi.FilePageIndex, headerAddr);
                }
                catch
                {
                    // Clear the FPI bitmap flag so the next checkpoint cycle will retry the capture.
                    _fpiBitmap.Clear(memPageIndex);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Release exclusive latch on an epoch-protected page (Exclusive → Idle).
    /// Decrements the re-entrance counter; only transitions to Idle when it reaches zero.
    /// </summary>
    internal void UnlatchPageExclusive(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];

        if (pi.ExclusiveLatchDepth > 0)
        {
            pi.ExclusiveLatchDepth--;
            return;
        }

        // Seqlock: signal modification complete (odd -> even)
        unsafe
        {
            var headerAddr = (PageBaseHeader*)(_memPagesAddr + (memPageIndex * (long)PageSize));
            ++headerAddr->ModificationCounter;
        }

        pi.PageExclusiveLatch.ExitExclusiveAccess();

        pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
        pi.PageState = PageState.Idle;
        // Reset epoch tag so the page becomes evictable immediately.
        // The exclusive latch already protected the page during writes;
        // once unlatched, epoch protection is no longer needed.
        pi.AccessEpoch = 0;
        pi.StateSyncRoot.ExitExclusiveAccess();
    }

    /// <summary>
    /// Serializes a Full-Page Image (FPI) WAL record capturing the before-image of a page. Called under exclusive latch — the page is stable during the copy.
    /// When <see cref="_enableFpiCompression"/> is true, the page payload is LZ4-compressed to reduce WAL bandwidth.
    /// Incompressible pages (e.g., random data) automatically fall back to uncompressed format.
    /// </summary>
    private unsafe void WriteFpiRecord(int memPageIndex, int filePageIndex, PageBaseHeader* headerAddr)
    {
        var pageAddr = _memPagesAddr + (memPageIndex * (long)PageSize);
        var pageSpan = new ReadOnlySpan<byte>(pageAddr, PageSize);

        byte[] rentedBuffer = null;
        try
        {
            bool useCompression = false;
            int compressedSize = 0;

            // Try LZ4 compression if enabled
            if (_enableFpiCompression)
            {
                rentedBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(FpiCompression.MaxCompressedSize(PageSize));
                compressedSize = FpiCompression.Compress(pageSpan, rentedBuffer);
                useCompression = compressedSize > 0;
            }

            int pagePayloadSize = useCompression ? compressedSize : PageSize;
            // FPI body: LSN (8B) + FpiMetadata (16B) + page data
            int fpiBodySize = sizeof(long) + FpiMetadata.SizeInBytes + pagePayloadSize;
            int fpiChunkSize = WalChunkHeader.SizeInBytes + fpiBodySize + WalChunkFooter.SizeInBytes;

            // Claim WAL buffer space for 1 FPI chunk
            var wc = WaitContext.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout);
            var claim = _walManager.CommitBuffer.TryClaim(fpiChunkSize, 1, ref wc);

            if (!claim.IsValid)
            {
                // WAL back-pressure — FPI is best-effort in this path.
                // The page will still be dirty and the next checkpoint will re-capture if needed.
                return;
            }

            try
            {
                int offset = 0;

                // Write chunk header (PrevCRC=0, CRC=0 — patched by WAL writer)
                var chunkHeader = new WalChunkHeader
                {
                    ChunkType = (ushort)WalChunkType.FullPageImage,
                    ChunkSize = (ushort)fpiChunkSize,
                    PrevCRC = 0,
                };
                MemoryMarshal.Write(claim.DataSpan[offset..], in chunkHeader);
                offset += WalChunkHeader.SizeInBytes;

                // Write LSN (always at body offset 0 for all chunk types)
                var lsn = claim.FirstLSN;
                MemoryMarshal.Write(claim.DataSpan[offset..], in lsn);
                offset += sizeof(long);

                // Write FPI metadata
                var meta = new FpiMetadata
                {
                    FilePageIndex = filePageIndex,
                    SegmentId = 0,
                    ChangeRevision = headerAddr->ChangeRevision,
                    UncompressedSize = PageSize,
                    CompressionAlgo = useCompression ? FpiCompression.AlgoLZ4 : FpiCompression.AlgoNone,
                    Reserved = 0,
                };
                MemoryMarshal.Write(claim.DataSpan[offset..], in meta);
                offset += FpiMetadata.SizeInBytes;

                // Write page data — compressed from rented buffer, or uncompressed from page address
                if (useCompression)
                {
                    new ReadOnlySpan<byte>(rentedBuffer, 0, compressedSize).CopyTo(claim.DataSpan[offset..]);
                }
                else
                {
                    pageSpan.CopyTo(claim.DataSpan[offset..]);
                }
                offset += pagePayloadSize;

                // Write chunk footer (CRC=0 — patched by WAL writer)
                var footer = new WalChunkFooter { CRC = 0 };
                MemoryMarshal.Write(claim.DataSpan[offset..], in footer);

                _walManager.CommitBuffer.Publish(ref claim);
            }
            catch
            {
                _walManager.CommitBuffer.AbandonClaim(ref claim);
                throw;
            }
        }
        finally
        {
            if (rentedBuffer != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // CRC Verification & FPI Repair
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Lazily verifies the CRC32C checksum of a cached page. Called from <see cref="RequestPageEpoch"/>
    /// after the page data is ready. Skips verification for: already-verified pages, RecoveryOnly mode,
    /// root page (file page 0), and never-checkpointed pages (CRC == 0).
    /// On mismatch, attempts FPI repair via <see cref="TryRepairPageFromFpi"/>; throws
    /// <see cref="PageCorruptionException"/> if repair fails.
    /// </summary>
    private unsafe void EnsurePageVerified(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];

        // Already verified this load cycle
        if (pi.CrcVerified)
        {
            return;
        }

        // RecoveryOnly mode: skip on-load checks (recovery handles torn pages via WalRecovery Phase 4)
        if (_pageChecksumVerification == PageChecksumVerification.RecoveryOnly)
        {
            pi.CrcVerified = true;
            return;
        }

        // Root page (file page 0) uses a different header format — skip
        if (pi.FilePageIndex <= 0)
        {
            pi.CrcVerified = true;
            return;
        }

        // Read stored CRC from the page header
        var pageAddr = (PageBaseHeader*)(_memPagesAddr + (memPageIndex * (long)PageSize));
        var storedCrc = pageAddr->PageChecksum;

        // CRC == 0 means the page has never been checkpointed (pre-FPI pages) — skip
        if (storedCrc == 0)
        {
            pi.CrcVerified = true;
            return;
        }

        // Compute CRC over the page, skipping the checksum field itself
        var pageSpan = new ReadOnlySpan<byte>((byte*)pageAddr, PageSize);
        var computedCrc = WalCrc.ComputeSkipping(pageSpan, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);

        if (computedCrc == storedCrc)
        {
            pi.CrcVerified = true;
            return;
        }

        // CRC mismatch — attempt FPI repair
        if (TryRepairPageFromFpi(memPageIndex, pi.FilePageIndex, storedCrc, computedCrc))
        {
            pi.CrcVerified = true;
            return;
        }

        // Repair failed — unrecoverable corruption
        throw new PageCorruptionException(pi.FilePageIndex, storedCrc, computedCrc);
    }

    /// <summary>
    /// Attempts to repair a corrupted page by finding the most recent FPI in the WAL and restoring it.
    /// Copies the FPI data into both the in-memory cache page and the on-disk data file.
    /// </summary>
    /// <returns>True if the page was successfully repaired; false if no FPI was available.</returns>
    private unsafe bool TryRepairPageFromFpi(int memPageIndex, int filePageIndex, uint storedCrc, uint computedCrc)
    {
        if (_walManager == null)
        {
            return false;
        }

        var fpiData = _walManager.SearchFpiForPage(filePageIndex);
        if (fpiData == null)
        {
            return false;
        }

        // Validate the FPI data CRC before applying it
        var fpiSpan = new ReadOnlySpan<byte>(fpiData);
        var fpiCrcStored = MemoryMarshal.Read<uint>(fpiSpan.Slice(PageBaseHeader.PageChecksumOffset));
        if (fpiCrcStored != 0)
        {
            var fpiCrcComputed = WalCrc.ComputeSkipping(fpiSpan, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
            if (fpiCrcComputed != fpiCrcStored)
            {
                return false; // FPI itself is corrupt — cannot use
            }
        }

        // Copy FPI data into the cache page
        var pageAddr = _memPagesAddr + (memPageIndex * (long)PageSize);
        fpiData.AsSpan().CopyTo(new Span<byte>(pageAddr, PageSize));

        // Write repaired page to disk
        WritePageDirect(filePageIndex, fpiData);

        Logger.LogWarning("Repaired torn page {FilePageIndex}: stored CRC=0x{StoredCrc:X8}, computed=0x{ComputedCrc:X8}, restored from FPI",
            filePageIndex, storedCrc, computedCrc);

        return true;
    }

    /// <summary>
    /// Reads a full page directly from the data file into the destination buffer.
    /// Used by <see cref="WalRecovery"/> for torn page detection during crash recovery.
    /// </summary>
    internal void ReadPageDirect(int filePageIndex, Span<byte> destination) => RandomAccess.Read(_fileHandle, destination, filePageIndex * (long)PageSize);

    /// <summary>
    /// Writes a full page directly to the data file from the source buffer.
    /// Used by <see cref="WalRecovery"/> and <see cref="TryRepairPageFromFpi"/> for torn page repair.
    /// Also updates the tracked file size if the write extends beyond the current end of file.
    /// </summary>
    internal void WritePageDirect(int filePageIndex, ReadOnlySpan<byte> source)
    {
        var pageOffset = filePageIndex * (long)PageSize;
        RandomAccess.Write(_fileHandle, source, pageOffset);
        TrackFileGrowth(pageOffset + PageSize);
    }

    internal void IncrementDirty(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        Debug.Assert(pi.PageState is PageState.Exclusive or PageState.Idle, "We can't increment the dirty counter for a page that is not Exclusive or Idle.");
        Interlocked.Increment(ref pi.DirtyCounter);
    }

    internal void DecrementDirty(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        var newVal = Interlocked.Decrement(ref pi.DirtyCounter);
        if (newVal == 0)
        {
            _backpressureStrategy.SignalPageAvailable();
        }
    }

    /// <summary>
    /// Atomically increments the <see cref="PageInfo.ActiveChunkWriters"/> counter for a page.
    /// Spins while ACW is negative (sentinel = checkpoint snapshot in progress on this page).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void IncrementActiveChunkWriters(int memPageIndex)
    {
        ref var acw = ref _memPagesInfo[memPageIndex].ActiveChunkWriters;
        SpinWait sw = default;
        while (true)
        {
            var current = acw;
            if (current < 0)
            {
                // Checkpoint is copying this page (~250ns). Spin until done.
                sw.SpinOnce();
                continue;
            }

            if (Interlocked.CompareExchange(ref acw, current + 1, current) == current)
            {
                return;
            }
            sw.SpinOnce();
        }
    }

    /// <summary>
    /// Atomically decrements the <see cref="PageInfo.ActiveChunkWriters"/> counter for a page.
    /// Called by <see cref="ChunkAccessor.CommitChanges"/> and <see cref="ChunkAccessor.EvictSlot"/>
    /// when a dirty slot is flushed to the <see cref="ChangeSet"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void DecrementActiveChunkWriters(int memPageIndex) => Interlocked.Decrement(ref _memPagesInfo[memPageIndex].ActiveChunkWriters);

    /// <summary>
    /// Raises <see cref="PageInfo.DirtyCounter"/> to <paramref name="minValue"/> if it is currently below that threshold.
    /// Used by <see cref="ChunkAccessor.MarkSlotDirty"/> when re-dirtying a page already tracked by the ChangeSet:
    /// <see cref="ChangeSet.AddByMemPageIndex"/> is idempotent (HashSet dedup), so subsequent accessor rentals
    /// within the same UoW don't increment DC. If checkpoint has drained DC to 0 in the meantime, this ensures
    /// the page stays dirty (DC &gt;= 1) to prevent premature eviction by the clock-sweep.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureDirtyAtLeast(int memPageIndex, int minValue)
    {
        var pi = _memPagesInfo[memPageIndex];
        SpinWait sw = default;
        while (true)
        {
            var current = pi.DirtyCounter;
            if (current >= minValue)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref pi.DirtyCounter, minValue, current) == current)
            {
                return;
            }

            sw.SpinOnce();
        }
    }

    internal void DecrementDirtyToMin(int memPageIndex, int minValue)
    {
        var pi = _memPagesInfo[memPageIndex];
        SpinWait sw = default;
        while (true)
        {
            var current = pi.DirtyCounter;
            if (current <= minValue)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref pi.DirtyCounter, minValue, current) == current)
            {
                if (minValue == 0)
                {
                    _backpressureStrategy.SignalPageAvailable();
                }

                return;
            }

            sw.SpinOnce();
        }
    }

    /// <summary>
    /// Flushes all pending writes to the underlying data file. Calls <c>RandomAccess.FlushToDisk</c> which issues an OS-level fsync.
    /// </summary>
    internal void FlushToDisk()
    {
        if (_fileHandle != null && !_fileHandle.IsInvalid)
        {
            RandomAccess.FlushToDisk(_fileHandle);
        }
    }

    /// <summary>
    /// Scans the in-memory page cache and returns the memory page indices of all dirty pages (DirtyCounter &gt; 0). The scan is approximate
    /// (no locking) — pages dirtied concurrently may be missed, which is safe because they will be caught in the next checkpoint cycle.
    /// </summary>
    internal int[] CollectDirtyMemPageIndices()
    {
        var dirty = new List<int>();
        for (int i = 0; i < MemPagesCount; i++)
        {
            var pi = _memPagesInfo[i];
            if (pi != null && pi.DirtyCounter > 0 && pi.PageState != PageState.Free)
            {
                dirty.Add(i);
            }
        }
        return dirty.ToArray();
    }

    /// <summary>
    /// Copies a live page into a destination buffer using a seqlock read protocol.
    /// Spins while the page's <see cref="PageBaseHeader.ModificationCounter"/> is odd (writer in progress),
    /// then memcpys the page and validates the counter hasn't changed. Retries on torn reads.
    /// </summary>
    /// <returns>True if a consistent snapshot was obtained; false if the page was skipped because a writer
    /// held the modification counter odd for longer than the checkpoint skip threshold (100ms).
    /// Skipping is safe: the page remains dirty and will be captured in the next checkpoint cycle.</returns>
    private unsafe bool CopyPageWithSeqlock(byte* pageAddr, byte* destAddr)
    {
        var sw = new SpinWait();
        long oddSpinStart = 0;
        while (true)
        {
            // Read the modification counter (must be even = quiescent)
            var counter = ((PageBaseHeader*)pageAddr)->ModificationCounter;
            if ((counter & 1) != 0)
            {
                // Writer in progress — track how long we've been waiting
                if (oddSpinStart == 0)
                {
                    oddSpinStart = Stopwatch.GetTimestamp();
                }
                else
                {
                    var elapsedMs = (Stopwatch.GetTimestamp() - oddSpinStart) * 1000.0 / Stopwatch.Frequency;
                    if (elapsedMs > 100)
                    {
                        // Writer has held the page for >100ms — likely blocked (e.g., waiting for
                        // backpressure to free cache pages). Skip this page to avoid deadlock:
                        // the writer may be waiting for this checkpoint to complete DecrementDirty.
                        Logger.LogWarning(
                            "CopyPageWithSeqlock: skipping page after {ElapsedMs:F0}ms — writer holding odd ModificationCounter={Counter}",
                            elapsedMs, counter);
                        return false;
                    }
                }

                sw.SpinOnce();
                continue;
            }

            // Writer finished (or was never active) — reset odd-spin timer
            oddSpinStart = 0;

            // Copy the full page
            Buffer.MemoryCopy(pageAddr, destAddr, PageSize, PageSize);

            // Validate counter hasn't changed (no torn read)
            if (((PageBaseHeader*)pageAddr)->ModificationCounter == counter)
            {
                return true; // Consistent snapshot obtained
            }

            // Counter changed — torn read, retry
            sw.SpinOnce();
        }
    }

    /// <summary>
    /// Writes dirty pages to the data file via staging buffers WITHOUT decrementing their DirtyCounter.
    /// Each page is snapshot-copied through the seqlock protocol, then CRC-stamped on the staging copy,
    /// and written synchronously to the data file. Called on the checkpoint thread.
    /// </summary>
    /// <param name="memPageIndices">Memory page indices of dirty pages to write. On return, the first
    /// <paramref name="writtenCount"/> entries contain the indices of pages that were actually written.
    /// Pages with an actively-held writer (odd ModificationCounter for &gt;100ms) are skipped.</param>
    /// <param name="stagingPool">Pool from which to rent page-sized staging buffers.</param>
    /// <param name="writtenCount">Number of pages actually written (may be less than input length if pages were skipped).</param>
    unsafe internal void WritePagesForCheckpoint(int[] memPageIndices, StagingBufferPool stagingPool, out int writtenCount)
    {
        writtenCount = 0;

        if (memPageIndices.Length == 0)
        {
            return;
        }

        Logger.LogInformation("Checkpoint: writing {PageCount} dirty pages", memPageIndices.Length);

        var memPageBaseAddr = _memPagesAddr;

        for (int i = 0; i < memPageIndices.Length; i++)
        {
            var memPageIndex = memPageIndices[i];
            var pi = _memPagesInfo[memPageIndex];

            // Wait for any pending I/O read to complete
            var ioTask = pi.IOReadTask;
            if (ioTask != null && !ioTask.IsCompletedSuccessfully)
            {
                ioTask.GetAwaiter().GetResult();
            }

            var livePageAddr = memPageBaseAddr + (memPageIndex * (long)PageSize);

            // Atomically claim the page for snapshot: CAS(ACW, -1, 0).
            // ACW = -1 is a sentinel that blocks new writers (they spin in IncrementActiveChunkWriters).
            // If ACW != 0, a writer is active — skip this page for the next checkpoint cycle.
            // This eliminates the TOCTOU race where a writer starts and completes (ACW 0→1→0) during the ~250ns memcpy, which CopyPageWithSeqlock can't
            // detect because OLC writes don't update ModificationCounter.
            if (Interlocked.CompareExchange(ref pi.ActiveChunkWriters, -1, 0) != 0)
            {
                continue;
            }

            // Rent a staging buffer and snapshot the live page via seqlock.
            // No concurrent OLC writers can start while ACW = -1 (they spin-wait).
            // Page-level latches (TryLatchPageExclusive) are still detected by the seqlock.
            using var staging = stagingPool.Rent();
            if (!CopyPageWithSeqlock(livePageAddr, staging.Pointer))
            {
                // Page has an active writer (via TryLatchPageExclusive) — skip it. The page stays dirty and will be picked up in the next checkpoint cycle.
                // This prevents deadlock when the writer is blocked on backpressure waiting for THIS checkpoint to free pages.
                Interlocked.Exchange(ref pi.ActiveChunkWriters, 0); // Release sentinel
                continue;
            }

            // Release the sentinel — writers can resume.
            Interlocked.Exchange(ref pi.ActiveChunkWriters, 0);

            // Increment ChangeRevision and compute CRC on the staging copy (not the live page)
            if (pi.FilePageIndex > 0)
            {
                var stagingHeader = (PageBaseHeader*)staging.Pointer;
                ++stagingHeader->ChangeRevision;
                stagingHeader->PageChecksum = WalCrc.ComputeSkipping(staging.Span, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
            }

            // Write staging buffer to the data file (synchronous — checkpoint runs on dedicated thread)
            var filePageIndex = pi.FilePageIndex;
            var pageOffset = filePageIndex * (long)PageSize;
            RandomAccess.Write(_fileHandle, staging.Span, pageOffset);
            TrackFileGrowth(pageOffset + PageSize);

            // Compact: move written index to front of array so caller knows which pages to decrement
            memPageIndices[writtenCount++] = memPageIndex;

            _metrics.PageWrittenToDiskCount++;
            _metrics.WrittenOperationCount++;
        }

        if (writtenCount < memPageIndices.Length)
        {
            Logger.LogInformation("Checkpoint: skipped {SkippedCount} pages with active writers", memPageIndices.Length - writtenCount);
        }
    }

    unsafe internal Task SavePages(int[] memPageIndices)
    {
        // Synchronous span brackets the setup+kickoff work. The async fsync+decrement completion in the ContinueWith is NOT captured under
        // this span because SpanScope is a ref struct — instead, we emit a separate PageCacheFlushCompleted record from inside the continuation,
        // correlated to this span by SpanId. PageCache.Flush is gated by Storage:PageCache:Enabled in JSON (post-2026-04-30 re-tier — only
        // PageCacheFetch is on the hard deny-list). The delta between FlushCompleted.duration and max(DiskWriteCompleted.duration)
        // is pure fsync cost — the single most useful number on a checkpoint-heavy workload.
        using var flushScope = TyphonEvent.BeginPageCacheFlush(memPageIndices.Length);

        // Capture begin-side correlator values before the ref-struct scope goes out of method scope. The existing ContinueWith already captures
        // memPageIndices into a display class, so adding these three fields to the capture costs zero extra allocations.
        var flushSpanId = flushScope.Header.SpanId;
        var flushBeginTs = flushScope.Header.StartTimestamp;
        var flushPageCount = memPageIndices.Length;

        // We want to generate as few IO operations as possible, so we sort the pages to identify the ones that are contiguous in the file
        Array.Sort(memPageIndices, (x, y) => x - y);

        var operations = new List<(int memPageIndex, int length)>();

        var curPageInfo = _memPagesInfo[memPageIndices[0]];
        var curOperation = (memPageIndex: memPageIndices[0], length: 1);
        var memPageBaseAddr = _memPagesAddr;

        for (int i = 1; i < memPageIndices.Length; i++)
        {
            // Increment the ChangeRevision for the page (File Page 0 is the file header, it's a different format so ignore it)
            if (curPageInfo.FilePageIndex > 0)
            {
                // Make sure the page to save is properly loaded first (wait for any pending IO read to complete).
                var ioTask = curPageInfo.IOReadTask;
                if (ioTask != null && !ioTask.IsCompletedSuccessfully)
                {
                    ioTask.GetAwaiter().GetResult();
                }

                var headerAddr = (PageBaseHeader*)(memPageBaseAddr + (curPageInfo.MemPageIndex * PageSize));
                ++headerAddr->ChangeRevision;

                // Compute CRC over the updated page so the on-disk copy is self-consistent (CP-07 equivalent for SavePages)
                var pageSpan = new ReadOnlySpan<byte>((byte*)headerAddr, PageSize);
                headerAddr->PageChecksum = WalCrc.ComputeSkipping(pageSpan, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
            }

            var nextMemPageIndex = memPageIndices[i];
            var nextPageInfo = _memPagesInfo[nextMemPageIndex];
            if ((curPageInfo.MemPageIndex+1)==nextPageInfo.MemPageIndex && (curPageInfo.FilePageIndex+1)==nextPageInfo.FilePageIndex)
            {
                // We are contiguous, extend the current operation
                curOperation.length++;
            }
            else
            {
                // We are not contiguous, store the current operation and start a new one
                operations.Add(curOperation);
                curOperation = (nextMemPageIndex, 1);
            }

            curPageInfo = nextPageInfo;
        }

        // Increment ChangeRevision for the last page (the loop above only processes pages before the last one)
        if (curPageInfo.FilePageIndex > 0)
        {
            var ioTask = curPageInfo.IOReadTask;
            if (ioTask != null && !ioTask.IsCompletedSuccessfully)
            {
                ioTask.GetAwaiter().GetResult();
            }

            var headerAddr = (PageBaseHeader*)(memPageBaseAddr + (curPageInfo.MemPageIndex * PageSize));
            ++headerAddr->ChangeRevision;

            var pageSpan = new ReadOnlySpan<byte>((byte*)headerAddr, PageSize);
            headerAddr->PageChecksum = WalCrc.ComputeSkipping(pageSpan, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
        }

        // Don't forget to add the last operation
        operations.Add(curOperation);

        var tasks = new Task[operations.Count];
        for (int i = 0; i < operations.Count; i++)
        {
            tasks[i] = SavePageInternal(operations[i].memPageIndex, operations[i].length).AsTask();
        }

        var saveTask = Task.WhenAll(tasks).ContinueWith(_ =>
        {
            // CP-03: fsync data file before decrementing DirtyCounter.
            // Without this, pages become evictable (DC=0) while data is only in OS buffer cache,
            // risking stale reload after eviction if the OS hasn't flushed to stable media.
            FlushToDisk();

            foreach (int memPageIndex in memPageIndices)
            {
                DecrementDirty(memPageIndex);
            }

            // Completion event: captures the full "kickoff → writes done → fsync done" duration. No-op when either Flush or FlushCompleted
            // is suppressed — the internal helper checks both. flushSpanId == 0 means the kickoff span itself was suppressed, so nothing to
            // correlate with either.
            if (flushSpanId != 0)
            {
                TyphonEvent.EmitPageCacheFlushCompleted(flushSpanId, flushBeginTs, flushPageCount, Stopwatch.GetTimestamp());
            }
        });
        return saveTask;
    }
    
    internal ValueTask SavePageInternal(int firstMemPageIndex, int length)
    {
        var pi = _memPagesInfo[firstMemPageIndex];

        // Save the page to disk
        var filePageIndex = pi.FilePageIndex;
        var pageOffset = filePageIndex * (long)PageSize;
        var lengthToWrite = PageSize * length;
        var pageData = MemPages.DataAsMemory.Slice(firstMemPageIndex * PageSize, lengthToWrite);

        TrackFileGrowth(pageOffset + lengthToWrite);

        _metrics.PageWrittenToDiskCount += length;
        _metrics.WrittenOperationCount++;

        // Synchronous span brackets only the WriteAsync kickoff. Manual scope + Dispose: `using var` marks the local readonly and blocks the
        // PageCount setter (CS1654). We capture SpanId + StartTimestamp before disposing so the optional async-completion wrap below can
        // correlate with this kickoff record through PageCacheDiskWriteCompleted.
        var writeScope = TyphonEvent.BeginPageCacheDiskWrite(filePageIndex);
        writeScope.PageCount = length;
        var writeSpanId = writeScope.Header.SpanId;
        var writeBeginTs = writeScope.Header.StartTimestamp;
        writeScope.Dispose();

        var writeTask = RandomAccess.WriteAsync(_fileHandle, pageData, pageOffset);

        // Async-completion tracking: opt-in via UnsuppressKind(PageCacheDiskWriteCompleted). Same gating logic as the read path — skip the
        // wrap when either the kickoff span is suppressed (nothing to correlate with) or the completion kind is suppressed (zero-alloc path).
        if (writeSpanId != 0 && !TyphonEvent.IsKindSuppressed(TraceEventKind.PageCacheDiskWriteCompleted))
        {
            var state = new PageCacheWriteCompletionState(writeSpanId, writeBeginTs, filePageIndex);
            return new ValueTask(writeTask.AsTask().ContinueWith(SWriteCompletionHandler, state, CancellationToken.None, 
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        }

        return writeTask;
    }

    internal unsafe byte* GetMemPageAddress(int memPageIndex) => &_memPagesAddr[memPageIndex * (long)PageSize];

    /// <summary>Diagnostic snapshot of a page's protection state. Used by ChunkAccessor error reporting.</summary>
    internal (int DirtyCounter, int ActiveChunkWriters, int SlotRefCount, long AccessEpoch, PageState PageState, bool CrcVerified) GetPageInfoForDiagnostic(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        return (pi.DirtyCounter, pi.ActiveChunkWriters, pi.SlotRefCount, pi.AccessEpoch, pi.PageState, pi.CrcVerified);
    }

    /// <summary>
    /// Get a typed <see cref="PageAccessor"/> for a memory page.
    /// Provides type-safe access to page header, metadata, and raw data regions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe PageAccessor GetPage(int memPageIndex) => new(GetMemPageAddress(memPageIndex));

    /// <summary>
    /// Get the raw data address for a memory page (skips header).
    /// Used by epoch-mode ChunkAccessor which computes chunk addresses directly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe byte* GetMemPageRawDataAddress(int memPageIndex)
        => GetMemPageAddress(memPageIndex) + PageHeaderSize;

    /// <summary>
    /// Get the base address of the memory page cache.
    /// Used by ChunkAccessor to compute memPageIndex from raw data addresses.
    /// </summary>
    internal unsafe byte* MemPagesBaseAddress => _memPagesAddr;

    /// <summary>
    /// Returns the FilePageIndex currently stored in a memory page slot.
    /// Used by <see cref="ChunkAccessor"/> to detect stale cached pointers after page eviction/reuse.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetFilePageIndex(int memPageIndex) => _memPagesInfo[memPageIndex].FilePageIndex;

    /// <summary>
    /// Increments the slot reference count for a memory page. While SlotRefCount &gt; 0,
    /// <see cref="TryAcquire"/> will not evict this page, protecting raw pointers held by
    /// ChunkAccessor slots. This complements EBR epoch protection: epochs bound the long-term
    /// protected set, while SlotRefCount provides precise short-term protection for live slots.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void IncrementSlotRefCount(int memPageIndex) => Interlocked.Increment(ref _memPagesInfo[memPageIndex].SlotRefCount);

    /// <summary>
    /// Decrements the slot reference count for a memory page.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void DecrementSlotRefCount(int memPageIndex) => Interlocked.Decrement(ref _memPagesInfo[memPageIndex].SlotRefCount);

    // ═══════════════════════════════════════════════════════════════════════
    // State Snapshot (test infrastructure)
    // ═══════════════════════════════════════════════════════════════════════

    internal readonly struct PageSnapshot(PageState state, short exclusiveLatchDepth, int dirtyCounter)
    {
        internal readonly PageState _state = state;
        internal readonly short _exclusiveLatchDepth = exclusiveLatchDepth;
        internal readonly int _dirtyCounter = dirtyCounter;
    }

    internal readonly struct StateSnapshot(PageSnapshot[] pages)
    {
        internal readonly PageSnapshot[] _pages = pages;
    }

    internal StateSnapshot SnapshotInternalState()
    {
        var pages = new PageSnapshot[_memPagesInfo.Length];
        for (int i = 0; i < _memPagesInfo.Length; i++)
        {
            var pi = _memPagesInfo[i];
            pages[i] = new PageSnapshot(pi.PageState, pi.ExclusiveLatchDepth, pi.DirtyCounter);
        }
        return new StateSnapshot(pages);
    }

    internal bool CheckInternalState(in StateSnapshot snapshot)
    {
        if (snapshot._pages.Length != _memPagesInfo.Length)
        {
            return false;
        }

        for (int i = 0; i < _memPagesInfo.Length; i++)
        {
            var pi = _memPagesInfo[i];
            ref readonly var snap = ref snapshot._pages[i];
            if (pi.PageState != snap._state ||
                pi.ExclusiveLatchDepth != snap._exclusiveLatchDepth ||
                pi.DirtyCounter != snap._dirtyCounter)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Get the PageInfo for a memory page by its memory index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PageInfo GetPageInfoByMemIndex(int memPageIndex) => _memPagesInfo[memPageIndex];

    /// <summary>Get the AccessEpoch for a memory page (test infrastructure).</summary>
    internal long GetPageAccessEpoch(int memPageIndex) => _memPagesInfo[memPageIndex].AccessEpoch;

    /// <summary>Get the PageState for a memory page (test infrastructure).</summary>
    internal PageState GetPageState(int memPageIndex) => _memPagesInfo[memPageIndex].PageState;

    public int EstimatedMemorySize
    {
        get
        {
            return Unsafe.SizeOf<PageInfo>() * _memPagesInfo.Length;
        }
    }
}