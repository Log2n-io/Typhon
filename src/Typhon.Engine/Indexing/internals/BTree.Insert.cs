// unset

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

internal abstract partial class BTree<TKey, TStore>
{
    /// <summary>Result of an OLC insert attempt.</summary>
    private enum OlcInsertResult
    {
        /// <summary>Insert completed successfully.</summary>
        Completed,
        /// <summary>OLC validation failed — caller should retry or fall back.</summary>
        Restart,
        /// <summary>Target leaf is full — needs pessimistic path for split/spill.</summary>
        LeafFull,
    }
    
    /// <summary>
    /// True when <paramref name="key"/> sits BELOW the lower bound that routes to <paramref name="leaf"/> — i.e. the descent that chose this leaf is stale and
    /// the insert must restart rather than proceed.
    /// </summary>
    /// <remarks>
    /// Inserting a key below the bound that routes to a leaf lands it at slot 0, drops the leaf's first key below that bound, and no insert path updates the
    /// ancestor — so descent for the new key then routes LEFT of the separator and never reaches the leaf. The key is counted by IncCount, sits in a correctly
    /// chained leaf, and is unreachable. That is #297/#679's mode 1, measured: `separator=408 -> child=5 firstKey=226`.
    /// <para>
    /// Every insert path already validated the UPPER bound — the OLC general path checks <c>key >= HighKey</c>, the append fast paths check
    /// <c>!GetNext().IsValid</c>, <c>InsertIterative</c> has its move-right gap check — and not one validated the lower bound. This is that missing half,
    /// stated once and applied at both sites, because a rule enforced at one call site is a rule enforced at one call site.
    /// </para>
    /// <para>
    /// The bound is the PREVIOUS leaf's <c>HighKey</c>, not this leaf's first key. Those coincide only immediately after a split. Removing a leaf's first key
    /// raises its minimum and leaves the separator where it was, so the band <c>prevHighKey &lt;= key &lt; firstKey</c> is a legitimate destination — the leaf
    /// IS correct and the insert lowers its minimum back toward the separator that already routes to it. This is the same one-sided slack
    /// <c>ValidateLeafSeparators</c> deliberately tolerates, and the first version of this guard contradicted it by testing <c>key &lt; firstKey</c>: a plain
    /// single-threaded remove-then-reinsert of any leaf's first key restarted forever and died on <c>MaxPessimisticRestarts</c> with no contention involved
    /// (<c>RemoveThenReinsertLeafFirstKey_DoesNotStallInsert</c>). <c>HighKey</c> is the exclusive upper bound the whole B-link descent already steers by, so
    /// reading it here asks the same question the descent asked, rather than a stricter one.
    /// </para>
    /// <para>
    /// A leaf with no previous sibling is the tree's leftmost and is reached through the left POINTER, which carries no separator — lowering its first key is
    /// legitimate and is what the prepend fast paths exist to do. The two extra chunk reads (previous leaf + its HighKey) are paid only after the first-key
    /// comparison has already failed, so the overwhelmingly common in-range insert still costs one <c>GetCount</c>, one <c>GetFirst</c> and one compare.
    /// </para>
    /// <para>
    /// Restarting is safe against livelock because both callers are bounded: the OLC loop by <c>MaxOptimisticRestarts</c> (then the pessimistic path), and the
    /// pessimistic loop by <c>MaxPessimisticRestarts</c>, which throws rather than spins — the guard #695 put in place for exactly this shape. That bound is
    /// what turned the defect above into a 2.5-minute throw instead of a hang, and it is why the message names liveness rather than contention.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool KeyBelowLeafLowerBound(NodeWrapper leaf, TKey key, IComparer<TKey> comparer, ref ChunkAccessor<TStore> accessor)
    {
        if (leaf.GetCount(ref accessor) == 0 || CompareKeys(key, leaf.GetFirst(ref accessor).Key, comparer) >= 0)
        {
            return false;
        }

        var previous = leaf.GetPrevious(ref accessor);
        return previous.IsValid && CompareKeys(key, previous.GetHighKey(ref accessor), comparer) < 0;
    }

    /// <summary>
    /// True when <paramref name="key"/> reaches at or past <paramref name="leaf"/>'s exclusive <c>HighKey</c>, i.e. the key's range belongs to a leaf further
    /// right and writing it here would place it past its own separator.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="KeyBelowLeafLowerBound"/>. This condition was already being tested inline on the OLC general insert path; naming it is what lets
    /// a second write path ask the same question instead of re-deriving it — <c>Move</c> asked it nowhere and that is #765's first finding.
    /// <para>
    /// Conditioned on a valid right sibling because the rightmost leaf's <c>HighKey</c> bounds nothing: it owns every key above its separator, which is what the
    /// append fast paths rely on.
    /// </para>
    /// <para>
    /// The <c>HighKey</c> comparison is tested BEFORE the right-sibling read, and the order is deliberate. Both conjuncts are required, so the result is
    /// identical either way, but the overwhelmingly common answer is "no, the key is in range" — and in that case this order short-circuits after one
    /// <c>GetHighKey</c> instead of paying a <c>GetNext</c> first. Every node access here is a virtual <c>BaseNodeStorage</c> call, so dropping one from the
    /// hot answer is worth more than it looks (#765 S8).
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool KeyAboveLeafUpperBound(NodeWrapper leaf, TKey key, IComparer<TKey> comparer, ref ChunkAccessor<TStore> accessor)
        => leaf.GetCount(ref accessor) > 0
           && CompareKeys(key, leaf.GetHighKey(ref accessor), comparer) >= 0
           && leaf.GetNext(ref accessor).IsValid;

    /// <summary>
    /// True when <paramref name="leaf"/> does not own <paramref name="key"/>'s range and therefore must not receive a write of it. Callers restart.
    /// </summary>
    /// <remarks>
    /// "Leaf authority" is the single question every write path has to answer before it mutates: is THIS the leaf the descent's separators claim owns this key?
    /// Both halves are needed and each was learned separately — the upper bound from concurrent splits, the lower bound from #297/#679 mode 1 and then corrected
    /// in #740 — so stating them as one predicate is the point. A path that asks only one half is the shape every one of those defects had.
    /// <para>
    /// Cheap by construction: the common in-range case pays one <c>GetCount</c>, one <c>GetNext</c>, one <c>GetHighKey</c> and one <c>GetFirst</c> with two
    /// compares, and the extra sibling reads happen only once a bound has already failed.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool KeyOutsideLeafAuthority(NodeWrapper leaf, TKey key, IComparer<TKey> comparer, ref ChunkAccessor<TStore> accessor)
        => KeyAboveLeafUpperBound(leaf, key, comparer, ref accessor) || KeyBelowLeafLowerBound(leaf, key, comparer, ref accessor);

