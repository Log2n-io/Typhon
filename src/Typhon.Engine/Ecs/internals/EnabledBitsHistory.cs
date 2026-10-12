using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-entity history of EnabledBits changes for MVCC snapshot isolation (family 5, VIS-10). Each change is a (stamp, old bits, new bits) entry; a change
/// still committing holds its owner's TSN with the pending bit instead of a stamp, from its PREPARE until its publish stamps it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The value at a snapshot.</b> The new bits of the highest-stamp change at or below it; with none, the old bits of the lowest-stamp change above it;
/// with neither, the record's inline bits. A pending change counts by its owner's commit card: registered with a stamp, it is that stamp; no card holding the
/// owner, it is not committed — unless it was stamped since, which a re-read of the history tells. Ordered by stamp, not by when the changes were recorded:
/// two changes after a snapshot used to answer with the newest's old bits, the bits the entity had only between the two (#1248).
/// </para>
/// <para>
/// <b>Copy-on-write.</b> The entries are an immutable array a writer replaces under the lock, so a reader iterates a consistent array with no lock (#1248:
/// the <c>List</c> was written at commit while readers walked it).
/// </para>
/// <para>Entries are pruned once MinTSN is past their stamp.</para>
/// </remarks>
internal sealed class EnabledBitsHistory
{
    internal readonly record struct Change(long Stamp, ushort OldBits, ushort NewBits);

    private Change[] _entries = [];
    private readonly Lock _writeLock = new();
    private bool _retired;

    /// <summary>Resolve the EnabledBits visible at <paramref name="txTsn"/>; <paramref name="currentBits"/> is the record's inline (latest published) value.</summary>
    public ushort ResolveAt(long txTsn, ushort currentBits, CommitCardTable cards)
    {
        var found = TryResolve(Volatile.Read(ref _entries), txTsn, cards, out var bits, out var stale);
        if (stale)
        {
            // A pending change with no card: not committing, or stamped since — the history read again says which.
            found = TryResolve(Volatile.Read(ref _entries), txTsn, cards, out bits, out _);
        }

        return found ? bits : currentBits;
    }

    private static bool TryResolve(Change[] entries, long txTsn, CommitCardTable cards, out ushort bits, out bool stale)
    {
        stale = false;
        long below = long.MinValue, above = long.MaxValue;
        ushort belowBits = 0, aboveBits = 0;
        foreach (var e in entries)
        {
            var s = e.Stamp;
            if ((s & EntityRecordHeader.PendingBit) != 0)
            {
                var owner = s & ~EntityRecordHeader.PendingBit;
                if (owner == txTsn)
                {
                    s = txTsn;
                }
                else if (cards == null || !cards.TryGetStamp(owner, out s))
                {
                    stale = true;   // not committing — or stamped since this array was read
                    continue;
                }
            }

            if (s <= txTsn)
            {
                if (s > below)
                {
                    below = s;
                    belowBits = e.NewBits;
                }
            }
            else if (s < above)
            {
                above = s;
                aboveBits = e.OldBits;
            }
        }

        bits = below != long.MinValue ? belowBits : aboveBits;
        return below != long.MinValue || above != long.MaxValue;
    }

    /// <summary>
    /// A change in PREPARE: its owner's TSN with the pending bit, until <see cref="Publish"/>. False when the history was retired by a prune that emptied it —
    /// the caller then records into the history that replaces it.
    /// </summary>
    public bool AddPending(long owner, ushort oldBits, ushort newBits)
    {
        lock (_writeLock)
        {
            if (_retired)
            {
                return false;
            }

            var entries = _entries;
            var grown = new Change[entries.Length + 1];
            entries.CopyTo(grown, 0);
            grown[^1] = new Change(EntityRecordHeader.PendingBit | owner, oldBits, newBits);
            Volatile.Write(ref _entries, grown);
            return true;
        }
    }

    /// <summary>The owner's pending change takes its commit stamp. Called at publish, after the record's inline bits, before the owner's card is released.</summary>
    public void Publish(long owner, long stamp)
    {
        lock (_writeLock)
        {
            var entries = _entries;
            var pending = EntityRecordHeader.PendingBit | owner;
            for (var i = 0; i < entries.Length; i++)
            {
                if (entries[i].Stamp == pending)
                {
                    var copy = (Change[])entries.Clone();
                    copy[i] = entries[i] with { Stamp = stamp };
                    Volatile.Write(ref _entries, copy);
                    return;
                }
            }
        }
    }

    /// <summary>An aborted owner's pending change is dropped. Returns true if the history is empty afterwards.</summary>
    public bool RemovePending(long owner)
    {
        lock (_writeLock)
        {
            var pending = EntityRecordHeader.PendingBit | owner;
            var entries = _entries;
            var kept = 0;
            foreach (var e in entries)
            {
                kept += e.Stamp != pending ? 1 : 0;
            }

            if (kept != entries.Length)
            {
                var copy = new Change[kept];
                var k = 0;
                foreach (var e in entries)
                {
                    if (e.Stamp != pending)
                    {
                        copy[k++] = e;
                    }
                }

                Volatile.Write(ref _entries, copy);
            }

            return kept == 0;
        }
    }

    /// <summary>
    /// Prune published entries whose stamp is at or below minTSN (no live snapshot needs them). Returns true if the history is completely empty after pruning
    /// (the caller can remove it). Pending entries are kept.
    /// </summary>
    public bool TryPrune(long minTSN)
    {
        lock (_writeLock)
        {
            var entries = _entries;
            var kept = 0;
            foreach (var e in entries)
            {
                kept += Keeps(e, minTSN) ? 1 : 0;
            }

            if (kept != entries.Length)
            {
                var copy = kept == 0 ? [] : new Change[kept];
                var k = 0;
                foreach (var e in entries)
                {
                    if (Keeps(e, minTSN))
                    {
                        copy[k++] = e;
                    }
                }

                Volatile.Write(ref _entries, copy);
            }

            // Emptied, the history is retired under its lock: a change recorded after this goes to the history replacing it, not to one being removed.
            _retired = kept == 0;
            return _retired;
        }
    }

    private static bool Keeps(Change e, long minTSN) => (e.Stamp & EntityRecordHeader.PendingBit) != 0 || e.Stamp > minTSN;

    /// <summary>Number of history entries.</summary>
    public int Count => Volatile.Read(ref _entries).Length;
}
