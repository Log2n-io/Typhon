using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// A B+Tree range scan parked between calls: everything a cursor needs to resume, and nothing that cannot be stored in an array.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>BTree.RangeEnumerator</c> is a <c>ref struct</c>, so K of them cannot be held at once and a K-way merge over K archetypes had to
/// drain every input up front. This state is a plain struct of integers, so the merge can hold one per archetype and pull from whichever is currently winning.
/// It carries no pointer, no accessor and no reference: the caller supplies a <c>ChunkAccessor</c> on each call, exactly as every <c>NodeWrapper</c> method
/// already does.
/// </para>
/// <para>
/// <b>The parked position is a key, not an index.</b> <see cref="ResumeKeyBits"/> holds the last key handed out. A leaf index would be meaningless after the
/// leaf split, merged, or had an entry inserted before the cursor — all of which a writer may do while the cursor is parked. Resuming by key is well defined
/// under every one of those, and it is what makes the emitted sequence strictly monotonic rather than merely usually so.
/// </para>
/// <para>
/// <b>Keys are parked as raw bits</b> (<see cref="OrderedKeyEncoding.ToRawBits{TKey}"/>), not as the ordered encoding the merge compares on, so no inverse
/// encoding has to exist.
/// </para>
/// </remarks>
internal struct LeafPageCursorState
{
    /// <summary>Raw bits of the range's lower bound key (the seek target going forward, the stop bound going backward).</summary>
    public long MinKeyBits;

    /// <summary>Raw bits of the range's upper bound key (the stop bound going forward, the seek target going backward).</summary>
    public long MaxKeyBits;

    /// <summary>Raw bits of the last key handed to the caller. Only meaningful once <see cref="HasResume"/> is set.</summary>
    public long ResumeKeyBits;

    /// <summary>Chunk id of the leaf the cursor is standing on, or 0 before the first fill.</summary>
    public int NodeChunkId;

    /// <summary>Sibling links captured from the same validated snapshot as the entries — never re-read from a leaf whose version has since moved.</summary>
    public int NextChunkId;

    public int PrevChunkId;

    /// <summary>Which typed encoding <see cref="OrderedKeyEncoding.Encode{TKey}"/> should apply to this tree's keys.</summary>
    public KeyType KeyType;

    public bool Reverse;

    /// <summary>False until the first fill has descended to the starting leaf.</summary>
    public bool Opened;

    public bool HasResume;

    /// <summary>Set once the range is fully consumed. Further fills return 0 without touching the tree.</summary>
    public bool Exhausted;

    /// <summary>
    /// Iteration index just past the last entry consumed from the current leaf. A HINT, never trusted: it is checked
    /// against <see cref="ResumeKeyBits"/> before use and discarded if the leaf no longer matches.
    /// </summary>
    /// <remarks>
    /// Resuming by key is what makes the cursor safe, but re-finding the key means rescanning the leaf's prefix on every
    /// refill, and a page smaller than a leaf refills into the same leaf repeatedly — which is exactly what an
    /// AllowMultiple scan does, since one leaf can hold 29 keys' worth of value lists. The hint turns that rescan into
    /// one comparison in the case where nothing moved, and costs nothing when something did.
    /// </remarks>
    public int LeafScanHint;
}

internal abstract partial class BTree<TKey, TStore>
{
    /// <summary>Why a leaf's copy loop stopped. The distinction matters: only <see cref="LeafDone"/> licenses moving to the sibling.</summary>
    private enum LeafFillOutcome
    {
        /// <summary>Every entry in the leaf was considered. The scan may continue on the sibling.</summary>
        LeafDone,

        /// <summary>The page ran out of room with entries still left in this leaf. The cursor must stay put.</summary>
        PageFull,

        /// <summary>Iteration passed the far end of the requested range. The scan is over.</summary>
        ReachedBound
    }

