using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Captured old index key for a single entity-field pair, stored before the first SV in-place mutation per tick.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShadowEntry
{
    public int ChunkId;

    /// <summary>Full <see cref="EntityId"/> of the mutated entity — typed, not a bare key or chunk id (issue #660).</summary>
    public EntityId EntityPK;

    public KeyBytes8 OldKey;
}

/// <summary>
/// Per-indexed-field append buffer that captures old index keys before SV in-place mutations.
/// <para>
/// <b>Write path (concurrent):</b> <see cref="Append"/> is called from <c>EntityRef.Write&lt;T&gt;()</c> on the first mutation per entity per tick
/// (guarded by <see cref="DirtyBitmap.TestAndSet"/>).
/// Multiple threads may append concurrently for different entities.
/// </para>
/// <para>
/// <b>Tick boundary (single-threaded):</b> Consumer iterates <see cref="Count"/> entries via indexer, then calls <see cref="Reset"/>. No concurrent appends
/// during drain (tick boundary is a sync point).
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Storage is segmented, and that is load-bearing (#558).</b> Entries live in fixed-size blocks reached by index; a block is allocated on first use and is
/// never moved, resized or freed. That is what lets <see cref="Append"/> be wait-free: a writer reserves its index with a single
/// <see cref="Interlocked.Increment(ref int)"/> and writes into a slot no other writer can claim, with no lock and no chance of a concurrent grow relocating
/// the array underneath it.
/// </para>
/// <para>
/// The previous implementation was a single array guarded by a per-append lock. On a 20 001-entity archetype at 120 Hz that lock measured
/// <c>Total 17 309 ms / Own 107 ms</c> — a ten-instruction body spending 99 % of its wall time blocked, and the largest single cost in the engine, ~73x the
/// entire tick fence. Every parallel system write funnelled through one lock per indexed field.
/// </para>
/// <para>
/// A flat pre-sized array is NOT a valid alternative: the gate that admits appends (<see cref="DirtyBitmap.TestAndSet"/>) grows on demand, so after a segment
/// grows more entities can shadow than any capacity computed at construction allowed.
/// </para>
/// <para>
/// <b>Memory-ordering contract.</b> The reservation increments the count BEFORE the entry is written (the old lock-based version incremented it after). Reads
/// are therefore only valid at the tick boundary, once the scheduler's phase barrier has retired every writer — which is exactly the contract
/// <see cref="Count"/> and the indexer already document. Do not read this buffer concurrently with appends.
/// </para>
/// </remarks>
internal sealed class FieldShadowBuffer
{
    /// <summary>log2 of the entries per block. 4096 x 24 B = 96 KB per block — big enough that crossing a boundary is rare, small enough not to waste memory
    /// on archetypes that shadow a handful of entities per tick.</summary>
    private const int BlockShift = 12;
    private const int BlockSize = 1 << BlockShift;
    private const int BlockMask = BlockSize - 1;

    /// <summary>Block table. Replaced wholesale when it needs to grow; existing block references are copied across, so a writer holding an older table still
    /// resolves the same arrays for every index it can address.</summary>
    private ShadowEntry[][] _blocks;
    private int _count;
    private readonly Lock _growLock = new();

    internal FieldShadowBuffer(int initialCapacity = 256)
    {
        var blockCount = Math.Max(1, (initialCapacity + BlockSize - 1) / BlockSize);
        _blocks = new ShadowEntry[blockCount][];
        _blocks[0] = new ShadowEntry[BlockSize];
    }

    /// <summary>Append a shadow entry. Wait-free: one interlocked reservation, then a write into a slot owned exclusively by this call.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Append(int chunkId, EntityId entityPK, KeyBytes8 oldKey)
    {
        var index = Interlocked.Increment(ref _count) - 1;
        var blockIndex = index >> BlockShift;

        var blocks = _blocks;
        var block = blockIndex < blocks.Length ? blocks[blockIndex] : null;
        if (block == null)
        {
            block = EnsureBlock(blockIndex);
        }

        block[index & BlockMask] = new ShadowEntry { ChunkId = chunkId, EntityPK = entityPK, OldKey = oldKey };
    }

    /// <summary>Allocates the block for <paramref name="blockIndex"/>, growing the block table if needed. Off the hot path — reached only the first time a
    /// tick's appends cross into a new block.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ShadowEntry[] EnsureBlock(int blockIndex)
    {
        lock (_growLock)
        {
            var blocks = _blocks;
            if (blockIndex >= blocks.Length)
            {
                var newLength = blocks.Length;
                while (newLength <= blockIndex)
                {
                    newLength *= 2;
                }

                // Copy the block REFERENCES into a wider table. The blocks themselves are untouched, so a writer that already read the old table keeps a
                // valid reference for every index it could have reserved against it.
                var grown = new ShadowEntry[newLength][];
                blocks.CopyTo(grown, 0);
                _blocks = grown;
                blocks = grown;
            }

            return blocks[blockIndex] ??= new ShadowEntry[BlockSize];
        }
    }

    /// <summary>Number of entries. Read at tick boundary (no concurrent appends).</summary>
    internal int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    /// <summary>Access entry by index. Read at tick boundary (no concurrent appends).</summary>
    internal ref ShadowEntry this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _blocks[index >> BlockShift][index & BlockMask];
    }

    /// <summary>Reset count to zero for next tick. Not thread-safe — call only at tick boundary. Allocated blocks are retained, so a steady-state tick
    /// allocates nothing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Reset() => _count = 0;
}
