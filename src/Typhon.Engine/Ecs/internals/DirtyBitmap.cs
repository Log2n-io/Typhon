using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Lock-free per-ComponentTable dirty tracking for <see cref="Typhon.Schema.Definition.StorageMode.SingleVersion"/> components.
/// Each bit represents one chunkId in the ComponentSegment. Set atomically via <see cref="Interlocked.Or(ref long, long)"/>.
/// Tick fence (3.4) calls <see cref="Snapshot"/> to drain the bitmap and serialize dirty entries to WAL.
/// </summary>
/// <remarks>
/// <para>Size: 500K entities = 62.5 KB of words (500K / 64 bits per long × 8 bytes per long), plus one reference per 8-word block.</para>
/// <para><b>Why the words live in fixed blocks (#709).</b> The obvious layout — one <c>long[]</c> replaced by a bigger one on growth, and swapped out
/// wholesale by <see cref="Snapshot"/> — cannot be made safe for concurrent writers, because a writer's <see cref="Interlocked.Or(ref long, long)"/> is
/// atomic on the array it captured, and that array can stop being the live one between the capture and the OR. The bit is then perfectly atomically set
/// on an object nobody will ever read again. Validating the array reference after the OR does not close it either: <c>Array.Copy</c> runs BEFORE the new
/// reference is published, so a writer landing in that window still sees its own array as current, returns satisfied, and loses the bit to a copy that has
/// already gone past its word. Measured on that design: contiguous runs of lost bits, 6 failures in 30 runs of
/// <c>DirtyBitmap_ConcurrentSetAcrossManyGrowths</c>.</para>
/// <para>So growth never moves a word. Only the outer block table is replaced, and it holds references: a writer that resolved its block through the old
/// table ORs into the very same block object the new table points at. There is no window to detect and no retry to get wrong.</para>
/// <para><see cref="Snapshot"/> drains in place (<see cref="Interlocked.Exchange(ref long, long)"/> per word) instead of swapping the storage out. A
/// concurrent <see cref="Set"/> therefore either lands before the exchange, and is reported by this snapshot, or after it, and stays set for the next one.
/// Neither outcome loses it.</para>
/// <para>A lost bit is a dirty chunk that is never checkpointed — silent data loss with no error and no CRC mismatch on a perfectly clean shutdown — which
/// is why this is worth an extra indirection on the write path rather than a cheaper design that is nearly right.</para>
/// <para>Thread safety: <see cref="Set"/> and <see cref="TestAndSet"/> take any number of concurrent writers. <see cref="Snapshot"/> is the single tick-fence
/// reader. <see cref="Clear"/> and <see cref="ShrinkForTesting"/> are single-threaded by contract.</para>
/// </remarks>
internal sealed class DirtyBitmap
{
    /// <summary>Words per block: 8 × 8 B = one 64-byte cache line, covering 512 chunk ids. Blocks are allocated whole and never move.</summary>
    private const int WordsPerBlockShift = 3;
    private const int WordsPerBlock = 1 << WordsPerBlockShift;
    private const int WordInBlockMask = WordsPerBlock - 1;

    /// <summary>Block table. The TABLE is replaced on growth; the blocks it points at are not, which is the whole point (see the class remarks).</summary>
    private long[][] _blocks;

    /// <summary>
    /// Logical word count — what <see cref="Snapshot"/> sizes its result to. Normally <c>_blocks.Length * WordsPerBlock</c>; kept as its own field so
    /// <see cref="ShrinkForTesting"/> can express a count that is not a whole number of blocks, which is what its callers are simulating.
    /// </summary>
    private int _wordCount;

    private readonly Lock _growLock = new();

    internal DirtyBitmap(int initialCapacity)
    {
        var wordCount = Math.Max(1, (initialCapacity + 63) >> 6);
        var blockCount = (wordCount + WordInBlockMask) >> WordsPerBlockShift;
        var blocks = new long[blockCount][];
        for (var i = 0; i < blockCount; i++)
        {
            blocks[i] = new long[WordsPerBlock];
        }
        _blocks = blocks;
        // The whole allocated capacity, not the requested word count: a block is allocated whole, so words inside it are writable and must therefore be
        // reportable. Rounding down instead would let Set(64) succeed on a bitmap built for 64 bits and then hide the bit from Snapshot.
        _wordCount = blockCount << WordsPerBlockShift;
    }

    /// <summary>Atomically mark a chunkId as dirty. Thread-safe for concurrent writers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Set(int chunkId)
    {
        var word = chunkId >> 6;
        var block = ResolveBlock(word >> WordsPerBlockShift);
        Interlocked.Or(ref block[word & WordInBlockMask], 1L << (chunkId & 63));
    }

    /// <summary>
    /// Atomically set a bit and return whether it was already set.
    /// Used by shadow capture to detect first-write-per-entity-per-tick.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TestAndSet(int chunkId)
    {
        var word = chunkId >> 6;
        var mask = 1L << (chunkId & 63);
        var block = ResolveBlock(word >> WordsPerBlockShift);
        return (Interlocked.Or(ref block[word & WordInBlockMask], mask) & mask) != 0;
    }

