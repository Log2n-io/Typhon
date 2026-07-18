using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

public class ManagedPagedMMFTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;
    private ILogger<ManagedPagedMMFTests> _logger;

    static readonly char[] charToRemove = ['(', ')'];
    private static string CurrentDatabaseName
    {
        get
        {
            var testName = TestContext.CurrentContext.Test.Name;

            foreach (var c in charToRemove)
            {
                testName = testName.Replace(c, '_');
            }
            
            return $"Typhon_{testName}_db";
        }
    }

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
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)dcs;
                options.PagesDebugPattern = false;
            });
        
        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        _logger = _serviceProvider.GetRequiredService<ILogger<ManagedPagedMMFTests>>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    [Test]
    public unsafe void InitializationTest()
    {
        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetService<ManagedPagedMMF>();
            var epochManager = pmmf.EpochManager;

            var metrics = pmmf.GetMetrics();
            var cacheHit = metrics.MemPageCacheHit;

            var guard = EpochGuard.Enter(epochManager);
            pmmf.RequestPageEpoch(0, epochManager.GlobalEpoch, out var memPageIndex);
            metrics = pmmf.GetMetrics();

            // The cache-hit counter is profiler-gated (it sits on the hottest path — see PagedMMF.FetchPageToMemory).
            // With the profiler active it must increment on this hit; with it off the increment is JIT-folded away, so
            // the counter must stay flat — that flatness IS the zero-cost guarantee the gate exists to provide.
            if (TelemetryConfig.ProfilerActive)
            {
                Assert.That(metrics.MemPageCacheHit, Is.GreaterThan(cacheHit), "hit counter must increment while the profiler is active");
            }
            else
            {
                Assert.That(metrics.MemPageCacheHit, Is.EqualTo(cacheHit), "hit counter must stay flat (zero cost) while the profiler is off");
            }
            guard.Dispose();
        }

        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetService<ManagedPagedMMF>();
            var epochManager = pmmf.EpochManager;

            var guard = EpochGuard.Enter(epochManager);
            pmmf.RequestPageEpoch(0, epochManager.GlobalEpoch, out var memPageIndex);
            ref var h = ref *(RootFileHeader*)(pmmf.GetMemPageAddress(memPageIndex) + PagedMMF.PageBaseHeaderSize);
            Assert.That(h.HeaderSignatureString, Is.EqualTo(ManagedPagedMMF.HeaderSignature));
            Assert.That(h.DatabaseNameString, Is.EqualTo(CurrentDatabaseName));
            guard.Dispose();
        }
    }
    
    [Test]
    public void FindNextUnsetL0Test()
    {
        var bitCount = 64 * 64 * 64 * 10;
        var pageCount = (int)Math.Ceiling((double)bitCount / (PagedMMF.PageRawDataSize * 8));
        
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();

        var seg = pmmf.AllocateSegment(PageBlockType.None, pageCount);

        var c = new ManagedPagedMMF.BitmapL3(seg);

        var index = -1;
        long mask = 0L;

        //c.Set(0);
        c.SetL0(1);
        c.SetL0(2);

        c.FindNextUnsetL0(ref index, ref mask);
        Assert.That(index, Is.EqualTo(0));

        c.FindNextUnsetL0(ref index, ref mask);
        Assert.That(index, Is.EqualTo(3));

        var offset = 0;
        var range = 64 * 64 * 64 + 64 * 64 + 1;
        for (int i = offset; i < (offset + range); i++)
        {
            c.SetL0(i);
        }

        index = -1;
        c.FindNextUnsetL0(ref index, ref mask);
        Assert.That(index, Is.EqualTo(range));

        offset = 64 * 64;
        range = 1;
        for (int i = offset; i < (offset + range); i++)
        {
            c.ClearL0(i);
        }

        index = -1;
        c.FindNextUnsetL0(ref index, ref mask);
        Assert.That(index, Is.EqualTo(offset));

        pmmf.DeleteSegment(seg);
    }
    
    [Test]
    public void FindNextUnsetL1Test()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        
        var bitCount = 64 * 64 * 64 * 10;
        var pageCount = (int)Math.Ceiling((double)bitCount / (PagedMMF.PageRawDataSize * 8));

        var seg = pmmf.AllocateSegment(PageBlockType.None, pageCount);

        var l3 = new ManagedPagedMMF.BitmapL3(seg);

        var index = -1;
        long mask = 0L;
        l3.FindNextUnsetL1(ref index, ref mask);
        Assert.That(index, Is.EqualTo(0));

        l3.SetL0(0);
        l3.SetL0(128);
        index = -1;
        mask = 0L;
        l3.FindNextUnsetL1(ref index, ref mask);
        Assert.That(index, Is.EqualTo(1));

        l3.FindNextUnsetL1(ref index, ref mask);
        Assert.That(index, Is.EqualTo(3));
        
        pmmf.DeleteSegment(seg);
    }

    [Test]
    public void SetL1Test()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();

        var bitCount = 64 * 64 * 64 * 10;
        var pageCount = (int)Math.Ceiling((double)bitCount / (PagedMMF.PageRawDataSize * 8));

        var seg = pmmf.AllocateSegment(PageBlockType.None, pageCount);

        var c = new ManagedPagedMMF.BitmapL3(seg);

        c.SetL1(0);
        Assert.That(c.IsSet(0), Is.EqualTo(true));
        Assert.That(c.IsSet(63), Is.EqualTo(true));
        Assert.That(c.IsSet(64), Is.EqualTo(false));

        var index = -1;
        long mask = 0L;
        c.FindNextUnsetL0(ref index, ref mask);
        Assert.That(index, Is.EqualTo(64));
            
        index = -1;
        mask = 0L;
        c.FindNextUnsetL1(ref index, ref mask);
        Assert.That(index, Is.EqualTo(1));
        c.FindNextUnsetL1(ref index, ref mask);
        Assert.That(index, Is.EqualTo(2));
        
        pmmf.DeleteSegment(seg);
    }
    
    [Test]
    public void CreateSegment()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var s0 = pmmf.AllocateSegment(PageBlockType.None, 10);
        var s1 = pmmf.AllocateSegment(PageBlockType.None, 50);
        pmmf.DeleteSegment(s0);
        var s2 = pmmf.AllocateSegment(PageBlockType.None, 100);

        var s3 = pmmf.AllocateSegment(PageBlockType.None, 100);

        pmmf.DeleteSegment(s2);
        pmmf.DeleteSegment(s1);
        pmmf.DeleteSegment(s3);
    }
    
    static object[] Cases = {
        new TestCaseData(5000).SetProperty("MemPageCount", 6000)
    };

    [Test]
    [TestCaseSource(nameof(Cases))]
    public unsafe void CreateAndLoadBigSegment(int segmentLength)
    {
        int rootSegmentIndex;
        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            using var guard = EpochGuard.Enter(pmmf.EpochManager);
            var epoch = pmmf.EpochManager.GlobalEpoch;

            var cs = pmmf.CreateChangeSet();

            var s0 = pmmf.AllocateSegment(PageBlockType.None, segmentLength, cs);

            for (int i = 0; i < segmentLength; i++)
            {
                var addr = s0.GetPageAddressExclusive(i, epoch, out var memPageIdx);
                cs.AddByMemPageIndex(memPageIdx);
                var root = (addr[0] & (byte)PageBlockFlags.IsLogicalSegmentRoot) != 0;
                var offset = root ? LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength : 0;
                var rd = new Span<int>(addr + PagedMMF.PageHeaderSize + offset, (PagedMMF.PageRawDataSize - offset) / sizeof(int));
                // Directory-only root (v4): the root page carries no data (offset == PageRawDataSize → empty span). Only data
                // pages (segment page 1+) round-trip user data.
                if (rd.Length > 0)
                {
                    rd[0] = i;
                    rd[^1] = i + 1000;
                }
                pmmf.UnlatchPageExclusive(memPageIdx);
            }
            cs.SaveChanges();
            rootSegmentIndex = s0.RootPageIndex;
        }

        {
            using var scope = _serviceProvider.CreateScope();
            var mpmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            using var guard = EpochGuard.Enter(mpmmf.EpochManager);
            var epoch = mpmmf.EpochManager.GlobalEpoch;

            var s0 = mpmmf.GetSegment(rootSegmentIndex);

            for (int i = 0; i < segmentLength; i++)
            {
                var addr = s0.GetPageAddress(i, epoch, out _);
                var root = (addr[0] & (byte)PageBlockFlags.IsLogicalSegmentRoot) != 0;
                var offset = root ? LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength : 0;
                var rd = new Span<int>(addr + PagedMMF.PageHeaderSize + offset, (PagedMMF.PageRawDataSize - offset) / sizeof(int));
                // Directory-only root (v4): the root page carries no data — nothing to verify on it.
                if (rd.Length == 0)
                {
                    continue;
                }
                Assert.That(rd[0], Is.EqualTo(i));
                Assert.That(rd[^1], Is.EqualTo(i + 1000));
            }
        }
    }

    [Test]
    public void OccupancyMapSaveTest()
    {
        int[] s0Pages;
        {
            using var scope = _serviceProvider.CreateScope();
            var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            
            var cs = mmf.CreateChangeSet();
            var s0 = mmf.AllocateSegment(PageBlockType.None, 10, cs);
            s0Pages = s0.Pages.ToArray();
            cs.SaveChanges();
        }
        
        {
            using var scope = _serviceProvider.CreateScope();
            var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

            var newS0 = mmf.AllocateSegment(PageBlockType.None, 10);

            int[] newS0Array = newS0.Pages.ToArray();
            Assert.That(newS0Array.All(p => p != 0 && p != 1), Is.True);       // The returned pages can't be 0 (header) or 1 (occupancy segment)
            Assert.That(newS0Array.All(p => !s0Pages.Contains(p)), Is.True);            
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ChunkA
    {
        public int A;
        public int B;
        public int C;
        public int D;
    }

    [Test]
    unsafe public void ChunkBasedSegmentTest()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var s0 = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(ChunkA));

        using var mo = s0.AllocateChunks(2000, false);
        var depth = epochManager.EnterScope();
        try
        {
            var ca = s0.CreateChunkAccessor();

            ref var obj = ref ca.GetChunk<ChunkA>(0);
            obj.A = -1;
            obj.B = -1;
            obj.C = -1;
            obj.D = -1;

            obj = ca.GetChunk<ChunkA>(1);
            obj.A = 1;

            obj = ca.GetChunk<ChunkA>(500);
            obj.A = 1;

            ca.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void VariableSizedBufferSegmentTest()
    {
        const int itemCount = 1024;

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var s = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 64);
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = s.CreateChunkAccessor();

            var vsb = new VariableSizedBufferSegment<long, PersistentStore>(s);

            var id0 = vsb.AllocateBuffer(ref accessor);

            for (int i = 0; i < itemCount; i++)
            {
                vsb.AddElement(id0, 1234, ref accessor);
            }

            var loopCount = 0;
            var ba = vsb.GetReadOnlyAccessor(id0);
            do
            {
                var elements = ba.ReadOnlyElements;
                var c = elements.Length;
                for (int i = 0; i < c; i++)
                {
                    Assert.That(elements[i], Is.EqualTo(1234));
                    ++loopCount;
                }
            } while (ba.NextChunk());
            Assert.That(loopCount, Is.EqualTo(itemCount));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void VariableSizedBuffer_CloneBuffer([Values(1, 34, 67, 129)] int seeds)
    {
        var rand = new Random(seeds);
        const int itemCount = 1024;

        // Services
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var s = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 64);
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = s.CreateChunkAccessor();

            // VSBS
            var vsb = new VariableSizedBufferSegment<int, PersistentStore>(s);

            // Buffer
            var id0 = vsb.AllocateBuffer(ref accessor);

            // Add the items, record their location and value
            var ids = new List<(int, int)>(itemCount);
            var co = 0;
            for (int i = 0; i < itemCount; i++)
            {
                co++;
                var value = rand.Next();
                ids.Add((vsb.AddElement(id0, value, ref accessor), value));
            }

            // Delete 1/16 of the items to create fragmentation
            const int deleteCount = itemCount >> 4;
            for (int i = 0; i < deleteCount; i++)
            {
                var itemIndex = rand.Next(0, itemCount - i);
                var record = ids[itemIndex];
                ids.RemoveAt(itemIndex);
                vsb.DeleteElement(id0, record.Item1, record.Item2, ref accessor);
            }

            // Clone the buffer
            var id1 = vsb.CloneBuffer(id0, ref accessor);

            var hashset = new HashSet<int>();
            hashset.EnsureCapacity(itemCount);
            hashset.UnionWith(ids.Select(item => item.Item2));

            var loopCount = 0;
            var ba = vsb.GetReadOnlyAccessor(id1);
            do
            {
                var elements = ba.ReadOnlyElements;
                var c = elements.Length;
                for (int i = 0; i < c; i++)
                {
                    Assert.That(hashset.Contains(elements[i]), Is.True);
                    ++loopCount;
                }
            } while (ba.NextChunk());
            Assert.That(loopCount, Is.EqualTo(itemCount - deleteCount));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }
    
    [Test]
    public void VariableSizedBufferSegmentDeleteTest()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var s = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 64);
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = s.CreateChunkAccessor();

            var vsb = new VariableSizedBufferSegment<long, PersistentStore>(s);

            var id0 = vsb.AllocateBuffer(ref accessor);
            var elIdList = new int[15];

            // 15 is spread into 3 chunks: 4, 7, 4 (root chunk has fewer due to header overhead)
            for (int i = 0; i < 15; i++)
            {
                elIdList[i] = vsb.AddElement(id0, i, ref accessor);
            }

            // Delete all the elements of the second chunk (values 4-10)
            for (int i = 4; i < 11; i++)
            {
                Assert.That(vsb.DeleteElement(id0, elIdList[i], i, ref accessor), Is.Not.EqualTo(-1));
            }

            // Trigger an enumeration that will remove the second chunk from the stored list and put it in the free list
            {
                int count = 0;
                int hops = 0;
                using var ba = vsb.GetReadOnlyAccessor(id0);
                do
                {
                    count += ba.ReadOnlyElements.Length;
                    ++hops;
                } while (ba.NextChunk());

                Assert.That(count, Is.EqualTo(8));
                Assert.That(hops, Is.EqualTo(2));
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }
    
    private const string Muse =
        @"Home, It's becoming a killing field
