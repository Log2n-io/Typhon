using JetBrains.Annotations;
using System;
using System.Diagnostics;

namespace Typhon.Engine.Internals;

/// <summary>
/// Handle returned by <see cref="HighResolutionSharedTimerService.Register"/>. Dispose to unregister the callback from the shared timer.
/// </summary>
[PublicAPI]
internal interface ITimerRegistration : IDisposable
{
    /// <summary>Human-readable name for this registration.</summary>
    string Name { get; }

    /// <summary>Configured interval in <see cref="Stopwatch"/> ticks.</summary>
    long IntervalTicks { get; }

    /// <summary>Configured interval as a <see cref="TimeSpan"/>.</summary>
    TimeSpan Interval { get; }

    /// <summary>Number of times this callback has been invoked.</summary>
    long InvocationCount { get; }

    /// <summary>Duration of the last callback invocation.</summary>
    TimeSpan LastCallbackDuration { get; }

    /// <summary>Maximum invocation duration observed.</summary>
    TimeSpan MaxCallbackDuration { get; }

    /// <summary>Number of invocations that exceeded 100µs.</summary>
    long SlowInvocationCount { get; }

    /// <summary>Whether this registration is still active (not disposed).</summary>
    bool IsActive { get; }
}
