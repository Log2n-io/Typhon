using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// Regression guard for the <c>.typhon</c> bundle format (#450): when the caller leaves
/// <see cref="WalWriterOptions.WalDirectory"/> null (the bundle-format default), the engine derives <c>{bundle}/wal</c>.
/// That derivation MUST happen before the reopen path reads the WAL directory to decide whether crash recovery must run —
/// otherwise a hard crash + reopen under the default config silently skips WAL replay and loses committed data.
/// </summary>
/// <remarks>
/// The two sessions use <b>independent</b> service providers (fresh options each) — this models a real process restart,
/// where session 2 sees <see cref="WalWriterOptions.WalDirectory"/> == null again. Reusing one provider would let
/// session 1's derivation mutate the shared options and mask the bug for session 2.
/// </remarks>
[TestFixture]
[NonParallelizable]
class BundleWalRecoveryTests
{
    private const string DbName = "bundle_wal_recovery";
    private string _dbDir;

    [SetUp]
    public void Setup()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(BundleWalRecoveryTests));
        Directory.CreateDirectory(_dbDir);
        // Start clean — remove any bundle left by a prior run so session 1 truly creates the database.
        var bundle = Path.Combine(_dbDir, $"{DbName}.typhon");
        if (Directory.Exists(bundle))
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            var bundle = Path.Combine(_dbDir, $"{DbName}.typhon");
            if (Directory.Exists(bundle))
            {
                Directory.Delete(bundle, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    // A fresh provider with WalDirectory LEFT NULL (the engine must derive {bundle}/wal) and the real WalFileIO (no
    // in-memory backend) so WAL segments survive a simulated hard crash. Each call is an independent "process".
    private ServiceProvider BuildProvider()
    {
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
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    // WalDirectory deliberately null → engine derives {bundle}/wal.
                    UseFUA = false,
                    GroupCommitIntervalMs = 5,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
            });
        return services.BuildServiceProvider();
    }

    [Test]
    [CancelAfter(15_000)]
    public void NullWalDirectory_HardCrashReopen_RecoversCommittedData()
    {
        EntityId id;

        // Session 1 (own process/provider): durably commit an entity (Immediate → WAL-acked), then hard-crash. The commit
        // lives ONLY in the WAL segments on disk (SimulateHardCrash skips the data-file flush + clean-shutdown marker).
        using (var provider1 = BuildProvider())
        {
            using var scope1 = provider1.CreateScope();
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using (var tx = uow.CreateTransaction())
                {
                    var a = new CompA(42);
                    id = tx.Spawn<CompAArch>(CompAArch.A.Set(in a));
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        // Session 2 (fresh process/provider — WalDirectory is null AGAIN): reopen the same bundle. The engine must derive
        // {bundle}/wal early enough that the reopen path sees the WAL segments and replays them.
        using (var provider2 = BuildProvider())
        {
            using var scope2 = provider2.CreateScope();
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            var read = tx.Open(id).Read(CompAArch.A);
            Assert.That(read.A, Is.EqualTo(42),
                "a committed entity must survive a hard crash + reopen with the default (null) WalDirectory — WAL recovery must run");
        }
    }
}
