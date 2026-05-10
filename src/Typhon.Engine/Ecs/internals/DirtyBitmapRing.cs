using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Ring buffer of archived <see cref="DirtyBitmap"/> snapshots, one per tick. Enables multi-tick delta accumulation for interest management: an observer
/// N ticks behind can OR the relevant ring slots to get a combined dirty set.
/// Ring size is 64 ticks (~2.1 seconds at 30Hz). Observers staler than 64 ticks trigger a full-sync fallback.
/// </summary>
/// <remarks>
/// <para>Memory: 100K entities × 12.5KB/bitmap × 64 slots = 800KB per ring. Acceptable for server workloads.</para>
/// <para>Thread model: single writer (tick fence) + single reader (GetSpatialChanges). No concurrent access guards in v1.</para>
/// </remarks>
internal sealed class DirtyBitmapRing
{
    internal const int RingSize = 64;
    private const int RingMask = RingSize - 1;

    private readonly long[][] _ring;
    private readonly int[] _wordCounts;
    private long _headTick; // most recently archived tick (0 = no ticks archived yet)

    internal DirtyBitmapRing(int initialWordCount)
    {
        _ring = new long[RingSize][];
        _wordCounts = new int[RingSize];

        for (int i = 0; i < RingSize; i++)
        {
            _ring[i] = new long[initialWordCount];
        }
    }

    /// <summary>Most recently archived tick number. 0 means no ticks archived yet.</summary>
    internal long HeadTick => _headTick;

    /// <summary>
    /// Archive a dirty bitmap snapshot into the ring. Called from WriteTickFence after WAL serialization and spatial maintenance.
    /// The bitmap array is copied into the ring slot (not stored by reference) — the caller can discard the original.
    /// </summary>
    internal void Archive(long tickNumber, long[] bitmap, int wordCount)
    {
        int slot = (int)(tickNumber & RingMask);

        // Grow ring slot if needed (rare: only when ComponentTable grows between ticks)
        if (_ring[slot].Length < wordCount)
        {
            _ring[slot] = new long[wordCount];
        }

        Array.Copy(bitmap, _ring[slot], wordCount);

        // Clear any trailing words from a previous larger bitmap that used this slot
        if (_wordCounts[slot] > wordCount)
        {
            Array.Clear(_ring[slot], wordCount, _wordCounts[slot] - wordCount);
        }

        _wordCounts[slot] = wordCount;
        _headTick = tickNumber;
    }

    /// <summary>
    /// Check if a tick is still available in the ring (hasn't been overwritten).
    /// </summary>
    internal bool IsTickAvailable(long tick) => _headTick > 0 && tick > _headTick - RingSize && tick <= _headTick;

    /// <summary>
    /// Accumulate (bitwise OR) all archived dirty bitmaps in the tick range [startTick, endTick] into the scratch buffer.
    /// The scratch buffer must be pre-cleared by the caller.
    /// Returns the maximum word count encountered across all accumulated slots.
    /// </summary>
    /// <param name="startTick">First tick to include (inclusive).</param>
    /// <param name="endTick">Last tick to include (inclusive). Must equal <see cref="HeadTick"/> or earlier.</param>
    /// <param name="scratchBuffer">Pre-cleared accumulation buffer. Must be at least as large as the largest ring slot.</param>
    /// <returns>Maximum word count across accumulated slots.</returns>
    internal int AccumulateDirty(long startTick, long endTick, long[] scratchBuffer)
    {
        int maxWords = 0;

        for (long t = startTick; t <= endTick; t++)
        {
            int slot = (int)(t & RingMask);
            int slotWords = _wordCounts[slot];
            if (slotWords > maxWords)
            {
                maxWords = slotWords;
            }

            int orWords = Math.Min(slotWords, scratchBuffer.Length);
            var slotBits = _ring[slot];
            for (int w = 0; w < orWords; w++)
            {
                scratchBuffer[w] |= slotBits[w];
            }
        }

        return maxWords;
    }

    /// <summary>Maximum word count across all currently archived slots. Used to size scratch buffers.</summary>
    internal int MaxWordCount
    {
        get
        {
            int max = 0;
            for (int i = 0; i < RingSize; i++)
            {
                if (_wordCounts[i] > max)
                {
                    max = _wordCounts[i];
                }
            }
            return max;
        }
    }
}
