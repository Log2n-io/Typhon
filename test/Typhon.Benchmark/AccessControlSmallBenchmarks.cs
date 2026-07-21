using BenchmarkDotNet.Attributes;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Concurrency: AccessControlSmall 4-byte RW Lock Microbenchmarks
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Concurrency", "Regression")]
public class AccessControlSmallBenchmarks
{
    private AccessControlSmall _lock;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _lock = new AccessControlSmall();
    }

    /// <summary>
    /// Single-thread shared acquire/release on the compact 4-byte RW lock.
    /// </summary>
    [Benchmark]
    public void SharedLock_Uncontended()
    {
        _lock.EnterSharedAccess(ref WaitContext.Null);
        _lock.ExitSharedAccess();
    }

    /// <summary>
    /// Single-thread exclusive acquire/release on the compact 4-byte RW lock.
    /// </summary>
    [Benchmark]
    public void ExclusiveLock_Uncontended()
    {
        _lock.EnterExclusiveAccess(ref WaitContext.Null);
        _lock.ExitExclusiveAccess();
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
