using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Unit tests for <see cref="CheckpointManager"/>. Tests cover lifecycle, pipeline execution, triggers,
/// UoW state transitions, segment recycling, and error handling.
/// </summary>
[TestFixture]
public class CheckpointManagerTests : AllocatorTestBase
{
    private InMemoryWalFileIO _fileIO;
    private string _walDir;
    private ManagedPagedMMF _mmf;
    private EpochManager _epochManager;
    private UowRegistry _uowRegistry;
    private WalManager _walManager;
    private ResourceOptions _resourceOptions;
    private StagingBufferPool _stagingPool;

    private static string CurrentDatabaseName => $"T_Chkpt_{TestContext.CurrentContext.Test.Name}_db";

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_chkpt_test_{Guid.NewGuid():N}");
        _resourceOptions = new ResourceOptions { CheckpointIntervalMs = 100 };
        _mmf = null;
        _epochManager = null;
        _uowRegistry = null;
        _walManager = null;
    }

    public override void TearDown()
    {
        _walManager?.Dispose();
        _walManager = null;
        _stagingPool?.Dispose();
        _stagingPool = null;
        _uowRegistry?.Dispose();
        _uowRegistry = null;
        _mmf?.Dispose();
        _mmf = null;
        _fileIO?.Dispose();
        _fileIO = null;
        if (Directory.Exists(_walDir))
        {
            Directory.Delete(_walDir, true);
        }

        base.TearDown();
    }

    /// <summary>
    /// Creates a minimal ManagedPagedMMF + EpochManager + UowRegistry setup for testing.
    /// </summary>
    private void CreateTestInfrastructure()
    {
        _epochManager = new EpochManager("TestEpochManager", AllocationResource);

        var logger = ServiceProvider.GetRequiredService<ILogger<PagedMMF>>();
        var options = new ManagedPagedMMFOptions
        {
            DatabaseDirectory = TestDatabaseDir,
            DatabaseName = CurrentDatabaseName,
            DatabaseCacheSize = PagedMMF.MinimumCacheSize,
        };
        options.EnsureFileDeleted();

        _mmf = new ManagedPagedMMF(ResourceRegistry, _epochManager, MemoryAllocator, options, AllocationResource, "TestMMF", logger);

        // Initialize UowRegistry on the freshly created file
        using var guard = EpochGuard.Enter(_epochManager);
        var epoch = guard.Epoch;
        var cs = _mmf.CreateChangeSet();
        var segment = _mmf.AllocateSegment(PageBlockType.None, 1, cs);

        var page = segment.GetPageExclusive(0, epoch, out var memPageIdx);
        cs.AddByMemPageIndex(memPageIdx);
        var offset = LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength;
        page.RawData<byte>(offset, PagedMMF.PageRawDataSize - offset).Clear();
        _mmf.UnlatchPageExclusive(memPageIdx);

        // Write SPI to bootstrap
        _mmf.Bootstrap.SetInt(DatabaseEngine.BK_UowRegistrySPI, segment.RootPageIndex);
        _mmf.SaveBootstrap(cs);
        cs.SaveChanges();

        _uowRegistry = new UowRegistry(segment, _mmf, _epochManager, MemoryAllocator, AllocationResource);
        _uowRegistry.Initialize();

        _stagingPool = new StagingBufferPool(MemoryAllocator, AllocationResource);
    }

    /// <summary>
    /// Creates and initializes a WalManager with InMemoryWalFileIO.
    /// </summary>
    private WalManager CreateWalManager(int commitBufferCapacity = 64 * 1024)
    {
        var options = new WalWriterOptions
        {
            WalDirectory = _walDir,
            GroupCommitIntervalMs = 2,
            SegmentSize = 1024 * 1024,
            PreAllocateSegments = 1,
            StagingBufferSize = 8192,
            UseFUA = false,
        };

        var mgr = new WalManager(options, MemoryAllocator, _fileIO, AllocationResource, commitBufferCapacity);
        mgr.Initialize();
        mgr.Start();

        // Wait for writer thread to be running
        SpinWait.SpinUntil(() => mgr.IsRunning, 2000);

        return mgr;
    }

    /// <summary>
    /// Produces WAL records to advance DurableLsn past 0.
    /// </summary>
    private void ProduceWalRecords(WalManager mgr, int count = 1)
    {
        var buffer = mgr.CommitBuffer;
        for (int i = 0; i < count; i++)
        {
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(64, 1, ref ctx);
            claim.DataSpan.Fill((byte)(i + 1));
            buffer.Publish(ref claim);
        }

        // Wait for records to become durable
        SpinWait.SpinUntil(() => mgr.DurableLsn > 0, 2000);
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Start_SetsIsRunning()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);
        ckpt.Start();

        SpinWait.SpinUntil(() => ckpt.IsRunning, 2000);
        Assert.That(ckpt.IsRunning, Is.True);
    }

    [Test]
    [CancelAfter(5000)]
    public void Dispose_StopsThread()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();

        var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);
        ckpt.Start();
        SpinWait.SpinUntil(() => ckpt.IsRunning, 2000);

        ckpt.Dispose();

        Assert.That(ckpt.IsRunning, Is.False);
    }

    [Test]
    public void Dispose_Idempotent()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();

        var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);
        ckpt.Start();

        ckpt.Dispose();
        Assert.DoesNotThrow(() => ckpt.Dispose());
    }

    [Test]
    public void InitialState_DefaultValues()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);

        Assert.That(ckpt.CheckpointLsn, Is.EqualTo(0));
        Assert.That(ckpt.IsRunning, Is.False);
        Assert.That(ckpt.HasFatalError, Is.False);
        Assert.That(ckpt.TotalCheckpoints, Is.EqualTo(0));
    }

    [Test]
    public void InitialCheckpointLsn_PreservedFromConstructor()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource, initialCheckpointLsn: 42);

        Assert.That(ckpt.CheckpointLsn, Is.EqualTo(42));
    }

    // ═══════════════════════════════════════════════════════════════
    // Pipeline Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void RunCheckpointCycle_NoDirtyPages_AdvancesLsn()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager);

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);

        var durableLsn = _walManager.DurableLsn;
        ckpt.RunCheckpointCycle(durableLsn);

        Assert.That(ckpt.CheckpointLsn, Is.EqualTo(durableLsn), "the checkpoint advances the CheckpointLSN to the durable high-water");
        Assert.That(ckpt.TotalCheckpoints, Is.EqualTo(1));
        // The original `TotalPagesWritten == 0` assertion was removed: it is obsolete under WAL-v2. SaveChanges defers
        // page flushes to the checkpoint, so CreateTestInfrastructure's UowRegistry bootstrap is still dirty here, and
        // every cycle also persists its own CheckpointLSN watermark into cached pages — a checkpoint is therefore never
        // a zero-page no-op (empirically each cycle writes its watermark/metadata). The invariant this test guards is
        // the LSN advance asserted above. (The per-cycle metadata re-write is noted for a separate efficiency look.)
    }

    [Test]
    [CancelAfter(5000)]
    public void RunCheckpointCycle_WithDirtyPages_WritesAndAdvances()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager);

        // Dirty a page the checkpoint actually owns.
        //
        // This used to dirty file page 0, which is MetaSlotA — externally persisted, written only by PersistMetaNow, and
        // deliberately excluded from CollectDirtyMemPageIndices. So the page named here was never the one that satisfied
        // the assertion below: the writes came from unrelated bootstrap pages that leaked dirty marks and were therefore
        // rewritten by EVERY cycle for the life of the process. With the marks conserved those pages are written once and
        // stay clean, and a test that dirties an uncollectable page correctly observes zero writes. Page 8 is the first
        // index past InitialReservedPageCount, so it is a plain data page.
        using (var guard = EpochGuard.Enter(_epochManager))
        {
            var cs = _mmf.CreateChangeSet();
            _mmf.RequestPageEpoch(8, guard.Epoch, out var memPageIdx);
            _mmf.TryLatchPageExclusive(memPageIdx);
            cs.AddByMemPageIndex(memPageIdx);
            _mmf.UnlatchPageExclusive(memPageIdx);
            cs.ReleaseDirtyMarks();   // the page stays owed a write; the mark is not what keeps it collectable
        }

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);

        var durableLsn = _walManager.DurableLsn;
        ckpt.RunCheckpointCycle(durableLsn);

        Assert.That(ckpt.CheckpointLsn, Is.EqualTo(durableLsn));
        Assert.That(ckpt.TotalCheckpoints, Is.EqualTo(1));
        Assert.That(ckpt.TotalPagesWritten, Is.GreaterThan(0));
    }

    // ═══════════════════════════════════════════════════════════════
    // Coverage gate (CK-03 / STO-1) — P0.1
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Dirties a page and pins its ActiveChunkWriters so checkpoint capture must skip it (a live writer mid-commit). The coverage gate must then refuse to advance
    /// CheckpointLSN and refuse to recycle WAL segments — otherwise the skipped page's committed records would be lost on a crash once their segment is recycled
    /// (STO-1). Fails against the pre-fix code, which advanced CheckpointLSN unconditionally. Acceptance criterion AC-1.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    public void CoverageGate_SkippedPage_NoAdvance()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager);

        var memPageIdx = DirtyRegularPage();

        _mmf.IncrementActiveChunkWriters(memPageIdx); // pin → checkpoint capture CAS(ACW,-1,0) fails → page skipped
        try
        {
            using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);
            var durableLsn = _walManager.DurableLsn;
            Assert.That(durableLsn, Is.GreaterThan(0), "the workload must have produced durable WAL to make the advance assertion meaningful");

            ckpt.RunCheckpointCycle(durableLsn);

            Assert.That(ckpt.CheckpointLsn, Is.LessThan(durableLsn), "CheckpointLsn must NOT advance while a collected dirty page is uncaptured (CK-03)");
            Assert.That(ckpt.ConsecutiveGatedCycles, Is.EqualTo(1), "the cycle must register as gated");
            Assert.That(ckpt.TotalSegmentsRecycled, Is.EqualTo(0), "no WAL segment may be recycled while gated (CK-04)");
        }
        finally
        {
            _mmf.DecrementActiveChunkWriters(memPageIdx);
        }
    }

    /// <summary>
    /// Once the active writer releases the page, a subsequent cycle captures it and the gate opens: CheckpointLSN advances and the gate counter resets.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    public void CoverageGate_Released_Advances()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager);

        var memPageIdx = DirtyRegularPage();

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);
        var durableLsn = _walManager.DurableLsn;
        Assert.That(durableLsn, Is.GreaterThan(0));

        // Cycle 1: page pinned → gated.
        _mmf.IncrementActiveChunkWriters(memPageIdx);
        ckpt.RunCheckpointCycle(durableLsn);
        Assert.That(ckpt.CheckpointLsn, Is.LessThan(durableLsn));
        Assert.That(ckpt.ConsecutiveGatedCycles, Is.EqualTo(1));

        // Cycle 2: writer released → page captured → advance + reset.
        _mmf.DecrementActiveChunkWriters(memPageIdx);
        ckpt.RunCheckpointCycle(durableLsn);
        Assert.That(ckpt.CheckpointLsn, Is.EqualTo(durableLsn), "once the page is captured, CheckpointLsn advances");
        Assert.That(ckpt.ConsecutiveGatedCycles, Is.EqualTo(0), "a fully-covered cycle resets the gate counter");
    }

    /// <summary>Allocates a regular data page and dirties it without saving, so it is collected by the next checkpoint but stays dirty. Returns its in-memory
    /// page index. (Not the meta pair, pages 0–1 — those are CK-05-alternation-managed and excluded from the checkpoint dirty-write.)</summary>
    private int DirtyRegularPage()
    {
        using var guard = EpochGuard.Enter(_epochManager);
        var cs = _mmf.CreateChangeSet();
        var filePageIndex = _mmf.AllocatePage(cs);
        _mmf.RequestPageEpoch(filePageIndex, guard.Epoch, out var memPageIdx);
        _mmf.TryLatchPageExclusive(memPageIdx);
        cs.AddByMemPageIndex(memPageIdx);
        _mmf.UnlatchPageExclusive(memPageIdx);
        return memPageIdx;
    }

    // ═══════════════════════════════════════════════════════════════
    // Trigger Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void ForceCheckpoint_WakesThread()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager);

        // Use a long interval so only ForceCheckpoint triggers the cycle
        _resourceOptions.CheckpointIntervalMs = 60000;

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);
        ckpt.Start();
        SpinWait.SpinUntil(() => ckpt.IsRunning, 2000);

        ckpt.ForceCheckpoint();

        // Wait for the checkpoint to complete
        SpinWait.SpinUntil(() => ckpt.TotalCheckpoints > 0, 3000);

        Assert.That(ckpt.TotalCheckpoints, Is.GreaterThan(0));
        Assert.That(ckpt.CheckpointLsn, Is.GreaterThan(0));
    }

    [Test]
    [CancelAfter(5000)]
    public void Timer_TriggersCheckpoint()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager);

        // Short interval to trigger quickly
        _resourceOptions.CheckpointIntervalMs = 50;

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);
        ckpt.Start();

        // Wait for at least one checkpoint via timer
        SpinWait.SpinUntil(() => ckpt.TotalCheckpoints > 0, 3000);

        Assert.That(ckpt.TotalCheckpoints, Is.GreaterThan(0));
        Assert.That(ckpt.CheckpointLsn, Is.GreaterThan(0));
    }

    // ═══════════════════════════════════════════════════════════════
    // Dirty-page trigger (#830 / CK-11)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Dirties <paramref name="pageCount"/> plain data pages and leaves them owing a writeback.
    /// </summary>
    /// <remarks>
    /// Starts past <c>InitialReservedPageCount</c> so none of these is the externally-persisted meta pair, which
    /// <c>CollectDirtyMemPageIndices</c> excludes by design — dirtying those would produce debt the checkpoint is never
    /// going to clear and the test would be measuring the wrong thing.
    /// </remarks>
    private void DirtyPages(int pageCount)
    {
        using var guard = EpochGuard.Enter(_epochManager);
        var cs = _mmf.CreateChangeSet();
        for (var i = 0; i < pageCount; i++)
        {
            _mmf.RequestPageEpoch(8 + i, guard.Epoch, out var memPageIdx);
            _mmf.TryLatchPageExclusive(memPageIdx);
            cs.AddByMemPageIndex(memPageIdx);
            _mmf.UnlatchPageExclusive(memPageIdx);
        }

        cs.ReleaseDirtyMarks();
    }

    /// <summary>
    /// Page-cache pressure runs a cycle even though the timer cannot possibly fire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interval is <see cref="int.MaxValue"/> — about 24 days — so a checkpoint happening AT ALL is proof the
    /// dirty-page trigger fired. That is the whole design of this test: no sleeping on a real interval, no racing a
    /// timer, and no way for a passing result to be attributed to anything else.
    /// </para>
    /// <para>
    /// Without this trigger the page cache can only reclaim at timer cadence, so a workload that dirties more than the
    /// cache holds inside one interval saturates it and the next allocation has nothing to evict — measured as a
    /// <c>PageCacheBackpressureTimeout</c> with 32 758 of 32 768 pages owed (#830).
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CK-11")]
    public void DirtyPagePressure_TriggersCheckpoint()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager);

        _resourceOptions.CheckpointIntervalMs = int.MaxValue;   // the timer is out of the picture
        _resourceOptions.CheckpointDirtyPageThresholdPercent = 5;

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);
        ckpt.Start();

        var slots = (int)(PagedMMF.MinimumCacheSize / PagedMMF.PageSize);
        DirtyPages((slots / 10) + 1);   // 10 % — comfortably over the 5 % threshold

        Assert.That(SpinWait.SpinUntil(() => ckpt.TotalCheckpoints > 0, 10_000), Is.True,
            "the cache passed its writeback-debt threshold and the timer is 24 days away, so the only thing that can "
            + "produce a cycle is the dirty-page trigger — and nothing did");

        Assert.That(ckpt.TotalPressureCheckpoints, Is.GreaterThan(0),
            "the cycle must be attributed to pressure, not silently counted as periodic");
    }

    /// <summary>
    /// A threshold of zero restores the pre-#830 behaviour exactly: the timer and explicit forces are the only causes.
    /// </summary>
    /// <remarks>
    /// The negative arm of the test above, and the one that makes it mean something. Same fixture, same debt, same
    /// unreachable timer — only the threshold differs. Without it, "a checkpoint happened" is not evidence the trigger
    /// caused it.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CK-11")]
    public void TriggerDisabled_LeavesCacheToTheTimer()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager);

        _resourceOptions.CheckpointIntervalMs = int.MaxValue;
        _resourceOptions.CheckpointDirtyPageThresholdPercent = 0;   // disabled

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);
        ckpt.Start();

        var slots = (int)(PagedMMF.MinimumCacheSize / PagedMMF.PageSize);
        DirtyPages((slots / 2) + 1);   // half the cache — far past any threshold that was armed

        Assert.That(SpinWait.SpinUntil(() => ckpt.TotalCheckpoints > 0, 1500), Is.False,
            "with the trigger disabled the cache is the timer's problem alone, however dirty it gets");
        Assert.That(ckpt.TotalPressureCheckpoints, Is.Zero);
    }

    /// <summary>
    /// With the trigger disabled, the timer still fires on its first interval — it is not delayed to the second.
    /// </summary>
    /// <remarks>
    /// Guards a regression the due-time bookkeeping can introduce: the event wait and <see cref="Stopwatch"/> are
    /// different clocks, so a wake landing a hair short of the computed due-time would read as "not due", skip the tick,
    /// and silently double the effective checkpoint interval. When the trigger is off the wait IS the interval, so the
    /// loop must not consult the due-time at all. The 400 ms budget is well under two 200 ms intervals, so a skipped
    /// tick fails this and a correct one does not.
    /// <para>
    /// Repeated because the skew is intermittent, and a single run is a weak detector. Measured against the mutant that
    /// removes the <c>!IsDirtyPageTriggerArmed</c> short-circuit: it fails roughly one run in five, so one execution
    /// would wave it through 80 % of the time. Ten make that ~11 %, and the correct implementation passes every run.
    /// </para>
    /// </remarks>
    [Test]
    [Repeat(10)]
    [CancelAfter(15_000)]
    [VerifiesRule("CK-11")]
    public void TriggerDisabled_TimerStillFiresOnItsFirstInterval()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager);

        _resourceOptions.CheckpointIntervalMs = 200;
        _resourceOptions.CheckpointDirtyPageThresholdPercent = 0;

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);
        ckpt.Start();

        Assert.That(SpinWait.SpinUntil(() => ckpt.TotalCheckpoints > 0, 400), Is.True,
            "the first interval must produce the first cycle; needing a second one means the wake was not recognised as a timer tick");
    }

    /// <summary>
    /// Polling for pressure does not turn the poll interval into the durability cadence.
    /// </summary>
    /// <remarks>
    /// The trap this guards: once the trigger is armed the loop wakes every 250 ms instead of every
    /// <c>CheckpointIntervalMs</c>, and if a wake were treated as a timer tick the engine would silently checkpoint four
    /// times a second on an idle database — burning a WAL barrier and an fsync each time for nothing. The loop therefore
    /// tracks the timer's due-time explicitly rather than inferring it from the wait returning.
    /// <para>
    /// Runs with a clean cache so no pressure exists: any cycle here could only have come from a mis-read poll.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CK-11")]
    public void PollingForPressure_DoesNotBecomeTheTimerCadence()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager);

        _resourceOptions.CheckpointIntervalMs = int.MaxValue;   // no timer tick may occur during this test
        _resourceOptions.CheckpointDirtyPageThresholdPercent = 90;   // armed, so the loop polls — but far above idle debt

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);
        ckpt.Start();

        // Several poll intervals' worth. Each one wakes, finds no pressure, and must go back to sleep.
        Assert.That(SpinWait.SpinUntil(() => ckpt.TotalCheckpoints > 0, 1500), Is.False,
            "a poll that finds no pressure is not a timer tick — treating it as one makes the poll interval the "
            + "durability cadence and checkpoints an idle database four times a second");
    }

    /// <summary>
    /// A pressure cycle is an ordinary cycle: it advances the checkpoint watermark like any other.
    /// </summary>
    /// <remarks>
    /// The point of this issue's approach is that nothing about the pipeline changes — same CK-02 barrier, same CK-03
    /// coverage gate, same watermark advance and segment reclamation. A "cheap" cycle that skipped the gate would be a
    /// second, weaker durability path, and CK-08 exists precisely to describe the one form allowed to do less.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CK-11")]
    public void PressureCycle_AdvancesTheWatermark()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager);

        _resourceOptions.CheckpointIntervalMs = int.MaxValue;
        _resourceOptions.CheckpointDirtyPageThresholdPercent = 5;

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);
        ckpt.Start();

        var slots = (int)(PagedMMF.MinimumCacheSize / PagedMMF.PageSize);
        DirtyPages((slots / 10) + 1);

        Assert.That(SpinWait.SpinUntil(() => ckpt.TotalCheckpoints > 0, 10_000), Is.True);

        Assert.That(ckpt.CheckpointLsn, Is.GreaterThan(0),
            "a pressure-triggered cycle runs the full pipeline, so it advances CheckpointLSN exactly as a periodic one does");
    }

    // ═══════════════════════════════════════════════════════════════
    // UoW Transition Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void Checkpoint_TransitionsWalDurableToCommitted()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager);

        // Allocate a UoW and transition it to WalDurable
        var uowId = _uowRegistry.AllocateUowId();
        _uowRegistry.PromoteToWalDurable(uowId);

        // Verify it's WalDurable before checkpoint
        using (var guard = EpochGuard.Enter(_epochManager))
        {
            var entry = _uowRegistry.ReadEntry(uowId, guard.Epoch);
            Assert.That(entry.State, Is.EqualTo(UnitOfWorkState.WalDurable));
        }

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);
        ckpt.RunCheckpointCycle(_walManager.DurableLsn);

        // Verify it's Committed after checkpoint
        using (var guard = EpochGuard.Enter(_epochManager))
        {
            var entry = _uowRegistry.ReadEntry(uowId, guard.Epoch);
            Assert.That(entry.State, Is.EqualTo(UnitOfWorkState.Committed));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Segment Recycling Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void MarkReclaimable_DeletesSegmentsBelowCheckpointLsn()
    {
        // Test WalSegmentManager.MarkReclaimable directly
        var segMgr = new WalSegmentManager(_fileIO, _walDir, 64 * 1024, 1, false);
        segMgr.Initialize(0, 1);

        // Rotate twice to create sealed segments
        segMgr.RotateSegment(100, 99);
        segMgr.RotateSegment(200, 199);

        Assert.That(segMgr.SealedSegmentCount, Is.EqualTo(2));

        // Reclaim segments below LSN 100 (only the first sealed segment with LastLSN=99)
        var reclaimed = segMgr.MarkReclaimable(100);

        Assert.That(reclaimed, Is.EqualTo(1));
        Assert.That(segMgr.SealedSegmentCount, Is.EqualTo(1));

        // Reclaim the remaining one
        reclaimed = segMgr.MarkReclaimable(200);
        Assert.That(reclaimed, Is.EqualTo(1));
        Assert.That(segMgr.SealedSegmentCount, Is.EqualTo(0));

        segMgr.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // CheckpointLSN Persistence Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void RunCheckpointCycle_PersistsCheckpointLsnToHeader()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager);

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);

        var durableLsn = _walManager.DurableLsn;
        ckpt.RunCheckpointCycle(durableLsn);

        // Read the CheckpointLSN from the durability watermark block (CK-05) to verify persistence
        Assert.That(DurabilityWatermarks.ReadCheckpointLsn(_mmf), Is.EqualTo(durableLsn));
    }

    // ═══════════════════════════════════════════════════════════════
    // Dispose Runs Final Checkpoint
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void Dispose_RunsFinalCheckpoint()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager);

        // Verify DurableLsn is > 0 before creating the checkpoint manager
        var durableLsn = _walManager.DurableLsn;
        Assert.That(durableLsn, Is.GreaterThan(0), "Precondition: DurableLsn should be > 0");

        // Use short interval so the first cycle runs before dispose
        _resourceOptions.CheckpointIntervalMs = 50;

        var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);
        ckpt.Start();
        SpinWait.SpinUntil(() => ckpt.IsRunning, 2000);

        // Wait for at least one checkpoint to complete before disposing
        SpinWait.SpinUntil(() => ckpt.TotalCheckpoints > 0, 3000);

        ckpt.Dispose();

        // The checkpoint should have run at least once (either via timer or final cycle)
        Assert.That(ckpt.HasFatalError, Is.False, "Checkpoint should not have a fatal error");
        Assert.That(ckpt.CheckpointLsn, Is.GreaterThan(0));
        Assert.That(ckpt.TotalCheckpoints, Is.GreaterThan(0));
    }
}
