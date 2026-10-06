using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Supplies one staged entry's key and value bytes to <c>InsertNewBulk</c> (#1100) — the insert twin of <see cref="IRawBulkUpdater{TKey,TEntry}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A struct type parameter, never an interface instance</b>, for the same reason the updater is shaped this way: the JIT monomorphises the bulk loop on
/// it, so the three members inline and nothing dispatches per entry.
/// </para>
/// <para>
/// <b><see cref="WriteValue"/> takes a destination span rather than returning a pointer</b>, and that is the project's pointer rule rather than taste. The
/// destination is inside a page-cache chunk, which the engine owns, so the map is free to address it; the SOURCE is the caller's staged batch, which is a
/// managed array. A <c>byte*</c> derived from that would be a pointer over GC-allocated data — the arrangement behind the SWG x64
/// <c>0x80131506</c> crash. Handing the inserter a span of engine memory to copy into keeps every pointer on the side that owns its memory.
/// </para>
/// </remarks>
internal interface IRawBulkInserter<TKey, TEntry>
    where TKey : unmanaged, IEquatable<TKey>
    where TEntry : struct
{
    /// <summary>The map key this entry will be inserted under.</summary>
    TKey KeyOf(in TEntry entry);

    /// <summary>
    /// The bucket index the caller sorted this entry by — what <see cref="RawValuePagedHashMap{TKey,TStore}.BucketIndexOf"/> returned when the batch was
    /// built. Trusted rather than re-derived, so the loop does not hash every key a second time purely to find where a run ends.
    /// </summary>
    int BucketOf(in TEntry entry);

    /// <summary>Copies this entry's value bytes into <paramref name="destination"/>, which is exactly the map's value size.</summary>
    void WriteValue(in TEntry entry, Span<byte> destination);
}

