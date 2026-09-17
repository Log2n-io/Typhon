using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The blocks whose slots this tick's interest hits marked watched, for one archetype — the list S1 walks instead of the archetype.
/// </summary>
/// <remarks>
/// <para>
/// <b>This list is rule SUB-13 made into a data structure.</b> S1 is partitioned over what is in it, so per-tick replication work is bounded by the entities at
/// least one session has in view rather than by how many entities the archetype holds. An archetype of ten million entities with a thousand watched costs a
/// thousand: the other 9 999 000 are never addressed, because no block naming them is ever appended here.
/// </para>
/// <para>
/// <b>Appending is the marking, and the two are one act.</b> <see cref="Mark"/> sets the slot's bit in the block's watched mask and, for the first caller to
/// reach a given block this tick, appends the block. The claim is an <see cref="Interlocked.Exchange(ref uint, uint)"/> on
/// <see cref="ReplicationBlockHeader.LastWatchedTick"/> — the field the block already carries for idle eviction — so a block is listed exactly once however
/// many sessions, on however many workers, hit it. A separate "is it listed" flag would be a second piece of state saying the same thing, and the two would
/// disagree the first time one of them was updated and the other was not.
/// </para>
/// <para>
/// <b>The mask is set here and cleared by the producer's prologue, never by the pass.</b> A block's watched mask has to survive the whole track — the frame
/// stage reads it after the projection stage has run — so it is dropped at the start of the <i>next</i> tick, single-threaded, by whoever marked it:
/// <see cref="ClearMasks"/> here, and the interest pass's own prologue for the blocks it claimed. A reset inside the pass would be both a lost-update race
/// against a concurrent mark and an erasure of what the next stage is about to read.
/// </para>
/// <para>
/// <b>Capacity cannot bind, by construction.</b> Only a block the directory names can be marked, so at most <c>Directory.Count</c> distinct blocks are ever
/// appended in one tick. <see cref="BeginTick"/> takes that count and sizes the array to it, single-threaded, before any worker runs — which is also what
/// keeps the growth off the parallel path. The overflow counter below is therefore a bug detector, not a policy: a non-zero value means a block outside the
/// directory was marked.
/// </para>
/// <para>
/// <b>Thread safety.</b> <see cref="Mark"/> is safe from any number of workers at once. <see cref="BeginTick"/>, <see cref="Dispose"/> and the indexer are
/// not: the first runs at the track's single-threaded blocks step, and the indexer is read only after the dispatch that publishes the appends.
/// </para>
/// </remarks>
internal sealed unsafe class WatchedBlockList : IDisposable
{
    private nint* _blocks;
    private int _capacity;
    private int _count;
    private int _overflow;
    private uint _tick;
    private bool _disposed;

    /// <summary>The tick this list was last reset for. A block whose <c>LastWatchedTick</c> equals it is already listed.</summary>
    public uint Tick => _tick;

    /// <summary>Blocks marked this tick, clamped to the capacity so an overflowing reservation can never be indexed.</summary>
    public int Count
    {
        get
        {
            var count = Volatile.Read(ref _count);
            return count > _capacity ? _capacity : count;
        }
    }

    /// <summary>Slots the array can hold without growing. Sized by the watched set, never by the archetype (SUB-13).</summary>
    public int Capacity => _capacity;

    /// <summary>Marks that found no room. Always zero unless a block outside the directory was marked; see the class remarks.</summary>
    public int Overflow => Volatile.Read(ref _overflow);

    /// <summary>Bytes the list holds, for the owner's resource accounting.</summary>
    public long EstimatedBytes => (long)_capacity * sizeof(nint);

    /// <summary>
    /// Opens the list for <paramref name="tick"/> and guarantees room for <paramref name="maxBlocks"/> appends. Single-threaded: the track's blocks step.
    /// </summary>
    /// <param name="tick">The tick being projected. Zero is not a legal tick here — see the remarks.</param>
    /// <param name="maxBlocks">The most blocks that can be marked, which is the directory's entry count.</param>
    /// <remarks>
    /// The tick doubles as the claim stamp, so it must differ from the value a freshly rented block's header carries. The pool zeroes
    /// <see cref="ReplicationBlockHeader.LastWatchedTick"/> on rent, so tick 0 would make every new block read as already listed and S1 would skip it. The
    /// runtime's first tick is 1; this states the dependency rather than relying on it.
    /// </remarks>
    public void BeginTick(uint tick, int maxBlocks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (tick == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick,
                "Tick 0 cannot be a claim stamp: a freshly rented block already reads as stamped with it");
        }

