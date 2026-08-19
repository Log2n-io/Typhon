using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    /// <summary>
    /// Wired by <see cref="DatabaseEngine"/> to <c>PersistArchetypeState</c>. Invoked at the START of every checkpoint cycle (before the durability
    /// barrier) so the per-archetype segment pointers (EntityMap / cluster-segment SPIs in the <c>ArchetypeR1</c> table, plus NextEntityKey + EntityMap
    /// meta) are updated and ride THIS cycle's barrier + dirty-page flush. Without this a checkpoint consolidates a cluster/EntityMap's DATA pages into
    /// the data file but leaves the durable ArchetypeR1 still pointing at 0/stale — so a hard crash after the checkpoint reopens an orphaned
    /// (unreachable) base and loses the entities (#395). Null until the engine wires it (early/test cycles no-op).
    /// </summary>
    internal Action PersistDurableMetadataHook { get; set; }

    /// <summary>Max passes per cycle to retry pages skipped because a writer was active, before the coverage gate blocks the LSN advance (CK-03). Skip windows
    /// are accessor-scoped (~µs), so the second pass almost always clears them.</summary>
    private const int MaxCoveragePasses = 3;

    // ═══════════════════════════════════════════════════════════════
    // Metrics
    // ═══════════════════════════════════════════════════════════════

    private long _totalCheckpoints;
    private long _totalPressureCheckpoints;
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
    /// <param name="resourceOptions">Configuration (CheckpointIntervalMs, CheckpointBarrierTimeoutMs).</param>
    /// <param name="epochManager">Epoch manager for page access.</param>
    /// <param name="stagingPool">Pre-allocated staging buffer pool for snapshot-based checkpoint writes.</param>
    /// <param name="parent">Parent resource node.</param>
    /// <param name="initialCheckpointLsn">Initial checkpoint LSN from file header (0 for fresh database).</param>
    /// <param name="lastTickFenceLsnProvider">
    /// Optional callback returning the LSN of the most recently emitted TickFence chunk. The checkpoint reads it while draining so TickFence-discipline
    /// writes are accounted for; <see langword="null"/> when TickFence tracking is not wired, in which case the LSN is treated as 0.
    /// </param>
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

    /// <summary>
    /// Of those, how many ran because the page cache reached its writeback-debt threshold rather than because the timer
    /// elapsed or someone forced one (#830).
    /// </summary>
    /// <remarks>
    /// The number to watch when tuning: zero over a long run means the timer alone is keeping up, and a value climbing
    /// alongside back-pressure waits means the threshold is set too high to prevent the stall it exists to prevent.
    /// </remarks>
    public long TotalPressureCheckpoints => Interlocked.Read(ref _totalPressureCheckpoints);

    /// <summary>Total number of dirty pages written across all checkpoints.</summary>
    public long TotalPagesWritten => Interlocked.Read(ref _totalPagesWritten);

    /// <summary>Total number of WAL segments reclaimed across all checkpoints.</summary>
    public long TotalSegmentsRecycled => Interlocked.Read(ref _totalSegmentsRecycled);

    /// <summary>
    /// Number of consecutive cycles whose coverage gate blocked the CheckpointLSN advance because a collected dirty page could not be captured (active writer).
    /// Reset to 0 by any fully-covered cycle. A sustained nonzero value flags a pinned writer starving checkpoint progress (CK-03 telemetry).
    /// </summary>
    public long ConsecutiveGatedCycles => Interlocked.Read(ref _consecutiveGatedCycles);

    // ─── #817 skip diagnostics ───────────────────────────────────────────────────────────────────────────────────
    // Written on the checkpoint thread only; read by a diagnostic consumer. Volatile on publish so a reader on
    // another thread cannot observe a count that outruns the array contents.

    private int[] _prevSkipped = [];
    private int[] _lastSkipped = [];
    private int _lastSkippedCount;

    /// <summary>Pages the coverage gate skipped on the most recent cycle (#817).</summary>
    public ReadOnlySpan<int> LastSkippedPages => _lastSkipped.AsSpan(0, Volatile.Read(ref _lastSkippedCount));

    /// <summary>How many of the last cycle's skipped pages were ALSO skipped on the cycle before it (#817).</summary>
    public int RepeatSkippedPages { get; private set; }

    /// <summary>Largest number of consecutive cycles any single page has been skipped (#817).</summary>
    public int MaxConsecutiveSkipsForOnePage { get; private set; }

    private readonly Dictionary<int, int> _skipStreak = [];

    /// <summary>Skip causes, from the page cache (#817). A = live chunk writer, B = writer held &gt; 100 ms, C = stale odd counter.</summary>
    public (long Acw, long WriterHeld, long StaleCounter) SkipCauses =>
        (_mmf.CheckpointSkipAcw, _mmf.CheckpointSkipWriterHeld, _mmf.CheckpointSkipStaleCounter);

    /// <summary>Live ActiveChunkWriters for a page (#817). Non-zero while quiescent proves a leaked registration.</summary>
    public int ActiveChunkWritersOf(int memPageIndex) => _mmf.ActiveChunkWritersOf(memPageIndex);

    /// <summary>Pages still dirty, and the cache size. Read quiescent after a checkpoint, a high count is a leak.</summary>
    public (int Dirty, int Total, int FirstDirtyPage) CountDirtyPages() => _mmf.CountDirtyPages();

    private void CaptureSkipDiagnostics(int[] pending, int stillSkipped)
    {
        if (stillSkipped <= 0)
        {
            Volatile.Write(ref _lastSkippedCount, 0);
            RepeatSkippedPages = 0;
            _skipStreak.Clear();
            _prevSkipped = [];
            return;
        }

        var skipped = new int[stillSkipped];
        Array.Copy(pending, pending.Length - stillSkipped, skipped, 0, stillSkipped);
        Array.Sort(skipped);

        var repeat = 0;
        foreach (var p in skipped)
        {
            if (Array.BinarySearch(_prevSkipped, p) >= 0)
            {
                repeat++;
            }
            var streak = _skipStreak.TryGetValue(p, out var s) ? s + 1 : 1;
            _skipStreak[p] = streak;
            if (streak > MaxConsecutiveSkipsForOnePage)
            {
                MaxConsecutiveSkipsForOnePage = streak;
            }
        }

        // Drop pages that were NOT skipped this cycle, so a streak means CONSECUTIVE cycles rather than a lifetime
        // tally — a page skipped once every other cycle is a completely different diagnosis from a pinned one.
        if (_skipStreak.Count > skipped.Length)
        {
            foreach (var key in _skipStreak.Keys.Where(k => Array.BinarySearch(skipped, k) < 0).ToArray())
            {
                _skipStreak.Remove(key);
            }
        }

        RepeatSkippedPages = repeat;
        _prevSkipped = skipped;
        _lastSkipped = skipped;
        Volatile.Write(ref _lastSkippedCount, stillSkipped);
    }

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
        writer.WriteThroughput("PressureCheckpoints", _totalPressureCheckpoints);
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

    /// <summary>
    /// How often the loop re-examines page-cache pressure while the dirty-page trigger is armed, and therefore also the
    /// closest two pressure-triggered cycles can be spaced.
    /// </summary>
    /// <remarks>
    /// Not a knob. It is an implementation detail of "notice pressure promptly", not a tuning decision the user has any
    /// basis to make — and it doubles as the floor between pressure cycles, which is what stops a cache that cannot be
    /// drained (every page held by a live writer, say) from spinning barrier-fsync cycles back to back. 250 ms is far
    /// below any interval worth configuring and far above the cost of one scan.
    /// </remarks>
    private const int DirtyPagePollIntervalMs = 250;

    /// <summary>The configured interval as Stopwatch ticks.</summary>
    private long IntervalTicks() =>
        (long)(Math.Max(_resourceOptions.CheckpointIntervalMs, 0) / 1000.0 * Stopwatch.Frequency);

    /// <summary>Whether the dirty-page trigger is configured on. Off ⇒ the loop is byte-for-byte the pre-#830 one.</summary>
    private bool IsDirtyPageTriggerArmed => _resourceOptions.CheckpointDirtyPageThresholdPercent > 0;

    /// <summary>
    /// How long to block on the wake event: the poll interval when the dirty-page trigger is armed, the full checkpoint
    /// interval when it is disabled.
    /// </summary>
    private int WaitMs()
    {
        var interval = Math.Max(_resourceOptions.CheckpointIntervalMs, 0);
        return IsDirtyPageTriggerArmed ? Math.Min(interval, DirtyPagePollIntervalMs) : interval;
    }

    /// <summary>Whether the page cache owes enough writeback to justify a cycle ahead of the timer.</summary>
    private bool IsDirtyPagePressureReached() =>
        IsDirtyPageTriggerArmed && _mmf.WritebackDebtPercent() >= _resourceOptions.CheckpointDirtyPageThresholdPercent;

    private void CheckpointLoop()
    {
        try
        {
            // The timer's due-time is tracked explicitly rather than inferred from the wait returning, because with the
            // dirty-page trigger armed the wait is much shorter than the interval (#830). A poll that finds no pressure
            // must not be mistaken for a timer tick, or the durability cadence silently becomes the poll cadence.
            var nextTimerDue = Stopwatch.GetTimestamp() + IntervalTicks();

            while (!_shutdown)
            {
                // Phase 8: Durability:Checkpoint:Sleep span — covers the inter-cycle wait.
                // wakeReason: 0=timer, 1=force, 2=shutdown, 3=dirty-page pressure.
                var waitMs = WaitMs();
                var sleepScope = TyphonEvent.BeginDurabilityCheckpointSleep((uint)waitMs, 0);
                bool force, timerDue, pressure;
                try
                {
                    // Sleep until woken by: the wait elapsing, ForceCheckpoint, or shutdown
                    _wakeEvent.Wait(waitMs);
                    _wakeEvent.Reset();

                    force = _forceRequested;
                    _forceRequested = false;

                    // When the trigger is disabled the wait above IS the checkpoint interval, so any wake is a tick and
                    // the due-time must not be consulted: the event wait and Stopwatch are different clocks, and a wake
                    // landing a few microseconds short of the due-time would skip the tick and silently double the
                    // effective interval. Armed, the explicit due-time is required precisely because the wait is now the
                    // poll interval and says nothing about the timer.
                    timerDue = !IsDirtyPageTriggerArmed || Stopwatch.GetTimestamp() >= nextTimerDue;

                    // Only pay for the page-cache scan when its answer can change what happens: a cycle is already going
                    // to run if the timer is due or someone forced one.
                    pressure = !_shutdown && !force && !timerDue && IsDirtyPagePressureReached();

                    sleepScope.WakeReason = _shutdown ? (byte)2 : force ? (byte)1 : pressure ? (byte)3 : (byte)0;
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

                // A poll wake with nothing to do — the common case once the trigger is armed. Costs one scan and goes
                // straight back to sleep, leaving the timer's due-time untouched.
                if (!force && !timerDue && !pressure)
                {
                    continue;
                }

                // Check if we should run a checkpoint cycle
                var durableLsn = _walManager.DurableLsn;
                if (durableLsn <= Interlocked.Read(ref _checkpointLsn) && !force && !pressure)
                {
                    // No new durable WAL records since last checkpoint, and nothing else is asking. This WAS a timer
                    // tick, so re-arm it — otherwise the timer stays permanently overdue and every later poll reads as
                    // one, which would run the pressure scan for nothing and mislabel every cycle as Periodic.
                    nextTimerDue = Stopwatch.GetTimestamp() + IntervalTicks();
                    continue;
                }

                // Pressure deliberately bypasses the LSN gate above, on the same reasoning the shutdown flush does:
                // CreateOrGrow and the other structural-write paths dirty segment pages WITHOUT writing WAL records, so
                // a cache filled by structural work alone has durableLsn == checkpointLsn. Gating on the log there would
                // skip exactly the pages causing the pressure, and the pressure would never clear.
                var reason = force ? CheckpointReason.Forced
                    : timerDue ? CheckpointReason.Periodic
                    : CheckpointReason.DirtyPagePressure;

                // Any cycle does the timer's job, so re-arm from here rather than from the last timer tick.
                nextTimerDue = Stopwatch.GetTimestamp() + IntervalTicks();
                RunCheckpointCycle(durableLsn, reason);
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

            // Persist per-archetype durable metadata (segment SPIs, NextEntityKey, EntityMap meta) into the ArchetypeR1 table BEFORE the barrier, so
            // its WAL records and dirty pages are flushed by THIS cycle. This makes the consolidated cluster/EntityMap base reachable on reopen after a
            // hard crash (CK-09 family / #395) — the checkpoint already writes the data pages; this records the pointers to them. Idempotent and cheap
            // (skips archetypes whose state is unchanged).
            PersistDurableMetadataHook?.Invoke();

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
                        // One slot per page this pass: the generation each page's snapshot covers, sampled under the ACW
                        // sentinel inside WritePagesForCheckpoint. Publishing THESE values after the fsync is what
                        // discharges the pages' writeback debt — a page re-modified between its capture and now has a
                        // higher generation and therefore stays owed, which is CP-04 falling out of the comparison.
                        var capturedGen = new long[pending.Length];
                        _mmf.WritePagesForCheckpoint(pending, _stagingPool, out var writtenThisPass, capturedGen);

                        if (writtenThisPass > 0)
                        {
                            // CK-02 flush2: the captured page copies just written may reflect records up to the current
                            // LastAppendedLsn. Flush the WAL through that point BEFORE the data fsync makes those bytes
                            // durable, so the data file can never hold a change whose record could still be lost
                            // (captured ⊆ durable, composing with AP-01 — 04 §3).
                            _walManager.RequestFlush();
                            _walManager.WaitForDurable(_walManager.LastAppendedLsn, ref ctx);

                            using (TyphonEvent.BeginCheckpointFsync())
                            {
                                _mmf.FlushToDisk();
                            }

                            for (int i = 0; i < writtenThisPass; i++)
                            {
                                _mmf.MarkCaptured(pending[i], capturedGen[i]);
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

                    // #817 diagnostic: capture WHICH pages the gate is stuck on, not just how many. The retry loop
                    // partitions written to the front, so the survivors are the last `stillSkipped` entries. The
                    // question that decides the fix is identity, not count: the same page every cycle means a
                    // leaked ACW / stale seqlock counter (a bug — the gate is working as designed), whereas a
                    // churning set means the coverage gate genuinely has no liveness guarantee under load.
                    CaptureSkipDiagnostics(pending, stillSkipped);

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

                // Step 7: Advance CheckpointLSN in the meta-pair watermark block + fsync — to barrierLsn (the post-flush durable high-water established at
                // step 1), NOT the stale loop-sampled targetLsn (CK-02/CK-03).
                DurabilityWatermarks.UpdateCheckpointLsn(_mmf, barrierLsn);
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
            if (reason == CheckpointReason.DirtyPagePressure)
            {
                // Counted HERE, beside the cycle counter, not at the call site: RunCheckpointCycle returns early under
                // _crashStop without counting, so a call-site increment would let TotalPressureCheckpoints exceed
                // TotalCheckpoints and make "of those" a lie in exactly the crash-simulation runs someone would be
                // reading it during.
                Interlocked.Increment(ref _totalPressureCheckpoints);
            }

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
