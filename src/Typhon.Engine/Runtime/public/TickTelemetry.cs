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

    /// <summary>Wall-clock duration of the subscription Output phase, in milliseconds. Zero if no subscriptions.</summary>
    public float OutputPhaseMs;

    /// <summary>Total entity deltas pushed to all clients this tick (Added + Modified + Removed across all Views).</summary>
    public int SubscriptionDeltasPushed;

    /// <summary>Number of send buffer overflows this tick (clients that missed deltas and need resync).</summary>
    public int SubscriptionOverflowCount;

    /// <summary>Current overload response level for this tick.</summary>
    public OverloadLevel CurrentLevel;

    /// <summary>Tick rate multiplier (1 = normal, 2+ = modulated under Level 3).</summary>
    public int TickMultiplier;

    /// <summary>Total entities deferred (budget-capped) across all systems this tick. Zero until Level 2 enforcement.</summary>
    public int TotalEntitiesDeferred;

    /// <summary>Total pending events across all event queues at tick end. Sustained growth indicates backlog.</summary>
    public int EventQueueDepth;
}
