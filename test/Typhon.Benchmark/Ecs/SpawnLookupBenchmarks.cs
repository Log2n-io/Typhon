using System;
using BenchmarkDotNet.Attributes;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Own-spawn lookup cost (#1477).
//
// A transaction finds its own pending spawns through a lazily built EntityId → list-index map. Until #1477 an append marked
// that map stale and the next lookup rebuilt it from the whole list, so "spawn one, look one up" repeated N times cost
// N(N+1)/2 map inserts — the two-step initialization of a component larger than a spawn value carries (Spawn, then
// OpenMut(id).Enable) is exactly that shape. Each method is reported per entity, so a flat line across N is linear and a
// line growing with N is the quadratic coming back.
//
//   SpawnThenOpenMut      the #1477 shape: append and lookup alternate
//   SpawnAllThenOpenMut   same work, all appends first — one lookup pass over a complete list (the control)
//   SpawnAfterOneLookup   one lookup, then N pure spawns: an append must stay free once the map exists
//
// Every method rolls back, so the database does not grow across invocations.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Lifecycle", "Regression")]
public class SpawnLookupBenchmarks : IDisposable
{
    private EcsOpFixture _f;
    private DatabaseEngine _dbe;
    private EntityId[] _ids;

    [Params(16, 256, 4096)]
    public int N;

    [GlobalSetup]
    public void Setup()
    {
        _f = new EcsOpFixture(16, "SpawnLookupBench");
        _dbe = _f.Dbe;
        _ids = new EntityId[N];
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>The #1477 shape: each spawn is opened and completed before the next one is appended.</summary>
    [Benchmark(Baseline = true)]
    public void SpawnThenOpenMut()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        var mov = new AaBenchMovement(1, 1);
        for (int i = 0; i < N; i++)
        {
            var pos = new AaBenchPosition(i, i);
            var id = tx.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos));
            tx.OpenMut(id).Enable(AaBenchAnt.Movement, in mov);
        }
        tx.Rollback();
    }

    /// <summary>The same spawns and Enables, appends first: the cost the interleaved shape should match.</summary>
    [Benchmark]
    public void SpawnAllThenOpenMut()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        var mov = new AaBenchMovement(1, 1);
        for (int i = 0; i < N; i++)
        {
            var pos = new AaBenchPosition(i, i);
            _ids[i] = tx.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos));
        }
        for (int i = 0; i < N; i++)
        {
            tx.OpenMut(_ids[i]).Enable(AaBenchAnt.Movement, in mov);
        }
        tx.Rollback();
    }

    /// <summary>One lookup builds the map, then N plain spawns follow and nothing looks them up: they must cost what a spawn costs.</summary>
    [Benchmark]
    public void SpawnAfterOneLookup()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        var pos = new AaBenchPosition(0, 0);
        var mov = new AaBenchMovement(1, 1);
        var first = tx.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos));
        tx.OpenMut(first).Enable(AaBenchAnt.Movement, in mov);
        for (int i = 1; i < N; i++)
        {
            pos = new AaBenchPosition(i, i);
            tx.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos), AaBenchAnt.Movement.Set(in mov));
        }
        tx.Rollback();
    }

    public void Dispose()
    {
        _f?.Dispose();
        _f = null;
    }
}