    /// <summary>
    /// Fills <paramref name="orderedKeys"/> / <paramref name="values"/> with the next page of entries in range order,
    /// advancing <paramref name="state"/> past them.
    /// </summary>
    /// <returns>
    /// The number of entries written; 0 when the range is exhausted; or a NEGATIVE number <c>-n</c> meaning the spans
    /// must be grown to <c>n</c> and the call retried. The negative case only arises on an <c>AllowMultiple</c> tree
    /// whose single key holds more values than the whole page (see below).
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>One leaf per call, snapshotted.</b> A fill reads a leaf's OLC version, copies its entries out while converting
    /// keys to the ordered encoding, then re-validates. On failure it retries the leaf, and if the leaf turned out to be
    /// obsolete it re-descends from <see cref="LeafPageCursorState.ResumeKeyBits"/>. Entries handed back therefore come
    /// from one consistent version of one leaf, which is what makes them safe to serve from an array afterwards with no
    /// further validation — the caller pays no per-entry OLC cost at all.
    /// </para>
    /// <para>
    /// <b>A leaf is small</b> — 29 entries at the 32-bit key stride, 19 at 64-bit — so "one page" is a bounded amount of
    /// read-ahead, not a materialisation. Over-reading to the end of a leaf costs an array write per entry, against the
    /// chunk resolution that opening the leaf has already paid for.
    /// </para>
    /// <para>
    /// <b>AllowMultiple emits whole keys only.</b> A key owns a variable-sized value buffer; parking in the middle of one
    /// would mean remembering a position inside a structure a concurrent writer may reallocate. Emitting entire keys
    /// keeps the resume point a key — the same well-defined thing it is everywhere else — at the cost of overshooting a
    /// <c>Take</c> boundary by at most one key's worth of values.
    /// </para>
    /// </remarks>
    internal override int FillOrderedPage(ref LeafPageCursorState state, Span<long> orderedKeys, Span<int> values, ref ChunkAccessor<TStore> accessor)
    {
        if (state.Exhausted || orderedKeys.IsEmpty)
        {
            return 0;
        }

        var minKey = OrderedKeyEncoding.FromRawBits<TKey>(state.MinKeyBits);
        var maxKey = OrderedKeyEncoding.FromRawBits<TKey>(state.MaxKeyBits);

        if (!state.Opened)
        {
            if (Comparer.Compare(minKey, maxKey) > 0 || IsEmpty())
            {
                state.Exhausted = true;
                return 0;
            }

            var startLeaf = FindLeaf(state.Reverse ? maxKey : minKey, out _, ref accessor);
            if (!startLeaf.IsValid)
            {
                state.Exhausted = true;
                return 0;
            }

            state.NodeChunkId = startLeaf.ChunkId;
            state.LeafScanHint = 0;
            state.Opened = true;
        }

        // ONE leaf per fill, and the loop below only ever moves on from a leaf that gave nothing.
        //
        // Continuing into the next leaf while the page still had room was tried and measured worse: it is read-ahead
        // that a stream losing every comparison in the merge still pays for, and at K=64 it cost 251 us against 152 us
        // for stopping at the leaf. Long scans are served instead by the caller growing its page (ArchetypeSortedStream
        // doubles it whenever a fill comes back full), which widens read-ahead only for streams that have proven they
        // are being drained.
        var leavesVisited = 0;

        while (true)
        {
            var written = FillFromCurrentLeaf(ref state, orderedKeys, values, minKey, maxKey, out var outcome, ref accessor);

            // "Grow the page and call again" is not a fill. It leaves the cursor deliberately untouched so the retry
            // re-reads the same key, so it must be handed straight back before any of the advance logic below sees a
            // non-zero return and mistakes it for progress — which skipped the oversized key outright.
            if (written < 0)
            {
                return written;
            }

            if (written > 0 || state.Exhausted)
            {
                // A leaf that was read to its end must be stepped off NOW, not on the next call. Leaving the cursor
                // parked on a finished leaf made every subsequent fill re-snapshot it and walk all 29 entries only to
                // discard each one as behind the resume key — measured as exactly 2x the leaf snapshots and 2x the
                // GetItem calls a scan actually needs.
                //
                // PageFull is the opposite case and must NOT advance: entries are still owed from this leaf, and the
                // cursor's untouched NodeChunkId is what makes the next fill resume on it, past the resume key.
                if (written > 0 && !state.Exhausted && outcome == LeafFillOutcome.LeafDone)
                {
                    var doneSibling = state.Reverse ? state.PrevChunkId : state.NextChunkId;
                    if (doneSibling == 0)
                    {
                        state.Exhausted = true;
                    }
                    else
                    {
                        state.NodeChunkId = doneSibling;
                        state.LeafScanHint = 0;
                    }
                }

                return written;
            }

            if (outcome == LeafFillOutcome.PageFull)
            {
                return 0;
            }

            var siblingId = state.Reverse ? state.PrevChunkId : state.NextChunkId;
            if (siblingId == 0)
            {
                state.Exhausted = true;
                return 0;
            }

            state.NodeChunkId = siblingId;
            state.LeafScanHint = 0;

            // Bound one call's latency over a long run of leaves that contribute nothing. Returning 0 here does NOT mean
            // the range ended — Exhausted says that — so the caller refills again from an advanced cursor.
            if (++leavesVisited >= MaxLeavesPerFill)
            {
                return 0;
            }
        }
    }

