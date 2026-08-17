// CS1591: this file declares public-accessibility types that live in the internal namespace (Phase 2b entanglement, see
// claude/research/PublicVsInternalApiClassification.md). They are excluded from the published API reference, so consumer-facing
// doc coverage is not enforced here.
#pragma warning disable 1591

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
using System.Runtime.Intrinsics.X86;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

[PublicAPI]
public partial class PagedMMF : ResourceNode, IMemoryResource
{
    // The minimum page-cache size in PAGES (× PageSize = MinimumCacheSize, the 8 MiB floor). Internal: the public cache-sizing
    // surface is byte-based (PagedMMFOptions.DatabaseCacheSize / TyphonOptions.PageCacheSize) — see the public *Bytes constants.
    // Raised 256→1024 (2→8 MiB): the old 2 MiB floor forced pathological eviction pressure (the seqlock slot-reuse stall) and
    // is smaller than any real working set; 8 MiB is the smallest sane default. A test that must stress eviction with a truly
    // tiny cache sets an explicit size under TestMode, which bypasses this floor.
    internal const int MinimumMemPageCount = 1024;

    #region Events

    internal event EventHandler CreatingEvent;
    internal event EventHandler LoadingEvent;

    /// <summary>
    /// Raised once at the end of <see cref="Dispose(bool)"/> (explicit disposal only). Lets observers run teardown that
    /// must outlive the engine — the profiler bootstrap finalizes the trace here, since this MMF is disposed after the
    /// <see cref="DatabaseEngine"/> so the engine's shutdown events are still buffered and can be drained.
    /// </summary>
    internal event EventHandler DisposingEvent;

    #endregion

    #region Constants

    internal const int PageHeaderSize           = 192;                                  // Base Header + Metadata
    internal const int PageBaseHeaderSize       = 64;
    internal const int PageMetadataSize         = 128;
    internal const int PageSize                 = 8192;                                 // Base Header + Metadata + RawData
    internal const int PageRawDataSize          = PageSize - PageHeaderSize;
    internal const int PageSizePow2             = 13;                                   // 2^( PageSizePow2 = PageSize
    internal const int ChunkStartAlignment      = 64;                                   // Cache line — ceiling for chunk-start alignment (see ChunkBasedSegment)
    // v4 (CK-05 C2, directory-only root): every logical segment's root page now holds ONLY its page directory (the full
    // 8000-byte raw-data area = 2000 entries), carrying no segment data — so the CK-05 twin protects only the immutable
    // directory, never live data. The occupancy genesis therefore reserves one extra page (the occupancy bitmap's first
    // data page) and segments always span >= 2 pages. v3 (segment-directory twins, occupancy reserve = Int3) and earlier
    // are refused — no backward compat.
    //
    // v5 (#629): every archetype is cluster-backed, so the on-disk shape changed in three ways that a v4 file cannot
    // satisfy. Archetypes that were FLAT now carry a cluster segment; their EntityMap records changed shape (flat
    // header 14 + Location[slot]*4 vs cluster 19 = header 14 + ClusterChunkId 4 + SlotIndex 1, then chain roots indexed
    // by VERSIONED ordinal); and secondary-index leaf VALUES changed meaning, from a CompRev chain root to a packed
    // ClusterLocation (clusterChunkId*64 + slotIndex). The four per-ComponentTable index segments are gone and the
    // three system tables shrank from FromInt4 to FromInt2.
    //
    // The bump is what makes the break HONEST. None of the above is self-describing: a v4 EntityMap record read as a
    // cluster record finds ClusterChunkId where Location[0] lived, and a v4 index leaf read as a ClusterLocation
    // resolves to a plausible-looking but wrong cluster slot. Both silently serve corrupt data rather than failing.
    // Pre-alpha means no migration is owed — it does NOT mean a stale file may open and answer wrongly.
    // 6: per-sector page verification (PageSectorFooter). A page that declares sector geometry stores a CRC32C + currency
    //    stamp per 512 B sector in the free tail of its metadata region, and its PageChecksum field becomes a CRC over that
    //    footer rather than over the whole page. A revision-5 reader would compute a whole-page checksum and report every
    //    such page as corrupt, so the bump is what turns a confusing false alarm into a clean "incompatible format" refusal.
    internal const int DatabaseFormatRevision   = 7;
    internal const ulong MinimumCacheSize       = MinimumMemPageCount * PageSize;      // 8 MiB — the hard floor (see Validate)
    internal const ulong DefaultDatabaseCacheSize   = 256UL * 1024 * 1024;             // 256 MiB — the shipped production default
    internal const ulong RecommendedMinimumCacheSize = 64UL * 1024 * 1024;             // 64 MiB — warn below this (unless TestMode)
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
            if (HasWritebackDebt(pi.MemPageIndex))
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
                    if (HasWritebackDebt(i))
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

    /// <summary>The database bundle directory (<c>{name}.typhon</c>). The <c>data</c> file, <c>db.lock</c>, and <c>wal/</c> live inside it.</summary>
    internal string BundleDirectory => Options.BundleDirectory;

    /// <summary>The database's name — the bundle directory's stem, without the <c>.typhon</c> suffix. Recorded in profiling captures so a trace can say which
    /// database it ran against in terms a human recognises (#614, D-2).</summary>
    internal string DatabaseName => Options.DatabaseName;
    
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

    // ── Test-only crash-recovery fault injection (P1.5 crash sweep). Null in production → zero overhead (one predictable branch). ──
    // The interceptors RECORD (and may throw to abort mid-cycle); they do NOT replace the real I/O — reads and _fileSize stay real, so a
    // reopened engine recovers through the genuine path. ChaosPageIO wires these, then damages specific pages in the real file post-crash.

    /// <summary>Test hook: invoked with the file page index at the START of every physical page write (checkpoint / direct / async). May throw to simulate a
    /// crash mid-write; otherwise the real <c>RandomAccess.Write</c> proceeds. Null in production.</summary>
    internal Action<int> PageWriteInterceptor { get; set; }

    /// <summary>Test hook: invoked at each <see cref="FlushToDisk"/> fsync barrier (records the durability boundary for crash simulation). Null in production.</summary>
    internal Action FlushToDiskInterceptor { get; set; }

    /// <summary>
    /// Count of protected pages (CK-05) persisted by the CHECKPOINT write pass (<see cref="WritePagesForCheckpoint"/>) since process start.
    /// </summary>
    /// <remarks>
    /// Scoped to the checkpoint path on purpose. <see cref="SavePages"/> persists protected pages too and runs asynchronously, so a counter spanning both is
    /// unattributable — a background save overlapping a pass inflates it with persists the pass never performed. Only one checkpoint write pass runs at a time.
    /// </remarks>
    internal long CheckpointProtectedPagePersistCount;

    /// <summary>
    /// CK-02 violation counter: times a protected page was persisted AFTER a plain data page had already been written within the same checkpoint write pass.
    /// Must always be zero.
    /// </summary>
    /// <remarks>
    /// A protected persist ends in a FILE-WIDE fsync. Occurring after a plain write in the same pass, it makes that page durable while the cycle's flush2 barrier
    /// has not yet run — so a commit whose WAL record is still in the ring buffer can reach the data file (#585). The ordering is enforced by hoisting protected
    /// pages to the front of the batch; this counts any escape.
    /// <para>
    /// Recorded here rather than reconstructed by a test, because ordering cannot be observed from outside: <see cref="PageWriteInterceptor"/> fires on the
    /// direct and async structural write paths as well, so a concurrent <see cref="SavePages"/> is indistinguishable from this pass's own writes. Evaluating the
    /// invariant where both events are already in scope is both exact and immune to that interference — one predictable branch per page, against a page write.
    /// </para>
    /// </remarks>
    internal long CheckpointProtectedAfterPlainWriteCount;

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

    // CRC verification mode — defaults to RecoveryOnly to avoid on-load checks during recovery itself.
    // Set to OnLoad after recovery completes via SetPageChecksumVerification().
    private PageChecksumVerification _pageChecksumVerification = PageChecksumVerification.RecoveryOnly;

    /// <summary>
    /// Sets the page CRC verification mode. Called by <see cref="DatabaseEngine"/> after recovery completes
    /// to enable on-load verification during normal operation.
    /// </summary>
    internal void SetPageChecksumVerification(PageChecksumVerification mode) => _pageChecksumVerification = mode;

    /// <summary>File pages whose CRC failed while in <see cref="PageChecksumVerification.RecoverySuspect"/> mode — recorded instead of repaired/thrown so the
    /// engine's post-apply resolution can classify each (derived → rebuilt, orphaned primary → healed, live primary → loud-fail RB-04). Concurrent because page
    /// loads may run on the background checkpoint/IO threads during recovery.</summary>
    private readonly ConcurrentDictionary<int, byte> _suspectPages = new();

    /// <summary>Returns the recorded suspect file pages and clears the set. Called once by recovery after apply+scrub+rebuild to resolve them (heal or loud-fail).</summary>
    internal int[] DrainSuspectPages()
    {
        if (_suspectPages.IsEmpty)
        {
            return Array.Empty<int>();
        }

        var keys = new int[_suspectPages.Count];
        _suspectPages.Keys.CopyTo(keys, 0);
        _suspectPages.Clear();
        return keys;
    }

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

        // A Typhon database is a "{name}.typhon" DIRECTORY. If a FILE occupies that path it is a legacy or foreign artifact (the old 0-byte Workbench marker,
        // or a stray/pre-bundle file) — reject with a clear, typed error instead of letting the Directory.CreateDirectory (further down) throw an opaque
        // IOException. Checked here, BEFORE any resource (the pinned page cache, the lock file) is acquired, so the throw leaks nothing and propagates
        // untyped-unwrapped (the try's catch below rewraps every exception). Removal/migration is the caller's call — we never silently delete on the open path
        // (the file may hold a real legacy database).
        if (File.Exists(Options.BundleDirectory))
        {
            throw new StorageException(
                TyphonErrorCode.InvalidDatabaseBundle,
                $"Cannot open Typhon database: a file exists at the bundle path '{Options.BundleDirectory}', but a Typhon " +
                $"database must be a '{Options.DatabaseName}.typhon' directory. This looks like a legacy or foreign artifact " +
                "(e.g. a pre-bundle data file or the old Workbench marker) — remove or migrate it, then retry.");
        }

        // Create the cache of the page, pin it and keeps its address
        var cacheSize = Options.DatabaseCacheSize;

        // Guidance: a small page cache risks PageCacheBackpressureTimeout when a transaction's working set exceeds it. With a
        // 256 MiB default this only fires when a size was explicitly set below the recommended floor. Suppressed in TestMode
        // (unit tests deliberately run a minimal cache to stress eviction).
        if (!Options.TestMode && cacheSize < RecommendedMinimumCacheSize)
        {
            LogSmallPageCache(Logger, cacheSize / (1024UL * 1024UL), RecommendedMinimumCacheSize / (1024UL * 1024UL));
        }

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
            // The database is a bundle directory ({name}.typhon) — ensure it exists before the lock + data files that live inside it. Idempotent: a no-op when
            // reopening an existing bundle.
            Directory.CreateDirectory(Options.BundleDirectory);

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

