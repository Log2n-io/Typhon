using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// A publish larger than the cache gives its dirty marks back between entries (#831), so the checkpoint writes some of its pages to the data file, and
/// the cache evicts them, while the commit is still between its append and the end of its publish. A hard crash right after the commit then reopens on a
/// data file holding part of the publish, below a CheckpointLSN that the in-flight floor kept under the commit's records (CK-13): recovery replays the
/// commit, and every value and index entry is the committed one.
/// </summary>
[TestFixture]
[NonParallelizable] // PagedMMF.PublishStepRefreshes is a static.
internal sealed class PublishLargerThanTheCacheRecoveryTests
{
    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;

    private const string CurrentDatabaseName = "PublishLargerThanTheCache";

    [SetUp]
    public void Setup()
    {
        var root = Path.Combine(TestStorage.Root, nameof(PublishLargerThanTheCacheRecoveryTests));
        _dbDir = Path.Combine(root, CurrentDatabaseName, "db");
        _walDir = Path.Combine(root, CurrentDatabaseName, "wal");
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_walDir);
        _serviceProvider = BuildProvider(PagedMMF.MinimumCacheSize);
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    private ServiceProvider BuildProvider(ulong cacheSize)
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
                opts.DatabaseName = CurrentDatabaseName;
                opts.DatabaseDirectory = _dbDir;
                opts.DatabaseCacheSize = cacheSize;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = _walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 16 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
            });

        return services.BuildServiceProvider();
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

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("CK-13")]
    [VerifiesRule("EP-02")]
    public void APublishThatGaveBackItsMarks_IsRecoveredWhole_AfterAHardCrash()
    {
        const int n = 20_000;
        const int moved = 10_000_000;
        var ids = new EntityId[n];

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompD>();
            dbe.InitializeArchetypes();

            for (var s = 0; s < n; s += 1_000)
            {
                using var seed = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
                for (var i = s; i < s + 1_000; i++)
                {
                    ids[i] = seed.Spawn<CompDArch>(CompDArch.D.Set(new CompD(i, i, i)));
                }

                Assert.That(seed.Commit(), Is.True);
            }

            var refreshes = Interlocked.Read(ref PagedMMF.PublishStepRefreshes);
            var written = dbe.CheckpointManager.TotalPagesWritten;
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < n; i++)
                {
                    tx.OpenMut(ids[i]).Set(CompDArch.D, new CompD(i + 0.5f, i + moved, i + 0.25));
                }

                Assert.That(tx.Commit(), Is.True);
            }

            Assert.That(Interlocked.Read(ref PagedMMF.PublishStepRefreshes) - refreshes, Is.GreaterThan(0),
                "premise: the publish gave its marks back under pressure");
            Assert.That(dbe.CheckpointManager.TotalPagesWritten - written, Is.GreaterThan(0), "premise: the checkpoint wrote pages meanwhile");
            var indexSegment = dbe._archetypeStates[ArchetypeRegistry.GetMetadata<CompDArch>().ArchetypeId].ClusterState.IndexSegment;
            Assert.That(indexSegment.Length, Is.GreaterThan((long)PagedMMF.MinimumCacheSize / PagedMMF.PageSize),
                "premise: the index segment the crash open clears is larger than the cache it reopens with (#1463)");

            // Power cut: the cache is discarded, no clean-shutdown marker. What the checkpoint wrote mid-publish is on the data file; the rest is in the WAL.
            dbe.SimulateHardCrash();
        }

        // Reopened with the same 8 MiB cache, smaller than the index segment the crash open clears: the clear reads and releases one bitmap page at a time
        // (EP-02). Epoch-tagged, it pinned the whole cache and the open timed out on back-pressure (#1463).
        _serviceProvider.Dispose();
        _serviceProvider = BuildProvider(PagedMMF.MinimumCacheSize);
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompD>();
            dbe.InitializeArchetypes();

            using var read = dbe.CreateQuickTransaction();
            Assert.That(read.Query<CompDArch>().WhereField<CompD>(d => d.B >= moved).Count(), Is.EqualTo(n), "every unique key moved");
            Assert.That(read.Query<CompDArch>().WhereField<CompD>(d => d.B < moved).Count(), Is.Zero, "no old key left");
            foreach (var i in new[] { 0, 1, n / 3, n / 2, n - 2, n - 1 })
            {
                var d = read.Open(ids[i]).Read(CompDArch.D);
                Assert.That((d.A, d.B, d.C), Is.EqualTo((i + 0.5f, i + moved, i + 0.25)), $"entity {i}");
            }
        }
    }
}
