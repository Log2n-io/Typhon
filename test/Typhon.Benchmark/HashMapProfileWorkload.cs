using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Typhon.Benchmark;

/// <summary>
/// Profiling workload for HashMap vs .NET Dictionary.
/// Realistic scenarios: random keys, no pre-sizing, mixed insert/remove.
/// Invoke via: Typhon.Benchmark.exe --profile-hashmap
/// </summary>
static class HashMapProfileWorkload
{
    const int N = 10_000;
    const int Iterations = 500;

    public static void Run()
    {
        var sp = new ServiceCollection()
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .BuildServiceProvider();
        var allocator = sp.GetRequiredService<IMemoryAllocator>();
        var parent = sp.GetRequiredService<IResourceRegistry>().Allocation;

        var rng = new Random(42);

        // ── Generate random int keys (unique) ────────────────────────────
        var intSet = new HashSet<int>();
        while (intSet.Count < N) intSet.Add(rng.Next(int.MinValue, int.MaxValue));
        var intKeys = new int[N];
        intSet.CopyTo(intKeys);
        var intLookup = (int[])intKeys.Clone();
        Shuffle(intLookup, rng);

        // ── Generate random Guid keys ────────────────────────────────────
        var guidKeys = new Guid[N];
        for (int i = 0; i < N; i++) guidKeys[i] = Guid.NewGuid();
        var guidLookup = (Guid[])guidKeys.Clone();
        Shuffle(guidLookup, rng);

        // ── Generate mixed ops (75% insert, 25% remove) ─────────────────
        // Pre-generate deterministic op sequence so Dict and Map see identical workload
        var mixedOps = new MixedOp[N];
        GenerateMixedOps(mixedOps, N, rng);

        Console.WriteLine($"HashMap Profile: N={N}, Iterations={Iterations}, no pre-sizing");
        Console.WriteLine();

        // ═══════════════════════════════════════════════════════════════
        // Scenario 1: Random Int — Insert + Lookup
        // ═══════════════════════════════════════════════════════════════
        {
            Console.WriteLine("── Random Int (insert 10K + lookup 10K) ──");
            var (da, dl) = BenchDictInt(intKeys, intLookup);
            var (ma, ml) = BenchMapInt(intKeys, intLookup, parent, allocator);
            PrintResult(da, dl, ma, ml);
        }

        // ═══════════════════════════════════════════════════════════════
        // Scenario 2: Random Guid — Insert + Lookup
        // ═══════════════════════════════════════════════════════════════
        {
            Console.WriteLine("── Random Guid (insert 10K + lookup 10K) ──");
            var (da, dl) = BenchDictGuid(guidKeys, guidLookup);
            var (ma, ml) = BenchMapGuid(guidKeys, guidLookup, parent, allocator);
            PrintResult(da, dl, ma, ml);
        }

        // ═══════════════════════════════════════════════════════════════
        // Scenario 3: Mixed Int — 75% insert / 25% remove + Lookup
        // ═══════════════════════════════════════════════════════════════
        {
            Console.WriteLine("── Mixed Int (75%% insert / 25%% remove + lookup) ──");
            var (dm, dl) = BenchDictIntMixed(intKeys, intLookup, mixedOps);
            var (mm, mml) = BenchMapIntMixed(intKeys, intLookup, mixedOps, parent, allocator);
            PrintResult(dm, dl, mm, mml);
        }

        // ═══════════════════════════════════════════════════════════════
        // Scenario 4: Mixed Guid — 75% insert / 25% remove + Lookup
        // ═══════════════════════════════════════════════════════════════
        {
            Console.WriteLine("── Mixed Guid (75%% insert / 25%% remove + lookup) ──");
            var (dm, dl) = BenchDictGuidMixed(guidKeys, guidLookup, mixedOps);
            var (mm, mml) = BenchMapGuidMixed(guidKeys, guidLookup, mixedOps, parent, allocator);
            PrintResult(dm, dl, mm, mml);
        }

        // ═══════════════════════════════════════════════════════════════
        // Concurrent Scenarios
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine("  Concurrent Scenarios");
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine();

        int[] threadCounts = [1, 4, 8, Math.Min(16, Environment.ProcessorCount)];
        foreach (int tc in threadCounts)
        {
            Console.WriteLine($"── {tc} threads, disjoint inserts (10K keys each) ──");
            long cdMs = BenchConcDictDisjointInsert(tc, N);
            long cmMs = BenchConcMapDisjointInsert(tc, N, parent, allocator);
            double ratio = (double)cmMs / Math.Max(cdMs, 1);
            Console.WriteLine($"  ConcurrentDict: {cdMs}ms  ConcurrentMap: {cmMs}ms  Ratio: {ratio:F2}x");
            Console.WriteLine();
        }

        foreach (int tc in threadCounts)
        {
            Console.WriteLine($"── {tc} threads, 90%% read / 10%% write on 10K keys ──");
            long cdMs = BenchConcDictMixedReadWrite(tc, N);
            long cmMs = BenchConcMapMixedReadWrite(tc, N, parent, allocator);
            double ratio = (double)cmMs / Math.Max(cdMs, 1);
            Console.WriteLine($"  ConcurrentDict: {cdMs}ms  ConcurrentMap: {cmMs}ms  Ratio: {ratio:F2}x");
            Console.WriteLine();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Mixed op generation
    // ═══════════════════════════════════════════════════════════════════════

    struct MixedOp
    {
        public bool IsInsert;        // true = insert, false = remove
        public int RemoveFromIndex;  // for remove: index into "inserted so far" list
    }

    static void GenerateMixedOps(MixedOp[] ops, int count, Random rng)
    {
        int insertedCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (insertedCount == 0 || rng.NextDouble() < 0.75)
            {
                ops[i] = new MixedOp { IsInsert = true };
                insertedCount++;
            }
            else
            {
                ops[i] = new MixedOp { IsInsert = false, RemoveFromIndex = rng.Next(insertedCount) };
                // Don't decrement insertedCount here — the actual key tracking happens at runtime
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario 1: Random Int
    // ═══════════════════════════════════════════════════════════════════════

    static (long addMs, long lookupMs) BenchDictInt(int[] keys, int[] lookupKeys)
    {
        // Warmup
        var d = new Dictionary<int, int>();
        for (int i = 0; i < N; i++) d[keys[i]] = i;
        for (int i = 0; i < N; i++) d.TryGetValue(lookupKeys[i], out _);

        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < Iterations; iter++)
        {
            d.Clear();
            DictAddInt(d, keys, N);
        }
        long addMs = sw.ElapsedMilliseconds;

        // Repopulate for lookup
        d.Clear();
        for (int i = 0; i < N; i++) d[keys[i]] = i;
        sw.Restart();
        for (int iter = 0; iter < Iterations; iter++)
        {
            DictLookupInt(d, lookupKeys, N);
        }
        return (addMs, sw.ElapsedMilliseconds);
    }

    static (long addMs, long lookupMs) BenchMapInt(int[] keys, int[] lookupKeys, IResource parent, IMemoryAllocator alloc)
    {
        // Warmup
        var m = new HashMap<int, int>();
        for (int i = 0; i < N; i++) m.TryAdd(keys[i], i);
        for (int i = 0; i < N; i++) m.TryGetValue(lookupKeys[i], out _);
        m.Dispose();

        using var map = new HashMap<int, int>();
        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < Iterations; iter++)
        {
            map.Clear();
            MapAddInt(map, keys, N);
        }
        long addMs = sw.ElapsedMilliseconds;

        // Repopulate for lookup
        map.Clear();
        for (int i = 0; i < N; i++) map.TryAdd(keys[i], i);
        sw.Restart();
        for (int iter = 0; iter < Iterations; iter++)
        {
            MapLookupInt(map, lookupKeys, N);
        }
        return (addMs, sw.ElapsedMilliseconds);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario 2: Random Guid
    // ═══════════════════════════════════════════════════════════════════════

    static (long addMs, long lookupMs) BenchDictGuid(Guid[] keys, Guid[] lookupKeys)
    {
        var d = new Dictionary<Guid, int>();
        for (int i = 0; i < N; i++) d[keys[i]] = i;
        for (int i = 0; i < N; i++) d.TryGetValue(lookupKeys[i], out _);

        d.Clear();
        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < Iterations; iter++)
        {
            d.Clear();
            DictAddGuid(d, keys, N);
        }
        long addMs = sw.ElapsedMilliseconds;

        d.Clear();
        for (int i = 0; i < N; i++) d[keys[i]] = i;
        sw.Restart();
        for (int iter = 0; iter < Iterations; iter++)
        {
            DictLookupGuid(d, lookupKeys, N);
        }
        return (addMs, sw.ElapsedMilliseconds);
    }

    static (long addMs, long lookupMs) BenchMapGuid(Guid[] keys, Guid[] lookupKeys, IResource parent, IMemoryAllocator alloc)
    {
        var m = new HashMap<Guid, int>();
        for (int i = 0; i < N; i++) m.TryAdd(keys[i], i);
        for (int i = 0; i < N; i++) m.TryGetValue(lookupKeys[i], out _);
        m.Dispose();

        using var map = new HashMap<Guid, int>();
        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < Iterations; iter++)
        {
            map.Clear();
            MapAddGuid(map, keys, N);
        }
        long addMs = sw.ElapsedMilliseconds;

        map.Clear();
        for (int i = 0; i < N; i++) map.TryAdd(keys[i], i);
        sw.Restart();
        for (int iter = 0; iter < Iterations; iter++)
        {
            MapLookupGuid(map, lookupKeys, N);
        }
        return (addMs, sw.ElapsedMilliseconds);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario 3: Mixed Int (75% insert / 25% remove)
    // ═══════════════════════════════════════════════════════════════════════

    static (long mixedMs, long lookupMs) BenchDictIntMixed(int[] keys, int[] lookupKeys, MixedOp[] ops)
    {
        var d = new Dictionary<int, int>();
        // Warmup
        DictMixedInt(d, keys, ops, N);
        DictLookupInt(d, lookupKeys, d.Count);

        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < Iterations; iter++)
        {
            d.Clear();
            DictMixedInt(d, keys, ops, N);
        }
        long mixedMs = sw.ElapsedMilliseconds;

        int remaining = d.Count;
        sw.Restart();
        for (int iter = 0; iter < Iterations; iter++)
        {
            DictLookupInt(d, lookupKeys, remaining);
        }
        return (mixedMs, sw.ElapsedMilliseconds);
    }

    static (long mixedMs, long lookupMs) BenchMapIntMixed(int[] keys, int[] lookupKeys, MixedOp[] ops, IResource parent, IMemoryAllocator alloc)
    {
        var m = new HashMap<int, int>();
        MapMixedInt(m, keys, ops, N);
        m.Dispose();

        using var map = new HashMap<int, int>();
        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < Iterations; iter++)
        {
            map.Clear();
            MapMixedInt(map, keys, ops, N);
        }
        long mixedMs = sw.ElapsedMilliseconds;

        int remaining = map.Count;
        sw.Restart();
        for (int iter = 0; iter < Iterations; iter++)
        {
            MapLookupInt(map, lookupKeys, remaining);
        }
        return (mixedMs, sw.ElapsedMilliseconds);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario 4: Mixed Guid (75% insert / 25% remove)
    // ═══════════════════════════════════════════════════════════════════════

    static (long mixedMs, long lookupMs) BenchDictGuidMixed(Guid[] keys, Guid[] lookupKeys, MixedOp[] ops)
    {
        var d = new Dictionary<Guid, int>();
        DictMixedGuid(d, keys, ops, N);
        DictLookupGuid(d, lookupKeys, d.Count);

        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < Iterations; iter++)
        {
            d.Clear();
            DictMixedGuid(d, keys, ops, N);
        }
        long mixedMs = sw.ElapsedMilliseconds;

        int remaining = d.Count;
        sw.Restart();
        for (int iter = 0; iter < Iterations; iter++)
        {
            DictLookupGuid(d, lookupKeys, remaining);
        }
        return (mixedMs, sw.ElapsedMilliseconds);
    }

