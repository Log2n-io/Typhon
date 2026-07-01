using System;
using System.Diagnostics;
using System.Threading;
using Typhon.CompetitiveBenchmark.Adapters;

namespace Typhon.CompetitiveBenchmark.Harness;

/// <summary>
/// C4-concurrent — the FAIR commit-durability test. Typhon's WAL is group-commit-optimized: the background writer
/// batches many in-flight commits into ONE direct device write, so per-commit durable cost amortizes under concurrency.
/// Single-thread C4 charges the full ~1 device-write per commit (one flush per commit) and is unrepresentative.
/// This runs Typhon SV-Committed @ D2 at increasing thread counts and reports committed throughput + amortized latency.
/// </summary>
public static class ConcurrentC4Runner
{
    public static void Run(int count = 200_000, int durationMs = 2000)
    {
        Console.WriteLine($"C4-concurrent — Typhon SV-Committed @ D2 (Immediate, on-disk WAL), durable commits/s vs threads, Zen 4 + NVMe");
        Console.WriteLine("Single-thread charges one device write per commit; concurrency lets the WAL writer batch them into one flush.");
        Console.WriteLine(new string('─', 76));

        int[] threadCounts = { 1, 2, 4, 8, 16 };

        TyphonAdapter.UseFua = true; // power-safe direct write
        var adapter = new TyphonAdapter(TyphonAdapter.Config.SvCommitted, DurabilityTier.D2);
        adapter.Load(count);

        // warmup
        RunThreads(adapter, count, 2, 500);

        Console.WriteLine($"{"threads",8} {"commits/s",14} {"amortized µs/commit",22} {"vs 1-thread",12}");
        Console.WriteLine(new string('─', 76));

        double baseline = 0;
        foreach (int threads in threadCounts)
        {
            long total = RunThreads(adapter, count, threads, durationMs);
            double perSec = total / (durationMs / 1000.0);
            double amortizedUs = threads / perSec * 1_000_000.0; // effective per-commit latency under `threads` concurrency
            if (threads == 1) baseline = perSec;
            Console.WriteLine($"{threads,8} {perSec,14:N0} {Measure.FormatNs(amortizedUs * 1000),22} {perSec / baseline,11:0.0}×");
        }

        adapter.Dispose();
        Console.WriteLine(new string('─', 76));
        Console.WriteLine("If commits/s scales well past the 1-thread rate, the writer is batching → the real durable-commit cost is");
        Console.WriteLine("« the single-thread number. That is Typhon's group-commit design; the competitors fsync inline (no batching).");
    }

    private static long RunThreads(TyphonAdapter adapter, int count, int threads, int durationMs)
    {
        long total = 0;
        var deadline = Stopwatch.StartNew();
        var workers = new Thread[threads];
        int chunk = count / threads;

        for (int t = 0; t < threads; t++)
        {
            int tid = t;
            workers[t] = new Thread(() =>
            {
                int lo = tid * chunk;
                int hi = (tid == threads - 1) ? count : lo + chunk;
                int k = lo;
                long local = 0;
                while (deadline.ElapsedMilliseconds < durationMs)
                {
                    adapter.PointWriteCommit(k, local);
                    local++;
                    if (++k >= hi) k = lo;
                }
                Interlocked.Add(ref total, local);
            });
            workers[t].IsBackground = true;
        }

        foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();
        return total;
    }
}
