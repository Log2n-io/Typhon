using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Diagnostics;
using System.Threading;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Background thread that periodically writes dirty data pages to disk and advances <see cref="CheckpointLsn"/>, enabling WAL segment reclamation and
/// bounding crash-recovery replay time.
/// </summary>
/// <remarks>
/// <para>
/// The checkpoint pipeline per cycle:
/// <list type="number">
///   <item>Durability barrier: flush the WAL through <c>LastAppendedLsn</c>, then capture <c>barrierLsn = DurableLsn</c> (CK-01/CK-02)</item>
///   <item>Collect dirty memory page indices from the page cache</item>
///   <item>Write dirty pages to the data file (without decrementing DirtyCounter)</item>
///   <item>Flush the WAL through the post-capture <c>LastAppendedLsn</c> (CK-02), then fsync the data file</item>
///   <item>Decrement DirtyCounter for each written page (re-dirtied pages stay &gt; 0)</item>
///   <item>Transition UoW entries from WalDurable → Committed</item>
///   <item>Advance CheckpointLSN in the file header + fsync</item>
///   <item>Recycle WAL segments below CheckpointLSN</item>
/// </list>
/// </para>
/// <para>
/// Key invariant: <c>CheckpointLSN ≤ DurableLSN ≤ CurrentLSN</c>
/// </para>
/// <para>
/// Thread model follows <see cref="WalWriter"/>: dedicated <see cref="Thread"/>, <c>IsBackground=true</c>, <c>ThreadPriority.Normal</c>, named "Typhon-Checkpoint".
/// </para>
/// </remarks>
[PublicAPI]
internal sealed partial class CheckpointManager : ResourceNode, IMetricSource
{
    // ═══════════════════════════════════════════════════════════════
    // Dependencies
    // ═══════════════════════════════════════════════════════════════

    private readonly ManagedPagedMMF _mmf;
    private readonly UowRegistry _uowRegistry;
    private readonly WalManager _walManager;
    private readonly ResourceOptions _resourceOptions;
    private readonly EpochManager _epochManager;
    private readonly StagingBufferPool _stagingPool;
    private readonly Func<long> _lastTickFenceLsnProvider;

    // ═══════════════════════════════════════════════════════════════
    // Thread lifecycle
    // ═══════════════════════════════════════════════════════════════

    private Thread _thread;
    private volatile bool _shutdown;
    private readonly Lock _lifecycleLock = new();
    private readonly ManualResetEventSlim _wakeEvent = new(false);

    // ═══════════════════════════════════════════════════════════════
    // State
    // ═══════════════════════════════════════════════════════════════

    private long _checkpointLsn;
    private volatile Exception _fatalError;
    private volatile DurabilityHealth _health = DurabilityHealth.Ok;
    private volatile bool _forceRequested;
    private volatile bool _crashStop;
    private long _consecutiveGatedCycles;

    /// <summary>Engine logger (wired by <see cref="DatabaseEngine"/>). Defaults to a no-op so direct unit construction
    /// (e.g. CheckpointManagerTests) never NREs in the <c>[LoggerMessage]</c> paths.</summary>
    private ILogger _logger = NullLogger.Instance;

    /// <summary>Test seam (A1.12): when set, invoked once at the start of every cycle so a fixture can inject a
    /// transient/fatal fault to exercise CK-06 classification. Null in production — a single null-check on the
    /// background checkpoint thread, off every hot path.</summary>
    internal Action CycleFaultInjector { get; set; }

    /// <summary>Max passes per cycle to retry pages skipped because a writer was active, before the coverage gate blocks the LSN advance (CK-03). Skip windows are
    /// accessor-scoped (~µs), so the second pass almost always clears them.</summary>
    private const int MaxCoveragePasses = 3;

    // ═══════════════════════════════════════════════════════════════
    // Metrics
    // ═══════════════════════════════════════════════════════════════

    private long _totalCheckpoints;
    private long _totalPagesWritten;
    private long _totalSegmentsRecycled;
    private long _totalUowTransitioned;
    private long _lastDurationUs;
    private long _maxDurationUs;

