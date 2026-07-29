using BenchmarkDotNet.Attributes;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Benchmark;

/// <summary>
/// Benchmark comparing different mechanisms for toggling telemetry at runtime.
///
/// This is the evidence behind ADR-019: a single build that toggles at runtime, rather than a separate
/// build configuration. It stays useful as the reference measurement for the gate mechanisms still in use
/// (see <c>CheckConfig</c>, which resolves its gates once at static init to keep them JIT-foldable).
///
/// Toggle mechanisms tested:
/// - No check at all (baseline)
/// - Static readonly bool (JIT can eliminate dead code in Tier 1)
/// - Static bool (runtime check every call)
/// - Instance bool field (runtime check + memory access)
/// - Func&lt;bool&gt; delegate (delegate invocation overhead)
/// - Interface null check (null check + potential virtual dispatch)
/// - Static readonly with actual telemetry work (realistic scenario)
/// </summary>
/// <remarks>
/// Key insight: The JIT compiler's Tier 1 can eliminate branches on `static readonly` fields
/// because they are guaranteed never to change after class initialization. This benchmark
/// verifies whether this optimization actually occurs and measures the residual overhead.
///
/// Uses OperationsPerInvoke to report per-operation timings.
/// </remarks>
[SimpleJob(warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
[BenchmarkCategory("Telemetry")]
public class TelemetryToggleBenchmark
{
    private const int Iterations = 100_000;

    // ═══════════════════════════════════════════════════════════════════════════
    // TOGGLE MECHANISMS - Different ways to enable/disable telemetry
    // ═══════════════════════════════════════════════════════════════════════════

    // Static readonly - JIT can eliminate dead branches in Tier 1
    private static readonly bool s_staticReadonlyDisabled = false;
    private static readonly bool s_staticReadonlyEnabled = true;

    // Tier-2 gate (Phase 1) — mirrors the shape of TelemetryConfig.ConcurrencyAdaptiveWaiterStepActive et al.
    // Disabled: JIT must fold the inlined factory to `return 0`.
    // Enabled: JIT inlines the factory, falls through to the NoInlining prologue stand-in.
    private static readonly bool s_tier2Disabled = false;
    private static readonly bool s_tier2Enabled = true;

    // Regular static - cannot be eliminated by JIT
    private static bool s_staticDisabled = false;
    private static bool s_staticEnabled = true;

    // Instance field
    private bool _instanceDisabled = false;

    // Delegate approach
    private static readonly Func<bool> s_delegateReturnsFalse = () => false;
    private static readonly Func<bool> s_delegateNull = null;

    // Interface approach
    private static readonly ITelemetryRecorder s_interfaceNoOp = new NoOpTelemetryRecorder();
    private static readonly ITelemetryRecorder s_interfaceNull = null;

    // ═══════════════════════════════════════════════════════════════════════════
    // TELEMETRY SIMULATION - Mimics actual AccessControl telemetry work
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Simulates the AccessOperation struct used in actual telemetry.
    /// Matches the layout from AccessOperation.cs
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TelemetryOperation
    {
        public ulong LockData;      // 8 bytes
        public long Tick;           // 8 bytes
        public byte Type;           // 1 byte
        public byte ThreadId;       // 1 byte

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RecordNow()
        {
            Tick = DateTime.UtcNow.Ticks;
            ThreadId = (byte)Environment.CurrentManagedThreadId;
        }
    }

    // Sink to prevent dead code elimination
    private static long s_sink;
    private static TelemetryOperation s_opSink;

    // ═══════════════════════════════════════════════════════════════════════════
    // BENCHMARKS - TOGGLE CHECK ONLY (telemetry disabled)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Baseline: No check at all - represents the ideal case where telemetry
    /// code is completely absent.
    /// </summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = Iterations)]
    public long NoCheck_Baseline()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            sum += DoActualWork(i);
        }
        s_sink = sum;
        return sum;
    }

    /// <summary>
    /// Static readonly bool check (disabled).
    /// JIT Tier 1 should eliminate this branch entirely because the value is known at JIT time.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public long StaticReadonly_Disabled()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            if (s_staticReadonlyDisabled)
            {
                RecordTelemetryOperation((byte)i);
            }
            sum += DoActualWork(i);
        }
        s_sink = sum;
        return sum;
    }

    /// <summary>
    /// Tier-2 gate-shape (disabled). Mimics the eventual Phase 2 factory shape:
    /// an aggressively-inlined method that checks a static-readonly Tier-2 flag, returns
    /// <c>default</c> on miss, otherwise calls a NoInlining "prologue-shape" stand-in.
    /// When the flag is false, JIT Tier-1 should fold the entire inlined call to
    /// <c>return 0</c> — runtime should match <see cref="NoCheck_Baseline"/> within noise.
    /// This is the Phase 1 D4 confirmation that the JIT-elim mechanism scales to factory shape.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public long Tier2GateShape_Disabled()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            sum += Tier2FactoryShape_Off(i);
            sum += DoActualWork(i);
        }
        s_sink = sum;
        return sum;
    }

    /// <summary>
    /// Tier-2 gate-shape (enabled). Same pattern as <see cref="Tier2GateShape_Disabled"/> but the
    /// flag is true, so the inlined factory falls through to the NoInlining prologue stand-in,
    /// which records actual telemetry work. Represents the worst case for an active Tier-2 path.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public long Tier2GateShape_Enabled()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            sum += Tier2FactoryShape_On(i);
            sum += DoActualWork(i);
        }
        s_sink = sum;
        return sum;
    }

    /// <summary>
    /// Regular static bool check (disabled).
    /// JIT cannot eliminate this because the value could change at any time.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public long Static_Disabled()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            if (s_staticDisabled)
            {
                RecordTelemetryOperation((byte)i);
            }
            sum += DoActualWork(i);
        }
        s_sink = sum;
        return sum;
    }

    /// <summary>
    /// Instance field bool check (disabled).
    /// Similar to static bool but requires memory access to 'this'.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public long Instance_Disabled()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            if (_instanceDisabled)
            {
                RecordTelemetryOperation((byte)i);
            }
            sum += DoActualWork(i);
        }
        s_sink = sum;
        return sum;
    }

    /// <summary>
    /// Delegate that returns false.
    /// Incurs delegate invocation overhead even when returning false.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public long Delegate_ReturnsFalse()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            if (s_delegateReturnsFalse())
            {
                RecordTelemetryOperation((byte)i);
            }
            sum += DoActualWork(i);
        }
        s_sink = sum;
        return sum;
    }

    /// <summary>
    /// Null delegate check pattern: if (delegate != null) delegate().
    /// Only a null check when disabled.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public long Delegate_NullCheck()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            if (s_delegateNull != null && s_delegateNull())
            {
                RecordTelemetryOperation((byte)i);
            }
            sum += DoActualWork(i);
        }
        s_sink = sum;
        return sum;
    }

    /// <summary>
    /// Interface with null check pattern.
    /// When null, only incurs null check overhead.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public long Interface_NullCheck()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            s_interfaceNull?.RecordOperation((byte)i);
            sum += DoActualWork(i);
        }
        s_sink = sum;
        return sum;
    }

    /// <summary>
    /// Interface with NoOp implementation.
    /// Incurs virtual dispatch overhead even though the implementation does nothing.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public long Interface_NoOp()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            s_interfaceNoOp.RecordOperation((byte)i);
            sum += DoActualWork(i);
        }
        s_sink = sum;
        return sum;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // BENCHMARKS - WITH TELEMETRY ENABLED (measures full telemetry overhead)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Static readonly bool check with telemetry enabled - measures full telemetry overhead.
    /// This represents the worst case: telemetry is always on.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public long StaticReadonly_Enabled()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            if (s_staticReadonlyEnabled)
            {
                RecordTelemetryOperation((byte)i);
            }
            sum += DoActualWork(i);
        }
        s_sink = sum;
        return sum;
    }

    /// <summary>
    /// Regular static bool check with telemetry enabled - measures full overhead.
    /// Includes both the check overhead and telemetry work.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public long Static_Enabled()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            if (s_staticEnabled)
            {
                RecordTelemetryOperation((byte)i);
            }
            sum += DoActualWork(i);
        }
        s_sink = sum;
        return sum;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // HELPER METHODS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Simulates actual work that would be done regardless of telemetry.
    /// Uses NoInlining to prevent JIT from optimizing across the telemetry check.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long DoActualWork(int value)
    {
        // Simulate a simple operation - just enough to have measurable work
        return value * 17L + 31;
    }

    /// <summary>
    /// Simulates recording a telemetry operation, similar to what AccessControl does.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RecordTelemetryOperation(byte type)
    {
        var op = new TelemetryOperation
        {
            Type = type,
            LockData = 0x1234_5678_9ABC_DEF0
        };
        op.RecordNow();
        s_opSink = op; // Prevent DCE
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TIER-2 FACTORY-SHAPE STAND-INS (Phase 1)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mimics a Tier-2-gated <c>BeginFooEvent</c> factory with the gate flag disabled.
    /// AggressiveInlining ensures the call site sees the gate as a constant; JIT folds the body to <c>ret 0</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Tier2FactoryShape_Off(int i)
    {
        if (!s_tier2Disabled)
        {
            return 0;
        }
        return SimulatedPrologue(i);
    }

    /// <summary>
    /// Mimics a Tier-2-gated <c>BeginFooEvent</c> factory with the gate flag enabled.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Tier2FactoryShape_On(int i)
    {
        if (!s_tier2Enabled)
        {
            return 0;
        }
        return SimulatedPrologue(i);
    }

    /// <summary>
    /// NoInlining stand-in for the eventual <c>BeginPrologue</c> call — represents the work that runs
    /// on the hot path when the Tier-2 gate is open. Identical structure to <see cref="DoActualWork"/>
    /// + a <see cref="RecordTelemetryOperation"/> call.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long SimulatedPrologue(int i)
    {
        RecordTelemetryOperation((byte)i);
        return i * 17L + 31;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // INTERFACE FOR NULL-CHECK PATTERN
    // ═══════════════════════════════════════════════════════════════════════════

    private interface ITelemetryRecorder
    {
        void RecordOperation(byte type);
    }

    private sealed class NoOpTelemetryRecorder : ITelemetryRecorder
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void RecordOperation(byte type)
        {
            // Intentionally empty - NoOp implementation
        }
    }
}

