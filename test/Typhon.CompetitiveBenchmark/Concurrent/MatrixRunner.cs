using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// The real comparison: CRUD on plain components/rows across a CONCURRENCY × BATCH-SIZE matrix. Single-config numbers are
/// meaningless for a lock-free engine — this sweeps threads × batch {1,8,16,256,512,1024 rows/op} and reports throughput
/// (M components/s). Threads use disjoint key partitions (no artificial conflict).
/// <para>
/// Workers are PINNED to a single CCD (the 7950X has 2 × 8-core complexes; cross-CCD shared-line traffic over Infinity
/// Fabric otherwise dominates at 16+ threads and pollutes the comparison). One CCD = 8 physical cores = 16 logical
/// threads, so the sweep caps at 16. The pin order fills distinct physical cores first, then SMT siblings.
/// </para>
/// </summary>
public static class MatrixRunner
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr mask);

    public static long Sink;

    // CCD0 logical cores, physical-cores-first then SMT siblings (7950X: phys core k = logical {2k, 2k+1}, CCD0 = log 0-15).
    private static readonly int[] Ccd0Order = { 0, 2, 4, 6, 8, 10, 12, 14, 1, 3, 5, 7, 9, 11, 13, 15 };

    private static readonly int[] Threads = { 1, 4, 8, 16 };
    private static readonly int[] Batches = { 1, 8, 16, 256, 512, 1024 };

    public static void Run(int count = 1_000_000, int durationMs = 400)
    {
        var root = Path.Combine(Path.GetTempPath(), "typhon-matrix");
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        Directory.CreateDirectory(root);

        Console.WriteLine($"CRUD concurrency × batch matrix — {count:N0} components, throughput = M components/s, Zen 4");
        Console.WriteLine($"threads {{{string.Join(",", Threads)}}} × batch {{{string.Join(",", Batches)}}} rows/op, {durationMs} ms/cell, disjoint partitions — PINNED to one CCD");

        var factories = new (string label, Func<IConcurrentAdapter> make)[]
        {
            ("Typhon SV", () => new TyphonConcurrentAdapter()),
            ("SQLite", () => new SqliteConcurrentAdapter(root)),
            ("RocksDB", () => new RocksDbConcurrentAdapter(root)),
            ("LMDB", () => new LmdbConcurrentAdapter(root)),
            ("FASTER", () => new FasterConcurrentAdapter(root)),
        };

        foreach (var scenarioRead in new[] { true, false })
        {
            foreach (var (label, make) in factories)
            {
                try
                {
                    var a = make();
                    a.Load(count);
                    PrintMatrix(a, scenarioRead, count, durationMs);
                    a.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n══ {label} — {(scenarioRead ? "READ" : "UPDATE")} ══  SKIPPED: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Console.WriteLine($"\n(GC.KeepAlive: {(Sink == long.MinValue ? "?" : "ok")})");
    }

    private static void PrintMatrix(IConcurrentAdapter a, bool read, int count, int durationMs)
    {
        Console.WriteLine($"\n══ {a.Name} — {(read ? "READ" : "UPDATE")} (M components/s) ══");
        Console.Write($"{"threads\\batch",-14}");
        foreach (var b in Batches) Console.Write($"{b,10}");
        Console.WriteLine();

        // warmup one cell
        Cell(a, read, 2, 64, count, 200);

        foreach (var t in Threads)
        {
            Console.Write($"{t,-14}");
            foreach (var b in Batches)
            {
                double mps = Cell(a, read, t, b, count, durationMs) / 1_000_000.0;
                Console.Write($"{mps,10:0.00}");
            }
            Console.WriteLine();
        }
    }

    private static double Cell(IConcurrentAdapter a, bool read, int threads, int batch, int count, int durationMs)
    {
        long totalComps = 0;
        int part = count / threads;
        var workers = new Thread[threads];
        var ready = new CountdownEvent(threads);     // workers signal once their PTA/connection is built
        var go = new ManualResetEventSlim(false);     // released only after timing starts
        var sw = new Stopwatch();

        for (int t = 0; t < threads; t++)
        {
            int tid = t;
            workers[t] = new Thread(() =>
            {
                SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << Ccd0Order[tid % Ccd0Order.Length])); // pin to one CCD
                var w = a.CreateWorker();             // ← worker (PTA / SQLite connection) creation — EXCLUDED from timing
                int lo = tid * part;
                int hi = (tid == threads - 1) ? count : lo + part;
                int b = Math.Min(batch, hi - lo);
                int k = lo;
                long localComps = 0;
                long sink = 0;
                ready.Signal();
                go.Wait();                            // all workers built; timer is running
                while (sw.ElapsedMilliseconds < durationMs)
                {
                    if (read) sink += w.ReadBatch(k, b); else w.UpdateBatch(k, b, localComps);
                    localComps += b;
                    k += b;
                    if (k + b > hi) k = lo;
                }
                w.Dispose();
                Interlocked.Add(ref totalComps, localComps);
                if (read) Interlocked.Add(ref Sink, sink);
            }) { IsBackground = true };
            workers[t].Start();
        }

        ready.Wait();                                 // every worker's PTA/connection is constructed
        sw.Start();
        go.Set();                                     // begin the measured window
        foreach (var w in workers) w.Join();
        sw.Stop();
        return totalComps / (durationMs / 1000.0);    // ops counted within [go, durationMs]; setup + dispose excluded
    }
}
