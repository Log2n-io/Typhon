using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;

namespace Typhon.Engine.Tests;

/// <summary>
/// End-to-end integration tests for SingleVersion tick fence crash recovery.
/// Verifies that SV component data written via WriteTickFence() survives database reopen.
/// </summary>
[TestFixture]
[Category("WAL")]
[NonParallelizable]
class TickFenceE2ETests
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
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_wal_{Guid.NewGuid():N}");
        _dbDir = Path.Combine(Path.GetTempPath(), $"typhon_db_{Guid.NewGuid():N}");
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
                opts.DatabaseName = "TickFenceE2E";
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
        catch { /* ignored */ }
        try { if (Directory.Exists(_dbDir)) Directory.Delete(_dbDir, true); }
        catch { /* ignored */ }
    }

    private DatabaseEngine CreateEngine(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
        dbe.RegisterComponentFromAccessor<CompSmVersioned>();
        dbe.RegisterComponentFromAccessor<CompSmTransient>();
        dbe.RegisterComponentFromAccessor<CompSmVersionedMix>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════
    // Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(15000)]
    public void TickFence_SV_WriteAndReopen_DataSurvives()
    {
        EntityId id;
        const int updatedValue = 999;

        // Phase 1: Create SV entity, write data, call WriteTickFence, close
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);

            // Spawn entity
            using (var t = dbe.CreateQuickTransaction())
            {
                var comp = new CompSmSingleVersion(42);
                id = t.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
                t.Commit();
            }

            // Update SV component (in-place write, marks DirtyBitmap)
            using (var t = dbe.CreateQuickTransaction())
            {
                var entity = t.OpenMut(id);
                ref var sv = ref entity.Write(SvTestArchetype.SvComp);
                sv.Value = updatedValue;
                t.Commit();
            }

            // Write tick fence — serializes dirty SV components to WAL
            dbe.WriteTickFence(1);

            // Force checkpoint to ensure WAL is durable
            dbe.ForceCheckpoint();
        }

        // Phase 2: Reopen database — WAL recovery should replay tick fence
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            using var t = dbe.CreateQuickTransaction();
            var entity = t.Open(id);
            ref readonly var sv = ref entity.Read(SvTestArchetype.SvComp);
            Assert.That(sv.Value, Is.EqualTo(updatedValue), "SV data should survive reopen via tick fence recovery");
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void TickFence_SV_MultipleUpdates_LastTickFenceWins()
    {
        EntityId id;

        // Phase 1: Create, update twice with two tick fences — second should win
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);

            using (var t = dbe.CreateQuickTransaction())
            {
                var comp = new CompSmSingleVersion(1);
                id = t.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
                t.Commit();
            }

            // First update + tick fence
            using (var t = dbe.CreateQuickTransaction())
            {
                var entity = t.OpenMut(id);
                entity.Write(SvTestArchetype.SvComp).Value = 100;
                t.Commit();
            }
            dbe.WriteTickFence(1);

            // Second update + tick fence
            using (var t = dbe.CreateQuickTransaction())
            {
                var entity = t.OpenMut(id);
                entity.Write(SvTestArchetype.SvComp).Value = 200;
                t.Commit();
            }
            dbe.WriteTickFence(2);

            dbe.ForceCheckpoint();
        }

        // Phase 2: Reopen — should see value from tick 2
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            using var t = dbe.CreateQuickTransaction();
            ref readonly var sv = ref t.Open(id).Read(SvTestArchetype.SvComp);
            Assert.That(sv.Value, Is.EqualTo(200), "Last tick fence should win on recovery");
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void TickFence_SV_MultipleEntities_AllRecovered()
    {
        const int entityCount = 50;
        var ids = new EntityId[entityCount];

        // Phase 1: Create many SV entities, update all, tick fence
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);

            using (var t = dbe.CreateQuickTransaction())
            {
                for (int i = 0; i < entityCount; i++)
                {
                    var comp = new CompSmSingleVersion(i);
                    ids[i] = t.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
                }
                t.Commit();
            }

            // Update all with new values
            using (var t = dbe.CreateQuickTransaction())
            {
                for (int i = 0; i < entityCount; i++)
                {
                    var entity = t.OpenMut(ids[i]);
                    entity.Write(SvTestArchetype.SvComp).Value = (i + 1) * 100;
                }
                t.Commit();
            }

            dbe.WriteTickFence(1);
            dbe.ForceCheckpoint();
        }

        // Phase 2: Reopen — verify all 50 entities recovered correctly
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            using var t = dbe.CreateQuickTransaction();
            for (int i = 0; i < entityCount; i++)
            {
                ref readonly var sv = ref t.Open(ids[i]).Read(SvTestArchetype.SvComp);
                Assert.That(sv.Value, Is.EqualTo((i + 1) * 100), $"Entity index {i} should have recovered value");
            }
        }
    }

    [Test]
    [CancelAfter(15000)]
    public void TickFence_SV_OnlyDirtyEntitiesWritten()
    {
        EntityId id1, id2;

        // Phase 1: Create two entities, only update one, tick fence
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope1);

            using (var t = dbe.CreateQuickTransaction())
            {
                var comp1 = new CompSmSingleVersion(10);
                var comp2 = new CompSmSingleVersion(20);
                id1 = t.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp1));
                id2 = t.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp2));
                t.Commit();
            }

            // First tick fence captures initial state
            dbe.WriteTickFence(1);

            // Update only id1
            using (var t = dbe.CreateQuickTransaction())
            {
                var entity = t.OpenMut(id1);
                entity.Write(SvTestArchetype.SvComp).Value = 777;
                t.Commit();
            }

            // Second tick fence — only id1 is dirty
            dbe.WriteTickFence(2);
            dbe.ForceCheckpoint();
        }

        // Phase 2: Reopen — both entities should have correct values
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = CreateEngine(scope2);

            using var t = dbe.CreateQuickTransaction();
            ref readonly var sv1 = ref t.Open(id1).Read(SvTestArchetype.SvComp);
            ref readonly var sv2 = ref t.Open(id2).Read(SvTestArchetype.SvComp);
            Assert.That(sv1.Value, Is.EqualTo(777), "Updated entity should have new value");
            Assert.That(sv2.Value, Is.EqualTo(20), "Untouched entity should have original value");
        }
    }
}