    /// <summary>Creates the insert value, handling AllowMultiple buffer creation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CreateInsertValue(ref InsertArguments args, ref ChunkAccessor<TStore> accessor)
    {
        if (AllowMultiple)
        {
            // VSBS buffer operations use SiblingAccessor to avoid evicting the leaf node's
            // slot from the primary CA's 16-slot cache.
            ref var bufferAccessor = ref args.SiblingAccessor;
            var bufferId = _storage.CreateBuffer(ref bufferAccessor);

            // Phase 6: Data:Index:BTree:BulkInsert span — wraps the multi-value VSBS buffer append.
            var bulkScope = TyphonEvent.BeginDataIndexBTreeBulkInsert(bufferId, 1);
            try
            {
                args.ElementId = _storage.Append(bufferId, args.GetValue(), ref bufferAccessor);
            }
            finally
            {
                bulkScope.Dispose();
            }
            args.BufferRootId = bufferId;
            return bufferId;
        }
        return args.GetValue();
    }
    private void AddOrUpdateCore(ref InsertArguments args)
    {
        ref var accessor = ref args.Accessor;

        // 1. Empty tree initialization.
        //    An empty tree has no root node, so OLC readers/writers have nothing to latch on.
        //    CAS on _rootChunkId atomically races to claim the init slot; loser sees non-zero root and proceeds.
        //
        // Issue #297: hold newRoot's OLC write lock around the initial PushLast and metadata writes. Without the lock, a concurrent thread that observes the
        // just-published _rootChunkId and races through TryInsertOlc's general path can acquire newRoot's latch (initial OlcVersion=4 reports unlocked) and
        // concurrently mutate the chunk, producing torn writes / out-of-order keys. SpinWriteLock takes newRoot's bit-0 lock; ReadVersion observes 0 for any
        // concurrent reader, who restarts; WriteUnlock at the end bumps the version so the next op sees the new state.
        if (IsEmpty())
        {
            var newRoot = AllocNode(NodeStates.IsLeaf, ref accessor);
            newRoot.PreDirtyForWrite(ref accessor);
            var newRootLatch = newRoot.GetLatch(ref accessor);
            // Freshly allocated and not yet published: no other thread can have marked it obsolete, so Obsolete is unreachable here (asserted, not handled —
            // a silent skip would leave the initialisation below unprotected, which is the very race this lock exists for).
            var newRootOutcome = SpinWriteLock(newRootLatch);  // exclude any concurrent OLC reader/writer touching newRoot
            Debug.Assert(newRootOutcome != WriteLockOutcome.Obsolete, "a freshly allocated root cannot be obsolete");
            if (Interlocked.CompareExchange(ref _rootChunkId, newRoot.ChunkId, 0) == 0)
            {
                // We won the race — initialize root, LinkList, ReverseLinkList while still holding newRoot's lock. Concurrent threads that observe _rootChunkId
                // set will spin or restart on newRoot's locked latch until WriteUnlock below.
                _linkList = newRoot;
                _reverseLinkList = newRoot;
                Height++;
                int value = CreateInsertValue(ref args, ref accessor);
                newRoot.PushLast(new KeyValueItem(args.Key, value), ref accessor);
                IncCount();
                _cachedLastKey = args.Key;
                _hasCachedLastKey = true;
                newRootLatch.WriteUnlock();

                // Phase 6: Data:Index:BTree:Root instant (op=0 / Init).
                TyphonEvent.EmitDataIndexBTreeRoot(0, newRoot.ChunkId, (byte)Math.Min(Height, byte.MaxValue));
                return;
            }
            // Another thread initialized the root — release our lock (no version bump, we didn't mutate anything visible) and free our unused node.
            newRootLatch.AbortWriteLock();
            _segment.FreeChunk(newRoot.ChunkId);
        }

        // 2. OLC retry loop — handles append/prepend fast paths + non-full leaf inserts.
        //    Zero writes to shared state except the single leaf being modified (WriteLocked).
        byte fallbackReason = 1;  // OlcFail (default) — overwritten to 0 if we break for LeafFull
        for (int attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
        {
            var result = TryInsertOlc(ref args);
            if (result == OlcInsertResult.Completed)
            {
                return;
            }
            if (result == OlcInsertResult.LeafFull)
            {
                // No counter here: the same signal is emitted a few lines below as
                // Data:Index:BTree:RebalanceFallback with reason 0, which is the channel that actually has readers.
                fallbackReason = 0;  // LeafFull
                break; // Need pessimistic path for split/spill
            }
            // Restart: version validation failed
            Interlocked.Increment(ref _optimisticRestarts);
        }

        // 3. Pessimistic fallback — exclusive lock + WriteLock all modified nodes for OLC readers
        Interlocked.Increment(ref _pessimisticFallbacks);
        // Phase 6: Data:Index:BTree:RebalanceFallback instant — reason byte distinguishes LeafFull (0) vs OLC retry budget exhausted (1).
        TyphonEvent.EmitDataIndexBTreeRebalanceFallback(fallbackReason);
        AddOrUpdateCorePessimistic(ref args);
    }

    /// <summary>
    /// OLC insert attempt: tries append/prepend fast paths and non-full leaf insert.
    /// Only modifies a single leaf node (WriteLocked). Returns LeafFull when the target leaf is full and needs split/spill (which requires the pessimistic path).
    /// </summary>
    private OlcInsertResult TryInsertOlc(ref InsertArguments args)
    {
        ref var accessor = ref args.Accessor;

        // --- Append fast path: insert at end of rightmost leaf ---
        var rl = _reverseLinkList;
        if (rl.IsValid)
        {
            var latch = rl.GetLatch(ref accessor);
            var version = latch.ReadVersion();
            if (version != 0)
            {
                // Fast path: use cached last key (field read ~1ns vs 3 chunk accessor calls ~35ns).
                // Safe even if stale: ValidateVersionLocked after write-lock catches any inconsistency.
                TKey lastKey = default;
                bool tryAppend;
                if (_hasCachedLastKey)
                {
                    lastKey = _cachedLastKey;
                    tryAppend = true;
                }
                else
                {
                    int rlCount = rl.GetCount(ref accessor);
                    tryAppend = rlCount > 0;
                    if (tryAppend)
                    {
                        // Use GetItem directly with known count — avoids redundant GetCount inside GetLast
                        lastKey = rl.GetItem(rlCount - 1, ref accessor).Key;
                    }
                }

                if (tryAppend)
                {
                    if (!latch.ValidateVersion(version))
                    {
                        return OlcInsertResult.Restart;
                    }

                    int order = args.Compare(args.Key, lastKey);
                    if (order > 0)
                    {
                        rl.PreDirtyForWrite(ref accessor);
                        if (!latch.TryWriteLock())
                        {
                            return OlcInsertResult.Restart;
                        }
                        if (!latch.ValidateVersionLocked(version))
                        {
                            latch.AbortWriteLock();
                            return OlcInsertResult.Restart;
                        }
                        // Safety: if rl was split concurrently, it's no longer the rightmost leaf.
                        // GetNext().IsValid means a new right sibling exists — abort and fall through.
                        bool isFull = rl.GetIsFull(ref accessor);
                        if (isFull || rl.GetNext(ref accessor).IsValid)
                        {
                            latch.AbortWriteLock();
                            return isFull ? OlcInsertResult.LeafFull : OlcInsertResult.Restart;
                        }
                        // Issue #297: re-verify ordering against the actual leaf content under the lock. The pre-lock check above used `lastKey` which can come
                        // from `_cachedLastKey` — a per-tree global updated on every successful append, but NOT atomically with `_reverseLinkList`. After a
                        // split, a concurrent reader can observe the new `_reverseLinkList` while still seeing the old cached last key (or vice versa), making
                        // the cached `args.Key > lastKey` check pass when actually `args.Key <= rl.GetLast().Key`. Without this guard, PushLast appends out of
                        // order, and binary-search lookups silently miss the misplaced key.
                        if (rl.GetCount(ref accessor) > 0 && args.Compare(args.Key, rl.GetLast(ref accessor).Key) <= 0)
                        {
                            latch.AbortWriteLock();
                            return OlcInsertResult.Restart;
                        }
                        int value = CreateInsertValue(ref args, ref accessor);
                        rl.PushLast(new KeyValueItem(args.Key, value), ref accessor);
                        _cachedLastKey = args.Key;
                        _hasCachedLastKey = true;
                        latch.WriteUnlock();
                        IncCount();
                        return OlcInsertResult.Completed;
                    }

                    if (order == 0)
                    {
                        if (AllowMultiple)
                        {
                            rl.PreDirtyForWrite(ref accessor);
                            if (!latch.TryWriteLock())
                            {
                                return OlcInsertResult.Restart;
                            }
                            if (!latch.ValidateVersionLocked(version))
                            {
                                latch.AbortWriteLock();
                                return OlcInsertResult.Restart;
                            }
                            // Issue #297: re-verify the duplicate-key claim under the lock — `lastKey` came from `_cachedLastKey` which can be stale after a
                            // split. Without this, we'd append the value to the wrong buffer (or to a buffer whose key isn't actually the same as args.Key).
                            var actualLast = rl.GetCount(ref accessor) > 0 ? rl.GetLast(ref accessor) : default;
                            if (rl.GetCount(ref accessor) == 0 || args.Compare(args.Key, actualLast.Key) != 0)
                            {
                                latch.AbortWriteLock();
                                return OlcInsertResult.Restart;
                            }
                            var bufferRootId = actualLast.Value;
                            args.ElementId = _storage.Append(bufferRootId, args.GetValue(), ref args.SiblingAccessor);
                            args.BufferRootId = bufferRootId;
                            latch.WriteUnlock();
                            return OlcInsertResult.Completed;
                        }
                        ThrowHelper.ThrowUniqueConstraintViolation();
                    }
                }
            }
        }

        // --- Prepend fast path: insert at beginning of leftmost leaf ---
        var ll = _linkList;
        if (ll.IsValid)
        {
            var llLatch = ll.GetLatch(ref accessor);
            var llVersion = llLatch.ReadVersion();
            if (llVersion != 0)
            {
                int llCount = ll.GetCount(ref accessor);
                if (llCount > 0)
                {
                    var firstKey = ll.GetFirst(ref accessor).Key;
                    if (!llLatch.ValidateVersion(llVersion))
                    {
                        return OlcInsertResult.Restart;
                    }

                    int order = args.Compare(args.Key, firstKey);
                    if (order < 0)
                    {
                        ll.PreDirtyForWrite(ref accessor);
                        if (!llLatch.TryWriteLock())
                        {
                            return OlcInsertResult.Restart;
                        }
                        if (!llLatch.ValidateVersionLocked(llVersion))
                        {
                            llLatch.AbortWriteLock();
                            return OlcInsertResult.Restart;
                        }
                        if (ll.GetIsFull(ref accessor))
                        {
                            llLatch.WriteUnlock();
                            return OlcInsertResult.LeafFull;
                        }
                        // #297 mode 1: `_linkList` is a cached field, so it can name a leaf that is no longer the leftmost. Pushing a smaller key to the front
                        // of an INTERIOR leaf drops its first key below the separator routing to it, with no ancestor update — see KeyBelowLeafLowerBound.
                        if (ll.GetPrevious(ref accessor).IsValid)
                        {
                            llLatch.AbortWriteLock();
                            return OlcInsertResult.Restart;
                        }
                        int value = CreateInsertValue(ref args, ref accessor);
                        ll.PushFirst(new KeyValueItem(args.Key, value), ref accessor);
                        llLatch.WriteUnlock();
                        IncCount();
                        return OlcInsertResult.Completed;
                    }

                    if (order == 0)
                    {
                        if (AllowMultiple)
                        {
                            ll.PreDirtyForWrite(ref accessor);
                            if (!llLatch.TryWriteLock())
                            {
                                return OlcInsertResult.Restart;
                            }
                            if (!llLatch.ValidateVersionLocked(llVersion))
                            {
                                llLatch.AbortWriteLock();
                                return OlcInsertResult.Restart;
                            }
                            var bufferRootId = ll.GetFirst(ref accessor).Value;
                            args.ElementId = _storage.Append(bufferRootId, args.GetValue(), ref args.SiblingAccessor);
                            args.BufferRootId = bufferRootId;
                            llLatch.WriteUnlock();
                            return OlcInsertResult.Completed;
                        }
                        ThrowHelper.ThrowUniqueConstraintViolation();
                    }
                }
            }
        }

        // --- General path: optimistic descent to leaf, non-full insert ---
        // followRightLink: false — inserts must not follow B-link because inserting into the right sibling bypasses the spill/split path that updates parent
        // separators. If the leaf was split concurrently, version validation will trigger a restart.
        var (leafChunkId, leafVersion, _) = OptimisticDescendToLeaf(args.Key, ref accessor, false);
        if (leafChunkId == 0)
        {
            return OlcInsertResult.Restart;
        }

        var leaf = new NodeWrapper(_storage, leafChunkId);
        leaf.PreDirtyForWrite(ref accessor);
        var leafLatch = leaf.GetLatch(ref accessor);
        if (!leafLatch.TryWriteLock())
        {
            return OlcInsertResult.Restart;
        }
        if (!leafLatch.ValidateVersionLocked(leafVersion))
        {
            leafLatch.AbortWriteLock();
            return OlcInsertResult.Restart;
        }
        // Range check: stale separator may route to wrong leaf after a concurrent split.
        // High key is an exclusive upper bound, so key >= highKey means we're out of range.
        if (KeyAboveLeafUpperBound(leaf, args.Key, args.KeyComparer, ref accessor))
        {
            leafLatch.WriteUnlock();
            return OlcInsertResult.Restart;
        }
        // #297 mode 1: and the same check on the LOWER bound, which this path never had. Above only rejects a key past the leaf's high key; a key below its
        // FIRST key is equally out of range and inserting it silently invalidates the separator. AbortWriteLock, not WriteUnlock — nothing was modified.
        if (KeyBelowLeafLowerBound(leaf, args.Key, args.KeyComparer, ref accessor))
        {
            leafLatch.AbortWriteLock();
            return OlcInsertResult.Restart;
        }
        if (leaf.GetIsFull(ref accessor))
        {
            leafLatch.WriteUnlock();
            return OlcInsertResult.LeafFull;
        }

        // Re-search under lock (key positions may have shifted since optimistic read)
        var keyIndex = leaf.Find(args.Key, args.KeyComparer, ref accessor);
        if (keyIndex < 0)
        {
            keyIndex = ~keyIndex;
            int value = CreateInsertValue(ref args, ref accessor);
            leaf.Insert(keyIndex, new KeyValueItem(args.Key, value), ref accessor);
            leafLatch.WriteUnlock();
            IncCount();
            return OlcInsertResult.Completed;
        }

        // Key already exists
        if (AllowMultiple)
        {
            var curItem = leaf.GetItem(keyIndex, ref accessor);
            args.ElementId = _storage.Append(curItem.Value, args.GetValue(), ref args.SiblingAccessor);
            args.BufferRootId = curItem.Value;
            leafLatch.WriteUnlock();
            return OlcInsertResult.Completed;
        }
        leafLatch.WriteUnlock();
        ThrowHelper.ThrowUniqueConstraintViolation();
        return OlcInsertResult.Restart; // unreachable — ThrowHelper always throws
    }

    /// <summary>
    /// Pessimistic insert fallback: uses InsertIterative with latch-coupled SMO.
    /// No global lock — concurrency is handled by per-node OLC latches.
    /// </summary>
    private void AddOrUpdateCorePessimistic(ref InsertArguments args)
    {
        try
        {
            ref var accessor = ref args.Accessor;

            // Issue #297/#679: the twin of AddOrUpdateCore's empty-tree initialisation, and it needs that one's CAS for the same reason. The trigger cannot be
            // `IsEmpty()`: that tests the ENTRY COUNT, and the winner of the OLC init publishes `_rootChunkId` several instructions BEFORE its IncCount(). A
            // writer that loses the OLC CAS, burns MaxOptimisticRestarts against the winner's still-locked leaf and lands here inside that window observes
            // count==0 with a live root — and the unconditional `Root = AllocNode(...)` this replaces then republished a fresh EMPTY leaf over it. The winner's
            // root and the key in it were orphaned (counted by EntryCount, reachable from nothing), and Height was left permanently one too high: two
            // increments, one level. Test on root existence and CAS the publication, so the loser frees its node exactly as the OLC path's loser does.
            //
            // Both symptoms outlive the microsecond that produced them, which is why this read as an insert-path defect for so long: the key is simply absent
            // from a structurally flawless tree, and the height drift persists into a large tree where it trips CheckConsistency's FIRST assertion and aborts
            // the walk before any separator is examined.
            if (_rootChunkId == 0)
            {
                var newRoot = AllocNode(NodeStates.IsLeaf, ref accessor);
                if (Interlocked.CompareExchange(ref _rootChunkId, newRoot.ChunkId, 0) == 0)
                {
                    _linkList = newRoot;
                    _reverseLinkList = newRoot;
                    Height++;
                }
                else
                {
                    // Another writer published a root first — drop ours rather than overwrite theirs.
                    Interlocked.Increment(ref _emptyInitRacesLost);
                    _segment.FreeChunk(newRoot.ChunkId);
                }
            }

            // Append fast path: lock the last leaf and insert if key > lastKey and leaf not full.
            // Bypass when leaf is contended and sufficiently populated — fall through to InsertIterative which has the path recording needed for contention
            // split propagation. Capture local refs to avoid races from concurrent ReverseLinkList/LinkList updates. Issue #297: skip the append fast-path
            // entirely when `_reverseLinkList` is not yet published. The OLC empty-tree CAS at AddOrUpdateCore writes _rootChunkId
            // BEFORE _linkList/_reverseLinkList — a concurrent thread that won the race to enter the pessimistic path can therefore observe `IsEmpty()=false`
            // (rootChunkId set) while `rl=default`, making `rl.GetLast()` NRE on `_storage`. CAUTION: capture the field into a local FIRST, then test IsValid
            // on the local. Testing the field directly and assigning separately is a TOCTOU race (concurrent thread can swap _reverseLinkList between the two
            // reads, leaving `rl=default` even though the if-check passed). The whole block must be guarded.
            {
                var rl = _reverseLinkList;
                if (rl.IsValid)
                {
                    var bypassAppendFastPath = rl.GetContentionHint(ref accessor) >= ContentionSplitThreshold && rl.GetCount(ref accessor) > rl.GetCapacity() / 2;
                    var order = IsEmpty() ? 1 : args.Compare(args.Key, _hasCachedLastKey ? _cachedLastKey : rl.GetLast(ref accessor).Key);
                    if (!bypassAppendFastPath && order > 0 && !rl.GetIsFull(ref accessor))
                    {
                        rl.PreDirtyForWrite(ref accessor);
                        var rlLatch = rl.GetLatch(ref accessor);
                        // #716: `_reverseLinkList` is a cached pointer, not a path through the tree, so it can name a leaf a concurrent merge already detached
                        // — and this fast path re-checks business conditions (not full, no right sibling, key ordering), every one of which a detached node can
                        // satisfy. On obsolete no lock is held (so no AbortWriteLock): fall through to the general path, which re-descends from the root.
                        if (SpinWriteLock(rlLatch) == WriteLockOutcome.Obsolete)
                        {
                            Interlocked.Increment(ref _obsoleteRestarts);
                        }
                        else
                        {
                            // Re-validate under lock: leaf may now be full, another writer inserted a larger key,
                            // or a concurrent split made this leaf no longer the rightmost (GetNext becomes valid).
                            if (!rl.GetIsFull(ref accessor) && !rl.GetNext(ref accessor).IsValid && args.Compare(args.Key, rl.GetLast(ref accessor).Key) > 0)
                            {
                                int value = CreateInsertValue(ref args, ref accessor);
                                rl.PushLast(new KeyValueItem(args.Key, value), ref accessor);
                                _cachedLastKey = args.Key;
                                _hasCachedLastKey = true;
                                rlLatch.WriteUnlock();
                                IncCount();
                                return;
                            }
                            rlLatch.AbortWriteLock();
                        }
                        // Fall through to general path
                    }
                    else if (order == 0 && AllowMultiple)
                    {
                        rl.PreDirtyForWrite(ref accessor);
                        var rlLatch = rl.GetLatch(ref accessor);
                        if (SpinWriteLock(rlLatch) == WriteLockOutcome.Obsolete)   // #716 — see the append fast path above
                        {
                            Interlocked.Increment(ref _obsoleteRestarts);
                        }
                        else
                        {
                            var lastEntry = rl.GetLast(ref accessor);
                            if (args.Compare(args.Key, lastEntry.Key) == 0)
                            {
                                args.ElementId = _storage.Append(lastEntry.Value, args.GetValue(), ref args.SiblingAccessor);
                                args.BufferRootId = lastEntry.Value;
                                rlLatch.WriteUnlock();
                                return;
                            }
                            rlLatch.AbortWriteLock();
                        }
                        // Fall through
                    }
                    else if (order == 0)
                    {
                        ThrowHelper.ThrowUniqueConstraintViolation();
                    }
                }
            }

            // Prepend fast path: lock the first leaf and insert if key < firstKey and leaf not full.
            // Issue #297: same publication-ordering caveat as the append path above — capture ll into a local first, then test IsValid on the
            // local (avoiding TOCTOU race).
            if (!IsEmpty())
            {
                var ll = _linkList;
                if (ll.IsValid)
                {
                    int order = args.Compare(args.Key, ll.GetFirst(ref accessor).Key);
                    if (order < 0 && !ll.GetIsFull(ref accessor))
                    {
                        ll.PreDirtyForWrite(ref accessor);
                        var llLatch = ll.GetLatch(ref accessor);
                        if (SpinWriteLock(llLatch) == WriteLockOutcome.Obsolete)   // #716 — `_linkList` is a cached pointer, same hazard as `_reverseLinkList`
                        {
                            Interlocked.Increment(ref _obsoleteRestarts);
                        }
                        else
                        {
                            // #297: `ll.GetPrevious()` must be checked, and its absence here was the defect. This path lowers the leaf's FIRST key, and a leaf's
                            // first key is what its parent separator holds. That is sound only for the tree's leftmost leaf, which hangs off the left POINTER
                            // and has no separator — so nothing needs updating. `_linkList` is a cached field, though, and a concurrent split can create a new
                            // leftmost leaf (or the field can simply be observed stale), leaving `ll` an INTERIOR leaf reached through a separator. Pushing a
                            // smaller key to its front then drops its first key below that separator with no ancestor update, and descent for the new key
                            // routes left of the separator and never reaches the leaf: the key is counted by IncCount and unreachable. Measured signature —
                            // `separator=1054 -> leaf firstKey=1049`, the key present in a chained leaf, TryGet failing.
                            //
                            // The other three cached-pointer fast paths already guard this: the append path re-checks `!rl.GetNext().IsValid`, and both Remove
                            // fast paths bail on a valid Previous/Next. The Remove BEGIN path's comment even names this exact hazard. Only this one was left.
                            if (!ll.GetIsFull(ref accessor) && !ll.GetPrevious(ref accessor).IsValid
                                                            && args.Compare(args.Key, ll.GetFirst(ref accessor).Key) < 0)
                            {
                                int value = CreateInsertValue(ref args, ref accessor);
                                ll.PushFirst(new KeyValueItem(args.Key, value), ref accessor);
                                llLatch.WriteUnlock();
                                IncCount();
                                return;
                            }
                            llLatch.AbortWriteLock();
                        }
                        // Fall through
                    }
                    else if (order == 0 && AllowMultiple)
                    {
                        ll.PreDirtyForWrite(ref accessor);
                        var llLatch = ll.GetLatch(ref accessor);
                        if (SpinWriteLock(llLatch) == WriteLockOutcome.Obsolete)   // #716 — see the prepend fast path above
                        {
                            Interlocked.Increment(ref _obsoleteRestarts);
                        }
                        else
                        {
                            var firstEntry = ll.GetFirst(ref accessor);
                            if (args.Compare(args.Key, firstEntry.Key) == 0)
                            {
                                args.ElementId = _storage.Append(firstEntry.Value, args.GetValue(), ref args.SiblingAccessor);
                                args.BufferRootId = firstEntry.Value;
                                llLatch.WriteUnlock();
                                return;
                            }
                            llLatch.AbortWriteLock();
                        }
                        // Fall through
                    }
                    else if (order == 0)
                    {
                        ThrowHelper.ThrowUniqueConstraintViolation();
                    }
                }
            }

            // General path with latch-coupled SMO — retry on lock contention
            // InsertIterative handles root splits internally under the root's write lock.
            SpinWait spin = default;
            for (int attempt = 0; ; attempt++)
            {
                InsertIterative(ref args, ref accessor, out bool insertCompleted);
                if (insertCompleted)
                {
                    break;
                }
                Interlocked.Increment(ref _optimisticRestarts);
                if (attempt >= MaxPessimisticRestarts)
                {
                    // #695: this loop used to be `while (true)`. See MaxPessimisticRestarts for why exhausting it means retrying cannot help.
                    ThrowHelper.ThrowInvalidOp(
                        $"B+Tree insert made no progress in {MaxPessimisticRestarts} pessimistic retries. The descent keeps reaching a leaf it can neither "
                        + "validate nor modify, which no further retrying resolves. This is a liveness defect in the tree, not contention (see #695).");
                }
                spin.SpinOnce();
            }

            if (args.Added)
            {
                IncCount();
            }
            else if (!AllowMultiple)
            {
                ThrowHelper.ThrowUniqueConstraintViolation();
            }

            // Issue #297: snapshot the field once and guard on IsValid. AddOrUpdateCore writes _rootChunkId via CAS BEFORE writing _linkList/_reverseLinkList;
            // on x86 TSO, another thread can observe IsEmpty()=false while still seeing _reverseLinkList=default. Skip the cache update on that race — the
            // next Add will re-read and refresh.
            {
                var tail = _reverseLinkList;
                if (tail.IsValid)
                {
                    var next = tail.GetNext(ref accessor);
                    if (next.IsValid)
                    {
                        _reverseLinkList = next;
                        tail = next;
                    }
                    _cachedLastKey = tail.GetLast(ref accessor).Key;
                    _hasCachedLastKey = true;
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
    /// Iterative insert with latch-coupled SMO: descends optimistically recording PathVersions, then locks bottom-up only as needed for structural modifications.
    /// Fast path (leaf not full): locks only the leaf node.
    /// Slow path (leaf full, new key): locks leaf + neighbors + path nodes with version validation.
    /// Returns null if no root split, non-null promoted key if root split needed.
    /// Sets <paramref name="completed"/> to false when lock acquisition fails and caller must retry.
    /// </summary>
    private void InsertIterative(ref InsertArguments args, ref ChunkAccessor<TStore> accessor, out bool completed)
    {
        completed = false; // descent
        MutationContext ctx = default;
        var relatives = new NodeRelatives();
        ref var sibAccessor = ref args.SiblingAccessor;

        // Phase 1: Descend from root to leaf, recording path + PathVersions for validation. Shared verbatim with RemoveIterative — see DescendRecordingPath.
        if (!DescendRecordingPath(args.Key, args.KeyComparer, OlcDescentTrace.OpInsert, ref ctx, ref relatives, ref accessor, ref sibAccessor, out var node))
        {
            return;
        }

        // Phase 1.5A: Lock leaf with version validation.
        // Between Phase 1 descent and lock acquisition, a concurrent writer may have split/modified this leaf. Snapshot the version before locking,
        // then validate after. // INSIDE leaf PreDirtyForWrite — page-cache admission, blocks without spinning
        node.PreDirtyForWrite(ref accessor); // leaf lock
        var leafLatch = node.GetLatch(ref accessor);
        int leafVersion = leafLatch.ReadVersion();
        if (leafVersion == 0)
        {
            // ReadVersion() returns 0 for LOCKED and for OBSOLETE alike, and the two need opposite treatment (IXS-03). Locked is transient: wait for the
            // holder, then restart with a fresh baseline. Obsolete is permanent — the node was replaced by a structure modification and will never become
            // valid — so do NOT take its write lock (that is #716's hazard: writing into a detached node) and do not wait on it. Restart immediately and let
            // the descent find the live node. #695 came from treating both as "retry": the caller's loop had no bound, so an obsolete leaf spun forever.
            if (!leafLatch.IsObsolete)
            {
                // It can still turn obsolete between that test and this acquisition — the holder may BE the merge. SpinWriteLock reports that instead of
                // waiting for a lock that will never be grantable, and there is then nothing to abort.
                if (SpinWriteLock(leafLatch) != WriteLockOutcome.Obsolete)
                {
                    leafLatch.AbortWriteLock(); // release without version bump (we didn't modify anything)
                }
            }
            return;
        }
        var leafOutcome = SpinWriteLock(leafLatch);
        if (leafOutcome == WriteLockOutcome.Obsolete)
        {
            Interlocked.Increment(ref _obsoleteRestarts);
            return; // completed=false → outer retry re-descends and finds the live leaf
        }
        bool leafAcquiredClean = leafOutcome == WriteLockOutcome.Acquired;
        if (!leafLatch.ValidateVersionLocked(leafVersion))
        {
            leafLatch.AbortWriteLock(); // release without version bump — leaf was modified, not by us
            return;
        }

        // Update contention hint: saturating counter for detecting hot leaves
        {
            int hint = node.GetContentionHint(ref accessor);
            if (!leafAcquiredClean)
            {
                node.SetContentionHint(Math.Min(hint + 1, 255), ref accessor);
            }
            else if (hint > 0)
            {
                node.SetContentionHint(hint - 1, ref accessor);
            }
        }

        // B-link move_right (Lehman & Yao): if the key is beyond this leaf's range, a concurrent split moved some keys to a right sibling. Chain right using
        // lock coupling (lock next before releasing current) until we find the correct leaf. Forward progress is guaranteed:
        // all movement is strictly rightward with no cycle, and SpinWriteLock waits for busy siblings. // move-right
        bool movedRight = false;
        // The loop condition IS the upper-bound half of leaf authority, and this was its fourth longhand copy. The pessimistic path answers it differently from
        // the optimistic one — it walks right until the leaf owns the key, rather than restarting, because mid-SMO it has no restart point — but the QUESTION is
        // the same one, and a question asked in four places is a question that will eventually be asked four different ways (#765 S2).
        while (KeyAboveLeafUpperBound(node, args.Key, args.KeyComparer, ref accessor))
        {
            Interlocked.Increment(ref _moveRightCount);
            var nextNode = node.GetNext(ref accessor);
            nextNode.PreDirtyForWrite(ref accessor);
            // #716, and the sharpest instance of it: the B-link right chain is followed WITHOUT consulting the parent, so a merge that detached `nextNode` is
            // invisible here. Before this check the chain terminated in a write into that detached node — a key inserted, counted, and unreachable from the
            // root, which is #297's and #679's symptom exactly. Release the leaf we hold and restart from the root; the next pass sees a closed-up chain.
            if (SpinWriteLock(nextNode.GetLatch(ref accessor)) == WriteLockOutcome.Obsolete)
            {
                Interlocked.Increment(ref _obsoleteRestarts);
                node.GetLatch(ref accessor).AbortWriteLock();
                return; // completed=false → outer retry
            }

            // Gap check: after locking next leaf, verify key belongs there.
            // Without this, move_right chains across subtree boundaries when the key space has gaps (e.g., leaves [14-26] → [201-213] with no intermediate leaves).
            // Key 27 would land in [201-213] where it doesn't belong → BST violation.
            if (nextNode.GetCount(ref accessor) > 0 &&
                args.Compare(args.Key, nextNode.GetFirst(ref accessor).Key) < 0)
            {
                nextNode.GetLatch(ref accessor).AbortWriteLock();
                // Issue #297: a gap means K lies between node.HighKey and nextNode.firstKey, i.e., not in EITHER leaf. The historical fix here `break`d and
                // let the next code path insert K into `node`, which silently violates node.HighKey — future searches use the parent's separator (which still
                // routes K to the next sibling/ or beyond) and never reach our (now invariant-violating) leaf. The key is lost. Treat the gap as a transient
                // structural inconsistency (a concurrent split's separator hasn't propagated up yet): release node and restart from the root. NOTE: requires
                // storage to maintain B-link invariant node.HighKey == nextNode.firstKey across splits/merges — L16/L32/L64 + String64 (since #297) all do.
                node.GetLatch(ref accessor).AbortWriteLock();
                return; // completed=false → outer retry, next pass should see a closed-up tree.
            }

            node.GetLatch(ref accessor).AbortWriteLock();    // release current
            node = nextNode;
            movedRight = true;
        }

        // #297 mode 1: the lower-bound half of the move-right gap check above. That one rejects a key past this leaf's range on the RIGHT; this rejects one
        // below its first key, which InsertLeaf would place at slot 0 — dropping the leaf's first key under the separator that routes to it, with no ancestor
        // update from either the non-full path or the split path below. See KeyBelowLeafLowerBound. Nothing has been modified yet, so abort without a version
        // bump and let the bounded outer loop re-descend.
        if (KeyBelowLeafLowerBound(node, args.Key, args.KeyComparer, ref accessor))
        {
            node.GetLatch(ref accessor).AbortWriteLock();
            return; // completed=false → outer retry
        }

        // Fast path: leaf not full → InsertLeaf only modifies this leaf (insert or duplicate append)
        // If contention is high and leaf is sufficiently populated, fall through to contention split.
        bool itemAlreadyInserted = false;
        if (!node.GetIsFull(ref accessor))
        {
            node.InsertLeaf(ref args, ref relatives, ref accessor);
            itemAlreadyInserted = true;

            bool shouldContentionSplit = !movedRight && node.GetContentionHint(ref accessor) >= ContentionSplitThreshold
                                                     && node.GetCount(ref accessor) > node.GetCapacity() / 2;

            if (!shouldContentionSplit)
            {
                node.GetLatch(ref accessor).WriteUnlock();
                completed = true;
                return;
            }

            // Contention split: reset hint and fall through to split path
            node.SetContentionHint(0, ref accessor);
            Interlocked.Increment(ref _contentionSplitCount);
            // Fall through to split path below
        }

        // Check if key already exists in full leaf (buffer append, no structural change)
        if (!itemAlreadyInserted)
        {
            var idx = node.Find(args.Key, args.KeyComparer, ref accessor);
            if (idx >= 0)
            {
                node.InsertLeaf(ref args, ref relatives, ref accessor);
                node.GetLatch(ref accessor).WriteUnlock();
                completed = true;
                return;
            }
        }

        // After move_right, PathVersions and relatives are stale (recorded for the original leaf's path).
        // Cannot force-split here: the move-right may have crossed a subtree boundary via the leaf linked list.
        // A force-split without parent propagation would strand keys in the wrong subtree, unreachable by tree descent.
        // Return incomplete to retry from root with fresh path — stale separators are transient (resolved by the concurrent split's Phase 3 propagation
        // within microseconds).
        if (movedRight)
        {
            // #679: AbortWriteLock, NOT WriteUnlock. Nothing was modified on this path — `shouldContentionSplit` requires `!movedRight`, so arriving here means
            // the leaf was full and the item was never inserted — and WriteUnlock BUMPS the version. That bump invalidates every concurrent OLC reader and
            // writer that had validated against this leaf, forcing them to restart; with several writers repeatedly reaching this same bail they invalidate one
            // another perpetually and none converges. Measured as the stress harness's HANG: all workers alive, none blocked on a latch, all sitting at
            // pessAttempt=664 and climbing toward the MaxPessimisticRestarts throw. Releasing without the bump is what the rest of the file already does at
            // every "condition failed — didn't modify node" exit.
            node.GetLatch(ref accessor).AbortWriteLock();
            return; // completed=false — retry with fresh path from root
        }

        // Slow path: leaf full or contention split — structural modification needed.
        // For contention split, skip leafPrev lock (no spill needed — item already in, only need right neighbor for linked list).
        // On lock failure: contention split uses WriteUnlock + completed=true (item is in); regular uses AbortWriteLock + restart.
        // Sibling locking: load sibling pages into the sibling CA to avoid evicting parent path pages from the primary CA // sibling locks
        var leafPrev = itemAlreadyInserted ? default : node.GetPrevious(ref accessor);
        var leafNext = node.GetNext(ref accessor);
        if (leafPrev.IsValid)
        {
            leafPrev.PreDirtyForWrite(ref sibAccessor);
        }
        if (leafPrev.IsValid && !leafPrev.GetLatch(ref sibAccessor).TryWriteLock())
        {
            node.GetLatch(ref accessor).AbortWriteLock();
            return;
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
            if (itemAlreadyInserted)
            {
                // Contention split abort: item is already inserted, just skip the proactive split
                node.GetLatch(ref accessor).WriteUnlock();
                completed = true;
                return;
            }
            node.GetLatch(ref accessor).AbortWriteLock();
            return;
        }
 // path locks
        // Lock path nodes bottom-up with version validation.
        // Required for ancestor key updates during spill and split propagation.
        for (int i = ctx.Depth - 1; i >= 0; i--)
        {
            ctx.PathNodes[i].PreDirtyForWrite(ref accessor);
            var pathLatch = ctx.PathNodes[i].GetLatch(ref accessor);
            if (!pathLatch.TryWriteLock())
            {
                // Unlock path nodes already acquired above this level
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
                if (itemAlreadyInserted)
                {
                    node.GetLatch(ref accessor).WriteUnlock();
                    completed = true;
                    return;
                }
                node.GetLatch(ref accessor).AbortWriteLock();
                return;
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
                if (itemAlreadyInserted)
                {
                    node.GetLatch(ref accessor).WriteUnlock();
                    completed = true;
                    return;
                }
                node.GetLatch(ref accessor).AbortWriteLock();
                return;
            }
        }
 // insert/split at leaf
        // All needed nodes locked — Phase 2: Insert at leaf (may spill or split) or contention split
        KeyValueItem? promoted;
        if (itemAlreadyInserted)
        {
            // Contention split: item is already in the leaf, just redistribute via SplitLeafRight
            var rightNode = node.SplitLeafRight(ref accessor);
            promoted = new KeyValueItem(rightNode.GetFirst(ref accessor).Key, rightNode.ChunkId);
        }
        else
        {
            promoted = node.InsertLeaf(ref args, ref relatives, ref accessor);
        }

        // Phase 2.5: Unlock leaf neighbors (version bumped by WriteUnlock) — sibling CA
        if (leafNext.IsValid)
        {
            leafNext.GetLatch(ref sibAccessor).WriteUnlock();
        }
        if (leafPrev.IsValid)
        {
            leafPrev.GetLatch(ref sibAccessor).WriteUnlock();
        }
        // Defer leaf unlock if this is a root-leaf that split (need to hold lock for atomic root creation)
        if (!(ctx.Depth == 0 && promoted != null))
        {
            node.GetLatch(ref accessor).WriteUnlock();
        }
 // propagate
        // Phase 3: Propagate splits upward through internal nodes
        while (ctx.Depth > 0 && promoted != null)
        {
            ctx.Depth--;
            node = ctx.PathNodes[ctx.Depth];
            relatives = ctx.PathRelatives[ctx.Depth];

            // Lock siblings that HandlePromotedInsert might spill to (only when node is full) — sibling CA
            NodeWrapper leftSib = default, rightSib = default;
            if (node.GetIsFull(ref accessor))
            {
                leftSib = relatives.GetLeftSibling(ref sibAccessor);
                rightSib = relatives.GetRightSibling(ref sibAccessor);
                // SMO-path acquisition (#716): mid-propagation, `promoted` MUST land, so there is no restart to take. The parent (PathNodes[Depth-1]) is
                // write-locked and version-validated, so no merge can be detaching a TRUE sibling under us; the cousin case is counted, not assumed away.
                if (leftSib.IsValid)
                {
                    leftSib.PreDirtyForWrite(ref sibAccessor);
                    SpinWriteLockOnSmoPath(leftSib.GetLatch(ref sibAccessor));
                }
                if (rightSib.IsValid)
                {
                    rightSib.PreDirtyForWrite(ref sibAccessor);
                    SpinWriteLockOnSmoPath(rightSib.GetLatch(ref sibAccessor));
                }
            }

            promoted = node.HandlePromotedInsert(ctx.PathChildIndices[ctx.Depth], promoted.Value, ref relatives, ref accessor, ref sibAccessor);

            // Unlock siblings
            if (rightSib.IsValid)
            {
                rightSib.GetLatch(ref sibAccessor).WriteUnlock();
            }
            if (leftSib.IsValid)
            {
                leftSib.GetLatch(ref sibAccessor).WriteUnlock();
            }
            // Defer root unlock if root split (need to hold lock for atomic root creation)
            if (!(ctx.Depth == 0 && promoted != null))
            {
                node.GetLatch(ref accessor).WriteUnlock();
            }
        }

        // Phase 3.5: Unlock remaining path nodes above propagation level
        while (ctx.Depth > 0)
        {
            ctx.Depth--;
            ctx.PathNodes[ctx.Depth].GetLatch(ref accessor).WriteUnlock();
        }

        // Phase 4: Root split — create new root while holding old root's write lock.
        // This prevents concurrent InsertIterative calls from racing to create multiple roots.
        //
        // Issue #297: hold newRoot's write lock around the structural writes (SetLeft + Insert). Without it, a concurrent thread reading the just-published
        // `Root` field can observe newRoot with count=0 (Insert(0, promoted) hasn't written yet) and descend through the leftmost child path, missing the
        // promoted subtree entirely. The lock forces the racer to restart on a locked latch until WriteUnlock publishes a consistent state. // root split
        if (promoted != null)
        {
            var newRoot = AllocNode(NodeStates.None, ref accessor);
            newRoot.PreDirtyForWrite(ref accessor);
            var newRootLatch = newRoot.GetLatch(ref accessor);
            var newRootOutcome = SpinWriteLock(newRootLatch);   // freshly allocated — Obsolete unreachable, see the twin in AddOrUpdateCore
            Debug.Assert(newRootOutcome != WriteLockOutcome.Obsolete, "a freshly allocated root cannot be obsolete");
            newRoot.SetLeft(Root, ref accessor);
            newRoot.Insert(0, promoted.Value, ref accessor);
            Root = newRoot;
            Height++;
            newRootLatch.WriteUnlock();
            node.GetLatch(ref accessor).WriteUnlock(); // release old root after publishing new root
        }
 // done
        completed = true;
    }
}
