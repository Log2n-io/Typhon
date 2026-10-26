using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// One session's figures for the operator's per-session record, as the emission collects them before deciding which to send.
/// </summary>
/// <remarks>
/// A struct in a fixed array rather than a row object per session: the selection runs once a second over every open session, and the whole point of selecting
/// is to not pay per session. <see cref="Bytes"/> and <see cref="Frames"/> are WINDOW deltas; the two cumulative counters are the session's own totals.
/// </remarks>
internal struct OperatorSessionRow
{
    /// <summary>The session's id — slot and generation, as the engine and the operator both name it.</summary>
    public ulong SessionId;

    /// <summary>Bytes published to this session over the window. The selection key.</summary>
    public long Bytes;

    /// <summary>Frames published to this session over the window — the denominator of its average payload.</summary>
    public long Frames;

    /// <summary>Frames published to this session since it opened.</summary>
    public long FramesProduced;

    /// <summary>Frames skipped for this session since it opened.</summary>
    public long FramesSkipped;

    /// <summary>The realm the client holds, or <c>0xFFFF</c> before its first <c>RESET</c>.</summary>
    public ushort RealmId;

    /// <summary>The engine's own "this client is struggling" level; zero is healthy.</summary>
    public int DegradeLevel;
}

/// <summary>
/// Keeps the N sessions that sent the most bytes in a window, out of a population the trace cannot afford to carry whole.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why selecting beats truncating.</b> The per-session emission is capped because its volume scales with the session count and not with the engine. The
/// first version of that cap kept whatever the session table's open list yielded first, which at 8 000 sessions is 64 arbitrary rows out of 8 000 — a sample
/// that cannot answer the one question the table is opened for, "which session is eating the bandwidth", and that made the panel's own "busiest first" caption
/// false. The walk already computes every session's windowed byte count on its way to advancing the session's mark, so the heaviest are selectable for the
/// cost of comparing a number that is already in hand.
/// </para>
/// <para>
/// <b>A min-heap of the survivors, not a sort of the population.</b> The whole population is never ordered: a candidate is compared against the smallest row
/// kept so far and dropped in one comparison when it does not beat it, which is the common case. Worst case is a push per session —
/// <c>8192 × log2(64) ≈ 49k</c> comparisons once per second, against an <c>Array.Sort</c> of 8 192 rows that the obvious implementation would have done.
/// </para>
/// <para>
/// <b>Ties go to the session seen first</b>, because <see cref="Offer"/> replaces only on a strict improvement. On an idle server where every row is zero that
/// makes the selection the old first-N behaviour exactly, which is the right answer when no session is heavier than another.
/// </para>
/// </remarks>
internal sealed class TopSessionRows
{
    private readonly OperatorSessionRow[] _rows;
    private int _count;
    private bool _sorted;

    /// <summary>Creates a selection that keeps at most <paramref name="capacity"/> rows.</summary>
    /// <param name="capacity">The row cap; at least one.</param>
    public TopSessionRows(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _rows = new OperatorSessionRow[capacity];
    }

    /// <summary>How many rows are held, never above the capacity.</summary>
    public int Count => _count;

    /// <summary>Empties the selection for a new window. Does not release the array — it is the point of the type.</summary>
    public void Clear()
    {
        _count = 0;
        _sorted = false;
    }

    /// <summary>Offers one session's row, keeping it only if it is among the heaviest seen since <see cref="Clear"/>.</summary>
    /// <param name="row">The candidate, keyed on <see cref="OperatorSessionRow.Bytes"/>.</param>
    public void Offer(in OperatorSessionRow row)
    {
        if (_sorted)
        {
            throw new InvalidOperationException(
                $"{nameof(TopSessionRows)}.{nameof(Heaviest)} has already ordered this window's rows, which leaves the heap's root no longer the smallest " +
                $"row kept — a row offered now would be selected against the wrong one. Call {nameof(Clear)} to start the next window.");
        }

        if (_count < _rows.Length)
        {
            // Fill first, sifting up so the smallest kept row is always at the root once the array is full.
            var at = _count++;
            _rows[at] = row;
            while (at > 0)
            {
                var parent = (at - 1) / 2;
                if (_rows[parent].Bytes <= _rows[at].Bytes)
                {
                    break;
                }

                (_rows[parent], _rows[at]) = (_rows[at], _rows[parent]);
                at = parent;
            }

            return;
        }

        // Full: the root is the smallest row kept, so one comparison rejects every candidate that does not beat it.
        if (row.Bytes <= _rows[0].Bytes)
        {
            return;
        }

        _rows[0] = row;
        SiftDown(0, _count);
    }

    /// <summary>
    /// The kept rows, heaviest first.
    /// </summary>
    /// <returns>The rows in descending <see cref="OperatorSessionRow.Bytes"/> order.</returns>
    /// <remarks>
    /// <b>Terminal for the window.</b> Ordering destroys the heap, so a later <see cref="Offer"/> would no longer be selecting against the smallest kept row.
    /// Calling it twice is allowed and sorts once; offering after it is a programming error and throws rather than quietly keeping the wrong rows.
    /// </remarks>
    public ReadOnlySpan<OperatorSessionRow> Heaviest()
    {
        if (!_sorted)
        {
            // Heapsort: pop the smallest to the tail, count times, which leaves the array descending. In place, no comparer, no allocation.
            for (var end = _count - 1; end > 0; end--)
            {
                (_rows[0], _rows[end]) = (_rows[end], _rows[0]);
                SiftDown(0, end);
            }

            _sorted = true;
        }

        return new ReadOnlySpan<OperatorSessionRow>(_rows, 0, _count);
    }

    private void SiftDown(int at, int length)
    {
        while (true)
        {
            var left = (at * 2) + 1;
            if (left >= length)
            {
                return;
            }

            var smallest = _rows[left].Bytes < _rows[at].Bytes ? left : at;
            var right = left + 1;
            if (right < length && _rows[right].Bytes < _rows[smallest].Bytes)
            {
                smallest = right;
            }

            if (smallest == at)
            {
                return;
            }

            (_rows[smallest], _rows[at]) = (_rows[at], _rows[smallest]);
            at = smallest;
        }
    }
}
