using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

/// <summary>
/// Rule PS-13 (#945): the page cache is one contiguous native block, so a page's address maps back to its slot exactly, and the disk I/O path's
/// windows and the access path agree on where every page lives. Under the small-window nightly the same cache spans eight windows.
/// </summary>
[TestFixture]
class PageCacheContiguityTests
{
    private const int CachePages = 1024;

    private IServiceProvider _serviceProvider;

    [SetUp]
    public void Setup()
    {
        _serviceProvider = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = $"Contig_{TestContext.CurrentContext.Test.ID}";
                options.DatabaseCacheSize = (ulong)CachePages * PagedMMF.PageSize;
                options.TestMode = true;
            })
            .BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<PagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    [Test]
    [VerifiesRule("PS-13")]
    public unsafe void EveryPage_MapsBackToItsSlot_AndTheIOWindowsAgreeOnItsAddress()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var cacheBase = mmf.MemPagesBaseAddress;

        string failure = null;
        for (var i = 0; i < CachePages && failure == null; i++)
        {
            var page = mmf.GetMemPageAddress(i);
            if (page != cacheBase + i * (long)PagedMMF.PageSize)
            {
                failure = $"slot {i}: not at its offset in the block";
            }
            else if (PagedMMF.MemPageIndexOfRawData(page + PagedMMF.PageHeaderSize, cacheBase) != i)
            {
                failure = $"slot {i}: its address does not map back to it";
            }
            else
            {
                using var handle = mmf.PageIOMemoryForTests(i).Pin();
                if ((byte*)handle.Pointer != page)
                {
                    failure = $"slot {i}: the disk I/O buffer is not the page the engine reads";
                }
            }
        }

        Assert.That(failure, Is.Null);
    }
}
