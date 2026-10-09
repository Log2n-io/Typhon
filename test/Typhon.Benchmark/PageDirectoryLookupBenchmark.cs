using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Typhon.Benchmark;

/// <summary>
/// #1136: the page directory's hit path, the native intrusive <see cref="PageDirectory"/> against the <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// it replaces. Each lookup also reads the slot record it lands on, as the cache's hit path does right after (PS-15's validation), so both sides
/// pay for that line and the directory gets no credit for touching it first.
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 7)]
[BenchmarkCategory("Storage")]
public unsafe class PageDirectoryLookupBenchmark
{
    private const int LookupsPerThread = 1 << 20;

    [Params(262_144, 4_194_304)]
    public int Entries;

    [Params(1, 16)]
    public int Threads;

    private IServiceProvider _serviceProvider;
    private PagedMMF.PageSlotTable _slots;
    private PageDirectory _directory;
    private ConcurrentDictionary<int, int> _dictionary;
    private int[][] _keys;
    private long _sink;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _serviceProvider = new ServiceCollection().AddResourceRegistry().AddMemoryAllocator().BuildServiceProvider();
        var allocator = _serviceProvider.GetRequiredService<IMemoryAllocator>();
        var owner = _serviceProvider.GetRequiredService<IResourceRegistry>().Allocation;

        _slots = new PagedMMF.PageSlotTable(allocator, owner, Entries);
        _directory = new PageDirectory(allocator, owner, _slots);
        _dictionary = new ConcurrentDictionary<int, int>(Environment.ProcessorCount, Entries);

        // File pages spread over a file 4x the cache, mapped to slots in random order: what a warm cache looks like.
        var rng = new Random(1136);
        var filePages = new int[Entries];
        for (var i = 0; i < Entries; i++)
        {
            filePages[i] = i * 4 + rng.Next(4);
        }

        rng.Shuffle(filePages);
        for (var slot = 0; slot < Entries; slot++)
        {
            var pi = _slots[slot];
            pi.FilePageIndex = filePages[slot];
            _directory.GetOrAdd(filePages[slot], slot);
            _dictionary[filePages[slot]] = slot;
        }

        _keys = new int[Threads][];
        for (var t = 0; t < Threads; t++)
        {
            _keys[t] = new int[LookupsPerThread];
            for (var i = 0; i < LookupsPerThread; i++)
            {
                _keys[t][i] = filePages[rng.Next(Entries)];
            }
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => (_serviceProvider as IDisposable)?.Dispose();

    [Benchmark(Baseline = true, OperationsPerInvoke = LookupsPerThread)]
    public long Dictionary()
    {
        long total = 0;
        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, t =>
        {
            var keys = _keys[t];
            var dictionary = _dictionary;
            var slots = _slots.Base;
            long sum = 0;
            for (var i = 0; i < keys.Length; i++)
            {
                if (dictionary.TryGetValue(keys[i], out var slot))
                {
                    sum += slots[slot].EncodedFilePageIndex;
                }
            }

            System.Threading.Interlocked.Add(ref total, sum);
        });
        return _sink = total;
    }

    [Benchmark(OperationsPerInvoke = LookupsPerThread)]
    public long Directory()
    {
        long total = 0;
        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, t =>
        {
            var keys = _keys[t];
            var directory = _directory;
            var slots = _slots.Base;
            long sum = 0;
            for (var i = 0; i < keys.Length; i++)
            {
                if (directory.TryGet(keys[i], out var slot))
                {
                    sum += slots[slot].EncodedFilePageIndex;
                }
            }

            System.Threading.Interlocked.Add(ref total, sum);
        });
        return _sink = total;
    }
}
