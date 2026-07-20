using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
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

    /// <summary>Resolves the on-disk file page index of an allocated CompD secondary-index node chunk that lives on a NON-root segment page (so tearing it leaves the
    /// chunk-0 BTree directory intact). Returns 0 if none exists (index fits on the root page).</summary>
    private static int ResolveNonRootIndexNodeFilePage(DatabaseEngine dbe)
    {
        var seg = dbe.GetComponentTable<CompD>().DefaultIndexSegment;
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
        Assert.That(dbe.IsEntityMapRebuildable(Archetype<FlatSvArch>.Metadata), Is.False,
            "non-cluster archetype with an SV slot (forced flat by a Transient-indexed slot) → not rebuildable → torn EntityMap loud-fails (RB-04 residual)");
    }
}
