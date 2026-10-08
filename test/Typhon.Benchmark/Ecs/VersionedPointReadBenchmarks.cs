using System;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// POINT READ OF A VERSIONED COMPONENT — the path every MVCC-correct read takes: EntityRef.Read → ReadEcsComponentData →
// the component's content chunk through the transaction's component accessor.
//
// Two shapes: one read-only transaction reading every entity, and many short read-only transactions of 64 reads each (what a
// reader that keeps its pinned set small does: MarketHardeningTests' snapshot). The cache holds the whole database, so the
// numbers are the read path's own cost, not eviction's.
//
// Means are PER READ (OperationsPerInvoke = Entities).
//
// Measured 2026-10-08 (7950X, two runs) against the pre-PS-19 path — accessors copied by value, read-only transactions not
// disposing theirs — through a temporary same-binary switch: one transaction 106–116 ns vs 131–146 ns, short transactions
// 117–128 ns vs 142–147 ns per read. Then the map's run-preserving hash against plain xxHash32, the same way: key order 75–79 ns vs
// 96–108 ns, short transactions 82 ns vs 113–117 ns, random order 234–255 ns vs 223–280 ns (no change beyond noise).
//
// Run: dotnet run -c Release -- --filter '*VersionedPointRead*'
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Benchmark.Vpr.Value", 1)]
[StructLayout(LayoutKind.Sequential)]
struct VprValue
{
    [Field] public long V;
    [Field] public long W;
}

[Archetype]
partial class VprUnit : Archetype<VprUnit>
{
    public static readonly Comp<VprValue> Value = Register<VprValue>();
}

[SimpleJob(warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
[BenchmarkCategory("PointRead")]
public class VersionedPointReadBenchmarks : IDisposable
{
    private const int Entities = 100_000;
    private const int ShortTransaction = 64;

    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private EntityId[] _ids;
    private EntityId[] _shuffled;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = $"VprBench_{Environment.ProcessId}";
              o.DatabaseCacheSize = (ulong)(200L * 1024 * PagedMMF.PageSize);
              o.TestMode = true;
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<VprValue>();
        _dbe.InitializeArchetypes();

        _ids = new EntityId[Entities];
        for (var s = 0; s < Entities; s += 2_000)
        {
            using var tx = _dbe.CreateQuickTransaction();
            for (var i = s; i < s + 2_000; i++)
            {
                var v = new VprValue { V = i, W = -i };
                _ids[i] = tx.Spawn<VprUnit>(VprUnit.Value.Set(in v));
            }

            tx.Commit();
        }

        _shuffled = (EntityId[])_ids.Clone();
        new Random(1205).Shuffle(_shuffled);
    }

    [Benchmark(OperationsPerInvoke = Entities)]
    public long OneTransaction()
    {
        long sum = 0;
        using var tx = _dbe.CreateReadOnlyTransaction();
        for (var i = 0; i < Entities; i++)
        {
            sum += tx.Open(_ids[i]).Read(VprUnit.Value).V;
        }

        return sum;
    }

    /// <summary>The same reads in random key order: no run for the map's hash to keep together.</summary>
    [Benchmark(OperationsPerInvoke = Entities)]
    public long RandomOrder()
    {
        long sum = 0;
        using var tx = _dbe.CreateReadOnlyTransaction();
        for (var i = 0; i < Entities; i++)
        {
            sum += tx.Open(_shuffled[i]).Read(VprUnit.Value).V;
        }

        return sum;
    }

    [Benchmark(OperationsPerInvoke = Entities)]
    public long ShortTransactions()
    {
        long sum = 0;
        for (var s = 0; s < Entities; s += ShortTransaction)
        {
            using var tx = _dbe.CreateReadOnlyTransaction();
            for (var i = s; i < s + ShortTransaction && i < Entities; i++)
            {
                sum += tx.Open(_ids[i]).Read(VprUnit.Value).V;
            }
        }

        return sum;
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _dbe?.Dispose();
        _sp?.Dispose();
    }
}
