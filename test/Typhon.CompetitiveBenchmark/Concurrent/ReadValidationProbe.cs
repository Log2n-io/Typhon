using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// Audit probe for the concurrent READ matrix: proves the throughput the matrix reports is paid for by REAL work.
/// <para>
/// The matrix accumulates <see cref="MatrixRunner.Sink"/> only to defeat dead-code elimination — it never checks the value.
/// So a read path that silently returned 0, resolved the wrong entity, or short-circuited would post a great number and no
/// one would notice. This probe closes that hole: <see cref="TyphonConcurrentAdapter.Load"/> stores <c>Value = i</c> for
/// key <c>i</c>, so <c>ReadBatch(k, n)</c> has an ANALYTICALLY known checksum — the arithmetic series
/// <c>sum(k .. k+n-1) = n*(2k + n - 1)/2</c>. Every batch is compared against it.
/// </para>
/// <para>
/// Phase 1 sweeps the whole keyspace single-threaded (catches a wrong-entity resolve anywhere in the map). Phase 2 replays
/// the exact 16-thread CCD-pinned matrix cell, asserting every batch — so the reported M components/s and the correctness
/// check come from the SAME loop. A number here that matches the published matrix, with zero mismatches, means the
/// published number is real.
/// </para>
/// Run: dotnet run -c Release -- verify
/// </summary>
public static class ReadValidationProbe
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr mask);

    private static readonly int[] Ccd0Order = { 0, 2, 4, 6, 8, 10, 12, 14, 1, 3, 5, 7, 9, 11, 13, 15 };

    /// <summary>Expected checksum of ReadBatch(startKey, count): sum of the consecutive integers [startKey, startKey+count).</summary>
    private static long Expected(int startKey, int count) => (long)count * (2L * startKey + count - 1) / 2;

    /// <summary>
    /// Checksum-validate EVERY engine's read path, not just Typhon's. Each adapter's <c>Load</c> stores <c>value = i</c> for
    /// key <c>i</c>, so the same analytic series applies to all of them.
    /// <para>
    /// This exists because a competitive benchmark that is wrong in OUR favour is worse than no benchmark: an adapter that
    /// silently returned nothing, or read a stale snapshot, would post a great number for Typhon by comparison and nothing
    /// in the matrix would notice. It is also the regression guard for adapter tuning — a "faster" adapter that stops
    /// returning correct rows fails here instead of shipping.
    /// </para>
    /// </summary>
    private static void ValidateAllAdapters(int count)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "typhon-cb-verify");
        if (System.IO.Directory.Exists(root)) { try { System.IO.Directory.Delete(root, true); } catch { } }
        System.IO.Directory.CreateDirectory(root);

        var factories = new (string label, Func<IConcurrentAdapter> make)[]
        {
            ("Typhon SV", () => new TyphonConcurrentAdapter()),
            ("SQLite", () => new SqliteConcurrentAdapter(root)),
            ("RocksDB", () => new RocksDbConcurrentAdapter(root)),
            ("LMDB", () => new LmdbConcurrentAdapter(root)),
            ("FASTER", () => new FasterConcurrentAdapter(root)),
        };

        Console.WriteLine("  Cross-engine read correctness (every adapter, single thread, whole keyspace):");
        foreach (var (label, make) in factories)
        {
            try
            {
                var a = make();
                a.Load(count);
                var w = a.CreateWorker();
                long mismatches = 0, firstBadKey = -1, firstGot = 0, firstWant = 0;
                foreach (var batch in new[] { 1, 64, 1024 })
                {
                    for (int k = 0; k + batch <= count; k += batch)
                    {
                        long got = w.ReadBatch(k, batch);
                        long want = Expected(k, batch);
                        if (got != want)
                        {
                            if (mismatches == 0) { firstBadKey = k; firstGot = got; firstWant = want; }
                            mismatches++;
                        }
                    }
                }
                w.Dispose();
                a.Dispose();
                Console.WriteLine(mismatches == 0
                    ? $"    {label,-12} OK"
                    : $"    {label,-12} {mismatches:N0} MISMATCH(ES) *** first at key {firstBadKey}: got {firstGot}, want {firstWant}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    {label,-12} FAIL  {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.WriteLine();
    }

    public static void Run(int count = 1_000_000, int durationMs = 400)
    {
        Console.WriteLine($"READ-PATH VALIDATION — {count:N0} entities, checksum-verified against the analytic series");
        Console.WriteLine(new string('─', 96));

        ValidateAllAdapters(count);

        var a = new TyphonConcurrentAdapter();
        a.Load(count);

        // ── Phase 1: full keyspace sweep, single thread, every batch checked ──────────────────────────────────────────
        foreach (var batch in new[] { 1, 8, 1024 })
        {
            var w = a.CreateWorker();
            long mismatches = 0, checkedBatches = 0;
            long firstBadKey = -1, firstGot = 0, firstWant = 0;
            for (int k = 0; k + batch <= count; k += batch)
            {
                long got = w.ReadBatch(k, batch);
                long want = Expected(k, batch);
                checkedBatches++;
                if (got != want)
                {
                    if (mismatches == 0) { firstBadKey = k; firstGot = got; firstWant = want; }
                    mismatches++;
                }
            }
            w.Dispose();
            Console.WriteLine(mismatches == 0
                ? $"  phase1 batch={batch,-5} {checkedBatches,9:N0} batches  ALL CHECKSUMS OK"
                : $"  phase1 batch={batch,-5} {checkedBatches,9:N0} batches  {mismatches:N0} MISMATCH(ES) — first at key {firstBadKey}: got {firstGot}, want {firstWant}");
        }

        // ── Phase 2: the real matrix cell, 16 threads, CCD-pinned, every batch checked while timed ────────────────────
        Console.WriteLine();
        Console.WriteLine($"  {"threads",-9}{"batch",-8}{"M comps/s",12}{"batches",14}{"mismatches",13}");
        foreach (var threads in new[] { 1, 16 })
        {
            foreach (var batch in new[] { 1, 1024 })
            {
                var (mps, batches, bad) = TimedVerifiedCell(a, threads, batch, count, durationMs);
                Console.WriteLine($"  {threads,-9}{batch,-8}{mps,12:0.00}{batches,14:N0}{(bad == 0 ? "0" : bad.ToString("N0") + " ***"),13}");
            }
        }

        a.Dispose();
        Console.WriteLine(new string('─', 96));
        Console.WriteLine("Zero mismatches ⇒ every timed read resolved the correct entity and returned its stored value.");
    }

    /// <summary>
    /// Byte-for-byte the MatrixRunner READ cell (pinning, disjoint partitions, barrier, nominal-duration divisor) with a
    /// checksum assert added inside the timed loop. The assert makes this cell slightly SLOWER than the published one —
    /// so if this matches, the published number is if anything conservative.
    /// </summary>
    private static (double mps, long batches, long bad) TimedVerifiedCell(IConcurrentAdapter a, int threads, int batch, int count, int durationMs)
    {
        long totalComps = 0, totalBatches = 0, totalBad = 0;
        int part = count / threads;
        var workers = new Thread[threads];
        var ready = new CountdownEvent(threads);
        var go = new ManualResetEventSlim(false);
        var sw = new Stopwatch();

        for (int t = 0; t < threads; t++)
        {
            int tid = t;
            workers[t] = new Thread(() =>
            {
                SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << Ccd0Order[tid % Ccd0Order.Length]));
                var w = a.CreateWorker();
                int lo = tid * part;
                int hi = (tid == threads - 1) ? count : lo + part;
                int b = Math.Min(batch, hi - lo);
                int k = lo;
                long localComps = 0, localBatches = 0, localBad = 0;
                ready.Signal();
                go.Wait();
                while (sw.ElapsedMilliseconds < durationMs)
                {
                    if (w.ReadBatch(k, b) != Expected(k, b))
                    {
                        localBad++;
                    }

                    localBatches++;
                    localComps += b;
                    k += b;
                    if (k + b > hi) { k = lo; }
                }
                w.Dispose();
                Interlocked.Add(ref totalComps, localComps);
                Interlocked.Add(ref totalBatches, localBatches);
                Interlocked.Add(ref totalBad, localBad);
            }) { IsBackground = true };
            workers[t].Start();
        }

        ready.Wait();
        sw.Start();
        go.Set();
        foreach (var w in workers) { w.Join(); }
        sw.Stop();
        return (totalComps / (durationMs / 1000.0) / 1_000_000.0, totalBatches, totalBad);
    }
}
