using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Typhon.CompetitiveBenchmark.Adapters;

namespace Typhon.CompetitiveBenchmark.Harness;

/// <summary>
/// C0 — cost-ladder ablation vs the KV floor. Walks one point-read and one point-write up a ladder of increasing
/// guarantees, all at D0 (in-mem WAL, no fsync), so the ladder isolates the *CPU* cost of each guarantee. The floor
/// (FASTER, LMDB-write) is the reference line, NOT an opponent. See the plan §7-C0.
/// </summary>
public static class C0Runner
{
    public static long Sink; // prevents dead-code elimination of reads

    public static void Run(int count = 200_000)
    {
        var root = Path.Combine(Path.GetTempPath(), "typhon-c0");
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        Directory.CreateDirectory(root);

        Console.WriteLine($"C0 cost-ladder — {count:N0} keys, single-long value, D0 (no fsync), Zen 4");
        Console.WriteLine("Read = primitive amortized under one read scope; Write = one full per-op commit unit.");
        Console.WriteLine(new string('─', 78));

        // Ladder order: floor first, then Typhon rungs of increasing guarantee.
        var adapters = new IEngineAdapter[]
        {
            new FasterAdapter(root),
            new LmdbAdapter(root),
            new TyphonAdapter(TyphonAdapter.Config.SvLean),
            new TyphonAdapter(TyphonAdapter.Config.SvCommitted),
            new TyphonAdapter(TyphonAdapter.Config.Versioned),
        };

        const int batch = 256; // ops per timed batch (amortizes the Stopwatch read)
        var results = new List<(string name, bool floor, double readNs, double writeNs)>();

        foreach (var a in adapters)
        {
            a.Load(count);

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

            results.Add((a.Name, a.IsFloor, readNs, writeNs));
            a.Dispose();
            Console.WriteLine($"  measured {a.Name}");
        }

        double floorRead = results.Where(r => r.floor).Min(r => r.readNs);
        double floorWrite = results.Where(r => r.floor).Min(r => r.writeNs);

        Console.WriteLine(new string('─', 78));
        Console.WriteLine($"{"Rung",-32} {"read ns/op",12} {"×floor",8} {"write ns/op",13} {"×floor",8}");
        Console.WriteLine(new string('─', 78));
        foreach (var r in results)
        {
            string rx = r.floor ? "floor" : $"{r.readNs / floorRead:0.0}×";
            string wx = r.floor ? "floor" : $"{r.writeNs / floorWrite:0.0}×";
            Console.WriteLine($"{r.name,-32} {Measure.FormatNs(r.readNs),12} {rx,8} {Measure.FormatNs(r.writeNs),13} {wx,8}");
        }
        Console.WriteLine(new string('─', 78));
        Console.WriteLine("Floor (FASTER/LMDB) is the reference line, not an opponent: the story is how small the gap is");
        Console.WriteLine("while Typhon also offers MVCC / atomic-durable commit the floor cannot. (GC.KeepAlive: " + (Sink == long.MinValue ? "?" : "ok") + ")");
    }
}
