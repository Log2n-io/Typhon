using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;

namespace Typhon.Benchmark;

/// <summary>
/// The revision-chain read paths a snapshot takes, measured where they actually run. <see cref="RevisionBenchmarks"/> reads entities whose chains the
/// cleanup has already trimmed to one entry, so every one of its reads takes the lock-free single-entry fast path. Here a reader opened before ten
/// updates pins the chain's first revision: every read of that entity walks a multi-entry chain under its shared lock, the path a snapshot pays while
/// writers commit to what it reads.
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 10)]
[BenchmarkCategory("MVCC", "Regression")]
public class RevisionLockedWalkBenchmarks : IDisposable
{
    private const int Reads = 1_000;

    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private Transaction _pin;
    private Transaction _reader;
    private EntityId _pinnedChain;
    private EntityId _trimmedChain;

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
              options.DatabaseName = $"RevisionLockedWalkBench_{System.Environment.ProcessId}";
              options.DatabaseCacheSize = (ulong)(64 * 1024 * PagedMMF.PageSize);
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<RevComp>();
        _dbe.InitializeArchetypes();

        using (var t = _dbe.CreateQuickTransaction())
        {
            _pinnedChain = t.Spawn<RevArch>(RevArch.Rev.Set(new RevComp { Value = 0 }));
            _trimmedChain = t.Spawn<RevArch>(RevArch.Rev.Set(new RevComp { Value = 0 }));
            t.Commit();
        }

        // Opened before the updates: its snapshot needs revision 0, so the cleanup keeps every revision after it.
        _pin = _dbe.CreateReadOnlyTransaction();
        _ = _pin.Open(_pinnedChain).Read(RevArch.Rev);
        for (var i = 1; i <= 10; i++)
        {
            using var t = _dbe.CreateQuickTransaction();
            var e = t.OpenMut(_pinnedChain);
            var c = e.Read(RevArch.Rev);
            c.Value = i;
            e.Set(RevArch.Rev, c);
            t.Commit();
        }

        _reader = _dbe.CreateReadOnlyTransaction();
    }

    [GlobalCleanup]
    public void GlobalCleanup() => Dispose();

    public void Dispose()
    {
        _reader?.Dispose();
        _reader = null;
        _pin?.Dispose();
        _pin = null;
        _dbe?.Dispose();
        _dbe = null;
        _serviceProvider?.Dispose();
        _serviceProvider = null;
    }

    /// <summary>A trimmed chain: the single-entry fast path, no lock, no clock.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = Reads)]
    public int Read_TrimmedChain()
    {
        var sum = 0;
        for (var k = 0; k < Reads; k++)
        {
            sum += _reader.Open(_trimmedChain).Read(RevArch.Rev).Value;
        }

        return sum;
    }

    /// <summary>A chain an older reader pins at eleven entries: the walk under the chain's shared lock.</summary>
    [Benchmark(OperationsPerInvoke = Reads)]
    public int Read_ChainPinnedByAReader()
    {
        var sum = 0;
        for (var k = 0; k < Reads; k++)
        {
            sum += _reader.Open(_pinnedChain).Read(RevArch.Rev).Value;
        }

        return sum;
    }
}
