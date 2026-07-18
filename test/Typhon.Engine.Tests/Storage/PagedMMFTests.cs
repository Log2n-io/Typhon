using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

class PagedMMFTests
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

#if DEBUG
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .CreateLogger();
#endif

        var serviceCollection = new ServiceCollection();
        _serviceCollection = serviceCollection;
        _serviceCollection
            .AddLogging(builder =>
            {
#if DEBUG
                // builder.AddSerilog(dispose: true);
#endif
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

    private const int CreateFillPagesThenReadThemMemPageCount = 512;
    [Test]
    [Property("MemPageCount", CreateFillPagesThenReadThemMemPageCount)]
    unsafe public void CreateFillPagesThenReadThem()
    {
        const int pageCount = 128;
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetService<PagedMMF>();

            Assert.That(pmmf.GetMetrics().FreeMemPageCount, Is.EqualTo(CreateFillPagesThenReadThemMemPageCount));

            // Fill pages with data
            {
                var now = DateTime.UtcNow;
                var cs = pmmf.CreateChangeSet();
                var guard = EpochGuard.Enter(epochManager);
                var currentEpoch = epochManager.GlobalEpoch;
                for (int i = 0; i < pageCount; i++)
                {
                    pmmf.RequestPageEpoch(i, currentEpoch, out int memPageIndex);
                    pmmf.TryLatchPageExclusive(memPageIndex);
                    var addr = pmmf.GetMemPageAddress(memPageIndex);
                    var ispan = (int*)(addr + PagedMMF.PageHeaderSize);
                    ispan[0] = i; // Set first element
                    ispan[PagedMMF.PageRawDataSize / sizeof(int) - 1] = i; // Set last element
                    cs.AddByMemPageIndex(memPageIndex);
                    pmmf.UnlatchPageExclusive(memPageIndex);
                }
                guard.Dispose();
                Console.WriteLine($"Average time to fill one page {((DateTime.UtcNow - now).TotalSeconds / pageCount).FriendlyTime()}");

                now = DateTime.UtcNow;
                cs.SaveChanges();

                Console.WriteLine($"Average time to save one page {((DateTime.UtcNow - now).TotalSeconds / pageCount).FriendlyTime()}");
            }

            Assert.That(pmmf.GetMetrics().FreeMemPageCount, Is.EqualTo(CreateFillPagesThenReadThemMemPageCount));
        }

        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetService<PagedMMF>();

            Assert.That(pmmf.GetMetrics().FreeMemPageCount, Is.EqualTo(CreateFillPagesThenReadThemMemPageCount));
            long totalRequest = 0;
            long totalAccess = 0;

            // The file exists and has content, load it
            {
                var guard = EpochGuard.Enter(epochManager);
                var currentEpoch = epochManager.GlobalEpoch;
                for (int i = 0; i < pageCount; i++)
                {
                    var now = DateTime.UtcNow;

                    pmmf.RequestPageEpoch(i, currentEpoch, out int memPageIndex);
                    totalRequest += (DateTime.UtcNow - now).Ticks;

                    now = DateTime.UtcNow;
                    var addr = pmmf.GetMemPageAddress(memPageIndex);
                    var content = (int*)(addr + PagedMMF.PageHeaderSize);
                    totalAccess += (DateTime.UtcNow - now).Ticks;

                    Assert.That(content[0], Is.EqualTo(i), $"Page {i} content mismatch after reset.");
                    Assert.That(content[PagedMMF.PageRawDataSize / sizeof(int) - 1], Is.EqualTo(i), $"Page {i} content mismatch after reset.");
                }
                guard.Dispose();
            }

            Assert.That(pmmf.GetMetrics().FreeMemPageCount, Is.EqualTo(CreateFillPagesThenReadThemMemPageCount));
            Console.WriteLine($"Average Request Time: {TimeSpan.FromTicks(totalRequest / pageCount).TotalSeconds.FriendlyTime()}, Average Access Time: {TimeSpan.FromTicks(totalAccess / pageCount).TotalSeconds.FriendlyTime()}");
        }
    }

    private static List<int> GenerateRandomAccess(int min, int max, int count=0)
    {
        int inputCount = max - min + 1;
        var input = new List<int>(inputCount);
        for (int i = min; i <= max; i++)
        {
            input.Add(i);
        }
        var r = new Random(DateTime.UtcNow.Millisecond);

        if (count == 0)
        {
            count = inputCount;
        }

        var res = new List<int>(inputCount);
            
        for (int i = 0; i < count; i++)
        {
            var p = r.Next(input.Count);
            res.Add(input[p]);
            input.RemoveAt(p);
        }

        return res;
    }

    /// <summary>
    /// Modifying multiple consecutive pages should trigger a single write on disk
    /// </summary>
    /// <remarks>
    /// This statement is valid only for an unfragmented/empty file (two consecutive DiskPages should have consecutive MemPages) and can't be kept
    ///  when the memory cache is being pressured.
    /// </remarks>
    [Test]
    unsafe public void SequentialWrites()
    {
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        // Write
        {
            using var scope = _serviceProvider.CreateScope();
            using var pmmf = scope.ServiceProvider.GetService<PagedMMF>();

            var metrics = pmmf.GetMetrics();
            var pageWrittenCount = metrics.PageWrittenToDiskCount;
            var writtenIOCount = metrics.WrittenOperationCount;
            var cs = pmmf.CreateChangeSet();

            var guard = EpochGuard.Enter(epochManager);
            var currentEpoch = epochManager.GlobalEpoch;

            pmmf.RequestPageEpoch(10, currentEpoch, out int mp1);    // The page 10, 11 and 12 will also be consecutive in the memory cache,
            pmmf.RequestPageEpoch(11, currentEpoch, out int mp2);    //  allowing a single write
            pmmf.RequestPageEpoch(12, currentEpoch, out int mp3);
            pmmf.RequestPageEpoch(14, currentEpoch, out int mp4);

            pmmf.TryLatchPageExclusive(mp1);
            var a = (int*)pmmf.GetMemPageAddress(mp1);
            a[0] = 10;
            cs.AddByMemPageIndex(mp1);
            pmmf.UnlatchPageExclusive(mp1);

            pmmf.TryLatchPageExclusive(mp2);
            a = (int*)pmmf.GetMemPageAddress(mp2);
            a[0] = 11;
            cs.AddByMemPageIndex(mp2);
            pmmf.UnlatchPageExclusive(mp2);

            pmmf.TryLatchPageExclusive(mp3);
            a = (int*)pmmf.GetMemPageAddress(mp3);
            a[0] = 12;
            cs.AddByMemPageIndex(mp3);
            pmmf.UnlatchPageExclusive(mp3);

            pmmf.TryLatchPageExclusive(mp4);
            a = (int*)pmmf.GetMemPageAddress(mp4);
            a[0] = 14;
            cs.AddByMemPageIndex(mp4);
            pmmf.UnlatchPageExclusive(mp4);

            guard.Dispose();

            cs.SaveChanges();
            Assert.That(metrics.PageWrittenToDiskCount, Is.EqualTo(pageWrittenCount+4));
            Assert.That(metrics.WrittenOperationCount, Is.EqualTo(writtenIOCount+2));
        }

        // Check read
        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetService<PagedMMF>();

            var guard = EpochGuard.Enter(epochManager);
            var currentEpoch = epochManager.GlobalEpoch;

            pmmf.RequestPageEpoch(10, currentEpoch, out int mp1);
            pmmf.RequestPageEpoch(11, currentEpoch, out int mp2);
            pmmf.RequestPageEpoch(12, currentEpoch, out int mp3);
            pmmf.RequestPageEpoch(14, currentEpoch, out int mp4);

            var a = (int*)pmmf.GetMemPageAddress(mp1);
            Assert.That(a[0], Is.EqualTo(10), "Page 10 should be 10");

            var b = (int*)pmmf.GetMemPageAddress(mp2);
            Assert.That(b[0], Is.EqualTo(11), "Page 11 should be 11");

            var c = (int*)pmmf.GetMemPageAddress(mp3);
            Assert.That(c[0], Is.EqualTo(12), "Page 12 should be 12");

            var d = (int*)pmmf.GetMemPageAddress(mp4);
            Assert.That(d[0], Is.EqualTo(14), "Page 14 should be 14");

            guard.Dispose();
        }
    }

    struct OPInfo
    {
        public int PageId;
        public bool ReadOnly;
        public int ExpectedValue;
    }

    [Test]
    [Property("MemPageCount", 1024)]
    [CancelAfter(10000)]
    unsafe public void ReliabilityTest()
    {
        var cacheFactor = 0.75f;   // This is nasty...we are going to have a lot of cache miss...
        var frameCount = 10;       // 10 frames × 500 ops still produces several rounds of cache eviction.
        var opsPerFrame = 500;
        var readWriteRatio = 0.75f;
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        // Size configured in the Property attribute above, right now it's 8 pages cached, which is vicious because
        //  my actual computer has more thread, which means multiple thread compete for the same memory page.
        var cacheSize = _serviceProvider.GetRequiredService<IOptions<PagedMMFOptions>>().Value.DatabaseCacheSize;
        var pagesCount = (int)(cacheSize * cacheFactor) / PagedMMF.PageSize;
        var coreCount = Environment.ProcessorCount / 2;
        pagesCount = pagesCount / coreCount * coreCount;                        // Make sure we have a multiple of the core count

        // Generate IO ops for all the frames
        var frames = new List<List<OPInfo>>(frameCount);

        // Initialize the Pages access scenario
        var rand = new Random(DateTime.UtcNow.Millisecond);
        var range = (int)(1.0f / (1.0f - readWriteRatio));
        var readCut = (int)(readWriteRatio / (1.0f - readWriteRatio));
        var values = new Dictionary<int, int>(pagesCount);
        for (int i = 0; i < pagesCount; i++)
        {
            values.Add(i, i);
        }

        int trueOpsCount = Math.Min(opsPerFrame, pagesCount);
        for (int i = 0; i < frameCount; i++)
        {
            var ops = new List<OPInfo>(opsPerFrame);
            int opsCount = trueOpsCount;
            var ioPages = GenerateRandomAccess(0, pagesCount-1, opsCount);
            for (int j = 0; j < opsCount; j++)
            {
                var ro = rand.Next(0, range) < readCut;
                var pageId = ioPages[j];
                if (ro == false)
                {
                    ++values[pageId];
                }
                var curValue = values[pageId];
                ops.Add(new OPInfo{ PageId = pageId, ReadOnly = ro, ExpectedValue = curValue});
            }
            frames.Add(ops);
        }

        var ranges = new ConcurrentBag<(int, int)>();
        {
            var heapCount = pagesCount / coreCount;
            var remaining = pagesCount;
            for (int i = 0; i < coreCount; i++)
            {
                ranges.Add((i * heapCount, (remaining < heapCount) ? remaining : heapCount));
                remaining -= heapCount;
            }
        }

        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetService<PagedMMF>();

            // Setup initial value of each page
            var sw = new Stopwatch();
            sw.Start();

            Parallel.ForEach(Enumerable.Range(0, coreCount), i =>
            {
                if (ranges.TryTake(out var operation) == false)
                {
                    return;
                }

                var firstPageIndex = operation.Item1;
                var heapCount = operation.Item2;

                var guard = EpochGuard.Enter(epochManager);
                var currentEpoch = epochManager.GlobalEpoch;
                var cs = pmmf.CreateChangeSet();
                for (int j = 0; j < heapCount; j++)
                {
                    var pageIndex = firstPageIndex + j;
                    pmmf.RequestPageEpoch(pageIndex, currentEpoch, out int memPageIndex);
                    pmmf.TryLatchPageExclusive(memPageIndex);
                    cs.AddByMemPageIndex(memPageIndex);

                    var dest = (int*)pmmf.GetMemPageAddress(memPageIndex);
                    dest[0] = pageIndex;
                    pmmf.UnlatchPageExclusive(memPageIndex);
                }
                guard.Dispose();
                cs.SaveChanges();
            });

            var di = pmmf.GetMetrics();
            Console.WriteLine($"Generated file in {sw.ElapsedMilliseconds}ms, Write counts: {di.PageWrittenToDiskCount}, Generated a total of {frameCount*trueOpsCount} Pages operations");
        }

        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetService<PagedMMF>();

            // Check the initial page of each page
            {
                var guard = EpochGuard.Enter(epochManager);
                var currentEpoch = epochManager.GlobalEpoch;
                for (int i = 0; i < pagesCount; i++)
                {
                    pmmf.RequestPageEpoch(i, currentEpoch, out int memPageIndex);

                    var dest = (int*)pmmf.GetMemPageAddress(memPageIndex);
                    var localI = i;
                    Assert.That(dest[0], Is.EqualTo(i), () => $"Bad DiskPageId {localI}");
                }
                guard.Dispose();
            }

            // Simulate accesses
            for (int curF = 0; curF < frameCount; curF++)
            {
                var curFrame = curF;

                // Console.WriteLine($"\r\n************** Simulating Frame {curFrame} ************** \r\n");
                var frameInfo = frames[curF];
                Parallel.ForEach(frameInfo, info =>
                {
                    var ro = info.ReadOnly;
                    if (ro)
                    {
                        // Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] Check Page {info.PageId} is {info.ExpectedValue}");

                        var guard = EpochGuard.Enter(epochManager);
                        var currentEpoch = epochManager.GlobalEpoch;
                        pmmf.RequestPageEpoch(info.PageId, currentEpoch, out int memPageIndex);
                        int actual = ((int*)pmmf.GetMemPageAddress(memPageIndex))[0];

                        // _logger.LogCritical("Check Page {PageId} has Value {ExpectedValue} and has {value}", info.PageId, info.ExpectedValue, actual);
                        Assert.That(actual, Is.EqualTo(info.ExpectedValue), $"Frame {curFrame}, Page {info.PageId} should be {info.ExpectedValue} but is {actual}");

                        guard.Dispose();
                    }
                    else
                    {
                        var guard = EpochGuard.Enter(epochManager);
                        var currentEpoch = epochManager.GlobalEpoch;
                        var cs = pmmf.CreateChangeSet();
                        // Console.WriteLine($"[{Thread.CurrentThread.ManagedThreadId}] Page {info.PageId} bumped to {info.ExpectedValue}");

                        pmmf.RequestPageEpoch(info.PageId, currentEpoch, out int memPageIndex);
                        pmmf.TryLatchPageExclusive(memPageIndex);
                        cs.AddByMemPageIndex(memPageIndex);
                        var pa = (int*)pmmf.GetMemPageAddress(memPageIndex);
                        ++pa[0]; // Bump the value
                        var actual = pa[0];

                        // _logger.LogCritical("Bump Page {PageId} to {value}, expected {ExpectedValue}", info.PageId, *pa, info.ExpectedValue);
                        Assert.That(actual, Is.EqualTo(info.ExpectedValue), $"Frame {curFrame}, Page {info.PageId} should be {info.ExpectedValue} but is {actual}");

                        pmmf.UnlatchPageExclusive(memPageIndex);
                        guard.Dispose();
                        cs.SaveChanges();
                    }
                });
            }
        }
    }
}