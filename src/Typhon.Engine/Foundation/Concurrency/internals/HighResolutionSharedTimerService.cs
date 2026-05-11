using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// High-resolution shared timer that multiplexes multiple periodic callbacks on a single background thread. Each callback has its own
/// period (in <see cref="Stopwatch"/> ticks). The timer thread wakes up at the nearest next callback, avoiding wasted ticks.
/// </summary>
/// <remarks>
/// Use this for non-critical periodic tasks (telemetry, metrics, cleanup). For safety-critical handlers that need guaranteed isolation,
/// use <see cref="HighResolutionTimerService"/> instead.
/// <para/>
/// Callbacks run sequentially on the timer thread. A slow callback delays all subsequent callbacks in that cycle. Follow the callback
/// contract: target &lt;100µs execution, no blocking calls.
/// </remarks>
[PublicAPI]
internal sealed class HighResolutionSharedTimerService : HighResolutionTimerServiceBase
{
    private static readonly long SlowThresholdTicks = (long)(Stopwatch.Frequency * 0.000_100); // 100µs

    private readonly ILogger<HighResolutionSharedTimerService> _logger;

    // Copy-on-write array for lock-free iteration on the timer thread.// Mutations (Register/Dispose) take _registrationLock and swap the array.
    private TimerRegistration[] _registrations = [];
    private readonly Lock _registrationLock = new();

    /// <summary>
    /// Creates a shared timer service. The thread starts lazily on first registration. Self-registers under the <c>Timer</c> root node in the resource tree.
    /// </summary>
    /// <param name="parent">
    /// Parent resource node. Should be <c>registry.Timer</c> (the Timer root node).
    /// </param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public HighResolutionSharedTimerService(IResource parent, ILogger<HighResolutionSharedTimerService> logger = null)
        : base("SharedTimer", parent)
    {
        _logger = logger;
    }

    /// <summary>Number of currently active registrations.</summary>
    public int ActiveRegistrations
    {
        get
        {
            var regs = _registrations;
            var count = 0;
            for (var i = 0; i < regs.Length; i++)
            {
                if (regs[i].IsActive)
                {
                    count++;
                }
            }
            return count;
        }
    }

