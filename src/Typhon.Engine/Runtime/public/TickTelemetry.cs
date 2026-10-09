using JetBrains.Annotations;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// Per-tick telemetry snapshot. Recorded at the end of each tick into the <see cref="TickTelemetryRing"/>.
/// </summary>
[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public struct TickTelemetry
{
    /// <summary>Monotonically increasing tick number (0-based).</summary>
    public long TickNumber;

    /// <summary>Target tick duration based on <see cref="RuntimeOptions.BaseTickRate"/> (e.g., 16.67ms at 60Hz).</summary>
    public float TargetDurationMs;

    /// <summary>Actual wall-clock tick execution time (reset → completion), in milliseconds.</summary>
    public float ActualDurationMs;

    /// <summary>
    /// Ratio of actual to target duration. Values &gt; 1.0 indicate overrun.
    /// Used by overload management (#201) to detect sustained overruns.
    /// </summary>
    public float OverrunRatio;

    /// <summary>
    /// Actual tick-to-tick interval in milliseconds (time from previous tick start to this tick start).
    /// This is the true period seen by the simulation. Compare against <see cref="TargetDurationMs"/> to measure jitter: <c>|TickIntervalMs - TargetDurationMs|</c>.
    /// Zero for the first tick.
    /// </summary>
    public float TickIntervalMs;

    /// <summary>Number of worker threads active during this tick.</summary>
    public int ActiveWorkerCount;

    /// <summary>Number of systems that actually executed (not skipped) this tick.</summary>
    public int ActiveSystemCount;

    /// <summary>Total entities processed across all systems this tick.</summary>
    public int TotalEntitiesProcessed;

    /// <summary>Current overload response level for this tick.</summary>
    public OverloadLevel CurrentLevel;

    /// <summary>Tick rate multiplier (1 = normal, 2+ = modulated under Level 3).</summary>
    public int TickMultiplier;

    /// <summary>Total entities deferred (budget-capped) across all systems this tick. Zero until Level 2 enforcement.</summary>
    public int TotalEntitiesDeferred;

    /// <summary>Total pending events across all event queues at tick end. Sustained growth indicates backlog.</summary>
    public int EventQueueDepth;

    /// <summary>
    /// Wall-clock time this tick spent in the Unit-of-Work flush, in milliseconds — which in WAL mode is
    /// <c>WalManager.RequestFlush</c> followed by <c>WaitForDurable</c>, i.e. the tick's DURABILITY WAIT (#CLI-04).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A wall-clock span, not CPU, and not a per-commit figure.</b> The tick driver blocks here until the WAL writer has
    /// published the tick's records, so this is the latency a tick pays for durability. The per-COMMIT wait is a different
    /// population: it is measured inside <c>WalWriter.WaitForDurableSlow</c>, has no always-on accumulator, and its fast
    /// path (already durable) returns without touching a timer at all. A consumer reporting this as
    /// <c>typhon.durability.wait.p99</c> must say which of the two it means.
    /// </para>
    /// <para>
    /// <b>Why here rather than on a profiler span.</b> The flush is already wrapped in a <c>TickPhase.UowFlush</c> span, but
    /// a span exists only while the profiler is recording: with the profiler off the wrapper folds away and the number does
    /// not exist, which is exactly why the metric emitted a hard zero before this field. The ring is single-writer from the
    /// tick driver and stamped at tick end, so carrying it here costs one <c>Stopwatch</c> pair per tick — on a path that
    /// just waited on an fsync — and no synchronisation at all.
    /// </para>
    /// <para>
    /// Zero is a real reading: a tick whose records were already durable, or one that committed nothing, genuinely waited
    /// for nothing. It is not "unknown". (It is also zero for a tick that did not reach its flush at all — see the clear at
    /// the top of the scheduler's tick driver, which is what keeps a failed flush from donating its wait to the next tick.)
    /// </para>
    /// </remarks>
    public float UowFlushMs;

    /// <summary>
    /// Lost wakes caught this tick: a parked worker's between-tick backstop (50 ms) fired after a dispatch whose Set never reached it. Non-zero means the
    /// wake protocol is broken; zero does not prove it is not, since a lost wake that the next dispatch's Set rescues before the backstop leaves nothing to
    /// count. Counted when caught, so a wake lost late in one tick lands in the next. Cumulative: <see cref="DagScheduler.LostWakeCount"/>.
    /// </summary>
    public int LostWakes;
}
