using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #945 S6: <c>SavePages</c> coalesces pages that follow each other in memory and on file into one write, and a write's buffer is a slice of one
/// page-cache window. So a run must end on a window's last page. Expectations follow the ACTIVE window size: at the default (1 GiB) the
/// round-trip test is inconclusive, since a test cache is one window; the nightly runs the suite at 1 MiB windows, where it crosses.
/// </summary>
[TestFixture]
class PageCacheWriteRunTests
{
    private static int PagesPerWindow => PageCacheAddressing.PagesPerWindowMask + 1;

    private static List<(int MemPageIndex, int Length)> Build(int[] mem, int[] file)
    {
        var runs = new List<(int memPageIndex, int length)>();
        PagedMMF.BuildWriteRuns(mem, file, runs);
        return runs.ConvertAll(r => (r.memPageIndex, r.length));
    }

    private static int[] Range(int start, int count)
    {
        var a = new int[count];
        for (var i = 0; i < count; i++)
        {
            a[i] = start + i;
        }

        return a;
    }

    [Test]
    public void ARunAcrossAWindowBoundary_IsSplitThere()
    {
        // At the default window this is the page pair 131 071 → 131 072.
        var runs = Build(Range(PagesPerWindow - 2, 4), Range(10, 4));

        Assert.That(runs, Is.EqualTo(new List<(int, int)> { (PagesPerWindow - 2, 2), (PagesPerWindow, 2) }));
    }

    [Test]
    public void NoRunStraddlesAWindow_AndEveryLengthFitsAnInt()
    {
        const int extra = 5;
        var mem = Range(PagesPerWindow - 3, 3 * PagesPerWindow + extra);
        var runs = Build(mem, Range(1, mem.Length));

        var covered = 0;
        var next = mem[0];
        foreach (var (first, length) in runs)
        {
            Assert.That(first, Is.EqualTo(next), "runs are in order and leave no gap");
            Assert.That(PageCacheAddressing.WindowIndex(first), Is.EqualTo(PageCacheAddressing.WindowIndex(first + length - 1)), $"run at {first}");
            Assert.That((long)length * PagedMMF.PageSize, Is.LessThanOrEqualTo(int.MaxValue));
            covered += length;
            next = first + length;
        }

        Assert.That(covered, Is.EqualTo(mem.Length));
        Assert.That(runs, Has.Count.EqualTo(5), "the pages touch five windows: 3 + 3 × window + 5 pages starting 3 before a boundary");
    }

    [Test]
    public void ARunEnds_WhenMemoryOrFileStopsBeingContiguous()
    {
        // 0,1,2 are contiguous both ways; 3 skips on file; 5 skips in memory.
        var runs = Build([0, 1, 2, 3, 5], [7, 8, 9, 20, 21]);

        Assert.That(runs, Is.EqualTo(new List<(int, int)> { (0, 3), (3, 1), (5, 1) }));
    }

    [Test]
    public void NothingToWrite_NoRun() => Assert.That(Build([], []), Is.Empty);

    /// <summary>
    /// A segment wider than a window, written through <c>SavePages</c> and read back by a fresh cache: every page comes back byte for byte with
    /// a valid checksum. Only meaningful when the cache spans more than one window.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    public unsafe void ASegmentWiderThanAWindow_RoundTripsThroughTheDisk()
    {
        const int cachePages = 1024;
        Assume.That(2 * PagesPerWindow + 8, Is.LessThanOrEqualTo(cachePages / 2),
            "needs a cache of several windows: run with TYPHON_PAGECACHE_WINDOW_PAGES_POW2=7 (the small-window nightly)");

        var name = $"WriteRun_{TestContext.CurrentContext.Test.ID}";
        var sp = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = name;
                options.DatabaseCacheSize = cachePages * PagedMMF.PageSize;
                options.PagesDebugPattern = false;
                options.TestMode = true;
            })
            .BuildServiceProvider();
        try
        {
            sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
            var count = 2 * PagesPerWindow + 8;
            int[] filePages;
            long writes;

            using (var scope = sp.CreateScope())
            {
                var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
                using var guard = EpochGuard.Enter(mmf.EpochManager);
                var cs = mmf.CreateChangeSet();
                var segment = mmf.AllocateSegment(PageBlockType.None, count + 1, cs);
                filePages = new int[count];
                for (var i = 0; i < count; i++)
                {
                    var addr = segment.GetPageAddressExclusive(i + 1, mmf.EpochManager.GlobalEpoch, out var memPageIndex);
                    cs.AddByMemPageIndex(memPageIndex);
                    filePages[i] = segment.Pages[i + 1];
                    NativeMemory.Fill(addr + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize, Pattern(filePages[i]));
                    mmf.UnlatchPageExclusive(memPageIndex);
                }

                var before = mmf.GetMetrics().WrittenOperationCount;
                cs.SaveChanges();
                writes = mmf.GetMetrics().WrittenOperationCount - before;
            }

            Assert.That(writes, Is.GreaterThanOrEqualTo(3), "the pages span three windows, so at least three writes");

            using (var scope = sp.CreateScope())
            {
                var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
                foreach (var fp in filePages)
                {
                    using var guard = EpochGuard.Enter(mmf.EpochManager);
                    Assert.That(mmf.RequestPageEpoch(fp, mmf.EpochManager.GlobalEpoch, out var memPageIndex), Is.True);   // verifies the CRC
                    var raw = new ReadOnlySpan<byte>(mmf.GetMemPageAddress(memPageIndex) + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize);
                    Assert.That(raw.IndexOfAnyExcept(Pattern(fp)), Is.EqualTo(-1), $"page {fp} came back as written");
                }
            }
        }
        finally
        {
            (sp as IDisposable)?.Dispose();
        }
    }

    private static byte Pattern(int filePageIndex) => (byte)(filePageIndex * 37 + 11);
}
