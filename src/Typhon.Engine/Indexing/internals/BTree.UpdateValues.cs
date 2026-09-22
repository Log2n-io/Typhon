using System;
using System.Diagnostics;

namespace Typhon.Engine.Internals;

/// <summary>
/// One entry of a bulk value update on a <b>unique</b> index: the key to find, and the value to store under it.
/// </summary>
/// <remarks>
/// Deliberately 16 bytes for a 64-bit key, carrying nothing an `AllowMultiple` index would need. A single struct serving both index kinds would put an
/// <c>ElementId</c> and an <c>OldValue</c> into every unique batch to serve fields the unique path never reads — 50 % more bytes streamed through the
/// partitioning descent, which is bandwidth-bound over the batch. <see cref="BTreeMultiValueUpdate{TKey}"/> is the wider one.
/// </remarks>
internal readonly struct BTreeValueUpdate<TKey> where TKey : unmanaged
{
    public readonly TKey Key;
    public readonly int NewValue;

    public BTreeValueUpdate(TKey key, int newValue)
    {
        Key = key;
        NewValue = newValue;
    }
}

/// <summary>
/// One entry of a bulk value update on an <c>AllowMultiple</c> index: which element under the key, and what it currently holds.
/// </summary>
/// <remarks>
/// <see cref="OldValue"/> is not redundant with <see cref="ElementId"/>. The id names the <b>chunk</b> holding the element, and elements are
/// addressed BY VALUE within that chunk — <c>DeleteElement</c> locates one the same way — so replacing an element requires the value being replaced.
/// Migration has it: the old <c>ClusterLocation</c> is precisely what is being overwritten.
/// </remarks>
internal readonly struct BTreeMultiValueUpdate<TKey> where TKey : unmanaged
{
    public readonly TKey Key;
    public readonly int ElementId;
    public readonly int OldValue;
    public readonly int NewValue;

    public BTreeMultiValueUpdate(TKey key, int elementId, int oldValue, int newValue)
    {
        Key = key;
        ElementId = elementId;
        OldValue = oldValue;
        NewValue = newValue;
    }
}

/// <summary>
/// What one bulk update actually did, for the node-visit model in §5.3 to be checked against rather than assumed.
/// </summary>
/// <remarks>
/// Deliberately a caller-owned struct rather than counters on the tree. The tree's diagnostics are always-on <c>Interlocked</c> fields, which is the right
/// shape for rare slow-path events and the wrong one here: step 6 runs W workers through this descent over disjoint key ranges of the SAME tree, where a
/// shared counter is both an <c>MD-03</c> false-sharing hotspot on the hot loop and an aggregate no worker can be held to.
/// </remarks>
internal struct BulkUpdateStats
{
    /// <summary>Nodes entered, internal and leaf. The quantity §5.3's table predicts.</summary>
    public int NodeVisits;

    /// <summary>Leaves entered. The batch's spatial locality shows up here — a clustered batch touches far fewer than it has entries.</summary>
    public int LeavesTouched;

    /// <summary>Entries whose key was found and whose value was written.</summary>
    public int Applied;

}

internal abstract partial class BTree<TKey, TStore>
{
    /// <summary>
    /// Depth bound for the partitioning descent. A well-formed tree of any size this engine can address is far below it; exceeding it means the tree is
    /// cyclic, and turning that into a loud failure rather than a stack overflow is the same choice <c>MaxNodesVisited</c> makes for the validators.
    /// </summary>
    private const int MaxBulkDescentDepth = 64;

    /// <summary>
    /// Applies one batch entry once the descent has reached the leaf that owns its key. Implemented by a stateless struct per index kind.
    /// </summary>
    /// <remarks>
    /// A struct type parameter constrained to this interface is monomorphised by the JIT, so <see cref="ILeafApplier{TEntry}.KeyOf"/> and
    /// <see cref="ILeafApplier{TEntry}.ApplyLeaf"/> devirtualise and inline — the partitioning descent is written once and neither index kind pays
    /// for the other's entry layout.
    /// </remarks>
    internal interface ILeafApplier<TEntry> where TEntry : struct
    {
        TKey KeyOf(in TEntry entry);

