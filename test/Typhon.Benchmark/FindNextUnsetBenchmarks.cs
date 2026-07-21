using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Collections: ConcurrentBitmapL3All.FindNextUnsetL0 Regression Benchmarks
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Collections", "Regression")]
public class FindNextUnsetBenchmarks
{
    private const int BitSize = 65536; // 64K bits

    private ServiceProvider _serviceProvider;
    private IResourceRegistry _resourceRegistry;
    private IMemoryAllocator _memoryAllocator;

    private ConcurrentBitmapL3All _sparseBitmap;
    private ConcurrentBitmapL3All _denseBitmap;
    private ConcurrentBitmapL3All _almostFullBitmap;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var sc = new ServiceCollection();
        sc.AddResourceRegistry()
          .AddMemoryAllocator();

        _serviceProvider = sc.BuildServiceProvider();
        _resourceRegistry = _serviceProvider.GetRequiredService<IResourceRegistry>();
        _memoryAllocator = _serviceProvider.GetRequiredService<IMemoryAllocator>();

        // Sparse: 25% filled (every 4th bit set)
        _sparseBitmap = new ConcurrentBitmapL3All("Sparse", _resourceRegistry.Allocation, _memoryAllocator, BitSize);
        for (int i = 0; i < BitSize; i += 4)
        {
            _sparseBitmap.SetL0(i);
        }

        // Dense: block pattern (first half of each 8K region filled)
        _denseBitmap = new ConcurrentBitmapL3All("Dense", _resourceRegistry.Allocation, _memoryAllocator, BitSize);
        for (int block = 0; block < BitSize; block += 8192)
        {
            for (int i = block; i < block + 4096 && i < BitSize; i++)
            {
                _denseBitmap.SetL0(i);
            }
        }

        // Almost full: 99% filled (every 100th bit left unset)
        _almostFullBitmap = new ConcurrentBitmapL3All("AlmostFull", _resourceRegistry.Allocation, _memoryAllocator, BitSize);
        for (int i = 0; i < BitSize; i++)
        {
            if (i % 100 != 0)
            {
                _almostFullBitmap.SetL0(i);
            }
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _serviceProvider?.Dispose();
    }

    /// <summary>
    /// Find first 100 unset bits in a 25%-filled bitmap.
    /// Unset bits are frequent — fast scan per bit.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 100)]
    public int FindNextUnset_Sparse25()
    {
        int count = 0;
        int index = -1;
        for (int i = 0; i < 100 && _sparseBitmap.FindNextUnsetL0(ref index); i++)
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// Find first 100 unset bits in a block-filled bitmap.
    /// L1/L2 skip logic kicks in for fully-set blocks.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 100)]
    public int FindNextUnset_Dense()
    {
        int count = 0;
        int index = -1;
        for (int i = 0; i < 100 && _denseBitmap.FindNextUnsetL0(ref index); i++)
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// Find first 100 unset bits in a 99%-filled bitmap.
    /// Tests worst-case scan: many set bits between each unset one.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 100)]
    public int FindNextUnset_AlmostFull()
    {
        int count = 0;
        int index = -1;
        for (int i = 0; i < 100 && _almostFullBitmap.FindNextUnsetL0(ref index); i++)
        {
            count++;
        }
        return count;
    }
}
