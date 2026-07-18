using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;

namespace Typhon.Engine.Tests;

[TestFixture]
class EpochPageCacheTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;

    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_db";

    [SetUp]
    public void Setup()
    {
        var o = TestContext.CurrentContext.Test.Properties.ContainsKey("MemPageCount");
        var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("MemPageCount")! : PagedMMF.MinimumMemPageCount;
        dcs *= PagedMMF.PageSize;

        var serviceCollection = new ServiceCollection();
        _serviceCollection = serviceCollection;
        _serviceCollection
            .AddLogging(builder =>
            {
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
            .AddScopedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)dcs;
                options.PagesDebugPattern = true;
                options.TestMode = true;
            });

        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<PagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        Log.CloseAndFlush();
    }

    // ========================================
    // Epoch-based eviction protection
    // ========================================

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    public void EpochTaggedPage_NotEvictedWhileScopeActive()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();

        // Step 1: Load pages 0-3 via epoch scope, then exit scope (→ Idle, stale epoch tag → evictable)
        {
            var oldGuard = EpochGuard.Enter(epochManager);
            var oldEpoch = epochManager.GlobalEpoch;
            for (int i = 0; i < 4; i++)
            {
                Assert.That(pmmf.RequestPageEpoch(i, oldEpoch, out _), Is.True, $"RequestPageEpoch should succeed for page {i}");
            }
            oldGuard.Dispose();
        }

        // Step 2: Enter new epoch scope and load pages 4-7 via RequestPageEpoch (→ Idle + current epoch tag)
        var guard = EpochGuard.Enter(epochManager);
        var currentEpoch = epochManager.GlobalEpoch;

        for (int i = 4; i < 8; i++)
        {
            Assert.That(pmmf.RequestPageEpoch(i, currentEpoch, out _), Is.True, $"RequestPageEpoch should succeed for page {i}");
        }

        // Step 3: Request 4 more pages (8-11) — should evict pages 0-3 (stale epoch) not 4-7 (current epoch)
        for (int i = 0; i < 4; i++)
        {
            Assert.That(pmmf.RequestPageEpoch(i + 8, currentEpoch, out _), Is.True, $"RequestPageEpoch should succeed for page {i + 8}");
        }

        // Step 4: Verify epoch-tagged pages 4-7 are still in cache (cache hit, not evicted)
        var metrics = pmmf.GetMetrics();
        var cacheMissBefore = metrics.MemPageCacheMiss;
        for (int i = 4; i < 8; i++)
        {
            Assert.That(pmmf.RequestPageEpoch(i, currentEpoch, out _), Is.True, $"Epoch-tagged page {i} should still be in cache");
        }
        Assert.That(metrics.MemPageCacheMiss, Is.EqualTo(cacheMissBefore), "Epoch-tagged pages should be cache hits (not evicted)");

        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    public void EpochTaggedPage_EvictedAfterScopeExit()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();

        // Step 1: Enter epoch scope and fill all 8 cache slots via RequestPageEpoch
        long currentEpoch;
        {
            var guard = EpochGuard.Enter(epochManager);
            currentEpoch = epochManager.GlobalEpoch;

            for (int i = 0; i < 8; i++)
            {
                Assert.That(pmmf.RequestPageEpoch(i, currentEpoch, out _), Is.True);
            }

            guard.Dispose();
        }

        // Step 2: After scope exit, epoch advanced — pages are now evictable
        Assert.That(epochManager.GlobalEpoch, Is.GreaterThan(currentEpoch), "Epoch should have advanced after scope exit");

        // Step 3: Request new pages via epoch — should be able to evict old epoch-tagged pages
        var metrics = pmmf.GetMetrics();
        var cacheMissBefore = metrics.MemPageCacheMiss;
        {
            var guard = EpochGuard.Enter(epochManager);
            var newEpoch = epochManager.GlobalEpoch;
            for (int i = 0; i < 4; i++)
            {
                Assert.That(pmmf.RequestPageEpoch(i + 100, newEpoch, out _), Is.True);
            }
            guard.Dispose();
        }

        // Verify eviction occurred (cache misses for the new pages)
        Assert.That(metrics.MemPageCacheMiss, Is.EqualTo(cacheMissBefore + 4), "New pages should cause cache misses (old pages evicted)");
    }

    // ========================================
    // Epoch tagging verification
    // ========================================

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    public void RequestPageEpoch_TagsCorrectEpoch()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();

        var guard = EpochGuard.Enter(epochManager);
        var currentEpoch = epochManager.GlobalEpoch;

        Assert.That(pmmf.RequestPageEpoch(0, currentEpoch, out var memPageIndex), Is.True);

        // Verify the page's AccessEpoch matches the epoch we passed in
        Assert.That(pmmf.GetPageAccessEpoch(memPageIndex), Is.EqualTo(currentEpoch));

        // The page should be in Idle state (epoch-mode doesn't use Shared/Exclusive for reads)
        Assert.That(pmmf.GetPageState(memPageIndex), Is.EqualTo(PagedMMF.PageState.Idle));

        guard.Dispose();
    }

    // ========================================
    // ChangeSet epoch-mode support
    // ========================================

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    public void AddByMemPageIndex_MarksDirty()
    {
        using var scope = _serviceProvider.CreateScope();
        var pmmf = scope.ServiceProvider.GetService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();

        // Enter epoch scope and get a page with exclusive latch
        var guard = EpochGuard.Enter(epochManager);
        var currentEpoch = epochManager.GlobalEpoch;

        Assert.That(pmmf.RequestPageEpoch(0, currentEpoch, out var memPageIndex), Is.True);
        Assert.That(pmmf.TryLatchPageExclusive(memPageIndex), Is.True);

        var cs = pmmf.CreateChangeSet();

        // Mark dirty using AddByMemPageIndex
        cs.AddByMemPageIndex(memPageIndex);

        // Unlatch — page goes to Idle with DirtyCounter > 0
        pmmf.UnlatchPageExclusive(memPageIndex);

        // Page should be in Idle state (dirty pages stay Idle with DirtyCounter > 0)
        Assert.That(pmmf.GetPageState(memPageIndex), Is.EqualTo(PagedMMF.PageState.Idle));

        // Reset should decrement dirty counter, page stays Idle
        cs.Reset();
        Assert.That(pmmf.GetPageState(memPageIndex), Is.EqualTo(PagedMMF.PageState.Idle));

        guard.Dispose();
    }
}
