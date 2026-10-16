using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// How the page cache keeps up with the work given to it (#1382). The engine does not fail the work for being slow: an allocation that waits on
/// back-pressure goes on waiting while pages are being served, and the state says so — so a workload too large for the cache and the disk it was given
/// is seen, not silently slowed. Reported in the log on every change, in the profiler's gauges and in the resource tree, and readable from
/// <see cref="DatabaseEngine.StorageHealth"/>.
/// </summary>
[PublicAPI]
public enum StorageHealthState
{
    /// <summary>No back-pressure: allocations find a slot at once.</summary>
    Healthy = 0,

    /// <summary>Back-pressure: some allocations wait for the checkpoint to write pages back, or for older transactions to let pages go.</summary>
    Pressured = 1,

    /// <summary>Back-pressure for over a second: the work is slowed for long. The cache or the disk is too small for the load, or the load too high.</summary>
    Overloaded = 2,

    /// <summary>
    /// Allocations wait and no page has been served for over a second: nothing is getting through. A wait that sees no page served for a whole
    /// back-pressure timeout gives up with <see cref="PageCacheBackpressureTimeoutException"/>.
    /// </summary>
    Starved = 3,
}

/// <summary>A change of <see cref="DatabaseEngine.StorageHealth"/>, raised by <see cref="DatabaseEngine.StorageHealthChanged"/>.</summary>
[PublicAPI]
public sealed class StorageHealthChangedEventArgs : EventArgs
{
    internal StorageHealthChangedEventArgs(StorageHealthState previous, StorageHealthState current)
    {
        Previous = previous;
        Current = current;
    }

    /// <summary>The state before the change.</summary>
    public StorageHealthState Previous { get; }

    /// <summary>The state now.</summary>
    public StorageHealthState Current { get; }
}
