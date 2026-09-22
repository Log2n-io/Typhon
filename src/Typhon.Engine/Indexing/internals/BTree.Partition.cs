using System;

namespace Typhon.Engine.Internals;

internal abstract partial class BTree<TKey, TStore>
{
    /// <summary>
    /// Splits a key-sorted batch into contiguous parts whose <b>leaves are disjoint</b>, so W workers can apply them concurrently with no latch and no
    /// shared-node write (#872 step 6, §5.5).
    /// </summary>
    /// <param name="sortedByKey">The batch, ascending by key — the same ordering <see cref="UpdateValues(ReadOnlySpan{BTreeValueUpdate{TKey}},
    /// ref ChunkAccessor{TStore})"/> requires.</param>
    /// <param name="desiredParts">Upper bound on the number of parts. Fewer are returned when the batch cannot be split that finely.</param>
    /// <param name="boundaries">Receives one start offset per part plus a trailing <c>sortedByKey.Length</c>; must hold at least
    /// <paramref name="desiredParts"/> + 1 elements. Part <c>i</c> is <c>sortedByKey[boundaries[i]..boundaries[i + 1]]</c>.</param>
    /// <param name="accessor">Caller's chunk accessor; supplies the ChangeSet.</param>
    /// <returns>The number of parts produced. Zero only for an empty batch.</returns>
    public int PartitionByLeafBoundaries(ReadOnlySpan<BTreeValueUpdate<TKey>> sortedByKey, int desiredParts, Span<int> boundaries,
        ref ChunkAccessor<TStore> accessor)
        => PartitionByLeafBoundaries<BTreeValueUpdate<TKey>, UniqueApplier>(sortedByKey, desiredParts, boundaries, ref accessor);

    /// <inheritdoc cref="PartitionByLeafBoundaries(ReadOnlySpan{BTreeValueUpdate{TKey}}, int, Span{int}, ref ChunkAccessor{TStore})"/>
    /// <param name="sortedByKey">The batch, ascending by key. Entries sharing a key are adjacent and are never split across parts.</param>
    /// <param name="desiredParts">Upper bound on the number of parts.</param>
    /// <param name="boundaries">Receives one start offset per part plus a trailing <c>sortedByKey.Length</c>.</param>
    /// <param name="accessor">Caller's chunk accessor; supplies the ChangeSet.</param>
    public int PartitionByLeafBoundaries(ReadOnlySpan<BTreeMultiValueUpdate<TKey>> sortedByKey, int desiredParts, Span<int> boundaries,
        ref ChunkAccessor<TStore> accessor)
        => PartitionByLeafBoundaries<BTreeMultiValueUpdate<TKey>, MultiApplier>(sortedByKey, desiredParts, boundaries, ref accessor);

    /// <summary>
    /// Splits by count, then snaps each boundary FORWARD until it crosses a leaf edge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Equal counts alone are not enough, and equal leaves alone balance terribly.</b> A count split gives each worker the same number of entries; the
    /// snap is what makes the parts structurally disjoint. §5.5 rejects the obvious alternative — partitioning by the root's separators — because it
    /// balances on key SPACE rather than on work, and re-clustering produces exactly the clustered distribution that sends most of a batch into a few
    /// subtrees.
    /// </para>
    /// <para>
    /// <b>Why snapping forward is sufficient.</b> <see cref="TryGetLeafUpperBound"/> returns the exclusive upper bound of the key range that descends to the
    /// boundary key's leaf. Advancing past every entry below that bound leaves the whole of that leaf on the left of the cut, and the first entry on the right
    /// routes to a strictly later leaf. Disjointness is therefore a property of the construction rather than something a later check has to discover — which
    /// matters because the parts run concurrently with no latch and no version validation (<c>EW-01</c>).
    /// </para>
    /// <para>
    /// Cost is <c>desiredParts - 1</c> root-to-leaf descents of ~4 node visits each, against the ~9 900 the batch itself costs at N = 10 000 (§5.3).
    /// </para>
    /// </remarks>
    private int PartitionByLeafBoundaries<TEntry, TApplier>(ReadOnlySpan<TEntry> sortedByKey, int desiredParts, Span<int> boundaries,
        ref ChunkAccessor<TStore> accessor)
        where TEntry : struct
        where TApplier : struct, ILeafApplier<TEntry>
    {
        if (boundaries.Length < desiredParts + 1)
        {
            ThrowHelper.ThrowInvalidOp(
                $"PartitionByLeafBoundaries needs room for {desiredParts + 1} offsets ({desiredParts} part starts plus the trailing end) but was given "
                + $"{boundaries.Length}.");
        }

        if (sortedByKey.Length == 0)
        {
            boundaries[0] = 0;
            return 0;
        }

        AssertSortedAscending<TEntry, TApplier>(sortedByKey);

        boundaries[0] = 0;

        // A single part needs no descent at all, and neither does an empty tree: every key is absent, so any split is as good as any other and one part
        // spares the caller a fan-out that would apply nothing.
        var root = Root;
        if (desiredParts <= 1 || !root.IsValid)
        {
            boundaries[1] = sortedByKey.Length;
            return 1;
        }

        TApplier applier = default;
        var parts = 1;
        var lastBoundary = 0;

        ref var opAccessor = ref _segment.RentWarmAccessor(accessor.ChangeSet);
        try
        {
            for (var p = 1; p < desiredParts; p++)
            {
                // Computed from the ORIGINAL length rather than from what is left, so a snap that swallows a lot does not drag every later boundary with it.
                var want = (int)((long)sortedByKey.Length * p / desiredParts);
                if (want <= lastBoundary)
                {
                    continue;   // a previous snap already reached past this nominal cut
                }

                if (!TryGetLeafUpperBound(applier.KeyOf(sortedByKey[want]), out var upperBound, out _, ref opAccessor))
                {
                    break;      // rightmost leaf: everything from here on belongs to the final part
                }

                var cut = want;
                while (cut < sortedByKey.Length && CompareKeys(applier.KeyOf(sortedByKey[cut]), upperBound, Comparer) < 0)
                {
                    cut++;
                }

                if (cut >= sortedByKey.Length)
                {
                    break;      // the snap consumed the tail; an empty part would only cost a dispatch
                }

                boundaries[parts++] = cut;
                lastBoundary = cut;
            }
        }
        finally
        {
            _segment.ReturnWarmAccessor();
        }

        boundaries[parts] = sortedByKey.Length;
        return parts;
    }

