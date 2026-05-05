// unset

using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

public abstract partial class BTree<TKey, TStore>
{
    /// <summary>Result of an OLC remove attempt.</summary>
    private enum OlcRemoveResult
    {
        /// <summary>Remove completed successfully.</summary>
        Completed,
        /// <summary>OLC validation failed — caller should retry.</summary>
        Restart,
        /// <summary>Key not found in the tree (confirmed by OLC validation).</summary>
        NotFound,
        /// <summary>Remove requires merge/borrow or structural change — needs pessimistic path.</summary>
        NeedsPessimistic,
    }
    /// <summary>
    /// OLC-dispatching remove: tries optimistic fast paths first, then falls back to pessimistic.
    /// Begin/end remove fast paths and general mid-leaf remove operate without exclusive lock when the leaf has enough items (no merge/borrow needed).
    /// All other cases use the pessimistic path.
    /// </summary>
    private void RemoveCore(ref RemoveArguments args)
    {
        if (IsEmpty())
        {
            return;
        }

        // OLC retry loop — handles begin/end fast paths + general non-merge removes
        for (int attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
        {
            var result = TryRemoveOlc(ref args);
            if (result == OlcRemoveResult.Completed || result == OlcRemoveResult.NotFound)
            {
                return;
            }
            if (result == OlcRemoveResult.NeedsPessimistic)
            {
                break;
            }
            // Restart: continue loop
            Interlocked.Increment(ref _optimisticRestarts);
        }

        // Pessimistic fallback
        Interlocked.Increment(ref _pessimisticFallbacks);
        RemoveCorePessimistic(ref args);
    }

    /// <summary>
    /// OLC remove attempt: tries begin/end fast paths (first/last key of first/last leaf) and general mid-leaf remove via optimistic descent.
    /// Only modifies a single leaf node (WriteLocked). Returns NeedsPessimistic when the leaf is too small (merge/borrow would be needed).
    /// </summary>
    private OlcRemoveResult TryRemoveOlc(ref RemoveArguments args)
    {
        ref var accessor = ref args.Accessor;

        // --- Begin-remove fast path: remove first key of leftmost leaf ---
        {
            var ll = _linkList;
            if (!ll.IsValid)
            {
                return OlcRemoveResult.Restart;
            }
            var llLatch = ll.GetLatch(ref accessor);
            int llVersion = llLatch.ReadVersion();
            if (llVersion == 0)
            {
                return OlcRemoveResult.Restart;
            }

            var firstKey = ll.GetFirst(ref accessor).Key;
            if (!llLatch.ValidateVersion(llVersion))
            {
                return OlcRemoveResult.Restart;
            }

            int order = args.Compare(args.Key, firstKey);
            if (order < 0)
            {
                // Issue #297: ll might no longer be the leftmost leaf (concurrent merge moved the head).
                // Snapshot ll's previous AND validate ll's version after — only conclude NotFound if ll is still leftmost (no previous sibling) AND data is
                // consistent. Otherwise restart.
                var llPrev = ll.GetPrevious(ref accessor);
                if (!llLatch.ValidateVersion(llVersion))
                {
                    return OlcRemoveResult.Restart;
                }
                if (llPrev.IsValid)
                {
                    return OlcRemoveResult.Restart; // ll has a previous sibling → not leftmost; restart with fresh head
                }
                if (OlcDescentTrace.OnRemoveNotFound != null && typeof(TKey) == typeof(int))
                {
                    var ak = args.Key; var fk = firstKey;
                    OlcDescentTrace.OnRemoveNotFound(OlcDescentTrace.RemoveBranchBeginFastPathLessThanFirst,
                        Unsafe.As<TKey, int>(ref ak), ll.ChunkId, Unsafe.As<TKey, int>(ref fk), ll.GetCount(ref accessor));
                }
                return OlcRemoveResult.NotFound; // key < first key AND ll is leftmost → definitely not in tree
            }

            if (order == 0)
            {
                int count = ll.GetCount(ref accessor);
                bool isRoot = _rootChunkId == ll.ChunkId;
                int capacity = ll.GetCapacity();
                if (!llLatch.ValidateVersion(llVersion))
                {
                    return OlcRemoveResult.Restart;
                }

                // Safe if: root leaf with count > 1, or non-root leaf above half-full
                if ((isRoot && count > 1) || (!isRoot && count > capacity / 2))
                {
                    ll.PreDirtyForWrite(ref accessor);
                    if (!llLatch.TryWriteLock())
                    {
                        return OlcRemoveResult.Restart;
                    }
                    if (!llLatch.ValidateVersionLocked(llVersion))
                    {
                        llLatch.AbortWriteLock();
                        return OlcRemoveResult.Restart;
                    }
                    // Re-verify first key under lock (concurrent OLC writer might have removed it)
                    if (args.Compare(args.Key, ll.GetFirst(ref accessor).Key) != 0)
                    {
                        llLatch.WriteUnlock();
                        return OlcRemoveResult.Restart;
                    }

                    args.SetRemovedValue(ll.PopFirstInternal(ref accessor).Value);
                    llLatch.WriteUnlock();
                    _hasCachedLastKey = false;
                    DecCount();
                    return OlcRemoveResult.Completed;
                }

                return OlcRemoveResult.NeedsPessimistic; // merge/borrow possible or tree might become empty
            }
        }

        // --- End-remove fast path: remove last key of rightmost leaf ---
        {
            var rll = _reverseLinkList;
            if (!rll.IsValid)
            {
                return OlcRemoveResult.Restart;
            }
            var rllLatch = rll.GetLatch(ref accessor);
            int rllVersion = rllLatch.ReadVersion();
            if (rllVersion == 0)
            {
                return OlcRemoveResult.Restart;
            }

            int rllCount = rll.GetCount(ref accessor);
            if (rllCount == 0)
            {
                return OlcRemoveResult.Restart; // transient empty state during concurrent tree emptying
            }
            var lastKey = rll.GetItem(rllCount - 1, ref accessor).Key;
            if (!rllLatch.ValidateVersion(rllVersion))
            {
                return OlcRemoveResult.Restart;
            }

            int order = args.Compare(args.Key, lastKey);
            if (order > 0)
            {
                // Issue #297: rll might no longer be the rightmost leaf (concurrent merge moved the tail, OR concurrent split added a new rightmost).
                // Snapshot rll's next AND validate rll's version after — only conclude NotFound if rll is still rightmost (no next sibling) AND data is
                // consistent. Otherwise restart.
                var rllNext = rll.GetNext(ref accessor);
                if (!rllLatch.ValidateVersion(rllVersion))
                {
                    return OlcRemoveResult.Restart;
                }
                if (rllNext.IsValid)
                {
                    return OlcRemoveResult.Restart; // rll has a next sibling → not rightmost; restart with fresh tail
                }
                if (OlcDescentTrace.OnRemoveNotFound != null && typeof(TKey) == typeof(int))
                {
                    var ak = args.Key; var lk = lastKey;
                    OlcDescentTrace.OnRemoveNotFound(OlcDescentTrace.RemoveBranchEndFastPathGreaterThanLast,
                        Unsafe.As<TKey, int>(ref ak), rll.ChunkId, Unsafe.As<TKey, int>(ref lk), rllCount);
                }
                return OlcRemoveResult.NotFound; // key > last key AND rll is rightmost → definitely not in tree
            }

            if (order == 0)
            {
                bool isRoot = _rootChunkId == rll.ChunkId;
                int capacity = rll.GetCapacity();
                if (!rllLatch.ValidateVersion(rllVersion))
                {
                    return OlcRemoveResult.Restart;
                }

                if ((isRoot && rllCount > 1) || (!isRoot && rllCount > capacity / 2))
                {
                    rll.PreDirtyForWrite(ref accessor);
                    if (!rllLatch.TryWriteLock())
                    {
                        return OlcRemoveResult.Restart;
                    }
                    if (!rllLatch.ValidateVersionLocked(rllVersion))
                    {
                        rllLatch.AbortWriteLock();
                        return OlcRemoveResult.Restart;
                    }
                    // Re-verify last key under lock
                    if (args.Compare(args.Key, rll.GetLast(ref accessor).Key) != 0)
                    {
                        rllLatch.WriteUnlock();
                        return OlcRemoveResult.Restart;
                    }

                    args.SetRemovedValue(rll.PopLastInternal(ref accessor).Value);
                    rllLatch.WriteUnlock();
                    _hasCachedLastKey = false;
                    DecCount();
                    return OlcRemoveResult.Completed;
                }

                return OlcRemoveResult.NeedsPessimistic;
            }
        }

        // --- General path: optimistic descent to leaf, remove if safe (no merge/borrow) ---
        var (leafChunkId, leafVersion, keyIndex) = OptimisticDescendToLeaf(args.Key, ref accessor);
        if (leafChunkId == 0)
        {
            return OlcRemoveResult.Restart;
        }

        if (keyIndex < 0)
        {
            // Key not found — validate version to confirm
            var nfLeaf = new NodeWrapper(_storage, leafChunkId);
            if (!nfLeaf.GetLatch(ref accessor).ValidateVersion(leafVersion))
            {
                return OlcRemoveResult.Restart;
            }
            if (OlcDescentTrace.OnRemoveNotFound != null && typeof(TKey) == typeof(int))
            {
                var nfCount = nfLeaf.GetCount(ref accessor);
                int firstKeyInt = 0;
                if (nfCount > 0)
                {
                    var fk = nfLeaf.GetFirst(ref accessor).Key;
                    firstKeyInt = Unsafe.As<TKey, int>(ref fk);
                }
                var ak = args.Key;
                OlcDescentTrace.OnRemoveNotFound(OlcDescentTrace.RemoveBranchGeneralKeyIndexNegative, Unsafe.As<TKey, int>(ref ak), leafChunkId,
                    firstKeyInt, nfCount);
            }
            return OlcRemoveResult.NotFound;
        }

        // Key found — check if safe to remove under OLC (no merge/borrow needed)
        {
            var leaf = new NodeWrapper(_storage, leafChunkId);
            var leafLatch = leaf.GetLatch(ref accessor);
            int count = leaf.GetCount(ref accessor);
            bool isRoot = _rootChunkId == leafChunkId;
            int capacity = leaf.GetCapacity();
            if (!leafLatch.ValidateVersion(leafVersion))
            {
                return OlcRemoveResult.Restart;
            }

            if ((isRoot && count > 1) || (!isRoot && count > capacity / 2))
            {
                leaf.PreDirtyForWrite(ref accessor);
                if (!leafLatch.TryWriteLock())
                {
                    return OlcRemoveResult.Restart;
                }
                if (!leafLatch.ValidateVersionLocked(leafVersion))
                {
                    leafLatch.AbortWriteLock();
                    return OlcRemoveResult.Restart;
                }

                // Re-find key under lock (index might have shifted due to concurrent modification)
                var reIndex = leaf.Find(args.Key, args.Comparer, ref accessor);
                if (reIndex < 0)
                {
                    if (OlcDescentTrace.OnRemoveNotFound != null && typeof(TKey) == typeof(int))
                    {
                        var leafCount = leaf.GetCount(ref accessor);
                        int firstKeyInt = 0;
                        if (leafCount > 0)
                        {
                            var fk = leaf.GetFirst(ref accessor).Key;
                            firstKeyInt = Unsafe.As<TKey, int>(ref fk);
                        }
                        var ak = args.Key;
                        OlcDescentTrace.OnRemoveNotFound(OlcDescentTrace.RemoveBranchUnderLockReFindNegative, Unsafe.As<TKey, int>(ref ak), leafChunkId,
                            firstKeyInt, leafCount);
                    }
                    leafLatch.WriteUnlock();
                    return OlcRemoveResult.NotFound; // concurrent writer already removed it
                }

                args.SetRemovedValue(leaf.RemoveAtInternal(reIndex, ref accessor).Value);
                leafLatch.WriteUnlock();
                _hasCachedLastKey = false;
                DecCount();
                return OlcRemoveResult.Completed;
            }

            return OlcRemoveResult.NeedsPessimistic;
        }
    }

    /// <summary>
    /// Pessimistic remove fallback: uses WriteLock/WriteUnlock on individual nodes so concurrent OLC readers detect changes. No global lock — concurrency is
    /// handled by per-node OLC latches and latch-coupled SMO in RemoveIterative.
    /// </summary>
    private void RemoveCorePessimistic(ref RemoveArguments args)
    {
        ref var accessor = ref args.Accessor;
        try
        {
            if (IsEmpty())
            {
                return;
            }

            // Begin-remove fast path (WriteLock protects against concurrent OLC writers)
            {
                var ll = _linkList;
                ll.PreDirtyForWrite(ref accessor);
                SpinWriteLock(ll.GetLatch(ref accessor));

                // Issue #297: ll might no longer be the leftmost leaf if a concurrent split inserted a new
                // left-side leaf, OR if we observed a stale `_linkList` field. If ll has a valid previous,
                // the key could live in that earlier leaf — fall through to the general path.
                // Mirrors the symmetric end-fast-path safety at the rll branch below.
                if (ll.GetPrevious(ref accessor).IsValid)
                {
                    ll.GetLatch(ref accessor).AbortWriteLock();
                }
                else
                {
                    int order = args.Compare(args.Key, ll.GetFirst(ref accessor).Key);
                    if (order < 0)
                    {
                        ll.GetLatch(ref accessor).AbortWriteLock(); // key not in tree — didn't modify node
                        return;
                    }

                    if (order == 0 && (Root == ll || ll.GetCount(ref accessor) > ll.GetCapacity() / 2))
                    {
                        args.SetRemovedValue(ll.PopFirstInternal(ref accessor).Value);
                        ll.GetLatch(ref accessor).WriteUnlock();
                        _hasCachedLastKey = false;
                        DecCount();
                        if (IsEmpty())
                        {
                            Root = _linkList = _reverseLinkList = default;
                            Height--;
                        }
                        return;
                    }
                    ll.GetLatch(ref accessor).AbortWriteLock(); // condition failed — didn't modify node
                }
            }

            // End-remove fast path
            {
                var rll = _reverseLinkList;
                rll.PreDirtyForWrite(ref accessor);
                SpinWriteLock(rll.GetLatch(ref accessor));

                // Safety: if rll was split concurrently, it's no longer the rightmost leaf.
                // Fall through to general path which handles stale pointers correctly.
                if (rll.GetNext(ref accessor).IsValid)
                {
                    rll.GetLatch(ref accessor).AbortWriteLock();
                }
                else
                {
                    int order = args.Compare(args.Key, rll.GetLast(ref accessor).Key);
                    if (order > 0)
                    {
                        rll.GetLatch(ref accessor).AbortWriteLock(); // key not in tree — didn't modify node
                        return;
                    }

                    if (order == 0 && (Root == rll || rll.GetCount(ref accessor) > rll.GetCapacity() / 2))
                    {
                        args.SetRemovedValue(rll.PopLastInternal(ref accessor).Value);
                        rll.GetLatch(ref accessor).WriteUnlock();
                        _hasCachedLastKey = false;
                        DecCount();
                        return;
                    }
                    rll.GetLatch(ref accessor).AbortWriteLock(); // condition failed — didn't modify node
                }
            }

            // General remove path with latch-coupled SMO — retry on lock contention
            _hasCachedLastKey = false;
            bool merge;
            SpinWait spin = default;
            while (true)
            {
                merge = RemoveIterative(ref args, ref accessor, out bool removeCompleted);
                if (removeCompleted)
                {
                    break;
                }
                Interlocked.Increment(ref _optimisticRestarts);
                spin.SpinOnce();
            }

            if (args.Removed)
            {
                DecCount();
            }

            if (merge && Root.GetLength(ref accessor) == 0)
            {
                Root = Root.GetChild(-1, ref accessor); // left most child becomes root. (returns null for leafs)
                if (Root.IsValid == false)
                {
                    _linkList = default;
                    _reverseLinkList = default;
                }
                Height--;
            }

            // Issue #297: avoid TOCTOU on `_reverseLinkList`. Snapshot the field once and operate on
            // the local — without this, four reads of the field could each see a different value
            // when concurrent removers race here, ending up assigning a stale or non-tail leaf.
            {
                var rl = _reverseLinkList;
                if (rl.IsValid)
                {
                    var prev = rl.GetPrevious(ref accessor);
                    if (prev.IsValid && !prev.GetNext(ref accessor).IsValid)
                    {
                        _reverseLinkList = prev;
                    }
                }
            }
        }
        finally
        {
            // Reclaim deferred nodes every 64 mutations to amortize MinActiveEpoch cost.
            if (++_deferredReclaimSkip >= 64)
            {
                _deferredReclaimSkip = 0;
                DeferredReclaim();
            }
        }
    }
    /// <summary>
    /// Iterative remove with latch-coupled SMO: descends optimistically recording PathVersions, then locks bottom-up only as needed for structural modifications.
    /// Fast path (leaf stays half-full or root leaf): locks only the leaf node.
    /// Slow path (leaf underflows): locks leaf + neighbors + path nodes with version validation.
    /// Sets <paramref name="completed"/> to false when lock acquisition fails and caller must retry.
    /// </summary>
    private bool RemoveIterative(ref RemoveArguments args, ref ChunkAccessor<TStore> accessor, out bool completed)
    {
        completed = false;
        MutationContext ctx = default;
        var node = Root;
        var relatives = new NodeRelatives();
        ref var sibAccessor = ref args.SiblingAccessor;

        // Phase 1: Descend from root to leaf, recording path + PathVersions for validation.
        // OLC protocol: read version BEFORE data, validate AFTER — ensures (index, version) are consistent.
        while (!node.GetIsLeaf(ref accessor))
        {
            var latch = node.GetLatch(ref accessor);
            int version = latch.ReadVersion();
            if (version == 0)
            {
                return false; // node locked or obsolete — restart
            }

            var index = node.Find(args.Key, args.Comparer, ref accessor);
            if (index < 0)
            {
                index = ~index - 1;
            }

            var child = node.GetChild(index, ref accessor);
            int parentCount = node.GetCount(ref accessor);

            // Validate: node wasn't modified during our unlocked read
            if (!latch.ValidateVersion(version))
            {
                return false; // node modified between version read and data read — restart
            }

            // Defensive: a torn-but-validated read should be impossible after the version
            // check above, but treat zero/invalid child as restart rather than crashing
            // when the next iteration tries to deref it. Issue #297.
            if (!child.IsValid)
            {
                return false;
            }

            OlcDescentTrace.RecordStep?.Invoke(OlcDescentTrace.OpRemove, node.ChunkId, version, index, child.ChunkId);

            NodeRelatives.Create(child, index, node, parentCount, ref relatives, out var childRelatives, ref accessor, ref sibAccessor);

            ctx.PathNodes[ctx.Depth] = node;
            ctx.PathChildIndices[ctx.Depth] = index;
            ctx.PathVersions[ctx.Depth] = version;

            // Store after Create so lazy-resolved siblings are cached in the stored copy
            ctx.PathRelatives[ctx.Depth] = relatives;

            node = child;
            relatives = childRelatives;
            ctx.Depth++;
        }

        // Phase 1.5A: Lock leaf with version validation.
        // Between Phase 1 descent and lock acquisition, a concurrent writer may have split/modified
        // this leaf. Snapshot the version before locking, then validate after.
        node.PreDirtyForWrite(ref accessor);
        var leafLatch = node.GetLatch(ref accessor);
        int leafVersion = leafLatch.ReadVersion();
        if (leafVersion == 0)
        {
            // Leaf is locked or obsolete. SpinWriteLock to wait, then restart.
            SpinWriteLock(leafLatch);
            leafLatch.AbortWriteLock();
            return false;
        }
        SpinWriteLock(leafLatch);
        if (!leafLatch.ValidateVersionLocked(leafVersion))
        {
            leafLatch.AbortWriteLock();
            return false; // restart — leaf was modified between descent and lock
        }

        // Issue #297: stale-separator detection. Pessimistic descent does NOT follow B-link right-pointers (unlike OptimisticDescendToLeaf at BTree.cs:1700-1738).
        // Under heavy concurrency, when a leaf has been split but the parent separator hasn't propagated yet, our descent lands at the LEFT half while our key
        // actually lives in the right sibling. Detect that exact signature (key >= leaf.HighKey AND a right sibling exists) and restart from root rather than
        // wrongly returning NotFound. Restart is safe (we hold leaf write lock — release via AbortWriteLock without version bump). Liveness: bounded by
        // parent-separator propagation, which Insert's Phase 3 performs immediately after the split. We do NOT mirror Insert's full move-right loop/ here
        // because the moved-to leaf would have stale `relatives` (computed for the original leaf), and lock-coupling to nextNode under heavy retry pressure has
        // surfaced a latent AV in InsertIterative.SpinWriteLock — separate engine investigation.
        if (node.GetNext(ref accessor).IsValid && args.Compare(args.Key, node.GetHighKey(ref accessor)) >= 0)
        {
            leafLatch.AbortWriteLock(); // we held the lock without modifying the node
            return false; // restart — parent separator hasn't propagated; descent landed at wrong leaf
        }

        // Check if key exists in this leaf
        var keyIndex = node.Find(args.Key, args.Comparer, ref accessor);
        if (keyIndex < 0)
        {
            // After the stale-separator guard above, a NotFound here means key is genuinely not in the tree (or the tree's rightmost leaf, where
            // GetNext().IsValid == false).
            node.GetLatch(ref accessor).AbortWriteLock(); // key not found — didn't modify leaf
            completed = true;
            return false; // key not found — no merge
        }

        // Fast path: leaf won't underflow after remove (count > capacity/2) or root leaf (depth == 0).
        // RemoveLeaf only modifies the leaf in this case (no borrow/merge needed).
        int count = node.GetCount(ref accessor);
        if (count > node.GetCapacity() / 2 || ctx.Depth == 0)
        {
            bool fastMerged = node.RemoveLeaf(ref args, ref relatives, ref accessor);
            node.GetLatch(ref accessor).WriteUnlock();
            completed = true;
            return fastMerged;
        }

        // Slow path: leaf may underflow → need neighbors + path for borrow/merge.
        // Lock leaf neighbors for potential borrow or merge.
        // Sibling pages loaded into sibling CA to avoid evicting parent path pages from primary CA.
        // AbortWriteLock on failure: no nodes modified yet — avoid spurious version bumps.
        var leafPrev = node.GetPrevious(ref accessor);
        var leafNext = node.GetNext(ref accessor);
        if (leafPrev.IsValid)
        {
            leafPrev.PreDirtyForWrite(ref sibAccessor);
        }
        if (leafPrev.IsValid && !leafPrev.GetLatch(ref sibAccessor).TryWriteLock())
        {
            node.GetLatch(ref accessor).AbortWriteLock();
            return false; // restart
        }
        if (leafNext.IsValid)
        {
            leafNext.PreDirtyForWrite(ref sibAccessor);
        }
        if (leafNext.IsValid && !leafNext.GetLatch(ref sibAccessor).TryWriteLock())
        {
            if (leafPrev.IsValid)
            {
                leafPrev.GetLatch(ref sibAccessor).AbortWriteLock();
            }
            node.GetLatch(ref accessor).AbortWriteLock();
            return false; // restart
        }

        // Lock path nodes bottom-up with version validation.
        // Required for ancestor key updates during borrow and merge propagation.
        // AbortWriteLock on failure: no nodes modified yet — avoid spurious version bumps.
        for (int i = ctx.Depth - 1; i >= 0; i--)
        {
            ctx.PathNodes[i].PreDirtyForWrite(ref accessor);
            var pathLatch = ctx.PathNodes[i].GetLatch(ref accessor);
            if (!pathLatch.TryWriteLock())
            {
                for (int j = i + 1; j < ctx.Depth; j++)
                {
                    ctx.PathNodes[j].GetLatch(ref accessor).AbortWriteLock();
                }
                if (leafNext.IsValid)
                {
                    leafNext.GetLatch(ref sibAccessor).AbortWriteLock();
                }
                if (leafPrev.IsValid)
                {
                    leafPrev.GetLatch(ref sibAccessor).AbortWriteLock();
                }
                node.GetLatch(ref accessor).AbortWriteLock();
                return false; // restart
            }
            if (!pathLatch.ValidateVersionLocked(ctx.PathVersions[i]))
            {
                pathLatch.AbortWriteLock();
                for (int j = i + 1; j < ctx.Depth; j++)
                {
                    ctx.PathNodes[j].GetLatch(ref accessor).AbortWriteLock();
                }
                if (leafNext.IsValid)
                {
                    leafNext.GetLatch(ref sibAccessor).AbortWriteLock();
                }
                if (leafPrev.IsValid)
                {
                    leafPrev.GetLatch(ref sibAccessor).AbortWriteLock();
                }
                node.GetLatch(ref accessor).AbortWriteLock();
                return false; // restart
            }
        }

        // All needed nodes locked — Phase 2: Remove at leaf (may borrow/merge)
        var merged = node.RemoveLeaf(ref args, ref relatives, ref accessor);

        // Phase 2.5: Mark obsolete merged leaf + unlock leaf neighbors + leaf (version bumped by WriteUnlock)
        var retireEpoch = _segment.Store.EpochManager.GlobalEpoch;
        if (merged)
        {
            Interlocked.Increment(ref _mergeCount);
            if (relatives.HasTrueLeftSibling)
            {
                // Current node was merged into its left sibling — mark current obsolete
                node.GetLatch(ref accessor).MarkObsolete();
                DeferredAdd(node.ChunkId, retireEpoch);
            }
            else if (relatives.HasTrueRightSibling && leafNext.IsValid)
            {
                // Right sibling was merged into current — mark right sibling obsolete
                leafNext.GetLatch(ref sibAccessor).MarkObsolete();
                DeferredAdd(leafNext.ChunkId, retireEpoch);
            }
        }
        if (leafNext.IsValid)
        {
            leafNext.GetLatch(ref sibAccessor).WriteUnlock();
        }
        if (leafPrev.IsValid)
        {
            leafPrev.GetLatch(ref sibAccessor).WriteUnlock();
        }
        node.GetLatch(ref accessor).WriteUnlock();

        // Phase 3: Propagate merges upward through internal nodes
        while (ctx.Depth > 0 && merged)
        {
            ctx.Depth--;
            node = ctx.PathNodes[ctx.Depth];
            relatives = ctx.PathRelatives[ctx.Depth];

            // Lock siblings that HandleChildMerge might borrow from or merge with
            NodeWrapper leftSib = relatives.GetLeftSibling(ref sibAccessor);
            NodeWrapper rightSib = relatives.GetRightSibling(ref sibAccessor);
            if (leftSib.IsValid)
            {
                leftSib.PreDirtyForWrite(ref sibAccessor);
                SpinWriteLock(leftSib.GetLatch(ref sibAccessor));
            }
            if (rightSib.IsValid)
            {
                rightSib.PreDirtyForWrite(ref sibAccessor);
                SpinWriteLock(rightSib.GetLatch(ref sibAccessor));
            }

            merged = node.HandleChildMerge(ctx.PathChildIndices[ctx.Depth], ref relatives, ref accessor, ref sibAccessor);

            // Mark obsolete internal node that was merged
            if (merged)
            {
                Interlocked.Increment(ref _mergeCount);
                if (relatives.HasTrueLeftSibling)
                {
                    // Current internal node merged into left sibling
                    node.GetLatch(ref accessor).MarkObsolete();
                    DeferredAdd(node.ChunkId, retireEpoch);
                }
                else if (relatives.HasTrueRightSibling && rightSib.IsValid)
                {
                    // Right sibling merged into current
                    rightSib.GetLatch(ref sibAccessor).MarkObsolete();
                    DeferredAdd(rightSib.ChunkId, retireEpoch);
                }
            }

            // Unlock siblings + this path node
            if (rightSib.IsValid)
            {
                rightSib.GetLatch(ref sibAccessor).WriteUnlock();
            }
            if (leftSib.IsValid)
            {
                leftSib.GetLatch(ref sibAccessor).WriteUnlock();
            }
            node.GetLatch(ref accessor).WriteUnlock();
        }

        // Phase 3.5: Unlock remaining path nodes above propagation level
        while (ctx.Depth > 0)
        {
            ctx.Depth--;
            ctx.PathNodes[ctx.Depth].GetLatch(ref accessor).WriteUnlock();
        }

        completed = true;
        return merged;
    }
}
