using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// The "One True Crash Test" (P0.3 / AC-3) — the program's north star. An entity is committed with <see cref="DurabilityMode.Immediate"/> (its records fsynced to
/// the WAL), then the engine is hard-crashed via <see cref="DatabaseEngine.SimulateHardCrash"/> (a power cut: the managed page cache is discarded with no checkpoint
/// and no <c>PersistEngineState</c>, so the committed data exists ONLY in the WAL). On reopen the entity must be recovered via WAL replay.
/// </summary>
/// <remarks>
/// <b>GREEN as of #395 P1.2.</b> Reopen replays the WAL through <see cref="DatabaseEngine"/>'s <c>RunWalV2Recovery</c> (the <c>RecoveryDriver</c>), which scans the
/// retained v2 segments, determines commit fate from TxCommit markers (LOG-04), and rebuilds each committed entity — its <c>EntityRecord</c> AND its spawn-init
/// component values (the Versioned revision chain is reconstructed from the Slot records). The assertions below are a differential oracle: every recovered
/// <c>CompA</c> field must equal the live-committed value byte-for-byte, not merely <c>IsAlive</c>. A prerequisite fix landed alongside: <see cref="WalSegmentReader"/>
/// now traverses the zero-padding gaps between O_DIRECT-aligned drain blocks (WR-02) — without it the reader stopped at the first padded FPI frame and never reached
/// the commit records (durable on disk all along). This test is the program's standing regression guard for crash survival; it is no longer quarantined.
/// </remarks>
[TestFixture]
internal sealed class TrueCrashE2ETests
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
            const string prefix = "Tct_";
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
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(TrueCrashE2ETests));
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

        var testRoot = Directory.GetParent(_dbDir)?.FullName; // the per-test "<root>/Tct_<name>" dir (parent of /db and /wal)
        try
        {
            if (testRoot != null && Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void ImmediateCommit_SurvivesHardCrash()
    {
        const int count = 10;
        var entityIds = new EntityId[count];

        // Phase 1: commit entities with Immediate durability (each fsynced to the WAL), then hard-crash without persisting the data file.
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1, i, i);
                    entityIds[i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                uow.Flush();
            }

            // Power cut: discard the managed page cache (uncheckpointed dirty pages) with no PersistEngineState / clean-shutdown marker. The committed entities now
            // live ONLY in the fsynced WAL — survival depends entirely on WAL replay at reopen.
            dbe.SimulateHardCrash();
        }

        // Phase 2: reopen the same directory and require every committed entity to be recovered.
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < count; i++)
            {
                Assert.That(tx.IsAlive(entityIds[i]), Is.True,
                    $"Immediate-committed entity {i} must survive a hard crash via WAL replay (RecoveryDriver, #395/P1.2)");

                // The entity must also recover its committed COMPONENT VALUE, not just its existence: recovery rebuilds the
                // Versioned revision chain from the Slot record (CompA = i+1, i, i as spawned). This is the differential oracle —
                // recovered value must equal the live-committed value byte-for-byte.
                var comp = tx.Open(entityIds[i]).Read(CompAArch.A);
                Assert.That(comp.A, Is.EqualTo(i + 1), $"entity {i}: CompA.A must survive the crash");
                Assert.That(comp.B, Is.EqualTo((float)i), $"entity {i}: CompA.B must survive the crash");
                Assert.That(comp.C, Is.EqualTo((double)i), $"entity {i}: CompA.C must survive the crash");
            }
        }
    }

    /// <summary>
    /// Recovery must honour committed deletes, not just spawns: an entity spawned then destroyed (each in its own committed
    /// Immediate transaction) before a hard crash must stay DEAD after reopen — never resurrected. Survivors keep their values.
    /// Both the spawn and the destroy are in the recovery window (no checkpoint), so the driver sees the spawn+destroy pair and
    /// declines to re-insert the entity (mirrors the live FinalizeSpawns skip), exactly as <c>RecoveryDriver</c> Phase 3 does.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void SpawnThenDestroy_DeletedEntitiesStayDeadAfterCrash()
    {
        const int count = 10;
        var entityIds = new EntityId[count];

        // Phase 1: spawn all, then destroy the even-indexed half in separate committed transactions, then hard-crash.
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1, i, i);
                    entityIds[i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                for (int i = 0; i < count; i += 2)
                {
                    using var tx = uow.CreateTransaction();
                    tx.Destroy(entityIds[i]);
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        // Phase 2: reopen — even indices must be dead (deletes honoured), odd indices alive with their committed values intact.
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < count; i++)
            {
                if (i % 2 == 0)
                {
                    Assert.That(tx.IsAlive(entityIds[i]), Is.False, $"destroyed entity {i} must NOT be resurrected by recovery");
                }
                else
                {
                    Assert.That(tx.IsAlive(entityIds[i]), Is.True, $"surviving entity {i} must remain alive after the crash");
                    var comp = tx.Open(entityIds[i]).Read(CompAArch.A);
                    Assert.That(comp.A, Is.EqualTo(i + 1), $"surviving entity {i}: CompA.A must be intact");
                    Assert.That(comp.C, Is.EqualTo((double)i), $"surviving entity {i}: CompA.C must be intact");
                }
            }
        }
    }

    /// <summary>
    /// Recovery must restore the committed enabled-bits, including post-spawn changes: a component disabled in a transaction
    /// after spawn must read back disabled after a hard crash. The driver folds the absolute SetEnabledBits record into the
    /// rebuilt EntityRecord (last write wins), so the recovered bits match the last committed state.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void DisableComponentAfterSpawn_EnabledBitsSurviveCrash()
    {
        const int count = 10;
        var entityIds = new EntityId[count];

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1, i, i);
                    entityIds[i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                // Disable CompA on the even-indexed entities in a later committed transaction (absolute enabled-bits change).
                for (int i = 0; i < count; i += 2)
                {
                    using var tx = uow.CreateTransaction();
                    tx.OpenMut(entityIds[i]).Disable(CompAArch.A);
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < count; i++)
            {
                Assert.That(tx.IsAlive(entityIds[i]), Is.True, $"entity {i} must remain alive");
                var enabled = tx.Open(entityIds[i]).IsEnabled(CompAArch.A);
                Assert.That(enabled, Is.EqualTo(i % 2 != 0),
                    $"entity {i}: CompA enabled-bit must reflect the last committed state after the crash (even=disabled, odd=enabled)");
            }
        }
    }

    /// <summary>
    /// Recovery must restore the LATEST committed component value, not the spawn-time one: a component written again after spawn
    /// (a second committed transaction) must read back its updated value after a hard crash. The driver collapses a component's
    /// in-window history to its last write (carrying that write's TSN), so the rebuilt revision chain holds the newest value.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void UpdateComponentAfterSpawn_LatestValueSurvivesCrash()
    {
        const int count = 10;
        var entityIds = new EntityId[count];

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1, i, i); // V0 (spawn-init)
                    entityIds[i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    tx.OpenMut(entityIds[i]).Write(CompAArch.A) = new CompA(i + 1000, i + 0.5f, i + 0.25); // V1 (post-spawn update)
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < count; i++)
            {
                Assert.That(tx.IsAlive(entityIds[i]), Is.True, $"entity {i} must remain alive");
                var comp = tx.Open(entityIds[i]).Read(CompAArch.A);
                Assert.That(comp.A, Is.EqualTo(i + 1000), $"entity {i}: must recover the UPDATED CompA.A, not the spawn value");
                Assert.That(comp.B, Is.EqualTo(i + 0.5f), $"entity {i}: must recover the updated CompA.B");
                Assert.That(comp.C, Is.EqualTo(i + 0.25), $"entity {i}: must recover the updated CompA.C");
            }
        }
    }

    /// <summary>
    /// Recovery must honour a delete of a CHECKPOINTED entity — the base-entity case. The spawn is checkpointed into the data
    /// file (so it falls below the recovery window); only the later Destroy lives in the WAL window. Recovery has no Spawn record
    /// for these entities, so it tombstones the already-loaded EntityMap record in place (DiedTSN). After a crash the deleted
    /// entities must stay dead — never resurrected from the checkpointed base.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void DestroyCheckpointedEntity_StaysDeadAfterCrash()
    {
        const int count = 10;
        var entityIds = new EntityId[count];

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            long spawnHighLsn;
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1, i, i);
                    entityIds[i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                uow.Flush();
                spawnHighLsn = dbe.DurabilityLog.LastAppendedLsn;
            }

            // Persist the spawns to the data file and advance the checkpoint frontier past them, so the spawns are BELOW the
            // recovery window — only the destroys (below) remain in it. This is what makes the test exercise the base-entity path.
            // ForceCheckpoint is asynchronous (signals the checkpoint thread); WaitForCheckpoint blocks until the cycle completes.
            dbe.ForceCheckpoint();
            Assert.That(dbe.CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(5)), Is.True, "checkpoint cycle must complete");
            var checkpointLsn = dbe.CheckpointManager.CheckpointLsn;
            Assert.That(checkpointLsn, Is.GreaterThanOrEqualTo(spawnHighLsn),
                "the checkpoint must advance past the spawns so they fall below the recovery window (base-entity scenario)");

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i += 2)
                {
                    using var tx = uow.CreateTransaction();
                    tx.Destroy(entityIds[i]);
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < count; i++)
            {
                if (i % 2 == 0)
                {
                    Assert.That(tx.IsAlive(entityIds[i]), Is.False,
                        $"checkpointed entity {i} deleted before the crash must NOT be resurrected by recovery");
                }
                else
                {
                    Assert.That(tx.IsAlive(entityIds[i]), Is.True, $"checkpointed survivor {i} must remain alive");
                    Assert.That(tx.Open(entityIds[i]).Read(CompAArch.A).A, Is.EqualTo(i + 1), $"survivor {i}: value intact");
                }
            }
        }
    }

    /// <summary>
    /// The Phase-6 seal must CONSOLIDATE recovered state into the data file, not leave it re-derivable from the WAL. After a crash
    /// the first reopen replays the WAL and seals (a checkpoint that writes the recovered pages + advances CheckpointLSN). We then
    /// hard-crash again AND delete every WAL file: a second reopen has no WAL to replay, so the entities can only survive if the
    /// seal truly persisted them to the data file. This is what makes recovered state durable across a SECOND crash and lets the
    /// replayed WAL recycle.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void RecoveredState_IsConsolidatedToDataFile_BySeal()
    {
        const int count = 10;
        var entityIds = new EntityId[count];

        // Phase 1: commit with Immediate durability, then hard-crash (data lives only in the WAL).
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1, i, i);
                    entityIds[i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        // Phase 2: reopen — recovery replays the WAL and the Phase-6 seal consolidates it to the data file. Then hard-crash AGAIN
        // (discard the cache with no clean shutdown), so nothing but already-persisted data-file content can survive.
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var tx = dbe.CreateQuickTransaction())
            {
                Assert.That(tx.IsAlive(entityIds[0]), Is.True, "sanity: recovery restored the entity before the seal test");
            }

            dbe.SimulateHardCrash();
        }

        // Delete every WAL file: the only remaining source of truth is the data file the seal wrote.
        foreach (var wal in Directory.GetFiles(_walDir, "*.wal"))
        {
            File.Delete(wal);
        }

        // Phase 3: reopen with NO WAL — the entities must still be present, proving the seal consolidated them to the data file.
        using (var scope3 = _serviceProvider.CreateScope())
        {
            var dbe = scope3.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < count; i++)
            {
                Assert.That(tx.IsAlive(entityIds[i]), Is.True,
                    $"entity {i} must survive with NO WAL — the seal must have consolidated it to the data file");
                Assert.That(tx.Open(entityIds[i]).Read(CompAArch.A).A, Is.EqualTo(i + 1),
                    $"entity {i}: component value must survive in the data file after the seal");
            }
        }
    }

    /// <summary>
    /// Recovery apply must be idempotent (AP-12). A crash mid-seal can persist an entity to the data file without advancing
    /// CheckpointLSN, so the next open replays its records again over a base that already contains it. Re-applying a Spawn must
    /// be spawn-if-absent — never a second EntityMap entry (the underlying InsertNew skips the duplicate check). This white-box
    /// test drives <see cref="RecoveryApplier"/> directly, applying the same spawn twice, and requires exactly one live entity.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void RecoveryApplier_ReapplyingSpawn_IsIdempotent()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();

        var routingId = dbe.RoutingIdOf(Archetype<CompAArch>.Metadata); // per-DB routing id embedded in EntityIds of CompAArch
        var entity = new EntityId(1L, routingId);
        const long tsn = 5;

        using (EpochGuard.Enter(dbe.EpochManager))
        using (var applier = new RecoveryApplier(dbe))
        {
            applier.ApplySpawnedEntity((long)entity.RawValue, routingId, 0, tsn, Array.Empty<RecoveryApplier.SlotData>());
            applier.ApplySpawnedEntity((long)entity.RawValue, routingId, 0, tsn, Array.Empty<RecoveryApplier.SlotData>()); // re-run
        }

        // NextFreeTSN restore is the driver's responsibility; do it here so a read transaction sees the recovered entity.
        dbe.TransactionChain.SetNextFreeId(tsn + 1);

        Assert.That(dbe._archetypeStates[Archetype<CompAArch>.Metadata.ArchetypeId].EntityMap.EntryCount, Is.EqualTo(1),
            "re-applying the same Spawn must not create a duplicate EntityMap entry");

        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.IsAlive(entity), Is.True, "the entity must be alive exactly once after the idempotent re-apply");
    }

    /// <summary>
    /// LOG-06 (#514 Phase 2): recovery resolves a component-value record by the per-archetype <b>slot</b> carried on the wire —
    /// never by the process-global, registration-order <c>ComponentTypeId</c>. This is what makes a crash→reopen with a shifted
    /// registration order safe: (routingId, slot) is durable by construction. White-box test over <see cref="RecoveryApplier"/>:
    /// a record whose <see cref="RecoveryApplier.SlotData.SlotIndex"/> is a valid slot restores the value at that slot; a record
    /// whose identity is an out-of-range value — exactly what a stale global <c>ComponentTypeId</c> would decode to in any real
    /// schema — is tolerated (the entity spawns, the bogus slot is dropped), never mis-mapped onto another component.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void RecoveryApplier_ResolvesComponentBySlot_Log06()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();

        var meta = Archetype<CompAArch>.Metadata;
        var routingId = dbe.RoutingIdOf(meta);
        var slot = meta.GetSlot(ArchetypeRegistry.GetComponentTypeId<CompA>()); // CompA's per-archetype slot in CompAArch

        var v = new CompA(42, 1.5f, 2.5);
        var payload = new byte[Unsafe.SizeOf<CompA>()];
        MemoryMarshal.Write(payload, in v);

        const long tsn = 5;
        var good = new EntityId(1L, routingId);
        var bogus = new EntityId(2L, routingId);

        using (EpochGuard.Enter(dbe.EpochManager))
        using (var applier = new RecoveryApplier(dbe))
        {
            // Well-formed record — identity is the SLOT → the value is restored at that slot. Enable the slot so it reads back.
            applier.ApplySpawnedEntity((long)good.RawValue, routingId, (ushort)(1 << slot), tsn,
                new[] { new RecoveryApplier.SlotData { SlotIndex = slot, Payload = payload, Tsn = tsn } });

            // Out-of-range identity (what a stale registration-order ComponentTypeId decodes to) → tolerated, dropped, no crash.
            applier.ApplySpawnedEntity((long)bogus.RawValue, routingId, 0, tsn,
                new[] { new RecoveryApplier.SlotData { SlotIndex = (ushort)meta.ComponentCount, Payload = payload, Tsn = tsn } });
        }

        dbe.TransactionChain.SetNextFreeId(tsn + 1);

        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.IsAlive(good), Is.True);
        ref readonly var read = ref tx.Open(good).Read(CompAArch.A);
        Assert.That(read.A, Is.EqualTo(42), "value recovered at the durable slot");
        Assert.That(read.B, Is.EqualTo(1.5f));
        Assert.That(read.C, Is.EqualTo(2.5));

        Assert.That(tx.IsAlive(bogus), Is.True, "an out-of-range (stale-id) slot record must be tolerated, not crash or mis-map recovery");
    }

    /// <summary>
    /// Recovery must honour an enabled-bits change to a CHECKPOINTED entity — the base-entity counterpart of the in-window
    /// enabled-bits case. The spawn is checkpointed below the recovery window; only the later disable is replayed, so recovery
    /// applies it in place to the already-loaded record. After a crash the disabled component must read back disabled.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void DisableComponentOnCheckpointedEntity_SurvivesCrash()
    {
        const int count = 10;
        var entityIds = new EntityId[count];

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            long spawnHighLsn;
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1, i, i);
                    entityIds[i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                uow.Flush();
                spawnHighLsn = dbe.DurabilityLog.LastAppendedLsn;
            }

            dbe.ForceCheckpoint();
            Assert.That(dbe.CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(5)), Is.True, "checkpoint cycle must complete");
            Assert.That(dbe.CheckpointManager.CheckpointLsn, Is.GreaterThanOrEqualTo(spawnHighLsn),
                "the spawns must be checkpointed below the recovery window (base-entity scenario)");

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i += 2)
                {
                    using var tx = uow.CreateTransaction();
                    tx.OpenMut(entityIds[i]).Disable(CompAArch.A);
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < count; i++)
            {
                Assert.That(tx.IsAlive(entityIds[i]), Is.True, $"checkpointed entity {i} must remain alive");
                Assert.That(tx.Open(entityIds[i]).IsEnabled(CompAArch.A), Is.EqualTo(i % 2 != 0),
                    $"checkpointed entity {i}: the enabled-bit change must survive (even=disabled, odd=enabled)");
            }
        }
    }
}
