using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// DIAGNOSTIC (not a permanent assertion suite): measures the WAL LSN watermarks through a BulkLoad to answer "is the
/// post-bulk CheckpointLSN ≈ 1 benign or a bug?". Reads CurrentLSN (CommitBuffer.NextLsn), DurableLSN, CheckpointLSN at
/// each step and contrasts bulk vs a normal-commit batch. Disk-backed WAL so segment behavior matches the fixtures.
/// </summary>
[TestFixture]
[NonParallelizable]
internal sealed class BulkCheckpointLsnDiagnosticTests
{
    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;

    private static string DbName
    {
        get
        {
            var name = TestContext.CurrentContext.Test.Name;
            foreach (var c in new[] { '(', ')', ',', ' ', '"' })
            {
                name = name.Replace(c, '_');
            }
            const int max = 63;
            const string prefix = "Bcl_";
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
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(BulkCheckpointLsnDiagnosticTests), DbName);
        _dbDir = Path.Combine(root, "db");
        _walDir = Path.Combine(root, "wal");
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = DbName;
                opts.DatabaseDirectory = _dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 8;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = _walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 1 * 1024 * 1024,
                    PreAllocateSegments = 2,
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
        try { Directory.Delete(Path.Combine(_dbDir, ".."), recursive: true); } catch { }
    }

    private static void Dump(string label, DatabaseEngine dbe)
    {
        var current = dbe.WalManager.CommitBuffer.NextLsn;
        var durable = dbe.WalManager.DurableLsn;
        var checkpoint = dbe.CheckpointManager.CheckpointLsn;
        var sealedSegs = dbe.WalManager.SegmentManager.SealedSegmentCount;
        var walBytes = dbe.WalManager.SegmentManager.TotalWalBytes;
        TestContext.WriteLine($"[{label}] currentLSN={current}  durableLSN={durable}  checkpointLSN={checkpoint}  sealedSegs={sealedSegs}  walBytes={walBytes}");
    }

    [Test]
    public void Diagnostic_Bulk_vs_Normal_LsnWatermarks()
    {
        const int count = 10000;

        TestContext.WriteLine("=== BULK ===");
        using (var scope = _serviceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();
            Dump("bulk:open", dbe);

            using (var bulk = dbe.BeginBulkLoad())
            {
                for (var i = 0; i < count; i++)
                {
                    var c = new CompA(i + 1, i, i);
                    bulk.Spawn<CompAArch>(CompAArch.A.Set(in c));
                }
                Dump("bulk:after-spawn-before-complete", dbe);
                bulk.CompleteBulkLoad();
            }
            Dump("bulk:after-complete", dbe);

            dbe.ForceCheckpoint(); // mimic FixtureDatabase's post-bulk checkpoint
            Dump("bulk:after-extra-forcecheckpoint", dbe);
        }

        TestContext.WriteLine("=== NORMAL (contrast) ===");
        // Fresh DB for the contrast.
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        try { foreach (var f in Directory.GetFiles(_walDir, "*.wal")) File.Delete(f); } catch { }
        using (var scope = _serviceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < count; i++)
                {
                    var c = new CompA(i + 1, i, i);
                    tx.Spawn<CompAArch>(CompAArch.A.Set(in c));
                }
                tx.Commit();
            }
            Dump("normal:after-commit", dbe);
            dbe.ForceCheckpoint();
            Dump("normal:after-forcecheckpoint", dbe);
        }

        TestContext.WriteLine("=== ABANDONED BULK (no CompleteBulkLoad) ===");
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        try { foreach (var f in Directory.GetFiles(_walDir, "*.wal")) File.Delete(f); } catch { }
        using (var scope = _serviceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();
            using (var bulk = dbe.BeginBulkLoad())
            {
                for (var i = 0; i < count; i++)
                {
                    var c = new CompA(i + 1, i, i);
                    bulk.Spawn<CompAArch>(CompAArch.A.Set(in c));
                }
                Dump("abandoned:after-spawn", dbe);
                // NO CompleteBulkLoad — the bulk session's Dispose rolls back. Mimics an interrupted/cancelled generation.
            }
            Dump("abandoned:after-bulk-dispose", dbe);
        }
        using (var scope = _serviceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();
            Dump("abandoned:after-reopen", dbe);
        }

        Assert.Pass("diagnostic — see TestContext output for LSN watermarks");
    }
}
