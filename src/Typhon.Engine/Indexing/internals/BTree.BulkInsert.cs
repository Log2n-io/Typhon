using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Typhon.Engine.Internals;

/// <summary>One staged index insert: the key and the value the tree stores under it (#1101).</summary>
internal readonly struct BTreeInsert<TKey> where TKey : unmanaged
{
    public readonly TKey Key;
    public readonly int Value;

    public BTreeInsert(TKey key, int value)
    {
        Key = key;
        Value = value;
    }
}

unsafe partial class BTree<TKey, TStore>
{
    /// <summary>
    /// Inserts a batch already sorted by key, ascending (#1101).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a sort, not a bulk load, and that distinction is the whole finding behind it.</b> #1101 was filed to build a bulk-load-shaped pre-split:
    /// compute each leaf's overflow up front, create the new leaves with separators taken from the batch's own keys, batch the parent inserts, and leave the
    /// parallel writers with leaves guaranteed to have room. #1098 measured the workload first and found something cheaper. Presenting the SAME batch in key
    /// order instead of shuffled, through the existing insert path, moved a concentrated 3 000-key burst from 3.59 entry-moves per key to 0.48 and from
    /// 1 417 ancestor writes to 208 — about 7x, for a sort. The pre-split would have been new code in the engine's most expensive file to chase a smaller
    /// remaining factor.
    /// </para>
    /// <para>
    /// <b>Why order matters that much.</b> An ascending insert lands on the rightmost-leaf fast path every time, so a full leaf splits once and the next
    /// keys go into the new right-hand leaf. Shuffled keys route through the general descent and keep re-entering leaves that are already full: the
    /// full-leaf rate goes from 6.8 % of inserts to 47 %, and each one spills — bulk-moving toward half a leaf into a chain NEIGHBOUR and rewriting that
    /// neighbour's ancestor — or splits. The bulk-spill tail exists to hold that rate near 10 %, and its own comment says it was measured "for sequential
    /// append workloads"; nothing had measured the other case.
    /// </para>
    /// <para>
    /// <b>What this does NOT do.</b> It performs no pre-split, so splits and spills still happen — fewer of them, and in the benign shape. It therefore does
    /// not on its own make a parallel leaf-partitioned apply safe: #1098 measured ancestor writes tracking full-leaf events almost one for one, so a full
    /// leaf always writes outside itself whichever outcome it takes. A caller wanting partitioned parallelism still needs structural change kept out of the
    /// region, which is the open part of this work.
    /// </para>
    /// <para>
    /// <b>Duplicates are fine.</b> On an <c>AllowMultiple</c> index equal keys append into one leaf entry's buffer and never grow the node — measured as
    /// zero full leaves at every batch size — so a run of equal keys is the cheapest case here, not a special one.
    /// </para>
    /// </remarks>
    /// <param name="sortedByKey">The batch, ascending by key. Debug builds verify it; a Release caller that lies gets a correct tree built the slow way.</param>
    /// <param name="accessor">Chunk accessor for the inserts.</param>
    /// <returns>The number of entries inserted.</returns>
    public int AddBulkSorted(ReadOnlySpan<BTreeInsert<TKey>> sortedByKey, ref ChunkAccessor<TStore> accessor)
    {
        AssertSortedByKey(sortedByKey);

        for (var i = 0; i < sortedByKey.Length; i++)
        {
            Add(sortedByKey[i].Key, sortedByKey[i].Value, ref accessor, out _);
        }

        return sortedByKey.Length;
    }

    /// <summary>
    /// Sorts <paramref name="batch"/> by key and inserts it (#1101), for a caller holding an unordered batch.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddBulkSorted"/> because the sort is the caller's cost to see: a caller that already has its batch in key order — the fence's
    /// index staging merges into key order anyway — must not pay for a second sort, and one that does not should be able to see the line that sorts. The
    /// comparison uses the tree's own <see cref="IComparer{TKey}"/> rather than <see cref="IComparable{TKey}"/>, so a tree with a custom ordering sorts the
    /// way it searches; sorting one way and descending another produces a correct tree by the slowest path available, which is the bug this would hide.
    /// </remarks>
    public int AddBulkUnsorted(Span<BTreeInsert<TKey>> batch, ref ChunkAccessor<TStore> accessor)
    {
        var comparer = Comparer ?? Comparer<TKey>.Default;
        batch.Sort((a, b) => comparer.Compare(a.Key, b.Key));
        return AddBulkSorted(batch, ref accessor);
    }

    /// <summary>
    /// Debug-only check that the batch really is ascending.
    /// </summary>
    /// <remarks>
    /// Compiled out of Release on purpose. A mis-sorted batch is a performance defect and not a correctness one — every entry still lands in the right leaf,
    /// just by the expensive route — so a Release build must not pay a comparison per entry to detect a caller mistake that costs the caller.
    /// </remarks>
    [Conditional("DEBUG")]
    private void AssertSortedByKey(ReadOnlySpan<BTreeInsert<TKey>> batch)
    {
        var comparer = Comparer ?? Comparer<TKey>.Default;
        for (var i = 1; i < batch.Length; i++)
        {
            Debug.Assert(comparer.Compare(batch[i - 1].Key, batch[i].Key) <= 0,
                $"AddBulkSorted was given a batch that is not ascending at index {i}. The tree will be correct and the insert will be about 7x more "
                + "expensive than it needed to be; sort with AddBulkUnsorted, or sort at the staging that produced the batch.");
        }
    }
}
