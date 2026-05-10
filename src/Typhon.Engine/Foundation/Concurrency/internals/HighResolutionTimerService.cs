using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace Typhon.Engine.Internals;

/// <summary>
/// High-resolution timer for a single periodic callback. Runs on its own dedicated thread. Use this for safety-critical or latency-sensitive
/// handlers that must not be affected by other callbacks.
/// </summary>
/// <example>
/// <code>
/// var watchdog = new HighResolutionTimerService(
///     "DeadlineWatchdog",
///     intervalTicks: Stopwatch.Frequency / 1000,  // 1ms
///     callback: (scheduled, actual) => DeadlineWatchdog.CheckExpired(),
///     parent: resourceRegistry.TimerDedicated);
/// watchdog.Start();
/// // ...
/// watchdog.Dispose();
/// </code>
/// </example>
[PublicAPI]
internal sealed class HighResolutionTimerService : HighResolutionTimerServiceBase
{
    private readonly string _name;
    private readonly long _intervalTicks;
    private readonly Action<long, long> _callback;
    private readonly ILogger _logger;

    private long _nextTick;
    private long _invocationCount;
    private long _lastCallbackDurationTicks;
    private long _maxCallbackDurationTicks;

    /// <summary>
    /// Creates a dedicated high-resolution timer for a single handler. The thread does not start
    /// until <see cref="HighResolutionTimerServiceBase.Start"/> is called.
    /// </summary>
    /// <param name="name">
    /// Human-readable name for diagnostics. Used as the thread name (prefixed with "Typhon.HRT.") and in telemetry.
    /// </param>
    /// <param name="intervalTicks">
    /// Period between invocations, expressed in <see cref="Stopwatch"/> ticks. Use <c>Stopwatch.Frequency / 1000</c> for 1ms.
    /// </param>
    /// <param name="callback">
    /// The action to invoke each tick. Receives the scheduled and actual <see cref="Stopwatch"/> timestamps. Must be fast (target: &lt;100µs).
    /// </param>
    /// <param name="parent">
    /// Parent resource node. Should be <c>registry.TimerDedicated</c>.
    /// </param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public HighResolutionTimerService(string name, long intervalTicks, Action<long, long> callback, IResource parent, ILogger logger = null) : 
        base(name, parent)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(intervalTicks, 0L);

        _name = name;
        _intervalTicks = intervalTicks;
        _callback = callback;
        _logger = logger;

        // First tick is set when Start() is called (via GetNextTick)
        _nextTick = long.MaxValue;
    }

    /// <summary>Configured interval in <see cref="Stopwatch"/> ticks.</summary>
    public long IntervalTicks => _intervalTicks;

    /// <summary>Configured interval as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Interval => TimeSpan.FromSeconds((double)_intervalTicks / Stopwatch.Frequency);

    /// <summary>Number of times the callback has been invoked.</summary>
    public long InvocationCount => _invocationCount;

    /// <summary>Duration of the last callback invocation.</summary>
    public TimeSpan LastCallbackDuration =>
        TimeSpan.FromSeconds((double)_lastCallbackDurationTicks / Stopwatch.Frequency);

    /// <summary>Maximum callback invocation duration observed.</summary>
    public TimeSpan MaxCallbackDuration =>
        TimeSpan.FromSeconds((double)_maxCallbackDurationTicks / Stopwatch.Frequency);

    /// <inheritdoc />
    protected override string ThreadName => $"Typhon.HRT.{_name}";

    /// <inheritdoc />
    protected override long GetNextTick()
    {
        // On first call after Start(), initialize the metronome anchor
        if (_nextTick == long.MaxValue)
        {
            _nextTick = Stopwatch.GetTimestamp() + _intervalTicks;
        }

        return _nextTick;
    }

    /// <inheritdoc />
    protected override void ExecuteCallbacks(long scheduledTick, long actualTick)
    {
        // Advance metronome (drift-free)
        _nextTick += _intervalTicks;

        // Skip forward if we fell behind
        if (actualTick > _nextTick + _intervalTicks)
        {
            var missed = (actualTick - _nextTick) / _intervalTicks;
            _nextTick += missed * _intervalTicks;
            AddMissedTicks(missed);
        }

        // Execute the single handler
        var callbackStart = Stopwatch.GetTimestamp();
        try
        {
            _callback(scheduledTick, actualTick);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "HighResolutionTimer '{Name}' callback threw", _name);
        }

        var elapsed = Stopwatch.GetTimestamp() - callbackStart;
        _lastCallbackDurationTicks = elapsed;

        if (elapsed > _maxCallbackDurationTicks)
        {
            _maxCallbackDurationTicks = elapsed;
        }

        _invocationCount++;
    }
}
