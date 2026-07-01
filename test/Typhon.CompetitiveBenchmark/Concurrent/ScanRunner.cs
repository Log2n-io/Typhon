using System;
using System.IO;
using System.Threading;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// A6 — YCSB-E ordered range scan. Each op seeks to a random start key and reads the next <see cref="ScanLen"/> rows in key
/// order, summing values. Ordered B+Tree/LSM cursors (Typhon secondary index, SQLite PK, RocksDB iterator, LMDB cursor) all
/// apply; FASTER is excluded (hash index — no ordered scan). CCD-pinned, throughput = M rows scanned/s.
/// </summary>
public static class ScanRunner
{
    private const int ScanLen = 100; // YCSB-E maxscanlength

    public static void Run(int count = 1_000_000, int durationMs = 400)
    {
        var root = Path.Combine(Path.GetTempPath(), "typhon-scan");
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        Directory.CreateDirectory(root);

        Console.WriteLine($"A6 — YCSB-E ordered range scan — {count:N0} rows, scanlen={ScanLen}, throughput = M rows/s, one CCD, Zen 4");
        Console.WriteLine("(FASTER excluded — hash index has no ordered range scan.)");
        Console.WriteLine($"\n══ range scan (M rows scanned/s) ══");
        Console.Write($"{"engine",-12}");
        foreach (var t in ConcurrentHarness.Threads) Console.Write($"{t + "t",10}");
        Console.WriteLine();

        var factories = new (string label, Func<IConcurrentAdapter> make)[]
        {
            ("Typhon idx", () => new TyphonScanConcurrentAdapter()),
            ("SQLite", () => new SqliteConcurrentAdapter(root)),
            ("RocksDB", () => new RocksDbConcurrentAdapter(root)),
            ("LMDB", () => new LmdbConcurrentAdapter(root)),
        };

        foreach (var (label, make) in factories)
        {
            try
            {
                var a = make();
                a.Load(count);

                // Correctness gate: a scan of 100 rows from key 1000 must sum values[1000..1099] = Σk = 104950 (value==key).
                // A byte-order or ordering bug would return a different (non-zero) sum, which throughput alone can't catch.
                const long expected = (1000L + 1099L) * 100 / 2;
                using (var vw = a.CreateWorker())
                {
                    long got = vw.RangeScan(1000, 100);
                    if (got != expected) { Console.WriteLine($"{label,-12}  ✗ SCAN INCORRECT: got {got}, expected {expected}"); a.Dispose(); continue; }
                }

                ConcurrentHarness.Run(a, 2, count, 150, ScanLoop); // warmup
                Console.Write($"{label,-12}");
                foreach (var t in ConcurrentHarness.Threads)
                {
                    double mrows = ConcurrentHarness.Run(a, t, count, durationMs, ScanLoop) / 1_000_000.0;
                    Console.Write($"{mrows,10:0.00}");
                }
                Console.WriteLine();
                a.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{label,-12}  SKIPPED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"\n(sink {(ConcurrentHarness.Sink == long.MinValue ? 0 : 1)})");
    }

    private static long ScanLoop(IWorker w, int lo, int hi, Func<bool> running)
    {
        var rng = new ConcurrentHarness.Rng((uint)(lo * 2654435761u + 13));
        int hiStart = Math.Max(lo + 1, hi - ScanLen); // keep a full scan inside the partition
        long rows = 0, sink = 0;
        while (running())
        {
            for (int i = 0; i < 16; i++)
            {
                sink += w.RangeScan(rng.Key(lo, hiStart), ScanLen);
                rows += ScanLen;
            }
        }
        Interlocked.Add(ref ConcurrentHarness.Sink, sink);
        return rows;
    }
}
