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
// [Seeded] attaches the repro line to a failure; Category("Seeded") is what the nightly's --filter selects. Two markers because an attribute cannot be
// selected by a vstest filter and a category cannot run code — and #703's taxonomy is explicit that a marker only counts if some tier actually runs it.
// "Seeded" is NOT a gate-excluding tier: these tests run in the PR gate too, at the fixed default seed.
[Category("Seeded")]
[Seeded]
internal sealed class WalCrashSweepTests
{
    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;

    private static readonly string[] NonClusterWorkloads = ["SingleTxSpawn", "LifecycleChurn", "IndexedFlat", "MultiValueDupKey"];

    // Checkpoint page-write boundaries.
    //
    // #705 T3: this comment used to read "boundaries beyond a cycle's write count let the cycle complete — recovery still holds, so the sweep is robust
    // without probing the exact per-workload write count". That was backwards. Such a boundary injects NO crash: the case runs a clean checkpoint, asserts
    // recovery over a consolidated base, and passes under a name claiming it crashed at write N. The sweep was not robust, it was partly inert, and the
    // inertness was invisible because nothing asserted ChaosPageIO.HasCrashed. Both are now fixed: every boundary case requires the crash to have fired, and
    // PageAxis_EveryBoundary_IsWithinTheObservedWriteCount pins the whole set against the measured write count so the requirement stays satisfiable.
    //
    // #704 T6: the fixed five are a FLOOR, not the set. This file's own :15 comment claims the sweep crashes "at EVERY page-write boundary"; it crashed at
    // five, and had done since it was written, because the array is a literal. Two seeded extras are appended so the boundary explored grows with CI-hours
    // instead of being frozen on the day the array was typed. With TYPHON_TEST_SEED unset the seed is constant, so the gate still runs one fixed set — the
    // nightly is what varies it.
    //
    // #705 T3: the seeded extras were drawn from [1, 17) — and MEASURED, a checkpoint cycle performs 29-79 page writes (SingleTxSpawn 29, MixedDiscipline 29,
    // IndexedFlat 49, MultiValueDupKey 49, LifecycleChurn 79; see PageAxis_EveryBoundary_IsWithinTheObservedWriteCount, which prints them). So no seed could
    // ever reach past write 16: the entire tail of every cycle had never been crashed at, and the seeding multiplied ~18 % of the space. The draw now spans
    // BoundaryCeiling, which the probe test pins BELOW the shortest observed cycle — so widening the range cannot make a boundary inert, and if the cycle ever
    // gets shorter the probe fails first, naming the number, instead of a nightly failing at an unlucky seed far from the cause.
    private static readonly int[] CrashBoundaries = BuildCrashBoundaries();

    /// <summary>
    /// Upper bound (exclusive) for both the seeded gate extras and the exhaustive nightly sweep. Held below the shortest MEASURED cycle (27 writes) so every
    /// boundary it admits is guaranteed to land inside a cycle and actually inject a crash. Enforced, not assumed — see
    /// <see cref="PageAxis_EveryBoundary_IsWithinTheObservedWriteCount"/>.
    /// <para>
    /// Was 80, against a shortest cycle of 87. The cycles got SHORTER — 87 → 29 on the two lightest workloads — because the page cache no longer keeps clean
    /// pages permanently dirty: leaked mutator marks used to pin them, so every cycle rewrote pages whose bytes were already on disk (#824). The writes that
    /// disappeared were redundant ones, and the probe below is exactly the guard that noticed, which is what it is for.
    /// </para>
    /// <para>
    /// Then 29 → 27, for the same reason a second time. #839 stopped a cluster-backed SingleVersion or Transient spawn allocating a content chunk it could
    /// never address again, so those spawns no longer dirty a ComponentSegment page for the checkpoint to write out. Measured 2026-08-17 across the five
    /// page-axis workloads: MixedDiscipline 27 (was 29), SingleTxSpawn 29, IndexedFlat 49, MultiValueDupKey 49, LifecycleChurn 79. Only MixedDiscipline moved
    /// — it is the workload that spawns non-Versioned components — and the two writes it lost were writes of a chunk nothing would ever read.
    /// </para>
    /// </summary>
    private const int BoundaryCeiling = 27;

