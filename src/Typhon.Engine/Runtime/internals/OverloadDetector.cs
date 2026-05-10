using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Overload detection state machine. Called once per tick from <see cref="DagScheduler.ComputeAndRecordTelemetry"/>.
/// Single writer (timer thread) — no synchronization needed.
/// </summary>
internal sealed class OverloadDetector
{
    private static readonly int[] DefaultMultipliers = [1, 2, 3, 4, 6];

    private readonly OverloadOptions _options;
    private readonly int[] _allowedMultipliers;
    private readonly int _maxMultiplierIndex;

    private int _multiplierIndex; // Index into _allowedMultipliers
    private int _previousQueueDepth;

    /// <summary>Current overload level.</summary>
    public OverloadLevel CurrentLevel { get; private set; }

    /// <summary>Current tick rate multiplier (1 = normal, 2+ = modulated).</summary>
    public int TickMultiplier => _allowedMultipliers[_multiplierIndex];

    // ── Read-only accessors for gauge emission (issue #289 follow-up) ─────────────
    /// <summary>Consecutive ticks above the overrun threshold. Reset on any deescalation tick.</summary>
    public int ConsecutiveOverrunTicks { get; private set; }

    /// <summary>Consecutive ticks below the deescalation ratio. Reset on any escalation tick.</summary>
    public int ConsecutiveUnderrunTicks { get; private set; }

    /// <summary>Consecutive ticks where the event-queue depth grew. Reset when growth pauses.</summary>
    public int ConsecutiveQueueGrowthTicks { get; private set; }

    internal OverloadDetector(OverloadOptions options, int baseTickRate)
    {
        _options = options ?? new OverloadOptions();

        // Build allowed multipliers, capped by MinTickRateHz
        var maxMultiplier = Math.Max(1, baseTickRate / Math.Max(1, _options.MinTickRateHz));
        var count = 0;
        for (var i = 0; i < DefaultMultipliers.Length; i++)
        {
            if (DefaultMultipliers[i] <= maxMultiplier)
            {
                count++;
            }
        }

        _allowedMultipliers = new int[Math.Max(1, count)];
        var idx = 0;
        for (var i = 0; i < DefaultMultipliers.Length && idx < _allowedMultipliers.Length; i++)
        {
            if (DefaultMultipliers[i] <= maxMultiplier)
            {
                _allowedMultipliers[idx++] = DefaultMultipliers[i];
            }
        }

        _maxMultiplierIndex = _allowedMultipliers.Length - 1;
    }

    /// <summary>
    /// Update overload state based on this tick's overrun ratio and event queue depth.
    /// Called once per tick by the timer thread.
    /// </summary>
    /// <returns>True if the level changed this tick.</returns>
    public bool Update(float overrunRatio, int eventQueueDepth = 0)
    {
        var previousLevel = CurrentLevel;

        // Signal 1: Overrun ratio
        var overrunning = overrunRatio > _options.OverrunThreshold;

        // Signal 2: Queue depth growth (sustained backlog)
        var queueGrowing = false;
        if (_options.QueueGrowthTicks > 0 && eventQueueDepth > _previousQueueDepth && _previousQueueDepth >= 0)
        {
            ConsecutiveQueueGrowthTicks++;
            if (ConsecutiveQueueGrowthTicks >= _options.QueueGrowthTicks)
            {
                queueGrowing = true;
            }
        }
        else
        {
            ConsecutiveQueueGrowthTicks = 0;
        }

        _previousQueueDepth = eventQueueDepth;

        // Escalation: either signal triggers
        if (overrunning || queueGrowing)
        {
            ConsecutiveOverrunTicks++;
            ConsecutiveUnderrunTicks = 0;

            if (ConsecutiveOverrunTicks >= _options.EscalationTicks)
            {
                Escalate();
                ConsecutiveOverrunTicks = 0;
            }
        }
        else if (overrunRatio < _options.DeescalationRatio)
        {
            ConsecutiveUnderrunTicks++;
            ConsecutiveOverrunTicks = 0;

            if (ConsecutiveUnderrunTicks >= _options.DeescalationTicks)
            {
                Deescalate();
                ConsecutiveUnderrunTicks = 0;
            }
        }
        // Between thresholds: no action, counters preserved (hysteresis)

        return CurrentLevel != previousLevel;
    }

    private void Escalate()
    {
        if (CurrentLevel == OverloadLevel.TickRateModulation)
        {
            // Within Level 3: try increasing multiplier before escalating to Level 4
            if (_multiplierIndex < _maxMultiplierIndex)
            {
                _multiplierIndex++;
                return;
            }
        }

        if (CurrentLevel < OverloadLevel.PlayerShedding)
        {
            CurrentLevel = (OverloadLevel)((byte)CurrentLevel + 1);

            // Entering Level 3: start at multiplier index 1 (2x)
            if (CurrentLevel == OverloadLevel.TickRateModulation && _maxMultiplierIndex > 0)
            {
                _multiplierIndex = 1;
            }
        }
    }

    private void Deescalate()
    {
        if (CurrentLevel == OverloadLevel.TickRateModulation)
        {
            // Within Level 3: reduce multiplier before dropping to Level 2
            if (_multiplierIndex > 1)
            {
                _multiplierIndex--;
                return;
            }

            // Multiplier back to 1x — drop out of Level 3
            _multiplierIndex = 0;
        }

        if (CurrentLevel > OverloadLevel.Normal)
        {
            CurrentLevel = (OverloadLevel)((byte)CurrentLevel - 1);
        }
    }

    /// <summary>Force a specific overload level and multiplier. For testing only.</summary>
    internal void ForceLevel(OverloadLevel level, int multiplier = 1)
    {
        CurrentLevel = level;
        _multiplierIndex = 0;
        for (var i = 0; i < _allowedMultipliers.Length; i++)
        {
            if (_allowedMultipliers[i] == multiplier)
            {
                _multiplierIndex = i;
                break;
            }
        }

        ConsecutiveOverrunTicks = 0;
        ConsecutiveUnderrunTicks = 0;
    }
}
