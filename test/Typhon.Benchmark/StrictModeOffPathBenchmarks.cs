using BenchmarkDotNet.Attributes;
using Typhon.Engine;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// CheckConfig (strict mode): zero-cost-when-off regression benchmark (#422)
// ═══════════════════════════════════════════════════════════════════════
//
// The whole strict-mode design rests on one load-bearing property: when
// CheckConfig.Enabled is false (the Release/production default), every
// converted `CheckConfig.Require(CheckConfig.Enabled, cond, $"…")` call
// site must JIT-DCE down to ~nothing — including skipping evaluation of
// the interpolated message arguments (the handler's `out bool shouldAppend`
// short-circuits argument evaluation, not just formatting).
//
// A regression here would silently tax every converted hot-path check
// (EntityRef per-read/write, ClusterRef, etc.) without any test failing.
//
// Acceptance bar: Require_OffPath mean time is within ~2 ns of the
// EmptyMethod baseline on x64, and allocates zero bytes. Run via:
//   dotnet run -c Release --filter '*StrictModeOffPath*'
//
// Measured (2026-07-02, Ryzen 7950X, real tiered JIT): the off-path cost of a
// `CheckConfig.Require(CheckConfig.Enabled, cond, $"…")` call is ~0.24 ns/call
// (≈1 cycle — within the noise of the work loop) with zero allocation. Confirms
// the gate + interpolated-message handler JIT-fold to nothing when strict mode
// is off. NOTE: run this with the *external* toolchain, not InProcess — the
// InProcessEmit toolchain does not reproduce `static readonly` constant-folding
// and reports a spurious ~9 ns/call.

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Checks", "Regression")]
public class StrictModeOffPathBenchmarks
{
    [Params(1024)]
    public int LoopCount;

    private int _value;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _value = 7;
        if (CheckConfig.Enabled)
        {
            throw new System.InvalidOperationException(
                "StrictModeOffPathBenchmarks requires CheckConfig.Enabled=false. Check typhon.telemetry.json in the benchmark bin directory (Checks:Enabled must be absent/false).");
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

    /// <summary>
    /// Passing-condition Require with an interpolated message — the common converted-site shape. When strict mode is off the
    /// gate folds and the `$"…{_value}…"` message is never built (nor its argument evaluated).
    /// </summary>
    [Benchmark]
    public void Require_ConditionTrue_OffPath()
    {
        for (int i = 0; i < LoopCount; i++)
        {
            CheckConfig.Require(CheckConfig.Enabled, _value >= 0, $"unexpected value {_value} at {i}");
        }
    }

    /// <summary>
    /// Failing-condition Require — proves the gate (not the condition) is what suppresses the throw/format: even though the
    /// condition is false, strict mode being off means no throw and no message build.
    /// </summary>
    [Benchmark]
    public void Require_ConditionFalse_OffPath()
    {
        for (int i = 0; i < LoopCount; i++)
        {
            CheckConfig.Require(CheckConfig.Enabled, _value < 0, $"unexpected value {_value} at {i}");
        }
    }
}
