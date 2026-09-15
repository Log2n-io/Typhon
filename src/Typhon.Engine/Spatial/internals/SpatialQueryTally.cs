using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// What one archetype's range queries tested against what they matched (#906, rule SO-02): the clusters they opened, the entities those clusters held, and
/// the matches they returned. Candidates per hit is what a match costs in entity tests, and loose cluster bounds are what raise it.
/// </summary>
/// <remarks>
/// <para><b>Per thread, never shared.</b> A query adds its totals once, when it hands its page window back, to the slot of the thread it ran on: three plain
/// adds to a line no other thread writes. One shared set of counters would put three interlocked adds on one line from every worker, once per query.</para>
/// <para><b>A slot per managed thread id.</b> Ids are unique among live threads and handed out lowest first, so a slot has one writer at a time and a
/// process uses the first few chunks. Every id up to <see cref="MaxOwnedThreadId"/>, the engine's bound on live threads, gets its own slot; the chunk
/// holding it is made the first time one of its threads adds, and never moves, so no add is lost to a copy. A slot is never reset: a thread that dies
/// leaves its counts, and the next thread given its id carries on from them. The slots only grow, which is what lets the fence read them while queries
/// run and take a delta that is never negative. An id past the bound, which the engine does not support, shares one slot through interlocked adds.</para>
/// <para><b>192 bytes a slot:</b> the adjacent-line prefetcher fetches 128-byte pairs, and an array's data is only 8-byte aligned, so two threads' 24 bytes
/// need a 168-byte gap to stay out of each other's pair whatever the array's address.</para>
/// </remarks>
internal sealed class SpatialQueryTally
{
    /// <summary>Slots per chunk: 6 KB.</summary>
    internal const int ChunkSlots = 32;

    /// <summary>The highest thread id with a slot of its own: the engine's bound on live threads, which its locks store in 16 bits.</summary>
    internal const int MaxOwnedThreadId = 32_767;

    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct Slot
    {
        [FieldOffset(0)]
        public long ClustersOpened;

        [FieldOffset(8)]
        public long Candidates;

        [FieldOffset(16)]
        public long Hits;
    }

    // Chunk c holds the slots of ids [c * ChunkSlots, (c + 1) * ChunkSlots). 8 KB of references, against a flat array's 6 MB.
    private readonly Slot[][] _chunks = new Slot[(MaxOwnedThreadId / ChunkSlots) + 1][];
    private readonly Slot[] _shared = new Slot[1];

    // One past the highest chunk made, so the fence reads the chunks in use rather than all of them. Raised once per chunk.
    private int _chunksInUse;

    /// <summary>Add one query's totals to the slot of <paramref name="threadId"/>, the calling thread's managed id.</summary>
    internal void Add(int threadId, int clustersOpened, int candidates, int hits)
    {
        if ((uint)threadId <= MaxOwnedThreadId)
        {
            var index = threadId / ChunkSlots;
            var chunk = Volatile.Read(ref _chunks[index]) ?? CreateChunk(index);

            // The one live thread holding this id is the slot's only writer, so plain adds lose nothing. A fence reading meanwhile may see part of one
            // query's three adds; its next read sees the rest.
            ref var slot = ref chunk[threadId % ChunkSlots];
            slot.ClustersOpened += clustersOpened;
            slot.Candidates += candidates;
            slot.Hits += hits;
            return;
        }

        ref var shared = ref _shared[0];
        Interlocked.Add(ref shared.ClustersOpened, clustersOpened);
        Interlocked.Add(ref shared.Candidates, candidates);
        Interlocked.Add(ref shared.Hits, hits);
    }

    /// <summary>
    /// Every thread's totals since this tally was created. Reads the slots while queries run: a query adding meanwhile is counted now or at the next read,
    /// never twice, and never subtracted.
    /// </summary>
    internal void Read(out long clustersOpened, out long candidates, out long hits)
    {
        ref var shared = ref _shared[0];
        clustersOpened = Volatile.Read(ref shared.ClustersOpened);
        candidates = Volatile.Read(ref shared.Candidates);
        hits = Volatile.Read(ref shared.Hits);

        var inUse = Volatile.Read(ref _chunksInUse);
        for (var c = 0; c < inUse; c++)
        {
            var chunk = Volatile.Read(ref _chunks[c]);
            if (chunk == null)
            {
                continue;
            }

            for (var i = 0; i < ChunkSlots; i++)
            {
                ref var slot = ref chunk[i];
                clustersOpened += Volatile.Read(ref slot.ClustersOpened);
                candidates += Volatile.Read(ref slot.Candidates);
                hits += Volatile.Read(ref slot.Hits);
            }
        }
    }

    /// <summary>Make chunk <paramref name="index"/>, or take the one a thread sharing it made first.</summary>
    private Slot[] CreateChunk(int index)
    {
        var created = new Slot[ChunkSlots];
        var chunk = Interlocked.CompareExchange(ref _chunks[index], created, null) ?? created;

        var current = Volatile.Read(ref _chunksInUse);
        while (current <= index)
        {
            var seen = Interlocked.CompareExchange(ref _chunksInUse, index + 1, current);
            if (seen == current)
            {
                break;
            }

            current = seen;
        }

        return chunk;
    }
}
