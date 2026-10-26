using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SwgTatooine.Server;

/// <summary>
/// A side's most recent ledger rows, read incrementally: <c>GET /swg/wallet</c> and <c>GET /swg/inventory</c> answer a character's last rows on every read,
/// and a client reads again on every row, so reading its whole history each time grew with the character's age.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first read of a character is its two side indexes, whole</b>; it remembers the newest rows and the ledger's last sequence it had seen. A later read
/// walks only the rows appended since, by the unique sequence index, and keeps the ones naming the character — the activity of the world between two reads,
/// not the character's life. The answer is the same rows either way.
/// </para>
/// <para>
/// <b>Why the sequence is a safe watermark</b>: each ledger's rows are appended by serial writers, each committing its rows and the head that numbers them
/// together, so a snapshot that holds sequence N holds every row below it. A snapshot older than what is remembered (two readers racing) reads in full and
/// leaves the memory alone. One per engine: a reopened world starts empty.
/// </para>
/// </remarks>
internal sealed class RecentRowCache<T>
    where T : unmanaged
{
    private readonly ConcurrentDictionary<long, (long Seen, T[] Rows)> _known = new();
    private readonly Func<T, long> _seq;
    private readonly Func<T, long> _from;
    private readonly Func<T, long> _to;
    private readonly int _keep;
    private readonly Comparison<T> _newestFirst;

    public RecentRowCache(Func<T, long> seq, Func<T, long> from, Func<T, long> to, int keep)
    {
        _seq = seq;
        _from = from;
        _to = to;
        _keep = keep;
        _newestFirst = (x, y) => _seq(y).CompareTo(_seq(x));
    }

    /// <summary>The <paramref name="key"/> side's newest rows, newest first, at most the count kept, as of <paramref name="tx"/>'s snapshot.</summary>
    /// <param name="lastSeq">The ledger's last sequence in this snapshot: its head's.</param>
    public List<T> Read(Transaction tx, IndexRef bySeq, IndexRef byFrom, IndexRef byTo, long key, long lastSeq)
    {
        var rows = new List<T>();
        if (_known.TryGetValue(key, out var known) && known.Seen <= lastSeq)
        {
            rows.AddRange(known.Rows);
            if (lastSeq > known.Seen)
            {
                using var newer = tx.EnumerateIndex<T, long>(bySeq, known.Seen + 1, lastSeq);
                while (newer.MoveNext())
                {
                    var r = newer.CurrentComponent;
                    if (_from(r) == key || _to(r) == key)
                    {
                        rows.Add(r);
                    }
                }
            }
        }
        else
        {
            Collect(tx, byFrom, key, rows);
            Collect(tx, byTo, key, rows);
        }

        rows.Sort(_newestFirst);
        if (rows.Count > _keep)
        {
            rows.RemoveRange(_keep, rows.Count - _keep);
        }

        if (known.Rows == null || known.Seen <= lastSeq)
        {
            _known[key] = (lastSeq, rows.ToArray());
        }

        return rows;
    }

    private void Collect(Transaction tx, IndexRef index, long key, List<T> into)
    {
        var start = into.Count;
        using var found = tx.EnumerateIndex<T, long>(index, key, key);
        while (found.MoveNext())
        {
            into.Add(found.CurrentComponent);
        }

        // Only the newest of this side can be among the newest of both.
        var mine = into.Count - start;
        if (mine > _keep)
        {
            into.Sort(start, mine, Comparer<T>.Create(_newestFirst));
            into.RemoveRange(start + _keep, mine - _keep);
        }
    }
}
