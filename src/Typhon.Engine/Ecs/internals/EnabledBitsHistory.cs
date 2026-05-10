using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-entity history of EnabledBits changes for MVCC snapshot isolation.
/// Stores (changeTSN, oldBits) pairs sorted ascending by TSN.
/// </summary>
/// <remarks>
/// <para>Used by the exception dictionary when concurrent transactions modify EnabledBits.
/// Older transactions see the bits as they were before the change.</para>
/// <para>Entries are pruned when MinTSN advances past all changeTSNs.</para>
/// </remarks>
internal class EnabledBitsHistory
{
    private readonly List<(long ChangeTSN, ushort OldBits)> _entries = [];

    /// <summary>
    /// Resolve the EnabledBits visible at the given transaction TSN.
    /// Walks the history backwards to find the latest change before or at txTsn.
    /// </summary>
    /// <param name="txTsn">The transaction's snapshot TSN.</param>
    /// <param name="currentBits">The inline (latest) EnabledBits from the EntityRecord.</param>
    /// <returns>The EnabledBits visible at txTsn.</returns>
    public ushort ResolveAt(long txTsn, ushort currentBits)
    {
        // Walk from newest to oldest. If any changeTSN > txTsn, the old bits
        // were what the entity had before that change — that's what this tx sees.
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var (changeTsn, oldBits) = _entries[i];
            if (changeTsn > txTsn)
            {
                // This change happened after our snapshot — we see the old bits
                return oldBits;
            }
        }

        // All changes happened at or before our snapshot — we see the current bits
        return currentBits;
    }

    /// <summary>Record an EnabledBits change. Must be called in TSN order (ascending).</summary>
    public void Record(long changeTSN, ushort oldBits) => _entries.Add((changeTSN, oldBits));

    /// <summary>
    /// Prune entries whose changeTSN is at or below minTSN (no active transaction needs them).
    /// Returns true if the history is completely empty after pruning (caller can remove the entry).
    /// </summary>
    public bool TryPrune(long minTSN)
    {
        _entries.RemoveAll(e => e.ChangeTSN <= minTSN);
        return _entries.Count == 0;
    }

    /// <summary>Number of history entries.</summary>
    public int Count => _entries.Count;
}
