// unset

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;

namespace Typhon.Benchmark;

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[BenchmarkCategory("Collections")]
public class FindNextUnsetL0Benchmark
{
    [Params(65536, 1048576)] // 64K, 1M bits
    public int BitSize { get; set; }

    [Params("Empty", "Sparse25", "Dense", "AlmostFull")]
    public string Pattern { get; set; } = "Empty";

    private ConcurrentBitmapL3All _bitmap = null!;
    private ConcurrentBitmapL3AllOld _bitmapOld = null!;

    // DI services for benchmarks
    private IServiceProvider _serviceProvider = null!;
    private IResourceRegistry _resourceRegistry = null!;
    private IMemoryAllocator _memoryAllocator = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Initialize DI services
        _serviceProvider = new ServiceCollection()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .BuildServiceProvider();
        _resourceRegistry = _serviceProvider.GetRequiredService<IResourceRegistry>();
        _memoryAllocator = _serviceProvider.GetRequiredService<IMemoryAllocator>();

        _bitmap = new ConcurrentBitmapL3All("BenchmarkBitmap", _resourceRegistry.Allocation, _memoryAllocator, BitSize);
        _bitmapOld = new ConcurrentBitmapL3AllOld(BitSize);

        switch (Pattern)
        {
            case "Empty":
                // No bits set
                break;

            case "Sparse25":
                // 25% filled sparsely (every 4th bit)
                for (int i = 0; i < BitSize; i += 4)
                {
                    _bitmap.SetL0(i);
                    _bitmapOld.SetL0(i);
                }
                break;

            case "Sparse50":
                // 50% filled sparsely (every 2nd bit)
                for (int i = 0; i < BitSize; i += 2)
                {
                    _bitmap.SetL0(i);
                    _bitmapOld.SetL0(i);
                }
                break;

            case "Dense":
                // 50% filled in dense blocks (first half of each 8K region)
                for (int block = 0; block < BitSize; block += 8192)
                {
                    for (int i = block; i < block + 4096 && i < BitSize; i++)
                    {
                        _bitmap.SetL0(i);
                        _bitmapOld.SetL0(i);
                    }
                }
                break;

            case "AlmostFull":
                // 99% filled - leaves only every 100th bit unset
                for (int i = 0; i < BitSize; i++)
                {
                    if (i % 100 != 0)
                    {
                        _bitmap.SetL0(i);
                        _bitmapOld.SetL0(i);
                    }
                }
                break;
        }
    }

    [Benchmark(Baseline = true)]
    public int Optimized()
    {
        int count = 0;
        int index = -1;
        while (_bitmap.FindNextUnsetL0(ref index))
        {
            count++;
        }
        return count;
    }

    [Benchmark]
    public int Original()
    {
        int count = 0;
        int index = -1;
        long mask = 0;
        while (_bitmapOld.FindNextUnsetL0(ref index, ref mask))
        {
            count++;
        }
        return count;
    }
}

/// <summary>
/// Copy of the original implementation for benchmark comparison
/// </summary>
public class ConcurrentBitmapL3AllOld
{
    private const int L0All = 0;
    private const int L1All = 1;
    private const int L1Any = 2;
    private const int L2All = 3;

    private volatile int _control;
    private Memory<long>[] _maps;

    public int Capacity { get; private set; }
    public int TotalBitSet { get; private set; }

    public ConcurrentBitmapL3AllOld(int bitCount)
    {
        Capacity = bitCount;
        TotalBitSet = 0;

        _maps = new Memory<long>[4];
        var length = Math.Max(1, (bitCount + 63) / 64);
        _maps[L0All] = new long[length];

        length = Math.Max(1, (length + 63) / 64);
        _maps[L1All] = new long[length];
        _maps[L1Any] = new long[length];

        length = Math.Max(1, (length + 63) / 64);
        _maps[L2All] = new long[length];
    }

    private void TakeControl()
    {
        if (Interlocked.CompareExchange(ref _control, 1, 0) != 0)
        {
            var sw = new SpinWait();
            while (Interlocked.CompareExchange(ref _control, 1, 0) != 0)
            {
                sw.SpinOnce();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool SetL0(int bitIndex)
    {
        var l0Offset = bitIndex >> 6;
        var l0Mask = 1L << (bitIndex & 0x3F);

        TakeControl();

        var prevL0 = Interlocked.Or(ref _maps[L0All].Span[l0Offset], l0Mask);
        if ((prevL0 & l0Mask) != 0)
        {
            _control = 0;
            return false;
        }

        if (prevL0 != -1 && (prevL0 | l0Mask) == -1)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            var prevL1 = _maps[L1All].Span[l1Offset];
            _maps[L1All].Span[l1Offset] |= l1Mask;

            if (prevL1 != -1 && (prevL1 | l1Mask) == -1)
            {
                var l2Offset = l1Offset >> 6;
                var l2Mask = 1L << (l1Offset & 0x3F);
                _maps[L2All].Span[l2Offset] |= l2Mask;
            }
        }

        if (prevL0 == 0 && (prevL0 | l0Mask) != 0)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);
            _maps[L1Any].Span[l1Offset] |= l1Mask;
        }

        ++TotalBitSet;
        _control = 0;
        return true;
    }

    /// <summary>
    /// Original implementation - for benchmark comparison
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool FindNextUnsetL0(ref int index, ref long mask)
    {
        var capacity = Capacity;
        var maps = _maps;

        var c0 = ++index;
        long v0 = mask;
        long t0;

        var ll0 = (capacity + 63) / 64;
        var ll1 = maps[L1All].Length;
        var ll2 = maps[L2All].Length;

        while (c0 < capacity)
        {
            // Do we have to fetch a new L0?
            if (((c0 & 0x3F) == 0) || (v0 == -1))
            {
                // Check if we can skip the rest of the level 0
                for (int i0 = c0 >> 6; i0 < ll0; i0 = c0 >> 6)
                {
                    t0 = 1L << (c0 & 0x3F);
                    v0 = maps[L0All].Span[i0] | (t0 - 1);

                    if (v0 != -1)
                    {
                        break;
                    }
                    c0 = ++i0 << 6;

                    // Check if we can skip the rest of the level 1
                    for (int i1 = c0 >> 12; i1 < ll1; i1 = c0 >> 12)
                    {
                        var v1 = maps[L1All].Span[i1] >> (i0 & 0x3F);
                        if (v1 != -1)
                        {
                            break;
                        }

                        i0 = 0;
                        c0 = ++i1 << 12;

                        // Check if we can skip the rest of the level 2
                        for (int i2 = c0 >> 18; i2 < ll2; i2 = c0 >> 18)
                        {
                            var v2 = maps[L2All].Span[i2] >> (i1 & 0x3F);
                            if (v2 != -1)
                            {
                                break;
                            }
                            i1 = 0;
                            c0 = ++i2 << 18;
                        }
                    }
                }
            }

            var bitPos = BitOperations.TrailingZeroCount(~v0);
            v0 |= (1L << bitPos);
            index = (c0 & ~0x3F) + bitPos;
            mask = v0;
            return true;
        }
        return false;
    }
}
