using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Monitors registered deadlines and fires <see cref="CancellationToken"/> on expiry. Uses a <see cref="HighResolutionSharedTimerService"/> registration
/// at 200Hz (5ms) to scan a priority queue. No dedicated thread — delegates thread management to the shared timer infrastructure.
/// </summary>
/// <remarks>
/// <para>Registered as a singleton in DI under <c>DataEngine</c> in the resource tree. Lifecycle is managed by the DI container — no manual
/// Initialize/Shutdown required.</para>
/// <para>The timer registration is lazy: the 200Hz callback is only registered with the shared timer on the first <see cref="Register"/> call.
/// If no deadlines are ever registered, no timer overhead is incurred.</para>
/// </remarks>
internal class DeadlineWatchdog : ResourceNode
{
    // ═══════════════════════════════════════════════════════════════
    // Types
    // ═══════════════════════════════════════════════════════════════

    private readonly record struct WatchedDeadline(Deadline Deadline, CancellationTokenSource Cts);

    // ═══════════════════════════════════════════════════════════════
    // Constants
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Watchdog check interval: 200Hz = 5ms.</summary>
    private static readonly long CheckIntervalTicks = Stopwatch.Frequency / 200;

    // ═══════════════════════════════════════════════════════════════
    // State
    // ═══════════════════════════════════════════════════════════════

    private readonly Lock _lock = new();
    private readonly PriorityQueue<WatchedDeadline, long> _queue = new();
    private readonly HighResolutionSharedTimerService _sharedTimer;
    private ITimerRegistration _timerRegistration;
    private bool _disposed;

    // ═══════════════════════════════════════════════════════════════
    // Construction
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a new DeadlineWatchdog. Registers under <c>DataEngine</c> in the resource tree.
    /// </summary>
    /// <param name="resourceRegistry">Resource registry for tree placement.</param>
    /// <param name="sharedTimer">Shared timer for 200Hz callback registration.</param>
    public DeadlineWatchdog(IResourceRegistry resourceRegistry, HighResolutionSharedTimerService sharedTimer)
        : base("DeadlineWatchdog", ResourceType.Service, resourceRegistry.DataEngine)
    {
        ArgumentNullException.ThrowIfNull(sharedTimer);
        _sharedTimer = sharedTimer;
    }

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Register a deadline for monitoring. Returns a <see cref="CancellationToken"/> that will be canceled when the deadline expires (within ~5ms).
    /// </summary>
    /// <param name="deadline">The deadline to monitor.</param>
    /// <returns>
    /// A token that cancels on deadline expiry. <see cref="CancellationToken.None"/> if <paramref name="deadline"/> is infinite.
    /// An already-cancelled token if <paramref name="deadline"/> is already expired.
    /// </returns>
    public CancellationToken Register(Deadline deadline)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Short-circuit: infinite deadline — no monitoring needed
        if (deadline.IsInfinite)
        {
            return CancellationToken.None;
        }

        // Short-circuit: already expired — return pre-cancelled token
        if (deadline.IsExpired)
        {
            return new CancellationToken(true);
        }

        var cts = new CancellationTokenSource();

        lock (_lock)
        {
            _queue.Enqueue(new WatchedDeadline(deadline, cts), deadline.Ticks);
        }

        EnsureTimerRegistered();

        return cts.Token;
    }

    // ═══════════════════════════════════════════════════════════════
    // Disposal
    // ═══════════════════════════════════════════════════════════════

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            lock (_lock)
            {
                // Unregister from shared timer
                _timerRegistration?.Dispose();
                _timerRegistration = null;

                // Cancel all remaining registered deadlines
                while (_queue.TryDequeue(out var entry, out _))
                {
                    try
                    {
                        entry.Cts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // CTS was disposed externally — ignore
                    }
                }
            }
        }

        base.Dispose(disposing);
    }

    // ═══════════════════════════════════════════════════════════════
    // Private — Timer Registration (Lazy)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Ensures the watchdog is registered with the shared timer. Called lazily on first <see cref="Register"/> call.
    /// </summary>
    private void EnsureTimerRegistered()
    {
        if (_timerRegistration != null)
        {
            return;
        }

        lock (_lock)
        {
            if (_timerRegistration != null)
            {
                return;
            }

            _timerRegistration = _sharedTimer.Register(
                "DeadlineWatchdog",
                CheckIntervalTicks,
                CheckExpiredDeadlines);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Private — Timer Callback
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Timer callback invoked at 200Hz (every 5ms) by the shared timer. Scans the priority queue and fires CancellationToken for any expired deadlines.
    /// Must complete in &lt;100µs to respect the shared timer callback contract.
    /// </summary>
    private void CheckExpiredDeadlines(long scheduledTick, long actualTick)
    {
        lock (_lock)
        {
            while (_queue.TryPeek(out var entry, out _))
            {
                // Clean up externally cancelled entries
                if (entry.Cts.IsCancellationRequested)
                {
                    _queue.Dequeue();
                    continue;
                }

                if (!entry.Deadline.IsExpired)
                {
                    break; // Queue is ordered — no more expired entries
                }

                _queue.Dequeue();

                try
                {
                    entry.Cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // CTS was disposed externally — ignore
                }
            }
        }
    }
}
