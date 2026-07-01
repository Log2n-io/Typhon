using System;
using System.IO;
using System.Threading;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// A4 — YCSB-F read-modify-write. Each op reads a key, increments it, writes it back atomically (uniform-random key in the
/// worker's disjoint partition). This is the transactional read-before-write profile: Typhon's lock-free SV RMW and FASTER's
/// native RMW scale; SQLite (BEGIN IMMEDIATE) and LMDB (single write mutex) serialize all writers. CCD-pinned, per-op (batch 1).
/// </summary>
public static class RmwRunner
{
    public static void Run(int count = 1_000_000, int durationMs = 400)
    {
        var root = Path.Combine(Path.GetTempPath(), "typhon-rmw");
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        Directory.CreateDirectory(root);

        Console.WriteLine($"A4 — YCSB-F read-modify-write — {count:N0} rows, per-op, throughput = M ops/s, one CCD, Zen 4");
        Console.WriteLine($"\n══ RMW (read+increment+write, M ops/s) ══");
        Console.Write($"{"engine",-12}");
        foreach (var t in ConcurrentHarness.Threads) Console.Write($"{t + "t",10}");
        Console.WriteLine();

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
                ConcurrentHarness.Run(a, 2, count, 150, RmwLoop); // warmup
                Console.Write($"{label,-12}");
                foreach (var t in ConcurrentHarness.Threads)
                {
                    double mops = ConcurrentHarness.Run(a, t, count, durationMs, RmwLoop) / 1_000_000.0;
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

    private static long RmwLoop(IWorker w, int lo, int hi, Func<bool> running)
    {
        var rng = new ConcurrentHarness.Rng((uint)(lo * 2654435761u + 7));
        long ops = 0;
        while (running())
        {
            for (int i = 0; i < 64; i++)
            {
                w.RmwBatch(rng.Key(lo, hi), 1);
                ops++;
            }
        }
        return ops;
    }
}
