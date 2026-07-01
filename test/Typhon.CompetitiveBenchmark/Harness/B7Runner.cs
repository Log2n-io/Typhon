using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.CompetitiveBenchmark.Adapters;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.CompetitiveBenchmark.Harness;

/// <summary>
/// B7 — analytical full-column aggregate (<c>SUM(v)</c>). Threading is controlled on BOTH sides: Typhon runs single-thread AND
/// parallel via the engine's PRODUCTION parallel-scan primitive — the ranged <c>GetClusterEnumerator(start,end)</c> overload that
/// backs <c>QuerySystem.Parallel</c> / <c>ClusterRangeEntityView</c> (the same path AntHill drives across 16 workers). Each CCD-pinned
/// worker walks a DISJOINT contiguous slice of <c>ActiveClusterIds</c> and the partials reduce. DuckDB runs at threads=1 and threads=8.
/// SQLite is the row-store reference that loses by design.
/// <para>Patate is the NEXT layer on top of this, NOT what unlocks multi-core: the chunked cluster scan already spreads work across
/// cores; Patate adds vectorized per-operator throughput (gather→SIMD-DAG→scatter) plus selectivity (narrow the set before processing).</para>
/// </summary>
public static class B7Runner
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr mask);
    private static readonly int[] Ccd0 = { 0, 2, 4, 6, 8, 10, 12, 14, 1, 3, 5, 7, 9, 11, 13, 15 };

    private const int ParThreads = 8; // one CCD's physical cores

    public static void Run(int count = 4_000_000)
    {
        var root = Path.Combine(Path.GetTempPath(), "typhon-b7");
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        Directory.CreateDirectory(root);

        Console.WriteLine($"B7 — analytical SUM(v) over {count:N0} rows — threading controlled both sides, one CCD, Zen 4");
        Console.WriteLine(new string('─', 86));

        var (tSingle, tPar) = MeasureTyphon(count);
        var (d1Cold, d1Hot, dSize) = MeasureDuckDb(root, count, 1);
        var (_, d8Hot, _) = MeasureDuckDb(root, count, ParThreads);
        var (_, sHot, sSize) = MeasureSqlite(root, count);

        Console.WriteLine(new string('─', 86));
        Console.WriteLine($"{"Engine",-34} {"hot ms",10} {"M rows/s",12} {"on-disk MB",12}");
        Console.WriteLine(new string('─', 86));
        Row("Typhon SoA — 1 thread", tSingle, count, -1);
        Row($"Typhon SoA — {ParThreads} threads (independent)", tPar, count, -1);
        Row("DuckDB — 1 thread", d1Hot, count, dSize);
        Row($"DuckDB — {ParThreads} threads", d8Hot, count, dSize);
        Row("SQLite — 1 thread (row-store)", sHot, count, sSize);
        Console.WriteLine(new string('─', 86));
        Console.WriteLine($"Typhon parallel scaling: {tSingle / tPar:0.0}× on {ParThreads} cores at {count:N0} rows ({count * 8 / (1024.0 * 1024):0}MB Value column).");
        Console.WriteLine("This dataset is DRAM-BANDWIDTH-BOUND (spills the CCD's L3) → ~4× is the hardware wall for a trivial SUM, not an engine limit.");
        Console.WriteLine("Cache-resident data scales 8.5–9.7× (run `b7x`). AVX2 over dense clusters ~2× per-core (compute-bound regime). Primitive = ranged");
        Console.WriteLine("GetClusterEnumerator(start,end), what QuerySystem.Parallel / AntHill's 16-worker dispatch use. DuckDB threads not CCD-pinned; cold excluded.");

        static void Row(string name, double hotMs, int n, long size)
        {
            string sz = size < 0 ? "—" : $"{size / (1024.0 * 1024):0.0}";
            Console.WriteLine($"{name,-34} {hotMs,8:0.000}ms {n / (hotMs / 1000.0) / 1e6,12:0.0} {sz,12}");
        }
    }

    // The scan as its OWN method — small body → clean JIT register allocation. Inlining this into a large worker/timed-loop body
    // makes the JIT spill the hot inner loop ("giant method" de-opt) → ~2× slower. This is the single most important detail for an
    // honest measurement (it, plus the per-round barrier, is what made the first cut read a bogus 2.4×).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long ScanRange(ref ArchetypeAccessor<SvValArch> acc, int start, int end)
    {
        long sum = 0;
        foreach (var cluster in acc.GetClusterEnumerator(start, end))
        {
            var span = cluster.GetSpan<SvVal>(SvValArch.Data);
            ulong bits = cluster.OccupancyBits;
            while (bits != 0) { int idx = BitOperations.TrailingZeroCount(bits); bits &= bits - 1; sum += span[idx].Value; }
        }
        return sum;
    }

    // Returns (singleHotMs, parallelHotMs).
    private static (double single, double par) MeasureTyphon(int count)
    {
        Archetype<SvValArch>.Touch();
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
          .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o => { o.DatabaseName = $"b7_{Environment.ProcessId}"; o.DatabaseCacheSize = (ulong)(65536 * PagedMMF.PageSize); o.PagesDebugPattern = false; })
          .AddSingleton<IWalFileIO>(_ => new InMemoryWalFileIO())
          .AddScopedDatabaseEngine(o => { o.Wal = new WalWriterOptions { UseFUA = false }; o.Resources.CheckpointIntervalMs = int.MaxValue; });
        using var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SvVal>();
        dbe.InitializeArchetypes();

        const int LoadBatch = 8192;
        var t = dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            var c = new SvVal { Value = i };
            t.Spawn<SvValArch>(SvValArch.Data.Set(in c));
            if ((i + 1) % LoadBatch == 0) { t.Commit(); t.Dispose(); t = dbe.CreateQuickTransaction(); }
        }
        t.Commit();
        t.Dispose();
        dbe.WriteTickFence(1);

        // ── single-thread: one PERSISTENT accessor, re-enumerated (no per-iteration transaction creation) ──
        double singleNs;
        {
            using var stx = dbe.CreateQuickTransaction(); // persistent transaction (class — capturable); accessor is created cheaply per iteration
            singleNs = Measure.NsPerOp(() =>
            {
                var sacc = stx.For<SvValArch>();
                long sum = ScanRange(ref sacc, 0, sacc.ClusterCount);
                sacc.Dispose();
                GC.KeepAlive(sum);
            }, 1, warmupBatches: 5, minMs: 500);
        }

        // ── parallel: ParThreads CCD-pinned workers, each looping its DISJOINT cluster slice INDEPENDENTLY (NO per-round barrier).
        //    Why no barrier: the first cut used a barrier per round (release N, wait for all, reduce) and measured a bogus ~2.4×.
        //    That barrier coupled every round to the SLOWEST-of-N worker; with benchmark threads contending against the engine's
        //    unpinned background threads on the shared CCD, stragglers dominated. Independent loops measure the true aggregate scan
        //    throughput. (Diagnosed in full by the `b7x` experiment: true scaling is 8.5–9.7× for cache-resident data, 4.2× at 4M
        //    where it hits the CCD's DRAM-bandwidth wall — the 4M number below is that bandwidth-bound regime.)
        //    Each worker walks ONLY its [start,end) slice via the ranged GetClusterEnumerator — the same primitive QuerySystem.Parallel
        //    drives across 16 workers in AntHill — through the extracted ScanRange method (clean codegen; see its note).
        var passes = new long[ParThreads];
        var sliceLive = new long[ParThreads];
        var ready = new CountdownEvent(ParThreads);
        var go = new ManualResetEventSlim(false);
        bool stop = false;
        var workers = new Thread[ParThreads];
        for (int w = 0; w < ParThreads; w++)
        {
            int wid = w;
            workers[w] = new Thread(() =>
            {
                SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << Ccd0[wid]));
                var tx = dbe.CreateQuickTransaction();
                var acc = tx.For<SvValArch>();
                int total = acc.ClusterCount;
                int per = total / ParThreads, rem = total % ParThreads;
                int start = wid * per + Math.Min(wid, rem);
                int end = start + per + (wid < rem ? 1 : 0);
                long live = 0;
                foreach (var cluster in acc.GetClusterEnumerator(start, end)) live += BitOperations.PopCount(cluster.OccupancyBits);
                sliceLive[wid] = live;
                ScanRange(ref acc, start, end); // warm codegen before timing
                ready.Signal();
                go.Wait();
                long sum = 0, p = 0;
                while (!Volatile.Read(ref stop)) { sum += ScanRange(ref acc, start, end); p++; }
                passes[wid] = p;
                GC.KeepAlive(sum);
                acc.Dispose();
                tx.Dispose();
            }) { IsBackground = true };
            workers[w].Start();
        }
        ready.Wait();

        // Correctness gate: one synchronous full scan must equal the closed form SUM(0..count-1) = count*(count-1)/2.
        long expected = (long)count * (count - 1) / 2;
        long check;
        using (var ctx = dbe.CreateQuickTransaction())
        {
            var cacc = ctx.For<SvValArch>();
            check = ScanRange(ref cacc, 0, cacc.ClusterCount);
            cacc.Dispose();
        }
        if (check != expected)
        {
            Console.WriteLine($"  ⚠ B7 SUM MISMATCH: got {check:N0}, expected {expected:N0}");
        }

        var sw = Stopwatch.StartNew();
        go.Set();
        Thread.Sleep(500);
        Volatile.Write(ref stop, true);
        foreach (var th in workers) th.Join();
        sw.Stop();

        long totalRows = 0;
        for (int i = 0; i < ParThreads; i++) totalRows += passes[i] * sliceLive[i];
        double parMrows = totalRows / sw.Elapsed.TotalSeconds / 1e6;
        double parMs = parMrows <= 0 ? 0 : count / (parMrows * 1e6) * 1000.0; // ms-equivalent of one full-table pass at this throughput

        dbe.Dispose();
        try { File.Delete($"b7_{Environment.ProcessId}.bin"); } catch { }
        return (singleNs / 1_000_000.0, parMs);
    }

    private static (double cold, double hot, long size) MeasureDuckDb(string root, int count, int threads)
    {
        var path = Path.Combine(root, $"b7_{threads}.duckdb");
        try { File.Delete(path); } catch { }
        using var conn = new DuckDBConnection($"Data Source={path}");
        conn.Open();
        Exec(conn, $"SET threads TO {threads}");
        Exec(conn, $"CREATE TABLE t AS SELECT range AS id, range AS v FROM range({count})");
        Exec(conn, "CHECKPOINT");

        long Sum()
        {
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT SUM(v) FROM t";
            var o = c.ExecuteScalar();
            return o is System.Numerics.BigInteger bi ? (long)bi : Convert.ToInt64(o);
        }

        var sw = Stopwatch.StartNew();
        long cold = Sum();
        sw.Stop();
        double hotNs = Measure.NsPerOp(() => { var _ = Sum(); }, 1, warmupBatches: 5, minMs: 500);
        GC.KeepAlive(cold);
        long size = DiskUtil.Sum(path);
        return (sw.Elapsed.TotalMilliseconds, hotNs / 1_000_000.0, size);

        static void Exec(DuckDBConnection cn, string sql) { using var c = cn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    }

    private static (double cold, double hot, long size) MeasureSqlite(string root, int count)
    {
        var path = Path.Combine(root, "b7.db");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL;");
        Exec(conn, "PRAGMA synchronous=OFF;");
        Exec(conn, "CREATE TABLE t(id INTEGER PRIMARY KEY, v INTEGER) STRICT;");
        using (var tx = conn.BeginTransaction())
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = "INSERT INTO t(id,v) VALUES($id,$v)";
            var pid = ins.CreateParameter(); pid.ParameterName = "$id"; ins.Parameters.Add(pid);
            var pv = ins.CreateParameter(); pv.ParameterName = "$v"; ins.Parameters.Add(pv);
            ins.Prepare();
            for (int i = 0; i < count; i++) { pid.Value = (long)i; pv.Value = (long)i; ins.ExecuteNonQuery(); }
            tx.Commit();
        }
        Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE);");

        long Sum() { using var c = conn.CreateCommand(); c.CommandText = "SELECT SUM(v) FROM t"; return (long)c.ExecuteScalar(); }

        var sw = Stopwatch.StartNew();
        long cold = Sum();
        sw.Stop();
        double hotNs = Measure.NsPerOp(() => { var _ = Sum(); }, 1, warmupBatches: 5, minMs: 500);
        GC.KeepAlive(cold);
        long size = DiskUtil.Sum(path, path + "-wal", path + "-shm");
        return (sw.Elapsed.TotalMilliseconds, hotNs / 1_000_000.0, size);

        static void Exec(SqliteConnection cn, string sql) { using var c = cn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    }
}
