using System;
using System.Collections.Generic;

namespace SwgTatooine.Bots;

/// <summary>
/// Hands out and takes back the ACCOUNT indices a fleet's bots are named from, in contiguous runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own type because it is the fleet's one real allocator, and it has been wrong twice.</b> An account name is derived from
/// an index (<c>bot000123</c>), so two live bots holding one index are two sessions logging into one account — which the server
/// refuses with <c>AccountAlreadyPlaying</c>, kicking whichever arrived second. The two failures were an index handed out while it
/// was still in use, and an index taken back and then lost; the first kicks sessions, the second shrinks the dial's reach for the
/// rest of the run. Neither was visible from outside the swarm, which is why this is now a type with tests rather than three
/// fields and a lock.
/// </para>
/// <para>
/// <b>Runs, not a set, because a chunk must be contiguous.</b> The provisioner signs up a RANGE
/// (<c>BotProvisioner.EnsureRangeAsync</c>) and the opener names bots <c>first + k</c>, so an allocation has to be a run rather
/// than a handful of indices. Released runs are therefore kept ordered and coalesced: giving back an arbitrary permutation of 250
/// indices one at a time leaves one run of 250, so the next growth allocates a full chunk instead of 250 chunks of one.
/// </para>
/// <para>Thread-safe: the reconciler allocates and releases while handshakes release failed indices from their own tasks.</para>
/// </remarks>
public sealed class BotIndexPool
{
    private readonly object _gate = new();

    /// <summary>Released runs, ordered by index, coalesced, all below <see cref="HighWater"/>.</summary>
    private readonly List<(int From, int Count)> _free = [];

    private readonly int _capacity;
    private int _next;

    /// <summary>Builds a pool over <paramref name="capacity"/> indices.</summary>
    /// <param name="capacity">How many indices exist, which is the fleet's ceiling.</param>
    public BotIndexPool(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _capacity = capacity;
    }

    /// <summary>The high-water mark: every index at or above it has never been handed out.</summary>
    public int HighWater
    {
        get
        {
            lock (_gate)
            {
                return _next;
            }
        }
    }

    /// <summary>How many indices are held in released runs, below <see cref="HighWater"/>.</summary>
    public int FreeCount
    {
        get
        {
            lock (_gate)
            {
                var total = 0;
                foreach (var run in _free)
                {
                    total += run.Count;
                }

                return total;
            }
        }
    }

    /// <summary>How many separate runs the free list holds, which is how fragmented it is.</summary>
    /// <remarks>Exposed for the tests that assert coalescing: a pool that has taken a whole chunk back should hold ONE run.</remarks>
    public int FreeRuns
    {
        get
        {
            lock (_gate)
            {
                return _free.Count;
            }
        }
    }

    /// <summary>Indices never handed out and not currently held: what a growth could still reach.</summary>
    public int Available => FreeCount + Math.Max(0, _capacity - HighWater);

    /// <summary>Takes a contiguous run of up to <paramref name="want"/> indices.</summary>
    /// <param name="want">How many the caller would like.</param>
    /// <param name="allocated">How many it got, which may be fewer, and is zero when the pool is exhausted.</param>
    /// <returns>The first index of the run.</returns>
    /// <remarks>
    /// <b>The LONGEST free run, not the lowest.</b> Taking the lowest would hand back a one-index run while a run of 250 sat behind
    /// it, and the chunk size is what the whole pipeline is paced by.
    /// </remarks>
    public int Allocate(int want, out int allocated)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(want);

        lock (_gate)
        {
            if (_free.Count > 0)
            {
                var best = 0;
                for (var i = 1; i < _free.Count; i++)
                {
                    if (_free[i].Count > _free[best].Count)
                    {
                        best = i;
                    }
                }

                var run = _free[best];
                allocated = Math.Min(want, run.Count);
                if (allocated == run.Count)
                {
                    _free.RemoveAt(best);
                }
                else
                {
                    _free[best] = (run.From + allocated, run.Count - allocated);
                }

                return run.From;
            }

            allocated = Math.Min(want, _capacity - _next);
            var first = _next;
            _next += allocated;
            return first;
        }
    }

    /// <summary>Gives a run of indices back.</summary>
    /// <param name="from">The first index.</param>
    /// <param name="count">How many, from <paramref name="from"/> upward.</param>
    /// <remarks>
    /// Safe to call one index at a time, which is what a shrink does: the slots a shrink retires are not a contiguous run of
    /// indices, so releasing <c>(min(index), count)</c> as one block would free indices that are still in use.
    /// </remarks>
    public void Release(int from, int count)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_gate)
        {
            // Walking the high-water mark back beats remembering a run, when the run IS the tail.
            if (from + count == _next)
            {
                _next = from;
                AbsorbTail();
                return;
            }

            var at = 0;
            while (at < _free.Count && _free[at].From < from)
            {
                at++;
            }

            _free.Insert(at, (from, count));

            // Merge with the run after, then the one before, so the list holds maximal runs and never two that touch.
            if (at + 1 < _free.Count && _free[at].From + _free[at].Count == _free[at + 1].From)
            {
                _free[at] = (_free[at].From, _free[at].Count + _free[at + 1].Count);
                _free.RemoveAt(at + 1);
            }

            if (at > 0 && _free[at - 1].From + _free[at - 1].Count == _free[at].From)
            {
                _free[at - 1] = (_free[at - 1].From, _free[at - 1].Count + _free[at].Count);
                _free.RemoveAt(at);
            }

            AbsorbTail();
        }
    }

    /// <summary>Pulls the high-water mark down over any free run that now ends at it.</summary>
    /// <remarks>
    /// Called with the lock held. It matters because the tail is often freed LAST: releasing 500..749 and then 250..499 should leave
    /// the mark at 250 and an empty list, not one run and a mark that never moved.
    /// </remarks>
    private void AbsorbTail()
    {
        while (_free.Count > 0)
        {
            var last = _free[^1];
            if (last.From + last.Count != _next)
            {
                return;
            }

            _next = last.From;
            _free.RemoveAt(_free.Count - 1);
        }
    }
}