    static (long mixedMs, long lookupMs) BenchMapGuidMixed(Guid[] keys, Guid[] lookupKeys, MixedOp[] ops, IResource parent, IMemoryAllocator alloc)
    {
        var m = new HashMap<Guid, int>();
        MapMixedGuid(m, keys, ops, N);
        m.Dispose();

        using var map = new HashMap<Guid, int>();
        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < Iterations; iter++)
        {
            map.Clear();
            MapMixedGuid(map, keys, ops, N);
        }
        long mixedMs = sw.ElapsedMilliseconds;

        int remaining = map.Count;
        sw.Restart();
        for (int iter = 0; iter < Iterations; iter++)
        {
            MapLookupGuid(map, lookupKeys, remaining);
        }
        return (mixedMs, sw.ElapsedMilliseconds);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Inner loops — NoInlining for profiling visibility
    // ═══════════════════════════════════════════════════════════════════════

    // Int Add/Lookup
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DictAddInt(Dictionary<int, int> d, int[] keys, int n)
    {
        for (int i = 0; i < n; i++) d[keys[i]] = i;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DictLookupInt(Dictionary<int, int> d, int[] keys, int n)
    {
        int sum = 0;
        for (int i = 0; i < n; i++)
        {
            if (d.TryGetValue(keys[i], out int v)) sum += v;
        }
        GC.KeepAlive(sum);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MapAddInt(HashMap<int, int> m, int[] keys, int n)
    {
        for (int i = 0; i < n; i++) m.TryAdd(keys[i], i);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MapLookupInt(HashMap<int, int> m, int[] keys, int n)
    {
        int sum = 0;
        for (int i = 0; i < n; i++)
        {
            if (m.TryGetValue(keys[i], out int v)) sum += v;
        }
        GC.KeepAlive(sum);
    }

    // Guid Add/Lookup
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DictAddGuid(Dictionary<Guid, int> d, Guid[] keys, int n)
    {
        for (int i = 0; i < n; i++) d[keys[i]] = i;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DictLookupGuid(Dictionary<Guid, int> d, Guid[] keys, int n)
    {
        int sum = 0;
        for (int i = 0; i < n; i++)
        {
            if (d.TryGetValue(keys[i], out int v)) sum += v;
        }
        GC.KeepAlive(sum);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MapAddGuid(HashMap<Guid, int> m, Guid[] keys, int n)
    {
        for (int i = 0; i < n; i++) m.TryAdd(keys[i], i);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MapLookupGuid(HashMap<Guid, int> m, Guid[] keys, int n)
    {
        int sum = 0;
        for (int i = 0; i < n; i++)
        {
            if (m.TryGetValue(keys[i], out int v)) sum += v;
        }
        GC.KeepAlive(sum);
    }

    // Int Mixed (75% insert / 25% remove)
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DictMixedInt(Dictionary<int, int> d, int[] keys, MixedOp[] ops, int n)
    {
        var inserted = new List<int>(n);
        int keyIdx = 0;
        for (int i = 0; i < n; i++)
        {
            if (ops[i].IsInsert && keyIdx < keys.Length)
            {
                d[keys[keyIdx]] = keyIdx;
                inserted.Add(keys[keyIdx]);
                keyIdx++;
            }
            else if (inserted.Count > 0)
            {
                int removeIdx = ops[i].RemoveFromIndex % inserted.Count;
                d.Remove(inserted[removeIdx]);
                // Swap-remove from tracking list
                inserted[removeIdx] = inserted[^1];
                inserted.RemoveAt(inserted.Count - 1);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MapMixedInt(HashMap<int, int> m, int[] keys, MixedOp[] ops, int n)
    {
        var inserted = new List<int>(n);
        int keyIdx = 0;
        for (int i = 0; i < n; i++)
        {
            if (ops[i].IsInsert && keyIdx < keys.Length)
            {
                m.TryAdd(keys[keyIdx], keyIdx);
                inserted.Add(keys[keyIdx]);
                keyIdx++;
            }
            else if (inserted.Count > 0)
            {
                int removeIdx = ops[i].RemoveFromIndex % inserted.Count;
                m.TryRemove(inserted[removeIdx], out _);
                inserted[removeIdx] = inserted[^1];
                inserted.RemoveAt(inserted.Count - 1);
            }
        }
    }

    // Guid Mixed
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DictMixedGuid(Dictionary<Guid, int> d, Guid[] keys, MixedOp[] ops, int n)
    {
        var inserted = new List<Guid>(n);
        int keyIdx = 0;
        for (int i = 0; i < n; i++)
        {
            if (ops[i].IsInsert && keyIdx < keys.Length)
            {
                d[keys[keyIdx]] = keyIdx;
                inserted.Add(keys[keyIdx]);
                keyIdx++;
            }
            else if (inserted.Count > 0)
            {
                int removeIdx = ops[i].RemoveFromIndex % inserted.Count;
                d.Remove(inserted[removeIdx]);
                inserted[removeIdx] = inserted[^1];
                inserted.RemoveAt(inserted.Count - 1);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void MapMixedGuid(HashMap<Guid, int> m, Guid[] keys, MixedOp[] ops, int n)
    {
        var inserted = new List<Guid>(n);
        int keyIdx = 0;
        for (int i = 0; i < n; i++)
        {
            if (ops[i].IsInsert && keyIdx < keys.Length)
            {
                m.TryAdd(keys[keyIdx], keyIdx);
                inserted.Add(keys[keyIdx]);
                keyIdx++;
            }
            else if (inserted.Count > 0)
            {
                int removeIdx = ops[i].RemoveFromIndex % inserted.Count;
                m.TryRemove(inserted[removeIdx], out _);
                inserted[removeIdx] = inserted[^1];
                inserted.RemoveAt(inserted.Count - 1);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    static void Shuffle<T>(T[] array, Random rng)
    {
        for (int i = array.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }
    }

    static void PrintResult(long dictOp, long dictLookup, long mapOp, long mapLookup)
    {
        Console.WriteLine($"  {"Op",-12} {"Dict",8} {"Map",8} {"Ratio",8}");
        Console.WriteLine($"  {"---",-12} {"---",8} {"---",8} {"---",8}");
        Console.WriteLine($"  {"Mutate",-12} {dictOp + "ms",8} {mapOp + "ms",8} {(double)mapOp / Math.Max(dictOp, 1):F2}x");
        Console.WriteLine($"  {"Lookup",-12} {dictLookup + "ms",8} {mapLookup + "ms",8} {(double)mapLookup / Math.Max(dictLookup, 1):F2}x");
        long dt = dictOp + dictLookup, mt = mapOp + mapLookup;
        Console.WriteLine($"  {"Total",-12} {dt + "ms",8} {mt + "ms",8} {(double)mt / Math.Max(dt, 1):F2}x");
        Console.WriteLine();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Concurrent scenario benchmarks
    // ═══════════════════════════════════════════════════════════════════════

    static long BenchConcDictDisjointInsert(int threadCount, int keysPerThread)
    {
        // Warmup
        var warmup = new ConcurrentDictionary<int, int>();
        for (int i = 0; i < keysPerThread; i++) warmup.TryAdd(i, i);

        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < 50; iter++)
        {
            var dict = new ConcurrentDictionary<int, int>(threadCount, keysPerThread * threadCount);
            var threads = new Thread[threadCount];
            using var barrier = new Barrier(threadCount);
            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    int start = tid * keysPerThread;
                    for (int i = 0; i < keysPerThread; i++)
                    {
                        dict.TryAdd(start + i, start + i);
                    }
                });
                threads[t].Start();
            }
            foreach (var t in threads) t.Join();
        }
        return sw.ElapsedMilliseconds;
    }

    static long BenchConcMapDisjointInsert(int threadCount, int keysPerThread, IResource parent, IMemoryAllocator alloc)
    {
        // Warmup
        var warmup = new ConcurrentHashMap<int, int>();
        for (int i = 0; i < keysPerThread; i++) warmup.TryAdd(i, i);
        warmup.Dispose();

        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < 50; iter++)
        {
            var map = new ConcurrentHashMap<int, int>(keysPerThread * threadCount);
            var threads = new Thread[threadCount];
            using var barrier = new Barrier(threadCount);
            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    int start = tid * keysPerThread;
                    for (int i = 0; i < keysPerThread; i++)
                    {
                        map.TryAdd(start + i, start + i);
                    }
                });
                threads[t].Start();
            }
            foreach (var t in threads) t.Join();
            map.Dispose();
        }
        return sw.ElapsedMilliseconds;
    }

    static long BenchConcDictMixedReadWrite(int threadCount, int keyRange)
    {
        var dict = new ConcurrentDictionary<int, int>(threadCount, keyRange);
        for (int i = 0; i < keyRange; i++) dict.TryAdd(i, i);

        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < 50; iter++)
        {
            var threads = new Thread[threadCount];
            using var barrier = new Barrier(threadCount);
            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    var rng = new Random(tid + iter * 100);
                    int sum = 0;
                    for (int i = 0; i < keyRange; i++)
                    {
                        int key = rng.Next(keyRange);
                        if (rng.NextDouble() < 0.9)
                        {
                            if (dict.TryGetValue(key, out int v)) sum += v;
                        }
                        else
                        {
                            dict[key] = key;
                        }
                    }
                    GC.KeepAlive(sum);
                });
                threads[t].Start();
            }
            foreach (var t in threads) t.Join();
        }
        return sw.ElapsedMilliseconds;
    }

    static long BenchConcMapMixedReadWrite(int threadCount, int keyRange, IResource parent, IMemoryAllocator alloc)
    {
        using var map = new ConcurrentHashMap<int, int>(keyRange);
        for (int i = 0; i < keyRange; i++) map.TryAdd(i, i);

        var sw = Stopwatch.StartNew();
        for (int iter = 0; iter < 50; iter++)
        {
            var threads = new Thread[threadCount];
            using var barrier = new Barrier(threadCount);
            for (int t = 0; t < threadCount; t++)
            {
                int tid = t;
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    var rng = new Random(tid + iter * 100);
                    int sum = 0;
                    for (int i = 0; i < keyRange; i++)
                    {
                        int key = rng.Next(keyRange);
                        if (rng.NextDouble() < 0.9)
                        {
                            if (map.TryGetValue(key, out int v)) sum += v;
                        }
                        else
                        {
                            map[key] = key;
                        }
                    }
                    GC.KeepAlive(sum);
                });
                threads[t].Start();
            }
            foreach (var t in threads) t.Join();
        }
        return sw.ElapsedMilliseconds;
    }
}
