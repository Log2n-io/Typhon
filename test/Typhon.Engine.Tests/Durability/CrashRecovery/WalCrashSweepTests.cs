using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// The full crash sweep (design 08 A1.2). Reuses the T-5 differential oracle (<see cref="RecoveryOracle"/>) over many crash points and the
/// T-6 workload library (<see cref="RecoveryWorkloads"/>), asserting that recovery through <c>RecoveryDriver</c> reproduces the
/// durably-committed state at every boundary:
/// <list type="bullet">
/// <item><b>Page-axis</b> (<see cref="PageAxis_CheckpointCrashAtBoundary_OracleHolds"/>): crash the checkpoint at every page-write boundary
///   via <see cref="ChaosPageIO"/>. The cycle aborts before advancing CheckpointLSN (coverage gate, CK-03), so the WAL window still covers
///   every entity → replay heals → oracle holds (AP-12).</item>
/// <item><b>WAL-window</b> (<see cref="WalWindow_Recover_OracleHolds"/> / <see cref="WalWindow_MidCheckpoint_OracleHolds"/>): hard-crash with
///   the entities in the WAL window, with and without a consolidating mid-workload checkpoint.</item>
/// </list>
/// Page <i>corruption</i> (torn/zero) is exercised in <c>SuspectPageTests</c> (A1.11), where the damaged page's segment kind is known and the
/// heal-vs-RB-04-loud-fail outcome can be asserted precisely. The cluster axis is covered by the <c>MixedDiscipline</c> cells below (Commit-discipline
/// writes over every boundary) and by <c>DifferentialRecoveryOracleTests.ClusterAllSv_PrimaryAxis_SurvivesCrash</c> (Commit-discipline SV spawns, #395 Face B,
/// at the no-checkpoint crash point — the recovery path is identical Slot-record application at every boundary the MixedDiscipline cells already sweep). Both
/// now green (#395 SV-durability is implemented: Face A = checkpoint persists segment SPIs, CK-10; Face B = Commit-discipline spawns WAL-log SV values, D5).
/// Category <c>CrashSweep</c> so the boundary fan-out can be sampled in PR CI / run full nightly.
/// </summary>
[TestFixture]
[Category("CrashSweep")]
internal sealed class WalCrashSweepTests
{
    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;

    private static readonly string[] NonClusterWorkloads = ["SingleTxSpawn", "LifecycleChurn", "IndexedFlat", "MultiValueDupKey"];

    // Representative checkpoint page-write boundaries. Boundaries beyond a cycle's write count let the cycle complete (consolidated base) —
    // recovery still holds, so the sweep is robust without probing the exact per-workload write count.
    private static readonly int[] CrashBoundaries = [1, 2, 3, 5, 8];

    private static IRecoveryWorkload MakeWorkload(string name) => name switch
    {
        "SingleTxSpawn" => new SingleTxSpawnWorkload(10),
        "LifecycleChurn" => new LifecycleChurnWorkload(1234, 24),
        "IndexedFlat" => new IndexedFlatWorkload(10),
        "MultiValueDupKey" => new MultiValueDupKeyWorkload(12, 3),
        "MixedDiscipline" => new MixedDisciplineWorkload(8),
        _ => throw new ArgumentException($"unknown workload '{name}'", nameof(name)),
    };

    private static IEnumerable<TestCaseData> WorkloadCases()
    {
        foreach (var w in NonClusterWorkloads)
        {
            yield return new TestCaseData(w).SetName($"WalWindow_{w}");
        }
    }

    private static IEnumerable<TestCaseData> PageAxisCases()
    {
        foreach (var w in NonClusterWorkloads)
        {
            foreach (var n in CrashBoundaries)
            {
                yield return new TestCaseData(w, n).SetName($"PageAxis_{w}_N{n}");
            }
        }
    }

    private static string CurrentDatabaseName
    {
        get
        {
            var name = TestContext.CurrentContext.Test.Name;
            foreach (var c in new[] { '(', ')', ',', ' ', '"' })
            {
                name = name.Replace(c, '_');
            }

            const int max = 63;
            const string prefix = "Sweep_";
            if (prefix.Length + name.Length > max)
            {
                name = name[^(max - prefix.Length)..];
            }

            return prefix + name;
        }
    }

