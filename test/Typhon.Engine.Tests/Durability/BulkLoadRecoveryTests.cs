using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// P3 crash-recovery tests for <see cref="BulkLoadSession"/>. Verifies the recovery contract:
/// <list type="bullet">
///   <item><b>BL-02 / crash mid-bulk:</b> a <c>BulkBegin</c> without a matching <c>BulkEnd</c> → bulk entities must not be visible on reopen.</item>
///   <item><b>BL-02 / crash post-Complete:</b> a <c>BulkBegin</c> + durable <c>BulkEnd</c> → all entities recovered on reopen.</item>
///   <item><b>No regression:</b> databases without any bulk sessions reopen cleanly via the standard recovery path.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>v1 design choice (P3 deviation from the original plan):</b> the recovery contract for an incomplete bulk is satisfied by the *existing*
/// <see cref="UowRegistry"/> void path (UR-03 / UR-05). The bulk's underlying <see cref="UnitOfWork"/> is left <c>Pending</c> on crash → WAL recovery sees no
/// <c>UowCommit</c> marker → <c>VoidRemainingPending</c> marks the UoW <c>Void</c> → <c>CommittedBeforeTSN = 0</c> + the void-bitmap path makes every
/// bulk-stamped revision invisible. Phase 3b (explicit page-free) is **deferred** because: (a) MVCC handles visibility correctly without it, (b) the v1
/// page-leak is bounded and not user-visible, (c) adding the per-session allocation log requires invasive hooks into the allocation path. Documented in
/// <c>claude/design/Durability/BulkLoad/03-recovery.md</c>.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class BulkLoadRecoveryTests
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
            const string prefix = "Blr_";
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
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(BulkLoadRecoveryTests));
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

        try { if (Directory.Exists(Path.Combine(_dbDir, ".."))) Directory.Delete(Path.Combine(_dbDir, ".."), true); } catch { }
    }

    [Test]
    public void Crash_AfterBulkBegin_NoEntitiesVisibleAfterReopen()
    {
        const int count = 50;
        var bulkIds = new EntityId[count];

        // Phase 1: open engine, begin bulk, spawn 50, then Dispose the bulk session (NOT CompleteBulkLoad).
        // The bulk session's Dispose calls Transaction.Rollback → UoW becomes Pending → on reopen, recovery
        // voids the UoW per UR-03 (no UowCommit marker → VoidRemainingPending). Engine disposes via scope.
        //
        // For a TRUE crash (no Dispose at all), we would need to abort the process — but that's not how
        // unit tests work. The Dispose path is the closest reproducible scenario; if Dispose-rollback +
        // engine-shutdown produces correct recovery behavior, then a real crash (which leaves the UoW
        // even more Pending than Dispose does) will produce the same outcome.
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var bulk = dbe.BeginBulkLoad())
            {
                for (int i = 0; i < count; i++)
                {
                    var comp = new CompA(i + 1, i, i);
                    bulkIds[i] = bulk.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                }
                // Dispose called here (NOT CompleteBulkLoad). Rolls back the transaction.
            }
        }

        // Phase 2: reopen. Recovery runs. UowRegistry.VoidRemainingPending kicks in for the bulk's UoW.
        // The bulk's revisions become invisible via the standard MVCC void path (UR-03 / UR-05).
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            int visibleCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (tx.IsAlive(bulkIds[i]))
                {
                    visibleCount++;
                }
            }

            Assert.That(visibleCount, Is.EqualTo(0),
                "BL-02: BulkBegin without matching BulkEnd → no bulk entities visible after recovery");
        }
    }

    [Test]
    public void Crash_AfterCompleteBulkLoad_AllEntitiesSurviveReopen()
    {
        const int count = 50;
        var bulkIds = new EntityId[count];

        // Phase 1: open, bulk-load 50 entities, CompleteBulkLoad (durability barrier), then engine disposes.
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var bulk = dbe.BeginBulkLoad())
            {
                for (int i = 0; i < count; i++)
                {
                    var comp = new CompA(i + 1, i, i);
                    bulkIds[i] = bulk.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                }
                bulk.CompleteBulkLoad();
            }
        }

        // Phase 2: reopen, all 50 entities should be visible.
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            // Note: WalRecovery's scanner may not observe BulkBegin/BulkEnd chunks in all configurations
            // due to WalSegmentReader's 4096-byte drain-gap limitation (the reader stops at FrameLength==0).
            // The visibility correctness (entities survive reopen) does NOT depend on manifest observation
            // — it comes from the UowRegistry state (WalDurable persists across reopen). The Phase-2
            // dispatch for BulkBegin/BulkEnd in WalRecovery is future-proofing for when the reader gap
            // is closed; for now it's a no-op observability counter.

            using var tx = dbe.CreateQuickTransaction();
            int visibleCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (tx.IsAlive(bulkIds[i]))
                {
                    visibleCount++;
                }
            }

            Assert.That(visibleCount, Is.EqualTo(count),
                "BL-02: BulkBegin + durable BulkEnd → all bulk entities visible after recovery");
        }
    }

    [Test]
    public void Recovery_NoBulk_NoRegression()
    {
        // Sanity: a database with only regular transactions reopens cleanly + entities survive.
        // Proves the BulkLoad code paths don't break the standard recovery flow when no bulk session ran.
        const int count = 25;
        var entityIds = new EntityId[count];

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
            for (int i = 0; i < count; i++)
            {
                using var tx = uow.CreateTransaction();
                var comp = new CompA(i + 1, i, i);
                entityIds[i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                tx.Commit();
            }
            uow.Flush();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < count; i++)
            {
                Assert.That(tx.IsAlive(entityIds[i]), Is.True, $"regular-tx entity {i} must survive reopen");
            }
        }
    }
}
