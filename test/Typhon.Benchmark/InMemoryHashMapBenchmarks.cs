using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using Typhon.Engine;

namespace Typhon.Benchmark;

/// <summary>
/// Benchmarks comparing HashMap against .NET's Dictionary and HashSet.
/// Parameterized on N (entry count) and BucketCap (entries per slot).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 3)]
[BenchmarkCategory("Collections")]
public class HashMapBenchmarks
{
    [Params(100, 1_000, 100_000)]
    public int N;

    [Params(4, 8)]
    public int BucketCap;

    private int[] _keys;
    private int[] _lookupKeys;

    private IServiceProvider _serviceProvider;
    private IMemoryAllocator _memoryAllocator;
    private IResource _parentResource;

    // Pre-populated for lookup benchmarks
    private Dictionary<int, int> _dictPopulated;
    private HashSet<int> _hashSetPopulated;
    private HashMap<int, int> _mapPopulated;
    private HashMap<int> _setPopulated;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _serviceProvider = new ServiceCollection()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .BuildServiceProvider();

        _memoryAllocator = _serviceProvider.GetRequiredService<IMemoryAllocator>();
        _parentResource = _serviceProvider.GetRequiredService<IResourceRegistry>().Allocation;

        var rng = new Random(42);
        _keys = new int[N];
        _lookupKeys = new int[N];
        for (int i = 0; i < N; i++)
        {
            _keys[i] = i;
            _lookupKeys[i] = i;
        }
        for (int i = N - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_lookupKeys[i], _lookupKeys[j]) = (_lookupKeys[j], _lookupKeys[i]);
        }

        // Pre-populate for lookup benchmarks
        _dictPopulated = new Dictionary<int, int>(N);
        _hashSetPopulated = new HashSet<int>(N);
        _mapPopulated = new HashMap<int, int>(64);
        _setPopulated = new HashMap<int>(64);

        for (int i = 0; i < N; i++)
        {
            _dictPopulated[i] = i;
            _hashSetPopulated.Add(i);
            _mapPopulated.TryAdd(i, i);
            _setPopulated.TryAdd(i);
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _mapPopulated?.Dispose();
        _setPopulated?.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Add — Dictionary vs HashMap<K,V>
    // ═══════════════════════════════════════════════════════════════════════

    [Benchmark(Description = "Dict.Add")]
    [BenchmarkCategory("Add")]
    public Dictionary<int, int> Dict_Add()
    {
        var dict = new Dictionary<int, int>(64);
        for (int i = 0; i < N; i++)
        {
            dict[_keys[i]] = i;
        }
        return dict;
    }

    [Benchmark(Description = "Map.Add")]
    [BenchmarkCategory("Add")]
    public object Map_Add()
    {
        var map = new HashMap<int, int>(64);
        for (int i = 0; i < N; i++)
        {
            map.TryAdd(_keys[i], i);
        }
        return map;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Add — HashSet vs HashMap<K>
    // ═══════════════════════════════════════════════════════════════════════

    [Benchmark(Description = "HSet.Add")]
    [BenchmarkCategory("Add")]
    public HashSet<int> HashSet_Add()
    {
        var set = new HashSet<int>(64);
        for (int i = 0; i < N; i++)
        {
            set.Add(_keys[i]);
        }
        return set;
    }

    [Benchmark(Description = "Set.Add")]
    [BenchmarkCategory("Add")]
    public object Set_Add()
    {
        var set = new HashMap<int>(64);
        for (int i = 0; i < N; i++)
        {
            set.TryAdd(_keys[i]);
        }
        return set;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Lookup — pre-populated, shuffled access
    // ═══════════════════════════════════════════════════════════════════════

    [Benchmark(Description = "Dict.Get")]
    [BenchmarkCategory("Lookup")]
    public int Dict_Lookup()
    {
        int sum = 0;
        var dict = _dictPopulated;
        var keys = _lookupKeys;
        for (int i = 0; i < N; i++)
        {
            if (dict.TryGetValue(keys[i], out int val))
            {
                sum += val;
            }
        }
        return sum;
    }

    [Benchmark(Description = "Map.Get")]
    [BenchmarkCategory("Lookup")]
    public int Map_Lookup()
    {
        int sum = 0;
        var map = _mapPopulated;
        var keys = _lookupKeys;
        for (int i = 0; i < N; i++)
        {
            if (map.TryGetValue(keys[i], out int val))
            {
                sum += val;
            }
        }
        return sum;
    }

    [Benchmark(Description = "HSet.Has")]
    [BenchmarkCategory("Lookup")]
    public int HashSet_Contains()
    {
        int count = 0;
        var set = _hashSetPopulated;
        var keys = _lookupKeys;
        for (int i = 0; i < N; i++)
        {
            if (set.Contains(keys[i]))
            {
                count++;
            }
        }
        return count;
    }

    [Benchmark(Description = "Set.Has")]
    [BenchmarkCategory("Lookup")]
    public int Set_Contains()
    {
        int count = 0;
        var set = _setPopulated;
        var keys = _lookupKeys;
        for (int i = 0; i < N; i++)
        {
            if (set.Contains(keys[i]))
            {
                count++;
            }
        }
        return count;
    }
}
