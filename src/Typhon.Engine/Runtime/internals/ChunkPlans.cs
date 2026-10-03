using System;

namespace Typhon.Engine.Internals;

/// <summary>The check every typed chunked prepare passes through, whichever front-end declared it.</summary>
internal static class ChunkPlans
{
    /// <summary>
    /// Returns <paramref name="chunks"/> after refusing a prepare that dispatches more chunks than it wrote records for — at prepare, on one thread,
    /// rather than in the chunks that would find no record. -1 (keep the static count) and 0 (skip) pass: the chunk itself checks its index (CD-03).
    /// </summary>
    public static int Checked(int chunks, int records)
    {
        if (chunks > records)
        {
            throw new InvalidOperationException(
                $"Prepare dispatches {chunks} chunk(s) but wrote {records} record(s): Reset the ChunkTable to the chunk count it returns.");
        }

        return chunks;
    }
}
