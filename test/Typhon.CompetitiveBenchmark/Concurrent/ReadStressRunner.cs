using System;
using System.Diagnostics;
using System.Threading;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// Focused read-scaling probe for profiling: Typhon Open(id) point reads, fixed batch, N threads, long duration — so a
/// sampling profiler captures a clean steady-state read phase. `rs &lt;threads&gt; &lt;ms&gt;`.
/// </summary>
public static class ReadStressRunner
{
    public static long Sink;

    public static void Run(int threads, int durationMs)
    {
        const int count = 1_000_000;
        const int batch = 256;

        var a = new TyphonConcurrentAdapter();
        a.Load(count);

        Console.WriteLine($"READ-STRESS: Typhon Open(id), {threads} threads, batch {batch}, {durationMs} ms, {count:N0} entities");

        long totalComps = 0;
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
                var w = a.CreateWorker();
                int lo = tid * part;
                int hi = (tid == threads - 1) ? count : lo + part;
                int k = lo;
                long local = 0, sink = 0;
                ready.Signal();
                go.Wait();
                while (sw.ElapsedMilliseconds < durationMs)
                {
                    sink += w.ReadBatch(k, batch);
                    local += batch;
                    k += batch;
                    if (k + batch > hi) k = lo;
                }
                w.Dispose();
                Interlocked.Add(ref totalComps, local);
                Interlocked.Add(ref Sink, sink);
            }) { IsBackground = true };
            workers[t].Start();
        }

        ready.Wait();
        sw.Start();
        go.Set();
        foreach (var w in workers) w.Join();
        sw.Stop();
        a.Dispose();

        double mps = totalComps / (durationMs / 1000.0) / 1_000_000.0;
        double nsPerRead = threads * 1e9 / (totalComps / (durationMs / 1000.0));
        Console.WriteLine($"  {mps:0.00} M reads/s total · {nsPerRead:0} ns/read per thread · (sink {(Sink == long.MinValue ? 0 : 1)})");
    }
}
