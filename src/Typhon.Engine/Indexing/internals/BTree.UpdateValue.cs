using System.Threading;

namespace Typhon.Engine.Internals;

internal abstract partial class BTree<TKey, TStore>
{
    /// <summary>
    /// Overwrite the value stored under <paramref name="key"/> in place, without touching the key or any structure. Unique indexes.
    /// </summary>
    /// <param name="key">The key whose value is replaced. Must already exist; this operation never inserts.</param>
    /// <param name="newValue">The value to store.</param>
    /// <param name="accessor">Caller's chunk accessor; supplies the ChangeSet. A per-operation warm accessor is rented for the descent.</param>
    /// <returns><c>true</c> if the key was found and its value written; <c>false</c> if the key is absent, in which case nothing is mutated.</returns>
    /// <remarks>
    /// <para>
    /// Exists because migration changes a value while the key stays the same, and <c>Remove</c> + <c>Add</c> pays two full root-to-leaf descents to do it —
    /// the dominant per-indexed-field cost in the migration path (#872 C9). This is one descent and one 4-byte store.
    /// </para>
    /// <para>
    /// <b>Structurally inert by construction, not by care.</b> It cannot split, merge, shift an item, allocate a node or move the root, because it calls
    /// nothing that does — the entire mutation is <c>SetValueOnly</c>. That is why <c>IXW-05</c> (a writer creates a new root only while it still holds the
    /// current root) has nothing to say here, and why the count, the key array, the separators and the <c>HighKey</c> are unchanged as a matter of which
    /// bytes are written rather than of what is written into them.
    /// </para>
    /// <para>
    /// <b>Against a concurrent reader</b> this is strictly better than the pair it replaces: the value is published with a release store, so a reader sees
    /// the old value or the new one and never a torn one, and there is no window in which the entry is ABSENT — which is exactly the false negative
    /// <c>Remove</c> + <c>Add</c> exposes (<c>IX-06</c>).
    /// </para>
    /// <para>
    /// <b>Unlock discipline.</b> The one path that writes releases with <c>WriteUnlock</c>; every bail uses <c>AbortWriteLock</c>. The difference is that
    /// <c>WriteUnlock</c> bumps the node version, telling every optimistic reader holding a snapshot that it is stale — true after a write, a lie after a
    /// bail, and #679 measured those spurious bumps as the cause of a restart storm. See the remarks on <c>Move</c> in <c>BTree.Move.cs</c>.
    /// </para>
    /// </remarks>
    public bool TryUpdateValue(TKey key, int newValue, ref ChunkAccessor<TStore> accessor)
    {
        _fenceWindow?.NoteMutation("BTree.TryUpdateValue");
        // On an AllowMultiple tree the leaf slot holds a bufferId, not a value (see CreateInsertValue in BTree.Insert.cs), so SetValueOnly below would
        // overwrite it with a ClusterLocation: the buffer is orphaned and every later read of that key dereferences an arbitrary chunk id as a buffer root.
        // That is silent index corruption from a caller using the wrong overload, which is why this throws rather than returning false — `false` is the
        // channel for "the key is absent", and EnumerateRange sets the precedent for refusing the wrong index kind outright (BTree.cs).
        if (AllowMultiple)
        {
            ThrowHelper.ThrowUpdateValueOnAllowMultiple();
        }

        // try/finally, not a bare rent: the warm accessor is pooled per segment and a path that leaves without returning it makes the NEXT rent a double-rent.
        // Debug.Fail catches that, which is how this was found — but only on the second call, so a single-test run would have looked clean.
        ref var opAccessor = ref _segment.RentWarmAccessor(accessor.ChangeSet);
        try
        {
            for (var attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
            {
                // followRightLink stays at its default `true`: this looks up an EXISTING key, which is the case the B-link right-walk exists for. Move passes
                // false for its second descent because that one picks an INSERTION target for a key that exists nowhere yet, so the walk would run
                // past the leaf whose separator range owns it (see BTree.Move.cs).
                var (leafId, version, keyIndex) = OptimisticDescendToLeaf(key, ref opAccessor);
                if (leafId == 0)
                {
                    if (IsEmpty())
                    {
                        return false;
                    }
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                if (keyIndex < 0)
                {
                    // Absent — but a stale read looks identical, so believe it only if the leaf has not moved under us.
                    var checkLeaf = _storage.LoadNode(leafId);
                    if (checkLeaf.GetLatch(ref opAccessor).ValidateVersion(version))
                    {
                        return false;
                    }
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                var leaf = _storage.LoadNode(leafId);
                leaf.PreDirtyForWrite(ref opAccessor);
                var latch = leaf.GetLatch(ref opAccessor);
                if (!latch.TryWriteLock())
                {
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                if (!latch.ValidateVersionLocked(version))
                {
                    latch.AbortWriteLock();
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                // Authority re-check under the lock: a split between descent and lock can leave this leaf no longer owning the key's range.
                if (KeyOutsideLeafAuthority(leaf, key, Comparer, ref opAccessor))
                {
                    latch.AbortWriteLock();
                    Interlocked.Increment(ref _optimisticRestarts);
                    continue;
                }

                // Re-find under the lock — the index from the optimistic descent may have shifted.
                var index = leaf.Find(key, Comparer, ref opAccessor);
                if (index < 0)
                {
                    latch.AbortWriteLock();
                    return false;
                }

                _storage.SetValueOnly(leaf, index, newValue, ref opAccessor);
                latch.WriteUnlock();
                return true;
            }

            // The optimistic budget is 3 (MaxOptimisticRestarts), and returning `false` here would be a LIE: the caller reads false as "the key is not in
            // the index" and a migration would then skip the update or re-Add a duplicate. Contention is a liveness condition, not a data fact, and every
            // sibling operation keeps the two apart — TryGet falls back to TryGetPessimistic, Move to MovePessimistic, Insert and Remove to their own
            // pessimistic loops. So does this one.
            Interlocked.Increment(ref _pessimisticFallbacks);
            return TryUpdateValuePessimistic(key, newValue, ref opAccessor);
        }
        finally
        {
            _segment.ReturnWarmAccessor();
        }
    }

    /// <summary>
    /// The fallback for <see cref="TryUpdateValue"/> when the optimistic budget is exhausted: pessimistic descent, spin for the leaf's write lock, one store.
    /// </summary>
    /// <remarks>
    /// Modelled on <c>RemoveValue</c>, which is written pessimistically throughout: <c>FindLeaf</c> is safe under OLC because internal nodes are stable, and
    /// the re-descent on an obsolete latch is unbounded for the reason #716 records — a merge can detach the leaf between finding it and locking it, and each
    /// pass sees a tree one merge closer to settled, so it terminates as long as writers make progress.
    /// <para>
    /// Takes the caller's already-rented warm accessor rather than renting its own; a second rent on the same segment is the double-rent
    /// <c>Debug.Fail</c> exists to catch.
    /// </para>
    /// </remarks>
    private bool TryUpdateValuePessimistic(TKey key, int newValue, ref ChunkAccessor<TStore> opAccessor)
    {
        // IXW-06: the leaf must OWN the key's range before a miss on it means "absent" — FindLeaf alone can land one leaf to the right under a concurrent
        // merge, and this path used to read that as not-found (the third copy of #887's second face; the other two were MoveValue's and RemoveValue's).
        if (!TryLatchAuthoritativeLeaf(key, ref opAccessor, out var leaf))
        {
            return false;
        }

        // Re-find under the lock, and re-resolve the latch through the node id on every use: GetLatch hands out a reference into the chunk's page and the
        // reads in between can evict that page and reuse the slot (the discipline OptimisticDescendToLeaf documents).
        var index = leaf.Find(key, Comparer, ref opAccessor);
        if (index < 0)
        {
            leaf.GetLatch(ref opAccessor).AbortWriteLock();
            return false;
        }

        _storage.SetValueOnly(leaf, index, newValue, ref opAccessor);
        leaf.GetLatch(ref opAccessor).WriteUnlock();
        return true;
    }

    /// <summary>
    /// Overwrite one element's value inside an <c>AllowMultiple</c> key's buffer, in place. Siblings at the same key and the element's own id are unchanged.
    /// </summary>
    /// <param name="key">The indexed key. Must already exist.</param>
    /// <param name="elementId">The element's chunk id, as returned by <c>AddValue</c> and taken by <c>RemoveValue</c>.</param>
    /// <param name="oldValue">The value currently stored. Required because elements are addressed BY VALUE within their chunk, not by position.</param>
    /// <param name="newValue">The value to store.</param>
    /// <param name="accessor">Caller's chunk accessor; supplies the ChangeSet.</param>
    /// <returns><c>true</c> if the key and the element were found and the value written; otherwise <c>false</c>, with nothing mutated.</returns>
    /// <remarks>
    /// <para>
    /// <b>The signature carries an <paramref name="oldValue"/> the design did not anticipate.</b> A buffer element has no positional address: the id is the
    /// CHUNK holding it, and <c>DeleteElement</c> finds the element by scanning that chunk for a value match. Locating it for an update needs the same
    /// input. Migration has it — the old <c>ClusterLocation</c> is precisely what is being replaced.
    /// </para>
    /// <para>
    /// <b>Nothing moves.</b> Unlike remove-then-append, this changes no element count and performs no swap-with-last, so the caller's
    /// <paramref name="elementId"/> stays valid and every sibling keeps its position — which is what makes it safe to hold element ids across a migration
    /// (#872 AC-4.3).
    /// </para>
    /// <para>
    /// A separate sibling accessor is rented for the buffer pages, mirroring <c>MoveValue</c>: VSBS chunks and index nodes evict each other out of one warm
    /// accessor, and the descent above still needs its leaf.
    /// </para>
    /// </remarks>
    public bool TryUpdateValueAt(TKey key, int elementId, int oldValue, int newValue, ref ChunkAccessor<TStore> accessor)
    {
        _fenceWindow?.NoteMutation("BTree.TryUpdateValueAt");

        // Symmetric with TryUpdateValue's guard, and for the same reason: a unique index has no element buffers at all, so there is nothing this could
        // address. Returning false would report it as "the element is not there", which is a different and recoverable condition.
        if (!AllowMultiple)
        {
            ThrowHelper.ThrowUpdateValueAtOnUnique();
        }

        ref var opAccessor = ref _segment.RentWarmAccessor(accessor.ChangeSet);
        try
        {
            ref var sibAccessor = ref _segment.RentWarmSiblingAccessor(accessor.ChangeSet);
            try
            {
                for (var attempt = 0; attempt < MaxOptimisticRestarts; attempt++)
                {
                    var (leafId, version, keyIndex) = OptimisticDescendToLeaf(key, ref opAccessor);
                    if (leafId == 0)
                    {
                        if (IsEmpty())
                        {
                            return false;
                        }
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    if (keyIndex < 0)
                    {
                        var checkLeaf = _storage.LoadNode(leafId);
                        if (checkLeaf.GetLatch(ref opAccessor).ValidateVersion(version))
                        {
                            return false;
                        }
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    // The leaf entry is locked while the bufferId is read, because a concurrent structural change could otherwise hand us a stale one. The
                    // buffer write itself is serialised by the buffer's own lock inside UpdateInBuffer.
                    var leaf = _storage.LoadNode(leafId);
                    leaf.PreDirtyForWrite(ref opAccessor);
                    var latch = leaf.GetLatch(ref opAccessor);
                    if (!latch.TryWriteLock())
                    {
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    if (!latch.ValidateVersionLocked(version))
                    {
                        latch.AbortWriteLock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    if (KeyOutsideLeafAuthority(leaf, key, Comparer, ref opAccessor))
                    {
                        latch.AbortWriteLock();
                        Interlocked.Increment(ref _optimisticRestarts);
                        continue;
                    }

                    var index = leaf.Find(key, Comparer, ref opAccessor);
                    if (index < 0)
                    {
                        latch.AbortWriteLock();
                        return false;
                    }

                    var bufferId = leaf.GetItem(index, ref opAccessor).Value;
                    bool updated;
                    try
                    {
                        updated = _storage.UpdateInBuffer(bufferId, elementId, oldValue, newValue, ref sibAccessor);
                    }
                    finally
                    {
                        // UpdateInBuffer CAN throw — LockBuffer times out into ThrowLockTimeout — and without this the leaf would stay write-locked for the
                        // life of the process: ReadVersion returns 0 for every reader and TryWriteLock refuses every writer, with no restart able to clear it.
                        // The latch is re-resolved through the node id rather than reusing the local, because the buffer reads in between can evict the leaf's
                        // page and reuse the slot.
                        //
                        // WriteUnlock either way, including when the element was not found: a buffer write has already touched storage, and MoveValue's
                        // discipline is explicit that anything which has written to a buffer is not a no-op. AbortWriteLock here would be the clever choice
                        // and the wrong one.
                        leaf.GetLatch(ref opAccessor).WriteUnlock();
                    }

                    return updated;
                }

                // Same lie as TryUpdateValue's, and refused for the same reason (see the note at the end of that loop): `false` here means "no such element"
                // to every caller, and contention is not a fact about the data. This one is the AllowMultiple twin and it was the one left behind — every
                // sibling operation in this tree has a pessimistic fallback, TryUpdateValue included.
                Interlocked.Increment(ref _pessimisticFallbacks);
                return TryUpdateValueAtPessimistic(key, elementId, oldValue, newValue, ref opAccessor, ref sibAccessor);
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
    /// The fallback for <see cref="TryUpdateValueAt"/> when the optimistic budget is exhausted: pessimistic descent, spin for the leaf's write lock, one
    /// buffer write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Modelled on <see cref="TryUpdateValuePessimistic"/>, and it keeps the optimistic path's leaf-lock discipline rather than
    /// <c>MoveValuePessimistic</c>'s: the leaf write lock is held across the <c>bufferId</c> read because a concurrent structural change would otherwise
    /// hand back a stale one, and the buffer write itself is serialised by the buffer's own lock inside <c>UpdateInBuffer</c>. The re-descent on an obsolete
    /// latch is unbounded for the reason #716 records — a merge can detach the leaf between finding it and locking it, and each pass sees a tree one merge
    /// closer to settled.
    /// </para>
    /// <para>
    /// <b>Internal rather than private so it can be tested directly.</b> <c>MaxOptimisticRestarts</c> is a <c>const</c>, so no test can shrink the budget to
    /// force the optimistic path into here without turning six hot loops into field reads. The contention test proves the ROUTING (the operation never
    /// answers <c>false</c> for a key that exists, and <c>PessimisticFallbacks</c> moves); calling this directly proves the BODY.
    /// </para>
    /// </remarks>
    internal bool TryUpdateValueAtPessimistic(TKey key, int elementId, int oldValue, int newValue, ref ChunkAccessor<TStore> opAccessor,
        ref ChunkAccessor<TStore> sibAccessor)
    {
        // IXW-06: the leaf must OWN the key's range before a miss on it means "absent" — FindLeaf alone can land one leaf to the right under a concurrent
        // merge, and this path used to read that as not-found (the third copy of #887's second face; the other two were MoveValue's and RemoveValue's).
        if (!TryLatchAuthoritativeLeaf(key, ref opAccessor, out var leaf))
        {
            return false;
        }

        // Re-find under the lock, and re-resolve the latch through the node id on every use: GetLatch hands out a reference into the chunk's page and the
        // reads in between can evict that page and reuse the slot.
        var index = leaf.Find(key, Comparer, ref opAccessor);
        if (index < 0)
        {
            leaf.GetLatch(ref opAccessor).AbortWriteLock();
            return false;
        }

        var bufferId = leaf.GetItem(index, ref opAccessor).Value;
        try
        {
            return _storage.UpdateInBuffer(bufferId, elementId, oldValue, newValue, ref sibAccessor);
        }
        finally
        {
            // WriteUnlock even when the element was not found, and even when UpdateInBuffer threw — the optimistic path's `finally` carries the full
            // argument; without it a LockBuffer timeout leaves the leaf write-locked for the life of the process.
            leaf.GetLatch(ref opAccessor).WriteUnlock();
        }
    }
}