/// <summary>
/// Additional benchmark focusing on nested/compound checks common in real telemetry code.
/// Tests patterns like: if (GlobalEnabled &amp;&amp; ComponentEnabled)
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
[BenchmarkCategory("Telemetry")]
public class TelemetryCompoundCheckBenchmark
{
    private const int Iterations = 100_000;

    // Simulates TelemetryConfig pattern: global master switch + component-specific switch
    private static readonly bool s_masterEnabled = false;
    private static readonly bool s_componentEnabled = true;
    private static readonly bool s_combinedFlag = false; // s_masterEnabled && s_componentEnabled

    private static bool s_dynamicMasterEnabled = false;
    private static bool s_dynamicComponentEnabled = true;

    private static long s_sink;

    /// <summary>
    /// Baseline with no checks.
    /// </summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = Iterations)]
    public long NoCheck()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            sum += DoWork(i);
        }
        s_sink = sum;
        return sum;
    }

    /// <summary>
    /// Two static readonly checks combined with &amp;&amp;.
    /// JIT should eliminate both checks in Tier 1.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public long StaticReadonly_TwoChecks()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            if (s_masterEnabled && s_componentEnabled)
            {
                DoTelemetry(i);
            }
            sum += DoWork(i);
        }
        s_sink = sum;
        return sum;
    }

    /// <summary>
    /// Pre-computed combined flag (like TelemetryConfig.ProfilerActive).
    /// Single static readonly check.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public long StaticReadonly_CombinedFlag()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            if (s_combinedFlag)
            {
                DoTelemetry(i);
            }
            sum += DoWork(i);
        }
        s_sink = sum;
        return sum;
    }

    /// <summary>
    /// Two dynamic (non-readonly) checks combined.
    /// JIT cannot eliminate these.
    /// </summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public long Static_TwoChecks()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++)
        {
            if (s_dynamicMasterEnabled && s_dynamicComponentEnabled)
            {
                DoTelemetry(i);
            }
            sum += DoWork(i);
        }
        s_sink = sum;
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long DoWork(int i) => i * 17L + 31;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DoTelemetry(int i) => s_sink += i;
}