        /// <summary>Applies every entry of one leaf's sub-batch and returns how many were written.</summary>
        /// <remarks>
        /// Per LEAF rather than per entry, and that is the whole reason it is shaped this way: every storage accessor call resolves a chunk id to an address,
        /// so a per-entry interface forces two resolutions per update no matter how good the descent above it is.
        /// </remarks>
        int ApplyLeaf(BTree<TKey, TStore> tree, NodeWrapper leaf, ReadOnlySpan<TEntry> batch, ref ChunkAccessor<TStore> accessor,
            ref ChunkAccessor<TStore> bufferAccessor, ref BulkUpdateStats stats);
    }

    private readonly struct UniqueApplier : ILeafApplier<BTreeValueUpdate<TKey>>
    {
        public TKey KeyOf(in BTreeValueUpdate<TKey> entry) => entry.Key;

        public int ApplyLeaf(BTree<TKey, TStore> tree, NodeWrapper leaf, ReadOnlySpan<BTreeValueUpdate<TKey>> batch, ref ChunkAccessor<TStore> accessor,
            ref ChunkAccessor<TStore> bufferAccessor, ref BulkUpdateStats stats) => tree._storage.ApplyValuesInLeaf(leaf, batch, tree.Comparer, ref accessor);
    }

    private readonly struct MultiApplier : ILeafApplier<BTreeMultiValueUpdate<TKey>>
    {
        public TKey KeyOf(in BTreeMultiValueUpdate<TKey> entry) => entry.Key;

        public int ApplyLeaf(BTree<TKey, TStore> tree, NodeWrapper leaf, ReadOnlySpan<BTreeMultiValueUpdate<TKey>> batch, ref ChunkAccessor<TStore> accessor,
            ref ChunkAccessor<TStore> bufferAccessor, ref BulkUpdateStats stats)
        {
            var applied = 0;
            var i = 0;
            while (i < batch.Length)
            {
                // Entries are sorted, so those sharing a key are adjacent: find the leaf slot and read the bufferId ONCE for the whole run rather than once
                // per element. A key with many elements is the normal case for a non-unique spatial index, which is what makes this worth doing.
                var key = batch[i].Key;
                var found = tree._storage.TryFindValueInLeaf(leaf, key, tree.Comparer, out var bufferId, ref accessor);

                var end = i + 1;
                while (end < batch.Length && CompareKeys(batch[end].Key, key, tree.Comparer) == 0)
                {
                    end++;
                }

                if (found)
                {
                    // TryFindValueInLeaf resolves the leaf chunk ONCE for this key, where Find followed by GetItem resolved the same id twice. That is the
                    // only amortisation this path keeps: a batched form that also hoisted the BUFFER lock out of the per-element loop was built, measured and
                    // removed. It cut lock acquisitions 200-fold on the widest shape (10 000 to 50 for a 10 000-entry batch) and moved the wall clock by
                    // 685.2 us to 683.7 us at the same row position, so buffer locks are simply not a material cost here and a ref struct holding a lock
                    // across a scope is not worth carrying for it.
                    for (var e = i; e < end; e++)
                    {
                        if (tree._storage.UpdateInBuffer(bufferId, batch[e].ElementId, batch[e].OldValue, batch[e].NewValue, ref bufferAccessor))
                        {
                            applied++;
                        }
                    }
                }

                i = end;
            }

            return applied;
        }
    }

    /// <summary>
    /// <b>Callable only inside the exclusive tick-fence window</b> (<c>EW-01</c>). Applies a whole batch of value updates to a <b>unique</b> index in one
    /// descent, visiting every internal node at most once for the batch.
    /// </summary>
    /// <param name="sortedByKey">The updates, ascending by key. Caller-owned; never retained. An unsorted batch is a caller bug (see remarks).</param>
    /// <param name="accessor">Caller's chunk accessor; supplies the ChangeSet.</param>
    /// <returns>The number of entries whose key was found and whose value was written. Absent keys are skipped, exactly as a per-entry loop would.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is not safe to call concurrently with anything, and nothing here enforces that.</b> It performs no OLC version read or validation, takes no
    /// write latch, and never follows a B-link right pointer — it navigates parent to child by separator and trusts what it reads, which is exactly the set of
    /// licences <c>EW-01</c> grants inside the tick fence and nowhere else. Step 6's writer-counter is what turns that obligation into a check; until then it
    /// is a contract, which is why this stays internal.
    /// </para>
    /// <para>
    /// <b>Why one descent beats N.</b> A sorted batch can be partitioned by a node's separators into one contiguous sub-range per child, so each child is
    /// entered at most once and children with no work are skipped entirely. 10 000 updates on a 1 M-entry tree share ~1 200 internal nodes; per-entry descent
    /// re-walks them 10 000 times (§5.3).
    /// </para>
    /// <para>
    /// <b>The structure cannot change.</b> Every mutation is <c>SetValueOnly</c> — one aligned 4-byte store into the node's SoA value array. No split, no
    /// merge, no item shift, no allocation, no root change, so the batch cannot invalidate the separators the partition is being computed from.
    /// </para>
    /// </remarks>
    public int UpdateValues(ReadOnlySpan<BTreeValueUpdate<TKey>> sortedByKey, ref ChunkAccessor<TStore> accessor)
        => UpdateValues(sortedByKey, ref accessor, out _);

