using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// PS-14: a page the engine did not read from disk reads as zero before any caller can reach it (#1126).
/// </summary>
/// <remarks>
/// Every page cache in the test process starts filled with <c>0xA5</c> (<see cref="PagedMMF.PoisonCacheForProcess"/>), and the
/// stale content these tests stage is <c>0x5A</c>. Neither is zero, so a passing assertion means "was cleared", never "happened
/// to be zero".
/// </remarks>
[TestFixture]
class PageInitialContentTests
{
    private const byte StalePattern = 0x5A;

    private IServiceProvider _serviceProvider;

    // Test names here exceed the 63-byte database-name limit, so the database is named after the test id.
    private static string CurrentDatabaseName => $"PageInit_{TestContext.CurrentContext.Test.ID}";

    [SetUp]
    public void Setup()
    {
        var pageCount = (int)TestContext.CurrentContext.Test.Properties.Get("MemPageCount")!;

        _serviceProvider = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)pageCount * PagedMMF.PageSize;
                options.PagesDebugPattern = false;
                options.TestMode = true;
            })
            .BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<PagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    [VerifiesRule("PS-14")]
    public unsafe void ANewPageInANeverUsedSlot_ReadsAsZero()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();

        Assert.That(PagedMMF.PoisonCacheForProcess, Is.True, "precondition: the test assembly poisons every page cache");

        using var guard = EpochGuard.Enter(epochManager);
        Assert.That(mmf.RequestPageEpoch(3, epochManager.GlobalEpoch, out var memPageIndex), Is.True);

        PageAssert.AllZero(mmf.GetMemPageAddress(memPageIndex), PagedMMF.PageSize, "a new page in a never-used (poisoned) slot");
    }

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    public unsafe void ANewPageInARecycledSlot_ReadsAsZero()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();

        // Occupy every slot with a page whose bytes are all StalePattern. The pages are never marked dirty, so once the epoch scope
        // ends each slot is evictable.
        var stagedSlots = new HashSet<int>();
        using (EpochGuard.Enter(epochManager))
        {
            var epoch = epochManager.GlobalEpoch;
            for (var filePageIndex = 0; filePageIndex < 8; filePageIndex++)
            {
                Assert.That(mmf.RequestPageEpoch(filePageIndex, epoch, out var memPageIndex), Is.True);
                NativeMemory.Fill(mmf.GetMemPageAddress(memPageIndex), PagedMMF.PageSize, StalePattern);
                stagedSlots.Add(memPageIndex);
            }
        }
        Assert.That(stagedSlots, Has.Count.EqualTo(8), "precondition: every slot of the cache holds a staged page");

        using (EpochGuard.Enter(epochManager))
        {
            Assert.That(mmf.RequestPageEpoch(100, epochManager.GlobalEpoch, out var memPageIndex), Is.True);
            Assert.That(stagedSlots, Does.Contain(memPageIndex), "precondition: the new page took a recycled slot");

            PageAssert.AllZero(mmf.GetMemPageAddress(memPageIndex), PagedMMF.PageSize, "a new page in a recycled slot");
        }
    }
}
