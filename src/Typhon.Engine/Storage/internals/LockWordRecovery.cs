using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// Frees the lock words a crash left held in a chunk segment's data file (PS-20). A capture can copy a held lock word, and only the checkpoint after the
/// release rewrites it: a crash before that one leaves the hold on disk, its holder gone. Run on a crash open as the segment loads, before anything of
/// the open takes one of its locks.
/// </summary>
internal static class LockWordRecovery
{
    /// <summary>
    /// Pages a recovery walk passes between two moves of its epoch. The scan accessor keeps only its window, slot-pinned, but the walk's per-chunk
    /// allocation check (<see cref="ChunkBasedSegment{TStore}.IsChunkAllocated"/>) requests each page tagged with the walk's epoch: one scope for a whole
    /// segment larger than the cache left every page it passed unevictable — the 29 GB SWG world's 6 GB revision segment timed out on back-pressure at
    /// open, 65,536 of 65,536 slots epoch-protected (512 MiB cache). Counted in pages, through the segment's chunks per page: a collection's chunk can be
    /// 2 KiB, and a count of chunks would then span a small cache.
    /// </summary>
    internal const int PagesPerEpoch = 64;

    /// <summary>Test seam: chunks between two moves for this thread's walks, in place of <see cref="PagesPerEpoch"/>'s; 0 for the default.</summary>
    [ThreadStatic]
    internal static int ChunksPerEpochForTests;

    private static int ChunksPerEpochOf(ChunkBasedSegment<PersistentStore> segment)
        => ChunksPerEpochForTests > 0 ? ChunksPerEpochForTests : Math.Max(1, PagesPerEpoch * Math.Max(1, segment.ChunkCountPerPage));

    /// <summary>
    /// Between two chunks, once every <see cref="ChunksPerEpochOf"/>: moves the thread's epoch — every scope's pin on it, as a recovery batch point does —
    /// and replaces the scan, whose window the next chunk re-pins. No address read before it is used after it.
    /// </summary>
    private static void MoveEpoch(ChunkBasedSegment<PersistentStore> segment, DatabaseEngine dbe, ref ChunkAccessor<PersistentStore> scan)
    {
        scan.Dispose();
        ChunkBasedSegment<PersistentStore>.RefreshWarmCacheEpoch(dbe.EpochManager.RefreshScope());
        scan = segment.CreateScanAccessor();
    }

    /// <summary>
    /// Whether <paramref name="chunkId"/>, a chunk no other chunk names as its next, is a root of its layout — read through <paramref name="scan"/>.
    /// </summary>
    internal delegate bool RootCheck(ref ChunkAccessor<PersistentStore> scan, ChunkBasedSegment<PersistentStore> segment, int chunkId);

    /// <summary>
    /// Clears the lock word at <paramref name="lockOffset"/>, <paramref name="lockSize"/> bytes, in every root chunk of <paramref name="segment"/>, a chained
    /// layout whose every chunk starts with the id of the next one: an allocated chunk no other chunk names as its next, that
    /// <paramref name="isRoot"/> accepts. Single-threaded; a root is dirtied only when its word was set.
    /// </summary>
    /// <remarks>
    /// Read through a scan accessor, which keeps only its window of pages: the segment may be larger than the cache, and an epoch-tagged accessor would
    /// pin every page it touched until the open's scope ends. <paramref name="isRoot"/> is what keeps an unnamed chunk that is no root — a chain torn by
    /// the crash, its link to the next chunk lost with a page the last checkpoint did not write; a buffer's first free chunk — from having data bytes
    /// cleared: it checks the chunk against its layout's root header.
    /// </remarks>
    /// <returns>The roots whose lock word was set.</returns>
    internal static unsafe int ResetRootLocks(ChunkBasedSegment<PersistentStore> segment, DatabaseEngine dbe, int lockOffset, int lockSize, RootCheck isRoot)
    {
        var capacity = segment.ChunkCapacity;
        if (capacity == 0 || segment.AllocatedChunkCount == 0)
        {
            return 0;
        }

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var held = new List<int>();
        var scan = segment.CreateScanAccessor();
        var every = ChunksPerEpochOf(segment);
        try
        {
            var named = new ulong[(capacity + 63) / 64];
            for (var chunkId = 0; chunkId < capacity; chunkId++)
            {
                if (chunkId > 0 && chunkId % every == 0)
                {
                    MoveEpoch(segment, dbe, ref scan);
                }

                if (segment.IsChunkAllocated(chunkId))
                {
                    var next = *(int*)scan.GetChunkAddress(chunkId);
                    if (next > 0 && next < capacity)
                    {
                        named[next >> 6] |= 1UL << (next & 63);
                    }
                }
            }

            for (var chunkId = 0; chunkId < capacity; chunkId++)
            {
                if (chunkId > 0 && chunkId % every == 0)
                {
                    MoveEpoch(segment, dbe, ref scan);
                }

                if ((named[chunkId >> 6] & (1UL << (chunkId & 63))) != 0 || !segment.IsChunkAllocated(chunkId))
                {
                    continue;
                }

                var word = scan.GetChunkAddress(chunkId) + lockOffset;
                var set = false;
                for (var i = 0; i < lockSize; i++)
                {
                    set |= word[i] != 0;
                }

                if (set && isRoot(ref scan, segment, chunkId))
                {
                    held.Add(chunkId);
                }
            }
        }
        finally
        {
            scan.Dispose();
        }

        return Clear(segment, dbe, lockOffset, lockSize, held);
    }

    /// <summary>
    /// Clears the lock word at <paramref name="lockOffset"/> in each of <paramref name="roots"/> whose word is set — roots found otherwise than by the
    /// scan above (a revision segment's named chunks its EntityMap rebuild adopted). Single-threaded; returns the roots whose word was set.
    /// </summary>
    internal static unsafe int ResetLocks(ChunkBasedSegment<PersistentStore> segment, DatabaseEngine dbe, int lockOffset, int lockSize, IEnumerable<int> roots)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var held = new List<int>();
        var scan = segment.CreateScanAccessor();
        try
        {
            var read = 0;
            var every = ChunksPerEpochOf(segment);
            foreach (var chunkId in roots)
            {
                if (++read % every == 0)
                {
                    MoveEpoch(segment, dbe, ref scan);
                }

                var word = scan.GetChunkAddress(chunkId) + lockOffset;
                for (var i = 0; i < lockSize; i++)
                {
                    if (word[i] != 0)
                    {
                        held.Add(chunkId);
                        break;
                    }
                }
            }
        }
        finally
        {
            scan.Dispose();
        }

        return Clear(segment, dbe, lockOffset, lockSize, held);
    }

    private static unsafe int Clear(ChunkBasedSegment<PersistentStore> segment, DatabaseEngine dbe, int lockOffset, int lockSize, List<int> held)
    {
        if (held.Count == 0)
        {
            return 0;
        }

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var changeSet = dbe.MMF.CreateChangeSet();
        var accessor = segment.CreateChunkAccessor(changeSet);
        try
        {
            foreach (var chunkId in held)
            {
                new Span<byte>(accessor.GetChunkAddress(chunkId, true) + lockOffset, lockSize).Clear();
            }
        }
        finally
        {
            accessor.Dispose();
            changeSet.SaveChanges();
        }

        return held.Count;
    }
}
