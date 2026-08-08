using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// The T-5 differential recovery oracle (design 03 §4.2, 08 T-5) exercised at one crash point. Each test runs a workload to durability, hard-crashes
/// (<see cref="DatabaseEngine.SimulateHardCrash"/>), reopens to drive WAL v2 recovery, then asserts the recovered engine reproduces a <see cref="RecoveryShadowModel"/>
/// captured just before the crash. This is the differential regression lock for the P1.2 flat-path recovery (increments 1–8 generalized from hand-picked asserts into a
/// property) and the evidence generator that adjudicated the two gaps it originally surfaced — both now FIXED and green:
/// <list type="bullet">
/// <item><b>index axis</b> (<see cref="IndexedFlat_IndexAxis_MatchesBroadScan"/>) — a recovered <i>indexed</i> archetype's secondary B+Tree, now
/// rebuilt at recovery (RB-01).</item>
/// <item><b>cluster axis</b> (<see cref="ClusterAllSv_PrimaryAxis_SurvivesCrash"/>) — a recovered all-SingleVersion (cluster-eligible) archetype:
/// spawned under the Commit discipline its SV values are now WAL-logged per-commit and restored exactly (#395 Face B / design D5).</item>
/// </list>
/// The harness mirrors <see cref="TrueCrashE2ETests"/>; the full crash sweep (A1.2, <see cref="WalCrashSweepTests"/>) reuses this oracle over many
/// crash points.
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
                    // Shrink the staging buffer to 8 KiB so a modest frame straddles multiple slice boundaries and exercises the WriteInChunks / cross-slice
                    // PatchChunkCrcs path — the on-disk frame + recovery are byte-identical regardless of slice size, so this only broadens coverage while
                    // letting the "exceeds staging buffer" case run at 600 entities instead of the cache-thrashing 4000.
                    StagingBufferSize = 8 * 1024,
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
    /// <see cref="RecoverWith"/> plus a <b>write-after-recovery</b> phase (#705 T3): recover, assert, let the workload keep mutating the recovered engine, then
    /// crash a SECOND time and require both generations to survive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the shape RocksDB's <c>db_stress</c> has and this suite did not: an expected-state record that survives the crash, is compared after reopen,
    /// <b>and keeps being written to afterwards</b>. Everything the crash suite asserted before stopped at the reopen, which is why a recovery that restores
    /// the data perfectly while leaving an allocator watermark wrong was unobservable here no matter how many workloads existed.
    /// </para>
    /// <para>
    /// The growth check is not a formality. A <c>Resume</c> that does nothing would let this harness report post-recovery-write coverage it never performed —
    /// and because <c>Resume</c> is default-implemented, EVERY existing workload can be passed here and would silently qualify. So the harness demands
    /// evidence that the phase did something, and names the workload when it did not.
    /// </para>
    /// </remarks>
    private void RecoverAndResume(IRecoveryWorkload workload, Action<DatabaseEngine, RecoveryShadowModel> assertAfterSecondCrash, bool crashAgain = true)
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

            shadow.CaptureValues(dbe);
            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            workload.Register(dbe);
            dbe.InitializeArchetypes(); // recovery

            // Generation 1 must be faithful BEFORE anything writes over it — otherwise a post-Resume failure could not be attributed.
            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);

            var beforeResume = shadow.AliveIds.Count;
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                workload.Resume(uow, shadow);
                uow.Flush();
            }

            Assert.That(
                shadow.AliveIds.Count,
                Is.GreaterThan(beforeResume),
                $"{workload.Name}: Resume() left the shadow at {beforeResume} entities — it wrote nothing the oracle can check. A default (no-op) Resume must "
                + "not reach this harness; implement the phase or use RecoverWith instead.");

            TestContext.WriteLine(
                $"{workload.Name} session 2 (recovered engine) after Resume: lastAppendedLsn={dbe.WalManager.LastAppendedLsn} "
                + $"durableLsn={dbe.WalManager.DurableLsn} recoveryFrontier={dbe.LastWalV2RecoveryResult.MaxLsn} "
                + $"checkpointLsnAtOpen={dbe.LastWalV2RecoveryCheckpointLsn}");

            // Both generations, live, on the recovered engine.
            shadow.CaptureValues(dbe);
            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);

            if (!crashAgain)
            {
                return;
            }

            dbe.SimulateHardCrash();
        }

        using (var scope3 = _serviceProvider.CreateScope())
        {
            var dbe = scope3.ServiceProvider.GetRequiredService<DatabaseEngine>();
            workload.Register(dbe);
            dbe.InitializeArchetypes(); // recovery again — now over a window written by a RECOVERED engine

            // The second recovery is the one nothing has ever exercised, so its numbers are printed unconditionally: a diff alone cannot distinguish
            // "the window was skipped" from "the window was applied and lost afterwards", and those have different causes.
            TestContext.WriteLine(
                $"{workload.Name} second recovery: checkpointLsn(threshold)={dbe.LastWalV2RecoveryCheckpointLsn} "
                + $"scanned={dbe.LastWalV2RecoveryResult.RecordsScanned} applied={dbe.LastWalV2RecoveryResult.RecordsApplied} "
                + $"maxLsn={dbe.LastWalV2RecoveryResult.MaxLsn} txCommitted={dbe.LastWalV2RecoveryResult.TxCommitted}");

            assertAfterSecondCrash(dbe, shadow);
        }
    }

    /// <summary>
    /// The cross-frontier harness (#705 T3 / #569): phase 1 commits and is CONSOLIDATED by a checkpoint, then phase 2 — built from phase 1's alive-set —
    /// mutates those same entities in the WAL window, and a hard crash must recover the phase-2 values.
    /// </summary>
    /// <remarks>
    /// The only structural difference from <see cref="RecoverWithMidCheckpoint"/> is that phase 2 is constructed AFTER phase 1 has run, from the shadow. That
    /// is what lets the two windows touch the same entities: with both workloads built up front, each could only spawn its own, which is why
    /// <c>RecoverWithMidCheckpoint</c>'s existing callers all pass disjoint <c>keyBase</c> values and why this case had never been expressed.
    /// </remarks>
    private void RecoverWithCrossFrontierUpdate(
        IRecoveryWorkload seed,
        Func<IReadOnlyCollection<EntityId>, IRecoveryWorkload> makeUpdater,
        Action<DatabaseEngine, RecoveryShadowModel> assertRecovered)
    {
        var shadow = new RecoveryShadowModel();

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            seed.Register(dbe);
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                seed.Execute(uow, shadow);
                uow.Flush();
            }

            // Consolidate phase 1 BELOW the checkpoint frontier: its entities now live in the data file, so a later record for one of them carries no Spawn in
            // the window — the `!agg.HasSpawn` branch #569 is about.
            dbe.WriteTickFence(1);
            dbe.ForceCheckpoint();
            dbe.CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(10));

            // Snapshot the ids: AliveIds is a live view over the shadow's dictionary, and the updater will be reading it while the shadow is in scope.
            var updater = makeUpdater([.. shadow.AliveIds]);
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                updater.Execute(uow, shadow);
                uow.Flush();
            }

            dbe.WriteTickFence(2);

            // Captured AFTER the update, so "expected" is the post-update state — the ≤1-tick window's own claim.
            shadow.CaptureValues(dbe);
            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            seed.Register(dbe);
            dbe.InitializeArchetypes();

            TestContext.WriteLine(
                $"{seed.Name} cross-frontier recovery: checkpointLsn={dbe.LastWalV2RecoveryCheckpointLsn} "
                + $"scanned={dbe.LastWalV2RecoveryResult.RecordsScanned} applied={dbe.LastWalV2RecoveryResult.RecordsApplied} "
                + $"maxLsn={dbe.LastWalV2RecoveryResult.MaxLsn}");

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

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion to every AP-12 verifier that asserts through
    /// <see cref="RecoveryOracle.AssertPrimaryAxis"/>: it proves that ASSERTION rejects a divergence, not merely
    /// that <see cref="RecoveryShadowModel.Diff"/> returns a non-empty list.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole point. <see cref="ShadowModel_MutatedCopy_IsDetected"/> above pins `Diff`'s
    /// RETURN VALUE, but every AP-12 verifier in the suite calls `AssertPrimaryAxis`, and a wired-wrong assertion
    /// there (asserting on the wrong collection, or with a matcher that cannot fail) would leave every one of them
    /// permanently green while `Diff` kept working perfectly. So the mutant drives the real path and requires the
    /// failure to carry the oracle's OWN message — positive evidence, per <see cref="RuleMutants.AssertDetects"/>.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("AP-12")]
    public void Mutant_PrimaryAxisAssertion_RejectsADivergentShadow()
    {
        RunWorkloadLive(new SingleTxSpawnWorkload(8), (dbe, shadow) =>
        {
            // Sanity: unmutated, the oracle's own assertion passes against the engine it was captured from. Without
            // this the mutant could "detect" a divergence that was there all along.
            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);

            shadow.Entities.Values.First().ValueBytesBySlot[0][0] ^= 0xFF;

            RuleMutants.AssertDetects(
                "AP-12",
                "Differential oracle — primary (broad-scan) axis found",
                () => RecoveryOracle.AssertPrimaryAxis(dbe, shadow));
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

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion to <see cref="IndexedFlat_IndexAxis_MatchesBroadScan"/>:
    /// proves the index-axis comparison rejects a SHORTFALL — the failure mode RB-01 is about.
    /// </summary>
    /// <remarks>
    /// This assertion has a specific way of being vacuously true, and <see cref="RecoveryOracle"/>'s own docstring
    /// warns about its mirror image: if the index enumeration returns nothing AND the broad scan returns nothing,
    /// `Is.EquivalentTo` passes while proving nothing. The real verifier guards one half with a "broad is not empty"
    /// sanity assert; this mutant guards the other, by removing a single entity from the index side and requiring
    /// the equivalence to reject it. A comparison that cannot see one missing entity cannot see recovery failing to
    /// rebuild a secondary index either.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("RB-01")]
    public void Mutant_IndexAxisComparison_RejectsAOneEntityShortfall()
    {
        RecoverWith(new IndexedFlatWorkload(10), (dbe, shadow) =>
        {
            var compDArch = shadow.Entities.Keys.First().ArchetypeId;
            using var tx = dbe.CreateQuickTransaction();
            var broad = RecoveryOracle.BroadScanEntityIds(tx, compDArch);
            var indexed = RecoveryOracle.IndexEntityIds<CompD, int>(dbe, tx, d => d.B, int.MinValue, int.MaxValue);

            // Unmutated the two sets agree — otherwise the mutant would be "detecting" a pre-existing divergence.
            Assert.That(indexed, Is.EquivalentTo(broad), "sanity: the index and broad-scan sets must agree before the mutation");

            // Exactly the shape RB-01 describes: recovery rebuilt the entity but not its index entry.
            indexed.Remove(indexed.First());

            RuleMutants.AssertDetects(
                "RB-01",
                "index axis: CompD.B index result set",
                () => Assert.That(
                    indexed,
                    Is.EquivalentTo(broad),
                    $"index axis: CompD.B index result set ({indexed.Count}) must equal the broad-scan set ({broad.Count}); a shortfall means recovery did not "
                    + "rebuild the secondary index (RB-01)."));
        });
    }

    // ── AC6 — cluster-axis recovery under the Commit discipline (#395 Face B — FIXED) ──
    // The oracle originally established (record-kind counts: spawns=10, slots=0) that a TickFence cluster/SingleVersion spawn logs its lifecycle but
    // NOT its values — the spawn copies them into the cluster SoA (checkpoint-durable) without emitting Slot records, so a hard crash before a
    // checkpoint recovered the entity alive-but-default (a phantom). That was #395 Face B, deferred to "the Committed discipline makes per-commit SV
    // WAL durability" (design D5). It is now FIXED: BuildCommitBatch emits a Slot upsert per SingleVersion spawn value when the tx is
    // Commit-discipline, and recovery aggregates the Spawn + Slots and applies them together (ApplySpawnedEntity → cluster slot claim + SoA write). So
    // an all-SV cluster archetype spawned under Commit discipline recovers EXACTLY across a hard crash with NO checkpoint. (A plain TickFence spawn is
    // still checkpoint-durable only — the documented non-guarantee, not a bug.)
    [Test]
    [CancelAfter(15_000)]
    public void ClusterAllSv_PrimaryAxis_SurvivesCrash()
        => RecoverWith(new ClusterAllSvWorkload(10, DurabilityDiscipline.Commit), RecoveryOracle.AssertPrimaryAxis);

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // #705 T3 — the WRITE-AFTER-RECOVERY axis (#697)
    //
    // Every assertion above this line stops at the reopen. A recovery that restores the data faithfully but leaves an allocation watermark below the
    // recovered population is therefore invisible to all of them: the first post-recovery Spawn re-issues a live entity's id and overwrites it, silently.
    // These cases continue the run past the reopen, so the defect has somewhere to surface.
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The three storage homes, named per CONSUMER — <c>SetName</c> replaces the whole display name, so two tests sharing one source and one name template
    /// produce indistinguishable cases, and a failure cannot be attributed to the test that produced it.
    /// </summary>
    private static IEnumerable<TestCaseData> PostRecoveryShapes(string prefix)
    {
        foreach (var shape in new[] { PostRecoveryShape.Flat, PostRecoveryShape.FlatIndexed, PostRecoveryShape.ClusterSv })
        {
            yield return new TestCaseData(shape).SetName($"{prefix}_{shape}");
        }
    }

    /// <summary>
    /// Reopen after a crash, spawn a second generation, and require every new <see cref="EntityId"/> to be distinct from the recovered ones (#697).
    /// </summary>
    /// <remarks>
    /// Run across all three storage homes because the watermark is restored per-archetype and the homes derive it differently: the flat path rebuilds it from
    /// the persisted EntityMap, the cluster path from the cluster's own entity-id array, and the WAL window's own spawns — the case that was missing — from
    /// <c>RecoveryApplier</c>. A fix to one proves nothing about the others.
    /// <para>
    /// The collision is detected by <see cref="RecoveryShadowModel.RecordSpawn"/> rather than asserted here, so it fires at the exact spawn that re-issued the
    /// id and names it.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    [TestCaseSource(nameof(PostRecoveryShapes), new object[] { "PostRecoveryWrite_NoReissue" })]
    [VerifiesRule("RB-06")]
    public void PostRecoveryWrite_DoesNotReissueARecoveredEntityId(PostRecoveryShape shape)
        => RecoverAndResume(new PostRecoveryWriteWorkload(shape), RecoveryOracle.AssertPrimaryAxis, crashAgain: false);

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion to <see cref="PostRecoveryWrite_DoesNotReissueARecoveredEntityId"/>: prove the entity-key watermark
    /// restore is what makes it pass, by lowering the watermark back and requiring the collision to reappear.
    /// </summary>
    /// <remarks>
    /// A green verifier here could mean the watermark is restored, or simply that this particular workload never happens to collide. Those are very different
    /// claims and only one of them is RB-06. Driving the counter back below the recovered population settles it: the shadow must reject the re-issued id with
    /// the message the real defect produced.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    [RuleMutant("RB-06")]
    public void Mutant_LoweringTheEntityKeyWatermark_ReissuesALiveId()
    {
        var workload = new PostRecoveryWriteWorkload(PostRecoveryShape.Flat);
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

        using var scope2 = _serviceProvider.CreateScope();
        var recovered = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        workload.Register(recovered);
        recovered.InitializeArchetypes();
        RecoveryOracle.AssertPrimaryAxis(recovered, shadow);

        // Unmutated the watermark now sits at or above the recovered population — otherwise the mutation below would be "detecting" a defect already present.
        var routingId = shadow.AliveIds.First().ArchetypeId;
        var state = recovered._stateByRouting[routingId];
        Assert.That(state.NextEntityKey, Is.GreaterThanOrEqualTo(shadow.AliveIds.Count),
            "sanity: recovery must have restored the entity-key watermark above the recovered population (RB-06)");

        // Exactly the pre-fix state: the counter below the population it must not collide with.
        state.NextEntityKey = 0;

        // Asserted directly rather than through RuleMutants.AssertDetects, which requires the violation to surface as an NUnit AssertionException. This
        // detector THROWS instead, on purpose: it fires inside RecordSpawn at the exact spawn that re-issued the id, which an assertion at the end of the
        // workload could not localise. AssertDetects would classify the throw as a broken mutant, so using it here would report the opposite of the truth.
        using var resumeUow = recovered.CreateUnitOfWork(DurabilityMode.Immediate);
        var ex = Assert.Throws<InvalidOperationException>(() => workload.Resume(resumeUow, shadow));
        Assert.That(ex.Message, Does.Contain("#697"), "the detector must name the defect it is detecting");
    }

    /// <summary>
    /// The full write-after-recovery round trip: recover, write, crash AGAIN, and require both generations to survive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Quarantined against #712</b>, a P0 this axis found on its first run. A crash-recovered engine's WAL writer restarts its LSN sequence at 1 — below the
    /// LSNs the recovery it just performed replayed — so every commit it then acknowledges is written at an already-used LSN and discarded by the next
    /// recovery as already-consolidated. Measured: recovery frontier 16, <c>LastAppendedLsn</c> 9, <c>DurableLsn</c> 16, and the next open scans 8 records and
    /// applies 0. All 12 entities are lost, not just the 4 written after recovery.
    /// </para>
    /// <para>
    /// Kept separate from <see cref="PostRecoveryWrite_DoesNotReissueARecoveredEntityId"/> so #697's fix stays gated: the id-reissue half passes today, and
    /// folding the two together would have parked a working regression lock behind an unrelated defect.
    /// </para>
    /// </remarks>
    /// <summary>
    /// #712 / LOG-08 on the CRASH path: a crash-recovered engine must continue its LSN sequence strictly above the window its own recovery replayed.
    /// </summary>
    /// <remarks>
    /// Asserted on the watermarks rather than on entity survival, deliberately. Entity survival across the SECOND crash is
    /// <see cref="PostRecoveryWrite_SurvivesASecondCrash"/>'s job and it depends on more than the allocator; this case fails if and only if the LSN floor is
    /// wrong, so it cannot be greened by an unrelated durability change or reddened by one. Pre-fix numbers, from the issue: frontier 16,
    /// <c>LastAppendedLsn</c> 9, <c>DurableLsn</c> 16 — the writer restarted at 1 and believed 7 LSNs it never appended were durable.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    [TestCaseSource(nameof(PostRecoveryShapes), new object[] { "PostRecoveryWrite_LsnFloor" })]
    public void PostRecoveryWrite_ContinuesTheLsnSequenceAboveTheReplayedFrontier(PostRecoveryShape shape)
    {
        var workload = new PostRecoveryWriteWorkload(shape);
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

        using var scope2 = _serviceProvider.CreateScope();
        var recovered = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        workload.Register(recovered);
        recovered.InitializeArchetypes();   // recovery

        var frontier = recovered.LastWalV2RecoveryResult.MaxLsn;
        Assert.That(frontier, Is.GreaterThan(0), "the workload must leave a WAL window for recovery to replay, or this case proves nothing");

        // Before the first post-recovery write: the allocator already sits on the frontier, and the durable watermark agrees with it.
        Assert.That(recovered.WalManager.LastAppendedLsn, Is.EqualTo(frontier),
            "the reopened allocator did not continue from the replayed frontier (LOG-08) — the next record will reuse an LSN the prior session wrote");
        Assert.That(recovered.WalManager.LastAppendedLsn, Is.GreaterThanOrEqualTo(recovered.WalManager.DurableLsn),
            "DurableLsn exceeds LastAppendedLsn: the writer believes LSNs it never appended are durable");

        using (var uow = recovered.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            workload.Resume(uow, shadow);
            uow.Flush();
        }

        Assert.That(recovered.WalManager.LastAppendedLsn, Is.GreaterThan(frontier),
            $"post-recovery records were written at or below the replayed frontier {frontier} — the next recovery discards them as already-consolidated");
        Assert.That(recovered.WalManager.LastAppendedLsn, Is.GreaterThanOrEqualTo(recovered.WalManager.DurableLsn),
            "DurableLsn exceeds LastAppendedLsn after the post-recovery window");
    }

    [Test]
    [CancelAfter(20_000)]
    [TestCaseSource(nameof(PostRecoveryShapes), new object[] { "PostRecoveryWrite_SecondCrash" })]
    // Quarantine, not [Ignore] — #712 is a known red whose cause is unfixed, and Quarantine still runs it LOCALLY so whoever fixes #712 sees it flip. [Ignore]
    // is unconditional in NUnit and would run it nowhere, which is how #695 stayed invisible for months (#703).
    [Category("Quarantine")]
    public void PostRecoveryWrite_SurvivesASecondCrash(PostRecoveryShape shape)
        => RecoverAndResume(new PostRecoveryWriteWorkload(shape), RecoveryOracle.AssertPrimaryAxis);

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // #705 T3 — the CROSS-FRONTIER UPDATE axis (#569)
    //
    // Every existing two-phase case gives each phase its own entities with a disjoint keyBase, so the two WAL windows never touch the same entity. These
    // cases update entities the FIRST window committed and a checkpoint consolidated — the `!agg.HasSpawn` branch whose aggregated Slot payloads
    // RecoveryDriver drops.
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An entity checkpointed in window 1 and UPDATED in window 2 must recover with the window-2 value, not the checkpointed one (#569).
    /// </summary>
    /// <remarks>
    /// All three shapes run because the branch that drops the payload is keyed on "no Spawn in this window", not on storage mode — #569 is titled for
    /// SingleVersion, but the flat Versioned shapes take the same path. If only the cluster shape failed, the title would be right and the other two would be
    /// evidence of that; they are run to find out rather than to confirm.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    [TestCaseSource(nameof(PostRecoveryShapes), new object[] { "CrossFrontierUpdate" })]
    public void CrossFrontierUpdate_RecoversTheWindowValue_NotTheCheckpointedOne(PostRecoveryShape shape)
        => RecoverWithCrossFrontierUpdate(
            new PostRecoveryWriteWorkload(shape, preCount: 8, postCount: 0),
            existing => new CrossFrontierUpdateWorkload(shape, existing),
            RecoveryOracle.AssertPrimaryAxis);

    /// <summary>
    /// Two updates to the same slot in one window: recovery must apply the LATER one (#569's CM-03 acceptance criterion).
    /// </summary>
    /// <remarks>
    /// The aggregation at <c>RecoveryDriver.cs:53</c> keeps one <c>SlotData</c> per (entity, slot) and overwrites it as records arrive in LSN order, so
    /// latest-wins is a property of the ORDER records are read in, not of anything the applier does. That was untested while the payloads were being dropped —
    /// there was no observable difference between "keeps the last" and "keeps the first" when both were discarded. The superseded pass writes a value far from
    /// the final one, so applying the wrong record is a diff rather than a near-miss.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    [TestCaseSource(nameof(PostRecoveryShapes), new object[] { "CrossFrontierLastWriterWins" })]
    [VerifiesRule("CM-03")]
    public void CrossFrontierUpdate_TwoWritesInOneWindow_RecoversTheLastOne(PostRecoveryShape shape)
        => RecoverWithCrossFrontierUpdate(
            new PostRecoveryWriteWorkload(shape, preCount: 8, postCount: 0),
            existing => new CrossFrontierUpdateWorkload(shape, existing, passes: 2),
            RecoveryOracle.AssertPrimaryAxis);

    /// <summary>
    /// The workload must refuse an empty alive-set rather than quietly assert nothing.
    /// </summary>
    /// <remarks>
    /// A cross-frontier workload handed zero entities updates zero entities and passes — a green case reporting coverage of the exact interaction it exists to
    /// test. Same shape as the <c>Resume</c> growth check: the harness that makes an axis reachable must also make "reached it" falsifiable.
    /// </remarks>
    [Test]
    public void CrossFrontierUpdateWorkload_RejectsAnEmptyAliveSet()
    {
        var ex = Assert.Throws<ArgumentException>(() => new CrossFrontierUpdateWorkload(PostRecoveryShape.Flat, []));
        Assert.That(ex.Message, Does.Contain("would assert nothing"));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // #705 T3 — the PAYLOAD axes (#389)
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The oracle must SEE a collection's elements, not just its buffer descriptor — proven by dropping one and requiring the diff.
    /// </summary>
    /// <remarks>
    /// The direct analogue of <see cref="ShadowModel_MutatedCopy_IsDetected"/>, and the prerequisite for believing any #389 result: the recorded symptom is a
    /// collection going 5 elements → 0 while <c>Diff()</c> returned 0 mismatches, because the descriptor bytes were unchanged. A capture that cannot notice a
    /// missing element cannot notice a missing buffer either.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void CollectionOracle_DroppedElement_IsDetected()
    {
        var workload = new PayloadPayloadWorkload(4);
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

        shadow.CaptureValues(dbe, workload);
        Assert.That(shadow.Diff(dbe), Is.Empty, "a shadow captured from the live engine must match it exactly, collections included");

        // Drop one element from the EXPECTED side. The raw component bytes are untouched, so only an element-aware comparison can react.
        var victim = shadow.Entities.Values.First(e => e.CollectionElements[0].Length > 1);
        victim.CollectionElements = [victim.CollectionElements[0][..^1]];

        var diffs = shadow.Diff(dbe);
        Assert.That(diffs, Is.Not.Empty, "a collection with one element missing must be reported — otherwise #389's 5→0 case would read as green");
        Assert.That(string.Join("|", diffs), Does.Contain("element(s), expected"), "the diff must name the COUNT mismatch, the earliest signal");
    }

    /// <summary>
    /// Capturing a collection-bearing archetype WITHOUT a projector must throw, not silently compare descriptor bytes.
    /// </summary>
    /// <remarks>
    /// This is the guard that makes the projector non-optional in practice. Without it the #389 false-green is one forgotten argument away on the very test
    /// written to catch it — and the failure mode is a passing test, which nobody investigates.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void CaptureValues_WithoutAProjector_RefusesACollectionBearingArchetype()
    {
        var workload = new PayloadPayloadWorkload(2);
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

        var ex = Assert.Throws<InvalidOperationException>(() => shadow.CaptureValues(dbe));
        Assert.That(ex.Message, Does.Contain("#389"), "the refusal must name the defect it prevents hiding");
    }

    /// <summary>
    /// The payload axes across a hard crash: collection elements, <c>String64</c> and the spatial box must all recover.
    /// </summary>
    /// <remarks>
    /// <b>Quarantined against #389</b> — <c>ComponentCollection</c> buffer mutations are not WAL-redo-logged, so the buffer contents do not survive a crash in
    /// the WAL window. Kept as the regression lock for when #389 is fixed: it fails today for exactly the reason the issue documents, and it is the first test
    /// in the suite that CAN fail for that reason.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    [Category("Quarantine")]
    public void PayloadAxes_SurviveACrash()
    {
        var workload = new PayloadPayloadWorkload(8);
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

            dbe.WriteTickFence(1);
            shadow.CaptureValues(dbe, workload);
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

    /// <summary>
    /// The anti-false-green companion to <see cref="RecoverAndResume"/>'s growth check: a workload that does NOT implement <c>Resume</c> must be rejected.
    /// </summary>
    /// <remarks>
    /// <c>Resume</c> is a DEFAULT interface method, so every one of the pre-existing workloads satisfies the interface while writing nothing after recovery.
    /// Without this test, passing one of them to the resume harness would produce a green case advertising an axis it never touched — #704's trap 2, which
    /// cost a round of case names that promised coverage the fixture body never performed.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    public void RecoverAndResume_RejectsAWorkloadThatWritesNothingAfterRecovery()
    {
        var ex = Assert.Throws<AssertionException>(() => RecoverAndResume(new SingleTxSpawnWorkload(4), RecoveryOracle.AssertPrimaryAxis));
        Assert.That(ex.Message, Does.Contain("wrote nothing the oracle can check"), "the rejection must name the reason, not merely fail");
    }

    /// <summary>
    /// The shadow must REJECT a re-issued live id rather than absorb it.
    /// </summary>
    /// <remarks>
    /// <see cref="RecoveryShadowModel.RecordSpawn"/> used to assign through the indexer, so a duplicate id silently replaced the first-generation entity — and
    /// an oracle that has forgotten an entity cannot report it lost. That made the shadow structurally incapable of observing the exact defect the harness
    /// above exists to catch, so the guard is pinned here rather than left to inspection.
    /// </remarks>
    [Test]
    public void ShadowModel_RespawningALiveId_IsRejected()
    {
        var shadow = new RecoveryShadowModel();
        var id = EntityId.FromRaw(65537); // Entity(Key=1, Arch=1) — the id from #697's transcript
        shadow.RecordSpawn(id);

        var ex = Assert.Throws<InvalidOperationException>(() => shadow.RecordSpawn(id));
        Assert.That(ex.Message, Does.Contain("#697"), "the rejection must point at the defect it detects");
        Assert.That(shadow.AliveIds, Has.Count.EqualTo(1), "the original entity must still be in the shadow — absorbing the duplicate is the false-green");
    }

    // ── Scale: a large indexed workload forces the recovery index rebuild to split the B+Tree across many nodes — stresses the apply loop + RB-01 (index.Add) at scale ──
    [Test]
    [CancelAfter(15_000)]
    public void IndexedFlat_AtScale_ValuesAndIndexRecover() => AssertIndexedFlatRecovers(600);

    // ── A commit whose WAL batch exceeds the writer's staging buffer forces WalWriter.WriteInChunks. That path used to copy + CRC-patch each write-slice
    // independently, so a record-batch chunk straddling a slice boundary kept its zero-placeholder footer CRC — which recovery reads as a CRC break, mistakes for a
    // torn tail, and truncates at, silently losing every record after it (recovery returned 0 applied). The fixture shrinks the staging buffer to 8 KiB (Setup), so
    // 600 CompD entities make the single committed frame straddle multiple 8 KiB slices, deterministically exercising the multi-slice write regardless of drain timing
    // — at ~0.3 s instead of the ~7 s the old 4000-entity / 256 KiB-buffer form spent thrashing the page cache. The oracle surfaced this at scale (first mis-attributed
    // to multi-segment rotation — the WAL was actually a flood of FPI frames hiding an unpatched chunk); the fix patches the whole drained batch before streaming the
    // page-aligned writes. This is the regression lock for that fix: full value + index recovery proves no chunk was left unpatched across the staging boundary. ──
    [Test]
    [CancelAfter(15_000)]
    public void IndexedFlat_LargeDrain_ExceedsStagingBuffer_Recovers() => AssertIndexedFlatRecovers(600);

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

    // ── Cross-session checkpoint frontier (post-reopen window loss, LOG-class). Identical in spirit to CheckpointFrontier_BelowAndWindow_BothRecoverWithIndex EXCEPT the
    //    checkpoint happens in a PRIOR session: session 1 seeds + cleanly shuts down (its final dispose checkpoint persists CheckpointLSN into session-1's LSN space, and the
    //    seed lands in the .bin); session 2 reopens, commits the window (Immediate ⇒ durably acked), then HARD-crashes with the window living only in the WAL; session 3
    //    reopens and recovery must replay the window. The single difference from the green same-session test — the reopen between the two phases — is what exposes the bug:
    //    the reopened writer restarts record LSNs at 1, BELOW session-1's persisted CheckpointLSN, so RecoveryDriver's `Lsn <= checkpointLsn` skip silently drops the entire
    //    window. A durably-acked commit is lost — the One True Crash Test's blind spot (it crashes on a fresh open, never after a reopen). ──
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("LOG-08")]
    public void PostReopenWindow_AfterPriorSessionCheckpoint_SurvivesCrash()
    {
        var shadow = new RecoveryShadowModel();
        var seed = new IndexedFlatWorkload(count: 200, keyBase: 0);          // session 1 → checkpointed into the .bin, advances CheckpointLSN ≈ several hundred
        var window = new IndexedFlatWorkload(count: 8, keyBase: 100_000);    // session 2 → WAL-only, distinct unique-index keys

        // Session 1: seed, then CLEAN shutdown. Dispose runs a final checkpoint that persists CheckpointLSN over session-1's records.
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            seed.Register(dbe);
            dbe.InitializeArchetypes();
            using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
            seed.Execute(uow, shadow);
            uow.Flush();
        }

        // Session 2: reopen (clean — seed is in the .bin), commit the window (Immediate), hard-crash. The window exists ONLY in the WAL.
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            window.Register(dbe);
            dbe.InitializeArchetypes();
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                window.Execute(uow, shadow);
                uow.Flush();
            }

            shadow.CaptureValues(dbe);
            dbe.SimulateHardCrash();
        }

        // Session 3: reopen ⇒ WAL v2 recovery must replay the window over the checkpointed seed base.
        using var scope3 = _serviceProvider.CreateScope();
        var dbe3 = scope3.ServiceProvider.GetRequiredService<DatabaseEngine>();
        window.Register(dbe3);
        dbe3.InitializeArchetypes();

        TestContext.WriteLine(
            $"checkpointLsn(threshold)={dbe3.LastWalV2RecoveryCheckpointLsn} scanned={dbe3.LastWalV2RecoveryResult.RecordsScanned} "
            + $"applied={dbe3.LastWalV2RecoveryResult.RecordsApplied} maxLsn={dbe3.LastWalV2RecoveryResult.MaxLsn} txCommitted={dbe3.LastWalV2RecoveryResult.TxCommitted}");

        // ROOT-CAUSE LOCK (sharper than the oracle alone): the window's records must sit ABOVE the prior session's persisted CheckpointLSN, so recovery actually applies
        // them. Pre-fix the reopened writer restarts LSNs at 1 ⇒ maxLsn == 0 (no record above the threshold) and applied == 0 — exactly what these two asserts forbid.
        Assert.That(dbe3.LastWalV2RecoveryResult.MaxLsn, Is.GreaterThan(dbe3.LastWalV2RecoveryCheckpointLsn),
            "LOG-08: the post-reopen window's record LSNs must continue ABOVE the prior session's CheckpointLSN (else recovery skips them as already-consolidated).");
        Assert.That(dbe3.LastWalV2RecoveryResult.RecordsApplied, Is.GreaterThan(0),
            "LOG-08: recovery must apply the post-reopen window (a durably-acked commit was lost when the reopened writer's LSNs fell below CheckpointLSN).");

        RecoveryOracle.AssertPrimaryAxis(dbe3, shadow);
    }

    // ── Multi-value (AllowMultiple) index rebuild: duplicate keys must ALL reappear post-crash, and the version-history tail is cleared (RB-01). The unique-index
    //    tests above never exercised duplicate multi-value buffers (their A/C values were all distinct); this one packs ~15 entities per A key. ──
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("RB-01")]
    public void MultiValueIndex_DuplicateKeys_AllRebuiltAfterCrash()
    {
        RecoverWith(new MultiValueDupKeyWorkload(count: 120, groups: 8), (dbe, shadow) =>
        {
            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);

            var compDArch = shadow.Entities.Keys.First().ArchetypeId;
            using var tx = dbe.CreateQuickTransaction();
            var broad = RecoveryOracle.BroadScanEntityIds(tx, compDArch);

            // A is AllowMultiple with ~15 entities sharing each key (120 / 8 groups). The rebuilt multi-value index must return EVERY entity across all duplicate-key
            // HEAD buffers — a single-entity-per-key shortfall would mean the rebuild's AllowMultiple append path or the tail clear regressed.
            var indexedA = RecoveryOracle.IndexEntityIds<CompD, float>(dbe, tx, d => d.A, float.MinValue, float.MaxValue);
            Assert.That(
                indexedA,
                Is.EquivalentTo(broad),
                $"multi-value index A: rebuilt set ({indexedA.Count}) must equal broad-scan set ({broad.Count}) — every duplicate-key member reindexed (RB-01).");
        });
    }

    // ── PROOF GATE (the acceptance gate for retiring FPI on index pages): tear a CHECKPOINTED index node page on disk, DISABLE FPI repair, and prove recovery still
    //    yields a correct index — i.e. scrub+rebuild (RB-01) replaces FPI for derived index pages. A post-checkpoint WAL window keeps the crash path active so the
    //    index is cleared+rebuilt; the torn checkpointed page is therefore never parsed. With FPI repair on, this same tear would be silently repaired; with it off,
    //    only the rebuild can save it. ──
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("RB-01")]
    public void TornCheckpointedIndexPage_WithFpiRepairDisabled_RecoversViaRebuild()
    {
        var shadow = new RecoveryShadowModel();
        var below = new IndexedFlatWorkload(count: 600, keyBase: 0);   // checkpointed: a large index spanning many B+Tree node pages
        var window = new IndexedFlatWorkload(count: 8, keyBase: 5000);  // WAL window (distinct keys): keeps WAL files present ⇒ crash path ⇒ clear+rebuild

        int tornFilePage;
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            below.Register(dbe);
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                below.Execute(uow, shadow);
                uow.Flush();
            }

            // Consolidate the index into the data file with valid CRCs, then resolve a NON-ROOT index node page (the directory chunks 0-3 live on the root page and
            // must survive — only a pure-node page is torn).
            dbe.ForceCheckpoint();
            dbe.CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(10));
            tornFilePage = ResolveNonRootIndexNodeFilePage(dbe);
            Assert.That(tornFilePage, Is.GreaterThan(0), "test needs a checkpointed non-root index node page to tear (workload too small?)");

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                window.Execute(uow, shadow);
                uow.Flush();
            }

            shadow.CaptureValues(dbe);
            dbe.SimulateHardCrash();
        }

        // Tear the checkpointed index node page on disk: corrupt its data region, leaving the stored CRC ⇒ CRC mismatch (a torn write).
        TearDataFilePage(tornFilePage);

        // FPI is retired (increment D): there is no repair flag — recovery heals the torn checkpointed index page solely via the rebuild net (RB-01), natively.
        {
            using var scope2 = _serviceProvider.CreateScope();
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            below.Register(dbe);
            dbe.InitializeArchetypes(); // crash path: clears the (torn) index, applies the window, scrubs, rebuilds from final HEADs — never parsing the torn page

            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);

            var compDArch = shadow.Entities.Keys.First().ArchetypeId;
            using var tx = dbe.CreateQuickTransaction();
            var broad = RecoveryOracle.BroadScanEntityIds(tx, compDArch);
            var indexed = RecoveryOracle.IndexEntityIds<CompD, int>(dbe, tx, d => d.B, int.MinValue, int.MaxValue);
            Assert.That(
                indexed,
                Is.EquivalentTo(broad),
                $"torn-index proof gate (FPI retired): rebuilt index ({indexed.Count}) must equal broad-scan set ({broad.Count}). A shortfall means the rebuild did "
                + "NOT heal the torn checkpointed index page (RB-01).");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // #656 — the same RB-01 guarantees for the PER-ARCHETYPE index home
    //
    // Every index-axis test above runs over CompD: a flat, non-cluster, Versioned archetype whose indexes live on the ComponentTable. That is exactly the
    // population the defect did NOT affect. A cluster-backed archetype keeps its indexes on the ARCHETYPE, and on the crash path that home loaded the
    // persisted segment and skipped its rebuild entirely — so a torn cluster-index node page was neither loud-failed (RB-04 skips derived kinds) nor
    // rebuilt, but silently served.
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Metadata of the cluster-backed archetype under test, resolved the same way the rest of the fixture resolves its archetype.</summary>
    private static ArchetypeMetadata SvIndexedMeta => Archetype<SvIndexedArch>.Metadata;

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("RB-01")]
    public void ClusterIndexed_IndexAxis_MatchesBroadScan()
    {
        RecoverWith(new ClusterAllSvWorkload(40, DurabilityDiscipline.Commit), (dbe, shadow) =>
        {
            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);

            using var tx = dbe.CreateQuickTransaction();
            var broad = RecoveryOracle.BroadScanEntityIds(tx, shadow.Entities.Keys.First().ArchetypeId);
            Assert.That(broad, Is.Not.Empty, "sanity: the cluster entities must be recovered for the index-axis comparison to mean anything");

            var indexed = RecoveryOracle.ClusterIndexEntityIds(dbe, SvIndexedMeta, 0, 0, int.MinValue, int.MaxValue);
            Assert.That(
                indexed,
                Is.EquivalentTo(broad),
                $"cluster index axis: the archetype-homed SvIndexed.K tree ({indexed.Count}) must equal the broad-scan set ({broad.Count}). A shortfall means "
                + "the crash path trusted the persisted index instead of clearing and rebuilding it (RB-01).");
        });
    }

    // ── The cluster twin of MultiValueIndex_DuplicateKeys_AllRebuiltAfterCrash. An AllowMultiple leaf holds a VSBS buffer ROOT, not the location itself, so a
    //    rebuild that gets the unique case right can still keep one entity per key and drop the rest — invisible to a unique-key workload. ──
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("RB-01")]
    public void ClusterMultiValueIndex_DuplicateKeys_AllRebuiltAfterCrash()
    {
        RecoverWith(new ClusterMultiValueDupKeyWorkload(count: 120, groups: 8), (dbe, shadow) =>
        {
            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);

            using var tx = dbe.CreateQuickTransaction();
            var broad = RecoveryOracle.BroadScanEntityIds(tx, shadow.Entities.Keys.First().ArchetypeId);

            var indexed = RecoveryOracle.ClusterIndexEntityIds(dbe, Archetype<SvMultiIndexedArch>.Metadata, 0, 0, int.MinValue, int.MaxValue);
            Assert.That(
                indexed,
                Is.EquivalentTo(broad),
                $"cluster multi-value index G: rebuilt set ({indexed.Count}) must equal broad-scan set ({broad.Count}) — 15 entities share each of the 8 keys, "
                + "so a per-key shortfall means the rebuild's AllowMultiple append path is not exercised on this home (RB-01).");
        });
    }

    // ── The frontier case: half the entities are consolidated into the data file by a checkpoint, half live only in the WAL window. The cluster rebuild
    //    runs in Phase 5 over the cluster SoA, so it must see BOTH — the checkpointed slots and the ones the apply phase has just written. ──
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("RB-02")]
    public void ClusterIndexed_MidCheckpoint_IndexAxisHolds()
    {
        RecoverWithMidCheckpoint(
            new ClusterAllSvWorkload(30, DurabilityDiscipline.Commit),
            new ClusterAllSvWorkload(20, DurabilityDiscipline.Commit, keyBase: 1000),
            (dbe, shadow) =>
            {
                RecoveryOracle.AssertPrimaryAxis(dbe, shadow);

                using var tx = dbe.CreateQuickTransaction();
                var broad = RecoveryOracle.BroadScanEntityIds(tx, shadow.Entities.Keys.First().ArchetypeId);
                var indexed = RecoveryOracle.ClusterIndexEntityIds(dbe, SvIndexedMeta, 0, 0, int.MinValue, int.MaxValue);

                Assert.That(
                    indexed,
                    Is.EquivalentTo(broad),
                    $"cluster index axis across the checkpoint frontier: rebuilt set ({indexed.Count}) must equal broad-scan ({broad.Count}). Rebuilding at "
                    + "OPEN instead of in Phase 5 would index only the checkpointed half — the window's entities are applied later (RB-02).");
            });
    }

    // ── PROOF GATE for this home: tear a CHECKPOINTED per-archetype index node page on disk and prove recovery still yields a correct index. Before #656 the
    //    crash path loaded that segment and served it: RB-04 skips CRC-failed pages of a derived segment kind on the premise that RB-01 discarded and rebuilt
    //    them, and for this home that premise was simply false. ──
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("RB-01")]
    public void TornCheckpointedClusterIndexPage_RecoversViaRebuild()
    {
        var shadow = new RecoveryShadowModel();
        var below = new ClusterAllSvWorkload(3000, DurabilityDiscipline.Commit);                    // checkpointed: an index spanning many node pages
        var window = new ClusterAllSvWorkload(8, DurabilityDiscipline.Commit, keyBase: 900_000);    // WAL window: keeps the crash path active

        int tornFilePage;
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            below.Register(dbe);
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                below.Execute(uow, shadow);
                uow.Flush();
            }
            dbe.WriteTickFence(1);

            dbe.ForceCheckpoint();
            dbe.CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(10));
            tornFilePage = ResolveNonRootClusterIndexNodeFilePage(dbe);
            Assert.That(tornFilePage, Is.GreaterThan(0), "test needs a checkpointed non-root cluster-index node page to tear (workload too small?)");

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                window.Execute(uow, shadow);
                uow.Flush();
            }

            shadow.CaptureValues(dbe);
            dbe.SimulateHardCrash();
        }

        TearDataFilePage(tornFilePage);

        {
            using var scope2 = _serviceProvider.CreateScope();
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            below.Register(dbe);
            dbe.InitializeArchetypes();

            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);

            using var tx = dbe.CreateQuickTransaction();
            var broad = RecoveryOracle.BroadScanEntityIds(tx, shadow.Entities.Keys.First().ArchetypeId);
            var indexed = RecoveryOracle.ClusterIndexEntityIds(dbe, SvIndexedMeta, 0, 0, int.MinValue, int.MaxValue);
            Assert.That(
                indexed,
                Is.EquivalentTo(broad),
                $"torn cluster-index proof gate: rebuilt index ({indexed.Count}) must equal broad-scan set ({broad.Count}). A shortfall means the torn "
                + "checkpointed page was parsed and served rather than discarded and rebuilt (RB-01/RB-04).");
        }
    }

    /// <summary>
    /// The cluster twin of <see cref="ResolveNonRootIndexNodeFilePage"/>: an allocated node chunk of the PER-ARCHETYPE index segment living on a non-root
    /// segment page, so tearing it leaves the chunk-0 B+Tree directory intact. Returns 0 if the whole index fits on the root page.
    /// </summary>
    private static int ResolveNonRootClusterIndexNodeFilePage(DatabaseEngine dbe)
    {
        var seg = dbe._archetypeStates[SvIndexedMeta.ArchetypeId].ClusterState.IndexSegment;
        for (var chunkId = seg.ChunkCapacity - 1; chunkId >= BTreeBase<PersistentStore>.DirectoryChunkCount; chunkId--)
        {
            if (!seg.IsChunkAllocated(chunkId))
            {
                continue;
            }

            var (segPageIndex, _) = seg.GetChunkLocation(chunkId);
            if (segPageIndex >= 1)
            {
                return seg.Pages[segPageIndex];
            }
        }

        return 0;
    }

    /// <summary>Resolves the on-disk file page index of an allocated CompD secondary-index node chunk that lives on a NON-root segment page (so tearing it leaves the
    /// chunk-0 BTree directory intact). Returns 0 if none exists (index fits on the root page).</summary>
    /// <remarks>
    /// Reads <c>CompDArch</c>'s OWN index segment rather than <c>ComponentTable.DefaultIndexSegment</c> (#629). The shared segment has no allocated node chunks
    /// left to tear, so this returned 0 and the test failed on its premise assert — which at least failed loudly. Had the locator instead found some unrelated
    /// allocated page, the test would have torn the wrong thing and passed while proving nothing.
    /// </remarks>
    private static int ResolveNonRootIndexNodeFilePage(DatabaseEngine dbe)
    {
        var seg = dbe._archetypeStates[ArchetypeRegistry.GetMetadata<CompDArch>().ArchetypeId].ClusterState.IndexSegment;
        for (var chunkId = seg.ChunkCapacity - 1; chunkId >= BTreeBase<PersistentStore>.DirectoryChunkCount; chunkId--)
        {
            if (!seg.IsChunkAllocated(chunkId))
            {
                continue;
            }

            var (segPageIndex, _) = seg.GetChunkLocation(chunkId);
            if (segPageIndex >= 1)
            {
                return seg.Pages[segPageIndex]; // segment page index → on-disk file page index
            }
        }

        return 0;
    }

    // ── PROOF GATE (the acceptance gate for retiring FPI on the occupancy bitmap): tear a CHECKPOINTED occupancy L0 page on disk, DISABLE FPI repair, and prove
    //    recovery still yields a consistent allocator — the crash-path occupancy re-derive (CK-09) rebuilds the bitmap from the final segment ownership, replacing FPI
    //    for the derived occupancy structure. With FPI off, only the re-derive can heal the torn page; post-recovery the integrity check must report ZERO orphans and
    //    ZERO phantoms and every entity must survive. ──
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CK-09")]
    public void TornOccupancyPage_WithFpiDisabled_RecoversViaRederive()
    {
        var shadow = new RecoveryShadowModel();
        var below = new IndexedFlatWorkload(count: 600, keyBase: 0);   // checkpointed: enough segments/pages that the occupancy bitmap has meaningful set bits
        var window = new IndexedFlatWorkload(count: 8, keyBase: 5000);  // WAL window (distinct keys): keeps WAL files present ⇒ crash path

        int tornFilePage;
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            below.Register(dbe);
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                below.Execute(uow, shadow);
                uow.Flush();
            }

            // Consolidate so the occupancy L0 page is checkpointed with a valid CRC, then resolve it.
            dbe.ForceCheckpoint();
            dbe.CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(10));
            tornFilePage = ResolveOccupancyDataFilePage(dbe);
            Assert.That(tornFilePage, Is.GreaterThan(0), "test needs a checkpointed occupancy L0 data page to tear");

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                window.Execute(uow, shadow);
                uow.Flush();
            }

            shadow.CaptureValues(dbe);
            dbe.SimulateHardCrash();
        }

        // Tear the checkpointed occupancy L0 page: corrupt its bit words, leaving the stored CRC ⇒ CRC mismatch (a torn write).
        TearDataFilePage(tornFilePage);

        // FPI is retired (increment D): only the crash-path occupancy re-derive (CK-09) can heal the torn bitmap — there is no repair flag, it runs natively.
        {
            using var scope2 = _serviceProvider.CreateScope();
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            below.Register(dbe);
            dbe.InitializeArchetypes(); // crash path: re-derives occupancy from final segment ownership, never trusting the torn page

            RecoveryOracle.AssertPrimaryAxis(dbe, shadow);

            // GENUINENESS: the re-derive is the ONLY thing that can heal the torn occupancy bitmap — assert it actually corrected words (not a no-op).
            Assert.That(dbe.LastOpenOccupancyRederiveWordsChanged, Is.GreaterThan(0),
                "GENUINENESS: the crash-path occupancy re-derive must have corrected at least one L0 word (else the torn bitmap was never overwritten — CK-09).");

            var report = dbe.RunStorageIntegrityCheck();
            foreach (var issue in report.Issues)
            {
                TestContext.WriteLine($"ISSUE {issue.Kind}: {issue.Detail}");
            }

            Assert.That(report.OrphanPageCount, Is.EqualTo(0),
                $"occupancy re-derive (CK-09): {report.OrphanPageCount} orphan page(s) post-recovery — the torn occupancy bitmap was not healed to the true ownership.");
            Assert.That(report.PhantomPageCount, Is.EqualTo(0),
                $"occupancy re-derive (CK-09): {report.PhantomPageCount} phantom page(s) post-recovery — a live page lost its occupancy bit (double-allocation risk).");
        }
    }

    /// <summary>Resolves the on-disk file page index of a NON-root occupancy-bitmap data page (it holds the L0 occupancy words). Returns 0 if the occupancy segment
    /// has no non-root page (file too small to need one).</summary>
    private static int ResolveOccupancyDataFilePage(DatabaseEngine dbe)
    {
        foreach (var seg in dbe.MMF.RegisteredSegments)
        {
            if (seg.Kind != StorageSegmentKind.Occupancy)
            {
                continue;
            }

            var pages = seg.Pages;
            if (pages.Length >= 2)
            {
                return pages[1]; // first non-root occupancy page = the L0 data page
            }
        }

        return 0;
    }

    /// <summary>Corrupts a page's data region in the on-disk data file (after the engine has crashed + released the handle), leaving the page header's stored CRC —
    /// the recomputed CRC will mismatch, exactly a torn write of a checkpointed page.</summary>
    private void TearDataFilePage(int filePageIndex)
    {
        var dbPath = Path.Combine(_dbDir, $"{CurrentDatabaseName}.typhon", "data");
        using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var offset = (long)filePageIndex * PagedMMF.PageSize + PagedMMF.PageHeaderSize + 32; // skip the page header (keep the stored CRC), corrupt chunk data
        var garbage = new byte[256];
        for (var i = 0; i < garbage.Length; i++)
        {
            garbage[i] = (byte)(0xA5 ^ i);
        }

        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(garbage, 0, garbage.Length);
        fs.Flush(true);
    }

    // ── RB-04 (suspect primary pages heal or fail loudly): a torn checkpointed COMPONENT (primary) page still backing a live chunk is unhealable lost data — with
    //    FPI disabled, recovery must FAIL THE OPEN loudly, never serve corrupt data silently. (Contrast the index proof gate: a torn DERIVED page is rebuilt.) ──
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("RB-04")]
    public void TornReachablePrimaryPage_WithFpiDisabled_FailsOpenLoudly()
    {
        var shadow = new RecoveryShadowModel();
        var below = new IndexedFlatWorkload(count: 200, keyBase: 0);    // checkpointed component content — all single-revision, every chunk live/reachable
        var window = new IndexedFlatWorkload(count: 8, keyBase: 9000);  // WAL window keeps the crash path active

        int tornFilePage;
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            below.Register(dbe);
            dbe.InitializeArchetypes();
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                below.Execute(uow, shadow);
                uow.Flush();
            }

            dbe.ForceCheckpoint();
            dbe.CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(10));
            tornFilePage = ResolveLivePrimaryContentFilePage(dbe);
            Assert.That(tornFilePage, Is.GreaterThan(0), "need a checkpointed component content page backing a live chunk to tear");

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                window.Execute(uow, shadow);
                uow.Flush();
            }

            shadow.CaptureValues(dbe);
            dbe.SimulateHardCrash();
        }

        TearDataFilePage(tornFilePage); // tear a checkpointed primary page that still backs live data

        // FPI is retired (increment D): there is no on-load repair, so a torn reachable primary page MUST fail the open loudly (RB-04), not open over corrupt data.
        {
            using var scope2 = _serviceProvider.CreateScope();
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            below.Register(dbe);

            var ex = Assert.Catch(() => dbe.InitializeArchetypes());
            Assert.That(ex, Is.Not.Null,
                "RB-04: a torn reachable primary page must FAIL THE OPEN loudly (FPI retired — no repair), not open silently over corrupt data.");
            Assert.That(
                ex.ToString(),
                Does.Contain(tornFilePage.ToString()).Or.Contains("unhealable"),
                $"the loud failure must name the torn page ({tornFilePage}) / be diagnostic (RB-04). Got: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Suspect-page classification (the FPI-retirement safety boundary). After increment D deleted FPI, a torn checkpointed page is either HEALED by rebuild (derived
    //    kinds) or fails the open loudly (primary kinds, RB-04) — it is NEVER silently accepted. ResolveSuspectPrimaryPages keys that decision on IsDerivedSegmentKind,
    //    so this predicate is the only thing between a torn page and silent corruption. Assert its boundary directly: every primary kind — including the previously
    //    un-gated Cluster / Vsbs / ComponentCollection, all ChunkBasedSegment-backed so they take the same loud-fail path the TornReachablePrimaryPage gate proves
    //    end-to-end — must be NON-derived. (Deterministic complement to the tear gates; replaces a fragile dedicated VSBS/cluster tear gate.) ──
    [Test]
    public void SuspectPageClassification_PartitionsDerivedVsPrimary()
    {
        // Derived → healed by unconditional rebuild (RB-01 / CK-09), so a torn one is discarded + rebuilt.
        Assert.That(DatabaseEngine.IsDerivedSegmentKind(StorageSegmentKind.Index), Is.True, "Index pages are rebuilt (RB-01).");
        Assert.That(DatabaseEngine.IsDerivedSegmentKind(StorageSegmentKind.Spatial), Is.True, "Spatial pages are rebuilt.");
        Assert.That(DatabaseEngine.IsDerivedSegmentKind(StorageSegmentKind.Occupancy), Is.True, "Occupancy is re-derived (CK-09).");

        // Primary → heal-by-apply or loud-fail (RB-04), NEVER silently accepted. All are ChunkBasedSegment-backed (incl. Vsbs/ComponentCollection via
        // VariableSizedBufferSegmentBase.Segment and Cluster via ClusterSegment), so ResolveSuspectPrimaryPages loud-fails their torn pages uniformly.
        foreach (var primary in new[]
                 {
                     StorageSegmentKind.Component, StorageSegmentKind.Revision, StorageSegmentKind.Cluster, StorageSegmentKind.Vsbs,
                     StorageSegmentKind.StringTable, StorageSegmentKind.EntityMap, StorageSegmentKind.ComponentCollection, StorageSegmentKind.System,
                 })
        {
            Assert.That(DatabaseEngine.IsDerivedSegmentKind(primary), Is.False,
                $"{primary} pages must be PRIMARY (torn ⇒ loud-fail RB-04) — never derived/silently-accepted now that FPI is retired (increment D).");
        }
    }

    /// <summary>Resolves the on-disk file page index of an allocated CompD COMPONENT content chunk on a non-root segment page (a primary page backing live data).
    /// Returns 0 if none exists.</summary>
    private static int ResolveLivePrimaryContentFilePage(DatabaseEngine dbe)
    {
        var seg = dbe.GetComponentTable<CompD>().ComponentSegment;
        for (var chunkId = seg.ChunkCapacity - 1; chunkId >= 1; chunkId--) // chunk 0 is segment-reserved
        {
            if (!seg.IsChunkAllocated(chunkId))
            {
                continue;
            }

            var (segPage, _) = seg.GetChunkLocation(chunkId);
            if (segPage >= 1)
            {
                return seg.Pages[segPage];
            }
        }

        return 0;
    }

    // ── EntityMap rebuild proof gates (RB-01): the EntityMap is derived-on-crash. Tear a CHECKPOINTED EntityMap page on disk, DISABLE FPI, and prove recovery still
    //    recovers every entity — i.e. the crash-path rebuild (cluster occupancy walk / Versioned chain heads) replaces FPI for EntityMap pages. With FPI repair on, the
    //    tear would be silently repaired; with it off, only the rebuild can save the identities. Today (pre-rebuild) a torn EntityMap page is primary ⇒ RB-04 loud-fail,
    //    so each test goes from red (throws) to green (recovers) exactly as the rebuild lands. ──

    /// <summary>Resolves the on-disk file page index of an allocated NON-root EntityMap bucket/overflow chunk for the given archetype (chunk 0 is the meta/root and is
    /// kept). Returns 0 if the map fits on its root page.</summary>
    private static int ResolveEntityMapFilePage(DatabaseEngine dbe, ushort archetypeId)
    {
        var seg = dbe._stateByRouting[archetypeId].EntityMap.Segment;
        for (var chunkId = seg.ChunkCapacity - 1; chunkId >= 1; chunkId--) // chunk 0 = meta — never torn here (Open reads it eagerly)
        {
            if (!seg.IsChunkAllocated(chunkId))
            {
                continue;
            }

            var (segPage, _) = seg.GetChunkLocation(chunkId);
            if (segPage >= 1)
            {
                return seg.Pages[segPage];
            }
        }

        return 0;
    }

    /// <summary>
    /// Shared 3-session proof harness for the crash-path EntityMap rebuild. A PRIOR CLEAN SHUTDOWN is essential: the EntityMap / cluster segment SPIs are persisted only
    /// on dispose (PersistArchetypeState), so without it the next crash reopen sees SPI == 0 and never LOADS the persisted maps (it would fall back to a fresh allocation,
    /// making the tear a no-op). Session 1 seeds + cleanly shuts down (SPIs &gt; 0); session 2 reopens (loads the persisted maps), resolves a non-root EntityMap page,
    /// commits a throwaway window (WAL files ⇒ crash path), captures the shadow over the SEED only, and hard-crashes; we tear that page with FPI disabled; session 3
    /// reopens and must re-derive the torn EntityMap from authoritative data (cluster occupancy / Versioned chain heads) so the seed recovers.
    /// <para>
    /// The window is recorded in the shadow and verified alongside the seed: it puts WAL files on disk so session 3 takes the crash path, AND — since the post-reopen
    /// window-loss defect (LOG-08) is fixed — its flat-Versioned entities recover by WAL apply into the freshly re-derived EntityMap, so the rebuild must coexist with the
    /// applied window (the EntityMap is cleared+rebuilt from the persisted chain heads BEFORE apply, then apply inserts the window entities).
    /// </para>
    /// </summary>
    private void RecoverTornEntityMapAfterPriorShutdown(IRecoveryWorkload seed, IRecoveryWorkload window, Action<DatabaseEngine, RecoveryShadowModel> assertRecovered)
    {
        var shadow = new RecoveryShadowModel();

        // Session 1: seed, then CLEAN shutdown ⇒ PersistArchetypeState writes EntityMapSPI / ClusterSegmentSPI > 0 so a later crash reopen actually loads them.
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            seed.Register(dbe);
            window.Register(dbe);
            dbe.InitializeArchetypes();
            using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
            seed.Execute(uow, shadow);
            uow.Flush();
            // scope dispose ⇒ clean shutdown (no SimulateHardCrash)
        }

        int tornFilePage;
        // Session 2: reopen (loads the persisted maps), resolve a non-root EntityMap page, commit a throwaway window (creates WAL files ⇒ crash path), capture, crash.
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            seed.Register(dbe);
            window.Register(dbe);
            dbe.InitializeArchetypes();
            tornFilePage = ResolveEntityMapFilePage(dbe, shadow.Entities.Keys.First().ArchetypeId);
            Assert.That(tornFilePage, Is.GreaterThan(0), "need a non-root EntityMap page to tear (seed too small?)");

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                window.Execute(uow, shadow); // recorded + verified: with LOG-08 fixed the post-reopen window survives the crash and must coexist with the EntityMap rebuild
                uow.Flush();
            }

            shadow.CaptureValues(dbe);
            dbe.SimulateHardCrash();
        }

        TearDataFilePage(tornFilePage);

        // FPI is retired (increment D): only the crash-path EntityMap rebuild can save the torn identities — it runs natively, no repair flag.
        {
            using var scope3 = _serviceProvider.CreateScope();
            var dbe = scope3.ServiceProvider.GetRequiredService<DatabaseEngine>();
            seed.Register(dbe);
            window.Register(dbe);
            dbe.InitializeArchetypes(); // crash path: discards the torn EntityMap, re-derives it from cluster occupancy / chains

            Assert.That(dbe.LastOpenCrashEntityMapRebuildCount, Is.GreaterThan(0),
                "GENUINENESS: the crash-path EntityMap rebuild must actually run on this hard-crash reopen (else the torn page was never loaded and the test proves nothing).");

            assertRecovered(dbe, shadow);
        }
    }

    // Cluster archetype: the EntityMap is re-derived purely from cluster data (OccupancyBits + EntityKeys[N] + EnabledBits[C]) — the design's "EntityKey not recoverable"
    // residual is refuted. (Cluster DATA recovery itself needs the prior clean shutdown to make ClusterSegmentSPI durable; crash-durable cluster data without a clean
    // shutdown is a separate P2 concern.)
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("RB-01")]
    public void TornClusterEntityMapPage_AfterPriorShutdown_RecoversViaRebuild()
        => RecoverTornEntityMapAfterPriorShutdown(
            new ClusterAllSvWorkload(count: 600),
            new IndexedFlatWorkload(count: 8, keyBase: 9000),
            RecoveryOracle.AssertPrimaryAxis);

    // Flat-Versioned archetype: the EntityMap is re-derived from the Versioned chain heads, forced on the crash gate instead of trusting the possibly-torn / stale
    // persisted map. The broad-scan equality also catches a chain-head rebuild that dropped entities.
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("RB-01")]
    public void TornFlatVersionedEntityMapPage_AfterPriorShutdown_RecoversViaRebuild()
        => RecoverTornEntityMapAfterPriorShutdown(
            new IndexedFlatWorkload(count: 600, keyBase: 0),
            new IndexedFlatWorkload(count: 8, keyBase: 9000),
            (dbe, shadow) =>
            {
                RecoveryOracle.AssertPrimaryAxis(dbe, shadow);
                var compDArch = shadow.Entities.Keys.First().ArchetypeId;
                using var tx = dbe.CreateQuickTransaction();
                var broad = RecoveryOracle.BroadScanEntityIds(tx, compDArch);
                Assert.That(broad.Count, Is.EqualTo(shadow.Entities.Count),
                    $"EntityMap rebuild: broad scan ({broad.Count}) must find every recovered entity ({shadow.Entities.Count}); a shortfall means the torn EntityMap page "
                    + "was not re-derived from the chain heads (RB-01).");
            });

    // GENUINENESS NOTE: with the crash-path rebuild disabled (DatabaseEngine.DisableEntityMapRebuildForTest), a torn (FPI-off) EntityMap page is trusted-as-loaded and its
    // garbage hash-directory pointers are dereferenced into a HARD process crash — before any RB-04 loud-fail can fire. That confirms the rebuild is load-bearing (it is
    // the only thing that recovers a torn EntityMap), and is precisely why a pointer-bearing derived structure must be re-derived, not trusted-and-healed. It is verified
    // manually rather than as a committed test, since it crashes the test host. The committed proof above uses the `LastOpenCrashEntityMapRebuildCount > 0` + FPI-off
    // signal: FPI cannot have repaired the torn page, the rebuild ran, and the seed recovered ⇒ the rebuild recovered it.

    // The rebuildability classifier draws the residual boundary: cluster + flat-Versioned EntityMaps heal on crash; the rare non-cluster-with-SV-slot does not and keeps
    // the RB-04 loud-fail (never silent-heal to a lossy map).
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("RB-04")]
    public void EntityMapRebuildability_Classifier_ClassifiesByStorageMode()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponentFromAccessor<SvIndexed>();        // all-SV indexed ⇒ cluster-eligible
        dbe.RegisterComponentFromAccessor<CompD>();            // all-Versioned ⇒ flat
        dbe.RegisterComponentFromAccessor<SvForFlat>();        // {SV + Transient-indexed} ⇒ non-cluster with an SV slot
        dbe.RegisterComponentFromAccessor<TransientIndexed>();
        dbe.InitializeArchetypes();

        Assert.That(dbe.IsEntityMapRebuildable(Archetype<SvIndexedArch>.Metadata), Is.True,
            "cluster archetype → EntityMap fully re-derivable from cluster occupancy + EntityKeys[N]");
        Assert.That(dbe.IsEntityMapRebuildable(Archetype<CompDArch>.Metadata), Is.True,
            "flat all-Versioned → EntityMap re-derivable from chain heads");
        // The RB-01/RB-04 "non-rebuildable" residual was exactly one shape: {SV slot + Transient-indexed slot}, which the old cluster-eligibility rule forced
        // onto the flat path, leaving its SV locations with no persisted source. #655 admits that shape to cluster storage, so the class no longer exists —
        // this archetype is now rebuildable like every other, and the rules no longer carry the carve-out.
        Assert.That(dbe.IsEntityMapRebuildable(Archetype<FlatSvArch>.Metadata), Is.True,
            "{SV + Transient-indexed} is cluster-backed since #655 → cluster occupancy + EntityKeys[N] make its EntityMap re-derivable like any other");
    }
}
