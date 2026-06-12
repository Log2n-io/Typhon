using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// The T-5 differential recovery oracle (design 03 §4.2, 08 T-5) exercised at one crash point. Each test runs a workload to durability, hard-crashes
/// (<see cref="DatabaseEngine.SimulateHardCrash"/>), reopens to drive WAL v2 recovery, then asserts the recovered engine reproduces a <see cref="RecoveryShadowModel"/>
/// captured just before the crash. This is the differential regression lock for the P1.2 flat-path recovery (increments 1–8 generalized from hand-picked asserts into a
/// property) and the evidence generator that adjudicates the two remaining gaps:
/// <list type="bullet">
/// <item><b>index axis</b> (<see cref="IndexedFlat_IndexAxis_MatchesBroadScan"/>) — a recovered <i>indexed</i> archetype whose secondary B+Tree is not rebuilt.</item>
/// <item><b>cluster axis</b> (<see cref="ClusterAllSv_PrimaryAxis_SurvivesCrash"/>) — a recovered all-SingleVersion (cluster-eligible) archetype the flat-only applier
/// does not restore.</item>
/// </list>
/// Both are quarantined RED (<c>KnownIssue-395-*</c>): the test asserts the desired post-fix behaviour and goes green when the corresponding increment lands, exactly as
/// the One True Crash Test gated P1.2. The harness mirrors <see cref="TrueCrashE2ETests"/>; the full crash sweep (A1.2) is P1.5 and reuses this oracle over many crash points.
/// </summary>
[TestFixture]
internal sealed class DifferentialRecoveryOracleTests
{
    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;

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
            const string prefix = "Dro_";
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
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(DifferentialRecoveryOracleTests));
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
            if (testRoot != null && Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    /// <summary>Run the workload to durability and capture the shadow on the live engine, invoking <paramref name="onLive"/> before any crash (used by the self-test).</summary>
    private void RunWorkloadLive(IRecoveryWorkload workload, Action<DatabaseEngine, RecoveryShadowModel> onLive)
    {
        var shadow = new RecoveryShadowModel();
        using var scope = _serviceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        workload.Register(dbe);
        dbe.InitializeArchetypes();

        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            workload.Execute(uow, shadow);
            uow.Flush();
        }

        shadow.CaptureValues(dbe);
        onLive(dbe, shadow);
    }

    /// <summary>Run the workload to durability, capture the shadow, hard-crash, reopen to drive recovery, then invoke <paramref name="assertRecovered"/> on the recovered engine.</summary>
    private void RecoverWith(IRecoveryWorkload workload, Action<DatabaseEngine, RecoveryShadowModel> assertRecovered)
    {
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

            shadow.CaptureValues(dbe); // read-back committed state just before the crash → the "expected" half of the oracle
            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            workload.Register(dbe);
            dbe.InitializeArchetypes(); // auto-runs RunWalV2Recovery + SealRecovery
            assertRecovered(dbe, shadow);
        }
    }

    /// <summary>
    /// Like <see cref="RecoverWith"/> but forces a checkpoint between two workload phases, so <paramref name="beforeCheckpoint"/>'s entities land below the checkpoint
    /// frontier (recovered from the data file) and <paramref name="afterCheckpoint"/>'s land in the WAL window (recovered by replay). Both phases share one shadow and
    /// must use the same components (only the first phase's Register runs).
    /// </summary>
    private void RecoverWithMidCheckpoint(IRecoveryWorkload beforeCheckpoint, IRecoveryWorkload afterCheckpoint, Action<DatabaseEngine, RecoveryShadowModel> assertRecovered)
    {
        var shadow = new RecoveryShadowModel();

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            beforeCheckpoint.Register(dbe);
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                beforeCheckpoint.Execute(uow, shadow);
                uow.Flush();
            }

            // Consolidate phase 1 into the data file: its entities + indexes now live below the checkpoint frontier (CheckpointLSN advances past their LSNs).
            dbe.ForceCheckpoint();
            dbe.CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(10));

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                afterCheckpoint.Execute(uow, shadow);
                uow.Flush();
            }

            shadow.CaptureValues(dbe);
            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            beforeCheckpoint.Register(dbe);
            dbe.InitializeArchetypes(); // auto-runs RunWalV2Recovery + SealRecovery over the WAL window (phase 2)
            assertRecovered(dbe, shadow);
        }
    }

    // ── AC1 — the oracle cannot false-green ──────────────────────────────────

    [Test]
    [CancelAfter(15_000)]
    public void ShadowModel_MutatedCopy_IsDetected()
    {
        RunWorkloadLive(new SingleTxSpawnWorkload(8), (dbe, shadow) =>
        {
            // The shadow was just captured from this very engine — it must match (0 diffs).
            Assert.That(shadow.Diff(dbe), Is.Empty, "a shadow captured from the live engine must match it exactly");

            // Corrupt one captured expected value byte. The oracle MUST now report a mismatch — proving Diff genuinely compares bytes and cannot false-green.
            var first = shadow.Entities.Values.First();
            first.ValueBytesBySlot[0][0] ^= 0xFF;
            Assert.That(shadow.Diff(dbe), Is.Not.Empty, "a corrupted expected value must be reported as a diff");
        });
    }

    // ── AC4 — primary (broad-scan) axis green on the flat path ───────────────

    [Test]
    [CancelAfter(15_000)]
    public void SingleTxSpawn_PrimaryAxis_SurvivesCrash() => RecoverWith(new SingleTxSpawnWorkload(10), RecoveryOracle.AssertPrimaryAxis);

    [Test]
    [CancelAfter(15_000)]
    public void LifecycleChurn_PrimaryAxis_SurvivesCrash() => RecoverWith(new LifecycleChurnWorkload(seed: 9876, count: 24), RecoveryOracle.AssertPrimaryAxis);

    // Indexed/overhead-bearing Versioned component (CompD carries ComponentOverhead=8): the slot emit and recovery now read/write the value at offset ComponentOverhead, so
    // the trailing field (double C) survives the WAL round-trip. This is where the oracle first surfaced the overhead-emit bug; green since the symmetric ComponentOverhead fix.
    [Test]
    [CancelAfter(15_000)]
    public void IndexedFlat_PrimaryAxis_SurvivesCrash() => RecoverWith(new IndexedFlatWorkload(10), RecoveryOracle.AssertPrimaryAxis);

    // ── AC5 — index axis: secondary B+Trees are rebuilt post-recovery (RB-01) ──

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("RB-01")]
    public void IndexedFlat_IndexAxis_MatchesBroadScan()
    {
        RecoverWith(new IndexedFlatWorkload(10), (dbe, shadow) =>
        {
            // Values recover faithfully (overhead-emit fix); now assert the secondary index does too.
            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);

            var compDArch = shadow.Entities.Keys.First().ArchetypeId; // all IndexedFlat entities are CompDArch
            using var tx = dbe.CreateQuickTransaction();
            var broad = RecoveryOracle.BroadScanEntityIds(tx, compDArch);
            Assert.That(broad, Is.Not.Empty, "sanity: the indexed entities must be recovered (broad-scannable) for the index-axis comparison to be meaningful");
            var indexed = RecoveryOracle.IndexEntityIds<CompD, int>(dbe, tx, d => d.B, int.MinValue, int.MaxValue);

            // The CompD.B index must report exactly the recovered entities — recovery rebuilds secondary indexes from the recovered values (RB-01); persisted indexes
            // are never trusted post-crash.
            Assert.That(
                indexed,
                Is.EquivalentTo(broad),
                $"index axis: CompD.B index result set ({indexed.Count}) must equal the broad-scan set ({broad.Count}); a shortfall means recovery did not rebuild "
                + "the secondary index (RB-01).");
        });
    }

    // ── AC6 — cluster-axis measurement: P2-gated, NOT a P1 recovery-routing bug ──
    // The oracle established (record-kind counts: spawns=10, slots=0, with and without a tick-fence) that cluster/SingleVersion spawn VALUES are checkpoint-durable,
    // not WAL-durable per-commit: the spawn path deliberately does not set the cluster fence dirty bitmap (Transaction.ECS.cs:1522), so neither the per-commit log nor
    // the tick-fence carries the value. An Immediate commit makes an SV value durable only at the next checkpoint; per-commit SV WAL durability is the Committed
    // discipline (P2 / design D5). So a window cluster/SV spawn recovers alive-but-default — a phantom — until P2. (Spawn IS logged but its value is not: an atomicity
    // nuance flagged for P2.) This is therefore not a RecoveryApplier routing fix; it goes green when P2 makes SV values WAL-durable.
    [Test]
    [CancelAfter(15_000)]
    [Category("KnownIssue-395-sv-durability-p2")]
    public void ClusterAllSv_PrimaryAxis_SurvivesCrash() => RecoverWith(new ClusterAllSvWorkload(10), RecoveryOracle.AssertPrimaryAxis);

    // ── Scale: a large indexed workload forces the recovery index rebuild to split the B+Tree across many nodes — stresses the apply loop + RB-01 (index.Add) at scale ──
    [Test]
    [CancelAfter(15_000)]
    public void IndexedFlat_AtScale_ValuesAndIndexRecover() => AssertIndexedFlatRecovers(600);

    // ── A commit whose WAL batch exceeds the writer's 256 KB staging buffer forces WalWriter.WriteInChunks. That path used to copy + CRC-patch each write-slice
    // independently, so a record-batch chunk straddling a 256 KB slice boundary kept its zero-placeholder footer CRC — which recovery reads as a CRC break, mistakes for a
    // torn tail, and truncates at, silently losing every record after it (recovery returned 0 applied). ~4000 CompD entities make the single committed frame > 256 KB,
    // deterministically exercising the multi-slice write regardless of drain timing. The oracle surfaced this at scale (first mis-attributed to multi-segment rotation —
    // the WAL was actually a flood of FPI frames hiding an unpatched chunk); the fix patches the whole drained batch before streaming the page-aligned writes. This is the
    // regression lock for that fix: full value + index recovery proves no chunk was left unpatched across the staging boundary. ──
    [Test]
    [CancelAfter(15_000)]
    public void IndexedFlat_LargeDrain_ExceedsStagingBuffer_Recovers() => AssertIndexedFlatRecovers(4000);

    private void AssertIndexedFlatRecovers(int count)
    {
        RecoverWith(new IndexedFlatWorkload(count), (dbe, shadow) =>
        {
            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);

            var compDArch = shadow.Entities.Keys.First().ArchetypeId;
            using var tx = dbe.CreateQuickTransaction();
            var broad = RecoveryOracle.BroadScanEntityIds(tx, compDArch);
            var indexed = RecoveryOracle.IndexEntityIds<CompD, int>(dbe, tx, d => d.B, int.MinValue, int.MaxValue);
            Assert.That(
                indexed,
                Is.EquivalentTo(broad),
                $"index axis at scale: index set ({indexed.Count}) must equal broad-scan set ({broad.Count}) — RB-01 rebuild across B+Tree node splits.");
        });
    }

    // ── Checkpoint-frontier crash: phase-1 below the frontier (data file) + phase-2 in the WAL window must BOTH recover — values and index ──
    [Test]
    [CancelAfter(15_000)]
    public void CheckpointFrontier_BelowAndWindow_BothRecoverWithIndex()
    {
        RecoverWithMidCheckpoint(
            new IndexedFlatWorkload(count: 8, keyBase: 0),    // below the frontier (checkpointed into the data file)
            new IndexedFlatWorkload(count: 8, keyBase: 100),  // in the WAL window (recovered by replay)
            (dbe, shadow) =>
            {
                // All 16 entities recover with correct values, regardless of which side of the frontier they were on.
                RecoveryOracle.AssertPrimaryAxis(dbe, shadow);

                // The CompD.B index must span the frontier: checkpointed (persisted) entries + window (recovery-rebuilt) entries = the full broad-scan set.
                var compDArch = shadow.Entities.Keys.First().ArchetypeId;
                using var tx = dbe.CreateQuickTransaction();
                var broad = RecoveryOracle.BroadScanEntityIds(tx, compDArch);
                var indexed = RecoveryOracle.IndexEntityIds<CompD, int>(dbe, tx, d => d.B, int.MinValue, int.MaxValue);
                Assert.That(
                    indexed,
                    Is.EquivalentTo(broad),
                    $"index axis across the checkpoint frontier: index set ({indexed.Count}) must equal broad-scan set ({broad.Count}) — below-frontier (persisted) + window (rebuilt).");
            });
    }
}