    /// <summary>How many leaves one <see cref="FillOrderedPage"/> call will walk before returning, however little it found.</summary>
    private const int MaxLeavesPerFill = 64;

    /// <summary>
    /// Snapshots the leaf named by <paramref name="state"/> and copies the entries that are in range and ahead of the
    /// resume point. Returns 0 without setting <c>Exhausted</c> when this leaf contributes nothing but the scan should
    /// continue on its sibling.
    /// </summary>
    private int FillFromCurrentLeaf(ref LeafPageCursorState state, Span<long> orderedKeys, Span<int> values, TKey minKey, TKey maxKey,
        out LeafFillOutcome outcome, ref ChunkAccessor<TStore> accessor)
    {
        outcome = LeafFillOutcome.LeafDone;

        // A leaf can be write-locked when the cursor arrives. Spinning is right for a lock — it is released in
        // nanoseconds — and wrong for an obsolete node, which never becomes valid again. The two are separated rather
        // than both mapped to "version 0, try again", which is how a reader could spin forever on a reclaimed leaf.
        const int lockSpinLimit = 64;

        var node = new NodeWrapper(_storage, state.NodeChunkId);
        if (!node.IsValid)
        {
            state.Exhausted = true;
            return 0;
        }

        var spins = 0;
        while (true)
        {
            var latch = node.GetLatch(ref accessor);
            var version = latch.ReadVersion();
            if (version == 0)
            {
                if (latch.IsObsolete || ++spins > lockSpinLimit)
                {
                    // Obsolete, or locked for longer than a writer plausibly holds it. Re-descend from the resume point:
                    // correct under a split, a merge and a reclaim alike, because it asks the tree where the key lives
                    // NOW rather than assuming the cursor's leaf still answers for it.
                    if (!Redescend(ref state, minKey, maxKey, ref accessor))
                    {
                        return 0;
                    }

                    node = new NodeWrapper(_storage, state.NodeChunkId);
                    spins = 0;
                    continue;
                }

                Thread.SpinWait(1);
                continue;
            }

            var count = node.GetCount(ref accessor);
            if ((uint)count > (uint)node.GetCapacity())
            {
                // Torn read of a header a writer is mutating. Validation would reject it anyway; bail out first so the
                // copy loop below can never run off the end of the chunk.
                if (!latch.ValidateVersion(version))
                {
                    spins = 0;
                    continue;
                }

                state.Exhausted = true;
                return 0;
            }

            var page = new PageFill(orderedKeys, values);
            var leafOutcome = CopyLeafEntries(node, count, in state, minKey, maxKey, ref page, ref accessor);

            // One conversion per fill instead of one per entry. Reading it here keeps it inside the optimistic window,
            // so the version check below covers the parked resume key exactly as it covers the entries.
            if (page.Written > 0 && page.LeafScanHint > 0)
            {
                var lastIterationIndex = page.LeafScanHint - 1;
                var lastLogicalIndex = state.Reverse ? count - 1 - lastIterationIndex : lastIterationIndex;
                page.LastKeyBits = OrderedKeyEncoding.ToRawBits(node.GetItem(lastLogicalIndex, ref accessor).Key);
            }

            var nextId = node.GetNext(ref accessor).ChunkId;
            var prevId = node.GetPrevious(ref accessor).ChunkId;

            // Everything above was read without holding anything. Only now does it become real. Note that this covers
            // the parked resume key too, because it was captured inside the same copy loop.
            if (!latch.ValidateVersion(version))
            {
                spins = 0;
                continue;
            }

            if (page.Needed != 0)
            {
                return -page.Needed;
            }

            state.NextChunkId = nextId;
            state.PrevChunkId = prevId;

            if (page.Written > 0)
            {
                state.ResumeKeyBits = page.LastKeyBits;
                state.HasResume = true;
                state.LeafScanHint = page.LeafScanHint;
            }

            if (leafOutcome == LeafFillOutcome.ReachedBound)
            {
                state.Exhausted = true;
            }

            outcome = leafOutcome;
            return page.Written;
        }
    }

