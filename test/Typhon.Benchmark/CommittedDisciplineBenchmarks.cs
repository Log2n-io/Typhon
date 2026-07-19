using System;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Committed durability discipline write/commit benchmarks (#392 AC-5).
// Targets (Zen 4): Commit-discipline write ≤ 25 ns; commit publish ≤ 60 ns/component; ≥ 8× faster than a Versioned write;
// the SV TickFence in-place path (~3 ns) unregressed.
//
// Reuses the cluster archetypes from ArchetypeAccessorBenchmark.cs:
//   AaBenchAnt (510):          SV Position + SV Movement
//   AaBenchMixedCluster (516): SV Position + SV Movement + Versioned Health
//
// Each benchmark drives N entities through one transaction and reports per-component time via OperationsPerInvoke = N
// (the per-transaction create/dispose overhead amortizes away). Write benchmarks roll back (isolate the write); the commit
// benchmark commits (Deferred, so no fsync dominates the in-memory publish cost).
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 7)]
[MemoryDiagnoser]
[BenchmarkCategory("Committed", "Durability")]
public class CommittedDisciplineBenchmarks : IDisposable
{
    private const int N = 1000;

    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private EntityId[] _svIds;       // AaBenchAnt — SV Position
    private EntityId[] _mixedIds;    // AaBenchMixedCluster — Versioned Health
    private Transaction _stagedTx;   // Publish_Only: a Commit tx with N staged writes, not yet committed

    [GlobalSetup]
    public void Setup()
    {

        var name = $"CommittedBench_{Environment.ProcessId}";
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = name;
              o.DatabaseCacheSize = (ulong)(200 * 1024 * PagedMMF.PageSize);
              o.PagesDebugPattern = false;
          })
          .AddInMemoryWalEngine();

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();

        _dbe.RegisterComponentFromAccessor<AaBenchPosition>();
        _dbe.RegisterComponentFromAccessor<AaBenchMovement>();
        _dbe.RegisterComponentFromAccessor<AaVcHealth>();
        _dbe.InitializeArchetypes();

        _svIds = new EntityId[N];
        _mixedIds = new EntityId[N];
        using (var tx = _dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < N; i++)
            {
                var pos = new AaBenchPosition(i, i);
                var mov = new AaBenchMovement(1, 1);
                _svIds[i] = tx.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos), AaBenchAnt.Movement.Set(in mov));
            }
            tx.Commit();
        }
        using (var tx = _dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < N; i++)
            {
                var pos = new AaBenchPosition(i, i);
                var mov = new AaBenchMovement(1, 1);
                var health = new AaVcHealth { Current = 100, Max = 100 };
                _mixedIds[i] = tx.Spawn<AaBenchMixedCluster>(
                    AaBenchMixedCluster.Position.Set(in pos), AaBenchMixedCluster.Movement.Set(in mov), AaBenchMixedCluster.Health.Set(in health));
            }
            tx.Commit();
        }
    }

    // ── Resolution-only baseline (OpenMut, no write). The AC-5 write/stage cost is the DELTA of the write benchmarks over this. ──
    [Benchmark(OperationsPerInvoke = N, Baseline = true)]
    public void Resolve_Only()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        ulong sink = 0;
        for (int i = 0; i < N; i++)
        {
            sink += tx.OpenMut(_svIds[i]).Id.RawValue;
        }
        tx.Rollback();
        GC.KeepAlive(sink);
    }

    // ── Write cost: SV TickFence in-place (baseline ~3 ns over resolution) ──────────────
    [Benchmark(OperationsPerInvoke = N)]
    public void Write_Sv_TickFence()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < N; i++)
        {
            tx.OpenMut(_svIds[i]).Write(AaBenchAnt.Position).X = i;
        }
        tx.Rollback();
    }

    // ── Write cost: Commit discipline (Variant-A staging, target ≤ 25 ns) ───────────────
    [Benchmark(OperationsPerInvoke = N)]
    public void Write_Sv_Commit()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred, DurabilityDiscipline.Commit);
        for (int i = 0; i < N; i++)
        {
            tx.OpenMut(_svIds[i]).Write(AaBenchAnt.Position).X = i;
        }
        tx.Rollback();
    }

    // ── Write cost: Versioned COW (the path Committed replaces, ~146–170 ns) ────────────
    [Benchmark(OperationsPerInvoke = N)]
    public void Write_Versioned()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < N; i++)
        {
            tx.OpenMut(_mixedIds[i]).Write(AaBenchMixedCluster.Health).Current = i;
        }
        tx.Rollback();
    }

    // ── Commit cost: full Commit-discipline transaction (stage + BUILD + APPEND + publish per component) ──
    [Benchmark(OperationsPerInvoke = N)]
    public void Commit_Sv_Commit()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred, DurabilityDiscipline.Commit);
        for (int i = 0; i < N; i++)
        {
            tx.OpenMut(_svIds[i]).Write(AaBenchAnt.Position).X = i;
        }
        tx.Commit();
    }

    // ── Publish cost in ISOLATION (target ≤ 60 ns/component): just the PUBLISH pass (re-resolve + memcpy→HEAD + SetDirty),
    //    excluding BUILD/APPEND/WAIT. Staged once per iteration in IterationSetup; the measured body re-runs the publish pass. ──
    [IterationSetup(Target = nameof(Publish_Only))]
    public void StagePublishTx()
    {
        _stagedTx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred, DurabilityDiscipline.Commit);
        for (int i = 0; i < N; i++)
        {
            _stagedTx.OpenMut(_svIds[i]).Write(AaBenchAnt.Position).X = i; // stage only — not committed
        }
    }

    [IterationCleanup(Target = nameof(Publish_Only))]
    public void DisposePublishTx()
    {
        _stagedTx?.Rollback();
        _stagedTx?.Dispose();
        _stagedTx = null;
    }

    [Benchmark(OperationsPerInvoke = N)]
    public void Publish_Only() => _stagedTx.PublishStagedForBenchmark();

    public void Dispose()
    {
        _stagedTx?.Dispose();
        _stagedTx = null;
        _serviceProvider?.Dispose();
        _serviceProvider = null;
    }
}