    /// <summary>
    /// Chunk id of the leaf that owns <paramref name="key"/>'s range — the leaf a bulk update would write it into, whether or not the key is present.
    /// </summary>
    /// <remarks>
    /// The unit step 6's disjointness argument is stated in. <see cref="PartitionByLeafBoundaries(ReadOnlySpan{BTreeValueUpdate{TKey}}, int, Span{int},
    /// ref ChunkAccessor{TStore})"/> guarantees that two parts never share a leaf, and the partitioning descent writes leaves and nothing else, so this is
    /// what "no two workers write the same node" reduces to. Exposed so that claim can be checked against an independently computed leaf set rather than
    /// taken from the partitioner that made it.
    /// </remarks>
    internal int GetLeafChunkIdFor(TKey key, ref ChunkAccessor<TStore> accessor)
    {
        TryGetLeafUpperBound(key, out _, out var leafChunkId, ref accessor);
        return leafChunkId;
    }

    /// <summary>
    /// Exclusive upper bound of the key range that descends to <paramref name="key"/>'s leaf, or <c>false</c> when that leaf is the rightmost one and the
    /// range runs to +∞. <paramref name="leafChunkId"/> is set either way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bound is the <b>tightest separator to the right</b> of the descent path, and it is already produced by the seam the bulk descent uses:
    /// <c>FindChildAndBound</c> returns each level's right bound as a by-product of locating the child. Levels where the walk enters a node's LAST child
    /// report no bound of their own and inherit the one from above, which is why the running value is only replaced when a level supplies one.
    /// </para>
    /// <para>
    /// Callable only inside the exclusive tick-fence window, like everything else on this path: it takes no read-version and validates nothing, so a
    /// concurrent split would leave it reporting a bound for a leaf that no longer owns the range.
    /// </para>
    /// </remarks>
    private bool TryGetLeafUpperBound(TKey key, out TKey upperBound, out int leafChunkId, ref ChunkAccessor<TStore> accessor)
    {
        upperBound = default;
        leafChunkId = -1;
        var node = Root;
        if (!node.IsValid)
        {
            return false;
        }

        var bounded = false;
        for (var depth = 0; depth <= MaxBulkDescentDepth; depth++)
        {
            _storage.ReadNodeHeader(node, out var isLeaf, out var count, ref accessor);
            if (isLeaf)
            {
                leafChunkId = node.ChunkId;
                return bounded;
            }

            _storage.FindChildAndBound(node, key, count, Comparer, out var childChunkId, out var rightBound, out var hasRightBound, ref accessor);
            if (hasRightBound)
            {
                upperBound = rightBound;
                bounded = true;
            }

            var child = _storage.LoadNode(childChunkId);
            if (!child.IsValid)
            {
                ThrowHelper.ThrowInvalidOp(
                    $"B+Tree leaf-bound descent reached an invalid child from node {node.ChunkId}. Under EW-01 no concurrent writer can be blamed, so this "
                    + "is structural damage.");
            }

            node = child;
        }

        ThrowHelper.ThrowInvalidOp(
            $"B+Tree leaf-bound descent exceeded a depth of {MaxBulkDescentDepth}. The tree is cyclic or impossibly deep.");
        return false;
    }
}