    /// <summary>
    /// Read-only snapshot of all active registrations for diagnostics.
    /// </summary>
    public IReadOnlyList<ITimerRegistration> Registrations
    {
        get
        {
            var regs = _registrations;
            var result = new List<ITimerRegistration>(regs.Length);
            for (var i = 0; i < regs.Length; i++)
            {
                if (regs[i].IsActive)
                {
                    result.Add(regs[i]);
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Register a periodic callback.
    /// </summary>
    /// <param name="name">Human-readable name for diagnostics and telemetry.</param>
    /// <param name="intervalTicks">
    /// Period between invocations, in <see cref="Stopwatch"/> ticks. Use <c>Stopwatch.Frequency / 1000</c> for 1ms, etc.
    /// </param>
    /// <param name="callback">
    /// Action to invoke. Receives the scheduled and actual <see cref="Stopwatch"/> timestamps. Must be fast (target: &lt;100µs).
    /// </param>
    /// <returns>A registration handle. Dispose it to unregister.</returns>
    public ITimerRegistration Register(string name, long intervalTicks, Action<long, long> callback)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(intervalTicks, 0L);

        var now = Stopwatch.GetTimestamp();
        var reg = new TimerRegistration
        {
            Name = name,
            IntervalTicks = intervalTicks,
            Callback = callback,
            NextFireTimestamp = now + intervalTicks,
            IsActive = true
        };

        lock (_registrationLock)
        {
            var current = _registrations;
            var next = new TimerRegistration[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = reg;
            _registrations = next;
        }

        Start(); // Lazy thread start (idempotent)

        _logger?.LogDebug("Registered shared timer callback '{Name}' every {Interval}ms", name, (double)intervalTicks / Stopwatch.Frequency * 1000.0);

        return reg;
    }

    /// <inheritdoc />
    protected override string ThreadName => "Typhon.HRT.Shared";

    /// <inheritdoc />
    protected override long GetNextTick()
    {
        var registrations = _registrations;

        if (registrations.Length == 0)
        {
            return long.MaxValue; // Idle signal → base class sleeps 100ms
        }

        var nearest = long.MaxValue;
        var hasInactive = false;

        for (var i = 0; i < registrations.Length; i++)
        {
            var reg = registrations[i];

            if (!reg.IsActive)
            {
                hasInactive = true;
                continue;
            }

            if (reg.NextFireTimestamp < nearest)
            {
                nearest = reg.NextFireTimestamp;
            }
        }

        // Lazy cleanup of disposed registrations
        if (hasInactive)
        {
            CleanupInactiveRegistrations();
        }

        return nearest;
    }

    /// <inheritdoc />
    protected override void ExecuteCallbacks(long scheduledTick, long actualTick)
    {
        var registrations = _registrations;

        for (var i = 0; i < registrations.Length; i++)
        {
            var reg = registrations[i];

            if (!reg.IsActive)
            {
                continue;
            }

            // Fire if this registration is due (its next-fire <= the scheduled tick)
            if (reg.NextFireTimestamp > scheduledTick)
            {
                continue;
            }

            // Advance metronome for this registration (drift-free)
            reg.NextFireTimestamp += reg.IntervalTicks;

            // If we fell behind, skip forward rather than burst-firing
            if (actualTick > reg.NextFireTimestamp + reg.IntervalTicks)
            {
                var missed = (actualTick - reg.NextFireTimestamp) / reg.IntervalTicks;
                reg.NextFireTimestamp += missed * reg.IntervalTicks;
                AddMissedTicks(missed);
            }

            // Execute the callback
            var callbackStart = Stopwatch.GetTimestamp();
            try
            {
                reg.Callback(scheduledTick, actualTick);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "SharedTimer callback '{Name}' threw", reg.Name);
            }

            var elapsed = Stopwatch.GetTimestamp() - callbackStart;
            reg.LastCallbackDurationTicks = elapsed;

            if (elapsed > reg.MaxCallbackDurationTicks)
            {
                reg.MaxCallbackDurationTicks = elapsed;
            }

            reg.InvocationCount++;

            if (elapsed > SlowThresholdTicks)
            {
                reg.SlowInvocationCount++;
            }
        }
    }

    private void CleanupInactiveRegistrations()
    {
        lock (_registrationLock)
        {
            var current = _registrations;
            var active = current.Where(r => r.IsActive).ToArray(); // Allocation OK: rare path

            if (active.Length != current.Length)
            {
                _registrations = active;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal registration data structure
    // ═══════════════════════════════════════════════════════════════

    private sealed class TimerRegistration : ITimerRegistration
    {
        public string Name { get; init; }
        public long IntervalTicks { get; init; }
        public Action<long, long> Callback { get; init; }

        // Scheduling state — only written from the timer thread
        public long NextFireTimestamp;

        // Per-callback metrics — written from timer thread, read from any thread
        public long InvocationCount;
        public long LastCallbackDurationTicks;
        public long MaxCallbackDurationTicks;
        public long SlowInvocationCount;
        public volatile bool IsActive;

        TimeSpan ITimerRegistration.Interval             => TimeSpan.FromSeconds((double)IntervalTicks / Stopwatch.Frequency);
        TimeSpan ITimerRegistration.LastCallbackDuration => TimeSpan.FromSeconds((double)LastCallbackDurationTicks / Stopwatch.Frequency);
        TimeSpan ITimerRegistration.MaxCallbackDuration  => TimeSpan.FromSeconds((double)MaxCallbackDurationTicks / Stopwatch.Frequency);

        long ITimerRegistration.InvocationCount => InvocationCount;
        long ITimerRegistration.SlowInvocationCount => SlowInvocationCount;
        bool ITimerRegistration.IsActive => IsActive;

        public void Dispose() => IsActive = false;
    }
}
