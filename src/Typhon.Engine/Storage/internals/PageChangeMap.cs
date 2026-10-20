using System;
using System.Numerics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// One bit per data-file page: "written to the data file since the last backup's freeze" (BKP-01). Set by the write chokepoint before each write,
/// detached by online backup at its freeze (it becomes the backup's page set), merged back if that backup fails (BKP-07).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-level, grow-without-copy.</b> A fixed directory of <see cref="DirectorySize"/> slots covers every <c>int</c> page index; each slot holds a
/// chunk of <see cref="WordsPerChunk"/> words (64 KiB, 524 288 pages, 4 GiB of data file), allocated on first touch and published with a CAS. A
/// file that grows moves no word, so writers need no lock.
/// </para>
/// <para>
/// <b>Managed memory, reached only by <c>ref</c>.</b> Words are read with <c>Volatile.Read</c> and set with
/// <see cref="Interlocked.Or(ref ulong, ulong)"/>; no pointer is ever formed over a chunk. A set tests the bit first: the common case is a page
/// already written since the freeze, and a locked read-modify-write right after the store-heavy checksum pass costs far more than a load.
/// </para>
/// </remarks>
internal sealed class PageChangeMap
{
    private const int WordsPerChunkShift = 13;
    private const int PagesPerChunkShift = WordsPerChunkShift + 6;

    /// <summary>Words per chunk.</summary>
    internal const int WordsPerChunk = 1 << WordsPerChunkShift;

    /// <summary>Pages per chunk.</summary>
    internal const int PagesPerChunk = 1 << PagesPerChunkShift;

    /// <summary>Directory slots: enough chunks for every non-negative <c>int</c> page index.</summary>
    internal const int DirectorySize = 1 << (31 - PagesPerChunkShift);

    private readonly ulong[][] _chunks = new ulong[DirectorySize][];

    /// <summary>
    /// Allocates the chunks covering pages <c>[0, pageCount)</c> now, so the first writes into a fresh map allocate nothing: a backup swaps one in at
    /// every freeze, and its 64 KiB chunks would otherwise be allocated by the data-file writers.
    /// </summary>
    internal void Reserve(int pageCount)
    {
        for (var chunk = 0; chunk < DirectorySize && (long)chunk * PagesPerChunk < pageCount; chunk++)
        {
            Chunk(chunk);
        }
    }

    /// <summary>Marks pages <c>[firstPage, firstPage + count)</c> written.</summary>
    internal void Set(int firstPage, int count)
    {
        for (var p = firstPage; p < firstPage + count; p++)
        {
            ref var word = ref Word(p);
            var bit = 1UL << (p & 63);
            if ((Volatile.Read(ref word) & bit) == 0)
            {
                Interlocked.Or(ref word, bit);
            }
        }
    }

    /// <summary>
    /// Unmarks pages <c>[firstPage, firstPage + count)</c>, a whole word at a time (so every page sharing a word with the range too). For benchmarks
    /// and tests that need a page's first write again; the engine never unmarks a page (a backup detaches the whole map instead).
    /// </summary>
    internal void ClearWordsForTests(int firstPage, int count)
    {
        for (var p = firstPage & ~63; p < firstPage + count; p += 64)
        {
            Volatile.Write(ref Word(p), 0UL);
        }
    }

    /// <summary>Whether page <paramref name="page"/> is marked.</summary>
    internal bool IsSet(int page)
    {
        var chunk = Volatile.Read(ref _chunks[page >> PagesPerChunkShift]);
        return chunk != null && (Volatile.Read(ref chunk[(page >> 6) & (WordsPerChunk - 1)]) & (1UL << (page & 63))) != 0;
    }

    /// <summary>The first marked page in <c>[fromPage, endPage)</c>, or <c>-1</c>.</summary>
    internal int NextSetPage(int fromPage, int endPage)
    {
        // In 64 bits: stepping past the last chunk or word would overflow an int.
        long p = fromPage;
        while (p < endPage)
        {
            var chunk = Volatile.Read(ref _chunks[(int)(p >> PagesPerChunkShift)]);
            if (chunk == null)
            {
                p = ((p >> PagesPerChunkShift) + 1) << PagesPerChunkShift;
                continue;
            }

            var word = Volatile.Read(ref chunk[(int)(p >> 6) & (WordsPerChunk - 1)]) & (ulong.MaxValue << (int)(p & 63));
            if (word != 0)
            {
                var found = (p & ~63L) + BitOperations.TrailingZeroCount(word);
                return found < endPage ? (int)found : -1;
            }

            p = (p & ~63L) + 64;
        }

        return -1;
    }