        EnsureCapacity(maxBlocks);
        _tick = tick;
        Volatile.Write(ref _count, 0);
        Volatile.Write(ref _overflow, 0);
    }

    /// <summary>
    /// Marks slot <paramref name="slot"/> of <paramref name="block"/> watched this tick, listing the block if this is the first mark it has had.
    /// </summary>
    /// <param name="block">The block describing the hit's cluster.</param>
    /// <param name="slot">The hit's slot within that cluster, 0 to 63.</param>
    /// <returns><see langword="true"/> when this call listed the block, i.e. it was the first mark of the tick.</returns>
    public bool Mark(ReplicationBlockHeader* block, int slot)
    {
        var tick = _tick;

        // Set-only, never cleared here. The mask is reset by the PROLOGUE of the next tick, single-threaded, from this list — not by whichever Mark got there
        // first. A first-caller reset looks obvious and is a lost-update race: a second worker that claimed after the reset's Exchange but ORed before its
        // store would have its bit erased, and the entity would silently not be projected for a session that was looking straight at it.
        Interlocked.Or(ref block->WatchedMask, 1UL << slot);

        if (Interlocked.Exchange(ref block->LastWatchedTick, tick) == tick)
        {
            return false;
        }

        var index = Interlocked.Increment(ref _count) - 1;
        if (index >= _capacity)
        {
            Interlocked.Increment(ref _overflow);
            return false;
        }

        _blocks[index] = (nint)block;
        return true;
    }

    /// <summary>
    /// Appends <paramref name="block"/> without claiming it — for a caller that has already established the block is listed once, such as the blocks step
    /// gathering the interest stage's per-worker lists. Single-threaded.
    /// </summary>
    /// <param name="block">The block.</param>
    /// <returns><see langword="false"/> when the list had no room, which is counted in <see cref="Overflow"/>.</returns>
    public bool Add(ReplicationBlockHeader* block)
    {
        if (block == null)
        {
            return false;
        }

        var index = _count;
        if (index >= _capacity)
        {
            _overflow++;
            return false;
        }

        _blocks[index] = (nint)block;
        _count = index + 1;
        return true;
    }

    /// <summary>The block at <paramref name="index"/>. Read only after the dispatch that published the appends.</summary>
    public ReplicationBlockHeader* this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index is outside the blocks marked this tick");
            }

            return (ReplicationBlockHeader*)_blocks[index];
        }
    }

    /// <summary>Drops every listing without touching the blocks. Used when a tick produces no projection at all.</summary>
    public void Clear() => Volatile.Write(ref _count, 0);

    /// <summary>
    /// Clears the watched mask of every block still listed — the prologue half of the contract in the class remarks. Single-threaded, and run <b>before</b>
    /// the tick's marks, never after them.
    /// </summary>
    public void ClearMasks()
    {
        var count = Count;
        for (var i = 0; i < count; i++)
        {
            ((ReplicationBlockHeader*)_blocks[i])->WatchedMask = 0;
        }
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _capacity)
        {
            return;
        }

        var capacity = _capacity == 0 ? 64 : _capacity;
        while (capacity < required)
        {
            capacity *= 2;
        }

        // Realloc rather than alloc-copy-free: the old contents are dead here (the count is about to be reset), but Realloc is the one call that cannot leave
        // both blocks live if it throws.
        _blocks = (nint*)NativeMemory.Realloc(_blocks, (nuint)capacity * (nuint)sizeof(nint));
        _capacity = capacity;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_blocks != null)
        {
            NativeMemory.Free(_blocks);
            _blocks = null;
        }

        _capacity = 0;
        _count = 0;
    }
}