There's a cross hair locked on my heart
With no recourse and there's no one behind the wheel
Hell fire, You're wiping me out, killed by
Drones, (killed by)
Drones(killed by)
You rule with lies and deceit
And the world is on your side
'Cause you've got the CIA, babe
But all you've done is brutalise
Drones!
War, war just moved up a gear
I don't think I can handle the truth
I'm just a pawn
And we're all expendable
Incidentally
Electronically erased
By your
Drones, (killed by)
Drones(killed by)
You kill by remote control
The world is on your side
You've got reapers and hawks babe
Now I am radicalized
Drones!
You rule with lies and deceit
And the world is on your side
'Cause you've got the CIA, babe
But all you've done is brutalise
You kill by remote control
The world is on your side
You've got reapers and hawks babe
Now I am radicalized
Here come the drones!
Here come the drones!
Here come the drones!";

    [Test]
    public void StringTableTest()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var s = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 64);

        var depth = epochManager.EnterScope();
        try
        {
            var st = new StringTableSegment<PersistentStore>(s, epochManager);

            var id = st.StoreString(Muse);

            string ns = st.LoadString(id);

            Assert.That(ns, Is.EqualTo(Muse));

            st.DeleteString(id);
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    public class LockStore
    {
        public int A;
        public int B;
        public int R;
    }

    [Test]
    public void LockTest()
    {
        const int iterationCount = 2_000; // 32 threads × 2 000 = 64 000 ops — ample for race detection.

        var s = new LockStore();

        var taskList = new List<Task>();
        var rwsl = new AccessControlSmall();
        var r = new Random(DateTime.UtcNow.Millisecond);

        for (int i = 0; i < 32; i++)
        {
            var t = Task.Run(() =>
            {
                Thread.CurrentThread.Name = $"UnitTest_{Environment.CurrentManagedThreadId}";

                for (int j = 0; j < iterationCount; j++)
                {
                    var write = r.Next(0, 100) < 25;

                    // Write use case
                    if (write)
                    {
                        rwsl.EnterExclusiveAccess(ref TestWaitContext.Default);

                        s.A = r.Next(0, 100000);
                        s.B = r.Next(0, 100000);
                        s.R = s.A + s.B;

                        rwsl.ExitExclusiveAccess();
                    }

                    // Read use case
                    else
                    {

                        rwsl.EnterSharedAccess(ref TestWaitContext.Default);

                        Assert.That(s.R, Is.EqualTo(s.A + s.B));

                        rwsl.ExitSharedAccess();

                    }
                }
            });

            taskList.Add(t);
        }

        Task.WaitAll(taskList.ToArray());

        Assert.That(rwsl.SharedUsedCounter, Is.EqualTo(0));
    }

    [Test, CancelAfter(30_000)]
    [Property("MemPageCount", 66000)]       // Must be larger than maxBeforeGrow defined below (all pages dirty until SaveChanges)
    public void GrowOccupancyMapTest()
    {
        // Directory-only root (v4): the occupancy segment's FIRST DATA page governs the full PageRawDataSize × 8 = 64000
        // file pages (the directory-only root governs none), so the occupancy map grows once total allocations approach
        // 64000 — up from 48000 under the old root-holds-bitmap layout. Size the segment just under that boundary; the
        // subsequent +10 grow (plus directory/twin overhead) pushes total allocations past it and forces the occupancy grow.
        const int maxBeforeGrow =
            (PagedMMF.PageRawDataSize * 8) - ManagedPagedMMF.InitialReservedPageCount - 25;
        
        int rootSegmentIndex, segmentTotalLength;
        ReadOnlySpan<int> segmentPages;

        {
            Stopwatch sw = Stopwatch.StartNew();
            
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            
            var cs = pmmf.CreateChangeSet();

            var s0 = pmmf.AllocateSegment(PageBlockType.None, maxBeforeGrow, cs);
            rootSegmentIndex = s0.RootPageIndex;
            
            sw.Stop();
            _logger.LogInformation("Segment allocated in {Elapsed} ms", sw.ElapsedMilliseconds);
            
            sw.Restart();
            cs.SaveChanges();
            sw.Stop();
            _logger.LogInformation("Save segment of {Size} in {Elapsed} ms", (s0.Length * PagedMMF.PageSize).FriendlySize(), sw.ElapsedMilliseconds);
            
            // Grow the segment to trigger the occupancy map grow
            s0.Grow(s0.Length + 10, true, cs);
            cs.SaveChanges();

            segmentPages = s0.Pages;
            segmentTotalLength = s0.Length;
            
        }
        
        {
            using var scope = _serviceProvider.CreateScope();
            var mpmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

            var s0 = mpmmf.GetSegment(rootSegmentIndex);
            Assert.That(s0.Length, Is.EqualTo(segmentTotalLength));

            // Check the pages of the segment were loaded correctly
            for (int i = 0; i < s0.Pages.Length; i++)
            {
                Assert.That(segmentPages[i], Is.EqualTo(s0.Pages[i]));
            }
        }
    }

    [Test]
    [Property("MemPageCount", 2600)]        // 2510 data pages + map/bitmap overhead; dirty pages are non-evictable until SaveChanges()
    public void LogicalSegmentGrowTest()
    {
        // v4 directory-only root: the root directory holds RootHeaderIndexSectionCount (2000) page-index entries; overflow spills
        // to map-extension directory pages of NextHeadersIndexSectionCount (2000) entries each.
        const int initialSize = 10;         // root directory = 10 entries
        const int firstGrowSize = 510;      // root directory = 510 entries (all fit in the 2000-entry root; no map extension)
        const int secondGrowSize = 2510;    // root directory = 2000 entries, Map #1 (new) = 510 entries
        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            
            var cs = pmmf.CreateChangeSet();

            var s0 = pmmf.AllocateSegment(PageBlockType.None, initialSize, cs);
            s0.Grow(firstGrowSize, true, cs);
            cs.SaveChanges();
            
            s0.Grow(secondGrowSize, true, cs);
            cs.SaveChanges();
        }

    }

    /// <summary>
    /// Verifies that AllocateChunk auto-grows the segment when capacity is exhausted.
    /// Previously this was a bug confirmation test, but auto-growth is now implemented.
    /// </summary>
    [Test]
    public void AllocateChunk_CapacityExhausted_AutoGrows()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        
        // Create a small segment with just 1 page to limit initial capacity
        // Using a large stride (1024 bytes) to minimize chunks per page
        const int stride = 1024;
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 1, stride);
        
        var initialCapacity = segment.ChunkCapacity;
        var freeCount = segment.FreeChunkCount;
        
        _logger.LogInformation("Initial segment capacity: {Capacity}, Free: {Free}, Allocated: {Allocated}", 
            initialCapacity, freeCount, segment.AllocatedChunkCount);
        
        // Allocate all available chunks (FreeChunkCount, since chunk 0 is already reserved)
        var allocatedChunks = new List<int>();
        for (int i = 0; i < freeCount; i++)
        {
            var chunkId = segment.AllocateChunk(false);
            
            _logger.LogInformation("Allocated chunk {ChunkId} (iteration {i}), Free remaining: {Free}", 
                chunkId, i, segment.FreeChunkCount);
            
            // Chunk ID 0 is reserved as "null" sentinel - should never be returned
            Assert.That(chunkId, Is.Not.EqualTo(0), $"Chunk 0 is reserved and should never be allocated (iteration {i})");
            Assert.That(chunkId, Is.GreaterThan(0), $"Expected valid chunk ID > 0, got {chunkId}");
            
            allocatedChunks.Add(chunkId);
        }
        
        // Verify all chunks are now allocated
        _logger.LogInformation("After allocation loop: Capacity={Capacity}, Allocated={Allocated}, Free={Free}",
            segment.ChunkCapacity, segment.AllocatedChunkCount, segment.FreeChunkCount);
        
        Assert.That(segment.FreeChunkCount, Is.EqualTo(0), "All chunks should be allocated");
        Assert.That(segment.AllocatedChunkCount, Is.EqualTo(initialCapacity), "Allocated count should equal initial capacity");
        
        // Now allocate beyond initial capacity - should trigger auto-growth
        var overflowChunkId = segment.AllocateChunk(false);
        
        _logger.LogInformation("After auto-growth: ChunkId={ChunkId}, Capacity={Capacity}, Free={Free}", 
            overflowChunkId, segment.ChunkCapacity, segment.FreeChunkCount);
        
        // Verify auto-growth worked correctly
        // 1. Should return a valid chunk ID
        Assert.That(overflowChunkId, Is.GreaterThan(0), "Auto-growth should return valid chunk ID > 0");
        Assert.That(overflowChunkId, Is.Not.EqualTo(0), "Should not return reserved sentinel 0");
        
        // 2. Capacity should have increased
        Assert.That(segment.ChunkCapacity, Is.GreaterThan(initialCapacity), 
            "Segment capacity should have grown");
        
        // 3. FreeChunkCount should be valid (non-negative)
        Assert.That(segment.FreeChunkCount, Is.GreaterThanOrEqualTo(0),
            "FreeChunkCount should remain non-negative after growth");
        
        // 4. The new chunk ID should be within the new capacity
        Assert.That(overflowChunkId, Is.LessThan(segment.ChunkCapacity),
            "New chunk ID should be within expanded capacity");
        
        allocatedChunks.Add(overflowChunkId);
        
        // Verify we can continue allocating after growth
        var anotherChunkId = segment.AllocateChunk(false);
        Assert.That(anotherChunkId, Is.GreaterThan(0), "Should be able to allocate more chunks after growth");
        
        _logger.LogInformation("Successfully allocated {Count} chunks with auto-growth", allocatedChunks.Count + 1);
    }
}