    private static int[] BuildCrashBoundaries()
    {
        var floor = new[] { 1, 2, 3, 5, 8 };

        // A context-free derivation, not TestSeed.Random(): this runs in a static field initializer, where TestContext may name the fixture, a random test, or
        // nothing at all. Keying on the fixture name instead keeps the boundary set identical for every case in the run.
        var rand = new Random(TestSeed.Derive(TestSeed.RunSeed, nameof(WalCrashSweepTests), "crash-boundaries"));
        var extras = new SortedSet<int>();
        while (extras.Count < 2)
        {
            var n = rand.Next(1, BoundaryCeiling);
            if (System.Array.IndexOf(floor, n) < 0)
            {
                extras.Add(n);
            }
        }

        var all = new List<int>(floor);
        all.AddRange(extras);
        return [.. all];
    }

    private static IRecoveryWorkload MakeWorkload(string name) => name switch
    {
        "SingleTxSpawn" => new SingleTxSpawnWorkload(10),
        // The seed was the literal 1234, which pinned this "seeded-random" workload to one churn sequence forever. It now derives from the run seed, so the
        // gate keeps a fixed sequence and a seeded nightly explores others — replayable from the printed seed alone.
        "LifecycleChurn" => new LifecycleChurnWorkload(TestSeed.For("lifecycle-churn"), 24),
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

    /// <summary>Every workload with page-axis cases — the four non-cluster ones plus MixedDiscipline, which drives its own boundary fan-out below.</summary>
    private static readonly string[] AllPageAxisWorkloads = [.. NonClusterWorkloads, "MixedDiscipline"];

    private static IEnumerable<TestCaseData> BoundaryProbeCases()
    {
        foreach (var w in AllPageAxisWorkloads)
        {
            yield return new TestCaseData(w).SetName($"BoundaryProbe_{w}");
        }
    }

    /// <summary>
    /// Fail a boundary case that injected NO crash (#705 T3).
    /// </summary>
    /// <remarks>
    /// A boundary past the cycle's write count runs a COMPLETE checkpoint and then asserts recovery over a consolidated base. That is a real assertion — it is
    /// simply not the one the case NAME claims, and the case can no longer fail for the reason it exists. It is the W1 class from #702 §3: a test that exists
    /// and cannot fail. <see cref="ChaosPageIO.HasCrashed"/> has been on the injector since it was written; nothing ever asserted it, and this file's own
    /// comment described the silent pass as robustness.
    /// </remarks>
    private static void AssertCrashWasInjected(ChaosPageIO chaos, string what, int crashAtWrite)
    {
        Assert.That(
            chaos.HasCrashed,
            Is.True,
            $"{what}: boundary N={crashAtWrite} injected NO crash — the cycle completed after {chaos.TotalWriteCount} page write(s), so this case exercised a "
            + $"clean checkpoint rather than a crash at that boundary. See {nameof(PageAxis_EveryBoundary_IsWithinTheObservedWriteCount)}, which pins the "
            + "boundary set against the observed write count.");
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

    /// <summary>
    /// Every boundary in <see cref="CrashBoundaries"/> must fall WITHIN the checkpoint cycle's real page-write count, for every workload (#705 T3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the guard that lets <see cref="AssertCrashWasInjected"/> be an unconditional failure rather than a judgement call. Without it, tightening the
    /// boundary cases would just relocate the problem: a boundary drifting out of range would turn a whole workload red with no indication of why, and the
    /// obvious "fix" would be to relax the assertion back to silence.
    /// </para>
    /// <para>
    /// It matters most for the SEEDED extras. #704 appended two boundaries drawn from the run seed; if the cycle ever gets shorter than the draw range, a
    /// nightly at an unlucky seed would fail somewhere far from the cause. This test fails FIRST and names the observed count, so the draw range can be
    /// corrected instead of guessed at.
    /// </para>
    /// <para>
    /// The probe runs a recording-only <see cref="ChaosPageIO"/> (no crash configured) over one complete cycle. That consumes the dirty set, which is exactly
    /// why the measurement cannot be folded into the boundary cases themselves — a probe and a crash cannot share a checkpoint.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    [TestCaseSource(nameof(BoundaryProbeCases))]
    public void PageAxis_EveryBoundary_IsWithinTheObservedWriteCount(string workloadName)
    {
        var workload = MakeWorkload(workloadName);
        var shadow = new RecoveryShadowModel();

        using var scope = _serviceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        workload.Register(dbe);
        dbe.InitializeArchetypes();
        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            workload.Execute(uow, shadow);
            uow.Flush();
        }

        var chaos = new ChaosPageIO();
        chaos.WireTo(mmf);
        dbe.CheckpointManager.RunCheckpointCycle(dbe.WalManager.DurableLsn);
        chaos.Unwire(mmf);

        var observed = chaos.TotalWriteCount;
        TestContext.WriteLine($"{workloadName}: checkpoint cycle performed {observed} page write(s); boundaries = [{string.Join(", ", CrashBoundaries)}]");

        Assert.That(chaos.HasCrashed, Is.False, "the probe configures no crash — a crash here means the injector fired without being asked to");
        Assert.That(
            observed,
            Is.GreaterThanOrEqualTo(BoundaryCeiling),
            $"{workloadName}: the checkpoint cycle performs only {observed} page write(s), but {nameof(BoundaryCeiling)} admits boundaries up to "
            + $"{BoundaryCeiling - 1} — for the seeded gate extras AND the exhaustive nightly sweep. Every boundary above {observed} runs a COMPLETE cycle and "
            + $"tests a clean checkpoint under a crash-at-N name. Lower {nameof(BoundaryCeiling)} to match, and record the new measurement here.");
    }

    /// <summary>
    /// The anti-false-green companion to <see cref="AssertCrashWasInjected"/>: drive a boundary deliberately past the end of the cycle and require the check to
    /// REJECT it.
    /// </summary>
    /// <remarks>
    /// Without this, the genuineness check is itself unverified — and it is guarding against exactly the failure mode of being green for the wrong reason, so
    /// leaving it unexercised would be the same mistake one level up. §5.5: a verifier that has never rejected anything is not evidence. This is also the only
    /// place the out-of-range behaviour is exercised on purpose, which is why the message assertion pins the wording the real cases would emit.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    public void AssertCrashWasInjected_RejectsABoundaryPastTheEndOfTheCycle()
    {
        var workload = MakeWorkload("SingleTxSpawn");
        var shadow = new RecoveryShadowModel();

        using var scope = _serviceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        workload.Register(dbe);
        dbe.InitializeArchetypes();
        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            workload.Execute(uow, shadow);
            uow.Flush();
        }

        // A boundary far past any cycle: the checkpoint completes normally and the injector never fires. Pre-#705 this was a PASSING case.
        const int wayPastTheEnd = 100_000;
        var chaos = new ChaosPageIO();
        chaos.WireTo(mmf);
        chaos.SetCrashAtPageWrite(wayPastTheEnd);
        dbe.CheckpointManager.RunCheckpointCycle(dbe.WalManager.DurableLsn);
        chaos.Unwire(mmf);

        Assert.That(chaos.HasCrashed, Is.False, "precondition: a boundary past the cycle must leave the injector unfired, or this test proves nothing");
        var ex = Assert.Throws<AssertionException>(() => AssertCrashWasInjected(chaos, "SingleTxSpawn", wayPastTheEnd));
        Assert.That(ex.Message, Does.Contain("injected NO crash"), "the rejection must say why, not merely fail");
    }

