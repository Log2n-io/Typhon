using System;
using BenchmarkDotNet.Attributes;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ECS op matrix — LIFECYCLE: spawn, destroy, commit.
//
// Spawn and destroy are dominated by per-slot chunk allocation and EntityMap/tombstone work rather than by StorageMode, so
// the modes are compared to confirm exactly that (a mode showing up as an outlier here is the signal). Commit is the
// opposite: it is entirely mode-driven -- Versioned stamps the chain and moves B+Tree entries, the Commit discipline
// publishes its staging arena, and SV TickFence does essentially nothing per transaction (the tick fence mops up later).
//
// Spawn/destroy methods ROLL BACK so the database does not grow across invocations; commit methods necessarily commit.
// Batch spawn paths are intentionally NOT duplicated here -- SpawnBatchBenchmarks already tracks loop-vs-batch-vs-SOA.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Lifecycle", "Regression")]
public class EcsLifecycleBenchmarks : IDisposable
{
    private const int N = 1000;

    private EcsOpFixture _f;
    private DatabaseEngine _dbe;

    [GlobalSetup]
    public void Setup()
    {
        _f = new EcsOpFixture(N, "EcsLifecycleBench");
        _dbe = _f.Dbe;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    // ── Spawn ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Per-entity spawn of a two-component SV cluster archetype.</summary>
    [Benchmark(OperationsPerInvoke = N, Baseline = true)]
    public void Spawn_Sv()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < N; i++)
        {
            var pos = new AaBenchPosition(i, i);
            var mov = new AaBenchMovement(1, 1);
            tx.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos), AaBenchAnt.Movement.Set(in mov));
        }
        tx.Rollback();
    }

    /// <summary>Per-entity spawn of a pure-Versioned (legacy-shape) archetype.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void Spawn_Versioned()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < N; i++)
        {
            var health = new AaVcHealth { Current = i, Max = 100 };
            tx.Spawn<AaBenchVersionedUnit>(AaBenchVersionedUnit.Health.Set(in health));
        }
        tx.Rollback();
    }

    /// <summary>Per-entity spawn of a pure-Transient archetype.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void Spawn_Transient()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < N; i++)
        {
            var data = new AaBenchTransientData(i, i);
            tx.Spawn<AaBenchTransientUnit>(AaBenchTransientUnit.Data.Set(in data));
        }
        tx.Rollback();
    }

    /// <summary>Per-entity spawn of an INDEXED SV archetype — adds the deferred index-insert bookkeeping.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void Spawn_Sv_Indexed()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < N; i++)
        {
            var pos = new AaBenchPosition(i, 0);
            var data = new AaBenchIdxData(i, 0);
            tx.Spawn<AaBenchIdxUnit>(AaBenchIdxUnit.Position.Set(in pos), AaBenchIdxUnit.Data.Set(in data));
        }
        tx.Rollback();
    }

    // ── Destroy ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Tombstone the pre-spawned SV set, then roll back so the population is restored for the next invocation.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void Destroy_Sv()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < N; i++)
        {
            tx.Destroy(_f.Sv[i]);
        }
        tx.Rollback();
    }

    /// <summary>
    /// The batch counterpart of <see cref="Destroy_Sv"/> — one <c>EnsureMutable</c> check and a pre-sized pending list for
    /// the whole span, instead of per-entity. Paired deliberately: spawn's loop-vs-batch comparison already exists
    /// (<c>SpawnBatchBenchmarks</c>), but destroy had no batch measurement anywhere, so the "is bulk worth it?" question
    /// was only half answered. Same fixture and same N as Destroy_Sv above, so the two are directly comparable.
    /// </summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void DestroyBatch_Sv()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        tx.DestroyBatch(_f.Sv.AsSpan(0, N));
        tx.Rollback();
    }

    // ── Commit ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Commit after N SV TickFence writes — per-transaction commit work for SV is minimal by design.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void Commit_Sv_TickFence()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < N; i++)
        {
            tx.OpenMut(_f.Sv[i]).Write(AaBenchAnt.Position).X = i;
        }
        tx.Commit();
    }

    /// <summary>Commit under the Commit discipline — stage + build + append + publish per component.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void Commit_Sv_Commit()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit);
        for (int i = 0; i < N; i++)
        {
            tx.OpenMut(_f.Sv[i]).Write(AaBenchAnt.Position).X = i;
        }
        tx.Commit();
    }

    /// <summary>Commit after N Versioned COW writes — chain stamp plus index maintenance.</summary>
    [Benchmark(OperationsPerInvoke = N)]
    public void Commit_Versioned()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < N; i++)
        {
            tx.OpenMut(_f.Mixed[i]).Write(AaBenchMixedCluster.Health).Current = i;
        }
        tx.Commit();
    }

    public void Dispose()
    {
        _f?.Dispose();
        _f = null;
        GC.SuppressFinalize(this);
    }
}
