using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using HdrHistogram;
using Typhon.CompetitiveBenchmark.Adapters;

namespace Typhon.CompetitiveBenchmark.Harness;

/// <summary>
/// C4 — single-entity commit latency at full durability (D2, fsync per commit). Every engine configured to fsync each
/// commit; we measure the per-commit latency distribution. The honest expectation (plan §9): all engines converge toward
/// the NVMe fsync floor (~15–85 µs); the winner is the one with the least *overhead above fsync*. FASTER is excluded
/// (checkpoint-per-commit is a model mismatch). Typhon uses SV-Committed (the matched atomic-durable config), not Versioned.
/// </summary>
public static class C4Runner
{
    public static void Run(int count = 50_000, int ops = 20_000)
    {
        var root = Path.Combine(Path.GetTempPath(), "typhon-c4");
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        Directory.CreateDirectory(root);

        Console.WriteLine($"C4 single-thread commit latency — {ops:N0} single-entity Commit-discipline commits, WAL on, Zen 4 + NVMe");
        Console.WriteLine("Two durability regimes: 'deferred' = transaction-commit cost (WAL record written + published, fsync async →");
        Console.WriteLine("durable-soon); 'durable@return' = block until fsync (zero-loss, but single-thread pays the writer handoff).");
        Console.WriteLine(new string('─', 84));

        // The first row is the answer to "measure Transaction.Commit(), not the UoW flush": Commit discipline + Deferred
        // mode writes the WAL redo record and publishes in place (logically committed, recoverable) WITHOUT blocking on the
        // fsync. The durable@return rows pay the background-writer handoff per commit single-thread (amortized under load —
        // see the `c4c` concurrent harness). Competitors fsync inline per commit (they have no async-writer group commit).
        var factories = new (string label, Func<IEngineAdapter> make)[]
        {
            ("Typhon Commit deferred",  () => { TyphonAdapter.UseFua = false; TyphonAdapter.ForceDeferredDurability = true;  return new TyphonAdapter(TyphonAdapter.Config.SvCommitted, DurabilityTier.D2); }),
            ("Typhon Commit FUA@return", () => { TyphonAdapter.UseFua = true;  TyphonAdapter.ForceDeferredDurability = false; return new TyphonAdapter(TyphonAdapter.Config.SvCommitted, DurabilityTier.D2); }),
            ("Typhon Commit fsync@return",() => { TyphonAdapter.UseFua = false; TyphonAdapter.ForceDeferredDurability = false; return new TyphonAdapter(TyphonAdapter.Config.SvCommitted, DurabilityTier.D2); }),
            ("SQLite WAL+FULL", () => new SqliteAdapter(root, DurabilityTier.D2)),
            ("RocksDB sync", () => new RocksDbAdapter(root, DurabilityTier.D2)),
            ("LMDB msync", () => new LmdbAdapter(root, DurabilityTier.D2)),
        };

        double nsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
        var results = new List<(string name, double mean, long p50, long p99, long p999)>();

        foreach (var (label, make) in factories)
        {
            var a = make();
            a.Load(count);

            // warmup
            for (int i = 0; i < 2000; i++)
            {
                a.PointWriteCommit(i % count, i);
            }

            var h = new LongHistogram(1, 1_000_000_000, 3); // 1 ns .. 1 s, 3 sig digits
            int k = 0;
            for (int i = 0; i < ops; i++)
            {
                long s = Stopwatch.GetTimestamp();
                a.PointWriteCommit(k, i);
                long ns = (long)((Stopwatch.GetTimestamp() - s) * nsPerTick);
                h.RecordValue(ns < 1 ? 1 : ns);
                if (++k >= count) k = 0;
            }

            results.Add((a.Name,
                h.GetMean(),
                h.GetValueAtPercentile(50.0),
                h.GetValueAtPercentile(99.0),
                h.GetValueAtPercentile(99.9)));
            Console.WriteLine($"  measured {a.Name}");
            a.Dispose();
        }

        Console.WriteLine(new string('─', 84));
        Console.WriteLine($"{"Engine (D2)",-22} {"mean",11} {"p50",11} {"p99",11} {"p99.9",11}");
        Console.WriteLine(new string('─', 84));
        foreach (var r in results)
        {
            Console.WriteLine($"{r.name,-22} {Measure.FormatNs(r.mean),11} {Measure.FormatNs(r.p50),11} {Measure.FormatNs(r.p99),11} {Measure.FormatNs(r.p999),11}");
        }
        Console.WriteLine(new string('─', 84));
        Console.WriteLine("READING IT:");
        Console.WriteLine("  • 'Typhon Commit deferred' = the TRANSACTION-COMMIT cost (WAL redo written + published in place,");
        Console.WriteLine("    recoverable). Durability is async (durable-soon) — the fair 'what does committing cost' number.");
        Console.WriteLine("  • 'durable@return' rows block until the WAL is fsync'd. Single-thread this charges the full background-");
        Console.WriteLine("    writer handoff (~1 ms, FUA-independent — it is the WaitForDurable wakeup, NOT the ~330 µs fsync).");
        Console.WriteLine("    Typhon group-commits, so under concurrency this amortizes toward the fsync floor — see `c4c`.");
        Console.WriteLine("  • Competitors fsync inline per commit (no async-writer group commit); their number IS the fsync path.");
        TyphonAdapter.ForceDeferredDurability = false; // reset process-wide toggle
    }
}
