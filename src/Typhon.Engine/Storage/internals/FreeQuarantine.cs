using System;
using System.Numerics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The chunks a segment freed that no checkpoint has made safe to reuse yet (#1251, CK-17). A free is not logged, and the data file holds each page as of
/// its last capture: until a checkpoint has written the pages that stopped naming a chunk, the disk can still show the chunk named — by a chain's root as
/// of before its compaction, by an element as of before its cleanup. Reused meanwhile, the chunk took another entity's data, and a crash recovered the
/// first chain over it: an entity read another's value, or lost its chain. A freed chunk stays allocated here until a checkpoint cycle that started after
/// the free has written every page it collected; only then is it freed.
/// </summary>
/// <remarks>
/// <para>
/// Two generations of held chunks, each a bitmap of blocks of <see cref="BlockBits"/> chunks allocated when a chunk in the range is first held and never
/// moved, so a free is one interlocked OR and <see cref="IsHeld"/> two reads, without a lock. A free goes to the current generation. The sealed one moves
/// through one state at a time, each step a compare-exchange: <c>Empty</c> → <see cref="Seal"/>, at a cycle's start, makes the other — empty — generation
/// current → <c>Sealed</c> → <see cref="MarkReleasable"/>, once that cycle has written every page it collected → <c>Releasable</c> →
/// <see cref="ReleaseSealed"/>, by the release worker → <c>Releasing</c> → <c>Empty</c>. A cycle that starts while the state is not <c>Empty</c> seals
/// nothing, and the frees wait for a later cycle: later, never early.
/// </para>
/// <para>
/// A free that reads the current generation just before a seal and sets its bit just after lands in the sealed one. Its unlinking write and the dirty mark
/// of its page came first, and that mark is an interlocked operation, so the read of the generation is ordered after it; the seal's exchange is a full
/// fence before the cycle collects. So the cycle that seals the free collects the page that stopped naming it, on x64 and arm64 alike.
/// </para>
/// </remarks>
internal sealed class FreeQuarantine
{
    private const int BlockShift = 16;
    private const int BlockBits = 1 << BlockShift;
    private const int WordsPerBlock = BlockBits / 64;

    // The frees released under one epoch scope: each touches its chunk's page, and one scope over millions would hold every page of the segment.
    private const int WordsPerEpochScope = 64;

    private const int Empty = 0;
    private const int Sealed = 1;
    private const int Releasable = 2;
    private const int Releasing = 3;

    private readonly object _grow = new();
    private readonly ulong[][][] _generations = [[], []];
    private int _current;
    private int _state = Empty;

    /// <summary>
    /// Holds <paramref name="chunkId"/>, freed now, until a checkpoint makes its reuse safe. False when it is held already: a second free, never held twice
    /// — released twice, it would be freed under its next owner.
    /// </summary>
    internal bool Add(int chunkId)
    {
        var current = Volatile.Read(ref _current);
        var word = (chunkId & (BlockBits - 1)) >> 6;
        var mask = 1UL << (chunkId & 63);
        ref var slot = ref Block(current, chunkId)[word];
        if ((Interlocked.Or(ref slot, mask) & mask) != 0)
        {
            return false;
        }

        // Set, then the other generation checked: two frees of one chunk racing a seal can each set a different generation, and the later to look backs
        // out. Both may back out — a leak, never a chunk freed twice.
        if (IsSet(current ^ 1, chunkId))
        {
            Interlocked.And(ref slot, ~mask);
            return false;
        }

        return true;
    }

    /// <summary>Whether <paramref name="chunkId"/> is held: freed by its owner, not yet reusable.</summary>
    internal bool IsHeld(int chunkId) => IsSet(0, chunkId) || IsSet(1, chunkId);

    /// <summary>A checkpoint cycle starts, before it collects its dirty pages: the frees made so far wait for it — unless frees sealed earlier still wait.</summary>
    internal void Seal()
    {
        if (Interlocked.CompareExchange(ref _state, Sealed, Empty) == Empty)
        {
            Interlocked.Exchange(ref _current, _current ^ 1);
        }
    }

    /// <summary>
    /// A cycle that sealed wrote every page it collected: the sealed frees may be freed. True when there are some to free — also when a release that
    /// threw left them releasable: no seal advances that state, and without the worker's signal they waited for another segment's (review).
    /// </summary>
    internal bool MarkReleasable() => Interlocked.CompareExchange(ref _state, Releasable, Sealed) is Sealed or Releasable;

    /// <summary>
    /// Frees, through <paramref name="free"/>, the sealed chunks a covered cycle made releasable — in chunk order, a page's frees together. Returns how many.
    /// </summary>
    internal int ReleaseSealed(Action<int> free, EpochManager epochManager)
    {
        if (Interlocked.CompareExchange(ref _state, Releasing, Releasable) != Releasable)
        {
            return 0;
        }

        try
        {
            return Free(Volatile.Read(ref _current) ^ 1, free, epochManager);
        }
        finally
        {
            // A free that threw leaves the rest held, and the state where the next covered cycle finds it.
            Volatile.Write(ref _state, IsEmpty(Volatile.Read(ref _current) ^ 1) ? Empty : Releasable);
        }
    }

    /// <summary>Frees every held chunk, sealed or not — the close, or a test that counts chunks after a free; nothing frees concurrently. Returns how many.</summary>
    internal int ReleaseAll(Action<int> free, EpochManager epochManager)
    {
        var freed = Free(0, free, epochManager) + Free(1, free, epochManager);
        Volatile.Write(ref _state, Empty);
        return freed;
    }

    /// <summary>The chunks held. Diagnostic: counts every bit.</summary>
    internal int Count
    {
        get
        {
            var count = 0;
            for (var g = 0; g < _generations.Length; g++)
            {
                foreach (var block in Volatile.Read(ref _generations[g]))
                {
                    if (block == null)
                    {
                        continue;
                    }

                    foreach (var word in block)
                    {
                        count += BitOperations.PopCount(word);
                    }
                }
            }

            return count;
        }
    }

    // Freed in the bitmap, then no longer held: a free of the chunk by a next owner in between is refused as a second one, and the chunk leaks — the other
    // order would let a check that finds it allocated and not held take a released chain for live.
    private int Free(int generation, Action<int> free, EpochManager epochManager)
    {
        var blocks = Volatile.Read(ref _generations[generation]);
        var freed = 0;
        for (var b = 0; b < blocks.Length; b++)
        {
            var block = Volatile.Read(ref blocks[b]);
            if (block == null)
            {
                continue;
            }

            for (var from = 0; from < WordsPerBlock; from += WordsPerEpochScope)
            {
                if (IsZero(block, from))
                {
                    continue;
                }

                using var guard = EpochGuard.Enter(epochManager);
                for (var w = from; w < from + WordsPerEpochScope; w++)
                {
                    var word = Volatile.Read(ref block[w]);
                    while (word != 0)
                    {
                        var bit = BitOperations.TrailingZeroCount(word);
                        word &= word - 1;
                        free((b << BlockShift) | (w << 6) | bit);
                        Interlocked.And(ref block[w], ~(1UL << bit));
                        freed++;
                    }
                }
            }
        }

        return freed;
    }

    private static bool IsZero(ulong[] block, int from)
    {
        for (var w = from; w < from + WordsPerEpochScope; w++)
        {
            if (Volatile.Read(ref block[w]) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsEmpty(int generation)
    {
        foreach (var block in Volatile.Read(ref _generations[generation]))
        {
            if (block != null && !IsZero(block))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsZero(ulong[] block)
    {
        for (var from = 0; from < WordsPerBlock; from += WordsPerEpochScope)
        {
            if (!IsZero(block, from))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsSet(int generation, int chunkId)
    {
        var blocks = Volatile.Read(ref _generations[generation]);
        var b = chunkId >> BlockShift;
        if ((uint)b >= (uint)blocks.Length)
        {
            return false;
        }

        var block = Volatile.Read(ref blocks[b]);
        return block != null && (Volatile.Read(ref block[(chunkId & (BlockBits - 1)) >> 6]) & (1UL << (chunkId & 63))) != 0;
    }

    private ulong[] Block(int generation, int chunkId)
    {
        var b = chunkId >> BlockShift;
        var blocks = Volatile.Read(ref _generations[generation]);
        if ((uint)b < (uint)blocks.Length && Volatile.Read(ref blocks[b]) is { } existing)
        {
            return existing;
        }

        lock (_grow)
        {
            blocks = _generations[generation];
            if (b >= blocks.Length)
            {
                var grown = new ulong[b + 1][];
                Array.Copy(blocks, grown, blocks.Length);
                blocks = grown;
            }

            var block = blocks[b];
            if (block == null)
            {
                block = new ulong[WordsPerBlock];
                Volatile.Write(ref blocks[b], block);
            }

            Volatile.Write(ref _generations[generation], blocks);
            return block;
        }
    }
}
