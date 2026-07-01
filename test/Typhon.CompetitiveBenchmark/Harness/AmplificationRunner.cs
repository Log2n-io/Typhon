using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.CompetitiveBenchmark.Adapters;

namespace Typhon.CompetitiveBenchmark.Harness;

/// <summary>
/// A5 — ingest amplification. Bulk-loads 1M rows (8-byte key + 8-byte value = 16 MB logical) into each engine at D2 (on-disk,
/// durable) and reports: final on-disk size (space-amp) and OS bytes-written during the load (write-amp). Space-amp is the
/// clean deterministic metric; write-amp uses the process-wide <c>GetProcessIoCounters.WriteTransferCount</c> delta (the only
/// number directly comparable across engines), with a settle delay so background flush/compaction is attributed to the load.
/// </summary>
public static class AmplificationRunner
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
    }

    [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr handle, out IO_COUNTERS counters);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    private static ulong ProcWriteBytes()
    {
        GetProcessIoCounters(GetCurrentProcess(), out var c);
        return c.WriteBytes;
    }

    public static void Run(int count = 1_000_000)
    {
        var root = Path.Combine(Path.GetTempPath(), "typhon-amp");
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        Directory.CreateDirectory(root);

        long logical = count * 16L; // 8-byte key + 8-byte value per row
        Console.WriteLine($"A5 — ingest amplification — {count:N0} rows × 16 B = {logical / (1024.0 * 1024):0.0} MB logical, D2 (on-disk durable), Zen 4 + NVMe");
        Console.WriteLine(new string('─', 92));

        var factories = new (string label, Func<IEngineAdapter> make)[]
        {
            ("Typhon SV-Committed", () => { TyphonAdapter.UseFua = false; return new TyphonAdapter(TyphonAdapter.Config.SvCommitted, DurabilityTier.D2); }),
            ("SQLite WAL", () => new SqliteAdapter(root, DurabilityTier.D2)),
            ("RocksDB", () => new RocksDbAdapter(root, DurabilityTier.D2)),
            ("LMDB", () => new LmdbAdapter(root, DurabilityTier.D2)),
            ("FASTER", () => new FasterAdapter(root, DurabilityTier.D2)),
        };

        var results = new System.Collections.Generic.List<(string name, long onDisk, ulong written)>();

        foreach (var (label, make) in factories)
        {
            try
            {
                var a = make();
                ulong before = ProcWriteBytes();
                a.Load(count);
                Thread.Sleep(1500); // let async WAL flush / LSM compaction settle so their writes count toward this load
                ulong after = ProcWriteBytes();
                long onDisk = a.OnDiskBytes();
                results.Add((a.Name, onDisk, after - before));
                Console.WriteLine($"  measured {a.Name}");
                a.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {label} SKIPPED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine(new string('─', 92));
        Console.WriteLine($"{"Engine (D2)",-22} {"on-disk MB",12} {"space-amp",11} {"written MB",12} {"write-amp",11}");
        Console.WriteLine(new string('─', 92));
        foreach (var r in results)
        {
            double onDiskMb = r.onDisk / (1024.0 * 1024);
            double writtenMb = r.written / (1024.0 * 1024);
            Console.WriteLine($"{r.name,-22} {onDiskMb,12:0.0} {(double)r.onDisk / logical,11:0.00}x {writtenMb,12:0.0} {(double)r.written / logical,11:0.00}x");
        }
        Console.WriteLine(new string('─', 92));
        Console.WriteLine("space-amp = on-disk / logical; write-amp = bytes physically written / logical (OS WriteTransferCount,");
        Console.WriteLine("process-wide, settle-delayed — a cross-engine ground truth, not a per-engine internal counter).");
        Console.WriteLine("LSM (RocksDB) pays compaction write-amp; CoW B-trees (LMDB) and Typhon's WAL+pages differ — the RUM tradeoff.");
    }
}
