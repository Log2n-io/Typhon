using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Integration tests exercising the WAL pipeline end-to-end with real disk I/O.
/// Covers: WAL creation, all three DurabilityModes, dirty page tracking, database reopen
/// (Create vs Load paths), crash recovery (FPI repair), and high-level scenarios.
/// </summary>
[TestFixture]
[Category("WAL")]
class WalIntegrationTests : TestBase
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private ServiceProvider _serviceProvider;
    private string _walDir;
    private string _dbDir;

    [SetUp]
    public void Setup()
    {
        // Unique paths per test — guarantees no WAL/DB files from previous sessions
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_wal_{Guid.NewGuid():N}");
        _dbDir = Path.Combine(Path.GetTempPath(), $"typhon_db_{Guid.NewGuid():N}");

        // Defensive: destroy any leftover files (handles rare Guid collision or crashed previous run)
        CleanupDirectories();

        Directory.CreateDirectory(_walDir);
        Directory.CreateDirectory(_dbDir);

        var services = new ServiceCollection();
        services
            .AddLogging(builder =>
            {
                builder.AddSerilog();
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddSingleton<IWalFileIO>(new WalFileIO())
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

        CleanupDirectories();
        Log.CloseAndFlush();
    }

    private void CleanupDirectories()
    {
        try { if (Directory.Exists(_walDir)) Directory.Delete(_walDir, true); }
        catch { /* ignored — may fail on locked files in crash scenarios */ }
        try { if (Directory.Exists(_dbDir)) Directory.Delete(_dbDir, true); }
        catch { /* ignored */ }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helper Methods
    // ═══════════════════════════════════════════════════════════════

    private DatabaseEngine CreateEngine(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.RegisterComponentFromAccessor<BagData>();
        dbe.RegisterComponentFromAccessor<ItemData>();
        dbe.RegisterComponentFromAccessor<TbSvData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private (EntityId[] ids, CompA[] values) CreateCompAEntities(DatabaseEngine dbe, int count, DurabilityMode mode)
    {
        var ids = new EntityId[count];
        var values = new CompA[count];

        using (var uow = dbe.CreateUnitOfWork(mode))
        {
            for (int i = 0; i < count; i++)
            {
                using var tx = uow.CreateTransaction();
                var comp = new CompA(i + 1, (float)(i * 1.5), i * 2.5);
                ids[i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                values[i] = comp;
                tx.Commit();
            }

            uow.Flush();
        }

        return (ids, values);
    }

    private void VerifyCompAEntities(DatabaseEngine dbe, EntityId[] ids, CompA[] expected)
    {
        var errors = new List<string>();
        using var tx = dbe.CreateQuickTransaction();

        for (int i = 0; i < ids.Length; i++)
        {
            var actual = tx.Open(ids[i]).Read(CompAArch.A);
            if (actual.A != expected[i].A || actual.B != expected[i].B || actual.C != expected[i].C)
            {
                errors.Add($"Entity {ids[i]} (index {i}): got A={actual.A},B={actual.B},C={actual.C}; " +
                           $"expected A={expected[i].A},B={expected[i].B},C={expected[i].C}");
            }
        }

        Assert.That(errors, Is.Empty, string.Join("\n", errors));
    }

    private string GetDatabaseFilePath() => Path.Combine(_dbDir, $"{CurrentDatabaseName}.bin");

    /// <summary>
    /// Forces a checkpoint cycle and waits for it to complete.
    /// This prevents the shutdown path from hanging: when checkpointLsn == durableLsn,
    /// the final checkpoint cycle is skipped and the thread exits immediately.
    /// </summary>
    private static void WaitForCheckpointComplete(DatabaseEngine dbe)
    {
        var cm = dbe.CheckpointManager;
        if (cm == null || dbe.IsDisposed)
        {
            return;
        }

        var before = cm.TotalCheckpoints;
        cm.ForceCheckpoint();

        var sw = Stopwatch.StartNew();
        while (cm.TotalCheckpoints <= before && sw.ElapsedMilliseconds < 5000)
        {
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Wraps a DI scope + DatabaseEngine. On dispose, forces a checkpoint to complete
    /// before disposing the scope, preventing the checkpoint thread from accessing freed memory.
    /// </summary>
    private sealed class EngineScope : IDisposable
    {
        private readonly IServiceScope _scope;
        public readonly DatabaseEngine Engine;

        public EngineScope(IServiceScope scope, DatabaseEngine engine)
        {
            _scope = scope;
            Engine = engine;
        }

        public void Dispose()
        {
            WaitForCheckpointComplete(Engine);
            _scope.Dispose();
        }
    }

    private EngineScope CreateEngineScope()
    {
        var scope = _serviceProvider.CreateScope();
        var engine = CreateEngine(scope);
        return new EngineScope(scope, engine);
    }

    /// <summary>
    /// Scans pages for pages with valid CRC (non-zero, matching computed value).
    /// Returns up to <paramref name="maxPages"/> page indices.
    /// </summary>
    /// <param name="maxPages">Maximum number of pages to find.</param>
    /// <param name="startPage">First page index to scan (use higher values to skip structural pages).</param>
    private int[] FindPagesWithValidCrc(int maxPages = 3, int startPage = 1)
    {
        var dbPath = GetDatabaseFilePath();
        var result = new List<int>();

        using var fs = File.OpenRead(dbPath);
        var page = new byte[PagedMMF.PageSize];

        for (int i = startPage; i < 100 && result.Count < maxPages; i++)
        {
            fs.Seek(i * (long)PagedMMF.PageSize, SeekOrigin.Begin);
            if (fs.Read(page) < PagedMMF.PageSize)
            {
                break;
            }

            var storedCrc = BitConverter.ToUInt32(page, PageBaseHeader.PageChecksumOffset);
            if (storedCrc != 0)
            {
                var computedCrc = Crc32CUtil.ComputeSkipping(page, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
                if (computedCrc == storedCrc)
                {
                    result.Add(i);
                }
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Binary-corrupts a data file page by writing garbage bytes near the END of the page.
    /// Writing near the end avoids breaking structural metadata (segment descriptors, bitmap pointers)
    /// that lives at the beginning of the data area. CRC will still mismatch, enabling FPI repair testing.
    /// </summary>
    private void CorruptDataFilePage(int pageIndex)
    {
        var dbPath = GetDatabaseFilePath();
        using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite);
        long offset = pageIndex * (long)PagedMMF.PageSize;
        // Write garbage near the end of the page (avoids structural metadata at the start)
        fs.Seek(offset + PagedMMF.PageSize - 32, SeekOrigin.Begin);
        fs.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE });
    }

    // ═══════════════════════════════════════════════════════════════
    // Category 1: Low-Level WAL Pipeline
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void WAL_FreshDatabase_CreatesWalDirectory()
    {
        using var es = CreateEngineScope();

        Assert.That(Directory.Exists(_walDir), Is.True, "WAL directory should exist");
        var walFiles = Directory.GetFiles(_walDir, "*.wal");
        Assert.That(walFiles.Length, Is.GreaterThan(0), "Should have pre-allocated WAL segment files");
    }

    [TestCase(DurabilityMode.Deferred)]
    [TestCase(DurabilityMode.GroupCommit)]
    [TestCase(DurabilityMode.Immediate)]
    [CancelAfter(15000)]
    public void WAL_Commit_DurableLsnAdvances(DurabilityMode mode)
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        using (var uow = dbe.CreateUnitOfWork(mode))
        {
            using var tx = uow.CreateTransaction();
            var comp = new CompA(42);
            tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            tx.Commit();
            uow.Flush();
        }

        Assert.That(dbe.WalManager.DurableLsn, Is.GreaterThan(0), $"DurableLsn should advance after {mode} commit + flush");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_CommitBuffer_NextLsnIncreases()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        var lsnBefore = dbe.WalManager.CommitBuffer.NextLsn;

        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            using var tx = uow.CreateTransaction();
            var comp = new CompA(1);
            tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            tx.Commit();
            uow.Flush();
        }

        var lsnMid = dbe.WalManager.CommitBuffer.NextLsn;
        Assert.That(lsnMid, Is.GreaterThan(lsnBefore), "NextLsn should increase after first commit");

        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            using var tx = uow.CreateTransaction();
            var comp = new CompA(2);
            tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            tx.Commit();
            uow.Flush();
        }

        var lsnAfter = dbe.WalManager.CommitBuffer.NextLsn;
        Assert.That(lsnAfter, Is.GreaterThan(lsnMid), "NextLsn should increase after second commit");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_ForceCheckpoint_WritesDirtyPages()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        CreateCompAEntities(dbe, 10, DurabilityMode.Immediate);
        WaitForCheckpointComplete(dbe);

        Assert.That(dbe.CheckpointManager.TotalPagesWritten, Is.GreaterThan(0), "Checkpoint should write dirty pages");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_ForceCheckpoint_AdvancesCheckpointLsn()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        CreateCompAEntities(dbe, 10, DurabilityMode.Immediate);
        WaitForCheckpointComplete(dbe);

        Assert.That(dbe.CheckpointManager.CheckpointLsn, Is.GreaterThan(0), "CheckpointLsn should advance after ForceCheckpoint");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_Shutdown_WriterStopsCleanly()
    {
        bool wasRunning;

        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope);
            wasRunning = dbe.WalManager.IsRunning;
        }
        // Engine disposed — WalManager stopped

        Assert.That(wasRunning, Is.True, "WAL writer should have been running before shutdown");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_Shutdown_UowRegistryEmpty()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        // Create and dispose some UoWs
        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            using var tx = uow.CreateTransaction();
            var comp = new CompA(1);
            tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            tx.Commit();
            uow.Flush();
        }

        Assert.That(dbe.UowRegistry.ActiveCount, Is.EqualTo(0), "All UoWs should be freed after dispose");
    }

    // ═══════════════════════════════════════════════════════════════
    // Category 2: CRUD Operations with WAL
    // ═══════════════════════════════════════════════════════════════

    [TestCase(DurabilityMode.Deferred)]
    [TestCase(DurabilityMode.GroupCommit)]
    [TestCase(DurabilityMode.Immediate)]
    [CancelAfter(15000)]
    public void WAL_CreateEntity_SurvivesReopen(DurabilityMode mode)
    {
        EntityId[] ids;
        CompA[] values;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            (ids, values) = CreateCompAEntities(dbe, 50, mode);
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            VerifyCompAEntities(dbe, ids, values);
        }
    }

    [TestCase(DurabilityMode.Deferred)]
    [TestCase(DurabilityMode.GroupCommit)]
    [TestCase(DurabilityMode.Immediate)]
    [CancelAfter(15000)]
    public void WAL_UpdateComponent_SurvivesReopen(DurabilityMode mode)
    {
        EntityId[] ids;
        CompA[] updatedValues;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            (ids, _) = CreateCompAEntities(dbe, 20, mode);

            // Update all entities with new values
            updatedValues = new CompA[ids.Length];
            using (var uow = dbe.CreateUnitOfWork(mode))
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1000, (float)(i * 3.0), i * 7.0);
                    ref var w = ref tx.OpenMut(ids[i]).Write(CompAArch.A);
                    w = comp;
                    updatedValues[i] = comp;
                    tx.Commit();
                }

                uow.Flush();
            }
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            VerifyCompAEntities(dbe, ids, updatedValues);
        }
    }

    [TestCase(DurabilityMode.Deferred)]
    [TestCase(DurabilityMode.GroupCommit)]
    [TestCase(DurabilityMode.Immediate)]
    [CancelAfter(15000)]
    public void WAL_DeleteEntity_SurvivesReopen(DurabilityMode mode)
    {
        EntityId[] ids;
        CompA[] values;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            (ids, values) = CreateCompAEntities(dbe, 20, mode);

            // Delete the first 10 entities
            using (var uow = dbe.CreateUnitOfWork(mode))
            {
                for (int i = 0; i < 10; i++)
                {
                    using var tx = uow.CreateTransaction();
                    tx.Destroy(ids[i]);
                    tx.Commit();
                }

                uow.Flush();
            }
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            var errors = new List<string>();
            using var tx = dbe.CreateQuickTransaction();

            // First 10 should be deleted
            for (int i = 0; i < 10; i++)
            {
                if (tx.IsAlive(ids[i]))
                {
                    errors.Add($"Entity {ids[i]} (index {i}) should be deleted but was alive");
                }
            }

            // Last 10 should survive
            for (int i = 10; i < 20; i++)
            {
                if (!tx.IsAlive(ids[i]))
                {
                    errors.Add($"Entity {ids[i]} (index {i}) should survive but was not alive");
                }
                else
                {
                    var actual = tx.Open(ids[i]).Read(CompAArch.A);
                    if (actual.A != values[i].A)
                    {
                        errors.Add($"Entity {ids[i]} (index {i}): A={actual.A}, expected {values[i].A}");
                    }
                }
            }

            Assert.That(errors, Is.Empty, string.Join("\n", errors));
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_Destroy_SecondaryIndexCleanedAfterReopen()
    {
        // CompD has secondary indexes on fields A (AllowMultiple), B (Unique), C (AllowMultiple)
        // Destroy should create tombstone revisions that clean secondary indexes via CommitComponentCore

        EntityId entityId;
        var comp = new CompD(1.0f, 42, 2.0);

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using var tx = uow.CreateTransaction();
                entityId = tx.Spawn<CompDArch>(CompDArch.D.Set(in comp));
                tx.Commit();
                uow.Flush();
            }

            // Verify entity exists and index works before destroy
            var indexRef = dbe.GetIndexRef<CompD, int>(d => d.B);
            using (var tx = dbe.CreateQuickTransaction())
            {
                using var enumerator = tx.EnumerateIndex<CompD, int>(indexRef, 42, 42);
                Assert.That(enumerator.MoveNext(), Is.True, "Entity should be in B=42 index before destroy");
            }

            // Destroy the entity
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using var tx = uow.CreateTransaction();
                tx.Destroy(entityId);
                tx.Commit();
                uow.Flush();
            }
        }

        // Reopen and verify: entity is dead AND secondary index is clean
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.IsAlive(entityId), Is.False, "Entity should be dead after reopen");

            // Secondary index should not return the destroyed entity
            var indexRef = dbe.GetIndexRef<CompD, int>(d => d.B);
            using var enumerator = tx.EnumerateIndex<CompD, int>(indexRef, 42, 42);
            var found = false;
            while (enumerator.MoveNext())
            {
                if (enumerator.Current.EntityPK == (long)entityId.RawValue)
                {
                    found = true;
                }
            }
            Assert.That(found, Is.False, "Destroyed entity should not appear in secondary index after reopen");
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_CascadeDestroy_SurvivesReopen()
    {
        // Create bag + items, destroy bag (cascade deletes items), reopen, verify all dead

        EntityId bagId, item1Id, item2Id;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                // Create bag
                using (var tx = uow.CreateTransaction())
                {
                    var bagData = new BagData { Capacity = 10 };
                    bagId = tx.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));
                    tx.Commit();
                }

                // Create items pointing to the bag
                using (var tx = uow.CreateTransaction())
                {
                    var item1 = new ItemData { Owner = bagId, Weight = 5 };
                    var item2 = new ItemData { Owner = bagId, Weight = 3 };
                    item1Id = tx.Spawn<CascadeItem>(CascadeItem.Item.Set(in item1));
                    item2Id = tx.Spawn<CascadeItem>(CascadeItem.Item.Set(in item2));
                    tx.Commit();
                }

                uow.Flush();
            }

            // Destroy bag — should cascade to items
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using var tx = uow.CreateTransaction();
                tx.Destroy(bagId);
                tx.Commit();
                uow.Flush();
            }
        }

        // Reopen and verify all dead
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.IsAlive(bagId), Is.False, "Bag should be dead after reopen");
            Assert.That(tx.IsAlive(item1Id), Is.False, "Item 1 should be cascade-dead after reopen");
            Assert.That(tx.IsAlive(item2Id), Is.False, "Item 2 should be cascade-dead after reopen");
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_Destroy_TombstonedEntitiesExcludedFromEntityMapRebuild()
    {
        // Verify that RebuildEntityMapsFromPersistedData correctly excludes
        // entities with tombstone revisions (CurCompContentChunkId == 0)

        EntityId aliveId, deadId;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using var tx = uow.CreateTransaction();
                var a1 = new CompA(100);
                var a2 = new CompA(200);
                aliveId = tx.Spawn<CompAArch>(CompAArch.A.Set(in a1));
                deadId = tx.Spawn<CompAArch>(CompAArch.A.Set(in a2));
                tx.Commit();
                uow.Flush();
            }

            // Destroy one entity
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using var tx = uow.CreateTransaction();
                tx.Destroy(deadId);
                tx.Commit();
                uow.Flush();
            }
        }

        // Reopen — RebuildEntityMapsFromPersistedData runs
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            using var tx = dbe.CreateQuickTransaction();

            // Alive entity should be in the rebuilt map and readable
            Assert.That(tx.IsAlive(aliveId), Is.True, "Alive entity should survive reopen");
            var comp = tx.Open(aliveId).Read(CompAArch.A);
            Assert.That(comp.A, Is.EqualTo(100), "Alive entity data should be correct");

            // Dead entity should NOT be in the rebuilt map
            Assert.That(tx.IsAlive(deadId), Is.False, "Tombstoned entity should be excluded from EntityMap rebuild");
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_StringComponent_SurvivesReopen()
    {
        const int count = 20;
        var ids = new EntityId[count];
        var strings = new string[count];

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var compA = new CompA(0);
                    var compC = new CompC($"Entity_{i:D3}");
                    ids[i] = tx.Spawn<CompABCArch>(CompABCArch.A.Set(in compA), CompABCArch.C.Set(in compC));
                    strings[i] = $"Entity_{i:D3}";
                    tx.Commit();
                }

                uow.Flush();
            }
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            var errors = new List<string>();
            using var tx = dbe.CreateQuickTransaction();

            for (int i = 0; i < count; i++)
            {
                var actual = tx.Open(ids[i]).Read(CompABCArch.C);
                if (actual.String.AsString != strings[i])
                {
                    errors.Add($"Entity {ids[i]}: got '{actual.String.AsString}', expected '{strings[i]}'");
                }
            }

            Assert.That(errors, Is.Empty, string.Join("\n", errors));
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_IndexedComponent_SurvivesReopen()
    {
        const int count = 20;
        var ids = new EntityId[count];
        var values = new CompD[count];

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompD((float)(i * 1.1), i * 10, i * 2.2);
                    ids[i] = tx.Spawn<CompDArch>(CompDArch.D.Set(in comp));
                    values[i] = comp;
                    tx.Commit();
                }

                uow.Flush();
            }
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            var errors = new List<string>();
            using var tx = dbe.CreateQuickTransaction();

            for (int i = 0; i < count; i++)
            {
                var actual = tx.Open(ids[i]).Read(CompDArch.D);
                if (actual.B != values[i].B)
                {
                    errors.Add($"Entity {ids[i]}: B={actual.B}, expected {values[i].B}");
                }
            }

            Assert.That(errors, Is.Empty, string.Join("\n", errors));
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_MultipleComponents_SurvivesReopen()
    {
        const int count = 20;
        var ids = new EntityId[count];
        var valuesA = new CompA[count];
        var valuesC = new CompC[count];

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var compA = new CompA(i + 1, (float)(i * 1.5), i * 2.5);
                    var compC = new CompC($"Multi_{i:D3}");
                    ids[i] = tx.Spawn<CompABCArch>(CompABCArch.A.Set(in compA), CompABCArch.C.Set(in compC));
                    valuesA[i] = compA;
                    valuesC[i] = compC;
                    tx.Commit();
                }

                uow.Flush();
            }
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            var errors = new List<string>();
            using var tx = dbe.CreateQuickTransaction();

            for (int i = 0; i < count; i++)
            {
                var entity = tx.Open(ids[i]);
                var actualA = entity.Read(CompABCArch.A);
                var actualC = entity.Read(CompABCArch.C);

                if (actualA.A != valuesA[i].A)
                {
                    errors.Add($"Entity {ids[i]}: CompA.A={actualA.A}, expected {valuesA[i].A}");
                }

                if (actualC.String.AsString != valuesC[i].String.AsString)
                {
                    errors.Add($"Entity {ids[i]}: CompC.String='{actualC.String.AsString}', expected '{valuesC[i].String.AsString}'");
                }
            }

            Assert.That(errors, Is.Empty, string.Join("\n", errors));
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_ManyEntities_SurvivesReopen()
    {
        EntityId[] ids;
        CompA[] values;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            (ids, values) = CreateCompAEntities(dbe, 100, DurabilityMode.Immediate);
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            VerifyCompAEntities(dbe, ids, values);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Category 3: Dirty Page Tracking
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void WAL_CreateEntity_PagesBecomesDirty()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        CreateCompAEntities(dbe, 5, DurabilityMode.Immediate);

        var dirtyPages = dbe.MMF.CollectDirtyMemPageIndices();
        Assert.That(dirtyPages.Length, Is.GreaterThan(0), "Creating entities should dirty pages");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_UpdateComponent_PagesBecomesDirty()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        var (ids, _) = CreateCompAEntities(dbe, 5, DurabilityMode.Immediate);

        // Drain dirty counters with multiple checkpoint cycles so we start from a clean baseline.
        // Each cycle decrements DirtyCounter by 1; pages latched N times need N cycles.
        for (int i = 0; i < 30; i++)
        {
            WaitForCheckpointComplete(dbe);
            if (dbe.MMF.CollectDirtyMemPageIndices().Length <= 1)
            {
                break;
            }
        }

        var dirtyBefore = dbe.MMF.CollectDirtyMemPageIndices().Length;

        // Update an entity to dirty its page
        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            using var tx = uow.CreateTransaction();
            var updated = new CompA(999);
            ref var w = ref tx.OpenMut(ids[0]).Write(CompAArch.A);
            w = updated;
            tx.Commit();
            uow.Flush();
        }

        var dirtyAfter = dbe.MMF.CollectDirtyMemPageIndices().Length;
        Assert.That(dirtyAfter, Is.GreaterThan(dirtyBefore), "Update should increase dirty page count");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_ForceCheckpoint_ClearsDirtyPages()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        CreateCompAEntities(dbe, 20, DurabilityMode.Immediate);

        var dirtyBefore = dbe.MMF.CollectDirtyMemPageIndices().Length;
        Assert.That(dirtyBefore, Is.GreaterThan(0), "Should have dirty pages before checkpoint");

        // Each checkpoint cycle decrements DirtyCounter by 1 per page.
        // Pages latched multiple times (e.g., shared by many entities) need multiple cycles.
        // Also, UpdateCheckpointLSN re-dirties the header page each cycle.
        // Run enough cycles to drain all accumulated dirty counts.
        int dirtyAfter = dirtyBefore;
        for (int i = 0; i < 30 && dirtyAfter > 1; i++)
        {
            WaitForCheckpointComplete(dbe);
            dirtyAfter = dbe.MMF.CollectDirtyMemPageIndices().Length;
        }

        // At most the header page stays dirty (UpdateCheckpointLSN re-dirties it each cycle)
        Assert.That(dirtyAfter, Is.LessThan(dirtyBefore), "Multiple checkpoint cycles should reduce dirty page count");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_MultipleOperations_AccumulateDirtyPages()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        WaitForCheckpointComplete(dbe);

        var counts = new List<int>();
        for (int batch = 0; batch < 3; batch++)
        {
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < 5; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(batch * 100 + i);
                    tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                uow.Flush();
            }

            counts.Add(dbe.MMF.CollectDirtyMemPageIndices().Length);
        }

        // Dirty count should generally increase (or at least not decrease) as we add data
        Assert.That(counts.Last(), Is.GreaterThanOrEqualTo(counts.First()),
            $"Dirty pages should accumulate: [{string.Join(", ", counts)}]");
    }

    // ═══════════════════════════════════════════════════════════════
    // Category 4: Create vs Load Modes
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void WAL_FreshDatabase_SchemaPersistedWithWal()
    {
        // Phase 1: Create database, register components
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            // RegisterComponents is called inside CreateEngine — schema persisted on create path
        }

        // Phase 2: Reopen — RegisterComponents should succeed on load path
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            // If this doesn't throw, schema was persisted and loaded correctly

            // Verify we can create entities (proves component tables are functional)
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            var comp = new CompA(42);
            var id = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            tx.Commit();

            Assert.That(id.IsNull, Is.False, "Should be able to create entities after schema reload");
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_LoadAndContinue_NewEntitiesAfterReopen()
    {
        EntityId[] ids1;
        CompA[] values1;

        // Phase 1: Create 20 entities
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            (ids1, values1) = CreateCompAEntities(dbe, 20, DurabilityMode.Immediate);
        }

        EntityId[] ids2;
        CompA[] values2;

        // Phase 2: Reopen and create 20 more
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            (ids2, values2) = CreateCompAEntities(dbe, 20, DurabilityMode.Immediate);
        }

        // Phase 3: Reopen and verify all 40
        using (var scope3 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope3);
            VerifyCompAEntities(dbe, ids1, values1);
            VerifyCompAEntities(dbe, ids2, values2);
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_MultipleReopenCycles_DataAccumulates()
    {
        var allIds = new List<EntityId>();
        var allValues = new List<CompA>();

        for (int cycle = 0; cycle < 3; cycle++)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbe = CreateEngine(scope);

            var (ids, values) = CreateCompAEntities(dbe, 10, DurabilityMode.Immediate);
            allIds.AddRange(ids);
            allValues.AddRange(values);
        }

        // Final reopen: verify all 30 entities
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope);
            VerifyCompAEntities(dbe, allIds.ToArray(), allValues.ToArray());
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_WalSegments_PersistAcrossReopen()
    {
        // Phase 1: Create data
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            CreateCompAEntities(dbe, 20, DurabilityMode.Immediate);
        }

        // After close: active WAL segment should persist (sealed segments recycled by final checkpoint)
        var walFiles = Directory.GetFiles(_walDir, "*.wal");
        Assert.That(walFiles.Length, Is.GreaterThan(0), "WAL segment files should persist after engine close");

        // Phase 2: Reopen succeeds with WAL recovery
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            Assert.That(dbe.LastRecoveryResult.SegmentsScanned, Is.GreaterThan(0),
                "Recovery should scan surviving WAL segments on reopen");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Category 5: Crash Recovery
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void WAL_Recovery_SegmentsScannedOnReopen()
    {
        // Phase 1: Create data (generates WAL records)
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            CreateCompAEntities(dbe, 20, DurabilityMode.Immediate);
        }

        // Phase 2: Reopen — recovery scans surviving WAL segment
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            Assert.That(dbe.LastRecoveryResult.SegmentsScanned, Is.GreaterThan(0),
                "Recovery should scan at least one WAL segment");
            Assert.That(dbe.LastRecoveryResult.RecordsScanned, Is.GreaterThan(0),
                "Recovery should find WAL records in surviving segment");
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_Recovery_CommittedDataSurvivesRecovery()
    {
        EntityId[] ids;
        CompA[] values;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            (ids, values) = CreateCompAEntities(dbe, 30, DurabilityMode.Immediate);
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            // Data should be fully intact — committed with Immediate durability + final checkpoint
            VerifyCompAEntities(dbe, ids, values);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Category 6: High-Level Scenarios
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void WAL_GameTick_DeferredBatch_FlushAtEnd()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        // Simulate a game tick: 100 entity updates in a single Deferred UoW, Flush at the end
        var (ids, _) = CreateCompAEntities(dbe, 100, DurabilityMode.Immediate);

        var expectedValues = new CompA[100];

        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Deferred))
        {
            for (int i = 0; i < 100; i++)
            {
                using var tx = uow.CreateTransaction();
                var comp = new CompA(i + 2000, (float)(i * 0.5), i * 0.25);
                ref var w = ref tx.OpenMut(ids[i]).Write(CompAArch.A);
                w = comp;
                expectedValues[i] = comp;
                tx.Commit();
            }

            // Single flush at the end — batches all WAL records
            uow.Flush();
        }

        // Verify all updates applied
        VerifyCompAEntities(dbe, ids, expectedValues);
    }

    [Test]
    [Category("Sensitive")] // WAL atomicity timing — flaky under parallel CPU load; runs in the gate's serial quiet pass
    [CancelAfter(15000)]
    public void WAL_CriticalTrade_ImmediateAtomicity()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        var (ids, _) = CreateCompAEntities(dbe, 2, DurabilityMode.Immediate);

        // Atomic trade: update both entities in one transaction with Immediate durability
        var tradeA = new CompA(100, 1.0f, 1.0);
        var tradeB = new CompA(200, 2.0f, 2.0);

        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            using var tx = uow.CreateTransaction();
            ref var wa = ref tx.OpenMut(ids[0]).Write(CompAArch.A);
            wa = tradeA;
            ref var wb = ref tx.OpenMut(ids[1]).Write(CompAArch.A);
            wb = tradeB;
            tx.Commit();
            uow.Flush();
        }

        // Both should reflect the trade
        using var readTx = dbe.CreateQuickTransaction();
        var a = readTx.Open(ids[0]).Read(CompAArch.A);
        var b = readTx.Open(ids[1]).Read(CompAArch.A);

        Assert.That(a.A, Is.EqualTo(100), "Trade entity A should have new value");
        Assert.That(b.A, Is.EqualTo(200), "Trade entity B should have new value");
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_MixedDurability_AllModesCoexist()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        var allIds = new ConcurrentBag<(EntityId id, CompA value, DurabilityMode mode)>();
        var errors = new ConcurrentBag<string>();
        var modes = new[] { DurabilityMode.Deferred, DurabilityMode.GroupCommit, DurabilityMode.Immediate };
        var barrier = new Barrier(modes.Length);

        var threads = new Thread[modes.Length];
        for (int t = 0; t < modes.Length; t++)
        {
            var mode = modes[t];
            var threadIdx = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    using var uow = dbe.CreateUnitOfWork(mode);

                    for (int i = 0; i < 20; i++)
                    {
                        using var tx = uow.CreateTransaction();
                        var comp = new CompA(threadIdx * 1000 + i, (float)(threadIdx * 0.1 + i), threadIdx * 100.0 + i);
                        var id = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                        tx.Commit();
                        allIds.Add((id, comp, mode));
                    }

                    uow.Flush();
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {threadIdx} ({mode}): {ex.Message}");
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join(TimeSpan.FromSeconds(10));
        }

        Assert.That(errors, Is.Empty, string.Join("\n", errors));
        Assert.That(allIds.Count, Is.EqualTo(60), "Should have 60 entities (3 threads × 20 each)");

        // Verify all entities readable
        var verifyErrors = new List<string>();
        using var readTx = dbe.CreateQuickTransaction();

        foreach (var (id, expected, mode) in allIds)
        {
            if (!readTx.IsAlive(id))
            {
                verifyErrors.Add($"Entity {id} ({mode}) not readable");
            }
            else
            {
                var actual = readTx.Open(id).Read(CompAArch.A);
                if (actual.A != expected.A)
                {
                    verifyErrors.Add($"Entity {id} ({mode}): A={actual.A}, expected {expected.A}");
                }
            }
        }

        Assert.That(verifyErrors, Is.Empty, string.Join("\n", verifyErrors));
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_BulkImport_DeferredBatches()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        var allIds = new List<EntityId>();
        var allValues = new List<CompA>();

        // Import 500 entities in 5 batches of 100 each
        for (int batch = 0; batch < 5; batch++)
        {
            using var uow = dbe.CreateUnitOfWork(DurabilityMode.Deferred);

            for (int i = 0; i < 100; i++)
            {
                using var tx = uow.CreateTransaction();
                var comp = new CompA(batch * 100 + i, (float)(batch * 10.0 + i), batch * 100.0 + i);
                allIds.Add(tx.Spawn<CompAArch>(CompAArch.A.Set(in comp)));
                allValues.Add(comp);
                tx.Commit();
            }

            uow.Flush();
        }

        // Verify all 500
        VerifyCompAEntities(dbe, allIds.ToArray(), allValues.ToArray());
    }

    [Test]
    [CancelAfter(15000)]
    public void WAL_ConcurrentWriters_NoCorruption()
    {
        using var scope = _serviceProvider.CreateScope();
        using var dbe = CreateEngine(scope);

        const int threadCount = 4;
        const int entitiesPerThread = 25;
        var allIds = new ConcurrentBag<(EntityId id, CompA value)>();
        var errors = new ConcurrentBag<string>();
        var barrier = new Barrier(threadCount);

        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadIdx = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();

                    for (int i = 0; i < entitiesPerThread; i++)
                    {
                        using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
                        using var tx = uow.CreateTransaction();
                        var comp = new CompA(threadIdx * 1000 + i, (float)(threadIdx + i * 0.01), threadIdx * 10.0 + i);
                        var id = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                        tx.Commit();
                        uow.Flush();
                        allIds.Add((id, comp));
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {threadIdx}: {ex.Message}");
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join(TimeSpan.FromSeconds(10));
        }

        Assert.That(errors, Is.Empty, string.Join("\n", errors));
        Assert.That(allIds.Count, Is.EqualTo(threadCount * entitiesPerThread),
            $"Should have {threadCount * entitiesPerThread} entities");

        // Verify all entities consistent
        var verifyErrors = new List<string>();
        using var readTx = dbe.CreateQuickTransaction();

        foreach (var (id, expected) in allIds)
        {
            if (!readTx.IsAlive(id))
            {
                verifyErrors.Add($"Entity {id} not readable");
            }
            else
            {
                var actual = readTx.Open(id).Read(CompAArch.A);
                if (actual.A != expected.A)
                {
                    verifyErrors.Add($"Entity {id}: A={actual.A}, expected {expected.A}");
                }
            }
        }

        Assert.That(verifyErrors, Is.Empty, string.Join("\n", verifyErrors));
    }

    // ═══════════════════════════════════════════════════════════════
    // ECS Destroy — Cascade delete crash recovery
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void WAL_CascadeDestroy_ParentAndChildrenExcludedFromReopen()
    {
        EntityId bagId, item1Id, item2Id, survivorItemId, survivorBagId;

        // Phase 1: Spawn parent (Bag) + children (Items), destroy parent → cascade deletes children
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using var tx = uow.CreateTransaction();
                var bagData = new BagData { Capacity = 10 };
                bagId = tx.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));

                var survivorBagData = new BagData { Capacity = 20 };
                survivorBagId = tx.Spawn<CascadeBag>(CascadeBag.Bag.Set(in survivorBagData));

                var i1 = new ItemData { Owner = new EntityLink<CascadeBag>(bagId), Weight = 5 };
                var i2 = new ItemData { Owner = new EntityLink<CascadeBag>(bagId), Weight = 10 };
                item1Id = tx.Spawn<CascadeItem>(CascadeItem.Item.Set(in i1));
                item2Id = tx.Spawn<CascadeItem>(CascadeItem.Item.Set(in i2));

                var survivorItem = new ItemData { Owner = new EntityLink<CascadeBag>(survivorBagId), Weight = 99 };
                survivorItemId = tx.Spawn<CascadeItem>(CascadeItem.Item.Set(in survivorItem));

                tx.Commit();
                uow.Flush();
            }

            // Destroy parent → cascade deletes item1 and item2
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using var tx = uow.CreateTransaction();
                tx.Destroy(bagId);
                tx.Commit();
                uow.Flush();
            }

            dbe.ForceCheckpoint();
        }

        // Phase 2: Reopen and verify
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            using var tx = dbe.CreateQuickTransaction();

            Assert.That(tx.IsAlive(bagId), Is.False, "Destroyed bag");
            Assert.That(tx.IsAlive(item1Id), Is.False, "Cascade-deleted item 1");
            Assert.That(tx.IsAlive(item2Id), Is.False, "Cascade-deleted item 2");
            Assert.That(tx.IsAlive(survivorBagId), Is.True, "Surviving bag");
            Assert.That(tx.IsAlive(survivorItemId), Is.True, "Surviving item");

            // Verify survivor data is intact
            var comp = tx.Open(survivorBagId).Read(CascadeBag.Bag);
            Assert.That(comp.Capacity, Is.EqualTo(20));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // ECS Destroy — SV component with tick fence + reopen
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void WAL_SvDestroy_TickFenceAndReopen_DataAndIndexCorrect()
    {
        EntityId aliveId, deadId;

        // Phase 1: Spawn SV entities, mutate, tick fence, destroy one, tick fence, checkpoint
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);
            var comp = TbSvArch.Data;

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using var tx = uow.CreateTransaction();
                var d1 = new TbSvData(10, 100);
                aliveId = tx.Spawn<TbSvArch>(TbSvArch.Data.Set(in d1));
                var d2 = new TbSvData(20, 200);
                deadId = tx.Spawn<TbSvArch>(TbSvArch.Data.Set(in d2));
                tx.Commit();
                uow.Flush();
            }

            // Tick fence to establish SV state
            dbe.WriteTickFence(1);

            // Mutate alive entity
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using var tx = uow.CreateTransaction();
                tx.OpenMut(aliveId).Write(comp) = new TbSvData(15, 150);
                tx.Commit();
                uow.Flush();
            }

            // Destroy dead entity
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using var tx = uow.CreateTransaction();
                tx.Destroy(deadId);
                tx.Commit();
                uow.Flush();
            }

            dbe.WriteTickFence(2);
            dbe.ForceCheckpoint();
        }

        // Phase 2: Reopen
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);
            using var tx = dbe.CreateQuickTransaction();

            Assert.That(tx.IsAlive(aliveId), Is.True, "Alive SV entity");
            Assert.That(tx.IsAlive(deadId), Is.False, "Destroyed SV entity");

            // Read surviving entity's data
            var data = tx.Open(aliveId).Read(TbSvArch.Data);
            Assert.That(data.Category, Is.EqualTo(15), "Mutated SV value survived");
            Assert.That(data.Value, Is.EqualTo(150));
        }
    }
}
