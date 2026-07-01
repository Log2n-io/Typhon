using System;
using System.Diagnostics;
using System.IO;

namespace Typhon.CompetitiveBenchmark.Harness;

/// <summary>
/// Isolates the RAW hardware durable-write latency on this NVMe — write-through 4 KB writes, no Typhon involved.
/// Decides whether Typhon's ~1 ms Immediate commit is the hardware FUA floor (design assumes 10–80 µs on faster storage)
/// or path overhead above an ~80 µs fsync.
/// </summary>
public static class RawFuaProbe
{
    public static void Run(int ops = 4000)
    {
        var dir = Path.Combine(Path.GetTempPath(), "typhon-fua-probe");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "probe.bin");
        try { File.Delete(path); } catch { }

        const int bs = 4096;
        var buf = new byte[bs];
        new Random(1).NextBytes(buf);

        double nsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

        Console.WriteLine($"Raw durable-write probe — {ops:N0} × {bs}-byte write-through writes to NVMe (no Typhon)");
        Console.WriteLine(new string('─', 70));

        // 1) WriteThrough (forces device write per Flush(true)) — closest to Typhon's FUA path
        MeasureMode("WriteThrough + Flush(true)", FileOptions.WriteThrough, flushToDisk: true);
        // 2) Buffered + Flush(true) (FlushFileBuffers) — closest to SQLite/RocksDB fsync-to-cache
        MeasureMode("Buffered + Flush(true) (fsync)", FileOptions.None, flushToDisk: true);
        // 3) Buffered, no flush — OS cache only (upper bound)
        MeasureMode("Buffered, no flush (OS cache)", FileOptions.None, flushToDisk: false);

        Console.WriteLine(new string('─', 70));
        Console.WriteLine("If (1) ≈ 1 ms, the ~1 ms Immediate commit is the hardware FUA floor on this NVMe (design's 10–80 µs");
        Console.WriteLine("assumed faster/PLP storage). If (1) ≈ 80 µs, Typhon's commit path adds overhead above the fsync.");

        void MeasureMode(string label, FileOptions opts, bool flushToDisk)
        {
            try { File.Delete(path); } catch { }
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bs, opts);
            // warmup
            for (int i = 0; i < 200; i++) { fs.Write(buf, 0, bs); if (flushToDisk) fs.Flush(true); }

            var lat = new long[ops];
            long off = 0;
            for (int i = 0; i < ops; i++)
            {
                long s = Stopwatch.GetTimestamp();
                fs.Write(buf, 0, bs);
                if (flushToDisk) fs.Flush(true);
                lat[i] = (long)((Stopwatch.GetTimestamp() - s) * nsPerTick);
                off += bs;
                if (off > 256L * 1024 * 1024) { fs.Seek(0, SeekOrigin.Begin); off = 0; }
            }
            Array.Sort(lat);
            double mean = 0; for (int i = 0; i < ops; i++) mean += lat[i]; mean /= ops;
            Console.WriteLine($"{label,-32} mean {Measure.FormatNs(mean),10}  p50 {Measure.FormatNs(lat[ops / 2]),10}  p99 {Measure.FormatNs(lat[(int)(ops * 0.99)]),10}");
        }
    }
}
