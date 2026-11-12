using System;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Internals;

unsafe partial class RawValuePagedHashMap<TKey, TStore>
    where TKey : unmanaged, IEquatable<TKey>
    where TStore : struct, IPageStore
{
    // The bulk build in progress: the next bucket whose chunk is not taken yet, and the bucket being filled with the last chunk of its chain. -1 when none.
    private int _bulkNextBucket = -1;
    private int _bulkBucket = -1;
    private int _bulkTailChunk;

    /// <summary>
    /// Starts a crash-recovery bulk build of a map that will hold <paramref name="entries"/> entries (#1665): the map is emptied at its final bucket count,
    /// which never splits during the build, then filled bucket by bucket in increasing order (<see cref="BulkBuildAppend"/>) and closed by
    /// <see cref="EndBulkBuild"/>. The buckets are consecutive chunks, so the build writes the map's pages in order, each once — where inserting the entries
    /// in their discovery order dirtied a random page per entry and split the map as it grew, which rewrote a map larger than the cache many times over.
    /// Single-threaded, on a map nothing else reads or writes until the build ends. Returns the bucket count, which <see cref="BucketIndexOf"/> now resolves
    /// against.
    /// </summary>
    internal int BeginBulkBuild(long entries, ChangeSet changeSet)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entries);
        Debug.Assert(_bulkNextBucket < 0, "a bulk build is already in progress");
        var bucketCount = BulkBuildBucketCount(entries);
        ResetForBulkBuild(bucketCount, changeSet);
        _bulkNextBucket = 0;
        _bulkBucket = -1;
        return bucketCount;
    }

    /// <summary>
    /// Appends <paramref name="key"/> to bucket <paramref name="bucket"/> — <see cref="BucketIndexOf"/> of the key, never below the previous call's — and
    /// returns where its value goes: <see cref="ValueSize"/> bytes of page-cache memory, valid until the next call through <paramref name="accessor"/>. The
    /// buckets skipped since the previous call are taken empty. Keys are known unique, as the source they are derived from guarantees: nothing is checked.
    /// </summary>
    internal byte* BulkBuildAppend(TKey key, int bucket, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        Debug.Assert(_bulkNextBucket >= 0, "no bulk build in progress");
        Debug.Assert(bucket == BucketIndexOf(key), "the bucket given is not the key's");
        if (bucket != _bulkBucket)
        {
            if (bucket < _bulkNextBucket)
            {
                ThrowHelper.ThrowInvalidOp($"PagedHashMap bulk build: bucket {bucket} came after bucket {_bulkNextBucket - 1}; the build fills them in order.");
            }

            for (; _bulkNextBucket <= bucket; _bulkNextBucket++)
            {
                ReserveBucketForBulkBuild(_bulkNextBucket, ref accessor, changeSet);
            }

            _bulkBucket = bucket;
            _bulkTailChunk = BucketChunkId(bucket);
        }

        var addr = accessor.GetChunkAddress(_bulkTailChunk, true);
        if (GetHeader(addr).EntryCount >= _bucketCapacity)
        {
            // The overflow chunk initialized whole before it is linked, as AppendEntry does (#301). The floor keeps it above every bucket.
            var overflowChunkId = Segment.AllocateChunk(changeSet, ref accessor);
            var ovAddr = accessor.GetChunkAddress(overflowChunkId, true);
            ref var ovHeader = ref GetHeader(ovAddr);
            TagOverflowOwner(ref ovHeader, bucket);
            ovHeader.EntryCount = 0;
            ovHeader.Flags = 0;
            ovHeader.Reserved = 0;
            ovHeader.OverflowChunkId = -1;
            GetHeader(accessor.GetChunkAddress(_bulkTailChunk, true)).OverflowChunkId = overflowChunkId;
            _overflowChunksChained++;
            _bulkTailChunk = overflowChunkId;
            addr = accessor.GetChunkAddress(overflowChunkId, true);
        }

        ref var header = ref GetHeader(addr);
        var index = header.EntryCount;
        KeysPtr(addr)[index] = key;
        header.EntryCount = (byte)(index + 1);
        _entryCount++;
        return ValueAt(addr, index);
    }

    /// <summary>Ends the bulk build: the buckets no entry reached are taken empty and the counts persisted.</summary>
    internal void EndBulkBuild(ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        Debug.Assert(_bulkNextBucket >= 0, "no bulk build in progress");
        for (var bucketCount = BucketCount; _bulkNextBucket < bucketCount; _bulkNextBucket++)
        {
            ReserveBucketForBulkBuild(_bulkNextBucket, ref accessor, changeSet);
        }

        PersistBulkBuildMeta(ref accessor);
        Volatile.Write(ref _bulkNextBucket, -1);
        _bulkBucket = -1;
    }
}
