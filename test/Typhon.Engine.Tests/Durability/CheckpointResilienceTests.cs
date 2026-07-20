using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Increment-A hardening of the checkpoint cycle (P1.3): the durability barrier (CK-02), failure classification
/// (CK-06), and the sealed-segment lock (CK-07). The barrier test proves the cycle advances to the post-flush
/// durable high-water (not a stale target); the classification tests prove a transient fault is retried (never
/// latched — STO-12) while a fatal fault still lets the shutdown path flush one last time.
/// </summary>
[TestFixture]
public class CheckpointResilienceTests : AllocatorTestBase
{
    private InMemoryWalFileIO _fileIO;
    private string _walDir;
    private ManagedPagedMMF _mmf;
    private EpochManager _epochManager;
    private UowRegistry _uowRegistry;
    private WalManager _walManager;
    private ResourceOptions _resourceOptions;
    private StagingBufferPool _stagingPool;

    // Short, stable-per-test name — the engine caps the database name at 63 UTF8 bytes, and the test method names here
    // are long, so we hash the name rather than embed it.
    private static string CurrentDatabaseName => $"T_CkRes_{(uint)TestContext.CurrentContext.Test.Name.GetHashCode():X8}";

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_chkptres_test_{Guid.NewGuid():N}");
        _resourceOptions = new ResourceOptions { CheckpointIntervalMs = 50 };
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

