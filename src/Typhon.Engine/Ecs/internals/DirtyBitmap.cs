using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Lock-free per-ComponentTable dirty tracking for <see cref="Typhon.Schema.Definition.StorageMode.SingleVersion"/> components.
/// Each bit represents one chunkId in the ComponentSegment. Set atomically via <see cref="Interlocked.Or(ref long, long)"/>.
/// Tick fence (3.4) calls <see cref="Snapshot"/> to atomically swap the bitmap and serialize dirty entries to WAL.
/// </summary>
/// <remarks>
/// <para>Size: 500K entities = 62.5 KB (500K / 64 bits per long × 8 bytes per long).</para>
/// <para>Thread safety: <see cref="Set"/> and <see cref="TestAndSet"/> support multiple concurrent writers; <see cref="Snapshot"/> is a single
/// reader at tick fence time. Per-word atomicity alone is NOT sufficient here, because <see cref="Grow"/> and <see cref="Snapshot"/> both replace
/// <c>_bits</c> wholesale: a writer that captured the previous array and is descheduled before its <see cref="Interlocked.Or(ref long, long)"/>
/// would perform an atomic OR on an array nobody reads any more, and the bit would be silently lost (#709). Both writers therefore re-read
/// <c>_bits</c> after the OR and repeat against the new array when a swap slipped in — the array reference is the unit of currency, not the word.</para>
/// <para>A lost bit is a dirty chunk that is never checkpointed, i.e. silent data loss with no error and no CRC mismatch on a perfectly clean
/// shutdown — which is why the retry is unconditional rather than best-effort.</para>
/// <para>The retry cannot livelock: it only repeats when another thread has already published a new array, so every iteration is preceded by
/// system-wide progress. Swaps are rare by construction — <see cref="Grow"/> doubles (O(log n) over the bitmap's whole life) and
/// <see cref="Snapshot"/> fires once per tick fence.</para>
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
        long mask = 1L << (chunkId & 63);

        while (true)
        {
            var bits = Volatile.Read(ref _bits);   // acquire: pairs with the release publish in Grow and the Exchange in Snapshot
            if (wordIndex >= bits.Length)
            {
                bits = Grow(wordIndex);
            }

            Interlocked.Or(ref bits[wordIndex], mask);   // full fence — orders the currency re-read below after the OR

            if (Volatile.Read(ref _bits) == bits)
            {
                return;   // the array we wrote into is still the live one, so the bit is reachable by the next reader
            }

            // A Grow or a Snapshot swapped the array under us. Our OR may have landed after the copy / after the Exchange handed the old
            // array to its consumer, so it may be unreachable — set it again on whatever is current now. Setting it twice is harmless: the
            // worst case is one chunk serialized in two consecutive tick fences.
        }
    }

    /// <summary>
    /// Atomically set a bit and return whether it was already set.
    /// Used by shadow capture to detect first-write-per-entity-per-tick.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TestAndSet(int chunkId)
    {
        var wordIndex = chunkId >> 6;
        long mask = 1L << (chunkId & 63);
        var haveVerdict = false;
        var wasAlreadySet = false;

        while (true)
        {
            var bits = Volatile.Read(ref _bits);   // acquire: pairs with the release publish in Grow and the Exchange in Snapshot
            if (wordIndex >= bits.Length)
            {
                bits = Grow(wordIndex);
            }

            long prev = Interlocked.Or(ref bits[wordIndex], mask);   // full fence — orders the currency re-read below after the OR

            // The FIRST OR is the only honest verdict, and a retry must not overwrite it: Grow copies the old array forward, so a second OR
            // can report "already set" purely because it is observing OUR bit copied into the new array. Trusting that would make the first
            // writer skip a shadow capture that never happened. Keeping the first observation errs the safe way — a duplicate capture at
            // worst, never a missing one.
            if (!haveVerdict)
            {
                wasAlreadySet = (prev & mask) != 0;
                haveVerdict = true;
            }

            if (Volatile.Read(ref _bits) == bits)
            {
                return wasAlreadySet;
            }
        }
    }

    /// <summary>Check if a bit is set without modifying state. On x64, long reads are naturally atomic.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Test(int chunkId)
    {
        var wordIndex = chunkId >> 6;
        var bits = Volatile.Read(ref _bits);
        if (wordIndex >= bits.Length)
        {
            return false;
        }

        long mask = 1L << (chunkId & 63);
        return (Volatile.Read(ref bits[wordIndex]) & mask) != 0;
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
            var bits = Volatile.Read(ref _bits);
            for (var i = 0; i < bits.Length; i++)
            {
                if (Volatile.Read(ref bits[i]) != 0)
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
            Volatile.Write(ref _bits, newBits);   // release: the copy above must be visible to any lock-free reader that sees this reference
            return newBits;
        }
    }
}