    /// <inheritdoc cref="UpdateValues(ReadOnlySpan{BTreeValueUpdate{TKey}}, ref ChunkAccessor{TStore})"/>
    /// <param name="sortedByKey">The updates, ascending by key. Caller-owned; never retained.</param>
    /// <param name="accessor">Caller's chunk accessor; supplies the ChangeSet.</param>
    /// <param name="stats">Node visits, leaves touched and entries applied — the measurement §5.3's model is checked against.</param>
    public int UpdateValues(ReadOnlySpan<BTreeValueUpdate<TKey>> sortedByKey, ref ChunkAccessor<TStore> accessor, out BulkUpdateStats stats)
    {
        _fenceWindow?.NoteMutation("BTree.UpdateValues");
        // Same guard, same reason as TryUpdateValue: on an AllowMultiple tree the leaf slot is a bufferId, and SetValueOnly would overwrite it with a value.
        if (AllowMultiple)
        {
            ThrowHelper.ThrowBulkUpdateValuesOnAllowMultiple();
        }

        return RunBatch<BTreeValueUpdate<TKey>, UniqueApplier>(sortedByKey, false, ref accessor, out stats);
    }

    /// <summary>
    /// <b>Callable only inside the exclusive tick-fence window</b> (<c>EW-01</c>). The <c>AllowMultiple</c> form of
    /// <see cref="UpdateValues(ReadOnlySpan{BTreeValueUpdate{TKey}}, ref ChunkAccessor{TStore})"/>.
    /// </summary>
    /// <param name="sortedByKey">The updates, ascending by key. Several entries may share a key; each names its own element.</param>
    /// <param name="accessor">Caller's chunk accessor; supplies the ChangeSet.</param>
    /// <returns>The number of entries whose key and element were both found and written.</returns>
    /// <remarks>
    /// Entries sharing a key are applied in the order given, each against its own <c>ElementId</c> and <c>OldValue</c>; the descent reaches that key's leaf
    /// once and the buffer writes happen from there. A separate sibling accessor is rented for the buffer pages, mirroring <c>TryUpdateValueAt</c>: VSBS
    /// chunks and index nodes evict each other out of a single warm accessor.
    /// </remarks>
    public int UpdateValues(ReadOnlySpan<BTreeMultiValueUpdate<TKey>> sortedByKey, ref ChunkAccessor<TStore> accessor)
        => UpdateValues(sortedByKey, ref accessor, out _);

    /// <inheritdoc cref="UpdateValues(ReadOnlySpan{BTreeMultiValueUpdate{TKey}}, ref ChunkAccessor{TStore})"/>
    /// <param name="sortedByKey">The updates, ascending by key. Several entries may share a key; each names its own element.</param>
    /// <param name="accessor">Caller's chunk accessor; supplies the ChangeSet.</param>
    /// <param name="stats">Node visits, leaves touched and entries applied.</param>
    public int UpdateValues(ReadOnlySpan<BTreeMultiValueUpdate<TKey>> sortedByKey, ref ChunkAccessor<TStore> accessor, out BulkUpdateStats stats)
    {
        _fenceWindow?.NoteMutation("BTree.UpdateValues(multi)");
        if (!AllowMultiple)
        {
            ThrowHelper.ThrowBulkUpdateValuesOnUnique();
        }

        return RunBatch<BTreeMultiValueUpdate<TKey>, MultiApplier>(sortedByKey, true, ref accessor, out stats);
    }

