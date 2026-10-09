using System;
using BenchmarkDotNet.Attributes;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// A transaction's begin and end, alone: the chain registration (push, TSN draw and publish — TXC-01), the epoch scope,
// the ChangeSet rental, and the removal. EcsLifecycleBenchmarks amortise one begin over a thousand operations, so a
// change to the begin path does not show there.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Transaction", "Regression")]
public class TransactionBeginBenchmarks : IDisposable
{
    private const int N = 1000;

    private EcsOpFixture _f;
    private DatabaseEngine _dbe;

    [GlobalSetup]
    public void Setup()
    {
        _f = new EcsOpFixture(N, "TxBeginBench");
        _dbe = _f.Dbe;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>A read-only transaction created and disposed: registration and removal, no ChangeSet, no cleanup.</summary>
    [Benchmark(OperationsPerInvoke = N, Baseline = true)]
    public void CreateDispose_ReadOnly()
    {
        for (var i = 0; i < N; i++)
        {
            using var tx = _dbe.CreateReadOnlyTransaction();
        }
    }

    /// <summary>
    /// A quick transaction created and disposed: its unit of work, its ChangeSet, and — the tail, alone in the chain — the cutoff its disposal computes.
    /// </summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void CreateDispose_Quick()
    {
        for (var i = 0; i < N; i++)
        {
            using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        }
    }

    public void Dispose()
    {
        _f?.Dispose();
        _f = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The cutoff a leaving tail computes (TXC-01) with 64 live transactions in the chain: one walk over every node, reading each one's TSN. The chain is
/// cache-hot here; it measures the walk's instructions, not its misses.
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[BenchmarkCategory("Transaction", "Regression")]
public class TransactionCutoffWalkBenchmarks : IDisposable
{
    private const int Live = 64;

    private EcsOpFixture _f;
    private DatabaseEngine _dbe;
    private Transaction[] _live;

    [GlobalSetup]
    public void Setup()
    {
        _f = new EcsOpFixture(16, "TxCutoffBench");
        _dbe = _f.Dbe;
        _live = new Transaction[Live];
        for (var i = 0; i < Live; i++)
        {
            _live[i] = _dbe.CreateReadOnlyTransaction();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    [Benchmark]
    public long Cutoff_64Live()
    {
        var chain = _dbe.TransactionChain;
        chain.Control.EnterSharedAccess(ref WaitContext.Null);
        var cutoff = chain.ComputeNextMinTSN();
        chain.Control.ExitSharedAccess();
        return cutoff;
    }

    public void Dispose()
    {
        if (_live != null)
        {
            for (var i = Live - 1; i >= 0; i--)
            {
                _live[i]?.Dispose();
            }

            _live = null;
        }

        _f?.Dispose();
        _f = null;
        GC.SuppressFinalize(this);
    }
}
