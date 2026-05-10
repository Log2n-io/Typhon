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
    /// <returns>True if the old key was found and moved; false if old key not found.</returns>
    public bool Move(TKey oldKey, TKey newKey, int value, ref ChunkAccessor<TStore> accessor)
    {
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

                var (newLeafId, newVersion, _) = OptimisticDescendToLeaf(newKey, ref opAccessor);
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
                        latch.WriteUnlock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Re-find under lock (indices may have shifted)
                    var oi = leaf.Find(oldKey, Comparer, ref opAccessor);
                    if (oi < 0)
                    {
                        latch.WriteUnlock();
                        return false; // old key gone
                    }

                    // Check newKey doesn't already exist BEFORE modifying anything
                    var ni = leaf.Find(newKey, Comparer, ref opAccessor);
                    if (ni >= 0)
                    {
                        latch.WriteUnlock();
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
                        firstLatch.WriteUnlock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Validate both versions
                    if (!firstLatch.ValidateVersion(firstVersion | 1) || !secondLatch.ValidateVersion(secondVersion | 1))
                    {
                        secondLatch.WriteUnlock();
                        firstLatch.WriteUnlock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Identify which is old and which is new
                    var oldLeaf = oldLeafId == firstId ? firstLeaf : secondLeaf;
                    var newLeaf = oldLeafId == firstId ? secondLeaf : firstLeaf;

                    // Safety check: if newLeaf is full (insert would overflow) or oldLeaf would underflow, bail to pessimistic which handles structural
                    // modifications properly
                    if (newLeaf.GetIsFull(ref opAccessor) || !oldLeaf.GetIsHalfFull(ref opAccessor))
                    {
                        secondLatch.WriteUnlock();
                        firstLatch.WriteUnlock();
                        break; // fall to pessimistic
                    }

                    // Re-find under locks
                    var oi = oldLeaf.Find(oldKey, Comparer, ref opAccessor);
                    if (oi < 0)
                    {
                        secondLatch.WriteUnlock();
                        firstLatch.WriteUnlock();
                        return false;
                    }

                    var ni = newLeaf.Find(newKey, Comparer, ref opAccessor);
                    if (ni >= 0)
                    {
                        // newKey already exists — fail without modification
                        secondLatch.WriteUnlock();
                        firstLatch.WriteUnlock();
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
    /// Pessimistic fallback for Move: traverses, removes oldKey, inserts newKey.
    /// No global lock — concurrency is handled by per-node OLC latches in Remove/Insert.
    /// </summary>
    private bool MovePessimistic(TKey oldKey, TKey newKey, int value, ref ChunkAccessor<TStore> accessor)
    {
        ref var sibAccessor = ref _segment.RentWarmSiblingAccessor(accessor.ChangeSet);
        try
        {
            var oldLeaf = FindLeaf(oldKey, out var oldIndex, ref accessor);
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
    /// Returns the new element ID and both HEAD buffer IDs for inline TAIL tracking.
    /// </summary>
    public int MoveValue(TKey oldKey, TKey newKey, int elementId, int value,
        ref ChunkAccessor<TStore> accessor, out int oldHeadBufferId, out int newHeadBufferId, bool preserveEmptyBuffer = false)
    {
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

                var (newLeafId, newVersion, _) = OptimisticDescendToLeaf(newKey, ref opAccessor);
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
                        latch.WriteUnlock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // Re-find oldKey under lock
                    var oi = leaf.Find(oldKey, Comparer, ref opAccessor);
                    if (oi < 0)
                    {
                        latch.WriteUnlock();
                        oldHeadBufferId = -1;
                        newHeadBufferId = -1;
                        return -1;
                    }

                    // Remove element from old buffer (VSBS via sibAccessor to avoid CA slot eviction)
                    var oldBufferId = leaf.GetItem(oi, ref opAccessor).Value;
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
                        // We can only reclaim when res==0 (old buffer empty) AND !preserveEmptyBuffer.
                        if (leaf.GetIsFull(ref opAccessor) && (res != 0 || preserveEmptyBuffer))
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
                        if (res == 0 && !preserveEmptyBuffer)
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
                    if (res == 0 && !preserveEmptyBuffer)
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
                        firstLatch.WriteUnlock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    if (!firstLatch.ValidateVersion(firstVersion | 1) || !secondLatch.ValidateVersion(secondVersion | 1))
                    {
                        secondLatch.WriteUnlock();
                        firstLatch.WriteUnlock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    var oldLeaf = oldLeafId == firstId ? firstLeaf : secondLeaf;
                    var newLeaf = oldLeafId == firstId ? secondLeaf : firstLeaf;

                    // Pre-check: if newLeaf is full and newKey doesn't exist, bail to pessimistic (we'd need to insert a new entry which could cause overflow)
                    if (newLeaf.GetIsFull(ref opAccessor))
                    {
                        var preNi = newLeaf.Find(newKey, Comparer, ref opAccessor);
                        if (preNi < 0)
                        {
                            secondLatch.WriteUnlock();
                            firstLatch.WriteUnlock();
                            break; // fall to pessimistic
                        }
                    }

                    // Remove element from old buffer
                    var oi = oldLeaf.Find(oldKey, Comparer, ref opAccessor);
                    if (oi < 0)
                    {
                        secondLatch.WriteUnlock();
                        firstLatch.WriteUnlock();
                        oldHeadBufferId = -1;
                        newHeadBufferId = -1;
                        return -1;
                    }

                    var oldBufferId = oldLeaf.GetItem(oi, ref opAccessor).Value;
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
                    if (res == 0 && !preserveEmptyBuffer)
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
            return MoveValuePessimistic(oldKey, newKey, elementId, value, ref opAccessor, ref sibAccessor, out oldHeadBufferId, out newHeadBufferId, preserveEmptyBuffer);
        }
        finally
        {
            _segment.ReturnWarmSiblingAccessor();
            _segment.ReturnWarmAccessor();
        }
    }

    /// <summary>
    /// Pessimistic fallback for MoveValue: removes element from old buffer,
    /// appends to new buffer, handles empty-buffer cleanup.
    /// No global lock — concurrency is handled by per-node OLC latches in Remove/Insert.
    /// </summary>
    private int MoveValuePessimistic(TKey oldKey, TKey newKey, int elementId, int value, ref ChunkAccessor<TStore> accessor, ref ChunkAccessor<TStore> sibAccessor,
        out int oldHeadBufferId, out int newHeadBufferId, bool preserveEmptyBuffer = false)
    {
        try
        {
            var oldLeaf = FindLeaf(oldKey, out var oldIndex, ref accessor);
            if (!oldLeaf.IsValid || oldIndex < 0)
            {
                oldHeadBufferId = -1;
                newHeadBufferId = -1;
                return -1;
            }

            // Remove element from old buffer (VSBS via sibAccessor)
            var oldBufferId = oldLeaf.GetItem(oldIndex, ref accessor).Value;
            var res = _storage.RemoveFromBuffer(oldBufferId, elementId, value, ref sibAccessor);
            oldHeadBufferId = oldBufferId;

            if (res == -1)
            {
                newHeadBufferId = -1;
                return -1;
            }

            // Append to new key's buffer
            var newLeaf = FindLeaf(newKey, out var newIndex, ref accessor);
            int newBufferId;
            int newElementId;
            if (newLeaf.IsValid && newIndex >= 0)
            {
                // newKey exists — append to its buffer
                newBufferId = newLeaf.GetItem(newIndex, ref accessor).Value;
                newElementId = _storage.Append(newBufferId, value, ref sibAccessor);
            }
            else
            {
                // newKey doesn't exist — create buffer and insert via AddOrUpdateCore
                newBufferId = _storage.CreateBuffer(ref sibAccessor);
                newElementId = _storage.Append(newBufferId, value, ref sibAccessor);
                var insertArgs = new InsertArguments(newKey, newBufferId, Comparer, ref accessor, ref sibAccessor);
                AddOrUpdateCorePessimistic(ref insertArgs);
            }
            newHeadBufferId = newBufferId;

            // If old buffer is now empty, remove the BTree entry
            if (res == 0 && !preserveEmptyBuffer)
            {
                var removeArgs = new RemoveArguments(oldKey, Comparer, ref accessor, ref sibAccessor);
                RemoveCorePessimistic(ref removeArgs);
                if (removeArgs.Removed)
                {
                    _storage.DeleteBuffer(oldBufferId, ref sibAccessor);
                }
            }

            SyncHeader(ref accessor);
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
