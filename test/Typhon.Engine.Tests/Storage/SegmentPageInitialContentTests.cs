using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// PS-14 at the segment layer: every page a new segment is given is fully initialised, even when its page number is reused from a
/// deleted segment and still holds that segment's bytes, in the cache or on disk (#1126).
/// </summary>
[TestFixture]
class SegmentPageInitialContentTests
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
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)pageCount * PagedMMF.PageSize;
                options.PagesDebugPattern = false;
                options.TestMode = true;
            })
            .BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 1024)]
    public unsafe void ANewSegmentOnReusedPageNumbers_HasZeroedDataPages()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        using var guard = EpochGuard.Enter(mmf.EpochManager);
        var epoch = mmf.EpochManager.GlobalEpoch;

        var deletedPages = StageDeletedSegment(mmf, 10, epoch, fillRootTail: false);

        var segment = mmf.AllocateSegment(PageBlockType.None, 10);
        var reused = 0;
        for (var i = 1; i < segment.Pages.Length; i++)
        {
            if (!deletedPages.ContainsKey(segment.Pages[i]))
            {
                continue;
            }
            reused++;
            var addr = segment.GetPageAddress(i, epoch, out _);
            PageAssert.AllZero(addr + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize, $"data page {i} (file page {segment.Pages[i]}, reused)");
        }
        Assert.That(reused, Is.GreaterThan(0), "precondition: the new segment reuses data-page numbers of the deleted one");
    }

    /// <summary>
    /// The full clear keeps the page's ChangeRevision: per-sector verification stamps every sector with its low 16 bits, and a reused
    /// page number's revision must not go back to 0.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 1024)]
    public unsafe void AReusedDataPage_KeepsItsChangeRevision()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        using var guard = EpochGuard.Enter(mmf.EpochManager);
        var epoch = mmf.EpochManager.GlobalEpoch;

        var deletedPages = StageDeletedSegment(mmf, 10, epoch, fillRootTail: false);

        var segment = mmf.AllocateSegment(PageBlockType.None, 10);
        var reused = 0;
        for (var i = 1; i < segment.Pages.Length; i++)
        {
            if (!deletedPages.TryGetValue(segment.Pages[i], out var persistedRevision))
            {
                continue;
            }
            reused++;
            Assert.That(persistedRevision, Is.GreaterThan(0), "precondition: the deleted page was persisted at a non-zero revision");
            var header = (PageBaseHeader*)(segment.GetPageAddress(i, epoch, out _) + PageBaseHeader.Offset);
            Assert.That(header->ChangeRevision, Is.EqualTo(persistedRevision), $"data page {i} (file page {segment.Pages[i]}, reused)");
        }
        Assert.That(reused, Is.GreaterThan(0), "precondition: the new segment reuses data-page numbers of the deleted one");
    }

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 1024)]
    public unsafe void ANewSegmentRootOnAReusedPageNumber_HasAZeroDirectoryTail_InMemoryAndOnDisk()
    {
        int rootPageIndex;
        const int length = 10;
        {
            using var scope = _serviceProvider.CreateScope();
            var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            using var guard = EpochGuard.Enter(mmf.EpochManager);
            var epoch = mmf.EpochManager.GlobalEpoch;

            var deletedPages = StageDeletedSegment(mmf, length, epoch, fillRootTail: true);

            var cs = mmf.CreateChangeSet();
            var segment = mmf.AllocateSegment(PageBlockType.None, length, cs);
            rootPageIndex = segment.RootPageIndex;
            Assert.That(deletedPages.Keys, Does.Contain(rootPageIndex), "precondition: the new root reuses a page number of the deleted segment");

            AssertDirectoryTailIsZero(segment.GetPageAddress(0, epoch, out _), length, "root directory tail, in memory");
            cs.SaveChanges();
        }

        {
            using var scope = _serviceProvider.CreateScope();
            var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            using var guard = EpochGuard.Enter(mmf.EpochManager);
            var segment = mmf.GetSegment(rootPageIndex);

            AssertDirectoryTailIsZero(segment.GetPageAddress(0, mmf.EpochManager.GlobalEpoch, out _), length, "root directory tail, reloaded from disk");
        }
    }

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 4400)]
    public unsafe void TheTerminatorOnlyMapPage_OnAReusedPageNumber_IsZeroPastTheTerminator()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        using var guard = EpochGuard.Enter(mmf.EpochManager);
        var epoch = mmf.EpochManager.GlobalEpoch;

        // A segment of exactly one root's worth of entries fills the root directory, so its terminating 0 goes alone on an extra map page.
        const int length = LogicalSegment<PersistentStore>.RootHeaderIndexSectionCount;
        var deletedPages = StageDeletedSegment(mmf, length + 16, epoch, fillRootTail: false);

        var segment = mmf.AllocateSegment(PageBlockType.None, length);
        var root = segment.GetPageAddress(0, epoch, out _);
        var endPageIndex = ((LogicalSegmentHeader*)(root + LogicalSegmentHeader.Offset))->LogicalSegmentNextMapPBID;
        Assert.That(endPageIndex, Is.Not.Zero, "precondition: the directory continues on a terminator-only map page");
        Assert.That(deletedPages.Keys, Does.Contain(endPageIndex), "precondition: the terminator-only map page reuses a page number of the deleted segment");

        Assert.That(mmf.RequestPageEpoch(endPageIndex, epoch, out var endMemPageIndex), Is.True);
        var endPageRawData = mmf.GetMemPageAddress(endMemPageIndex) + PagedMMF.PageHeaderSize;
        Assert.That(*(int*)endPageRawData, Is.Zero, "the terminator");
        PageAssert.AllZero(endPageRawData + sizeof(int), PagedMMF.PageRawDataSize - sizeof(int), "terminator-only map page, past the terminator");
    }

    /// <summary>
    /// Creates a segment, fills every data page's raw data with <see cref="StalePattern"/> (and, if asked, the root's raw data past its
    /// directory), persists it, deletes it, and returns its page numbers with the ChangeRevision each was persisted at — the pages now
    /// free, and still holding that content in the cache and on disk.
    /// </summary>
    private static unsafe Dictionary<int, int> StageDeletedSegment(ManagedPagedMMF mmf, int length, long epoch, bool fillRootTail)
    {
        var cs = mmf.CreateChangeSet();
        var segment = mmf.AllocateSegment(PageBlockType.None, length, cs);
        for (var i = 0; i < segment.Pages.Length; i++)
        {
            var addr = segment.GetPageAddressExclusive(i, epoch, out var memPageIndex);
            cs.AddByMemPageIndex(memPageIndex);
            var rawData = addr + PagedMMF.PageHeaderSize;
            if (i > 0)
            {
                NativeMemory.Fill(rawData, PagedMMF.PageRawDataSize, StalePattern);
            }
            else if (fillRootTail)
            {
                // The root's raw data is its directory: `length` entries, then the terminating 0. Everything after is unused.
                var used = (length + 1) * sizeof(int);
                NativeMemory.Fill(rawData + used, (nuint)(PagedMMF.PageRawDataSize - used), StalePattern);
            }
            mmf.UnlatchPageExclusive(memPageIndex);
        }
        cs.SaveChanges();

        var revisions = new Dictionary<int, int>();
        for (var i = 0; i < segment.Pages.Length; i++)
        {
            revisions[segment.Pages[i]] = ((PageBaseHeader*)(segment.GetPageAddress(i, epoch, out _) + PageBaseHeader.Offset))->ChangeRevision;
        }
        Assert.That(mmf.DeleteSegment(segment), Is.True);
        return revisions;
    }

    private static unsafe void AssertDirectoryTailIsZero(byte* rootPage, int length, string what)
    {
        var rawData = rootPage + PagedMMF.PageHeaderSize;
        Assert.That(((int*)rawData)[length], Is.Zero, $"{what}: the terminator");
        var used = (length + 1) * sizeof(int);
        PageAssert.AllZero(rawData + used, PagedMMF.PageRawDataSize - used, what);
    }
}

static class PageAssert
{
    /// <summary>Asserts <paramref name="length"/> bytes at <paramref name="address"/> are zero, reporting the first offender.</summary>
    public static unsafe void AllZero(byte* address, int length, string what)
    {
        var bytes = new ReadOnlySpan<byte>(address, length);
        var firstNonZero = bytes.IndexOfAnyExcept((byte)0);
        if (firstNonZero >= 0)
        {
            Assert.Fail($"{what}: byte {firstNonZero} of {length} is 0x{bytes[firstNonZero]:X2}, expected every byte to be zero");
        }
    }
}
