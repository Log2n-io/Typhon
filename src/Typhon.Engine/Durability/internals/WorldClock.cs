using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The world clock of one database session (#1495, TICK-01): one past the last world tick completed. Raised on the tick driver, read by every WAL append (the
/// value rides each commit and fence record) and by the checkpoint. Never lowered.
/// </summary>
internal sealed class WorldClock
{
    private long _next;

    /// <summary>A clock starting at <paramref name="next"/>: the world tick the session's tick 0 is.</summary>
    internal WorldClock(long next) => _next = next;

    /// <summary>One past the last world tick completed, by acquire.</summary>
    internal long Next => Volatile.Read(ref _next);

    /// <summary>
    /// Raises the clock to <paramref name="next"/> when it is ahead; never lowers it. A CAS loop, so two raisers — a host's WriteTickFence beside a runtime,
    /// outside the contract but possible — cannot lower it. Once a tick: the cost is nothing.
    /// </summary>
    internal void Raise(long next)
    {
        var current = Volatile.Read(ref _next);
        while (next > current)
        {
            var seen = Interlocked.CompareExchange(ref _next, next, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