    /// <summary>
    /// Copies one leaf's in-range, ahead-of-cursor entries into <paramref name="page"/>.
    /// Returns true when iteration crossed the far end of the range, so the scan is finished rather than merely done with this leaf.
    /// </summary>
    private LeafFillOutcome CopyLeafEntries(NodeWrapper node, int count, in LeafPageCursorState state, TKey minKey, TKey maxKey, ref PageFill page,
        ref ChunkAccessor<TStore> accessor)
    {
        var allowMultiple = AllowMultiple;
        var reverse = state.Reverse;
        var resume = state.HasResume ? OrderedKeyEncoding.FromRawBits<TKey>(state.ResumeKeyBits) : default;

        // A leaf's keys are sorted, and iteration walks them in order, so the two "not there yet" filters — before the near bound, and behind the parked resume
        // point — can only go from failing to passing, never back. Once one entry clears them, every later entry in this leaf clears them too. Hoisting that
        // out matters: Comparer is an IComparer<TKey>, so each of these is an interface call, and running all three per entry made a single-archetype scan
        // measurably slower than the ref-struct enumerator it replaced. In steady state only the far-bound check survives, which is exactly what the old
        // enumerator paid.
        var gateOpen = false;
        var start = 0;

        // Try to resume where the last fill stopped. The hint is only honoured when the entry just before it still IS the resume key, which is a single
        // comparison and is false exactly when a writer has shifted the leaf under the cursor — in which case the scan falls back to the linear walk that
        // re-finds the key from scratch.
        if (state.HasResume && state.LeafScanHint > 0 && state.LeafScanHint < count)
        {
            var probeIndex = reverse ? count - state.LeafScanHint : state.LeafScanHint - 1;
            if (Comparer.Compare(node.GetItem(probeIndex, ref accessor).Key, resume) == 0)
            {
                start = state.LeafScanHint;
                gateOpen = true;
            }
        }

        for (var i = start; i < count; i++)
        {
            // The leaf is a circular buffer in storage but GetItem takes a logical index, so forward is 0..count-1 and reverse is its mirror.
            var item = node.GetItem(reverse ? count - 1 - i : i, ref accessor);

            // The far bound is the scan's stopping condition, so it is checked for every entry, gate or no gate.
            if (reverse ? Comparer.Compare(item.Key, minKey) < 0 : Comparer.Compare(item.Key, maxKey) > 0)
            {
                return LeafFillOutcome.ReachedBound;
            }

            if (!gateOpen)
            {
                if (reverse ? Comparer.Compare(item.Key, maxKey) > 0 : Comparer.Compare(item.Key, minKey) < 0)
                {
                    continue;
                }

                // Behind the cursor — already handed out, or inserted behind it while it was parked. Either way it must not be emitted: an ordered scan that
                // goes backwards is indistinguishable, to its caller, from a duplicate row.
                if (state.HasResume && !IsAhead(item.Key, resume, reverse))
                {
                    continue;
                }

                gateOpen = true;
            }

            var encoded = OrderedKeyEncoding.Encode(item.Key, state.KeyType);

            if (!allowMultiple)
            {
                if (!page.TryAppend(encoded, item.Value))
                {
                    return LeafFillOutcome.PageFull;
                }

                page.LeafScanHint = i + 1;
                continue;
            }

            if (!AppendKeyValues(encoded, item.Value, ref page))
            {
                return LeafFillOutcome.PageFull;
            }

            page.LeafScanHint = i + 1;
        }

        return LeafFillOutcome.LeafDone;
    }

