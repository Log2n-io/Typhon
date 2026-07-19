using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.CompetitiveBenchmark.Adapters;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.CompetitiveBenchmark.Harness;

/// <summary>
/// B7-X — diagnostic experiment to find WHY the parallel cluster scan only scales ~2.4× on 8 cores. Measures, for several
/// dataset sizes: (1) cluster density (occupancy), (2) an INDEPENDENT-LOOP scaling sweep (each worker loops its disjoint slice
/// for a fixed window with NO per-round barrier — isolates true aggregate scan bandwidth from the straggler/barrier coupling of
/// the B7 measurement), and (3) effective useful bandwidth (GB/s of payload). Small datasets (per-worker slice fits L2) should
/// scale near-linearly IF the limit is shared L3/DRAM bandwidth; if even small sets cap at ~2.4×, the bottleneck is per-cluster
/// overhead or shared-state contention, not bandwidth.
/// </summary>
public static class B7ScalingExperiment
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr mask);
    private static readonly int[] Ccd0 = { 0, 2, 4, 6, 8, 10, 12, 14, 1, 3, 5, 7, 9, 11, 13, 15 };
    private static readonly int[] ThreadCounts = { 1, 2, 4, 8 };

    // The scan, as its OWN method — small, clean register allocation. Inlining this into a large timed-loop body causes the JIT
    // to spill the hot inner loop (the "giant method" de-opt). Keep it standalone (this is exactly what Measure.NsPerOp's lambda is).
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

    // SIMD variant: for FULL clusters (OccupancyBits == FullMask — the dense common case) sum the whole Value column with AVX2
    // (4 longs/instr), skipping the per-slot TZCNT bit-walk. Falls back to the scalar walk for partial clusters.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe long ScanRangeSimd(ref ArchetypeAccessor<SvValArch> acc, int start, int end)
    {
        long sum = 0;
        var vsum = Vector256<long>.Zero;
        foreach (var cluster in acc.GetClusterEnumerator(start, end))
        {
            var span = cluster.GetSpan<SvVal>(SvValArch.Data);
            ulong bits = cluster.OccupancyBits;
            if (bits == cluster.FullMask)
            {
                var lspan = MemoryMarshal.Cast<SvVal, long>(span);
                ref long b = ref MemoryMarshal.GetReference(lspan);
                int n = lspan.Length, i = 0;
                for (; i + 4 <= n; i += 4) vsum = Avx2.Add(vsum, Vector256.LoadUnsafe(ref Unsafe.Add(ref b, i)));
                for (; i < n; i++) sum += Unsafe.Add(ref b, i);
            }
            else
            {
                while (bits != 0) { int idx = BitOperations.TrailingZeroCount(bits); bits &= bits - 1; sum += span[idx].Value; }
            }
        }
        return sum + vsum.GetElement(0) + vsum.GetElement(1) + vsum.GetElement(2) + vsum.GetElement(3);
    }

    public static void Run()
    {
        Console.WriteLine("B7-X — parallel cluster-scan scaling diagnostic (independent-loop, CCD0-pinned, Zen 4)");
        Console.WriteLine(new string('═', 92));

        foreach (var count in new[] { 65_536, 262_144, 1_048_576, 4_000_000 })
        {
            RunOne(count);
            Console.WriteLine();
        }
    }

    private static void RunOne(int count)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
          .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o => { o.DatabaseName = $"b7x_{Environment.ProcessId}"; o.DatabaseCacheSize = (ulong)(65536 * PagedMMF.PageSize); o.PagesDebugPattern = false; })
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

        // ── structural facts: cluster count, slot capacity, occupancy ──
        int clusterCount, clusterSize;
        long liveRows;
        {
            using var dtx = dbe.CreateQuickTransaction();
            var dacc = dtx.For<SvValArch>();
            clusterCount = dacc.ClusterCount;
            clusterSize = 0;
            liveRows = 0;
            foreach (var cluster in dacc.GetClusterEnumerator())
            {
                if (clusterSize == 0) clusterSize = cluster.ClusterSize;
                liveRows += BitOperations.PopCount(cluster.OccupancyBits);
            }
            dacc.Dispose();
        }
        double occupancy = clusterCount == 0 ? 0 : (double)liveRows / (clusterCount * (double)clusterSize);
        long valueBytes = liveRows * 8;                                   // useful payload (the long Value column)
        long pageSpanBytes = (long)clusterCount * clusterSize * 8;        // bytes spanned by the Value column across all clusters (incl. empty slots)

        Console.WriteLine($"rows={count:N0}  liveRows={liveRows:N0}  clusters={clusterCount:N0}  clusterSize={clusterSize}  occupancy={occupancy * 100:0.0}%  "
                          + $"valueColumn={valueBytes / (1024.0 * 1024):0.0}MB  spanned(incl.empty)={pageSpanBytes / (1024.0 * 1024):0.0}MB");
        // ── single-core probe: isolate boost-clock / core-0-noise. One worker over the FULL range, pinned to specific cores + unpinned. ──
        double p0 = ProbeSingle(dbe, clusterCount, 0);     // core 0 (busiest — OS/timer/engine threads)
        double p2 = ProbeSingle(dbe, clusterCount, 2);     // a quieter physical core
        double p4 = ProbeSingle(dbe, clusterCount, 4);     // another quiet physical core
        double pu = ProbeSingle(dbe, clusterCount, -1);    // unpinned (OS picks; best boost)
        double b7 = ProbeB7Style(dbe, count, onNewThread: false, ranged: false, clusterCount);   // 0-arg enum, calling thread  (= B7)
        double b7T = ProbeB7Style(dbe, count, onNewThread: true, ranged: false, clusterCount);    // 0-arg enum, NEW thread
        double b7R = ProbeB7Style(dbe, count, onNewThread: false, ranged: true, clusterCount);     // ranged enum, calling thread
        Console.WriteLine($"single-core probe (M rows/s): core0={p0:0} core2={p2:0} core4={p4:0} unpinned={pu:0}");
        Console.WriteLine($"B7-style (Measure.NsPerOp): callThread/0arg={b7:0}  newThread/0arg={b7T:0}  callThread/ranged={b7R:0}");

        Console.WriteLine($"{"threads",8} {"M rows/s",12} {"scaling",9} {"useful GB/s",13} {"spanned GB/s",14}");

        double baseThroughput = 0;
        foreach (var threads in ThreadCounts)
        {
            double mrows = MeasureIndependent(dbe, clusterCount, threads, false, out _);
            if (threads == 1) baseThroughput = mrows;
            double scaling = baseThroughput == 0 ? 1 : mrows / baseThroughput;
            double passesPerSec = liveRows == 0 ? 0 : (mrows * 1e6) / liveRows;
            double usefulGBs = passesPerSec * valueBytes / 1e9;
            double spannedGBs = passesPerSec * pageSpanBytes / 1e9;
            Console.WriteLine($"{threads,8} {mrows,12:0.0} {scaling,8:0.0}× {usefulGBs,12:0.0} {spannedGBs,13:0.0}");
        }

        // SIMD comparison (dense-cluster fast path) at 1 and 8 threads.
        double simd1 = MeasureIndependent(dbe, clusterCount, 1, true, out _);
        double simd8 = MeasureIndependent(dbe, clusterCount, 8, true, out _);
        Console.WriteLine($"   SIMD  1t={simd1:0.0}  8t={simd8:0.0} M rows/s  (8t useful {(liveRows == 0 ? 0 : simd8 * 1e6 / liveRows * valueBytes / 1e9):0.0} GB/s)");

        dbe.Dispose();
        try { File.Delete($"b7x_{Environment.ProcessId}.bin"); } catch { }
    }

    // EXACT replica of B7Runner's single-thread measurement: Measure.NsPerOp, recreate accessor each call, 0-arg full enumerator,
    // on the calling thread (no new Thread, no pin). Returns M rows/s. If this matches the probe → no methodology gap; if it
    // matches B7's ~1300 → the gap is thread/enumerator-shape, not the scan itself.
    private static double ProbeB7Style(DatabaseEngine dbe, int count, bool onNewThread, bool ranged, int clusterCount)
    {
        double result = 0;
        void Body()
        {
            using var stx = dbe.CreateQuickTransaction();
            double ns = Measure.NsPerOp(() =>
            {
                var sacc = stx.For<SvValArch>();
                long sum = 0;
                var en = ranged ? sacc.GetClusterEnumerator(0, clusterCount) : sacc.GetClusterEnumerator();
                foreach (var cluster in en)
                {
                    var span = cluster.GetSpan<SvVal>(SvValArch.Data);
                    ulong bits = cluster.OccupancyBits;
                    while (bits != 0) { int idx = BitOperations.TrailingZeroCount(bits); bits &= bits - 1; sum += span[idx].Value; }
                }
                sacc.Dispose();
                GC.KeepAlive(sum);
            }, 1, warmupBatches: 5, minMs: 500);
            result = count / (ns / 1e9) / 1e6;
        }

        if (onNewThread)
        {
            var th = new Thread(Body) { IsBackground = true };
            th.Start();
            th.Join();
        }
        else
        {
            Body();
        }
        return result;
    }

    // Single-core probe: ONE worker, SELF-TIMED with a local Stopwatch (like Measure.NsPerOp — no captured stop flag),
    // reusing acc + ranged enumerator. pinCore<0 = unpinned.
    private static double ProbeSingle(DatabaseEngine dbe, int clusterCount, int pinCore)
    {
        double result = 0;
        var th = new Thread(() =>
        {
            if (pinCore >= 0) SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << pinCore));
            var tx = dbe.CreateQuickTransaction();
            var acc = tx.For<SvValArch>();
            long liveSlice = 0, warm = 0;
            foreach (var cluster in acc.GetClusterEnumerator(0, clusterCount))
            {
                var span = cluster.GetSpan<SvVal>(SvValArch.Data);
                ulong bits = cluster.OccupancyBits;
                while (bits != 0) { int idx = BitOperations.TrailingZeroCount(bits); bits &= bits - 1; warm += span[idx].Value; liveSlice++; }
            }
            GC.KeepAlive(warm);
            // self-timed loop — scan extracted into its own method (ScanRange) to get clean codegen, reusing acc.
            long sum = 0, passes = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 300)
            {
                sum += ScanRange(ref acc, 0, clusterCount);
                passes++;
            }
            sw.Stop();
            GC.KeepAlive(sum);
            result = passes * liveSlice / sw.Elapsed.TotalSeconds / 1e6;
            acc.Dispose();
            tx.Dispose();
        }) { IsBackground = true };
        th.Start();
        th.Join();
        return result;
    }

    // Independent-loop measurement: `threads` CCD-pinned workers each loop their DISJOINT cluster slice repeatedly for a fixed
    // window, with NO per-round barrier. Returns aggregate M rows/s. Each worker counts how many full passes it completed; total
    // rows summed = Σ(worker passes × worker slice live-rows). We approximate worker slice live-rows by total liveRows × (sliceClusters/clusterCount)
    // — but to be exact, each worker counts the rows it actually summed and we total those.
    private static double MeasureIndependent(DatabaseEngine dbe, int clusterCount, int threads, bool simd, out double seconds)
    {
        var passes = new long[threads];       // full slice-passes each worker completed in the window
        var sliceLive = new long[threads];     // live rows in each worker's slice (counted once in warmup)
        var ready = new CountdownEvent(threads);
        var go = new ManualResetEventSlim(false);
        bool stop = false;
        var workers = new Thread[threads];

        for (int w = 0; w < threads; w++)
        {
            int wid = w;
            workers[w] = new Thread(() =>
            {
                SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << Ccd0[wid]));
                var tx = dbe.CreateQuickTransaction();
                var acc = tx.For<SvValArch>();
                int per = clusterCount / threads;
                int rem = clusterCount % threads;
                int start = wid * per + Math.Min(wid, rem);
                int end = start + per + (wid < rem ? 1 : 0);

                // Warm up once (page-in this worker's slice) + count its live rows.
                long warm = 0, live = 0;
                foreach (var cluster in acc.GetClusterEnumerator(start, end))
                {
                    var span = cluster.GetSpan<SvVal>(SvValArch.Data);
                    ulong bits = cluster.OccupancyBits;
                    while (bits != 0) { int idx = BitOperations.TrailingZeroCount(bits); bits &= bits - 1; warm += span[idx].Value; live++; }
                }
                sliceLive[wid] = live;
                GC.KeepAlive(warm);

                ready.Signal();
                go.Wait();

                // Timed loop — scan extracted into ScanRange (standalone method, clean codegen; avoids the giant-method JIT de-opt).
                long localSum = 0, localPasses = 0;
                while (!Volatile.Read(ref stop))
                {
                    localSum += simd ? ScanRangeSimd(ref acc, start, end) : ScanRange(ref acc, start, end);
                    localPasses++;
                }
                passes[wid] = localPasses;
                GC.KeepAlive(localSum);
                acc.Dispose();
                tx.Dispose();
            }) { IsBackground = true };
            workers[w].Start();
        }

        ready.Wait();
        var sw = Stopwatch.StartNew();
        go.Set();
        Thread.Sleep(600);
        Volatile.Write(ref stop, true);
        foreach (var th in workers) th.Join();
        sw.Stop();

        seconds = sw.Elapsed.TotalSeconds;
        long total = 0;
        for (int i = 0; i < threads; i++) total += passes[i] * sliceLive[i];
        return total / seconds / 1e6;
    }
}
