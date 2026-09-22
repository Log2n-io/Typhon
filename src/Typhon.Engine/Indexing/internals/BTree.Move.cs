// unset

using System;
using System.Threading;

namespace Typhon.Engine.Internals;

internal abstract partial class BTree<TKey, TStore>
{
    /// <summary>
    /// Compound move for unique indexes: atomically removes the entry at <paramref name="oldKey"/> and inserts it under <paramref name="newKey"/>.
    /// Uses OLC: same-leaf fast path (single lock), different-leaf (dual lock by ChunkId order).
    /// Falls back to pessimistic after <see cref="MaxOptimisticRestarts"/>.
    /// </summary>
    /// <remarks>
    /// <b>Unlock discipline (#765 S3).</b> A bail that wrote nothing releases with <c>AbortWriteLock</c>, never <c>WriteUnlock</c>. The two differ in one bit of
    /// behaviour and a lot of consequence: <c>WriteUnlock</c> bumps the node's version, which tells every optimistic reader and writer holding a snapshot of
    /// that node that their snapshot is stale, and they restart. When the node was genuinely modified that is exactly right. When it was not — a version
    /// validation that failed, a key that turned out to be absent, a full-leaf bail to the pessimistic path — it is a lie, and the threads it restarts go around
    /// and contend for the same latch again.
    /// <para>
    /// This file used to hold 31 <c>WriteUnlock</c> calls and zero <c>AbortWriteLock</c>, of which only ten follow an actual mutation; #679 named those spurious
    /// bumps as the MEASURED cause of its restart storm. Ten remain, and each one is downstream of a leaf mutation or a VSBS buffer write. Anything that touches
    /// storage keeps <c>WriteUnlock</c> even where the leaf's own items are unchanged — a bail that has already written to a buffer is not a no-op, and this is
    /// not the place to be clever about it.
    /// </para>
    /// </remarks>
    /// <returns>True if the old key was found and moved; false if old key not found.</returns>
    public bool Move(TKey oldKey, TKey newKey, int value, ref ChunkAccessor<TStore> accessor)
    {
        _fenceWindow?.NoteMutation("BTree.Move");
        // Per-operation accessor for thread safety under OLC (thread-local warm cache)
        ref var opAccessor = ref _segment.RentWarmAccessor(accessor.ChangeSet);
        try
        {
            for (int attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
            {
                // Phase 1: Optimistic descent for both keys
                var (oldLeafId, oldVersion, oldKeyIndex) = OptimisticDescendToLeaf(oldKey, ref opAccessor);
                if (oldLeafId == 0)
                {
                    if (IsEmpty())
                    {
                        return false;
                    }
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                if (oldKeyIndex < 0)
                {
                    // oldKey not found — validate this is not a stale read
                    var checkLeaf = _storage.LoadNode(oldLeafId);
                    if (checkLeaf.GetLatch(ref opAccessor).ValidateVersion(oldVersion))
                    {
                        return false; // genuinely not found
                    }
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue; // stale read, restart
                }

                // followRightLink: false — this descent picks an INSERTION target, and the B-link right-walk answers a different question. The walk exists so a
                // reader that lands left of a concurrently-split leaf still finds an EXISTING key; it hops right until the key falls inside a leaf's real
                // contents. newKey exists nowhere yet, so for it the walk never terminates on a match and instead runs past the leaf whose separator range owns
                // the key, into the next one. Move then inserted there, below that leaf's separator — which is precisely the state ValidateLeafSeparators
                // reports and Stress_MoveSameLeaf has been printing and discarding since it was written (#765). Measured: with the default `true`, a
                // SINGLE-THREADED sweep of 200 moves over one key range breaks a separator/leaf pair every run; with `false`, zero. Insert has always passed
                // false here and says so at BTree.Insert.cs — Move is the copy that never received it.
                // #221: skip the second descent entirely when the leaf we are already standing on demonstrably owns newKey's range. A same-leaf move — which is
                // what "shift an entity to an adjacent slot" is — was paying two full root-to-leaf descents to reach a conclusion the first one already
                // contains. The test is the complement of the leaf-authority predicate: newKey at or above this leaf's first key, and below its HighKey (or the
                // leaf is rightmost, where HighKey bounds nothing). If either bound fails, fall through and descend properly.
                //
                // Safe because it can only ever return the SAME leaf the general path would have picked: it concludes only when both bounds hold on a
                // version-validated read, and the authority check under the lock below re-asks the identical question. A wrong answer costs a restart, never a
                // misplaced key.
                int newLeafId;
                int newVersion;
                var standingLeaf = _storage.LoadNode(oldLeafId);
                if (!KeyOutsideLeafAuthority(standingLeaf, newKey, Comparer, ref opAccessor)
                    && standingLeaf.GetLatch(ref opAccessor).ValidateVersion(oldVersion))
                {
                    newLeafId = oldLeafId;
                    newVersion = oldVersion;
                }
                else
                {
                    (newLeafId, newVersion, _) = OptimisticDescendToLeaf(newKey, ref opAccessor, false);
                }

                if (newLeafId == 0)
                {
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue; // restart
                }

                // Phase 2: Lock and mutate
                if (oldLeafId == newLeafId)
                {
                    // Same-leaf fast path: single WriteLock, net count unchanged
                    var leaf = _storage.LoadNode(oldLeafId);
                    leaf.PreDirtyForWrite(ref opAccessor);
                    var latch = leaf.GetLatch(ref opAccessor);
                    if (!latch.TryWriteLock())
                    {
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue; // contended, restart
                    }

                    // Validate version (detects concurrent modification between our read and lock)
                    if (!latch.ValidateVersion(oldVersion | 1))
                    {
                        latch.AbortWriteLock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Leaf authority for the key being WRITTEN. Dropping the right-link walk above means the descent can now stop short of the leaf that owns
                    // newKey's range, and an unchecked insert there would either misplace the key or miss an existing duplicate sitting one leaf right.
                    // AbortWriteLock, not WriteUnlock — nothing has been modified, so bumping the version would only restart other threads for free (#679).
                    if (KeyOutsideLeafAuthority(leaf, newKey, Comparer, ref opAccessor))
                    {
                        latch.AbortWriteLock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Re-find under lock (indices may have shifted)
                    var oi = leaf.Find(oldKey, Comparer, ref opAccessor);
                    if (oi < 0)
                    {
                        latch.AbortWriteLock();
                        return false; // old key gone
                    }

                    // Check newKey doesn't already exist BEFORE modifying anything
                    var ni = leaf.Find(newKey, Comparer, ref opAccessor);
                    if (ni >= 0)
                    {
                        latch.AbortWriteLock();
                        return false; // newKey already exists — no modification
                    }

                    // Remove old entry and insert new entry
                    leaf.RemoveAtInternal(oi, ref opAccessor);
                    // Re-find insertion point after removal (indices shifted)
                    ni = leaf.Find(newKey, Comparer, ref opAccessor);
                    ni = ~ni;
                    leaf.Insert(ni, new KeyValueItem(newKey, value), ref opAccessor);

                    latch.WriteUnlock();
                    return true;
                }
                else
                {
                    // Different-leaf path: lock in ChunkId order to prevent deadlocks
                    var firstId = Math.Min(oldLeafId, newLeafId);
                    var secondId = Math.Max(oldLeafId, newLeafId);
                    var firstVersion = oldLeafId == firstId ? oldVersion : newVersion;
                    var secondVersion = oldLeafId == firstId ? newVersion : oldVersion;

                    var firstLeaf = _storage.LoadNode(firstId);
                    var secondLeaf = _storage.LoadNode(secondId);

                    firstLeaf.PreDirtyForWrite(ref opAccessor);
                    var firstLatch = firstLeaf.GetLatch(ref opAccessor);
                    if (!firstLatch.TryWriteLock())
                    {
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    secondLeaf.PreDirtyForWrite(ref opAccessor);
                    var secondLatch = secondLeaf.GetLatch(ref opAccessor);
                    if (!secondLatch.TryWriteLock())
                    {
                        firstLatch.AbortWriteLock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Validate both versions
                    if (!firstLatch.ValidateVersion(firstVersion | 1) || !secondLatch.ValidateVersion(secondVersion | 1))
                    {
                        secondLatch.AbortWriteLock();
                        firstLatch.AbortWriteLock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Identify which is old and which is new
                    var oldLeaf = oldLeafId == firstId ? firstLeaf : secondLeaf;
                    var newLeaf = oldLeafId == firstId ? secondLeaf : firstLeaf;

                    // Same leaf-authority question as the same-leaf path, asked of the leaf that will receive the key. Aborting rather than unlocking: the two
                    // leaves are untouched at this point.
                    if (KeyOutsideLeafAuthority(newLeaf, newKey, Comparer, ref opAccessor))
                    {
                        secondLatch.AbortWriteLock();
                        firstLatch.AbortWriteLock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Safety check: if newLeaf is full (insert would overflow) or oldLeaf would underflow, bail to pessimistic which handles structural
                    // modifications properly
                    if (newLeaf.GetIsFull(ref opAccessor) || !oldLeaf.GetIsHalfFull(ref opAccessor))
                    {
                        secondLatch.AbortWriteLock();
                        firstLatch.AbortWriteLock();
                        break; // fall to pessimistic
                    }

                    // Re-find under locks
                    var oi = oldLeaf.Find(oldKey, Comparer, ref opAccessor);
                    if (oi < 0)
                    {
                        secondLatch.AbortWriteLock();
                        firstLatch.AbortWriteLock();
                        return false;
                    }

                    var ni = newLeaf.Find(newKey, Comparer, ref opAccessor);
                    if (ni >= 0)
                    {
                        // newKey already exists — fail without modification
                        secondLatch.AbortWriteLock();
                        firstLatch.AbortWriteLock();
                        return false;
                    }
                    ni = ~ni;

                    // Remove from old, insert into new
                    oldLeaf.RemoveAtInternal(oi, ref opAccessor);
                    newLeaf.Insert(ni, new KeyValueItem(newKey, value), ref opAccessor);

                    secondLatch.WriteUnlock();
                    firstLatch.WriteUnlock();
                    return true;
                }
            }

            // Pessimistic fallback: full exclusive lock
            Interlocked.Increment(ref _pessimisticFallbacks);
            return MovePessimistic(oldKey, newKey, value, ref opAccessor);
        }
        finally
        {
            _segment.ReturnWarmAccessor();
        }
    }

    /// <summary>
    /// Whether <paramref name="leaf"/> can give up one entry without a structural modification: the root may hold anything, every other leaf must stay above
    /// half full — the same condition <c>RemoveCorePessimistic</c>'s fast paths use to decide that a pop needs no borrow or merge.
    /// </summary>
    private bool CanLoseAnEntryInPlace(NodeWrapper leaf, ref ChunkAccessor<TStore> accessor)
        => Root == leaf || leaf.GetCount(ref accessor) > leaf.GetCapacity() / 2;

    /// <summary>
    /// Pessimistic fallback for Move: traverses, removes oldKey, inserts newKey.
    /// No global lock — concurrency is handled by per-node OLC latches in Remove/Insert.
    /// </summary>
    /// <remarks>
    /// True for a unique index, where the leaf entry IS the value. The multi-value twin, <c>MoveValuePessimistic</c>, has a buffer behind the entry and
    /// therefore needs the latch around the buffer step too — see its remarks and IXW-06.
    /// </remarks>
    private bool MovePessimistic(TKey oldKey, TKey newKey, int value, ref ChunkAccessor<TStore> accessor)
    {
        ref var sibAccessor = ref _segment.RentWarmSiblingAccessor(accessor.ChangeSet);
        try
        {
            // An unlatched pre-check whose `false` is DECISIVE — the fence's drain reads it as a unique collision and throws #675 — so the one thing it must
            // not do is report a present key absent because FindLeaf landed one leaf to the right of it (see TryLatchAuthoritativeLeaf for why it can).
            // Re-descend until the leaf owns the key's range; only then is a miss a miss. Bounded like every other pessimistic retry (IXW-01): a stale
            // separator nobody fixes must surface as a throw, not a spin.
            NodeWrapper oldLeaf;
            int oldIndex;
            PureSpin authoritySpin = default;
            for (var attempt = 0; ; attempt++)
            {
                oldLeaf = FindLeaf(oldKey, out oldIndex, ref accessor);
                if (!oldLeaf.IsValid || oldIndex >= 0 || !KeyOutsideLeafAuthority(oldLeaf, oldKey, Comparer, ref accessor))
                {
                    break;
                }

                Interlocked.Increment(ref _pessimisticRestarts);
                if (attempt >= MaxPessimisticRestarts)
                {
                    ThrowHelper.ThrowInvalidOp(
                        $"B+Tree Move pre-check made no progress in {MaxPessimisticRestarts} retries: every descent lands on a leaf that does not own the "
                        + "old key's range. This is a liveness defect in the tree, not contention (IXW-01, IXW-06).");
                }

                authoritySpin.Once();
            }

            if (!oldLeaf.IsValid || oldIndex < 0)
            {
                return false;
            }

            // Check that newKey doesn't already exist
            var newLeaf = FindLeaf(newKey, out var newIndex, ref accessor);
            if (newLeaf.IsValid && newIndex >= 0)
            {
                return false; // newKey already exists
            }

            // Remove old entry — use RemoveArguments/RemoveCore for proper structural handling
            var removeArgs = new RemoveArguments(oldKey, Comparer, ref accessor, ref sibAccessor);
            RemoveCorePessimistic(ref removeArgs);
            if (!removeArgs.Removed)
            {
                return false;
            }

            // Insert new entry
            var insertArgs = new InsertArguments(newKey, value, Comparer, ref accessor, ref sibAccessor);
            AddOrUpdateCorePessimistic(ref insertArgs);
            SyncHeader(ref accessor);
            return true;
        }
        finally
        {
            _segment.ReturnWarmSiblingAccessor();
            if (++_deferredReclaimSkip >= 64)
            {
                _deferredReclaimSkip = 0;
                DeferredReclaim();
            }
        }
    }

    /// <summary>
    /// Compound move for AllowMultiple indexes: removes <paramref name="elementId"/>/<paramref name="value"/> from <paramref name="oldKey"/>'s buffer and
    /// appends <paramref name="value"/> under <paramref name="newKey"/>.
    /// Returns the new element ID, plus both HEAD buffer IDs as <c>out</c> values.
    /// </summary>
    /// <remarks>
    /// The two buffer ids have no consumer in the engine: every production caller discards them with <c>out _, out _</c> (<c>Transaction.cs</c> and both
    /// cluster-migration sites), and only <c>OlcBTreeTests</c> asserts on them. They are returned because resolving each key's HEAD buffer is work this
    /// method already does under the leaf latch, so handing the ids back costs nothing and saves a caller that wants them a second descent.
    /// </remarks>
    /// <remarks>
    /// Multi-writer safe, and the rule that says what that rests on is IXW-06 (#887): a buffer is read or mutated only under the write latch of the leaf whose
    /// entry names it, and a key emptied by one thread is removed only if its buffer is still empty under that latch. The optimistic paths below always had
    /// both; the pessimistic fallback had neither, and with <c>MaxOptimisticRestarts</c> at 3 it is where two moves in three land under real contention.
    /// <c>BTreeMoveValueConcurrencyTests</c> is the census that caught it and now guards it.
    /// </remarks>
    public int MoveValue(TKey oldKey, TKey newKey, int elementId, int value,
        ref ChunkAccessor<TStore> accessor, out int oldHeadBufferId, out int newHeadBufferId)
    {
        _fenceWindow?.NoteMutation("BTree.MoveValue");
        // Per-operation accessor for thread safety under OLC (thread-local warm cache)
        ref var opAccessor = ref _segment.RentWarmAccessor(accessor.ChangeSet);
        // Separate CA for VSBS buffer operations — prevents VSBS page loads from evicting
        // B+Tree leaf node slots in the primary CA's 16-slot cache.
        ref var sibAccessor = ref _segment.RentWarmSiblingAccessor(accessor.ChangeSet);
        try
        {
            for (int attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
            {
                // Phase 1: Optimistic descent for both keys
                var (oldLeafId, oldVersion, oldKeyIndex) = OptimisticDescendToLeaf(oldKey, ref opAccessor);
                if (oldLeafId == 0)
                {
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                if (oldKeyIndex < 0)
                {
                    var checkLeaf = _storage.LoadNode(oldLeafId);
                    if (checkLeaf.GetLatch(ref opAccessor).ValidateVersion(oldVersion))
                    {
                        oldHeadBufferId = -1;
                        newHeadBufferId = -1;
                        return -1; // old key genuinely not found
                    }
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                // followRightLink: false, for the same reason as Move — see the note there. This is the AllowMultiple twin and carries the identical hole.
                var (newLeafId, newVersion, _) = OptimisticDescendToLeaf(newKey, ref opAccessor, false);
                if (newLeafId == 0)
                {
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                // Phase 2: Lock and mutate
                if (oldLeafId == newLeafId)
                {
                    var leaf = _storage.LoadNode(oldLeafId);
                    leaf.PreDirtyForWrite(ref opAccessor);
                    var latch = leaf.GetLatch(ref opAccessor);
                    if (!latch.TryWriteLock())
                    {
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    if (!latch.ValidateVersion(oldVersion | 1))
                    {
                        latch.AbortWriteLock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Leaf authority for newKey, asked BEFORE the buffer mutation below — past that point a bail has to undo storage writes, which is why this
                    // sits here rather than next to the Insert call it protects.
                    if (KeyOutsideLeafAuthority(leaf, newKey, Comparer, ref opAccessor))
                    {
                        latch.AbortWriteLock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Re-find oldKey under lock
                    var oi = leaf.Find(oldKey, Comparer, ref opAccessor);
                    if (oi < 0)
                    {
                        latch.AbortWriteLock();
                        oldHeadBufferId = -1;
                        newHeadBufferId = -1;
                        return -1;
                    }

                    // Remove element from old buffer (VSBS via sibAccessor to avoid CA slot eviction)
                    var oldBufferId = leaf.GetItem(oi, ref opAccessor).Value;

                    // Asked BEFORE the buffer mutation, like the authority check above. When this move empties the old buffer AND the new key already exists
                    // in this leaf, the leaf loses an entry with nothing coming in; if that would take it under half full, only the SMO path can borrow or
                    // merge — so bail to it now, while nothing has been written. Move's two-leaf path has always had this guard; MoveValue had it nowhere,
                    // and CheckConsistency found the empty leaves it left behind (#887).
                    if (_storage.BufferElementCount(oldBufferId, ref sibAccessor) == 1 && leaf.Find(newKey, Comparer, ref opAccessor) >= 0
                        && !CanLoseAnEntryInPlace(leaf, ref opAccessor))
                    {
                        latch.AbortWriteLock();
                        break; // fall to pessimistic
                    }

                    var res = _storage.RemoveFromBuffer(oldBufferId, elementId, value, ref sibAccessor);
                    oldHeadBufferId = oldBufferId;

                    if (res == -1)
                    {
                        latch.WriteUnlock();
                        newHeadBufferId = -1;
                        return -1; // element not found in buffer
                    }

                    // Find or prepare newKey
                    var ni = leaf.Find(newKey, Comparer, ref opAccessor);
                    int newBufferId;
                    int newElementId;
                    if (ni >= 0)
                    {
                        // newKey exists — append to its buffer
                        newBufferId = leaf.GetItem(ni, ref opAccessor).Value;
                        newElementId = _storage.Append(newBufferId, value, ref sibAccessor);
                    }
                    else
                    {
                        // newKey doesn't exist — need to insert a new key entry
                        // If leaf is full and we won't reclaim a slot, bail to pessimistic.
                        // We can only reclaim when res==0 (the old buffer emptied).
                        if (leaf.GetIsFull(ref opAccessor) && res != 0)
                        {
                            // Undo the buffer removal — re-add the element
                            _storage.Append(oldBufferId, value, ref sibAccessor);
                            latch.WriteUnlock();
                            break; // fall to pessimistic
                        }

                        newBufferId = _storage.CreateBuffer(ref sibAccessor);
                        newElementId = _storage.Append(newBufferId, value, ref sibAccessor);
                        ni = ~ni;
                        // If old buffer empty (res==0) and not preserving, remove old key first to free a slot
                        if (res == 0)
                        {
                            oi = leaf.Find(oldKey, Comparer, ref opAccessor);
                            if (oi >= 0)
                            {
                                leaf.RemoveAtInternal(oi, ref opAccessor);
                                _storage.DeleteBuffer(oldBufferId, ref sibAccessor);
                                Interlocked.Decrement(ref _count);
                            }
                            // Re-find insertion point after removal
                            ni = leaf.Find(newKey, Comparer, ref opAccessor);
                            ni = ~ni;
                            res = -2; // sentinel: old key already cleaned up
                        }
                        leaf.Insert(ni, new KeyValueItem(newKey, newBufferId), ref opAccessor);
                        Interlocked.Increment(ref _count);
                    }
                    newHeadBufferId = newBufferId;

                    // If old buffer is now empty and not yet cleaned up, remove the BTree entry for oldKey
                    if (res == 0)
                    {
                        // Re-find oldKey (index may have shifted after insert)
                        oi = leaf.Find(oldKey, Comparer, ref opAccessor);
                        if (oi >= 0)
                        {
                            leaf.RemoveAtInternal(oi, ref opAccessor);
                            _storage.DeleteBuffer(oldBufferId, ref sibAccessor);
                            Interlocked.Decrement(ref _count);
                        }
                    }

                    latch.WriteUnlock();
                    SyncHeader(ref opAccessor);
                    return newElementId;
                }
                else
                {
                    // Different-leaf path: lock in ChunkId order
                    var firstId = Math.Min(oldLeafId, newLeafId);
                    var secondId = Math.Max(oldLeafId, newLeafId);
                    var firstVersion = oldLeafId == firstId ? oldVersion : newVersion;
                    var secondVersion = oldLeafId == firstId ? newVersion : oldVersion;

                    var firstLeaf = _storage.LoadNode(firstId);
                    var secondLeaf = _storage.LoadNode(secondId);

                    firstLeaf.PreDirtyForWrite(ref opAccessor);
                    var firstLatch = firstLeaf.GetLatch(ref opAccessor);
                    if (!firstLatch.TryWriteLock())
                    {
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    secondLeaf.PreDirtyForWrite(ref opAccessor);
                    var secondLatch = secondLeaf.GetLatch(ref opAccessor);
                    if (!secondLatch.TryWriteLock())
                    {
                        firstLatch.AbortWriteLock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    if (!firstLatch.ValidateVersion(firstVersion | 1) || !secondLatch.ValidateVersion(secondVersion | 1))
                    {
                        secondLatch.AbortWriteLock();
                        firstLatch.AbortWriteLock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    var oldLeaf = oldLeafId == firstId ? firstLeaf : secondLeaf;
                    var newLeaf = oldLeafId == firstId ? secondLeaf : firstLeaf;

                    // Leaf authority for newKey — the fourth and last write site that lacked it.
                    if (KeyOutsideLeafAuthority(newLeaf, newKey, Comparer, ref opAccessor))
                    {
                        secondLatch.AbortWriteLock();
                        firstLatch.AbortWriteLock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Pre-check: if newLeaf is full and newKey doesn't exist, bail to pessimistic (we'd need to insert a new entry which could cause overflow)
                    if (newLeaf.GetIsFull(ref opAccessor))
                    {
                        var preNi = newLeaf.Find(newKey, Comparer, ref opAccessor);
                        if (preNi < 0)
                        {
                            secondLatch.AbortWriteLock();
                            firstLatch.AbortWriteLock();
                            break; // fall to pessimistic
                        }
                    }

                    // Remove element from old buffer
                    var oi = oldLeaf.Find(oldKey, Comparer, ref opAccessor);
                    if (oi < 0)
                    {
                        secondLatch.AbortWriteLock();
                        firstLatch.AbortWriteLock();
                        oldHeadBufferId = -1;
                        newHeadBufferId = -1;
                        return -1;
                    }

                    var oldBufferId = oldLeaf.GetItem(oi, ref opAccessor).Value;

                    // The old leaf loses its entry when this move empties the buffer, and nothing comes in to replace it. Move's twin bails whenever the old
                    // leaf is not half full; this asks the sharper question — will THIS move take an entry out, and would that underflow — and bails only
                    // then, before any storage write. Without it the two-leaf path left EMPTY leaves linked into the chain (#887, found by CheckConsistency).
                    if (_storage.BufferElementCount(oldBufferId, ref sibAccessor) == 1 && !CanLoseAnEntryInPlace(oldLeaf, ref opAccessor))
                    {
                        secondLatch.AbortWriteLock();
                        firstLatch.AbortWriteLock();
                        break; // fall to pessimistic
                    }

                    var res = _storage.RemoveFromBuffer(oldBufferId, elementId, value, ref sibAccessor);
                    oldHeadBufferId = oldBufferId;

                    if (res == -1)
                    {
                        secondLatch.WriteUnlock();
                        firstLatch.WriteUnlock();
                        newHeadBufferId = -1;
                        return -1;
                    }

                    // Append to new buffer
                    var ni = newLeaf.Find(newKey, Comparer, ref opAccessor);
                    int newBufferId;
                    int newElementId;
                    if (ni >= 0)
                    {
                        newBufferId = newLeaf.GetItem(ni, ref opAccessor).Value;
                        newElementId = _storage.Append(newBufferId, value, ref sibAccessor);
                    }
                    else
                    {
                        newBufferId = _storage.CreateBuffer(ref sibAccessor);
                        newElementId = _storage.Append(newBufferId, value, ref sibAccessor);
                        ni = ~ni;
                        newLeaf.Insert(ni, new KeyValueItem(newKey, newBufferId), ref opAccessor);
                        Interlocked.Increment(ref _count);
                    }
                    newHeadBufferId = newBufferId;

                    // If old buffer is now empty, remove the BTree entry
                    if (res == 0)
                    {
                        oi = oldLeaf.Find(oldKey, Comparer, ref opAccessor);
                        if (oi >= 0)
                        {
                            oldLeaf.RemoveAtInternal(oi, ref opAccessor);
                            _storage.DeleteBuffer(oldBufferId, ref sibAccessor);
                            Interlocked.Decrement(ref _count);
                        }
                    }

                    secondLatch.WriteUnlock();
                    firstLatch.WriteUnlock();
                    SyncHeader(ref opAccessor);
                    return newElementId;
                }
            }

            // Pessimistic fallback
            Interlocked.Increment(ref _pessimisticFallbacks);
            return MoveValuePessimistic(oldKey, newKey, elementId, value, ref opAccessor, ref sibAccessor, out oldHeadBufferId, out newHeadBufferId);
        }
        finally
        {
            _segment.ReturnWarmSiblingAccessor();
            _segment.ReturnWarmAccessor();
        }
    }

    /// <summary>
    /// Pessimistic fallback for MoveValue: removes the element from the old key's buffer, appends it under the new key, and drops the old key if its buffer
    /// emptied — each step under the latch of the leaf it touches, and no buffer id carried from one step to the next.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#887 lived here.</b> This used to read <c>oldBufferId</c> and <c>newBufferId</c> through an unlatched <c>FindLeaf</c> + <c>GetItem</c> and then
    /// call <c>RemoveFromBuffer</c> / <c>Append</c> on them. The optimistic paths above only ever touch a buffer under the write latch of the leaf whose entry
    /// names it, and that is not decoration: a peer that empties a key removes the entry and FREES the buffer under its latch, and the allocator re-issues the
    /// chunk to whoever creates a buffer next. An id read before that and used after it addressed a dead buffer, or another key's — the element vanished, or
    /// landed under a key its move never named. With <c>MaxOptimisticRestarts</c> at 3, thirty-two threads reach this fallback for two moves in three, which
    /// is why <c>BTreeMoveValueConcurrencyTests</c> lost whole runs of a key's values in eight runs of twelve, and lost nothing at all with the fallback
    /// unreachable.
    /// </para>
    /// <para>
    /// The three steps now are the three latched primitives the tree already had: <see cref="RemoveElementLatched"/> for the old buffer,
    /// <c>AddOrUpdateCorePessimistic</c> for the new key — whose duplicate branch appends under the leaf latch and whose insert branch creates buffer, element
    /// and entry together (#885 is why it is trusted with the buffer rather than handed one) — and <see cref="RemoveKeyIfBufferStillEmpty"/> for the old key,
    /// which re-checks under the latch that no appender refilled the buffer in between. Between the first and second step the element is in neither buffer,
    /// which readers already tolerate from the unique fallback; what they no longer see is an element that never arrives.
    /// </para>
    /// </remarks>
    private int MoveValuePessimistic(TKey oldKey, TKey newKey, int elementId, int value, ref ChunkAccessor<TStore> accessor, ref ChunkAccessor<TStore> sibAccessor,
        out int oldHeadBufferId, out int newHeadBufferId)
    {
        try
        {
            var remaining = RemoveElementLatched(oldKey, elementId, value, ref accessor, ref sibAccessor, out oldHeadBufferId);
            if (remaining < 0)
            {
                newHeadBufferId = -1;
                return -1;
            }

            var insertArgs = new InsertArguments(newKey, value, Comparer, ref accessor, ref sibAccessor);
            AddOrUpdateCorePessimistic(ref insertArgs);
            newHeadBufferId = insertArgs.BufferRootId;
            var newElementId = insertArgs.ElementId;

            if (remaining == 0)
            {
                RemoveKeyIfBufferStillEmpty(oldKey, ref accessor, ref sibAccessor);
            }
            else
            {
                SyncHeader(ref accessor);
            }

            return newElementId;
        }
        finally
        {
            if (++_deferredReclaimSkip >= 64)
            {
                _deferredReclaimSkip = 0;
                DeferredReclaim();
            }
        }
    }
}
