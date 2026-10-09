using JetBrains.Annotations;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine;

/// <summary>
/// One record per chunk of a parallel dispatch: what chunk <c>i</c> needs to know about its work, written by the system's prepare step and read by
/// the chunk that claims index <c>i</c>. Context, not the data to process — an owner id, a range, a set of cursors.
/// </summary>
/// <remarks>
/// <para><b>Who writes, who reads.</b> Prepare runs on one thread before any chunk can be claimed: it calls <see cref="Reset"/> with the dispatch's chunk
/// count and fills every record. Chunks then reach their own record through the indexer, concurrently. Nothing writes a table while its chunks run:
/// the next <see cref="Reset"/> comes from the next dispatch's prepare, which starts only once every chunk of this one has completed (rule CD-03).</para>
/// <para><b>Ordering.</b> No fence is added here, and none is needed: prepare's writes precede the dispatch's publication of its claim word (a release
/// store) and a chunk is reached only through an <c>Interlocked</c> claim on that word, so every record is visible to every chunk on x64 and arm64.</para>
/// <para><b>By <c>ref</c>, never <c>in</c>.</b> A record is handed out as a plain <c>ref</c>: <c>in</c> / <c>ref readonly</c> on a struct that is
/// not declared <c>readonly</c> makes the compiler copy it before every member call, and no generic constraint can demand a <c>readonly</c> struct.
/// Writing through the <c>ref</c> is harmless — record <c>i</c> is read by chunk <c>i</c> alone.</para>
/// <para><b>Cost.</b> A read is one unsigned compare and one indexed load. The backing array grows to the largest count seen and is never
/// shrunk, so a steady workload allocates nothing. The record's size is the author's responsibility; a record that spans a cache line or less is the
/// one that stays cheap, since every chunk reads its own.</para>
/// </remarks>
/// <typeparam name="T">The record. Unmanaged, so the table is a flat array of values.</typeparam>
[PublicAPI]
public sealed class ChunkTable<T> where T : unmanaged
{
    private T[] _records = [];

    /// <summary>The number of records the current dispatch prepared — the chunk count it was sized for.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Sizes the table for a dispatch of <paramref name="count"/> chunks and returns its records, zeroed, for prepare to fill. A record left over from a
    /// larger earlier dispatch is never visible: <see cref="Count"/> bounds every read.
    /// </summary>
    public Span<T> Reset(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_records.Length < count)
        {
            _records = new T[Math.Max(count, _records.Length * 2)];
        }

        Count = count;
        var records = _records.AsSpan(0, count);
        records.Clear();
        return records;
    }

    /// <summary>The record of <paramref name="chunk"/>: prepare fills it, the chunk that claimed <paramref name="chunk"/> reads it.</summary>
    public ref T this[int chunk]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)chunk >= (uint)Count)
            {
                ThrowOutOfRange(chunk, Count);
            }

            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_records), chunk);
        }
    }

    [DoesNotReturn]
    private static void ThrowOutOfRange(int chunk, int count) =>
        throw new InvalidOperationException(
            $"Chunk {chunk} has no record: this dispatch's prepare sized its ChunkTable for {count} chunk(s). A prepare must Reset the table to the chunk count " +
            "it dispatches — including when it returns -1 to keep the static ChunkedParallel count.");
}
