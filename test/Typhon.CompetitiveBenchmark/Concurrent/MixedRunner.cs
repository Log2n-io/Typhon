using System;
using System.IO;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// A2 (YCSB-A, 50/50 read/update) + A3 (YCSB-B, 95/5) — the mixed-workload scenarios. Each operation is independently a
/// read OR an update (uniform-random key in the worker's partition), single-op (the YCSB point-op model). This is where
/// MVCC matters: Typhon's readers never block its writers, while LMDB and SQLite serialize all writes on one mutex — the
/// mix exposes that. CCD-pinned, threads {1,4,8,16}.
/// </summary>
public static class MixedRunner
{
    public static void Run(int count = 1_000_000, int durationMs = 400)
    {
        var root = Path.Combine(Path.GetTempPath(), "typhon-ycsb");
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        Directory.CreateDirectory(root);

        Console.WriteLine($"YCSB mixed read/update — {count:N0} rows, single-op, throughput = M ops/s, one CCD, Zen 4");

        var factories = new (string label, Func<IConcurrentAdapter> make)[]
        {
            ("Typhon SV", () => new TyphonConcurrentAdapter()),
            ("SQLite", () => new SqliteConcurrentAdapter(root)),
            ("RocksDB", () => new RocksDbConcurrentAdapter(root)),
            ("LMDB", () => new LmdbConcurrentAdapter(root)),
            ("FASTER", () => new FasterConcurrentAdapter(root)),
        };

        foreach (var (name, readPct) in new[] { ("A2 — YCSB-A 50/50 read/update", 50), ("A3 — YCSB-B 95/5 read/update", 95) })
        {
            Console.WriteLine($"\n══ {name} (M ops/s) ══");
            Console.Write($"{"engine",-12}");
            foreach (var t in ConcurrentHarness.Threads) Console.Write($"{t + "t",10}");
            Console.WriteLine();

            foreach (var (label, make) in factories)
            {
                try
                {
                    var a = make();
                    a.Load(count);
                    ConcurrentHarness.Run(a, 2, count, 150, MixLoop(readPct)); // warmup
                    Console.Write($"{label,-12}");
                    foreach (var t in ConcurrentHarness.Threads)
                    {
                        double mops = ConcurrentHarness.Run(a, t, count, durationMs, MixLoop(readPct)) / 1_000_000.0;
                        Console.Write($"{mops,10:0.00}");
                    }
                    Console.WriteLine();
                    a.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{label,-12}  SKIPPED: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Console.WriteLine($"\n(sink {(ConcurrentHarness.Sink == long.MinValue ? 0 : 1)})");
    }

    private static Func<IWorker, int, int, Func<bool>, long> MixLoop(int readPct) => (w, lo, hi, running) =>
    {
        var rng = new ConcurrentHarness.Rng((uint)(lo * 2654435761u + 1));
        long ops = 0, sink = 0, v = 0;
        while (running())
        {
            for (int i = 0; i < 64; i++) // amortize the running()/Stopwatch check over a chunk of ops
            {
                int k = rng.Key(lo, hi);
                if (rng.Next() % 100 < (uint)readPct) sink += w.ReadBatch(k, 1);
                else w.UpdateBatch(k, 1, v++);
                ops++;
            }
        }
        System.Threading.Interlocked.Add(ref ConcurrentHarness.Sink, sink);
        return ops;
    };
}
