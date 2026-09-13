using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SwgTatooine;

/// <summary>
/// <c>--chunk-stats</c>: how evenly the awareness system's chunks shared its work and the worker pool.
/// </summary>
/// <remarks>
/// Every chunk records its start, its end and the hits it counted. Per tick that gives the system's span (first start to last end), the pool it
/// actually kept busy (summed chunk time over span × workers), how much longer the slowest chunk ran than the mean one, and how much more work the
/// heaviest chunk carried. Chunks are sized by entity count; an interest query's cost is not, so this is the direct test of whether equal-count chunks
/// make equal work.
/// </remarks>
public sealed partial class SimBridge
{
    private long[] _chunkStart;
    private long[] _chunkEnd;
    private long[] _chunkTick;
    private long[] _chunkHits;
    private long[] _chunkQueries;
    private int _chunkCount;

    /// <summary>Sized once, at construction: parallel workers must never race a lazy allocation.</summary>
    private void InitChunkStats()
    {
        if (!_config.ChunkStats)
        {
            return;
        }

        // Measured ticks only (RecordChunk skips the warm-up). Awareness's ChunksPerWorker is 2, and the cost rule goes to twice that width: 4 chunks per
        // worker at most.
        var capacity = (_config.MeasuredTicks + 16) * ((4 * _config.ResolveWorkerCount()) + 16);
        _chunkStart = new long[capacity];
        _chunkEnd = new long[capacity];
        _chunkTick = new long[capacity];
        _chunkHits = new long[capacity];
        _chunkQueries = new long[capacity];
    }

    private void RecordChunk(long tick, long start, long end, long hits, long queries)
    {
        var i = Interlocked.Increment(ref _chunkCount) - 1;
        if (i >= _chunkStart.Length)
        {
            return;
        }

        _chunkStart[i] = start;
        _chunkEnd[i] = end;
        _chunkTick[i] = tick;
        _chunkHits[i] = hits;
        _chunkQueries[i] = queries;
    }

    /// <summary>Per-tick balance of the awareness chunks, as medians over the measured window.</summary>
    public void PrintChunkStats()
    {
        if (!_config.ChunkStats)
        {
            return;
        }

        var n = Math.Min(_chunkCount, _chunkStart.Length);
        var byTick = new Dictionary<long, List<int>>();
        for (var i = 0; i < n; i++)
        {
            if (!byTick.TryGetValue(_chunkTick[i], out var list))
            {
                byTick[_chunkTick[i]] = list = [];
            }

            list.Add(i);
        }

        var workers = _config.ResolveWorkerCount();
        var spanUs = new List<double>();
        var poolBusy = new List<double>();
        var slowestOverMean = new List<double>();
        var slowestOverSpan = new List<double>();
        var heaviestOverMean = new List<double>();
        var chunks = new List<double>();
        var busyUs = new List<double>();
        var straggleUs = new List<double>();
        long busyAll = 0, hitsAll = 0, queriesAll = 0;
        foreach (var list in byTick.Values)
        {
            long first = long.MaxValue, last = long.MinValue, busy = 0, slowest = 0, hitsTotal = 0, heaviest = 0;
            foreach (var i in list)
            {
                first = Math.Min(first, _chunkStart[i]);
                last = Math.Max(last, _chunkEnd[i]);
                var d = _chunkEnd[i] - _chunkStart[i];
                busy += d;
                slowest = Math.Max(slowest, d);
                hitsTotal += _chunkHits[i];
                heaviest = Math.Max(heaviest, _chunkHits[i]);
                queriesAll += _chunkQueries[i];
            }

            busyAll += busy;
            hitsAll += hitsTotal;
            busyUs.Add(busy * 1e6 / Stopwatch.Frequency);

            var span = last - first;
            if (span <= 0 || busy <= 0)
            {
                continue;
            }

            spanUs.Add(span * 1e6 / Stopwatch.Frequency);
            poolBusy.Add(100.0 * busy / ((double)span * workers));
            slowestOverMean.Add(slowest / (busy / (double)list.Count));
            slowestOverSpan.Add(100.0 * slowest / span);
            heaviestOverMean.Add(hitsTotal == 0 ? 0 : heaviest / (hitsTotal / (double)list.Count));
            chunks.Add(list.Count);

            // The span a perfect split of the same work over the whole pool would have taken, and what the pool waited beyond it: too few chunks, or
            // the last one running long.
            straggleUs.Add((span - (busy / (double)workers)) * 1e6 / Stopwatch.Frequency);
        }

        Console.WriteLine();
        Console.WriteLine($"  awareness chunks, median over {spanUs.Count} ticks: {Median(chunks):F0} chunks, span {Median(spanUs):F0} us, "
            + $"pool busy {Median(poolBusy):F1} % of {workers} workers, slowest chunk {Median(slowestOverMean):F2}x the mean and "
            + $"{Median(slowestOverSpan):F0} % of the span, heaviest chunk {Median(heaviestOverMean):F2}x the mean hits");
        spanUs.Sort();
        straggleUs.Sort();
        Console.WriteLine($"  awareness tail: span p99 {At(spanUs, 0.99):F0}, max {At(spanUs, 1):F0} us; "
            + $"wait past a perfect split p50 {At(straggleUs, 0.5):F0}, p99 {At(straggleUs, 0.99):F0}, max {At(straggleUs, 1):F0} us");

        // Worker time, not wall time: the sum of every chunk's duration, divided by the queries and hits those chunks served.
        var busyNs = busyAll * 1e9 / Stopwatch.Frequency;
        Console.WriteLine($"  awareness work: {busyNs / Math.Max(1, queriesAll):F1} ns per query, {busyNs / Math.Max(1, hitsAll):F2} ns per hit, "
            + $"{Median(busyUs):F0} us of worker time per tick (median), {hitsAll / (double)Math.Max(1, queriesAll):F1} hits per query");
    }

    private static double At(List<double> sorted, double p) =>
        sorted.Count == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * p) - 1, 0, sorted.Count - 1)];
}
