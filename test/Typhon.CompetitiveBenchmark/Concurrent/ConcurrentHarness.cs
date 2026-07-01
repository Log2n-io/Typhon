using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// Shared concurrency driver for the A-series YCSB scenarios (mixed, RMW, scan). Spawns N worker threads PINNED to one CCD
/// (the 7950X's cross-CCD Infinity-Fabric traffic otherwise dominates past one 8-core complex — see the NUMA finding), over
/// disjoint key partitions, with a barrier so worker construction is excluded from the timed window. Each runner supplies
/// only its inner loop; pinning, partitioning, the barrier, and timing live here.
/// </summary>
public static class ConcurrentHarness
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr mask);

    // CCD0 logical cores, physical-cores-first then SMT siblings (7950X: phys core k = logical {2k, 2k+1}).
    private static readonly int[] Ccd0Order = { 0, 2, 4, 6, 8, 10, 12, 14, 1, 3, 5, 7, 9, 11, 13, 15 };

    /// <summary>One CCD = 8 physical cores = 16 logical threads, so the sweep caps at 16.</summary>
    public static readonly int[] Threads = { 1, 4, 8, 16 };

    public static long Sink;

    /// <summary>
    /// Runs <paramref name="threads"/> CCD-pinned workers over disjoint <c>[lo,hi)</c> partitions of <paramref name="count"/>.
    /// Each worker runs <paramref name="loop"/>(worker, lo, hi, running) until <c>running()</c> is false and returns the ops it
    /// completed. Worker creation is excluded from timing. Returns total ops/second over the measured window.
    /// </summary>
    public static double Run(IConcurrentAdapter a, int threads, int count, int durationMs,
        Func<IWorker, int, int, Func<bool>, long> loop)
    {
        long total = 0;
        int part = count / threads;
        var workers = new Thread[threads];
        var ready = new CountdownEvent(threads);
        var go = new ManualResetEventSlim(false);
        var sw = new Stopwatch();
        Func<bool> running = () => sw.ElapsedMilliseconds < durationMs;

        for (int t = 0; t < threads; t++)
        {
            int tid = t;
            workers[t] = new Thread(() =>
            {
                SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << Ccd0Order[tid % Ccd0Order.Length]));
                var w = a.CreateWorker();
                int lo = tid * part;
                int hi = (tid == threads - 1) ? count : lo + part;
                ready.Signal();
                go.Wait();
                long ops = loop(w, lo, hi, running);
                w.Dispose();
                Interlocked.Add(ref total, ops);
            }) { IsBackground = true };
            workers[t].Start();
        }

        ready.Wait();
        sw.Start();
        go.Set();
        foreach (var w in workers) w.Join();
        sw.Stop();
        return total / (durationMs / 1000.0);
    }

    /// <summary>Per-worker xorshift32 — cheap, allocation-free, independent per thread (seed by partition).</summary>
    public struct Rng
    {
        private uint _s;
        public Rng(uint seed) => _s = seed == 0 ? 0x9E3779B9u : seed;

        public uint Next()
        {
            _s ^= _s << 13;
            _s ^= _s >> 17;
            _s ^= _s << 5;
            return _s;
        }

        /// <summary>Uniform key in [lo, hi).</summary>
        public int Key(int lo, int hi) => lo + (int)(Next() % (uint)(hi - lo));
    }
}