    /// <summary>
    /// Expands one <c>AllowMultiple</c> key's value buffer into <paramref name="page"/>, all of it or none of it.
    /// Returns false when the page is full, having left it exactly as it was before this key.
    /// </summary>
    private bool AppendKeyValues(long encodedKey, int bufferId, ref PageFill page)
    {
        var buffer = _storage.GetBufferReadOnlyAccessor(bufferId);
        try
        {
            if (!buffer.IsValid)
            {
                return true;   // a key whose buffer holds nothing is not a stopping condition, just an empty key
            }

            var mark = page.Written;
            do
            {
                var chunk = buffer.ReadOnlyElements;
                for (var i = 0; i < chunk.Length; i++)
                {
                    if (!page.TryAppend(encodedKey, chunk[i]))
                    {
                        // Indivisible: back the partial key out so the cursor's resume point stays on the last COMPLETE
                        // key and the next fill re-reads this one whole.
                        page.Rewind(mark);

                        // Nothing preceded this key on the page, so growing is the only way it will ever fit.
                        if (mark == 0)
                        {
                            page.DemandCapacity();
                        }

                        return false;
                    }
                }
            }
            while (buffer.NextChunk());

            return true;
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>Re-descends to the leaf that now owns the resume point, and reports whether the scan can continue at all.</summary>
    private bool Redescend(ref LeafPageCursorState state, TKey minKey, TKey maxKey, ref ChunkAccessor<TStore> accessor)
    {
        var seek = state.HasResume ? OrderedKeyEncoding.FromRawBits<TKey>(state.ResumeKeyBits) : state.Reverse ? maxKey : minKey;

        var leaf = FindLeaf(seek, out _, ref accessor);
        if (!leaf.IsValid)
        {
            state.Exhausted = true;
            return false;
        }

        state.NodeChunkId = leaf.ChunkId;
        state.LeafScanHint = 0;
        return true;
    }

    /// <summary>Whether <paramref name="key"/> lies strictly ahead of the cursor's resume point in iteration order.</summary>
    private bool IsAhead(TKey key, TKey resumeKey, bool reverse) => reverse ? Comparer.Compare(key, resumeKey) < 0 : Comparer.Compare(key, resumeKey) > 0;

    /// <summary>
    /// The page being filled: the two output spans, how far they are filled, and the raw bits of the last key written.
    /// </summary>
    /// <remarks>
    /// Tracking the last key here rather than re-deriving it after the copy is what lets the parked resume point be covered by the same OLC validation as the
    /// entries — a key read after validation could name an entry that the leaf never held at that version.
    /// </remarks>
    private ref struct PageFill(Span<long> keys, Span<int> values)
    {
        private readonly Span<long> _keys = keys;
        private readonly Span<int> _values = values;

        public int Written;

        /// <summary>
        /// Raw bits of the last key fully emitted. Filled ONCE per fill, from <see cref="LeafScanHint"/>, rather than on every append: for a unique tree that
        /// is one conversion per entry, and it showed up as a per-row cost on a single-archetype scan where there is no fan-out win to pay for it.
        /// </summary>
        public long LastKeyBits;

        /// <summary>Iteration index just past the last entry consumed, carried out to <see cref="LeafPageCursorState.LeafScanHint"/>.</summary>
        public int LeafScanHint;

        /// <summary>Set to the span size the caller must grow to, and only when a single key could not fit in an empty page.</summary>
        public int Needed;

        /// <summary>Appends one entry. Returns false when the page is full — which for a unique tree simply ends the page, not the scan.</summary>
        public bool TryAppend(long orderedKey, int value)
        {
            if (Written == _keys.Length)
            {
                return false;
            }

            _keys[Written] = orderedKey;
            _values[Written] = value;
            Written++;
            return true;
        }

        /// <summary>
        /// Discards everything written since <paramref name="mark"/>. The resume key is derived from <see cref="LeafScanHint"/>, which this deliberately does
        /// not move.
        /// </summary>
        public void Rewind(int mark) => Written = mark;

        /// <summary>Declares that the page is too small to hold a single indivisible key, and how big it must become.</summary>
        public void DemandCapacity() => Needed = _keys.Length * 2;
    }
}