    /// <summary>The number of marked pages in <c>[0, endPage)</c>.</summary>
    internal long PopCount(int endPage)
    {
        long total = 0;
        for (var c = 0; c < DirectorySize && (long)c << PagesPerChunkShift < endPage; c++)
        {
            var chunk = Volatile.Read(ref _chunks[c]);
            if (chunk == null)
            {
                continue;
            }

            for (var w = 0; w < WordsPerChunk; w++)
            {
                var firstPage = ((long)c << PagesPerChunkShift) + ((long)w << 6);
                if (firstPage >= endPage)
                {
                    break;
                }

                var word = Volatile.Read(ref chunk[w]);
                var valid = endPage - firstPage;
                total += BitOperations.PopCount(valid >= 64 ? word : word & ((1UL << (int)valid) - 1));
            }
        }

        return total;
    }

    /// <summary>
    /// ORs every mark of <paramref name="other"/> into this map. Lock-free and safe beside concurrent <see cref="Set"/> calls on this map; it is how a
    /// failed backup gives its page set back (BKP-07).
    /// </summary>
    internal void OrWith(PageChangeMap other)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (var c = 0; c < DirectorySize; c++)
        {
            var source = Volatile.Read(ref other._chunks[c]);
            if (source == null)
            {
                continue;
            }

            ulong[] target = null;
            for (var w = 0; w < WordsPerChunk; w++)
            {
                var bits = Volatile.Read(ref source[w]);
                if (bits == 0)
                {
                    continue;
                }

                target ??= Chunk(c);
                if ((Volatile.Read(ref target[w]) & bits) != bits)
                {
                    Interlocked.Or(ref target[w], bits);
                }
            }
        }
    }

    /// <summary>Marks every page of <c>[0, pageCount)</c>.</summary>
    internal void SetAll(int pageCount)
    {
        for (var p = 0; p < pageCount;)
        {
            if ((p & 63) == 0 && p + 64 <= pageCount)
            {
                ref var word = ref Word(p);
                if (Volatile.Read(ref word) != ulong.MaxValue)
                {
                    Interlocked.Or(ref word, ulong.MaxValue);
                }

                p += 64;
            }
            else
            {
                Set(p, 1);
                p++;
            }
        }
    }

    /// <summary>The map over <c>[0, pageCount)</c> as dense words, for the clean-close side file.</summary>
    internal ulong[] ToDense(int pageCount)
    {
        var dense = new ulong[(pageCount + 63) >> 6];
        for (var i = 0; i < dense.Length; i++)
        {
            var page = i << 6;
            var chunk = Volatile.Read(ref _chunks[page >> PagesPerChunkShift]);
            var word = chunk == null ? 0UL : Volatile.Read(ref chunk[i & (WordsPerChunk - 1)]);
            var valid = pageCount - page;
            dense[i] = valid >= 64 ? word : word & ((1UL << valid) - 1);
        }

        return dense;
    }

    /// <summary>ORs dense words written by <see cref="ToDense"/> into this map.</summary>
    internal void OrDense(ReadOnlySpan<ulong> dense)
    {
        for (var i = 0; i < dense.Length; i++)
        {
            if (dense[i] == 0)
            {
                continue;
            }

            ref var word = ref Word(i << 6);
            Interlocked.Or(ref word, dense[i]);
        }
    }

    private ref ulong Word(int page) => ref Chunk(page >> PagesPerChunkShift)[(page >> 6) & (WordsPerChunk - 1)];

    private ulong[] Chunk(int chunkIndex)
    {
        ref var slot = ref _chunks[chunkIndex];
        var chunk = Volatile.Read(ref slot);
        if (chunk != null)
        {
            return chunk;
        }

        var fresh = new ulong[WordsPerChunk];
        return Interlocked.CompareExchange(ref slot, fresh, null) ?? fresh;
    }
}