    /// <summary>Every workload × every boundary in <c>1..BoundaryCeiling-1</c> — the exhaustive sweep, ~400 cases, nightly only.</summary>
    private static IEnumerable<TestCaseData> ExhaustivePageAxisCases()
    {
        foreach (var w in AllPageAxisWorkloads)
        {
            for (var n = 1; n < BoundaryCeiling; n++)
            {
                yield return new TestCaseData(w, n).SetName($"Exhaustive_PageAxis_{w}_N{n}");
            }
        }
    }

    /// <summary>
    /// The exhaustive boundary sweep #705 asks for: crash the checkpoint at EVERY write from 1 to <see cref="BoundaryCeiling"/>, for every workload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate keeps the seeded floor; this cell is where the claim in this file's own summary — "crash the checkpoint at every page-write boundary" —
    /// becomes true. It is <c>[Explicit] + [Category("Nightly")]</c> because ~400 crash-and-recover cycles is minutes, not a PR gate.
    /// </para>
    /// <para>
    /// <b>One case per boundary, not a loop.</b> A loop would stop at the first failing boundary and hide every later one, and #704's trap 5 is the sharper
    /// version of the same lesson: <c>Assert.Multiple</c> records into the current result independently of the exception, so a caught failure leaks into the
    /// next iteration. Independent cases also mean the failure NAME carries the boundary.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    [TestCaseSource(nameof(ExhaustivePageAxisCases))]
    [Explicit("Exhaustive boundary sweep — ~400 crash-and-recover cycles; the gate runs the seeded floor instead")]
    [Category("Nightly")]
    [VerifiesRule("CK-03")]
    public void Exhaustive_PageAxis_CheckpointCrashAtBoundary_OracleHolds(string workloadName, int crashAtWrite)
        => PageAxis_CheckpointCrashAtBoundary_OracleHolds(workloadName, crashAtWrite);

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
            AssertCrashWasInjected(chaos, workloadName, crashAtWrite);

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
            AssertCrashWasInjected(chaos, "MixedDiscipline", crashAtWrite);

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
