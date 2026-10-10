using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading;
using Typhon.Engine;
using Typhon.Engine.Internals;

namespace Typhon.Benchmark;

/// <summary>
/// Revision-chain locks taken by several threads at once, each on entities of its own whose chain roots share pages (#1253, PS-20). Each lock word sits
/// in its chain's root chunk, a cache line each; a hold also reads its page's capture-fence word, one per cache slot, written only by captures. A reader
/// opened before the updates keeps every chain multi-entry, so each read walks under the chain's shared lock. <see cref="Assignment.Contiguous"/> gives
/// each thread a run of consecutive entities; <see cref="Assignment.Interleaved"/> deals them round-robin, the pattern that puts neighbouring roots on
/// different threads; <see cref="Assignment.SameResidue"/> deals round-robin only every 8th entity, roots 8 lines apart. That deal measured the
/// alternative this design replaced — the lock words moved to a per-slot shadow block, where those roots' words shared one line: 2x slower at 8 and 16
/// threads — and shows nothing here. Times are per call, all threads together.
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 10)]
[BenchmarkCategory("MVCC", "Concurrency")]
public class ChainLockContentionBenchmarks : System.IDisposable
{
    public enum Assignment
    {
        Contiguous,
        Interleaved,
        SameResidue,
    }

    private const int Entities = 2_000;
    private const int Reads = 64_000;

    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private Transaction _pin;
    private EntityId[] _ids;
    private EntityId[][] _perThread;
    private Thread[] _workers;
    private Barrier _start;
    private Barrier _done;
    private long[] _sums;
    private volatile bool _stop;

    [Params(1, 4, 8, 16)]
    public int ThreadCount;

    [Params(Assignment.Contiguous, Assignment.Interleaved, Assignment.SameResidue)]
    public Assignment Deal;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = $"ChainLockContentionBench_{System.Environment.ProcessId}";
              options.DatabaseCacheSize = (ulong)(64 * 1024 * PagedMMF.PageSize);
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<RevComp>();
        _dbe.InitializeArchetypes();

        _ids = new EntityId[Entities];
        using (var t = _dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Entities; i++)
            {
                _ids[i] = t.Spawn<RevArch>(RevArch.Rev.Set(new RevComp { Value = i }));
            }

            t.Commit();
        }

        // Opened before the updates: every chain keeps the revisions after its first, so each read below walks under the chain's shared lock.
        _pin = _dbe.CreateReadOnlyTransaction();
        for (var round = 1; round <= 3; round++)
        {
            using var t = _dbe.CreateQuickTransaction();
            for (var i = 0; i < Entities; i++)
            {
                var e = t.OpenMut(_ids[i]);
                var c = e.Read(RevArch.Rev);
                c.Value += round;
                e.Set(RevArch.Rev, c);
            }

            t.Commit();
        }

        _perThread = new EntityId[ThreadCount][];
        var dealt = Deal == Assignment.SameResidue ? Entities / 8 : Entities;
        var perThread = dealt / ThreadCount;
        for (var t = 0; t < ThreadCount; t++)
        {
            _perThread[t] = new EntityId[perThread];
            for (var k = 0; k < perThread; k++)
            {
                _perThread[t][k] = Deal switch
                {
                    Assignment.Contiguous => _ids[t * perThread + k],
                    Assignment.Interleaved => _ids[k * ThreadCount + t],
                    _ => _ids[(k * ThreadCount + t) * 8],
                };
            }
        }

        // Workers live for the whole run: no thread is started inside a measured invocation.
        if (ThreadCount > 1)
        {
            _start = new Barrier(ThreadCount + 1);
            _done = new Barrier(ThreadCount + 1);
            _sums = new long[ThreadCount];
            _workers = new Thread[ThreadCount];
            for (var t = 0; t < ThreadCount; t++)
            {
                var threadNo = t;
                _workers[t] = new Thread(() =>
                {
                    while (true)
                    {
                        _start.SignalAndWait();
                        if (_stop)
                        {
                            return;
                        }

                        _sums[threadNo] = ReadOwn(threadNo, Reads / ThreadCount);
                        _done.SignalAndWait();
                    }
                }) { IsBackground = true, Name = $"chainlock-bench-{t}" };
                _workers[t].Start();
            }
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => Dispose();

    public void Dispose()
    {
        if (_workers != null)
        {
            _stop = true;
            _start.SignalAndWait();
            foreach (var w in _workers)
            {
                w.Join();
            }

            _workers = null;
        }

        _pin?.Dispose();
        _dbe?.Dispose();
        _serviceProvider?.Dispose();
    }

    /// <summary>Each thread reads its own entities, over and over, in its own snapshot: a shared chain-lock enter and exit per read.</summary>
    [Benchmark(OperationsPerInvoke = Reads)]
    public long LockedWalks()
    {
        if (ThreadCount == 1)
        {
            return ReadOwn(0, Reads);
        }

        _start.SignalAndWait();
        _done.SignalAndWait();
        long sum = 0;
        foreach (var s in _sums)
        {
            sum += s;
        }

        return sum;
    }

    private long ReadOwn(int threadNo, int reads)
    {
        var own = _perThread[threadNo];
        long sum = 0;
        using var tx = _dbe.CreateReadOnlyTransaction();
        for (var r = 0; r < reads; r++)
        {
            sum += tx.Open(own[r % own.Length]).Read(RevArch.Rev).Value;
        }

        return sum;
    }
}
