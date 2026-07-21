using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace Typhon.Benchmark;

/// <summary>
/// Compares HashMap&lt;long&gt;.Contains vs HashSet&lt;long&gt;.Contains.
/// HashMap is used by ViewBase for entity set management — Contains is the hottest operation
/// (100K+/tick for dirty entity filtering and delta processing).
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[BenchmarkCategory("Collections")]
public class HashMapContainsBenchmarks
{
    private HashSet<long> _hashSet;
    private HashMap<long> _hashMap;
    private long[] _lookupKeys;
    private long[] _missingKeys;

    [Params(1000, 10000, 100000)]
    public int Count;

    [GlobalSetup]
    public void Setup()
    {
        _hashSet = new HashSet<long>(Count);
        _hashMap = new HashMap<long>(Count * 2);
        _lookupKeys = new long[Count];
        _missingKeys = new long[Count];

        for (int i = 0; i < Count; i++)
        {
            long key = i * 7 + 1; // non-sequential, realistic entity PKs
            _hashSet.Add(key);
            _hashMap.TryAdd(key);
            _lookupKeys[i] = key;
            _missingKeys[i] = -(i + 1); // guaranteed miss
        }
    }

    [Benchmark(Baseline = true)]
    public int HashSet_Contains_Hit()
    {
        int found = 0;
        for (int i = 0; i < _lookupKeys.Length; i++)
        {
            if (_hashSet.Contains(_lookupKeys[i]))
            {
                found++;
            }
        }
        return found;
    }

    [Benchmark]
    public int HashMap_Contains_Hit()
    {
        int found = 0;
        for (int i = 0; i < _lookupKeys.Length; i++)
        {
            if (_hashMap.Contains(_lookupKeys[i]))
            {
                found++;
            }
        }
        return found;
    }

    [Benchmark]
    public int HashSet_Contains_Miss()
    {
        int found = 0;
        for (int i = 0; i < _missingKeys.Length; i++)
        {
            if (_hashSet.Contains(_missingKeys[i]))
            {
                found++;
            }
        }
        return found;
    }

    [Benchmark]
    public int HashMap_Contains_Miss()
    {
        int found = 0;
        for (int i = 0; i < _missingKeys.Length; i++)
        {
            if (_hashMap.Contains(_missingKeys[i]))
            {
                found++;
            }
        }
        return found;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _hashMap?.Dispose();
    }
}
