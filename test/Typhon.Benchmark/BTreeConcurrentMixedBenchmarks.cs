using BenchmarkDotNet.Attributes;
using System;
using System.Threading;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// BTree: Mixed Read-Write Workloads — The most realistic scenario.
// Writers hold exclusive lock, blocking ALL readers even if they touch
// different leaves. This is the scenario where OLC shines most:
// readers proceed unimpeded regardless of writer activity.
//
// Real-world:
//   95/5:   game server — many entity queries, few position updates per frame
//   50/50:  batch import while serving queries
//   BulkInsert: schema migration backfill while queries continue
//
// Profile mapping:
//   Fast:   ThreadCount = [1, 32], Mixed_95Read_5Write only
//   Medium: ThreadCount = [1, 8, 32]
//   Full:   ThreadCount = [1, 4, 8, 16, 32]
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 2, iterationCount: 5)]
[BenchmarkCategory("BTree", "Concurrency", "BTreeMedium")]
public class BTreeConcurrentMixedBenchmarks
{
    private BTreeBenchmarkHelper _helper;
    private EpochManager _epochManager;
    private ChunkBasedSegment<PersistentStore> _segment;
    private LongSingleBTree<PersistentStore> _tree;
    private long[][] _perThreadReadKeys;
    private long[][] _perThreadWriteKeys;

    private const int PreFillCount = 10_000;
    private const int OpsPerInvocation = 100_000;
    private long _nextBulkKey = PreFillCount + 1_000_000;

    [Params(1, 4, 8, 16, 32)]
    public int ThreadCount;

