// unset

using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;

namespace Typhon.Benchmark;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 3)]
[BenchmarkCategory("Collections")]
public class ConcurrentBitmapBenchmark
{
    private const int BitSize = 1024 * 1024;
    private const int SampleCount = 100_000_000;

    private readonly int[] _samples;

    private ConcurrentBitmap _concurrent;
    private ConcurrentBitmapL3Any _concurrentL3;
    private ConcurrentBitmapL3All _concurrentL3All;
    private BitmapL3Any _bitmapL3;

    private BitmapL3Any _bitmapL3Filled;

    // DI services for benchmarks
    private IServiceProvider _serviceProvider;
    private IResourceRegistry _resourceRegistry;
    private IMemoryAllocator _memoryAllocator;

    public ConcurrentBitmapBenchmark()
    {

        _samples = new int[SampleCount];

        var r = new Random(DateTime.UtcNow.Millisecond);
        for (int i = 0; i < SampleCount; i++)
        {
            _samples[i] = r.Next(0, BitSize);
        }
    }
    struct PageActivityInfo
    {
        public int AllocatedTime;
        public int HitCounter;
    }

    private PageActivityInfo[] _aList;
    private Memory<PageActivityInfo> _aMem;
    private int[] _iList;
    private int[] _vList;
    private int _pageCount;
    private PagedMMF.PageInfo[] _pages;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Initialize DI services
        _serviceProvider = new ServiceCollection()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .BuildServiceProvider();
        _resourceRegistry = _serviceProvider.GetRequiredService<IResourceRegistry>();
        _memoryAllocator = _serviceProvider.GetRequiredService<IMemoryAllocator>();

        _concurrent = new ConcurrentBitmap(BitSize);
        _bitmapL3 = new BitmapL3Any(BitSize);
        _concurrentL3 = new ConcurrentBitmapL3Any(BitSize);
        _concurrentL3All = new ConcurrentBitmapL3All("BenchmarkBitmap", _resourceRegistry.Allocation, _memoryAllocator, BitSize);

        _pageCount = 1 << 19;
            
        _aList = new PageActivityInfo[_pageCount];
        _iList = new int[_pageCount];
        _vList = new int[_pageCount];
        _pages = new PagedMMF.PageInfo[_pageCount];

        var r = new Random(DateTime.UtcNow.Millisecond);
        for (int i = 0; i < _pageCount; i++)
        {
            var at = (int)(TimeSpan.FromMilliseconds(r.Next(0, 1000 * 60 * 10)).Ticks >> 20);
            var hc = r.Next(1, 10000);
            _aList[i] = new PageActivityInfo { AllocatedTime = at, HitCounter = hc };
            _iList[i] = hc;
            _vList[i] = i;
            _pages[i] = new PagedMMF.PageInfo(i);
        }

        _aMem = _aList.ToArray();

