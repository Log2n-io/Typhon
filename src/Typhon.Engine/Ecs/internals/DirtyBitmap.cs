using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Lock-free per-ComponentTable dirty tracking for <see cref="StorageMode.SingleVersion"/> components.
/// Each bit represents one chunkId in the ComponentSegment. Set atomically via <see cref="Interlocked.Or"/>.
/// Tick fence (3.4) calls <see cref="Snapshot"/> to atomically swap the bitmap and serialize dirty entries to WAL.
/// </summary>
/// <remarks>
/// <para>Size: 500K entities = 62.5 KB (500K / 64 bits per long × 8 bytes per long).</para>
/// <para>Thread safety: <see cref="Set"/> uses <see cref="Interlocked.Or"/> (multiple concurrent writers).
/// <see cref="Snapshot"/> uses <see cref="Interlocked.Exchange"/> (single reader at tick fence time).</para>
/// </remarks>
internal sealed class DirtyBitmap
{
    private long[] _bits;
    private readonly Lock _growLock = new();

    internal DirtyBitmap(int initialCapacity)
    {
        var wordCount = Math.Max(1, (initialCapacity + 63) >> 6);
        _bits = new long[wordCount];
    }

    /// <summary>Atomically mark a chunkId as dirty. Thread-safe for concurrent writers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Set(int chunkId)
    {
        var wordIndex = chunkId >> 6;
        var bits = _bits;

        if (wordIndex >= bits.Length)
        {
            bits = Grow(wordIndex);
        }

        Interlocked.Or(ref bits[wordIndex], 1L << (chunkId & 63));
    }

    /// <summary>
    /// Atomically set a bit and return whether it was already set.
    /// Used by shadow capture to detect first-write-per-entity-per-tick.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TestAndSet(int chunkId)
    {
        var wordIndex = chunkId >> 6;
        var bits = _bits;

        if (wordIndex >= bits.Length)
        {
            bits = Grow(wordIndex);
        }

        long mask = 1L << (chunkId & 63);
        long prev = Interlocked.Or(ref bits[wordIndex], mask);
        return (prev & mask) != 0;
    }

    /// <summary>Check if a bit is set without modifying state. On x64, long reads are naturally atomic.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Test(int chunkId)
    {
        var wordIndex = chunkId >> 6;
        var bits = _bits;
        if (wordIndex >= bits.Length)
        {
            return false;
        }

        long mask = 1L << (chunkId & 63);
        return (bits[wordIndex] & mask) != 0;
    }

    /// <summary>Reset all bits to zero. Not thread-safe — call only when no concurrent writers are active.</summary>
    internal void Clear() => Array.Clear(_bits);

    /// <summary>
    /// TEST-ONLY: forcibly shrink the internal bits array to <paramref name="wordCount"/> words.
    /// Used by regression tests that need to simulate a snapshot length smaller than a subsequent destination chunk id, to exercise the <c>ExecuteMigrations</c>
    /// dirtyBits growth path. Not thread-safe; all bits beyond the truncation point are discarded.
    /// </summary>
    internal void ShrinkForTesting(int wordCount)
    {
        if (wordCount < 1)
        {
            wordCount = 1;
        }
        var shrunk = new long[wordCount];
        var bits = _bits;
        var copyLen = Math.Min(bits.Length, wordCount);
        Array.Copy(bits, shrunk, copyLen);
        _bits = shrunk;
    }

    /// <summary>
    /// Atomically swap the current bitmap with a fresh empty one.
    /// Returns the old bitmap containing all dirty bits since the last snapshot.
    /// Called by tick fence serialization (3.4) — outside hot write path.
    /// </summary>
    internal long[] Snapshot()
    {
        var current = _bits;
        return Interlocked.Exchange(ref _bits, new long[current.Length]);
    }

    /// <summary>Returns true if any bit is set (fast skip for tick fence). On x64, long reads are naturally atomic.</summary>
    internal bool HasDirty
    {
        get
        {
            var bits = _bits;
            for (var i = 0; i < bits.Length; i++)
            {
                if (bits[i] != 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    private long[] Grow(int requiredWordIndex)
    {
        lock (_growLock)
        {
            var bits = _bits;
            if (requiredWordIndex < bits.Length)
            {
                return bits;
            }

            var newLength = Math.Max(bits.Length * 2, requiredWordIndex + 1);
            var newBits = new long[newLength];
            Array.Copy(bits, newBits, bits.Length);
            _bits = newBits;
            return newBits;
        }
    }
}
