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
    /// Clears the lock word at <paramref name="lockOffset"/>, <paramref name="lockSize"/> bytes, in every root chunk of <paramref name="segment"/>, a chained
    /// layout whose every chunk starts with the id of the next one: a root is an allocated chunk no other chunk names as its next. Single-threaded; a root
    /// is dirtied only when its word was set.
    /// </summary>
    /// <remarks>
    /// An allocated chunk no chain reaches is cleared too — a leaked one, or a buffer's first free chunk, which its root names by another field: neither
    /// holds data anyone reads.
    /// </remarks>
    /// <returns>The roots whose lock word was set.</returns>
    internal static unsafe int ResetRootLocks(ChunkBasedSegment<PersistentStore> segment, DatabaseEngine dbe, int lockOffset, int lockSize)
    {
        var capacity = segment.ChunkCapacity;
        if (capacity == 0 || segment.AllocatedChunkCount == 0)
        {
            return 0;
        }

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var changeSet = dbe.MMF.CreateChangeSet();
        var reset = 0;
        try
        {
            var accessor = segment.CreateChunkAccessor(changeSet);
            try
            {
                var named = new HashSet<int>();
                for (var chunkId = 0; chunkId < capacity; chunkId++)
                {
                    if (segment.IsChunkAllocated(chunkId))
                    {
                        var next = *(int*)accessor.GetChunkAddress(chunkId);
                        if (next != 0)
                        {
                            named.Add(next);
                        }
                    }
                }

                for (var chunkId = 0; chunkId < capacity; chunkId++)
                {
                    if (!segment.IsChunkAllocated(chunkId) || named.Contains(chunkId))
                    {
                        continue;
                    }

                    var word = accessor.GetChunkAddress(chunkId) + lockOffset;
                    var set = false;
                    for (var i = 0; i < lockSize; i++)
                    {
                        set |= word[i] != 0;
                    }

                    if (set)
                    {
                        new Span<byte>(accessor.GetChunkAddress(chunkId, true) + lockOffset, lockSize).Clear();
                        reset++;
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            changeSet.SaveChanges();
        }

        return reset;
    }
}