    /// <summary>Check if a bit is set without modifying state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Test(int chunkId)
    {
        var word = chunkId >> 6;
        var blockIndex = word >> WordsPerBlockShift;
        var blocks = Volatile.Read(ref _blocks);
        if (blockIndex >= blocks.Length)
        {
            return false;
        }

        return (Volatile.Read(ref blocks[blockIndex][word & WordInBlockMask]) & (1L << (chunkId & 63))) != 0;
    }

    /// <summary>Reset all bits to zero. Not thread-safe — call only when no concurrent writers are active.</summary>
    internal void Clear()
    {
        var blocks = Volatile.Read(ref _blocks);
        for (var i = 0; i < blocks.Length; i++)
        {
            Array.Clear(blocks[i]);
        }
    }

    /// <summary>
    /// TEST-ONLY: forcibly reduce the logical word count to <paramref name="wordCount"/>.
    /// Used by regression tests that need to simulate a snapshot length smaller than a subsequent destination chunk id, to exercise the <c>ExecuteMigrations</c>
    /// dirtyBits growth path. Not thread-safe; all bits beyond the truncation point are discarded.
    /// </summary>
    internal void ShrinkForTesting(int wordCount)
    {
        if (wordCount < 1)
        {
            wordCount = 1;
        }

        lock (_growLock)
        {
            var blockCount = (wordCount + WordInBlockMask) >> WordsPerBlockShift;
            var blocks = _blocks;
            var shrunk = new long[blockCount][];
            for (var i = 0; i < blockCount; i++)
            {
                shrunk[i] = i < blocks.Length ? blocks[i] : new long[WordsPerBlock];
            }

            // Discard whatever sat above the new logical count, including the tail of the last block, so a later Snapshot cannot report it.
            for (var w = wordCount; w < blockCount << WordsPerBlockShift; w++)
            {
                Volatile.Write(ref shrunk[w >> WordsPerBlockShift][w & WordInBlockMask], 0);
            }

            Volatile.Write(ref _blocks, shrunk);
            _wordCount = wordCount;
        }
    }

    /// <summary>
    /// Drain the bitmap: returns the dirty words accumulated since the last call, and clears them.
    /// Called by tick fence serialization (3.4) — outside the hot write path.
    /// </summary>
    /// <remarks>
    /// Drains in place rather than swapping the storage out. A <see cref="Set"/> racing this either lands before the word's exchange, and is reported here,
    /// or after it, and stays set for the next drain — see the class remarks for why swapping cannot offer that guarantee.
    /// </remarks>
    internal long[] Snapshot()
    {
        lock (_growLock)   // serialises against growth so the drain sees one stable block table; uncontended and once per fence
        {
            var blocks = _blocks;
            var wordCount = _wordCount;
            var result = new long[wordCount];
            for (var w = 0; w < wordCount; w++)
            {
                result[w] = Interlocked.Exchange(ref blocks[w >> WordsPerBlockShift][w & WordInBlockMask], 0);
            }
            return result;
        }
    }

    /// <summary>Returns true if any bit is set (fast skip for tick fence).</summary>
    internal bool HasDirty
    {
        get
        {
            var blocks = Volatile.Read(ref _blocks);
            for (var b = 0; b < blocks.Length; b++)
            {
                var block = blocks[b];
                for (var i = 0; i < block.Length; i++)
                {
                    if (Volatile.Read(ref block[i]) != 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Resolve the block holding <paramref name="blockIndex"/>, extending the table if the bitmap has not reached that far yet. The returned block is the
    /// live one and stays live: growth replaces only the table, so a writer racing it ORs into the same object either way.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long[] ResolveBlock(int blockIndex)
    {
        var blocks = Volatile.Read(ref _blocks);   // acquire: pairs with the release publish in Grow
        return blockIndex < blocks.Length ? blocks[blockIndex] : Grow(blockIndex);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private long[] Grow(int requiredBlockIndex)
    {
        lock (_growLock)
        {
            var blocks = _blocks;
            if (requiredBlockIndex < blocks.Length)
            {
                return blocks[requiredBlockIndex];   // another writer already extended past us
            }

            var newCount = Math.Max(blocks.Length * 2, requiredBlockIndex + 1);
            var grown = new long[newCount][];
            Array.Copy(blocks, grown, blocks.Length);   // copies REFERENCES: every existing block keeps its identity, so no in-flight OR can be stranded
            for (var i = blocks.Length; i < newCount; i++)
            {
                grown[i] = new long[WordsPerBlock];
            }

            Volatile.Write(ref _blocks, grown);   // release: the table and its fresh blocks must be visible to any lock-free reader that sees this reference
            _wordCount = newCount << WordsPerBlockShift;
            return grown[requiredBlockIndex];
        }
    }
}