unsafe partial class RawValuePagedHashMap<TKey, TStore>
    where TKey : unmanaged, IEquatable<TKey>
    where TStore : struct, IPageStore
{
    /// <summary>
    /// Inserts a bucket-sorted batch of known-unique keys, one lock and one chain walk per bucket, performing NO split (#1100).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The missing split is the point, not an omission.</b> <see cref="InsertNew"/> ends with <c>TrySplitIfNeeded</c>, and a split is the one thing an
    /// insert does that is not bucket-local: it takes the global split lock, splits the round-robin bucket <c>Next</c> rather than the bucket being
    /// inserted into, and rewrites the directory and the meta. Inside a region whose workers own disjoint buckets, that crosses every partition at once.
    /// The caller therefore runs the splits first, with <see cref="PagedHashMapBase{TStore}.AdvanceHashStateFor"/>, and this method is free to assume
    /// the state has room.
    /// </para>
    /// <para>
    /// <b>It refuses rather than splits if the assumption is wrong.</b> A load factor over the threshold here means the pre-advance was not run, or was run
    /// for a smaller batch, and silently splitting would corrupt the very partitioning the caller built. The <c>PackedMeta</c> re-check inside the
    /// bucket loop is the same guard <c>UpdateValuesBulk</c> carries and for the same reason: the batch's bucket indices were resolved before the region
    /// opened, so a resize mid-batch makes every one of them name the wrong chain.
    /// </para>
    /// <para>
    /// <b>Known-unique keys, like <see cref="InsertNew"/>.</b> No duplicate scan, which is what makes a run cheap — the chain is walked once to its tail and
    /// then appended to, rather than walked per entry. Freshly generated entity keys are unique by construction.
    /// </para>
    /// </remarks>
    /// <returns>Entries inserted — always the batch length, since a known-unique insert cannot be skipped.</returns>
    public int InsertNewBulk<TEntry, TInserter>(Span<TEntry> sortedByBucket, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
        where TEntry : struct
        where TInserter : struct, IRawBulkInserter<TKey, TEntry>
    {
        _fenceWindow?.NoteMutation("EntityMap.InsertNewBulk");

        if (sortedByBucket.Length == 0)
        {
            return 0;
        }

        TInserter inserter = default;
        var packed = PackedMeta;

        // The caller's contract, checked rather than trusted: if the state cannot hold the batch, some insert below would have wanted a split, and the
        // partitioning the caller built is already invalid. Loud here beats corrupt later.
        var (_, _, bucketCount) = UnpackMeta(packed);
        var projected = Interlocked.Read(ref _entryCount) + sortedByBucket.Length;
        if ((double)projected / ((long)bucketCount * _bucketCapacity) > MaxLoadFactorForBulk)
        {
            ThrowHelper.ThrowInvalidOp(
                $"InsertNewBulk was given {sortedByBucket.Length} entries, which would take the map to {projected} entries over {bucketCount} buckets — "
                + "past the split threshold. This method never splits, because a split is not bucket-local and would cross the caller's partitions. Call "
                + "AdvanceHashStateFor(batchSize) while the map is quiescent, before opening the parallel region.");
        }

        var inserted = 0;
        var runStart = 0;

        while (runStart < sortedByBucket.Length)
        {
            var bucket = inserter.BucketOf(sortedByBucket[runStart]);
            var runEnd = runStart + 1;
            while (runEnd < sortedByBucket.Length && inserter.BucketOf(sortedByBucket[runEnd]) == bucket)
            {
                runEnd++;
            }

            inserted += InsertBucketRun<TEntry, TInserter>(sortedByBucket[runStart..runEnd], bucket, packed, ref inserter, ref accessor, changeSet);
            runStart = runEnd;
        }

        return inserted;
    }

    /// <summary>Appends one bucket's run under a single write lock.</summary>
    /// <remarks>
    /// IXW-07: the overflow chunks the run needs are reserved with the lock released, then the bucket is locked and measured again; nothing under the lock
    /// allocates. See <see cref="AppendUnderBucketLock"/>.
    /// </remarks>
    private int InsertBucketRun<TEntry, TInserter>(Span<TEntry> run, int bucket, long packed, ref TInserter inserter,
        ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
        where TEntry : struct
        where TInserter : struct, IRawBulkInserter<TKey, TEntry>
    {
        var reservation = ChunkReservation<TStore>.Current;
        reservation.Begin(Segment);
        try
        {
            return InsertBucketRunReserved(run, bucket, packed, ref inserter, ref accessor, changeSet, reservation);
        }
        finally
        {
            reservation.End();
        }
    }

    private int InsertBucketRunReserved<TEntry, TInserter>(Span<TEntry> run, int bucket, long packed, ref TInserter inserter,
        ref ChunkAccessor<TStore> accessor, ChangeSet changeSet, ChunkReservation<TStore> reservation)
        where TEntry : struct
        where TInserter : struct, IRawBulkInserter<TKey, TEntry>
    {
        while (true)
        {
            if (PackedMeta != packed)
            {
                ThrowHelper.ThrowInvalidOp(
                    "InsertNewBulk saw the map resize mid-batch. The batch was sorted by bucket indices read before the split, so those indices no longer "
                    + "name the buckets the keys resolve to and inserting them would write the wrong chains.");
            }

            var rootChunkId = GetBucketChunkId(bucket, ref accessor);
            var rootAddr = accessor.GetChunkAddress(rootChunkId, true);
            ref var header = ref GetHeader(rootAddr);
            var latch = new OlcLatch(ref header.OlcVersion);
            if (!latch.TryWriteLock())
            {
                continue;
            }

            if (PackedMeta != packed)
            {
                latch.AbortWriteLock();
                continue;
            }

            int need;
            try
            {
                need = OverflowChunksFor(rootChunkId, run.Length, ref accessor);
            }
            catch
            {
                ReleaseAfterFault(rootChunkId, written: false, ref accessor);
                throw;
            }

            if (reservation.Available < need)
            {
                HeadLatchForRelease(rootChunkId, ref accessor).AbortWriteLock();
                reservation.Fill(need, changeSet, ref accessor);
                continue;
            }

            // The write phase runs the caller's WriteValue under the lock, so it can fault: the lock is released with a version bump, and the entries already
            // in the bucket — each one whole, as AppendEntryFrom counts an entry only after writing it — are counted.
            var appended = 0;
            try
            {
                for (; appended < run.Length; appended++)
                {
                    AppendEntryFrom<TEntry, TInserter>(rootChunkId, inserter.KeyOf(run[appended]), in run[appended], ref inserter, ref accessor, changeSet);
                }

                // Re-fetch for the unlock: chaining an overflow chunk above may have evicted and reloaded the primary.
                var unlockAddr = accessor.GetChunkAddress(rootChunkId, true);
                new OlcLatch(ref GetHeader(unlockAddr).OlcVersion).WriteUnlock();
            }
            catch
            {
                Interlocked.Add(ref _entryCount, appended);
                ReleaseAfterFault(rootChunkId, written: true, ref accessor);
                throw;
            }

            Interlocked.Add(ref _entryCount, run.Length);
            return run.Length;
        }
    }

    /// <summary>New overflow chunks appending <paramref name="entries"/> entries to the chain at <paramref name="startChunkId"/> takes.</summary>
    private int OverflowChunksFor(int startChunkId, int entries, ref ChunkAccessor<TStore> accessor)
    {
        var chunkId = startChunkId;
        while (true)
        {
            ref readonly var header = ref GetHeader(accessor.GetChunkAddress(chunkId));
            if (header.OverflowChunkId == -1)
            {
                var beyondTail = entries - (_bucketCapacity - header.EntryCount);
                return beyondTail <= 0 ? 0 : (beyondTail + _bucketCapacity - 1) / _bucketCapacity;
            }

            chunkId = header.OverflowChunkId;
        }
    }

    /// <summary>
    /// <see cref="AppendEntry"/>, sourcing the value through the inserter instead of from a pointer.
    /// </summary>
    /// <remarks>
    /// Deliberately a sibling rather than a refactor of <see cref="AppendEntry"/>: that method's body carries the #301 ordering argument for initialising
    /// an overflow chunk fully BEFORE linking it, with a comment longer than the code, and the two must not drift. The ordering here is the same and is
    /// repeated in short form; the shape to compare against is next door.
    /// </remarks>
    private void AppendEntryFrom<TEntry, TInserter>(int startChunkId, TKey key, in TEntry entry, ref TInserter inserter,
        ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
        where TEntry : struct
        where TInserter : struct, IRawBulkInserter<TKey, TEntry>
    {
        var chunkId = startChunkId;

        while (true)
        {
            var addr = accessor.GetChunkAddress(chunkId, true);
            ref var header = ref GetHeader(addr);

            if (header.EntryCount < _bucketCapacity)
            {
                var idx = header.EntryCount;
                KeysPtr(addr)[idx] = key;
                inserter.WriteValue(entry, new Span<byte>(ValueAt(addr, idx), _valueSize));
                header.EntryCount = (byte)(idx + 1);
                return;
            }

            if (header.OverflowChunkId != -1)
            {
                chunkId = header.OverflowChunkId;
                continue;
            }

            // #301: initialise the new chunk FULLY — including the -1 end-of-chain sentinel — before linking it from the predecessor, or a checkpoint
            // snapshotting the gap persists a chain ending at chunk 0 (the meta) and the corruption becomes permanent on the next reload. IXW-07: reserved by
            // InsertBucketRun before it took the bucket lock.
            var overflowChunkId = ChunkReservation<TStore>.AllocateUnderLatch(Segment, changeSet, ref accessor);
            Interlocked.Increment(ref _overflowChunksChained);

            var ovAddr = accessor.GetChunkAddress(overflowChunkId, true);
            ref var ovHeader = ref GetHeader(ovAddr);
            ovHeader.OlcVersion = 0;
            ovHeader.EntryCount = 1;
            ovHeader.Flags = 0;
            ovHeader.Reserved = 0;
            ovHeader.OverflowChunkId = -1;
            KeysPtr(ovAddr)[0] = key;
            try
            {
                inserter.WriteValue(entry, new Span<byte>(ValueAt(ovAddr, 0), _valueSize));
            }
            catch
            {
                // Not linked yet: back to the reservation, which frees it, rather than allocated with nothing reaching it.
                ChunkReservation<TStore>.ReturnUnlinked(Segment, overflowChunkId);
                throw;
            }

            addr = accessor.GetChunkAddress(chunkId, true);
            GetHeader(addr).OverflowChunkId = overflowChunkId;
            return;
        }
    }
}
