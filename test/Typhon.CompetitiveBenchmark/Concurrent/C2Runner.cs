using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// C2 — MVCC: readers never block writers, multiple concurrent writers. Dedicated reader threads and writer threads run
/// simultaneously on the same keyspace; we report each group's throughput separately. The story is WRITER scaling:
/// Typhon's lock-free writers scale with thread count, while LMDB (single global write mutex) and SQLite (single writer)
/// cap — readers stay fast for all (snapshot / lock-free reads). CCD-pinned, 8 readers + 8 writers (= one CCD).
/// </summary>
public static class C2Runner
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr mask);
    private static readonly int[] Ccd0 = { 0, 2, 4, 6, 8, 10, 12, 14, 1, 3, 5, 7, 9, 11, 13, 15 };

    public static long Sink;

    public static void Run(int count = 1_000_000, int durationMs = 600)
    {
        var root = Path.Combine(Path.GetTempPath(), "typhon-c2");
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        Directory.CreateDirectory(root);

        const int readers = 8, writers = 8;
        Console.WriteLine($"C2 — concurrent readers + writers (MVCC) — {count:N0} rows, {readers} readers + {writers} writers, one CCD, Zen 4");
        Console.WriteLine("The story is WRITER scaling: lock-free (Typhon) vs single global writer (LMDB/SQLite). Readers stay fast for all.");
        Console.WriteLine(new string('─', 72));
        Console.WriteLine($"{"engine",-12} {"reader M ops/s",16} {"writer M ops/s",16}");
        Console.WriteLine(new string('─', 72));

        var factories = new (string label, Func<IConcurrentAdapter> make)[]
        {
            ("Typhon SV", () => new TyphonConcurrentAdapter()),
            ("SQLite", () => new SqliteConcurrentAdapter(root)),
            ("RocksDB", () => new RocksDbConcurrentAdapter(root)),
            ("LMDB", () => new LmdbConcurrentAdapter(root)),
            ("FASTER", () => new FasterConcurrentAdapter(root)),
        };

        foreach (var (label, make) in factories)
        {
            try
            {
                var a = make();
                a.Load(count);
                var (rMops, wMops) = RunReadersWriters(a, readers, writers, count, durationMs);
                Console.WriteLine($"{label,-12} {rMops,16:0.00} {wMops,16:0.00}");
                a.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{label,-12}  SKIPPED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine(new string('─', 72));
        Console.WriteLine($"(sink {(Sink == long.MinValue ? 0 : 1)})");
    }

    private static (double rMops, double wMops) RunReadersWriters(IConcurrentAdapter a, int readers, int writers, int count, int durationMs)
    {
        long readerOps = 0, writerOps = 0;
        int total = readers + writers;
        int wPart = count / writers;
        var threads = new Thread[total];
        var ready = new CountdownEvent(total);
        var go = new ManualResetEventSlim(false);
        var sw = new Stopwatch();
        Func<bool> running = () => sw.ElapsedMilliseconds < durationMs;

        for (int t = 0; t < total; t++)
        {
            int tid = t;
            bool isReader = tid < readers;
            threads[t] = new Thread(() =>
            {
                SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << Ccd0[tid % Ccd0.Length]));
                var w = a.CreateWorker();
                var rng = new ConcurrentHarness.Rng((uint)(tid * 2654435761u + 17));
                long ops = 0, sink = 0;
                ready.Signal();
                go.Wait();
                if (isReader)
                {
                    // read random keys across the WHOLE keyspace while writers mutate
                    while (running())
                    {
                        for (int i = 0; i < 64; i++) { sink += w.ReadBatch(rng.Key(0, count), 1); ops++; }
                    }
                    Interlocked.Add(ref readerOps, ops);
                    Interlocked.Add(ref Sink, sink);
                }
                else
                {
                    // each writer owns a disjoint partition (no artificial write-write conflict)
                    int lo = (tid - readers) * wPart;
                    int hi = (tid - readers == writers - 1) ? count : lo + wPart;
                    long v = 0;
                    while (running())
                    {
                        for (int i = 0; i < 64; i++) { w.UpdateBatch(rng.Key(lo, hi), 1, v++); ops++; }
                    }
                    Interlocked.Add(ref writerOps, ops);
                }
                w.Dispose();
            }) { IsBackground = true };
            threads[t].Start();
        }

        ready.Wait();
        sw.Start();
        go.Set();
        foreach (var th in threads) th.Join();
        sw.Stop();

        double secs = durationMs / 1000.0;
        return (readerOps / secs / 1e6, writerOps / secs / 1e6);
    }
}