    [SetUp]
    public void Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(WalCrashSweepTests));
        _dbDir = Path.Combine(root, CurrentDatabaseName, "db");
        _walDir = Path.Combine(root, CurrentDatabaseName, "wal");
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b =>
            {
                b.AddSimpleConsole();
                b.SetMinimumLevel(LogLevel.Warning);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = CurrentDatabaseName;
                opts.DatabaseDirectory = _dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = _walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;

        var testRoot = Directory.GetParent(_dbDir)?.FullName;
        try
        {
            if (testRoot != null && Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    // ── AC5: WAL-window oracle over all non-cluster workloads (with / without a consolidating checkpoint) ──

    [Test]
    [CancelAfter(20_000)]
    [TestCaseSource(nameof(WorkloadCases))]
    [VerifiesRule("AP-12")]
    public void WalWindow_Recover_OracleHolds(string workloadName)
    {
        var workload = MakeWorkload(workloadName);
        var shadow = new RecoveryShadowModel();

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            workload.Register(dbe);
            dbe.InitializeArchetypes();
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                workload.Execute(uow, shadow);
                uow.Flush();
            }

            shadow.CaptureValues(dbe);
            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            workload.Register(dbe);
            dbe.InitializeArchetypes();
            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);
        }
    }

    [Test]
    [CancelAfter(20_000)]
    [TestCaseSource(nameof(WorkloadCases))]
    [VerifiesRule("AP-12")]
    public void WalWindow_MidCheckpoint_OracleHolds(string workloadName)
    {
        // KnownIssue #398: a hard crash AFTER a consolidating checkpoint loses the enabled-bits of FLAT archetypes. The checkpoint persists the
        // EntityMap's data chunks but not its index metadata (ArchetypeR1.EntityMapSPI + the hash-map entryCount/bucket directory), so the
        // consolidated map is orphaned on reopen → increment-D rebuilds it all-enabled. Flat archetypes have no derivable enabled-bits source
        // (cluster archetypes recover them from EnabledBits[C]). LifecycleChurn disables a component on a flat archetype, so it trips this.
        // Remove this guard when #398 is fixed. The same workload's WalWindow_Recover (live WAL window → replay heals) stays green.
        if (workloadName == "LifecycleChurn")
        {
            Assert.Ignore("KnownIssue #398: flat-archetype enabled-bits not durable through a consolidating checkpoint on hard crash.");
        }

        var workload = MakeWorkload(workloadName);
        var shadow = new RecoveryShadowModel();

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            workload.Register(dbe);
            dbe.InitializeArchetypes();
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                workload.Execute(uow, shadow);
                uow.Flush();
            }

            // Consolidate the workload into the data file (CheckpointLSN advances past its LSNs), then hard-crash with an empty WAL window —
            // recovery must restore from the persisted base + rebuilds (RB-01) alone.
            dbe.ForceCheckpoint();
            dbe.CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(10));

            shadow.CaptureValues(dbe);
            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            workload.Register(dbe);
            dbe.InitializeArchetypes();
            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);
        }
    }

    // ── AC4: page-axis — crash the checkpoint at every write boundary; the aborted cycle never advances CheckpointLSN, so replay heals ──

    [Test]
    [CancelAfter(20_000)]
    [TestCaseSource(nameof(PageAxisCases))]
    [VerifiesRule("CK-03")]
    public void PageAxis_CheckpointCrashAtBoundary_OracleHolds(string workloadName, int crashAtWrite)
    {
        var workload = MakeWorkload(workloadName);
        var shadow = new RecoveryShadowModel();

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            var mmf = scope1.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            workload.Register(dbe);
            dbe.InitializeArchetypes();
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                workload.Execute(uow, shadow);
                uow.Flush();
            }

            shadow.CaptureValues(dbe);

            // Crash the synchronous checkpoint at the Nth page write. RunCheckpointCycle swallows the ChaosSimulatedCrashException (CK-06) and
            // returns WITHOUT advancing CheckpointLSN — pages 1..N-1 may be on disk, but the coverage gate keeps the whole WAL window live.
            var chaos = new ChaosPageIO();
            chaos.WireTo(mmf);
            chaos.SetCrashAtPageWrite(crashAtWrite);
            dbe.CheckpointManager.RunCheckpointCycle(dbe.WalManager.DurableLsn);
            chaos.Unwire(mmf);

            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            workload.Register(dbe);
            dbe.InitializeArchetypes();
            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);
        }
    }

    // ── P2: MixedDiscipline (08 §5) joins the sweep ─────────────────────────────────────────────────────────────────
    // A cluster-eligible all-SV archetype written under interleaved TickFence + Commit transactions. The asserted state is the Commit-durable
    // last-writer values, so the oracle proves recovery reproduces them at every boundary despite the TickFence churn. Cluster + Commit-write means the
    // entity rides the Commit slot records through RecoveryApplier's cluster reconstruction (the path #392 built), not the pure-SV-spawn path that
    // excludes ClusterAllSv.

    private static IEnumerable<TestCaseData> MixedDisciplinePageAxisCases()
    {
        foreach (var n in CrashBoundaries)
        {
            yield return new TestCaseData(n).SetName($"PageAxis_MixedDiscipline_N{n}");
        }
    }

    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("AP-12")]
    public void MixedDiscipline_WalWindow_Recover_OracleHolds()
    {
        var workload = new MixedDisciplineWorkload(8);
        var shadow = new RecoveryShadowModel();

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            workload.Register(dbe);
            dbe.InitializeArchetypes();
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                workload.Execute(uow, shadow);
                uow.Flush();
            }

            shadow.CaptureValues(dbe);
            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            workload.Register(dbe);
            dbe.InitializeArchetypes();
            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);
        }
    }

    // #395 Face A (consolidation-orphan) — FIXED. A consolidating checkpoint followed by a hard crash used to LOSE the cluster entities entirely: the
    // checkpoint wrote the cluster DATA pages (OccupancyBits / EntityKeys / SoA) to the data file but the per-archetype segment SPIs in the durable
    // ArchetypeR1 table were recorded only at clean shutdown, so reopen found ArchetypeR1.ClusterSegmentSPI == 0, took the fresh-allocation path, and
    // rebuilt from an empty cluster (ActiveClusterCount == 0). The fix persists the SPIs at every checkpoint
    // (CheckpointManager.PersistDurableMetadataHook → DatabaseEngine.PersistArchetypeState, run before the cycle's barrier), so the consolidated base
    // is reachable on reopen and the EntityMap rebuild re-derives the entities from the cluster occupancy. (NB: this is distinct from #395 Face B — a
    // plain SV cluster *spawn* value is not WAL-durable per-commit, so ClusterAllSv, which never checkpoints and never Commit-writes, stays red; that
    // is the Committed discipline's job.)
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("AP-12")]
    public void MixedDiscipline_WalWindow_MidCheckpoint_OracleHolds()
    {
        var workload = new MixedDisciplineWorkload(8);
        var shadow = new RecoveryShadowModel();

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            workload.Register(dbe);
            dbe.InitializeArchetypes();
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                workload.Execute(uow, shadow);
                uow.Flush();
            }

            // Consolidate the Commit-discipline writes into the data file (CheckpointLSN advances past their LSNs), then hard-crash with an empty WAL
            // window — recovery must restore the cluster SV state from the persisted base alone.
            dbe.ForceCheckpoint();
            dbe.CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(10));

            shadow.CaptureValues(dbe);
            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            workload.Register(dbe);
            dbe.InitializeArchetypes();
            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);
        }
    }

    [Test]
    [CancelAfter(20_000)]
    [TestCaseSource(nameof(MixedDisciplinePageAxisCases))]
    [VerifiesRule("CK-03")]
    public void MixedDiscipline_PageAxis_CheckpointCrashAtBoundary_OracleHolds(int crashAtWrite)
    {
        var workload = new MixedDisciplineWorkload(8);
        var shadow = new RecoveryShadowModel();

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            var mmf = scope1.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            workload.Register(dbe);
            dbe.InitializeArchetypes();
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                workload.Execute(uow, shadow);
                uow.Flush();
            }

            shadow.CaptureValues(dbe);

            var chaos = new ChaosPageIO();
            chaos.WireTo(mmf);
            chaos.SetCrashAtPageWrite(crashAtWrite);
            dbe.CheckpointManager.RunCheckpointCycle(dbe.WalManager.DurableLsn);
            chaos.Unwire(mmf);

            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            workload.Register(dbe);
            dbe.InitializeArchetypes();
            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);
        }
    }
}