    // ═══════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new Checkpoint Manager. Call <see cref="Start"/> to begin the background thread.
    /// </summary>
    /// <param name="mmf">The managed paged memory-mapped file (data file storage).</param>
    /// <param name="uowRegistry">Unit of Work registry for state transitions.</param>
    /// <param name="walManager">WAL manager for reading DurableLsn and segment reclamation.</param>
    /// <param name="resourceOptions">Configuration (CheckpointIntervalMs, CheckpointMaxDirtyPages).</param>
    /// <param name="epochManager">Epoch manager for page access.</param>
    /// <param name="stagingPool">Pre-allocated staging buffer pool for snapshot-based checkpoint writes.</param>
    /// <param name="parent">Parent resource node.</param>
    /// <param name="initialCheckpointLsn">Initial checkpoint LSN from file header (0 for fresh database).</param>
    internal CheckpointManager(ManagedPagedMMF mmf, UowRegistry uowRegistry, WalManager walManager, ResourceOptions resourceOptions, EpochManager epochManager,
        StagingBufferPool stagingPool, IResource parent, long initialCheckpointLsn = 0, Func<long> lastTickFenceLsnProvider = null) :
        base("CheckpointManager", ResourceType.WAL, parent)
    {
        ArgumentNullException.ThrowIfNull(mmf);
        ArgumentNullException.ThrowIfNull(uowRegistry);
        ArgumentNullException.ThrowIfNull(walManager);
        ArgumentNullException.ThrowIfNull(resourceOptions);
        ArgumentNullException.ThrowIfNull(epochManager);
        ArgumentNullException.ThrowIfNull(stagingPool);

        _mmf = mmf;
        _uowRegistry = uowRegistry;
        _walManager = walManager;
        _resourceOptions = resourceOptions;
        _epochManager = epochManager;
        _stagingPool = stagingPool;
        _lastTickFenceLsnProvider = lastTickFenceLsnProvider;
        _checkpointLsn = initialCheckpointLsn;
    }

    // ═══════════════════════════════════════════════════════════════
    // Public properties
    // ═══════════════════════════════════════════════════════════════

    /// <summary>The highest LSN that has been fully checkpointed (data pages on stable media).</summary>
    public long CheckpointLsn => Interlocked.Read(ref _checkpointLsn);

    /// <summary>Whether the checkpoint thread is currently running.</summary>
    public bool IsRunning => _thread != null && _thread.IsAlive;

    /// <summary>Whether a fatal I/O error has occurred during checkpointing.</summary>
    public bool HasFatalError => _fatalError != null;

    /// <summary>Current durability health (CK-06). <see cref="DurabilityHealth.Degraded"/> means the last cycle hit a
    /// transient stall and will retry next tick; <see cref="DurabilityHealth.Fatal"/> means periodic checkpointing
    /// has halted (the shutdown path still attempts one last-chance flush).</summary>
    public DurabilityHealth Health => _health;

    /// <summary>Sets the engine logger used by the CK-06 <c>[LoggerMessage]</c> paths. Null resets to a no-op logger.</summary>
    internal ILogger Logger { set => _logger = value ?? NullLogger.Instance; }

    /// <summary>Total number of checkpoint cycles completed.</summary>
    public long TotalCheckpoints => Interlocked.Read(ref _totalCheckpoints);

    /// <summary>Total number of dirty pages written across all checkpoints.</summary>
    public long TotalPagesWritten => Interlocked.Read(ref _totalPagesWritten);

    /// <summary>Total number of WAL segments reclaimed across all checkpoints.</summary>
    public long TotalSegmentsRecycled => Interlocked.Read(ref _totalSegmentsRecycled);

