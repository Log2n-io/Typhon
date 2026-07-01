using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// Characterizes the 16-thread read-scaling dip by PINNING worker threads to specific logical cores, decomposing the
/// possible causes — oversubscription (threads &gt; physical cores), SMT contention (two threads per physical core), and
/// cross-CCD traffic (threads spanning both 8-core complexes). Assumes the standard Windows Ryzen layout (verified by an
/// up-front sanity check): physical core k → logical {2k, 2k+1}; CCD0 = logical 0-15, CCD1 = logical 16-31.
/// </summary>
public static class ReadStressPinnedRunner
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr mask);

    public static long Sink;

    private const int Count = 1_000_000;
    private const int Batch = 256;
    private const int DurationMs = 3000;

    public static void Run()
    {
        Console.WriteLine("READ-STRESS PINNED — assumed 7950X layout: phys core k = logical {2k,2k+1}; CCD0=log0-15, CCD1=log16-31");
        var a = new TyphonConcurrentAdapter();
        a.Load(Count);

        var one = Measure(a, [0]).mps;

        // Sanity: 2 threads on ONE physical core (SMT siblings 0,1) should be markedly slower than 2 distinct cores (0,2).
        var sameCore = Measure(a, [0, 1]);
        var twoCores = Measure(a, [0, 2]);
        Console.WriteLine($"\nSanity (SMT layout): {{0,1}}=one phys core → {sameCore.mps:0.00} M/s · {{0,2}}=two phys cores → {twoCores.mps:0.00} M/s");
        Console.WriteLine(twoCores.mps > sameCore.mps * 1.25
            ? "  → CONFIRMED: {0,1} are SMT siblings; adjacent-logical layout holds, pinning below is valid."
            : "  → WARNING: {0,1} not slower — layout assumption suspect; interpret pinned results with care.");

        Console.WriteLine($"\nSingle-thread baseline (core 0): {one:0.00} M/s");

        Console.WriteLine("\n── S1: ONE CCD (CCD0), 1 thread per physical core — no SMT, no oversubscription ──");
        foreach (var n in new[] { 1, 2, 4, 8 })
        {
            PrintRow(a, $"{n}t", EvenCores(0, n), n, one);
        }

        Console.WriteLine("\n── S2: BOTH CCDs, 1 thread per physical core (16 phys cores) — adds cross-CCD, still no SMT/oversub ──");
        PrintRow(a, "16t", EvenCores(0, 16), 16, one);

        Console.WriteLine("\n── S3: ONE CCD (CCD0), BOTH SMT siblings per core (16 threads on 8 cores) — isolates SMT contention ──");
        PrintRow(a, "16t", Range(0, 16), 16, one);

        Console.WriteLine("\n── S4: ALL 32 logical (16 phys × 2 SMT, both CCDs) — full machine ──");
        PrintRow(a, "32t", Range(0, 32), 32, one);

        Console.WriteLine("\n── Reference: UNPINNED (OS scheduler chooses), the default benchmark condition ──");
        PrintRow(a, "16t", null, 16, one);
        PrintRow(a, "32t", null, 32, one);

        a.Dispose();
        Console.WriteLine($"\n(sink {(Sink == long.MinValue ? 0 : 1)})");
    }

    private static void PrintRow(TyphonConcurrentAdapter a, string label, int[] cores, int threads, double one)
    {
        var r = Measure(a, cores, threads);
        var eff = r.mps / threads / one;
        Console.WriteLine($"  {label,-5} {r.mps,7:0.00} M/s   {r.ns,6:0} ns/read   scaling-eff {eff,5:P0}");
    }

    // One logical core per physical core, starting at physical `startPhys`, count `n` → {2*startPhys, 2*startPhys+2, ...}.
    private static int[] EvenCores(int startPhys, int n)
    {
        var c = new int[n];
        for (int i = 0; i < n; i++)
        {
            c[i] = (startPhys + i) * 2;
        }
        return c;
    }

    private static int[] Range(int start, int n)
    {
        var c = new int[n];
        for (int i = 0; i < n; i++)
        {
            c[i] = start + i;
        }
        return c;
    }

    // Run the read stress with `cores.Length` threads (or `threadsIfNull` unpinned when cores==null). Returns throughput.
    private static (double mps, double ns) Measure(TyphonConcurrentAdapter a, int[] cores, int threadsIfNull = 0)
    {
        int threads = cores?.Length ?? threadsIfNull;
        long totalComps = 0;
        int part = Count / threads;
        var workers = new Thread[threads];
        var ready = new CountdownEvent(threads);
        var go = new ManualResetEventSlim(false);
        var sw = new Stopwatch();

        for (int t = 0; t < threads; t++)
        {
            int tid = t;
            workers[t] = new Thread(() =>
            {
                if (cores != null)
                {
                    SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << cores[tid]));
                }
                var w = a.CreateWorker();
                int lo = tid * part;
                int hi = (tid == threads - 1) ? Count : lo + part;
                int k = lo;
                long local = 0, sink = 0;
                ready.Signal();
                go.Wait();
                while (sw.ElapsedMilliseconds < DurationMs)
                {
                    sink += w.ReadBatch(k, Batch);
                    local += Batch;
                    k += Batch;
                    if (k + Batch > hi) k = lo;
                }
                w.Dispose();
                Interlocked.Add(ref totalComps, local);
                Interlocked.Add(ref Sink, sink);
            }) { IsBackground = true };
            workers[t].Start();
        }

        ready.Wait();
        sw.Start();
        go.Set();
        foreach (var w in workers) w.Join();
        sw.Stop();

        double mps = totalComps / (DurationMs / 1000.0) / 1_000_000.0;
        double ns = threads * 1e9 / (totalComps / (DurationMs / 1000.0));
        return (mps, ns);
    }
}
