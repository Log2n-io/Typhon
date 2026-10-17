using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Passively waits for in-flight IO to complete, making dirty pages evictable.
/// Signaled when a page is left with no mutator marks and no writeback debt: by <see cref="PagedMMF.MarkCaptured"/> after a checkpoint write, or by
/// a ChangeSet releasing a page's last mark.
/// </summary>
/// <remarks>
/// A generation counter, not a shared event (#1382). The event was waited on, then reset by each waiter it woke: a page made evictable between one
/// waiter's wake and its reset was swallowed for every waiter not yet back in its wait, which then slept out its whole round. A waiter now ends its round
/// at the first signal after the round began, whoever else woke. A signal pulses only when a waiter arrived since the last pulse — a pulse wakes every
/// waiter, and each re-reads the generation — so a settled batch, which signals once per page, takes the lock once per arrival, not once per page;
/// with no waiter a signal is one atomic increment, as the event's set was.
/// </remarks>
internal sealed class WaitForIOStrategy : IPageCacheBackpressureStrategy, IDisposable
{
    /// <summary>The longest wait of one round; the retry loop re-checks its deadline between rounds. Settable: tests lengthen it.</summary>
    internal int RoundMilliseconds = 50;

    /// <summary>Threads waiting in a round now (tests).</summary>
    internal int Waiters => Volatile.Read(ref _counters.Waiters);

    private readonly object _sync = new();
    private Counters _counters;

    /// <summary>Written by every signaler and every waiter: each on its own cache line.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Counters
    {
        [FieldOffset(64)] public long Generation;
        [FieldOffset(128)] public int Waiters;
        [FieldOffset(192)] public long Arrivals;   // waiters that began a round; PulsedArrivals the count the last pulse covered
        [FieldOffset(200)] public long PulsedArrivals;
    }

    public bool OnPressure(ref BackpressureContext ctx, int dirtyPageCount, int epochProtectedCount)
    {
        if (ctx.ShouldGiveUp)
        {
            return false;
        }

        ctx.RecordRetry();

        lock (_sync)
        {
            Interlocked.Increment(ref _counters.Waiters);
            try
            {
                var seen = Interlocked.Read(ref _counters.Generation);
                var deadline = Environment.TickCount64 + RoundMilliseconds;
                while (true)
                {
                    // An arrival before every wait, then the generation read again, both with full fences: a signal either sees this arrival and
                    // pulses (it takes the lock, so after the wait below has released it), or comes before the read and ends the round now. A waiter
                    // woken by a pulse whose signal it had already seen waits again as a new arrival, so the next signal pulses for it.
                    Interlocked.Increment(ref _counters.Arrivals);
                    if (Interlocked.Read(ref _counters.Generation) != seen)
                    {
                        break;
                    }

                    var left = deadline - Environment.TickCount64;
                    if (left <= 0)
                    {
                        break;
                    }

                    Monitor.Wait(_sync, (int)left);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _counters.Waiters);
            }
        }

        return true;
    }

    /// <summary>Called by DecrementDirty when a page becomes evictable.</summary>
    public void SignalPageAvailable()
    {
        Interlocked.Increment(ref _counters.Generation);
        if (Volatile.Read(ref _counters.Waiters) == 0 || Interlocked.Read(ref _counters.Arrivals) == Volatile.Read(ref _counters.PulsedArrivals))
        {
            return;   // nobody waits, or every waiter has been pulsed since it began its round
        }

        lock (_sync)
        {
            // Under the lock no waiter is between its arrival and its wait: every arrival counted here is waiting, and this pulse wakes it.
            Volatile.Write(ref _counters.PulsedArrivals, Interlocked.Read(ref _counters.Arrivals));
            Monitor.PulseAll(_sync);
        }
    }

    public void Dispose()
    {
    }
}