    /// <summary>Rents the accessors the descent needs and starts it at the root.</summary>
    private int RunBatch<TEntry, TApplier>(ReadOnlySpan<TEntry> batch, bool needsBufferAccessor, ref ChunkAccessor<TStore> accessor, out BulkUpdateStats stats)
        where TEntry : struct
        where TApplier : struct, ILeafApplier<TEntry>
    {
        stats = default;
        if (batch.Length == 0)
        {
            return 0;
        }

        AssertSortedAscending<TEntry, TApplier>(batch);

        var root = Root;
        if (!root.IsValid)
        {
            return 0;
        }

        ref var opAccessor = ref _segment.RentWarmAccessor(accessor.ChangeSet);
        try
        {
            if (!needsBufferAccessor)
            {
                // The unique path never touches a buffer, so the applier's bufferAccessor argument is never read. Aliasing it to the descent's own accessor
                // avoids renting a second one, which matters because AC-5.6 holds this path to step 4's single-entry cost at N = 1.
                return ApplyBatch<TEntry, TApplier>(root, batch, 0, ref opAccessor, ref opAccessor, ref stats);
            }

            ref var sibAccessor = ref _segment.RentWarmSiblingAccessor(accessor.ChangeSet);
            try
            {
                return ApplyBatch<TEntry, TApplier>(root, batch, 0, ref opAccessor, ref sibAccessor, ref stats);
            }
            finally
            {
                _segment.ReturnWarmSiblingAccessor();
            }
        }
        finally
        {
            _segment.ReturnWarmAccessor();
        }
    }

    /// <summary>
    /// Partition the batch by this node's separators and recurse; on a leaf, apply every entry whose key is present.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The partition follows <c>GetChild</c>'s indexing, which starts at −1.</b> With separators <c>k[0..c-1]</c>, the left node owns <c>(-∞, k[0])</c>,
    /// child <c>i</c> owns <c>[k[i], k[i+1])</c> and child <c>c-1</c> owns <c>[k[c-1], +∞)</c> — the same mapping the optimistic descent derives from
    /// <c>index = ~index - 1</c>. The loop runs once per child that HAS work, locating each with the node's own vectorised search and then scanning the batch
    /// forward to that child's right bound; children with an empty sub-range are never entered.
    /// </para>
    /// <para>
    /// <b>Skipping empty sub-ranges is the whole mechanism.</b> A 10-entry batch on a 1 M-entry tree enters ~32 of its ~35 000 nodes because every child whose
    /// sub-range is empty is never entered at all.
    /// </para>
    /// <para>
    /// The leaf's sub-batch is handed to <c>ApplyValuesInLeaf</c>, which resolves the chunk ONCE for the whole sub-batch and then chooses per density: a
    /// dense sub-batch is merged against a cursor walked through the leaf, a sparse one is searched per entry. Either way a key repeated in the batch
    /// resolves to the same slot, which is what the <c>AllowMultiple</c> path needs.
    /// </para>
    /// <para>
    /// A key routed here but not present in this leaf is <b>absent from the tree</b>: the partition sends every key to the one leaf whose separator range owns
    /// it, and under <c>EW-01</c> no concurrent structural change can move it elsewhere mid-batch. That is why this can skip a miss without the B-link walk
    /// and the re-validation the single-entry path needs.
    /// </para>
    /// </remarks>
    private int ApplyBatch<TEntry, TApplier>(NodeWrapper node, ReadOnlySpan<TEntry> batch, int depth, ref ChunkAccessor<TStore> accessor, 
        ref ChunkAccessor<TStore> bufferAccessor, ref BulkUpdateStats stats)
        where TEntry : struct
        where TApplier : struct, ILeafApplier<TEntry>
    {
        if (depth > MaxBulkDescentDepth)
        {
            ThrowHelper.ThrowInvalidOp(
                $"B+Tree bulk update exceeded a descent depth of {MaxBulkDescentDepth}. The tree is cyclic or impossibly deep; no batch can be applied to it.");
        }

        stats.NodeVisits++;
        TApplier applier = default;

        // One resolution for both, instead of GetIsLeaf then GetCount.
        _storage.ReadNodeHeader(node, out var isLeaf, out var count, ref accessor);

        if (isLeaf)
        {
            stats.LeavesTouched++;
            var appliedHere = applier.ApplyLeaf(this, node, batch, ref accessor, ref bufferAccessor, ref stats);
            stats.Applied += appliedHere;
            return appliedHere;
        }

        var pos = 0;
        var applied = 0;

        // Walk the CHILDREN THAT HAVE WORK, not the separators. The first shape of this loop scanned all `count` separators with GetItem, which is one chunk
        // lookup EACH — measured at ~470 ns per node visit on a 1 M-entry tree, so a 10-entry batch that visits 32 nodes lost to the per-entry path it was
        // supposed to beat. Locating the child for the first unassigned entry with the node's own vectorised search costs one lookup and one SIMD compare, and
        // the loop then runs once per child in use rather than once per separator.
        while (pos < batch.Length)
        {
            var childIndex = _storage.FindChildAndBound(
                node,
                applier.KeyOf(batch[pos]),
                count,
                Comparer,
                out var childChunkId,
                out var rightBound,
                out var hasRightBound,
                ref accessor);

            // This child owns everything up to its right bound; the last child owns the rest of the batch.
            var end = batch.Length;
            if (hasRightBound)
            {
                end = pos;
                while (end < batch.Length && CompareKeys(applier.KeyOf(batch[end]), rightBound, Comparer) < 0)
                {
                    end++;
                }
            }

            // In a well-formed tree this cannot fire: the key routed to childIndex is by definition below that child's right bound, so `end` is always past
            // `pos`. It CAN fire on a node whose separators are not ascending (the IXS-04 corruption class the depth and invalid-child guards above exist
            // for), and the failure mode there is not a wrong answer but an unbounded loop INSIDE the tick fence, with no depth growth to trip the other
            // guard. Cheaper to say so than to diagnose a hung fence.
            if (end == pos)
            {
                ThrowHelper.ThrowInvalidOp(
                    $"B+Tree bulk update made no progress at node {node.ChunkId}: the key routed to child {childIndex} does not sort below that child's "
                    + "right bound, so the node's separators are not in ascending order.");
            }

            applied += DescendInto<TEntry, TApplier>(node, childIndex, childChunkId, batch[pos..end], depth, ref accessor, ref bufferAccessor, ref stats);
            pos = end;
        }

        return applied;
    }