    /// <summary>
    /// Number of consecutive cycles whose coverage gate blocked the CheckpointLSN advance because a collected dirty page could not be captured (active writer).
    /// Reset to 0 by any fully-covered cycle. A sustained nonzero value flags a pinned writer starving checkpoint progress (CK-03 telemetry).
    /// </summary>
    public long ConsecutiveGatedCycles => Interlocked.Read(ref _consecutiveGatedCycles);

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Starts the checkpoint background thread. Idempotent — does nothing if already running.
    /// </summary>
    public void Start()
    {
        if (_thread != null && _thread.IsAlive)
        {
            return;
        }

        lock (_lifecycleLock)
        {
            if (_thread != null && _thread.IsAlive)
            {
                return;
            }

            _shutdown = false;
            _thread = new Thread(CheckpointLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Normal,
                Name = "Typhon-Checkpoint"
            };
            _thread.Start();
        }
    }

    /// <summary>
    /// Requests an immediate checkpoint cycle. Wakes the background thread if sleeping.
    /// </summary>
    public void ForceCheckpoint()
    {
        _forceRequested = true;
        _wakeEvent.Set();
    }

    /// <summary>
    /// Blocks until at least one checkpoint cycle has completed (since the call entered) or <paramref name="timeout"/>
    /// elapses. Returns <see langword="true"/> if a cycle completed, <see langword="false"/> on timeout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pairs with <see cref="ForceCheckpoint"/> to expose a synchronous checkpoint barrier. Used by <see cref="BulkLoadSession.CompleteBulkLoad"/> to drain
    /// the bulk's dirty pages to disk before emitting the <c>BulkEnd</c> manifest. Detection is via the monotonic <see cref="TotalCheckpoints"/> counter — the
    /// method records the count on entry and spin-waits (adaptively) until it advances.
    /// </para>
    /// <para>
    /// Concurrent waiters all observe the same cycle increment; this is safe (the counter is monotonic and the post-cycle invariants — CheckpointLSN advance,
    /// page fsync — hold by the time any waiter returns).
    /// </para>
    /// </remarks>
    /// <param name="timeout">Maximum wall-clock time to wait for the next cycle.</param>
    /// <returns><see langword="true"/> if a checkpoint completed, <see langword="false"/> on timeout.</returns>
    public bool WaitForCheckpoint(TimeSpan timeout)
    {
        var startCount = Interlocked.Read(ref _totalCheckpoints);
        return SpinWait.SpinUntil(() => Interlocked.Read(ref _totalCheckpoints) > startCount, timeout);
    }

