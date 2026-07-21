using BenchmarkDotNet.Attributes;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// TyphonEvent: zero-cost-when-off regression benchmark (#294)
// ═══════════════════════════════════════════════════════════════════════
//
// The whole producer-side architecture rests on a single load-bearing
// property: when TelemetryConfig.ProfilerActive is false, every BeginX
// factory must JIT-DCE down to ~nothing. A regression here would
// silently slow every BeginX call site by 5-50× without any test
// failing. The codec-equivalence and golden-bytes tests don't catch
// this — they exercise the on-path.
//
// What we measure: a tight loop of N BeginX(...) + Dispose pairs with
// the profiler off. The expected per-call cost is on the order of one
// branch-predictor-friendly conditional: ProfilerActive is `static
// readonly` so the JIT folds the gate at tier-1, and the rest of the
// factory body is unreachable. The Empty baseline below is an inlined
// no-op call, so the delta between BeginX and Empty bounds the cost
// of "almost nothing happened."
//
// Acceptance bar: BeginX/Dispose mean time should be within 2 ns of
// EmptyMethod mean time on x64. Anything larger means DCE is broken
// somewhere in the prologue, MakeHeader, the Dispose path, or the
// factory itself. Run via:
//   dotnet run -c Release --filter '*TyphonEventOffPath*'

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Profiler", "Regression")]
public class TyphonEventOffPathBenchmarks
{
    // Tight inner loop — the per-call cost is too small to measure once, so we batch.
    [Params(1024)]
    public int LoopCount;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Ensure the profiler is off. typhon.telemetry.json in the bench bin/ dir defaults this OFF for
        // benchmarks; this is belt-and-braces.
        if (Typhon.Engine.TelemetryConfig.ProfilerActive)
        {
            throw new System.InvalidOperationException(
                "TyphonEventOffPathBenchmarks requires ProfilerActive=false. Check typhon.telemetry.json in the benchmark bin directory.");
        }
    }

    /// <summary>Baseline — the JIT should inline this to a no-op loop body.</summary>
    [Benchmark(Baseline = true)]
    public long EmptyMethod()
    {
        long sum = 0;
        for (int i = 0; i < LoopCount; i++)
        {
            sum += EmptyInline(i);
        }
        return sum;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static long EmptyInline(int i) => i;

    /// <summary>No-payload BeginX — exercises the simplest possible factory.</summary>
    [Benchmark]
    public void BeginBTreeInsert_OffPath()
    {
        for (int i = 0; i < LoopCount; i++)
        {
            using var ev = TyphonEvent.BeginBTreeInsert();
            // Empty body: when off, BeginBTreeInsert returns default(BTreeInsertEvent) and Dispose is a no-op.
        }
    }

    /// <summary>Single-payload BeginX — adds one factory parameter assignment when on.</summary>
    [Benchmark]
    public void BeginEcsSpawn_OffPath()
    {
        for (int i = 0; i < LoopCount; i++)
        {
            using var ev = TyphonEvent.BeginEcsSpawn(archetypeId: 7);
        }
    }

    /// <summary>BeginX + optional setter — exercises the property-setter mask path. try/finally instead of `using var`
    /// because C# forbids member writes on a `using var` ref struct (CS1654).</summary>
    [Benchmark]
    public void BeginEcsSpawn_WithOptional_OffPath()
    {
        for (int i = 0; i < LoopCount; i++)
        {
            var ev = TyphonEvent.BeginEcsSpawn(archetypeId: 7);
            try
            {
                ev.EntityId = 42UL;
                ev.Tsn = 99L;
            }
            finally
            {
                ev.Dispose();
            }
        }
    }

    /// <summary>Larger payload — three required factory params + one optional.</summary>
    [Benchmark]
    public void BeginCheckpointCycle_OffPath()
    {
        for (int i = 0; i < LoopCount; i++)
        {
            var ev = TyphonEvent.BeginCheckpointCycle(targetLsn: 1000L, reason: Typhon.Profiler.CheckpointReason.Periodic);
            try
            {
                ev.DirtyPageCount = 256;
            }
            finally
            {
                ev.Dispose();
            }
        }
    }
}