    /// <summary>Resolves one child and recurses into it, refusing to dereference an invalid child rather than descending into garbage.</summary>
    private int DescendInto<TEntry, TApplier>(NodeWrapper node, int childIndex, int childChunkId, ReadOnlySpan<TEntry> batch, int depth,
        ref ChunkAccessor<TStore> accessor, ref ChunkAccessor<TStore> bufferAccessor, ref BulkUpdateStats stats)
        where TEntry : struct
        where TApplier : struct, ILeafApplier<TEntry>
    {
        var child = _storage.LoadNode(childChunkId);
        if (!child.IsValid)
        {
            // Under EW-01 there is no concurrent writer to blame, so this is structural damage rather than a race, and continuing would silently drop every
            // update in this sub-range.
            ThrowHelper.ThrowInvalidOp(
                $"B+Tree bulk update reached an invalid child (node {node.ChunkId}, child index {childIndex}) with {batch.Length} update(s) routed to it.");
        }

        return ApplyBatch<TEntry, TApplier>(child, batch, depth + 1, ref accessor, ref bufferAccessor, ref stats);
    }

    /// <summary>
    /// A batch out of key order is a caller bug that would otherwise mis-apply silently, so it is caught where the cost is affordable.
    /// </summary>
    /// <remarks>
    /// The partition assumes ascending order: it hands each child one CONTIGUOUS sub-range and never revisits it. An out-of-order entry is therefore not
    /// merely misplaced — it is routed to whichever child the walk had reached, and either skipped as an absent key or, if that leaf happens to hold the key,
    /// applied to the wrong tree region. The check is <c>O(N)</c> against a batch that costs far more than that to apply, but it is still per-entry work on the
    /// hot path, so it is DEBUG-only: Release keeps the contract, Debug enforces it.
    /// </remarks>
    [Conditional("DEBUG")]
    private void AssertSortedAscending<TEntry, TApplier>(ReadOnlySpan<TEntry> batch)
        where TEntry : struct
        where TApplier : struct, ILeafApplier<TEntry>
    {
        TApplier applier = default;
        for (var i = 1; i < batch.Length; i++)
        {
            if (CompareKeys(applier.KeyOf(batch[i - 1]), applier.KeyOf(batch[i]), Comparer) > 0)
            {
                ThrowHelper.ThrowInvalidOp(
                    $"UpdateValues requires a batch sorted ascending by key; entry {i} sorts before entry {i - 1}. An unsorted batch is not merely reordered "
                    + "— the partitioning descent would route entries to the wrong subtree and drop or misapply them.");
            }
        }
    }
}
