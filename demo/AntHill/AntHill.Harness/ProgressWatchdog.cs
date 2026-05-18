using System;

namespace AntHill.Harness;

/// <summary>
/// Wall-clock backstop that detects a run which has stopped making tick progress. The harness does not rely solely on the engine's own timeout — if that
/// machinery itself wedges, this still trips. The current time is passed in (millisecond clock) so the logic is deterministically testable without real sleeping.
/// </summary>
public sealed class ProgressWatchdog
{
    private readonly long _timeoutMs;
    private long _lastTick;
    private long _lastProgressMs;

    /// <summary>Creates a watchdog that trips after <paramref name="timeout"/> of no tick progress.</summary>
    public ProgressWatchdog(TimeSpan timeout, long nowMs)
    {
        _timeoutMs = (long)timeout.TotalMilliseconds;
        _lastProgressMs = nowMs;
        _lastTick = -1;
    }

    /// <summary>
    /// Records the current tick number at time <paramref name="nowMs"/>. Returns <c>false</c> once
    /// the run has made no tick progress for longer than the configured timeout (a hang).
    /// </summary>
    public bool Observe(long currentTick, long nowMs)
    {
        if (currentTick > _lastTick)
        {
            _lastTick = currentTick;
            _lastProgressMs = nowMs;
        }
        return nowMs - _lastProgressMs <= _timeoutMs;
    }
}