        _bitmapL3Filled = new BitmapL3Any(BitSize);
        var c = _bitmapL3Filled;
        for (int i = 0; i < SampleCount; i += 10)
        {
            c.Set(_samples[i + 0]);
            c.Set(_samples[i + 1]);
            c.Set(_samples[i + 2]);
            c.Set(_samples[i + 3]);
            c.Set(_samples[i + 4]);
            c.Set(_samples[i + 5]);
            c.Set(_samples[i + 6]);
            c.Set(_samples[i + 7]);
            c.Set(_samples[i + 8]);
            c.Set(_samples[i + 9]);
        }
    }

    [Benchmark(Baseline = true)]
    public void hConcurrentBitmap()
    {
        var c = _concurrent;
        for (int i = 0; i < SampleCount; i += 10)
        {
            c.Set(_samples[i + 0]);
            c.Set(_samples[i + 1]);
            c.Set(_samples[i + 2]);
            c.Set(_samples[i + 3]);
            c.Set(_samples[i + 4]);
            c.Set(_samples[i + 5]);
            c.Set(_samples[i + 6]);
            c.Set(_samples[i + 7]);
            c.Set(_samples[i + 8]);
            c.Set(_samples[i + 9]);
        }
    }

    [Benchmark]
    public void ConcurrentBitmapL3Any()
    {
        var c = _concurrentL3;
        for (int i = 0; i < SampleCount; i += 10)
        {
            c.Set(_samples[i + 0]);
            c.Set(_samples[i + 1]);
            c.Set(_samples[i + 2]);
            c.Set(_samples[i + 3]);
            c.Set(_samples[i + 4]);
            c.Set(_samples[i + 5]);
            c.Set(_samples[i + 6]);
            c.Set(_samples[i + 7]);
            c.Set(_samples[i + 8]);
            c.Set(_samples[i + 9]);
        }
    }

    [Benchmark]
    public void ConcurrentBitmapL3All()
    {
        var c = _concurrentL3All;
        for (int i = 0; i < SampleCount; i += 10)
        {
            c.SetL0(_samples[i + 0]);
            c.SetL0(_samples[i + 1]);
            c.SetL0(_samples[i + 2]);
            c.SetL0(_samples[i + 3]);
            c.SetL0(_samples[i + 4]);
            c.SetL0(_samples[i + 5]);
            c.SetL0(_samples[i + 6]);
            c.SetL0(_samples[i + 7]);
            c.SetL0(_samples[i + 8]);
            c.SetL0(_samples[i + 9]);
        }
    }

    [Benchmark]
    public int FindNextUnsetL0_Sparse()
    {
        // Test FindNextUnsetL0 with sparse bitmap (25% filled)
        var c = new ConcurrentBitmapL3All("SparseBitmap", _resourceRegistry.Allocation, _memoryAllocator, BitSize);

        // Fill 25% of bits sparsely
        for (int i = 0; i < BitSize; i += 4)
        {
            c.SetL0(i);
        }

        int count = 0;
        int index = -1;
        while (c.FindNextUnsetL0(ref index))
        {
            count++;
        }
        return count;
    }

    [Benchmark]
    public int FindNextUnsetL0_Dense()
    {
        // Test FindNextUnsetL0 with dense bitmap (blocks of filled data)
        var c = new ConcurrentBitmapL3All("DenseBitmap", _resourceRegistry.Allocation, _memoryAllocator, BitSize);

        // Fill blocks of 4096 bits (L1 regions) leaving gaps
        for (int block = 0; block < BitSize; block += 8192)
        {
            for (int i = block; i < block + 4096 && i < BitSize; i++)
            {
                c.SetL0(i);
            }
        }

        int count = 0;
        int index = -1;
        while (c.FindNextUnsetL0(ref index))
        {
            count++;
        }
        return count;
    }

    [Benchmark]
    public void BitmapL3Any()
    {
        var c = _bitmapL3;
        for (int i = 0; i < SampleCount; i += 10)
        {
            c.Set(_samples[i + 0]);
            c.Set(_samples[i + 1]);
            c.Set(_samples[i + 2]);
            c.Set(_samples[i + 3]);
            c.Set(_samples[i + 4]);
            c.Set(_samples[i + 5]);
            c.Set(_samples[i + 6]);
            c.Set(_samples[i + 7]);
            c.Set(_samples[i + 8]);
            c.Set(_samples[i + 9]);
        }
    }

    //[Benchmark(Baseline = true)]
    //public void BenchSortInt()
    //{
    //    Array.Sort(_iList);

    //}

    //[Benchmark]
    //public void BenchSortIntC()
    //{
    //    //Array.Sort(_pages, (x, y) => x.MemPageId - y.MemPageId);
    //    Array.Sort(_iList, _vList);

    //}

    //[Benchmark]
    //public void BenchSortIntV()
    //{
    //    Array.Sort(_iList, _vList);

    //}

    //[Benchmark(Baseline = true)]
    //public int BenchL3()
    //{
    //    int a = 0;
    //    int b = 0;
    //    int c = 0;
    //    var cm = _bitmapL3Filled;
    //    foreach (var i in cm)
    //    {
    //        a ^= ((i&1) == 0) ? (i * 4 + 123 / 2) : (int)Math.Acos(i);
    //        b ^= i + 13 & a;
    //        c ^= i | b;
    //    }

    //    return a + b + c;
    //}

    //[Benchmark]
    //public int BenchL3Lambda()
    //{
    //    int a = 0;
    //    int b = 0;
    //    int c = 0;
    //    var cm = _bitmapL3Filled;
    //    cm.ForEach(i =>
    //    {
    //        a ^= ((i & 1) == 0) ? (i * 4 + 123 / 2) : (int)Math.Acos(i);
    //        b ^= i + 13 & a;
    //        c ^= i | b;
    //    });

    //    return a + b + c;
    //}



    //[Benchmark]
    //public void BenchSortIntV2()
    //{
    //    Array.Sort(_iList, _pages);

    //}

    //[Benchmark]
    //public void BenchDecay()
    //{
    //    var c = _pageCount;
    //    var dT = (double)TimeSpan.FromMilliseconds(2).Ticks;
    //    for (int i = 0; i < c; i ++)
    //    {
    //        var t = (double)(_aList[i].AllocatedTime << 20);
    //        _aList[i].HitCounter = (int)(t / (t + dT) * _aList[i].HitCounter);
    //    }
    //}
}