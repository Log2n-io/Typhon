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

        // Dirty a page by writing to it via ChangeSet
        using (var guard = EpochGuard.Enter(_epochManager))
        {
            var cs = _mmf.CreateChangeSet();
            _mmf.RequestPageEpoch(0, guard.Epoch, out var memPageIdx);
            var latched = _mmf.TryLatchPageExclusive(memPageIdx);
            cs.AddByMemPageIndex(memPageIdx);
            _mmf.UnlatchPageExclusive(memPageIdx);
            // Don't call SaveChanges — leave page dirty
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
