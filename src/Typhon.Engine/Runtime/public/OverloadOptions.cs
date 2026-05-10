using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Configuration for the overload detection and response system.
/// </summary>
[PublicAPI]
public sealed class OverloadOptions
{
    /// <summary>
    /// Overrun ratio above which a tick is considered "overrunning". Default: 1.2 (20% over target).
    /// </summary>
    public float OverrunThreshold { get; set; } = 1.2f;

    /// <summary>
    /// Completion ratio below which a tick is considered "recovering". Default: 0.6 (40% headroom).
    /// Must be less than <see cref="OverrunThreshold"/> to create hysteresis.
    /// </summary>
    public float DeescalationRatio { get; set; } = 0.6f;

    /// <summary>
    /// Number of consecutive overrun ticks required to escalate to the next level. Default: 5 (~80ms at 60Hz).
    /// </summary>
    public int EscalationTicks { get; set; } = 5;

    /// <summary>
    /// Number of consecutive under-run ticks required to de-escalate. Default: 20 (~320ms at 60Hz).
    /// Deliberately asymmetric with <see cref="EscalationTicks"/> to prevent oscillation.
    /// </summary>
    public int DeescalationTicks { get; set; } = 20;

    /// <summary>
    /// Hard floor for tick rate under modulation, in Hz. Default: 10 (100ms per tick).
    /// </summary>
    public int MinTickRateHz { get; set; } = 10;

    /// <summary>
    /// Consecutive ticks of growing event queue depth before it counts as an overload escalation signal.
    /// Default: 5. Set to 0 to disable queue depth monitoring.
    /// </summary>
    public int QueueGrowthTicks { get; set; } = 5;
}