    [GlobalSetup]
    public unsafe void GlobalSetup()
    {
        _helper = new BTreeBenchmarkHelper();
        _helper.Setup(1000);
        _epochManager = _helper.EpochManager;

        _segment = _helper.AllocateSegment<Index64Chunk>(2000);
        _tree = new LongSingleBTree<PersistentStore>(_segment);
        BTreeBenchmarkHelper.PreFillLong(_tree, _segment, PreFillCount);

        // Pre-generate keys for each thread
        var maxOpsPerThread = OpsPerInvocation; // upper bound
        _perThreadReadKeys = new long[ThreadCount][];
        _perThreadWriteKeys = new long[ThreadCount][];
        for (int t = 0; t < ThreadCount; t++)
        {
            _perThreadReadKeys[t] = BTreeBenchmarkHelper.GenerateRandomLongKeys(maxOpsPerThread, PreFillCount, seed: 300 + t);

            // Write keys: disjoint ranges per thread
            var rangeStart = 1 + t * (PreFillCount / ThreadCount);
            var rangeEnd = (t == ThreadCount - 1) ? PreFillCount : rangeStart + (PreFillCount / ThreadCount) - 1;
            var rangeSize = rangeEnd - rangeStart + 1;
            _perThreadWriteKeys[t] = new long[maxOpsPerThread];
            var rng = new Random(400 + t);
            for (int i = 0; i < maxOpsPerThread; i++)
            {
                _perThreadWriteKeys[t][i] = rangeStart + rng.NextInt64(0, rangeSize);
            }
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _helper?.Dispose();

    /// <summary>
    /// 95% readers, 5% writers. The most common real-world pattern.
    /// At 32 threads: 30 readers + 2 writers (rounding to nearest).
    ///
    /// Current model: 2 writers block all 30 readers whenever they acquire exclusive lock.
    /// After OLC: readers never blocked. Writers only latch 1-2 leaves.
    /// Expected: dramatic throughput improvement at high thread counts.
    /// </summary>
    [Benchmark(OperationsPerInvoke = OpsPerInvocation)]
    [BenchmarkCategory("BTreeFast")]
    public void Mixed_95Read_5Write()
    {
        RunMixedWorkload(readPercent: 95);
    }

    /// <summary>
    /// 50% readers, 50% writers. Balanced workload — heavy write pressure.
    /// At 32 threads: 16 readers + 16 writers.
    ///
    /// Current model: writers dominate lock, readers starve.
    /// After OLC: readers unaffected by writers on different leaves.
    /// </summary>
    [Benchmark(OperationsPerInvoke = OpsPerInvocation)]
    public void Mixed_50Read_50Write()
    {
        RunMixedWorkload(readPercent: 50);
    }

    /// <summary>
    /// Half the threads do sequential bulk inserts (simulating schema backfill),
    /// other half do random lookups (simulating concurrent queries).
    ///
    /// Current model: bulk inserter holds exclusive lock nearly continuously,
    /// starving all readers.
    /// After OLC: readers proceed optimistically while bulk inserts only latch leaves.
    /// </summary>
    [Benchmark(OperationsPerInvoke = OpsPerInvocation)]
    public void Mixed_ReadDuringBulkInsert()
    {
        if (ThreadCount == 1)
        {
            // Single-threaded: just do reads (no contention to measure)
            var accessor = _segment.CreateChunkAccessor();
            var keys = _perThreadReadKeys[0];
            for (int i = 0; i < OpsPerInvocation; i++)
            {
                _tree.TryGet(keys[i % keys.Length], ref accessor);
            }
            accessor.Dispose();
            return;
        }

        var readerCount = ThreadCount / 2;
        var writerCount = ThreadCount - readerCount;
        var readerOps = OpsPerInvocation * readerCount / ThreadCount;
        var writerOps = OpsPerInvocation * writerCount / ThreadCount;

        BTreeBenchmarkHelper.RunConcurrentWithBarrier(ThreadCount, _epochManager, threadId =>
        {
            var accessor = _segment.CreateChunkAccessor();
            if (threadId < readerCount)
            {
                // Reader: random lookups
                var keys = _perThreadReadKeys[threadId];
                var ops = readerOps / readerCount;
                for (int i = 0; i < ops; i++)
                {
                    _tree.TryGet(keys[i % keys.Length], ref accessor);
                }
            }
            else
            {
                // Writer: sequential bulk inserts (keys persist across invocations via field).
                // Remove immediately after Add to keep tree size stable across BDN invocations.
                var ops = writerOps / writerCount;
                for (int i = 0; i < ops; i++)
                {
                    var key = Interlocked.Increment(ref _nextBulkKey);
                    _tree.Add(key, 42, ref accessor);
                    _tree.Remove(key, out _, ref accessor);
                }
            }
            accessor.Dispose();
        });
    }

    private void RunMixedWorkload(int readPercent)
    {
        if (ThreadCount == 1)
        {
            // Single-threaded: interleave reads and writes
            var accessor = _segment.CreateChunkAccessor();
            var readKeys = _perThreadReadKeys[0];
            var writeKeys = _perThreadWriteKeys[0];
            for (int i = 0; i < OpsPerInvocation; i++)
            {
                if (i % 100 < readPercent)
                {
                    _tree.TryGet(readKeys[i % readKeys.Length], ref accessor);
                }
                else
                {
                    var key = writeKeys[i % writeKeys.Length];
                    if (_tree.Remove(key, out var val, ref accessor))
                    {
                        _tree.Add(key, val, ref accessor);
                    }
                }
            }
            accessor.Dispose();
            return;
        }

        var readerThreads = Math.Max(1, ThreadCount * readPercent / 100);
        var writerThreads = ThreadCount - readerThreads;
        var opsPerReader = OpsPerInvocation * readPercent / 100 / readerThreads;
        var opsPerWriter = writerThreads > 0 ? OpsPerInvocation * (100 - readPercent) / 100 / writerThreads : 0;

        BTreeBenchmarkHelper.RunConcurrentWithBarrier(ThreadCount, _epochManager, threadId =>
        {
            var accessor = _segment.CreateChunkAccessor();
            if (threadId < readerThreads)
            {
                // Reader
                var keys = _perThreadReadKeys[threadId];
                for (int i = 0; i < opsPerReader; i++)
                {
                    _tree.TryGet(keys[i % keys.Length], ref accessor);
                }
            }
            else
            {
                // Writer: remove + reinsert at random positions in this thread's range
                var keys = _perThreadWriteKeys[threadId];
                for (int i = 0; i < opsPerWriter; i++)
                {
                    var key = keys[i % keys.Length];
                    if (_tree.Remove(key, out var val, ref accessor))
                    {
                        _tree.Add(key, val, ref accessor);
                    }
                }
            }
            accessor.Dispose();
        });
    }
}
