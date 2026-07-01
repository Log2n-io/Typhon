using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Typhon.CompetitiveBenchmark.Adapters;

namespace Typhon.CompetitiveBenchmark.Harness;

/// <summary>
/// Credibility-core cross-engine runner: A1 (point read) + point-write throughput, every engine in the SAME tier.
/// Single-thread v1 (the concurrency sweep is a separate axis). A5 ingest (with on-disk size + setup/ingest split) and
/// the multi-thread open-loop/HdrHistogram throughput pass are follow-ups.
/// </summary>
public static class AScenarioRunner
{
    public static long Sink;

    public static void Run(int count = 200_000, DurabilityTier tier = DurabilityTier.D0)
    {
        var root = Path.Combine(Path.GetTempPath(), "typhon-a");
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        Directory.CreateDirectory(root);

        Console.WriteLine($"A1 point-read + point-write throughput — {count:N0} keys, single-long, tier={tier}, single-thread, Zen 4");
        Console.WriteLine(new string('─', 86));

        var factories = new (string label, Func<IEngineAdapter> make)[]
        {
            ("FASTER", () => new FasterAdapter(root, tier)),
            ("LMDB", () => new LmdbAdapter(root, tier)),
            ("SQLite", () => new SqliteAdapter(root, tier)),
            ("RocksDB", () => new RocksDbAdapter(root, tier)),
            ("LiteDB", () => new LiteDbAdapter(root, tier)),
            ("Typhon SV-lean", () => new TyphonAdapter(TyphonAdapter.Config.SvLean, tier)),
            ("Typhon SV-Committed", () => new TyphonAdapter(TyphonAdapter.Config.SvCommitted, tier)),
            ("Typhon Versioned", () => new TyphonAdapter(TyphonAdapter.Config.Versioned, tier)),
        };

        const int batch = 256;
        var results = new List<(string name, double readNs, double writeNs, double loadMs)>();

        foreach (var (label, make) in factories)
        {
            var a = make();

            var swLoad = Stopwatch.StartNew();
            a.Load(count);
            swLoad.Stop();

            double readNs;
            using (a.OpenReadScope())
            {
                int rk = 0;
                long sink = 0;
                readNs = Measure.NsPerOp(() =>
                {
                    for (int b = 0; b < batch; b++)
                    {
                        sink += a.PointRead(rk);
                        if (++rk >= count) rk = 0;
                    }
                }, batch);
                Sink += sink;
            }

            int wk = 0;
            long wv = 1;
            double writeNs = Measure.NsPerOp(() =>
            {
                for (int b = 0; b < batch; b++)
                {
                    a.PointWriteCommit(wk, wv++);
                    if (++wk >= count) wk = 0;
                }
            }, batch);

            results.Add((a.Name, readNs, writeNs, swLoad.Elapsed.TotalMilliseconds));
            Console.WriteLine($"  measured {a.Name}");
            a.Dispose();
        }

        Console.WriteLine(new string('─', 86));
        Console.WriteLine($"{"Engine",-28} {"read ns/op",11} {"read Mops/s",12} {"write ns/op",12} {"write Mops/s",13}");
        Console.WriteLine(new string('─', 86));
        foreach (var r in results)
        {
            Console.WriteLine($"{r.name,-28} {Measure.FormatNs(r.readNs),11} {1000.0 / r.readNs,12:0.00} {Measure.FormatNs(r.writeNs),12} {1000.0 / r.writeNs,13:0.000}");
        }
        Console.WriteLine(new string('─', 86));
        Console.WriteLine($"(GC.KeepAlive: {(Sink == long.MinValue ? "?" : "ok")}; tier={tier}; reads = read primitive amortized under one scope; writes = per-op commit)");
    }
}
