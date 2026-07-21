using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Benchmark;

/// <summary>
/// Measures the overhead of Barrier synchronization at different work phase durations.
/// This helps determine the minimum work duration where Barrier is efficient.
/// </summary>
[SimpleJob(RuntimeMoniker.Net90)]
[MemoryDiagnoser]
[BenchmarkCategory("Concurrency")]
public class BarrierOverheadBenchmark
{
    private Barrier _barrier = null!;
    private int _threadCount;

    [Params(2, 4, 8)]
    public int ThreadCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _threadCount = ThreadCount;
        _barrier = new Barrier(_threadCount);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _barrier.Dispose();
    }

    /// <summary>
    /// Baseline: Just the barrier overhead with no work
    /// </summary>
    [Benchmark(Baseline = true)]
    public void BarrierOnly_NoWork()
    {
        var tasks = new Task[_threadCount];
        for (int i = 0; i < _threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    _barrier.SignalAndWait();
                }
            });
        }
        Task.WaitAll(tasks);
    }

    /// <summary>
    /// ~1 microsecond of work per phase
    /// </summary>
    [Benchmark]
    public void BarrierWith_1us_Work()
    {
        var tasks = new Task[_threadCount];
        for (int i = 0; i < _threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    SpinMicroseconds(1);
                    _barrier.SignalAndWait();
                }
            });
        }
        Task.WaitAll(tasks);
    }

    /// <summary>
    /// ~10 microseconds of work per phase
    /// </summary>
    [Benchmark]
    public void BarrierWith_10us_Work()
    {
        var tasks = new Task[_threadCount];
        for (int i = 0; i < _threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    SpinMicroseconds(10);
                    _barrier.SignalAndWait();
                }
            });
        }
        Task.WaitAll(tasks);
    }

    /// <summary>
    /// ~100 microseconds of work per phase
    /// </summary>
    [Benchmark]
    public void BarrierWith_100us_Work()
    {
        var tasks = new Task[_threadCount];
        for (int i = 0; i < _threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    SpinMicroseconds(100);
                    _barrier.SignalAndWait();
                }
            });
        }
        Task.WaitAll(tasks);
    }

    /// <summary>
    /// ~1000 microseconds (1ms) of work per phase
    /// </summary>
    [Benchmark]
    public void BarrierWith_1000us_Work()
    {
        var tasks = new Task[_threadCount];
        for (int i = 0; i < _threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    SpinMicroseconds(1000);
                    _barrier.SignalAndWait();
                }
            });
        }
        Task.WaitAll(tasks);
    }

    /// <summary>
    /// Spin-wait barrier alternative (pure spinning, no kernel)
    /// </summary>
    [Benchmark]
    public void SpinBarrier_1us_Work()
    {
        var counter = 0;
        var generation = 0;
        var tasks = new Task[_threadCount];

        for (int i = 0; i < _threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    SpinMicroseconds(1);

                    // Simple spin barrier
                    var gen = Volatile.Read(ref generation);
                    if (Interlocked.Increment(ref counter) == _threadCount)
                    {
                        counter = 0;
                        Interlocked.Increment(ref generation);
                    }
                    else
                    {
                        var sw = new SpinWait();
                        while (Volatile.Read(ref generation) == gen)
                        {
                            sw.SpinOnce();
                        }
                    }
                }
            });
        }
        Task.WaitAll(tasks);
    }

    private static void SpinMicroseconds(int microseconds)
    {
        // Approximate spin for given microseconds
        // This is calibrated for modern CPUs (~3GHz)
        // Actual time will vary but gives relative comparison
        int iterations = microseconds * 300;
        for (int i = 0; i < iterations; i++)
        {
            // Prevent optimization
            Thread.SpinWait(1);
        }
    }
}