    // ═══════════════════════════════════════════════════════════════
    // IMetricSource
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteThroughput("Checkpoints", _totalCheckpoints);
        writer.WriteThroughput("PagesWritten", _totalPagesWritten);
        writer.WriteThroughput("SegmentsRecycled", _totalSegmentsRecycled);
        writer.WriteThroughput("UowTransitioned", _totalUowTransitioned);
        writer.WriteDuration("CheckpointDuration", _lastDurationUs, 0, _maxDurationUs);
    }

    /// <inheritdoc />
    public void ResetPeaks() => _maxDurationUs = _lastDurationUs;

    // ═══════════════════════════════════════════════════════════════
    // Checkpoint loop (runs on dedicated thread)
    // ═══════════════════════════════════════════════════════════════

    private void CheckpointLoop()
    {
        try
        {
            while (!_shutdown)
            {
                // Phase 8: Durability:Checkpoint:Sleep span — covers the inter-cycle wait.
                // wakeReason: 0=timer, 1=force, 2=shutdown.
                var sleepMs = (uint)Math.Max(_resourceOptions.CheckpointIntervalMs, 0);
                var sleepScope = TyphonEvent.BeginDurabilityCheckpointSleep(sleepMs, 0);
                try
                {
                    // Sleep until woken by: timer expiry, ForceCheckpoint, or shutdown
                    _wakeEvent.Wait(_resourceOptions.CheckpointIntervalMs);
                    _wakeEvent.Reset();

                    sleepScope.WakeReason = _shutdown ? (byte)2 : (_forceRequested ? (byte)1 : (byte)0);
                }
                finally
                {
                    sleepScope.Dispose();
                }

                if (_shutdown)
                {
                    break;
                }

                // Skip if a previous cycle encountered a fatal error
                if (_fatalError != null)
                {
                    continue;
                }

                var force = _forceRequested;
                _forceRequested = false;

                // Check if we should run a checkpoint cycle
                var durableLsn = _walManager.DurableLsn;
                if (durableLsn <= Interlocked.Read(ref _checkpointLsn) && !force)
                {
                    // No new durable WAL records since last checkpoint and no force request
                    continue;
                }

                RunCheckpointCycle(durableLsn, force ? CheckpointReason.Forced : CheckpointReason.Periodic);
            }

            // Shutdown: run one final checkpoint cycle to flush all dirty pages.
            //
            // UNCONDITIONAL by design: CreateOrGrow and other structural-write paths bump DirtyCounter on segment pages WITHOUT writing WAL records.
            // Guarding the final cycle on `finalLsn > _checkpointLsn` skips those dirty pages whenever the last structural write came after the last
            // transaction commit + last periodic checkpoint.
            // _crashStop suppresses the shutdown flush for hard-crash simulation: the engine is modelling a power cut, so no final cycle may push dirty pages to
            // the data file (the committed data must survive only via WAL replay).
            //
            // CK-06 / 04 §7: the shutdown flush is attempted even after a fatal latch — a last-chance cycle may still
            // get committed data to the data file. Only _crashStop (power-cut simulation) suppresses it. Best-effort:
            // RunCheckpointCycle classifies its own failures and never rethrows, so this can't escape the loop.
            if (!_crashStop)
            {
                var finalLsn = _walManager.DurableLsn;
                RunCheckpointCycle(finalLsn, CheckpointReason.Shutdown);
            }
        }
        catch (Exception ex)
        {
            ClassifyCycleFailure(ex);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Checkpoint pipeline
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes one full checkpoint cycle. Visible internally for testability.
    /// </summary>
    internal void RunCheckpointCycle(long targetLsn, CheckpointReason reason = CheckpointReason.Periodic)
    {
        var sw = Stopwatch.GetTimestamp();

        // CheckpointCycle span wraps the full cycle. DirtyPageCount is set after collection (not known at span-begin time).
        var cycleScope = TyphonEvent.BeginCheckpointCycle(targetLsn, reason);
        try
        {
            // Hard-crash simulation: once _crashStop is set the engine is modelling a power cut — NO further data-file writes are permitted. Bail before any
            // WritePagesForCheckpoint / FlushToDisk / CheckpointLSN advance (even un-fsynced RandomAccess.Write would be visible to an in-process reopen). This
            // covers periodic/forced cycles that begin after the flag is set; the shutdown final cycle is guarded separately in CheckpointLoop.
            if (_crashStop)
            {
                return;
            }

            // Test seam (A1.12): lets CheckpointResilienceTests inject a transient/fatal fault to exercise CK-06
            // classification. Null in production — one negligible null-check on the background thread.
            CycleFaultInjector?.Invoke();

            // Step 1: Durability barrier (CK-01/CK-02). Flush the WAL through everything appended so far, then take the
            // post-flush DurableLsn as the cycle's authoritative high-water (barrierLsn). The checkpoint advances to
            // THIS — not the stale loop-sampled targetLsn — so any records appended since the loop's trigger are now
            // durable and safely covered. A timeout here throws WalBackPressureTimeoutException (transient → CK-06
            // retry). The WaitContext gives the whole cycle one shared, bounded deadline budget.
            var ctx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(_resourceOptions.CheckpointBarrierTimeoutMs));
            _walManager.RequestFlush();
            _walManager.WaitForDurable(_walManager.LastAppendedLsn, ref ctx);
            long barrierLsn = _walManager.DurableLsn;

            // Step 2: Collect dirty pages
            int[] dirtyPages;
            {
                using var collectScope = TyphonEvent.BeginCheckpointCollect();
                // Phase 5: Storage:PageCache:DirtyWalk span — inner span over the bitmap walk. rangeStart/rangeLen filled in after the walk.
                var dirtyWalkScope = TyphonEvent.BeginStoragePageCacheDirtyWalk(0, 0);
                try
                {
                    dirtyPages = _mmf.CollectDirtyMemPageIndices();
                    dirtyWalkScope.RangeLen = dirtyPages.Length;
                }
                finally
                {
                    dirtyWalkScope.Dispose();
                }
            }
            cycleScope.DirtyPageCount = dirtyPages.Length;

            // Step 3: Write dirty pages, retrying the skipped tail up to MaxCoveragePasses times. Each pass fsyncs its writes BEFORE decrementing their DirtyCounter
            // so a written page is durable on the data file before it becomes evictable. WritePagesForCheckpoint partitions the array (written front, skipped back),
            // so each retry re-attempts exactly the pages an active writer blocked last pass.
            int writtenTotal = 0;
            int stillSkipped = 0;
            if (dirtyPages.Length > 0)
            {
                var writeScope = TyphonEvent.BeginCheckpointWrite();
                // Phase 8: Durability:Checkpoint:WriteBatch span — covers the staging-buffered batch write.
                var writeBatchScope = TyphonEvent.BeginDurabilityCheckpointWriteBatch(dirtyPages.Length, _stagingPool.PoolCapacity);
                try
                {
                    var pending = dirtyPages;
                    for (int pass = 0; pass < MaxCoveragePasses; pass++)
                    {
                        _mmf.WritePagesForCheckpoint(pending, _stagingPool, out var writtenThisPass);

                        if (writtenThisPass > 0)
                        {
                            // CK-02 flush2: the captured page copies just written may reflect records up to the current
                            // LastAppendedLsn. Flush the WAL through that point BEFORE the data fsync makes those bytes
                            // durable, so the data file can never hold a change whose record could still be lost
                            // (captured ⊆ durable, composing with AP-01 — 04 §3).
                            _walManager.RequestFlush();
                            _walManager.WaitForDurable(_walManager.LastAppendedLsn, ref ctx);

                            using (var fsyncScope = TyphonEvent.BeginCheckpointFsync())
                            {
                                _mmf.FlushToDisk();
                            }

                            for (int i = 0; i < writtenThisPass; i++)
                            {
                                _mmf.DecrementDirty(pending[i]);
                            }
                        }

                        writtenTotal += writtenThisPass;
                        stillSkipped = pending.Length - writtenThisPass;
                        if (stillSkipped == 0)
                        {
                            break;
                        }

                        // Retry exactly the skipped pages (now partitioned into the tail), not the whole dirty set — new commits dirtying other pages must not block this cycle.
                        var retry = new int[stillSkipped];
                        Array.Copy(pending, writtenThisPass, retry, 0, stillSkipped);
                        pending = retry;
                    }

                    writeScope.WrittenCount = writtenTotal;
                    writeBatchScope.StagingAllocated = writtenTotal;
                }
                finally
                {
                    writeBatchScope.Dispose();
                    writeScope.Dispose();
                }
            }

            Interlocked.Add(ref _totalPagesWritten, writtenTotal);

            // Coverage gate (CK-03 / STO-1): advance the checkpoint watermark and recycle WAL segments ONLY when every page collected at cycle start was durably
            // written this cycle. If a page was skipped, its committed records may not have reached the data file — advancing CheckpointLSN past them and recycling
            // their WAL segment (CK-04) would lose that data permanently after a crash. A gated page stays dirty (DC > 0) and is retried next cycle.
            if (stillSkipped == 0)
            {
                _consecutiveGatedCycles = 0;

                // Step 6: Transition WalDurable → Committed
                {
                    var transitionScope = TyphonEvent.BeginCheckpointTransition();
                    var transitioned = _uowRegistry.TransitionWalDurableToCommitted();
                    transitionScope.TransitionedCount = transitioned;
                    transitionScope.Dispose();
                    Interlocked.Add(ref _totalUowTransitioned, transitioned);
                }

                // Step 7: Advance CheckpointLSN in file header + fsync — to barrierLsn (the post-flush durable
                // high-water established at step 1), NOT the stale loop-sampled targetLsn (CK-02/CK-03).
                _mmf.UpdateCheckpointLSN(barrierLsn, _epochManager);
                Interlocked.Exchange(ref _checkpointLsn, barrierLsn);

                // Step 8: Recycle WAL segments
                var segmentManager = _walManager.SegmentManager;
                if (segmentManager != null)
                {
                    var recycleScope = TyphonEvent.BeginCheckpointRecycle();
                    try
                    {
                        var tickFenceLsn = _lastTickFenceLsnProvider?.Invoke() ?? 0;
                        var trimLsn = tickFenceLsn > 0 ? Math.Min(barrierLsn, tickFenceLsn) : barrierLsn;
                        var recycled = segmentManager.MarkReclaimable(trimLsn);
                        recycleScope.RecycledCount = recycled;
                        Interlocked.Add(ref _totalSegmentsRecycled, recycled);
                    }
                    finally
                    {
                        recycleScope.Dispose();
                    }
                }
            }
            else
            {
                Interlocked.Increment(ref _consecutiveGatedCycles);
            }

            Interlocked.Increment(ref _totalCheckpoints);

            // A cycle that completed without throwing clears a prior Degraded state. A gated cycle (the coverage gate
            // working as designed) is NOT an error, so it also lands here as Ok. Fatal is terminal — never downgraded.
            if (_health != DurabilityHealth.Fatal)
            {
                _health = DurabilityHealth.Ok;
            }
        }
        catch (Exception ex)
        {
            ClassifyCycleFailure(ex);
            return;
        }
        finally
        {
            cycleScope.Dispose();
        }

        // Record duration
        var elapsed = Stopwatch.GetTimestamp() - sw;
        var us = (long)((double)elapsed / Stopwatch.Frequency * 1_000_000.0);
        _lastDurationUs = us;
        if (us > _maxDurationUs)
        {
            _maxDurationUs = us;
        }
    }

    /// <summary>
    /// Test hook: latches the crash flag so NO further checkpoint writes the data file — the shutdown final cycle is suppressed AND any periodic/forced cycle that
    /// begins afterward bails immediately (see <see cref="RunCheckpointCycle"/>). Used by <c>DatabaseEngine.SimulateHardCrash</c> to model a power cut where only
    /// WAL-durable data survives. Must be called before <see cref="Dispose"/>. (A cycle already mid-flight when the flag is set has a small residual window — the
    /// test sets this on an otherwise-idle engine, so in practice no cycle is in flight.)
    /// </summary>
    internal void PrepareCrashStop() => _crashStop = true;

    // ═══════════════════════════════════════════════════════════════
    // Failure classification (CK-06)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// CK-06 failure classification. A <b>transient</b> cycle exception — any <see cref="TyphonException"/> whose
    /// <see cref="TyphonException.IsTransient"/> is set (WAL / page-cache back-pressure, lock or IO timeout) — is
    /// logged and retried on the next cycle; it MUST NEVER latch the subsystem (the STO-12 anti-pattern). Anything
    /// else is <b>fatal</b>: latched into <see cref="HasFatalError"/> and surfaced via <see cref="Health"/>, halting
    /// periodic cycles — but the shutdown path still attempts one last-chance flush (04 §7).
    /// </summary>
    private void ClassifyCycleFailure(Exception ex)
    {
        if (ex is TyphonException { IsTransient: true })
        {
            _health = DurabilityHealth.Degraded;
            LogCheckpointTransient(ex);
        }
        else
        {
            _fatalError = ex;
            _health = DurabilityHealth.Fatal;
            LogCheckpointFatal(ex);
        }
    }

    [LoggerMessage(LogLevel.Warning, "Checkpoint cycle hit a transient failure (Health=Degraded); retrying on the next cycle")]
    private partial void LogCheckpointTransient(Exception ex);

    [LoggerMessage(LogLevel.Error, "Checkpoint cycle hit a FATAL failure (Health=Fatal); periodic checkpointing halted (shutdown flush still attempted)")]
    private partial void LogCheckpointFatal(Exception ex);

    // ═══════════════════════════════════════════════════════════════
    // Dispose
    // ═══════════════════════════════════════════════════════════════

    private bool _disposed;

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _shutdown = true;
            _wakeEvent.Set(); // Wake the thread so it sees _shutdown
            _thread?.Join(TimeSpan.FromSeconds(10));

            _wakeEvent.Dispose();
        }

        base.Dispose(disposing);
        _disposed = true;
    }
}