            // Say what went wrong, not merely that something did. "Virtual Disk Manager initialization error, check
            // inner exception" named neither cause nor fix, and it referred the reader to an inner exception that every
            // caller then dropped: Spectre.Console prints `Error: {ex.Message}` and stops, the Workbench renders the
            // message into a toast, and the top-level shell handler only unwraps under #if DEBUG. So the one actionable
            // sentence — "Database name mismatch: expected 'broken', found 'world'" — reached nobody. It has misdirected
            // twice now (the other was a Win32Exception out of the liveness probe, #621), which is once more than a
            // wrapper that adds no information deserves. The inner exception is still attached for the stack trace.
            throw new Exception($"Cannot open the database at '{Options.BundleDirectory}': {e.Message}", e);
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

    private string BuildLockFilePath() => DatabaseLockFile.PathFor(Options.BundleDirectory);

    /// <summary>True once THIS instance successfully wrote the advisory lock file. Gates <see cref="ReleaseLockFile"/> so a rejected open never deletes the
    /// lock belonging to the live holder that rejected it.</summary>
    private bool _lockFileOwned;

    /// <summary>How often to re-check while waiting for a yieldable holder to let go.</summary>
    private static readonly TimeSpan HandoffPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Takes the advisory lock, waiting briefly for a <i>yieldable</i> incumbent to release it (#621).
    /// </summary>
    /// <remarks>
    /// <para>Three outcomes for an existing lock, unchanged in the first two:</para>
    /// <list type="bullet">
    /// <item><b>Dead PID</b> (or unreadable, or a different machine's file we can prove nothing about) — stale, removed, we proceed.</item>
    /// <item><b>Live and not yieldable</b> — <see cref="DatabaseLockedException"/>, immediately. This is every ordinary
    /// application-versus-application collision, and it behaves exactly as it did before this protocol existed.</item>
    /// <item><b>Live and yieldable</b> — publish a claim and wait up to <see cref="PagedMMFOptions.LockHandoffTimeout"/>.</item>
    /// </list>
    /// <para>The claim is written once and deleted by <b>this</b> process after acquiring, never by the holder. That is
    /// what lets the request file mean "a claim is in flight": a holder that has released must not race back in during
    /// the window between its own release and the claimant's acquisition, and the request's presence is how it knows.</para>
    /// </remarks>
    private void AcquireLockFile()
    {
        var handoffDeadline = (DateTimeOffset?)null;
        var claimPublished = false;

        while (true)
        {
            if (!TryClearOrRejectExistingLock(ref handoffDeadline, ref claimPublished))
            {
                Thread.Sleep(HandoffPollInterval);
                continue;
            }
            break;
        }

        // Write new lock file
        try
        {
            File.WriteAllText(_lockFilePath, DatabaseLockFile.SerializeLock(
                Environment.ProcessId, DateTimeOffset.UtcNow, Environment.MachineName, Options.YieldableLock, ResolveAdvertisedProfilerEndpoint()));

            // Only now may this instance ever delete that file — see ReleaseLockFile. A failed write below leaves this false,
            // so an instance that never owned a lock can never remove one.
            _lockFileOwned = true;
        }
        catch (Exception ex)
        {
            // Lock file creation failed — log warning but proceed (OS file share is the real protection)
            Logger.LogWarning(ex, "Failed to create lock file '{LockFilePath}'. OS-level file sharing will still prevent concurrent access", _lockFilePath);
        }

        if (claimPublished)
        {
            // Ours to retire, and only now: while it existed the holder knew not to re-acquire into the gap.
            DatabaseLockFile.DeleteRequest(Options.BundleDirectory);
        }
    }

    /// <summary>
    /// One pass of the acquisition loop. Returns <c>true</c> when the path is clear to write our own lock, <c>false</c> when the caller should wait and try
    /// again. Throws <see cref="DatabaseLockedException"/> when it never will be.
    /// </summary>
    private bool TryClearOrRejectExistingLock(ref DateTimeOffset? handoffDeadline, ref bool claimPublished)
    {
        if (!File.Exists(_lockFilePath))
        {
            return true;
        }

        if (!DatabaseLockFile.TryReadLock(Options.BundleDirectory, out var info))
        {
            // Corrupt, truncated or mid-write. Treated as removable, exactly as before this protocol — the OS file share is the real protection, so a bad
            // advisory file must never be able to wedge an open permanently.
            Logger.LogWarning("Lock file '{LockFilePath}' is corrupt or unreadable. Removing it", _lockFilePath);
            try { DeleteFileAndWait(_lockFilePath); } catch { /* best effort */ }
            return true;
        }

        var owner = Options.BuildDatabasePathFileName();
        var isRemote = !string.Equals(info.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        if (!isRemote && !IsProcessAlive(info.Pid))
        {
            // Stale lock — process is dead, delete and proceed.
            Logger.LogWarning("Stale lock file detected for PID {Pid} (started {StartedAt:u}). Previous process may have crashed. Removing lock file",
                info.Pid, info.StartedAt);
            DeleteFileAndWait(_lockFilePath);
            return true;
        }

        // Live. A remote holder's PID is unverifiable, so it is treated as live and — since we cannot ask it to yield
        // either — never eligible for handoff.
        if (isRemote || !info.Yieldable || Options.LockHandoffTimeout <= TimeSpan.Zero)
        {
            ThrowHelper.ThrowDatabaseLocked(owner, info.Pid, info.MachineName, info.StartedAt);
        }

        if (handoffDeadline is null)
        {
            handoffDeadline = DateTimeOffset.UtcNow + Options.LockHandoffTimeout;
            DatabaseLockFile.WriteRequest(Options.BundleDirectory);
            claimPublished = true;
            Logger.LogInformation("Database is held by PID {Pid}, which advertises it will yield. Requesting release (waiting up to {TimeoutMs} ms)",
                info.Pid, (int)Options.LockHandoffTimeout.TotalMilliseconds);
        }
        else if (DateTimeOffset.UtcNow > handoffDeadline)
        {
            // The holder advertised yieldable and did not deliver — hung, or an older build that writes the flag without
            // honouring it. Say so: "locked" alone would send the user hunting for a process that believes it cooperated.
            DatabaseLockFile.DeleteRequest(Options.BundleDirectory);
            throw new DatabaseLockedException(owner, info.Pid, info.MachineName, info.StartedAt,
                new TimeoutException(
                    $"PID {info.Pid} advertised that it would yield the database but did not release it within "
                    + $"{(int)Options.LockHandoffTimeout.TotalMilliseconds} ms."));
        }

        return false;
    }

    /// <summary>
    /// Deletes the advisory lock file — but ONLY the one this instance created.
    ///
    /// <para>The ownership check is load-bearing, not defensive tidiness. A rejected open (the database is held by a live
    /// process) reaches this through the <see cref="DatabaseLockedException"/> handler in the constructor, having never
    /// written a lock of its own — it found somebody else's. Deleting it there stripped the LIVE holder's advisory lock, so
    /// the very collision the lock exists to report destroyed the evidence of itself: subsequent openers saw an unlocked
    /// database and fell through to the OS sharing violation with no idea who held it.</para>
    /// </summary>
    /// <summary>
    /// The <c>host:port</c> this process's live profiler can be reached at, for the lock file to advertise, or
    /// <c>null</c> when no live port is configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lets an observer that has the database — the Workbench opening a bundle its holder is using — discover where to
    /// watch it, without the user retyping an endpoint they never chose. Read from the resolved launch config, which
    /// <see cref="TelemetryConfig"/> settles at class load from <c>typhon.telemetry.json</c> plus environment.
    /// </para>
    /// <para>
    /// <b>Intent, not liveness.</b> The lock is written when the database opens, which can precede the TCP listener
    /// binding; a host that overrides the port in code after this point is also not reflected. Consumers treat a refused
    /// connect as "not watchable right now", so the failure mode of being early or wrong is a retry, not a lie that
    /// matters.
    /// </para>
    /// </remarks>
    private static string ResolveAdvertisedProfilerEndpoint()
    {
        var port = TelemetryConfig.ProfilerLaunch?.LivePort ?? -1;
        return port >= 0 ? $"localhost:{port}" : null;
    }

    private void ReleaseLockFile()
    {
        if (!_lockFileOwned)
        {
            return;
        }

        try
        {
            if (_lockFilePath != null)
            {
                DeleteFileAndWait(_lockFilePath);
            }

            _lockFileOwned = false;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to delete lock file '{LockFilePath}'", _lockFilePath);
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        // A lock can only ever have been written by a real, running process, so a non-positive id is a corrupt or synthetic record — pid 0 is the System Idle
        // Process on Windows and not a process at all on POSIX. Probing it would hit the "cannot inspect" branch below and read as LIVE, which would let one
        // bogus lock file wedge a database permanently. Rejected up front instead.
        if (pid <= 0)
        {
            return false;
        }

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
        catch (System.ComponentModel.Win32Exception)
        {
            // The process exists but cannot be inspected — another user's session, a protected process, or PID 0 (the System Idle Process, whose HasExited
            // throws ERROR_ACCESS_DENIED). "Cannot tell" must read as LIVE: the alternative is stealing a lock from a process that is very much running. Before
            // this was handled the exception escaped the constructor and surfaced as "Virtual Disk Manager initialization error", which describes neither the
            // cause nor the fix.
            return true;
        }
    }

    /// <summary>
    /// Deletes a file and polls until the NTFS pending-delete completes.
    /// On Windows, <see cref="File.Delete"/> returns immediately but the directory entry removal is deferred — <see cref="File.Exists"/> can return true
    /// briefly after deletion.
    /// Without polling, a subsequent <see cref="File.WriteAllText(string, string)"/> to the same path can fail with <see cref="IOException"/>.
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
        // Verify BEFORE taking a handle, so a structurally-broken database is never touched at all. The clean-shutdown
        // flag says the last process closed properly; it says nothing about whether the bytes are still correct, and
        // damage that happens while a database is closed is otherwise served silently.
        VerifyOnOpen();

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

    /// <summary>
    /// Runs the configured open-time verification, if this store has a structural spine to verify.
    /// </summary>
    /// <remarks>
    /// A no-op on the base paged file, which is a bare page container: it has no meta pair, no bootstrap dictionary and no
    /// occupancy segment, so every spine check would be asking about structures that were never supposed to exist. Only
    /// <see cref="ManagedPagedMMF"/> — the layer that owns those structures — overrides this. Getting that split wrong
    /// makes verification report a healthy raw file as unopenable, which is the worst possible failure mode for a feature
    /// whose entire value is that its findings can be trusted.
    /// </remarks>
    protected virtual void VerifyOnOpen()
    {
    }

    /// <summary>
    /// Verifies a bundle's structural spine before it is opened and refuses the open on a
    /// <see cref="IntegritySeverity.Fatal"/> finding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>Fatal</c> finding means the spine is broken — both meta slots invalid, an unparseable bootstrap, a segment
    /// pointer that does not resolve. Opening anyway is the most harmful possible response: the engine follows those
    /// pointers into garbage. Lesser findings do <b>not</b> block the open, because a database with a divergent index is
    /// still a working database and refusing it would be the cure being worse than the disease; they are logged so the
    /// operator learns about them.
    /// </para>
    /// <para>
    /// The scan is skipped when the bundle cannot be read, since there is then nothing to verify — verification must never
    /// be the thing that breaks an open it was meant to protect.
    /// </para>
    /// </remarks>
    protected void VerifyBundleSpineOnOpen()
    {
        var mode = Options.VerifyOnOpen;
        if (mode == OpenVerification.None)
        {
            LogOpenVerificationDisabled();
            return;
        }

        var depth = mode switch
        {
            OpenVerification.Quick => ScanDepth.Quick,
            OpenVerification.Standard => ScanDepth.Standard,
            _ => ScanDepth.Spine
        };

        IntegrityReport report;
        try
        {
            using var source = new OfflineBundlePageSource(Options.BundleDirectory);
            report = IntegrityScanner.Scan(source, new IntegrityOptions { Depth = depth });
        }
        catch (Exception ex) when (ex is IOException or DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
        {
            // Nothing to verify (a fresh or non-bundle store, or a file another process is exclusively holding). The
            // ordinary open path reports those far better than a checker would.
            return;
        }

        var fatal = 0;
        for (var i = 0; i < report.Findings.Count; i++)
        {
            if (report.Findings[i].Severity == IntegritySeverity.Fatal)
            {
                fatal++;
            }
        }

        if (fatal > 0)
        {
            throw new DatabaseIntegrityException(report);
        }

        if (report.Findings.Count > 0)
        {
            LogOpenVerificationFindings(report.Findings.Count, report.Verdict.ToString());
        }
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
            // Null-conditional Logger throughout: an early ctor throw (e.g. options.Validate at construction, before Logger
            // is assigned) still registers this node in the resource tree, so Dispose can run on a half-constructed instance.
            Logger?.LogInformation("Disposing Virtual Disk Manager");
            if (_fileHandle != null)
            {
                TyphonEvent.EmitStorageFileHandle(1, _filePathId, 0);
                _fileHandle.Dispose();
                _fileHandle = null;
            }

            ReleaseLockFile();

            _memPagesInfo = null;
            _memPagesAddr = null;
            // Null-safe: an early ctor throw (e.g. the InvalidDatabaseBundle rejection above, raised before the strategy is
            // built) still registers this node in the resource tree, so Dispose can run on a half-constructed instance.
            _backpressureStrategy?.Dispose();

            Logger?.LogInformation("Virtual Disk Manager disposed");
        }
        IsDisposed = true;
        base.Dispose(disposing);

        // Signal observers AFTER the full storage + resource-node teardown. Explicit-disposal only — handlers touch
        // managed state, so this must not run on the finalizer path.
        if (disposing)
        {
            var handler = DisposingEvent;
            handler?.Invoke(this, null!);
        }
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

            // Ensure data is ready (wait for pending I/O). Defensive: assert the disk read returned the full page;
            // a short read would leave stale/zero bytes in the cache slot and Load would see truncated content.
            var ioTask = pi.IOReadTask;
            if (ioTask != null && !ioTask.IsCompletedSuccessfully)
            {
                var bytesRead = ioTask.GetAwaiter().GetResult();
                CheckConfig.Require(CheckConfig.Enabled, bytesRead == PageSize,
                    $"Short disk read for filePageIndex={filePageIndex}: got {bytesRead}, expected {PageSize} (corrupt/truncated file)");
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
            // Skip EnsurePageVerified — caller will overwrite the page content. Set the flag under the page-state lock (STO-10) so it stays consistent with the
            // locked check-and-set in EnsurePageVerified.
            pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
            pi.CrcVerified = true;
            pi.StateSyncRoot.ExitExclusiveAccess();
            return true;
        }
    }

    /// <summary>
    /// Like <see cref="RequestPageEpochUnchecked"/> but does <b>not</b> bump the page's clock-sweep counter and does <b>not</b> touch
    /// <see cref="PageInfo.CrcVerified"/>. This is the read-only introspection path consumed by the Database File Map's detail tier (Module 15, A2): faulting
    /// a page in purely to inspect it must not perturb the eviction heuristic that protects the live working set, and must leave a genuine CRC verification
    /// still pending. Caller must be inside an <see cref="EpochGuard"/> scope.
    /// </summary>
    internal bool RequestPageEpochNoSweep(int filePageIndex, long currentEpoch, out int memPageIndex)
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

            // Deliberately omitted vs RequestPageEpoch / RequestPageEpochUnchecked: IncrementClockSweepCounter (the eviction heuristic must not see
            // introspection reads) and EnsurePageVerified / CrcVerified = true (the detail tier recomputes the CRC itself and must be able to read an
            // unverified or corrupt page).
            return true;
        }
    }

    /// <summary>
    /// Reports whether file page <paramref name="filePageIndex"/> is currently resident in the page cache and, if so, whether it is dirty
    /// (<see cref="PageInfo.DirtyCounter"/> &gt; 0). Non-faulting — a directory lookup only, never triggers page I/O — so it reflects residency as it stands
    /// before any introspection read.
    /// </summary>
    internal bool TryGetPageResidency(int filePageIndex, out bool resident, out bool dirty)
    {
        if (_memPageIndexByFilePageIndex.TryGetValue(filePageIndex, out var memPageIndex))
        {
            var pi = _memPagesInfo[memPageIndex];
            if (pi.FilePageIndex == filePageIndex && pi.PageState != PageState.Free)
            {
                resident = true;
                dirty = HasWritebackDebt(pi.MemPageIndex);
                return true;
            }
        }

        resident = false;
        dirty = false;
        return false;
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
            // Cache-hit stat — PROFILER-GATED. This is the hottest increment in the engine (3-4× per point read, every
            // reader thread); as an always-on shared-field `++` it bounced one cache line across all cores and halved
            // concurrent-read throughput at 8+ threads. ProfilerActive is a JIT-folded static, so with the profiler off
            // this whole branch is eliminated — a hit-rate statistic must not tax the path it measures.
            if (TelemetryConfig.ProfilerActive)
            {
                ++_metrics.MemPageCacheHit;
            }
            return true;
        }

        return FetchPageToMemoryOnMiss(filePageIndex, out memPageIndex, timeout, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool FetchPageToMemoryOnMiss(int filePageIndex, out int memPageIndex, long timeout, CancellationToken cancellationToken)
    {
        // Profiler-gated, paired with MemPageCacheHit so the derived hit-rate is both-counted or both-zero. This is the
        // cold path (a miss already does disk I/O), so the gate is for coherence, not perf.
        if (TelemetryConfig.ProfilerActive)
        {
            ++_metrics.MemPageCacheMiss;
        }

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
        // The load is async and not part of the returned task but stored in the PageInfo.
        // MapReadOffset is identity for normal pages; for an A/B-paired page (CK-05 meta pair) it resolves the current slot.
        var pageOffset = MapReadOffset(filePageIndex);
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

                            if (HasWritebackDebt(i))
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

                        // High-water marks, kept because this is the ONLY place the engine looks at the cache while it is
                        // actually under pressure. Every other gauge samples between units of work, when nothing holds an
                        // epoch and the numbers are uninformative by construction — which is exactly how a run can report
                        // zero epoch-held pages in its census for 57,000 ticks and then die naming 17,164 of them.
                        if (dirtyCount > PeakBackpressureDebt) { PeakBackpressureDebt = dirtyCount; }
                        if (epochCount > PeakBackpressureEpochHeld) { PeakBackpressureEpochHeld = epochCount; }

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
                var dirtyBit = (byte)(HasWritebackDebt(pi.MemPageIndex) ? 1 : 0);
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
            if (info.SlotRefCount > 0 || info.ActiveChunkWriters > 0 || info.DirtyCounter > 0 || HasWritebackDebt(info.MemPageIndex))
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
                    (info.SlotRefCount > 0 || info.ActiveChunkWriters > 0 || info.DirtyCounter > 0 || HasWritebackDebt(info.MemPageIndex)
                     || info.AccessEpoch >= minActiveEpoch))
                {
                    return false;
                }

                // Idle page is still referenced in the cache directory, so we remove it
                if (info.PageState == PageState.Idle)
                {
                    _memPageIndexByFilePageIndex.TryRemove(info.FilePageIndex, out _);
                }
                // Reset the seqlock ModificationCounter to a known EVEN (quiescent) value as the slot is repurposed. The counter is per-slot memory, never
                // reset elsewhere — TryLatch/Unlatch only increment it (parity-preserving) and the page-clear path deliberately PRESERVES it. So a slot whose
                // prior occupant left an ODD value (e.g. an OLC-only page that never touched the counter, over stale/loaded memory) would hand that odd value
                // to the fresh page, making it look permanently "write-in-progress": CopyPageWithSeqlock then spin-waits the full 100 ms skip timeout on a page
                // no one is writing, every checkpoint cycle. A page loaded from disk immediately overwrites this with its persisted (even) counter; a freshly
                // grown page keeps the 0. Done under StateSyncRoot with the slot detached from any accessor (Idle/Free, ACW==0, not dirty), so no reader races.
                unsafe
                {
                    ((PageBaseHeader*)(_memPagesAddr + info.MemPageIndex * (long)PageSize))->ModificationCounter = 0;
                }
                info.ResetClockSweepCounter();
                info.FilePageIndex = -1;
                info.AccessEpoch = 0;  // Clear epoch tag on reallocation

                // Clear the writeback ledger with the slot. The guards above already established that this slot owes nothing (both gens equal), so this is a
                // normalisation, not a discard — but leaving a nonzero pair behind would make the NEXT occupant inherit a stale "already captured at gen N"
                // claim, and the first N modifications to a freshly loaded page would then look durable when they are not.
                Volatile.Write(ref info.WritebackGen, 0);
                Volatile.Write(ref info.CapturedGen, 0);
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

    // ─── ChangeSet pool ────────────────────────────────────────────────────────────────
    // Fence-tick parallel phases create one ChangeSet per chunk (FencePrep + FenceMigrate × chunkCount × tickRate ≈ thousands per second). Pool them via a
    // ConcurrentBag — bag uses thread-local internal storage so Rent/Return is lock-free in the common case (per-worker pool slot). The ChangeSet's owner
    // PagedMMF reference is stable across reuse cycles, so a rented ChangeSet behaves identically to a freshly-allocated one after ClearForReuse.
    private readonly ConcurrentBag<ChangeSet> _changeSetPool = new();

    /// <summary>
    /// Rent a reusable <see cref="ChangeSet"/> from the per-engine pool, falling back to a fresh allocation when the pool is empty. Caller must call
    /// <see cref="ReturnChangeSet"/> exactly once when done; pages tracked by the rented ChangeSet must have their dirty marks resolved
    /// (via SaveChangesAsync / ReleaseDirtyMarks / Reset) BEFORE returning, otherwise the next renter will receive stale state.
    /// </summary>
    public ChangeSet RentChangeSet() => _changeSetPool.TryTake(out var cs) ? cs : new ChangeSet(this);

    /// <summary>
    /// Return a previously-rented <see cref="ChangeSet"/> to the pool. Clears the local tracking buffers via <see cref="ChangeSet.ClearForReuse"/>;
    /// caller is responsible for having resolved dirty marks beforehand.
    /// </summary>
    public void ReturnChangeSet(ChangeSet cs)
    {
        if (cs == null)
        {
            return;
        }

        cs.ClearForReuse();
        _changeSetPool.Add(cs);
    }

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

        // Seqlock: signal modification in progress (even -> odd).
        //
        // NOT for atomicity. SL-05 guarantees a single writer — the counter is only ever touched under the exclusive latch, so there is no RMW race to
        // protect against. Interlocked is used here purely as a FENCE.
        //
        // The ordering that matters is against the checkpoint reader, which runs the seqlock protocol with no latch at all (CopyPageWithSeqlock). SL-02
        // requires the odd counter to be visible BEFORE any of the caller's page writes; otherwise the reader can load an even counter, memcpy a page that
        // already contains new data, re-read the still-even counter and accept a torn snapshot as valid. A plain `++` does not establish that on either the
        // hardware or the JIT: arm64 permits StoreStore reordering, and the .NET memory model permits ordinary writes to be reordered outright ("the effects
        // of ordinary reads and writes can be reordered as long as that preserves single-thread consistency").
        //
        // A release store (Volatile.Write) does NOT work here — release keeps EARLIER accesses from sinking below the store, and what this site needs is to
        // stop LATER stores rising above it. That is the opposite direction, and it is why this site and the closing one in UnlatchPageExclusive use
        // different primitives despite looking symmetric. Only a full fence gives StoreStore-after, and Interlocked.Increment is one instruction of it.
        //
        // An `if (!X86Base.IsSupported)` barrier would also be wrong here: it folds away on x64 and leaves the JIT unconstrained (#579).
        unsafe
        {
            var headerAddr = (PageBaseHeader*)(_memPagesAddr + (memPageIndex * (long)PageSize));
            Interlocked.Increment(ref headerAddr->ModificationCounter);
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

        // Seqlock: signal modification complete (odd -> even).
        //
        // SL-03: every page write must be visible BEFORE the counter goes even, or a reader can observe an even counter while the last data stores are still
        // in flight and accept a torn snapshot. "Prior stores visible before this store" IS release semantics, so unlike the opening increment this site
        // needs no full fence — Volatile.Write expresses exactly the requirement, and costs nothing on x64 (plain mov under TSO; stlr on arm64).
        //
        // As at the open site, atomicity is not the point: SL-05 gives us a single writer, so the read-increment-write below cannot race. The plain read is
        // safe because we are reading back our own store from the matching TryLatchPageExclusive.
        //
        // The exclusive-latch release below is a fence too, but it happens AFTER the counter store, which is the wrong side — Linux's write_sequnlock places
        // its smp_wmb() before the counter bump for exactly this reason (#579).
        unsafe
        {
            var headerAddr = (PageBaseHeader*)(_memPagesAddr + (memPageIndex * (long)PageSize));
            Volatile.Write(ref headerAddr->ModificationCounter, headerAddr->ModificationCounter + 1);
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

    // ═══════════════════════════════════════════════════════════════
    // CRC Verification
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Lazily verifies the CRC32C checksum of a cached page. Called from <see cref="RequestPageEpoch"/>
    /// after the page data is ready. Skips verification for: already-verified pages, RecoveryOnly mode,
    /// root page (file page 0), and never-checkpointed pages (CRC == 0).
    /// On mismatch: in <see cref="PageChecksumVerification.RecoverySuspect"/> mode the page is recorded for post-apply resolution (heal or RB-04 loud-fail) and
    /// accepted; otherwise (OnLoad) it throws <see cref="PageCorruptionException"/> — there is no FPI repair (the rebuild net replaces it).
    /// </summary>
    private unsafe void EnsurePageVerified(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];

        // Already verified this load cycle (fast path, unlocked — once set true it stays true until the slot is reloaded, where CrcVerified is reset to false).
        if (pi.CrcVerified)
        {
            return;
        }

        // STO-10: serialize the check-and-set of CrcVerified — and the FPI repair that may overwrite the live page — under the page-state lock. A concurrent
        // writer must ALSO take StateSyncRoot to transition Idle→Exclusive before it can latch and mutate the page, so holding it here blocks writers for the
        // duration of verification, closing both the double-verify race and the verify-vs-concurrent-write race the unlocked check left open.
        pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
        try
        {
            // Re-check under the lock: another thread may have verified (or reset) while we waited.
            if (pi.CrcVerified)
            {
                return;
            }

            // RecoveryOnly mode: skip on-load checks (recovery heals torn pages via the rebuild net + suspect-page resolution, RB-01/RB-04)
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
            var pageSpan = new ReadOnlySpan<byte>((byte*)pageAddr, PageSize);

            // CRC == 0 means the page has never been checkpointed — skip. The sentinel only applies to whole-page
            // checksumming: a page carrying a per-sector footer was definitionally written by the stamping path, so its
            // checksum field is a footer CRC that may legitimately be any value including zero.
            var sectorCount = PageSectorFooter.ReadSectorCount(pageSpan);
            if (storedCrc == 0 && sectorCount == 0)
            {
                pi.CrcVerified = true;
                return;
            }

            if (VerifyPageImage(pageSpan, out var computedCrc))
            {
                pi.CrcVerified = true;
                return;
            }

            // RecoverySuspect mode: record the torn page and accept it for now — never throw, never FPI-repair. The engine's post-apply resolution
            // (DatabaseEngine.ResolveSuspectPrimaryPages) classifies each: a derived page is rebuilt (RB-01); an orphaned primary page was in-window-replaced
            // and is healed; a primary page still holding a live chunk fails the open loudly (RB-04). This is what lets recovery proceed without FPI.
            if (_pageChecksumVerification == PageChecksumVerification.RecoverySuspect)
            {
                _suspectPages.TryAdd(pi.FilePageIndex, 0);
                pi.CrcVerified = true;
                return;
            }

            // CRC mismatch in a non-recovery mode (OnLoad) — unrecoverable corruption. There is no FPI repair: a torn checkpointed page is healed during recovery by
            // the rebuild net (RB-01 derived rebuild, CK-09 occupancy re-derive) or fails the open loudly via suspect resolution (RB-04, the RecoverySuspect path above).
            throw new PageCorruptionException(pi.FilePageIndex, storedCrc, computedCrc);
        }
        finally
        {
            pi.StateSyncRoot.ExitExclusiveAccess();
        }
    }

    /// <summary>
    /// Stamps a page image's identity and integrity fields immediately before it is written to disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things are stamped. The page's own <b>logical index</b>, which makes a misdirected write — a page landing at the
    /// wrong file offset — detectable with certainty; without it such a write is invisible, because the page's checksum is
    /// perfectly valid and it is simply the wrong page.
    /// </para>
    /// <para>
    /// And the <b>checksum</b>, in whichever form the page declared at initialisation. A page that declared per-sector
    /// geometry gets one CRC32C per sector plus a currency stamp, and its <c>PageChecksum</c> field becomes a CRC over that
    /// footer — so the array protecting the sectors is itself protected. A page that declared none keeps the single
    /// whole-page checksum. The per-sector form is not merely finer, it is also <i>cheaper</i>: independent CRC chains
    /// break the <c>crc32</c> instruction's 3-cycle latency dependency that makes one 8 KiB chain leave the execution unit
    /// mostly idle.
    /// </para>
    /// <para>
    /// Callers must have advanced <see cref="PageBaseHeader.ChangeRevision"/> first — the per-sector currency stamp is
    /// derived from it.
    /// </para>
    /// </remarks>
    /// <param name="page">The page image about to be written.</param>
    /// <param name="logicalPageIndex">The page's logical file-page index.</param>
    /// <param name="allowSectorFooter">
    /// <c>false</c> for A/B protected pages. They are already covered against a torn write by their twin, so the finer
    /// granularity buys nothing, and keeping them on the whole-page checksum leaves the pair-selection predicate untouched.
    /// </param>
    internal static void StampPageForWrite(Span<byte> page, int logicalPageIndex, bool allowSectorFooter = true)
    {
        PageSectorFooter.StampFilePageIndex(page, logicalPageIndex);

        var sectors = allowSectorFooter ? PageSectorFooter.ReadSectorCount(page) : 0;
        if (sectors > 0)
        {
            PageSectorFooter.Stamp(page, sectors);
            return;
        }

        var crc = Crc32CUtil.ComputeSkipping(page, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
        MemoryMarshal.Write(page[PageBaseHeader.PageChecksumOffset..], in crc);
    }

    /// <summary>
    /// Verifies a page image the same way <see cref="StampPageForWrite"/> stamped it, honouring the page's own declared
    /// geometry so a reader never has to consult a directory to know how to check a page.
    /// </summary>
    /// <param name="page">The page image.</param>
    /// <param name="computed">Receives the computed whole-page checksum when the page uses that form; <c>0</c> otherwise.</param>
    /// <returns><c>true</c> when the page verifies.</returns>
    internal static bool VerifyPageImage(ReadOnlySpan<byte> page, out uint computed)
    {
        var sectors = PageSectorFooter.ReadSectorCount(page);
        if (sectors == 0)
        {
            computed = Crc32CUtil.ComputeSkipping(page, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
            return computed == MemoryMarshal.Read<uint>(page[PageBaseHeader.PageChecksumOffset..]);
        }

        computed = 0;
        Span<bool> sectorOk = stackalloc bool[PageSectorFooter.MaxSectorCount];
        PageSectorFooter.Verify(page, sectors, sectorOk, out var failed);
        return failed == 0;
    }

    /// <summary>
    /// Reads a full page directly from the data file into the destination buffer.
    /// Used by <see cref="WalRecovery"/> for torn page detection during crash recovery.
    /// </summary>
    internal void ReadPageDirect(int filePageIndex, Span<byte> destination) => RandomAccess.Read(_fileHandle, destination, filePageIndex * (long)PageSize);

    /// <summary>
    /// Writes a full page directly to the data file from the source buffer (used by the structural / direct-write paths).
    /// Also updates the tracked file size if the write extends beyond the current end of file.
    /// </summary>
    internal void WritePageDirect(int filePageIndex, ReadOnlySpan<byte> source)
    {
        PageWriteInterceptor?.Invoke(filePageIndex);   // test-only crash injection; throws to abort mid-write
        var pageOffset = filePageIndex * (long)PageSize;
        RandomAccess.Write(_fileHandle, source, pageOffset);
        TrackFileGrowth(pageOffset + PageSize);
    }

    /// <summary>
    /// Maps a logical file-page index to the on-disk byte offset to read it from. The identity mapping by default; a
    /// derived store overrides it for A/B-paired pages (CK-05) whose current content alternates between two physical
    /// slots — e.g. the meta pair, where logical page 0 reads from the current slot. Used on the cold page-cache miss.
    /// </summary>
    protected virtual long MapReadOffset(int filePageIndex) => filePageIndex * (long)PageSize;

    /// <summary>
    /// Whether a file page is persisted outside the normal checkpoint dirty-write path (its durability is owned
    /// elsewhere) and must therefore be skipped by <see cref="CollectDirtyMemPageIndices"/>. False by default; a
    /// derived store returns true for its A/B-paired protected pages (CK-05 meta pair), written only by the
    /// alternation path. Defence-in-depth: such pages are never DC-marked, but this guarantees they are never written
    /// in-place at their slot-A offset by a checkpoint.
    /// </summary>
    protected virtual bool IsExternallyPersisted(int filePageIndex) => false;

    /// <summary>
    /// Hook for the protected-page (CK-05) write redirect. When <paramref name="filePageIndex"/> is a protected segment-
    /// directory page, a derived store writes <paramref name="image"/> to the page's alternate slot (stamping
    /// <c>PairGeneration = gen+1</c> + a fresh CRC), fsyncs, and flips the in-memory current slot — returning <c>true</c>
    /// so the caller skips the normal in-place write + CRC. Base: not protected → <c>false</c> (caller writes in place).
    /// <paramref name="image"/> is the page's live or staging buffer and is mutated in place (generation + checksum).
    /// Called from both write paths (<see cref="SavePages"/> structural, <see cref="WritePagesForCheckpoint"/> checkpoint).
    /// </summary>
    protected virtual unsafe bool TryPersistProtectedPage(int filePageIndex, byte* image) => false;

    /// <summary>
    /// Whether a file page is a protected segment-directory page (CK-05, C2) — i.e. its writes must be redirected to an
    /// alternate slot. Lets the structural-save path partition such pages out before coalescing contiguous in-place writes.
    /// Base: false. A derived store returns true for any page currently registered in its directory-pair state.
    /// </summary>
    protected virtual bool IsProtectedPage(int filePageIndex) => false;

    // ─── Page-counter census (CP-13 / #817) ──────────────────────────────────────────────────────────────────────
    // Read with the workload QUIESCENT. That is the whole point: while anything is in flight, a counter that is too
    // high is indistinguishable from one that is legitimately busy, which is why an ACW leak survived 5 000+ tests
    // and only surfaced as a WalBackPressureTimeout ten minutes into a demo run.

    /// <summary>Live <see cref="PageInfo.ActiveChunkWriters"/> for a page. Non-zero at quiesce proves a leaked registration.</summary>
    internal int ActiveChunkWritersOf(int memPageIndex) => Volatile.Read(ref _memPagesInfo[memPageIndex].ActiveChunkWriters);

    /// <summary>Pages holding a writer registration. Must be zero at quiesce (CP-13).</summary>
    internal int CountPagesWithActiveChunkWriters()
    {
        var n = 0;
        for (var i = 0; i < _memPagesInfo.Length; i++)
        {
            if (Volatile.Read(ref _memPagesInfo[i].ActiveChunkWriters) != 0)
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>Live <see cref="PageInfo.DirtyCounter"/> for a page.</summary>
    internal int DirtyCounterOf(int memPageIndex) => Volatile.Read(ref _memPagesInfo[memPageIndex].DirtyCounter);

    /// <summary>Pages still dirty, the cache size, and the lowest dirty page index (-1 if none).</summary>
    internal (int Dirty, int Total, int FirstDirtyPage) CountDirtyPages()
    {
        var n = 0;
        var first = -1;
        for (var i = 0; i < _memPagesInfo.Length; i++)
        {
            if (Volatile.Read(ref _memPagesInfo[i].DirtyCounter) > 0)
            {
                n++;
                if (first < 0)
                {
                    first = i;
                }
            }
        }
        return (n, _memPagesInfo.Length, first);
    }

    /// <summary>
    /// Records that this page's bytes changed, so the next checkpoint must write it and no one may evict it until then.
    /// </summary>
    /// <remarks>
    /// The whole writeback contract is this one bump. It is idempotent in effect (any number of modifications between two
    /// captures owe exactly one write) but strictly ordered against the capture: a bump that lands after the checkpoint
    /// sampled <see cref="PageInfo.WritebackGen"/> leaves the page owed, which is precisely CP-04's re-dirty defence.
    /// Callers that also need eviction protection for a mutation still in flight take a ChangeSet mark on top — that is a
    /// separate concern and a separate field.
    /// </remarks>
    internal void MarkPageModified(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        Interlocked.Increment(ref pi.WritebackGen);
    }

    /// <summary>
    /// Publishes that the bytes this page held at <paramref name="capturedGen"/> are durable on the data file.
    /// </summary>
    /// <remarks>
    /// Monotonic: a stale or duplicate publication can never walk <see cref="PageInfo.CapturedGen"/> backwards, so two
    /// writers racing on the same page settle on the newer capture rather than resurrecting an older one. Called only
    /// after the fsync that made <paramref name="capturedGen"/>'s bytes durable (CP-03).
    /// </remarks>
    internal void MarkCaptured(int memPageIndex, long capturedGen)
    {
        var pi = _memPagesInfo[memPageIndex];
        SpinWait sw = default;
        while (true)
        {
            var current = Volatile.Read(ref pi.CapturedGen);
            if (current >= capturedGen)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref pi.CapturedGen, capturedGen, current) == current)
            {
                if (Volatile.Read(ref pi.DirtyCounter) == 0 && !HasWritebackDebt(memPageIndex))
                {
                    _backpressureStrategy.SignalPageAvailable();
                }
                return;
            }

            sw.SpinOnce();
        }
    }

    /// <summary>Whether this page holds bytes that are not yet on the data file.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool HasWritebackDebt(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        return Volatile.Read(ref pi.WritebackGen) != Volatile.Read(ref pi.CapturedGen);
    }

    /// <summary>Live <see cref="PageInfo.WritebackGen"/> for a page. Diagnostic.</summary>
    internal long WritebackGenOf(int memPageIndex) => Volatile.Read(ref _memPagesInfo[memPageIndex].WritebackGen);

    /// <summary>Live <see cref="PageInfo.CapturedGen"/> for a page. Diagnostic.</summary>
    internal long CapturedGenOf(int memPageIndex) => Volatile.Read(ref _memPagesInfo[memPageIndex].CapturedGen);

    /// <summary>Pages owing a writeback, i.e. modified since their last durable capture. Diagnostic.</summary>
    internal int CountPagesWithWritebackDebt()
    {
        var n = 0;
        for (var i = 0; i < _memPagesInfo.Length; i++)
        {
            if (HasWritebackDebt(i))
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Percentage (0-100) of the page cache's slots that owe a writeback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Denominated in TOTAL slots, not resident ones. The question this answers is "can the next allocation find a slot
    /// to take", and a Free slot is as available as an evictable one — measuring against residency would report 100 %
    /// on a cache that is mostly empty and trigger checkpoints on a database that has barely started.
    /// </para>
    /// <para>
    /// One linear pass, same shape and cost as <see cref="CollectDirtyMemPageIndices"/>, which already runs once per
    /// checkpoint cycle. Called from the checkpoint thread's poll, so it is off every hot path. A maintained counter was
    /// considered and rejected: it would have to detect the clean→owed edge inside <see cref="MarkPageModified"/> on the
    /// commit path, and a racing counter that drifts is worse than an exact scan nobody is waiting on.
    /// </para>
    /// </remarks>
    internal int WritebackDebtPercent()
    {
        var pages = _memPagesInfo;
        if (pages == null || pages.Length == 0)
        {
            return 0;
        }

        // Read through the captured array, NOT via HasWritebackDebt(i) — that helper re-reads _memPagesInfo on every
        // call, and Dispose nulls it. This runs on the checkpoint thread, which is still polling while the engine tears
        // down, so a scan that re-read the field would NRE on a perfectly ordinary shutdown.
        var owed = 0;
        for (var i = 0; i < pages.Length; i++)
        {
            var pi = pages[i];
            if (pi != null && Volatile.Read(ref pi.WritebackGen) != Volatile.Read(ref pi.CapturedGen))
            {
                owed++;
            }
        }

        return (int)((owed * 100L) / pages.Length);
    }

    /// <summary>Lowest-indexed resident (non-Free) page, or -1. Test seam.</summary>
    /// <remarks>
    /// Tests that need "a real page" must ask for one rather than inventing an index: a synthetic index runs off the end
    /// of the live page-state array and tears the host down instead of failing an assertion.
    /// </remarks>
    internal int FirstResidentPage()
    {
        for (var i = 0; i < _memPagesInfo.Length; i++)
        {
            if (_memPagesInfo[i].PageState != PageState.Free && _memPagesInfo[i].FilePageIndex > 0)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Lowest-indexed resident page owing a writeback, or -1. Diagnostic / test seam.</summary>
    internal int FirstPageWithWritebackDebt()
    {
        for (var i = 0; i < _memPagesInfo.Length; i++)
        {
            if (_memPagesInfo[i].PageState != PageState.Free && HasWritebackDebt(i))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Total page-cache slots, resident or not. Diagnostic.</summary>
    internal int PageCacheSlotCountForDiagnostics => _memPagesInfo?.Length ?? 0;

    /// <summary>Highest writeback-debt page count observed while the cache was under back-pressure. Diagnostic.</summary>
    internal int PeakBackpressureDebt;

    /// <summary>Highest epoch-held page count observed while the cache was under back-pressure. Diagnostic.</summary>
    /// <remarks>
    /// Epoch holds are bounded by how long a scope stays open, not by anything the storage layer does — so unlike debt or
    /// writer registrations, this number cannot be brought down by checkpointing harder. A high value here means some
    /// scope is living long enough to pin everything it has touched, and the fix is in the scope's lifetime.
    /// </remarks>
    internal int PeakBackpressureEpochHeld;

    /// <summary>
    /// The full unevictability breakdown, taken in ONE pass so the parts are mutually consistent.
    /// </summary>
    /// <remarks>
    /// A page is unevictable if ANY of the four holds, so the individual counts overlap and do not sum — <c>Unevictable</c>
    /// is the union and is the only one that answers "how much of the cache is unavailable". Reporting the parts
    /// separately is what distinguishes the failure modes: writeback debt means the checkpoint is behind, ACW means a
    /// writer is mid-mutation, and epoch means some scope is old enough to pin everything touched since it opened —
    /// which, unlike the other three, is bounded by transaction LIFETIME rather than by anything the storage layer does.
    /// </remarks>
    internal (int Debt, int Acw, int SlotRef, int EpochHeld, int Unevictable, int Total) CountUnevictablePages()
    {
        var pages = _memPagesInfo;
        if (pages == null)
        {
            return (0, 0, 0, 0, 0, 0);
        }

        var minActiveEpoch = EpochManager?.MinActiveEpoch ?? long.MaxValue;
        int debt = 0, acw = 0, slotRef = 0, epochHeld = 0, unevictable = 0, total = 0;

        // Captured array throughout — see WritebackDebtPercent: Dispose nulls the field, and every helper that re-reads
        // it is a shutdown NRE waiting for the right interleaving.
        for (var i = 0; i < pages.Length; i++)
        {
            var pi = pages[i];
            if (pi == null || pi.PageState == PageState.Free)
            {
                continue;
            }

            total++;
            var d = Volatile.Read(ref pi.WritebackGen) != Volatile.Read(ref pi.CapturedGen);
            var a = Volatile.Read(ref pi.ActiveChunkWriters) != 0;
            var s = Volatile.Read(ref pi.SlotRefCount) > 0;
            var e = Volatile.Read(ref pi.AccessEpoch) >= minActiveEpoch;

            if (d) { debt++; }
            if (a) { acw++; }
            if (s) { slotRef++; }
            if (e) { epochHeld++; }
            if (d || a || s || e) { unevictable++; }
        }

        return (debt, acw, slotRef, epochHeld, unevictable, total);
    }

    /// <summary>Pages holding outstanding mutator marks. At quiesce this must be zero — see the conservation rule.</summary>
    internal int CountPagesWithDirtyMarks()
    {
        var n = 0;
        for (var i = 0; i < _memPagesInfo.Length; i++)
        {
            if (Volatile.Read(ref _memPagesInfo[i].DirtyCounter) != 0)
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Takes one mutator mark on a page: it is being modified, and the modifier will release the mark when it is done.
    /// </summary>
    /// <remarks>
    /// Also records the modification (<see cref="MarkPageModified"/>) — every mark implies changed bytes, and keeping the
    /// two together here means no caller can take a mark and forget to owe the write.
    /// </remarks>
    internal void IncrementDirty(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        Debug.Assert(pi.PageState is PageState.Exclusive or PageState.Idle, "We can't increment the dirty counter for a page that is not Exclusive or Idle.");
        if (DirtyTracePage == memPageIndex)
        {
            RecordDcTrace(+1);
        }
        Interlocked.Increment(ref pi.WritebackGen);
        Interlocked.Increment(ref pi.DirtyCounter);
    }

    /// <summary>
    /// Releases one mutator mark. Only the ChangeSet that took the mark may call this, and it must release exactly as many
    /// as it took — the writeback obligation is <see cref="PageInfo.WritebackGen"/>'s business, never this counter's.
    /// </summary>
    internal void DecrementDirty(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        if (DirtyTracePage == memPageIndex)
        {
            RecordDcTrace(-1);
        }
        var newVal = Interlocked.Decrement(ref pi.DirtyCounter);
        Debug.Assert(newVal >= 0, $"DirtyCounter went negative on page {memPageIndex}: a mark was released twice, or by someone who never took it.");
        if (newVal == 0 && !HasWritebackDebt(memPageIndex))
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
        if (AcwTracePage == memPageIndex)
        {
            RecordAcwTrace(+1);
        }
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
    /// Called by <see cref="ChunkAccessor{TStore}.CommitChanges"/> and <see cref="ChunkAccessor{TStore}.EvictSlot"/>
    /// when a dirty slot is flushed to the <see cref="ChangeSet"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void DecrementActiveChunkWriters(int memPageIndex)
    {
        if (AcwTracePage == memPageIndex)
        {
            RecordAcwTrace(-1);
        }
        Interlocked.Decrement(ref _memPagesInfo[memPageIndex].ActiveChunkWriters);
    }

    // ─── #817 ACW balance tracing ────────────────────────────────────────────────────────────────────────────────
    // Reading the code found four balanced paths and no leak, yet the counter demonstrably never returns to zero.
    // So: make the leak name its own call site. Every increment and decrement for ONE page is bucketed by call
    // stack; any signature whose increments outnumber its decrements IS the unbalanced path. Off unless a page is
    // selected, and deliberately not thread-safe beyond the lock — this is a diagnostic, not a shipping feature.

    /// <summary>Page to trace ACW increments/decrements for, or -1 (off). Diagnostic only (#817).</summary>
    internal static int AcwTracePage = -1;

    private static readonly object AcwTraceLock = new();
    private static readonly Dictionary<string, (int Inc, int Dec)> AcwTraceBuckets = [];

    private static void RecordAcwTrace(int delta)
    {
        // Skip the two frames of this helper and its caller so the signature starts at the code that actually
        // decided to register a writer.
        var st = new StackTrace(2, false);
        var sb = new StringBuilder();
        var frames = Math.Min(6, st.FrameCount);
        for (var i = 0; i < frames; i++)
        {
            var m = st.GetFrame(i)?.GetMethod();
            if (m == null)
            {
                continue;
            }
            if (i > 0)
            {
                sb.Append(" <- ");
            }
            sb.Append(m.DeclaringType?.Name).Append('.').Append(m.Name);
        }
        var key = sb.ToString();
        lock (AcwTraceLock)
        {
            AcwTraceBuckets.TryGetValue(key, out var e);
            AcwTraceBuckets[key] = delta > 0 ? (e.Inc + 1, e.Dec) : (e.Inc, e.Dec + 1);

            // Outstanding-increment ledger. The bucket table above cannot pair anything — increments and
            // decrements have structurally different stacks — but this can: push on increment, pop on decrement,
            // and whatever survives to a QUIESCED end of run is, by definition, an increment that was never
            // released. That set names the leaking call site directly.
            if (delta > 0)
            {
                AcwOutstanding.Add(key);
            }
            else if (AcwOutstanding.Count > 0)
            {
                AcwOutstanding.RemoveAt(AcwOutstanding.Count - 1);
            }
        }
    }

    private static readonly List<string> AcwOutstanding = [];

    // ─── DirtyCounter balance tracing ────────────────────────────────────────────────────────────────────────────
    // Same ledger, applied to DC. Note DC is NOT strictly conserved by design — DecrementDirtyToMin and
    // DecrementDirtyByDelta both CLAMP, so an over-decrement is silently absorbed while a MISSING decrement leaks
    // permanently. That asymmetry is why a leak here shows up as a dirty-page count that only ever climbs, and why
    // the surviving increments at a quiesced end of run are the ones worth reading.

    /// <summary>Page to trace DirtyCounter mutations for, or -1 (off). Diagnostic only.</summary>
    internal static int DirtyTracePage = -1;

    private static readonly List<string> DcOutstanding = [];

    private static void RecordDcTrace(int delta)
    {
        if (delta == 0)
        {
            return;
        }
        var key = delta > 0 ? CaptureStack() : null;
        lock (AcwTraceLock)
        {
            for (var i = 0; i < Math.Abs(delta); i++)
            {
                if (delta > 0)
                {
                    DcOutstanding.Add(key);
                }
                else if (DcOutstanding.Count > 0)
                {
                    DcOutstanding.RemoveAt(DcOutstanding.Count - 1);
                }
            }
        }
    }

    /// <summary>DirtyCounter increment sites still outstanding at end of run — the leak, named.</summary>
    internal static string DescribeDirtyOutstanding()
    {
        lock (AcwTraceLock)
        {
            if (DcOutstanding.Count == 0)
            {
                return "  (none outstanding — page balanced)";
            }
            var sb = new StringBuilder();
            foreach (var g in DcOutstanding.GroupBy(s => s).OrderByDescending(g => g.Count()))
            {
                sb.Append('\n').Append($"  LEAKED x{g.Count(),-4} {g.Key}");
            }
            return sb.ToString();
        }
    }

    private static string CaptureStack()
    {
        var st = new StackTrace(2, false);
        var sb = new StringBuilder();
        var frames = Math.Min(7, st.FrameCount);
        for (var i = 0; i < frames; i++)
        {
            var m = st.GetFrame(i)?.GetMethod();
            if (m == null)
            {
                continue;
            }
            if (i > 0)
            {
                sb.Append(" <- ");
            }
            sb.Append(m.DeclaringType?.Name).Append('.').Append(m.Name);
        }
        return sb.ToString();
    }

    /// <summary>Increment call sites still outstanding at end of run — the leak, named (#817).</summary>
    internal static string DescribeAcwOutstanding()
    {
        lock (AcwTraceLock)
        {
            if (AcwOutstanding.Count == 0)
            {
                return "  (none outstanding — page balanced)";
            }
            var sb = new StringBuilder();
            foreach (var g in AcwOutstanding.GroupBy(s => s).OrderByDescending(g => g.Count()))
            {
                sb.Append('\n').Append($"  LEAKED x{g.Count(),-4} {g.Key}");
            }
            return sb.ToString();
        }
    }

    /// <summary>Call-stack signatures whose ACW increments and decrements do not balance (#817).</summary>
    internal static string DescribeAcwImbalance()
    {
        lock (AcwTraceLock)
        {
            if (AcwTraceBuckets.Count == 0)
            {
                return "no ACW traces captured (set AcwTracePage)";
            }
            var sb = new StringBuilder();
            foreach (var kv in AcwTraceBuckets.OrderByDescending(k => k.Value.Inc - k.Value.Dec))
            {
                sb.Append('\n').Append($"  inc {kv.Value.Inc,6}  dec {kv.Value.Dec,6}  net {kv.Value.Inc - kv.Value.Dec,6}   {kv.Key}");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Releases <paramref name="delta"/> mutator marks that this caller previously took, in one CAS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O(1) regardless of <paramref name="delta"/>, where looping <see cref="DecrementDirty"/> is O(delta) and a hot page
    /// in a long-running unit of work can carry thousands of marks.
    /// </para>
    /// <para>
    /// Callers must pass exactly the number of marks they hold. The clamp at zero is a belt-and-braces guard against a
    /// caller that over-releases, not a licence to: an over-release is a conservation bug and the DEBUG assert below names
    /// it rather than absorbing it silently, which is how the old cap-to-1 primitive hid #385 for so long.
    /// </para>
    /// </remarks>
    internal void DecrementDirtyByDelta(int memPageIndex, int delta)
    {
        if (delta <= 0)
        {
            return;
        }

        // The page table is gone: the store was disposed while a unit of work was still open, which is legal and
        // deliberately exercised (a transaction outliving Dispose). There is no counter left to balance and nothing left
        // to protect, so releasing is vacuously complete. Checked here rather than at every call site because release is
        // the ONE operation that can legitimately arrive after teardown — a mark is taken while the store is alive by
        // construction, but it is returned whenever its owner happens to unwind.
        var pages = _memPagesInfo;
        if (pages == null)
        {
            return;
        }

        var pi = pages[memPageIndex];
        SpinWait sw = default;
        while (true)
        {
            var current = Volatile.Read(ref pi.DirtyCounter);
            if (current == 0)
            {
                Debug.Assert(false, $"DirtyCounter release of {delta} on page {memPageIndex} found the counter already at 0 — marks were released by someone who did not take them.");
                return;
            }

            Debug.Assert(current >= delta, $"DirtyCounter release of {delta} on page {memPageIndex} exceeds the {current} outstanding marks — releasing marks taken by another owner.");
            var newVal = current > delta ? current - delta : 0;
            if (Interlocked.CompareExchange(ref pi.DirtyCounter, newVal, current) == current)
            {
                if (DirtyTracePage == memPageIndex)
                {
                    RecordDcTrace(newVal - current);
                }
                if (newVal == 0 && !HasWritebackDebt(memPageIndex))
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
        FlushToDiskInterceptor?.Invoke();   // test-only: records the fsync barrier for crash simulation
        // TestMode skips the physical fsync (FlushFileBuffers): a unit test lives in one process, so its writes remain visible via the OS page cache even
        // without a sync — the sync only matters for real power-loss durability, which same-process tests can't exercise (crash-simulation fixtures reproduce a
        // crash via interceptors + the clean-shutdown marker, not by dropping the OS cache). The interceptor above still fires, so barrier-counting crash tests
        // are unaffected. This is the single largest test-suite lever (fsync dominated wall-clock). Production and any fixture that leaves TestMode off still sync.
        if (!Options.TestMode && _fileHandle != null && !_fileHandle.IsInvalid)
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
            if (pi != null && HasWritebackDebt(i) && pi.PageState != PageState.Free && !IsExternallyPersisted(pi.FilePageIndex))
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
    /// <returns>True if a consistent snapshot was obtained; false if the page was skipped — either because a real exclusive writer held the modification
    /// counter odd for longer than the checkpoint skip threshold (100ms), or because the counter was odd on a page no writer holds (a stale counter, skipped
    /// immediately). Skipping is safe: the page remains dirty and will be captured in the next checkpoint cycle.</returns>
    /// <summary>
    /// Why the last <see cref="CopyPageWithSeqlock"/> call declined: 0 = success, 2 = writer held &gt; 100 ms,
    /// 3 = stale odd counter. Diagnostic only (#817) — written and read on the checkpoint thread alone, so no
    /// synchronisation is required or implied.
    /// </summary>
    internal int LastSeqlockSkipReason;

    /// <summary>Cumulative checkpoint page-skip counts by cause (#817). Diagnostic only.</summary>
    internal long CheckpointSkipAcw;
    internal long CheckpointSkipWriterHeld;
    internal long CheckpointSkipStaleCounter;

    private unsafe bool CopyPageWithSeqlock(byte* pageAddr, byte* destAddr, int memPageIndex)
    {
        LastSeqlockSkipReason = 0;
        var sw = new SpinWait();
        long oddSpinStart = 0;
        while (true)
        {
            // Read the modification counter (must be even = quiescent). Acquire load: the page-data loads below must not be hoisted above this snapshot
            // (SL-06). Free on x64 (TSO — plain mov); emits ldar on arm64.
            var counter = Volatile.Read(ref ((PageBaseHeader*)pageAddr)->ModificationCounter);
            if ((counter & 1) != 0)
            {
                // A real writer sets PageState=Exclusive BEFORE making the counter odd (TryLatchPageExclusive) and makes it even BEFORE clearing Exclusive
                // (UnlatchPageExclusive). So an odd counter on a page that is NOT Exclusive-latched is stale — there is no writer to wait for. Skip at once
                // instead of burning the full 100ms timeout. Defensive: TryAcquire resets the counter to even on slot reuse, so a quiescent page should never
                // present odd here.
                //
                // That implication is only sound because BOTH sides are now ordered (#579): the writer's Interlocked increment keeps the PageState=Exclusive
                // store from sinking past the odd-counter store, and the acquire load above keeps this PageState load from being hoisted above the counter
                // snapshot. It previously rested on x64 TSO alone, so on arm64 a live writer could be misclassified as stale — bounded (the page stays dirty
                // and is retried next cycle) but an explicit x64-only assumption in a protocol required to be arm64-correct.
                if (_memPagesInfo[memPageIndex].PageState != PageState.Exclusive)
                {
                    LogStaleSeqlockCounterSkip(Logger, memPageIndex, counter);
                    LastSeqlockSkipReason = 3;
                    CheckpointSkipStaleCounter++;
                    return false;
                }

                // A real exclusive writer holds the page — track how long we've been waiting
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
                        LogSeqlockWriterHeldSkip(Logger, (int)elapsedMs, counter);
                        LastSeqlockSkipReason = 2;
                        CheckpointSkipWriterHeld++;
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

            // The memcpy's PLAIN loads sit between the counter snapshot and this validating re-read. An acquire load only stops LATER accesses from hoisting
            // above it — it does NOT stop those earlier loads from sinking BELOW the validating load on a weakly-ordered CPU. If they sink, the validation
            // checks a counter read that happened before the data it is meant to be validating, and the protocol degenerates to "read, copy, hope": the check
            // cannot fail, and a torn copy is then CRC-stamped over the torn bytes and written, defeating ADR-015 checksum validation on reload.
            //
            // x64 (TSO) orders loads in program order, so the fence is needed only off-x86. X86Base.IsSupported is a JIT-time constant, so this folds to
            // nothing on x64 and emits dmb ish on arm64 — the same shape as OlcLatch.ValidateVersion, which names this identical hazard (#579).
            if (!X86Base.IsSupported)
            {
                Interlocked.MemoryBarrier();
            }

            // Validate counter hasn't changed (no torn read)
            if (Volatile.Read(ref ((PageBaseHeader*)pageAddr)->ModificationCounter) == counter)
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
    /// <param name="capturedGen">
    /// Optional, aligned with the WRITTEN prefix of <paramref name="memPageIndices"/> on return: the page's
    /// <see cref="PageInfo.WritebackGen"/> as observed at the instant of capture, under the ACW sentinel. After the fsync
    /// that makes these bytes durable, the caller publishes each value via <see cref="MarkCaptured"/> — which is what
    /// discharges the page's writeback debt.
    /// <para>
    /// Sampling BEFORE the copy is what makes CP-04's re-dirty defence automatic: a modification that lands between this
    /// read and the copy bumps <see cref="PageInfo.WritebackGen"/> past the sampled value, so publishing the sample leaves
    /// the page still owed and the next cycle rewrites it. Sampling after the copy would discharge a debt the copy does
    /// not cover, which is the lost-write shape of #385.
    /// </para>
    /// </param>
    unsafe internal void WritePagesForCheckpoint(int[] memPageIndices, StagingBufferPool stagingPool, out int writtenCount, long[] capturedGen = null)
    {
        writtenCount = 0;

        if (memPageIndices.Length == 0)
        {
            return;
        }

        Logger.LogInformation("Checkpoint: writing {PageCount} dirty pages", memPageIndices.Length);

        var memPageBaseAddr = _memPagesAddr;

        // CK-02 ordering (#585). A protected segment-directory page (CK-05) persists itself inside TryPersistProtectedPage as write → fsync → slot flip, and that
        // fsync is FILE-WIDE, not page-scoped. Reached at an arbitrary position in this array it therefore makes every PLAIN data page written earlier in the same
        // pass durable — while the cycle's flush2 barrier has not run yet, because CheckpointManager issues RequestFlush + WaitForDurable only after this method
        // returns. Any commit that appended and published between the step-1 barrier and that page's capture would then be sitting in the data file with its WAL
        // record still in the ring buffer: "captured ⊆ durable" inverted, i.e. a phantom partial write of a never-durable transaction, and Typhon has no undo.
        //
        // Hoisting protected pages to the front makes their fsync always PRECEDE the plain writes of this pass instead of following them, so it can only ever
        // flush bytes that a previous pass already barriered and fsynced. SavePages reaches the same guarantee by persisting protected pages in a dedicated
        // pre-pass; expressing it here as an ordering keeps this method's in-place written-front/skipped-back partition (CK-03 retry contract) intact.
        //
        // Cheap in the common case: one FilePageIndex probe per page and zero swaps when the batch holds no directory page, which is the usual shape — they only
        // go dirty on segment create/grow.
        var plainWrittenThisPass = false;
        var protectedCount = 0;
        for (int i = 0; i < memPageIndices.Length; i++)
        {
            var probeFilePageIndex = _memPagesInfo[memPageIndices[i]].FilePageIndex;
            if (probeFilePageIndex > 0 && IsProtectedPage(probeFilePageIndex))
            {
                (memPageIndices[protectedCount], memPageIndices[i]) = (memPageIndices[i], memPageIndices[protectedCount]);
                protectedCount++;
            }
        }

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
                CheckpointSkipAcw++;   // #817 diagnostic: cause A — a live chunk writer held the page
                continue;
            }

            // Rent a staging buffer and snapshot the live page via seqlock.
            // No concurrent OLC writers can start while ACW = -1 (they spin-wait).
            // Page-level latches (TryLatchPageExclusive) are still detected by the seqlock.
            // Read under the ACW sentinel (ACW == -1 blocks new writers), so this is the generation the copy below
            // actually covers — not a racing sample.
            var genAtCapture = Volatile.Read(ref pi.WritebackGen);

            using var staging = stagingPool.Rent();
            if (!CopyPageWithSeqlock(livePageAddr, staging.Pointer, memPageIndex))
            {
                // Page has an active writer (via TryLatchPageExclusive) — skip it. The page stays dirty and will be picked up in the next checkpoint cycle.
                // This prevents deadlock when the writer is blocked on backpressure waiting for THIS checkpoint to free pages.
                Interlocked.Exchange(ref pi.ActiveChunkWriters, 0); // Release sentinel
                continue;
            }

            // Release the sentinel — writers can resume.
            Interlocked.Exchange(ref pi.ActiveChunkWriters, 0);

            // Increment ChangeRevision and compute CRC on the staging copy (not the live page).
            var filePageIndex = pi.FilePageIndex;
            var redirected = false;
            if (filePageIndex > 0)
            {
                var stagingHeader = (PageBaseHeader*)staging.Pointer;
                ++stagingHeader->ChangeRevision;

                // CK-05 (C2): a dirty segment-directory page is redirected to its alternate slot (PairGeneration + CRC stamped
                // on this staging copy, write, fsync, flip — all inside TryPersistProtectedPage). Non-directory pages fall
                // through to the normal in-place CRC + write below.
                redirected = TryPersistProtectedPage(filePageIndex, staging.Pointer);
                if (redirected)
                {
                    CheckpointProtectedPagePersistCount++;
                    if (plainWrittenThisPass)
                    {
                        CheckpointProtectedAfterPlainWriteCount++;   // CK-02 escape — see the field docs
                    }
                }
                else
                {
                    StampPageForWrite(staging.Span, filePageIndex);
                }
            }

            // Write staging buffer to the data file (synchronous — checkpoint runs on dedicated thread). Skipped when the
            // page was already written (and fsynced) to its alternate slot by the protected-page redirect above.
            if (!redirected)
            {
                PageWriteInterceptor?.Invoke(filePageIndex);   // test-only crash injection; throws to abort the checkpoint mid-cycle
                plainWrittenThisPass = true;
                var pageOffset = filePageIndex * (long)PageSize;
                RandomAccess.Write(_fileHandle, staging.Span, pageOffset);
                TrackFileGrowth(pageOffset + PageSize);
            }

            // Partition in place: written pages to the front [0, writtenCount), skipped pages to the back [writtenCount, length). Swap (never overwrite) so the
            // caller can retry exactly the skipped tail in a later pass (coverage gate, CK-03) instead of losing track of which pages still need writing.
            memPageIndices[i] = memPageIndices[writtenCount];
            memPageIndices[writtenCount] = memPageIndex;
            if (capturedGen != null && writtenCount < capturedGen.Length)
            {
                capturedGen[writtenCount] = genAtCapture;
            }
            writtenCount++;

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

        var memPageBaseAddr = _memPagesAddr;

        // We want to generate as few IO operations as possible, so we sort the pages to identify the ones that are contiguous in the file
        Array.Sort(memPageIndices, (x, y) => x - y);

        // Sample each page's writeback generation BEFORE anything is written, and publish these exact values once the fsync
        // below has made the bytes durable. Same contract as the checkpoint's capture, same reason: a modification landing
        // after this sample leaves the page owed, so it is rewritten rather than silently treated as clean. Sampled after the
        // sort so the array stays index-aligned with memPageIndices.
        var gensAtEntry = new long[memPageIndices.Length];
        for (int i = 0; i < memPageIndices.Length; i++)
        {
            gensAtEntry[i] = Volatile.Read(ref _memPagesInfo[memPageIndices[i]].WritebackGen);
        }

        // CK-05 (C2): protected segment-directory pages must be written to their ALTERNATE slot (gen+1 + CRC + fsync + flip,
        // all inside PersistProtectedPage) — they cannot be coalesced with their in-place neighbors. Handle them individually
        // here and build `normalPages` (sorted, protected pages removed) for the coalesced in-place path below. memPageIndices
        // is left intact so the continuation's DecrementDirty still releases the protected pages' DirtyCounter.
        // The common structural flush touches ZERO protected pages (pure data), so scan first — a cheap dictionary probe per
        // page, no allocation — and only build the partitioned `normalPages` list when a protected page is actually present.
        // (The previous version allocated a full-length List<int> on EVERY flush and discarded it whenever protectedCount==0.)
        // The same partition must ALSO exclude externally-persisted pages (the CK-05 meta pair). They are written only by
        // PersistMetaNow, which alternates slots and stamps a fresh CRC; the in-place path below skips the CRC stamp for
        // file page 0 (its `FilePageIndex > 0` guard) but did NOT skip the WRITE, so a structural flush that happened to
        // carry logical page 0 overwrote meta slot 0 with an image whose stored checksum no longer matched its content.
        // The pair then silently ran on one copy: the surviving slot still opened the database, so nothing complained —
        // until that slot tore too, at which point BOTH slots read invalid and the database became permanently unopenable.
        // That is precisely the failure CK-05 exists to make impossible, so the exclusion belongs here and not only in
        // CollectDirtyMemPageIndices (which had it all along — this path is fed by a ChangeSet, not by that scan).
        int[] normalPages = memPageIndices;
        var needsPartition = false;
        for (int i = 0; i < memPageIndices.Length; i++)
        {
            var filePageIdx = _memPagesInfo[memPageIndices[i]].FilePageIndex;
            if (IsExternallyPersisted(filePageIdx) || (filePageIdx > 0 && IsProtectedPage(filePageIdx)))
            {
                needsPartition = true;
                break;
            }
        }

        if (needsPartition)
        {
            var normal = new List<int>(memPageIndices.Length);
            for (int i = 0; i < memPageIndices.Length; i++)
            {
                var pi = _memPagesInfo[memPageIndices[i]];
                if (IsExternallyPersisted(pi.FilePageIndex))
                {
                    // Owned by PersistMetaNow. Not written here at all — the continuation still releases its DirtyCounter.
                    continue;
                }

                if (pi.FilePageIndex > 0 && IsProtectedPage(pi.FilePageIndex))
                {
                    // Wait for any pending IO read so the live page is complete, bump ChangeRevision, then redirect the write.
                    var ioTask = pi.IOReadTask;
                    if (ioTask != null && !ioTask.IsCompletedSuccessfully)
                    {
                        ioTask.GetAwaiter().GetResult();
                    }

                    var headerAddr = (PageBaseHeader*)(memPageBaseAddr + (pi.MemPageIndex * (long)PageSize));
                    ++headerAddr->ChangeRevision;
                    TryPersistProtectedPage(pi.FilePageIndex, (byte*)headerAddr);
                }
                else
                {
                    normal.Add(memPageIndices[i]);
                }
            }

            // Sorted, protected pages removed; empty if every page was protected (handled by the length==0 branch below).
            normalPages = normal.ToArray();
        }

        if (normalPages.Length == 0)
        {
            // Every page was a protected directory page — already written + fsynced + flipped. Discharge their debt.
            for (int i = 0; i < memPageIndices.Length; i++)
            {
                MarkCaptured(memPageIndices[i], gensAtEntry[i]);
            }

            if (flushSpanId != 0)
            {
                TyphonEvent.EmitPageCacheFlushCompleted(flushSpanId, flushBeginTs, flushPageCount, Stopwatch.GetTimestamp());
            }

            return Task.CompletedTask;
        }

        var operations = new List<(int memPageIndex, int length)>();

        var curPageInfo = _memPagesInfo[normalPages[0]];
        var curOperation = (memPageIndex: normalPages[0], length: 1);

        for (int i = 1; i < normalPages.Length; i++)
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

                // Stamp identity + checksum over the updated page so the on-disk copy is self-consistent (CP-07 equivalent for SavePages)
                StampPageForWrite(new Span<byte>((byte*)headerAddr, PageSize), curPageInfo.FilePageIndex);
            }

            var nextMemPageIndex = normalPages[i];
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

            StampPageForWrite(new Span<byte>((byte*)headerAddr, PageSize), curPageInfo.FilePageIndex);
        }

        // Don't forget to add the last operation
        operations.Add(curOperation);

        // Highest byte offset this batch will have made durable once its async writes + fsync complete. SavePageInternal no longer advances
        // _fileSize (it is the async path); we advance it once in the continuation below, AFTER FlushToDisk and BEFORE DecrementDirty, so a
        // page is covered by the read gate's durable watermark before it can ever become evictable. Mirrors the per-write growth the
        // synchronous paths (WritePageDirect / WritePagesForCheckpoint) already do post-write.
        long batchEndOffset = 0;
        for (int i = 0; i < operations.Count; i++)
        {
            var opFilePageIdx = _memPagesInfo[operations[i].memPageIndex].FilePageIndex;
            var end = (opFilePageIdx + operations[i].length) * (long)PageSize;
            if (end > batchEndOffset)
            {
                batchEndOffset = end;
            }
        }

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

            // Now the batch's bytes are durable — advance the file-size watermark BEFORE any page becomes evictable (the debt discharge below). This keeps
            // _fileSize honest: the read gate never authorizes a disk read of a not-yet-written page (fixes the short-read race).
            TrackFileGrowth(batchEndOffset);

            for (int i = 0; i < memPageIndices.Length; i++)
            {
                MarkCaptured(memPageIndices[i], gensAtEntry[i]);
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

        // NOTE: file-growth tracking is deliberately NOT done here. This is the only ASYNC write path (WriteAsync below), so advancing
        // _fileSize here — before the bytes physically land — would let the read gate (FetchPageToMemoryOnMiss, "loadPage = offset+PageSize <=
        // _fileSize") authorize a disk read of a page whose WriteAsync hasn't extended the file yet, yielding a 0-byte read past EOF. _fileSize
        // must only ever reflect DURABLE bytes; SavePages advances it in its post-FlushToDisk continuation, before any page becomes evictable.
        PageWriteInterceptor?.Invoke(filePageIndex);   // test-only crash injection; throws to abort the async structural write
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

    /// <summary>
    /// Test/diagnostic invariant check for the seqlock protocol: a quiescent (<see cref="PageState.Idle"/>) page that participates in the seqlock must carry an
    /// EVEN <see cref="PageBaseHeader.ModificationCounter"/> — an odd value signals a write in progress, but an Idle page has no writer. Returns the number of
    /// such Idle slots whose counter is odd; a correct engine always returns 0. A non-zero result means a slot was reused without resetting the stale counter
    /// (guarded in <see cref="TryAcquire"/>), which makes <see cref="CopyPageWithSeqlock"/> spin-wait the skip timeout on a page nobody is writing.
    /// </summary>
    internal int CountQuiescentPagesWithOddSeqlock() => CollectQuiescentOddSeqlockDiagnostics().Count;

    /// <summary>
    /// Test/diagnostic companion to <see cref="CountQuiescentPagesWithOddSeqlock"/>: returns a per-page description of every stably-Idle-with-odd-counter slot.
    /// The full slot state (file page, counter value, dirty/ACW/refcount/epoch) is captured under <see cref="PageInfo.StateSyncRoot"/> so the snapshot is
    /// coherent. Externally-persisted pages (the CK-05 meta pair, <see cref="IsExternallyPersisted"/>) are EXCLUDED: they use a different header format, are
    /// written only by PersistMetaNow, and never flow through <see cref="CopyPageWithSeqlock"/> — the bytes at the ModificationCounter offset are meta/generation
    /// data, not a seqlock counter, so applying the invariant to them is a category error (they legitimately read "odd").
    /// </summary>
    internal unsafe List<string> CollectQuiescentOddSeqlockDiagnostics()
    {
        var hits = new List<string>();
        for (var i = 0; i < MemPagesCount; i++)
        {
            var pi = _memPagesInfo[i];
            if (pi.PageState != PageState.Idle)
            {
                continue;
            }
            // The meta pair does not participate in the seqlock (see the remark above) — its ModificationCounter offset holds directory/generation data. Skip it.
            if (IsExternallyPersisted(pi.FilePageIndex))
            {
                continue;
            }
            var hdr = (PageBaseHeader*)(_memPagesAddr + i * (long)PageSize);
            if ((hdr->ModificationCounter & 1) == 0)
            {
                continue;
            }
            pi.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
            var counter = hdr->ModificationCounter;
            var state = pi.PageState;
            var filePage = pi.FilePageIndex;
            var dirty = pi.DirtyCounter;
            var acw = pi.ActiveChunkWriters;
            var refCount = pi.SlotRefCount;
            var epoch = pi.AccessEpoch;
            var crc = pi.CrcVerified;
            pi.StateSyncRoot.ExitExclusiveAccess();
            if (state == PageState.Idle && (counter & 1) != 0)
            {
                hits.Add($"memPage={i} filePage={filePage} counter={counter} state={state} dirty={dirty} acw={acw} refCount={refCount} epoch={epoch} crcVerified={crc}");
            }
        }
        return hits;
    }

    /// <summary>Diagnostic snapshot of a page's protection state. Used by ChunkAccessor error reporting.</summary>
    internal (int DirtyCounter, int ActiveChunkWriters, int SlotRefCount, long AccessEpoch, PageState PageState, bool CrcVerified) GetPageInfoForDiagnostic(int memPageIndex)
    {
        var pi = _memPagesInfo[memPageIndex];
        return (pi.DirtyCounter, pi.ActiveChunkWriters, pi.SlotRefCount, pi.AccessEpoch, pi.PageState, pi.CrcVerified);
    }

    /// <summary>
    /// Diagnostic: the clock-sweep counter of a file page, or <c>-1</c> when the page is not resident. Used by the Database File Map tests to assert that
    /// <see cref="RequestPageEpochNoSweep"/> leaves the counter untouched.
    /// </summary>
    internal int GetClockSweepCounterForDiagnostic(int filePageIndex)
    {
        if (_memPageIndexByFilePageIndex.TryGetValue(filePageIndex, out var memPageIndex))
        {
            var pi = _memPagesInfo[memPageIndex];
            if (pi.FilePageIndex == filePageIndex)
            {
                return pi.ClockSweepCounter;
            }
        }

        return -1;
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
    /// Used by <see cref="ChunkAccessor{TStore}"/> to detect stale cached pointers after page eviction/reuse.
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