    // ───────────────────────────────────────────────────────────────
    // CK-02 — durability barrier
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// AC1 / CK-02: the cycle establishes its checkpoint watermark from the post-flush DurableLsn (the barrier), not
    /// the caller-supplied target. We pass a deliberately stale <c>targetLsn = 0</c>; the cycle must still flush the
    /// WAL through everything appended and advance CheckpointLsn to that — proving the barrier, not the stale target,
    /// drives the advance. Pre-fix (advance used <c>targetLsn</c>): CheckpointLsn would land on 0 → red.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-02")]
    public void Barrier_DrivesCheckpointAdvance_IgnoringStaleTarget()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager, 3);

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);

        var lastAppended = _walManager.LastAppendedLsn;
        Assert.That(lastAppended, Is.GreaterThan(0), "the workload must have appended records for the advance to be meaningful");

        // Deliberately stale target — the barrier must override it.
        ckpt.RunCheckpointCycle(targetLsn: 0);

        Assert.That(ckpt.CheckpointLsn, Is.EqualTo(_walManager.LastAppendedLsn),
            "the barrier (CK-02) must flush through LastAppendedLsn and drive the advance — the stale target 0 is ignored");
        Assert.That(ckpt.Health, Is.EqualTo(DurabilityHealth.Ok));
    }

    // ───────────────────────────────────────────────────────────────
    // CK-06 — failure classification
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// AC2 / CK-06: a transient cycle exception (any <see cref="TyphonException"/> with <c>IsTransient</c>) sets
    /// Health=Degraded and is NOT latched as fatal, so the loop's <c>if (_fatalError != null) continue</c> never trips
    /// and the next cycle runs and advances. Pre-fix (classification reverted to <c>_fatalError = ex</c>):
    /// HasFatalError becomes true after the transient → red (and the real loop would permanently disable — STO-12).
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-06")]
    public void TransientCycleFault_NextCycleRecovers()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();
        ProduceWalRecords(_walManager, 2);

        using var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);

        var faulted = false;
        ckpt.CycleFaultInjector = () =>
        {
            if (!faulted)
            {
                faulted = true;
                throw new WalBackPressureTimeoutException(0, TimeSpan.Zero);   // transient (TyphonTimeoutException)
            }
        };

        // Cycle 1: faults transiently.
        ckpt.RunCheckpointCycle(_walManager.LastAppendedLsn);
        Assert.That(ckpt.HasFatalError, Is.False, "a transient fault must NOT latch the subsystem (STO-12)");
        Assert.That(ckpt.Health, Is.EqualTo(DurabilityHealth.Degraded));
        Assert.That(ckpt.CheckpointLsn, Is.EqualTo(0), "the faulted cycle must not advance the checkpoint");

        // Cycle 2: no fault — recovers and advances.
        ckpt.RunCheckpointCycle(_walManager.LastAppendedLsn);
        Assert.That(ckpt.Health, Is.EqualTo(DurabilityHealth.Ok), "a clean cycle clears the Degraded state");
        Assert.That(ckpt.CheckpointLsn, Is.EqualTo(_walManager.LastAppendedLsn), "the next cycle advances normally");
    }

    /// <summary>
    /// AC3 / CK-06: a fatal cycle exception latches Health=Fatal + HasFatalError (halting periodic cycles), but the
    /// shutdown path still attempts one last-chance flush cycle, which advances the checkpoint. Pre-fix (shutdown
    /// guard <c>_fatalError == null &amp;&amp; !_crashStop</c>): the final cycle is skipped → CheckpointLsn stays 0 → red.
    /// </summary>
    [Test]
    [CancelAfter(8000)]
    [VerifiesRule("CK-06")]
    public void FatalCycleFault_LatchesFatal_ButShutdownStillFlushes()
    {
        CreateTestInfrastructure();
        _walManager = CreateWalManager();

        var ckpt = new CheckpointManager(_mmf, _uowRegistry, _walManager, _resourceOptions, _epochManager, _stagingPool, AllocationResource);

        var faultEnabled = true;
        ckpt.CycleFaultInjector = () =>
        {
            if (faultEnabled)
            {
                throw new InvalidOperationException("simulated fatal checkpoint fault");   // non-transient → fatal
            }
        };

        ckpt.Start();
        SpinWait.SpinUntil(() => ckpt.IsRunning, 2000);

        // Force a cycle → fatal latch.
        ckpt.ForceCheckpoint();
        Assert.That(SpinWait.SpinUntil(() => ckpt.HasFatalError, 3000), Is.True, "the fatal fault must latch");
        Assert.That(ckpt.Health, Is.EqualTo(DurabilityHealth.Fatal));
        Assert.That(ckpt.CheckpointLsn, Is.EqualTo(0), "no periodic cycle may advance after a fatal latch");

        // Now give the engine durable data and let the shutdown flush run despite the fatal latch.
        faultEnabled = false;
        ProduceWalRecords(_walManager, 2);

        ckpt.Dispose();   // runs the shutdown final cycle on the checkpoint thread

        Assert.That(ckpt.CheckpointLsn, Is.GreaterThan(0),
            "the shutdown path must still flush + advance once even after a fatal latch (CK-06 / STO-12)");
    }

    // ───────────────────────────────────────────────────────────────
    // Helpers (mirrored from CheckpointManagerTests — kept local per the plan; do not refactor the green fixture)
    // ───────────────────────────────────────────────────────────────

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

        using var guard = EpochGuard.Enter(_epochManager);
        var epoch = guard.Epoch;
        var cs = _mmf.CreateChangeSet();
        var segment = _mmf.AllocateSegment(PageBlockType.None, 1, cs);

        var page = segment.GetPageExclusive(0, epoch, out var memPageIdx);
        cs.AddByMemPageIndex(memPageIdx);
        var offset = LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength;
        page.RawData<byte>(offset, PagedMMF.PageRawDataSize - offset).Clear();
        _mmf.UnlatchPageExclusive(memPageIdx);

        _mmf.Bootstrap.SetInt(DatabaseEngine.BK_UowRegistrySPI, segment.RootPageIndex);
        _mmf.SaveBootstrap(cs);
        cs.SaveChanges();

        _uowRegistry = new UowRegistry(segment, _mmf, _epochManager, MemoryAllocator, AllocationResource);
        _uowRegistry.Initialize();

        _stagingPool = new StagingBufferPool(MemoryAllocator, AllocationResource);
    }

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
        SpinWait.SpinUntil(() => mgr.IsRunning, 2000);
        return mgr;
    }

    private static void ProduceWalRecords(WalManager mgr, int count = 1)
    {
        var buffer = mgr.CommitBuffer;
        for (int i = 0; i < count; i++)
        {
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(64, 1, ref ctx);
            claim.DataSpan.Fill((byte)(i + 1));
            buffer.Publish(ref claim);
        }

        SpinWait.SpinUntil(() => mgr.DurableLsn > 0, 2000);
    }
}
