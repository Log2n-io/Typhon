using BenchmarkDotNet.Attributes;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Concurrency: AccessControl RW Lock Microbenchmarks
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Concurrency", "Regression")]
public class AccessControlBenchmarks
{
    private AccessControl _lock;

    // Background contention state
    private volatile bool _stopContention;
    private Task[] _contentionTasks;

    [Params(2, 4, 8)]
    public int ThreadCount;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _lock = new AccessControl();
    }

    [IterationSetup(Target = nameof(SharedLock_Contended))]
    public void SetupContention()
    {
        _stopContention = false;
        // Start (ThreadCount - 1) background threads continuously acquiring/releasing shared locks
        _contentionTasks = new Task[ThreadCount - 1];
        for (int i = 0; i < _contentionTasks.Length; i++)
        {
            _contentionTasks[i] = Task.Run(() =>
            {
                while (!_stopContention)
                {
                    _lock.EnterSharedAccess(ref WaitContext.Null);
                    _lock.ExitSharedAccess();
                }
            });
        }
        // Brief pause to ensure contention threads are running
        Thread.Sleep(1);
    }

    [IterationCleanup(Target = nameof(SharedLock_Contended))]
    public void CleanupContention()
    {
        _stopContention = true;
        if (_contentionTasks != null)
        {
            Task.WaitAll(_contentionTasks);
            _contentionTasks = null;
        }
    }

    /// <summary>
    /// Single-thread shared acquire/release cycle — baseline for uncontended path.
    /// </summary>
    [Benchmark]
    public void SharedLock_Uncontended()
    {
        _lock.EnterSharedAccess(ref WaitContext.Null);
        _lock.ExitSharedAccess();
    }

    /// <summary>
    /// Single-thread exclusive acquire/release cycle — baseline for uncontended path.
    /// </summary>
    [Benchmark]
    public void ExclusiveLock_Uncontended()
    {
        _lock.EnterExclusiveAccess(ref WaitContext.Null);
        _lock.ExitExclusiveAccess();
    }

    /// <summary>
    /// Shared acquire/release while (ThreadCount - 1) background threads also acquire shared.
    /// Measures cache-line contention overhead on atomic operations.
    /// </summary>
    [Benchmark]
    public void SharedLock_Contended()
    {
        _lock.EnterSharedAccess(ref WaitContext.Null);
        _lock.ExitSharedAccess();
    }

    /// <summary>
    /// Full promotion cycle: shared → exclusive → demote → release shared.
    /// </summary>
    [Benchmark]
    public void Promotion_SharedToExclusive()
    {
        _lock.EnterSharedAccess(ref WaitContext.Null);
        _lock.TryPromoteToExclusiveAccess(ref WaitContext.Null);
        _lock.DemoteFromExclusiveAccess();
        _lock.ExitSharedAccess();
    }
}