/// <summary>
/// Micro-benchmark focusing purely on the check overhead without any other work.
/// This isolates the cost of the branch itself.
/// </summary>
[SimpleJob(warmupCount: 5, iterationCount: 15)]
[BenchmarkCategory("Telemetry")]
public class TelemetryCheckOnlyBenchmark
{
    private const int Iterations = 1_000_000;

    private static readonly bool s_readonlyFalse = false;
    private static readonly bool s_readonlyTrue = true;
    private static bool s_dynamicFalse = false;
    private static bool s_dynamicTrue = true;
    private bool _instanceFalse = false;

    private static long s_counter;

    [Benchmark(Baseline = true, OperationsPerInvoke = Iterations)]
    public void NoCheck()
    {
        for (int i = 0; i < Iterations; i++)
        {
            IncrementCounter();
        }
    }

    [Benchmark(OperationsPerInvoke = Iterations)]
    public void StaticReadonly_False()
    {
        for (int i = 0; i < Iterations; i++)
        {
            if (s_readonlyFalse) IncrementCounterSlow();
            IncrementCounter();
        }
    }

    [Benchmark(OperationsPerInvoke = Iterations)]
    public void StaticReadonly_True()
    {
        for (int i = 0; i < Iterations; i++)
        {
            if (s_readonlyTrue) IncrementCounterSlow();
            IncrementCounter();
        }
    }

    [Benchmark(OperationsPerInvoke = Iterations)]
    public void Static_False()
    {
        for (int i = 0; i < Iterations; i++)
        {
            if (s_dynamicFalse) IncrementCounterSlow();
            IncrementCounter();
        }
    }

    [Benchmark(OperationsPerInvoke = Iterations)]
    public void Static_True()
    {
        for (int i = 0; i < Iterations; i++)
        {
            if (s_dynamicTrue) IncrementCounterSlow();
            IncrementCounter();
        }
    }

    [Benchmark(OperationsPerInvoke = Iterations)]
    public void Instance_False()
    {
        for (int i = 0; i < Iterations; i++)
        {
            if (_instanceFalse) IncrementCounterSlow();
            IncrementCounter();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IncrementCounter() => s_counter++;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IncrementCounterSlow()
    {
        s_counter += 100;
    }
}
