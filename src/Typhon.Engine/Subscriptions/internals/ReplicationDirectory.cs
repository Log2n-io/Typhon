using System;
using System.Diagnostics;

namespace Typhon.Engine.Internals;

/// <summary>
/// Maps a cluster chunk id to the replication state block describing that cluster. One per replicated archetype.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sized by the watched set, not by the chunk-id space.</b> This is rule SUB-13 in a single data-structure choice. A flat array indexed by chunk id — the
/// shape the engine's own <c>ClusterAabbs</c> / <c>ClusterCellMap</c> / <c>ClusterSpatialIndexSlot</c> side tables use — would cost 8 B for every cluster the
/// <i>database</i> holds, because a chunk id is a persisted file address rather than a residency measure: roughly 381 MB at a billion entities to track
/// perhaps 170 k watched clusters. That is memory following the database, which is exactly what SUB-13 forbids, and no cluster-count threshold fixes it —
/// a threshold only moves where it breaks.
/// </para>
/// <para>
/// <b>The probe is not paid per hit.</b> Hits arrive grouped by cluster and a cluster holds up to 64 entities, so the directory is consulted once per
/// <i>run</i> of hits, not once per hit. A ~10-20 ns probe amortised over a run of 10-60 hits is well under a nanosecond per hit.
/// </para>
/// <para>
/// <b>Storage deviates from the design doc, deliberately, and the reason is narrower than it first looks.</b> § 4 specifies "native memory, like the blocks
/// it points to". This uses <see cref="HashMap{TKey,TValue}"/>, whose entry array is an ordinary managed <c>byte[]</c> reached through <c>ref</c>s. The
/// project rule that a raw pointer never addresses GC memory does not by itself decide this — a natively allocated map would satisfy that rule too, and the
/// pool beside it does exactly that. What decides it is reuse: this primitive already provides open addressing with linear probing, backward-shift deletion
/// (so a directory churning with the watched set accumulates no tombstones), a 0.75 load factor with doubling growth, and the hardened 4-byte key hash.
/// Writing a second open-addressed map natively would duplicate all of it to satisfy a phrase rather than a requirement. Nothing here stores a pointer into
/// managed memory: the values are block pointers into native pool slabs, and an <c>nint</c> held inside a managed array is not a pointer into that array.
/// </para>
/// <para>
/// <b>Consequence worth stating:</b> the entry stride is 16 B, so past a capacity of 8 192 — roughly 6 100 watched clusters at a 0.75 load factor — each
/// backing array is a Large Object Heap allocation. Growth happens at the prologue and at the blocks step, not on the hit path, but a directory that reaches
/// six figures of watched clusters is allocating and copying multi-megabyte LOH arrays as it grows. If that becomes the binding cost, the answer is a native
/// map, and the reuse argument above is what would have to be given up to get one.
/// </para>
/// <para>
/// <b>Thread safety: none, by contract.</b> Like the pool it indexes, the directory is mutated only at the replication track's prologue and its
/// single-threaded "blocks" step, each followed by a worker dispatch that publishes the writes. Parallel steps read it and take their pointers after the
/// change. There is deliberately no lock and no CAS.
/// </para>
/// <para>
/// <b>Lifetime: the directory does not own the blocks it names.</b> Its values are raw pointers into a <see cref="ReplicationBlockPool"/>'s slabs, and it
/// holds no reference to that pool, so it cannot keep those slabs alive and cannot notice when they go. <b>Dispose the directory before the pool.</b>
/// Disposing the pool first leaves every entry dangling, and a subsequent lookup would hand out a pointer into freed native memory. The per-archetype owner
/// that constructs both is what enforces the order; until it exists, the order is a contract and this paragraph is it.
/// </para>
/// </remarks>
internal sealed unsafe class ReplicationDirectory : IDisposable
{
    private readonly HashMap<int, nint> _map;
    private bool _disposed;

    /// <summary>
    /// Debug-only CONCURRENCY guard on the MUTATORS only — it rejects a second caller entering while one is inside, not a caller on a different thread.
    /// The distinction matters: the cluster-drain path legitimately runs on the driver thread one tick and a pool worker the next. The parallel steps read
    /// this directory by design (§ 7 has them taking their pointers after the single-threaded change), so guarding the readers would fire on correct code.
    /// </summary>
    private ReplicationThreadAffinity _affinity;

    /// <summary>Creates an empty directory.</summary>
    /// <param name="initialCapacity">
    /// Starting SLOT count, rounded up to a power of two — not a watched-cluster count. The backing map grows at a 0.75 load factor and does not divide by
    /// it, so a directory expected to hold <c>n</c> watched clusters wants roughly <c>n / 0.75</c> here if it is to avoid a rehash. Either way it is sized
    /// by the watched set, never by the archetype.
    /// </param>
    public ReplicationDirectory(int initialCapacity = 64)
    {
        _map = new HashMap<int, nint>(initialCapacity);
    }

    /// <summary>Number of clusters currently carrying a block — the watched-cluster count.</summary>
    public int Count
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _map.Count;
        }
    }

    /// <summary>Slots in the backing map. Grows with the watched set; unrelated to the archetype's chunk-id space.</summary>
    public int Capacity
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _map.Capacity;
        }
    }

    /// <summary>
    /// Approximate bytes held by the backing map, so the owner can report them to the resource graph.
    /// </summary>
    /// <remarks>
    /// The entry stride is 16 B — a 4-byte hash, a 4-byte key and an 8-byte value, rounded to 4 (<c>HashMapKV</c>'s <c>_entryStride</c>). This is the memory
    /// that used to be invisible: it grows with the watched set, it is an LOH allocation past a capacity of 8 192, and its capacity never shrinks. It does
    /// NOT include the dead doubling trail awaiting collection, which can briefly be as large again.
    /// </remarks>
    public long EstimatedBytes => _disposed ? 0L : ((long)_map.Capacity * 16L) + 64L;

    /// <summary>
    /// Associates <paramref name="chunkId"/> with <paramref name="block"/>, stamping the block's header with the id so the block is self-describing.
    /// </summary>
    /// <returns><see langword="false"/> when the id already has a block; the existing entry is left untouched.</returns>
    /// <exception cref="ArgumentException">
    /// The block is already registered under another chunk id. A block describes exactly one cluster, so registering it twice would leave the first entry
    /// naming a block that denies being its — and, once the drain hook returns both entries' blocks to the pool, would return the same block twice.
    /// </exception>
    /// <remarks>
    /// Stamping here rather than at rent is what makes <see cref="ReplicationBlockHeader.ChunkId"/> meaningful: the pool hands out a block with
    /// <see cref="ReplicationBlockPool.UnassignedChunkId"/>, and this is the moment it learns which cluster it describes. That stamp is also what makes the
    /// one-cluster-per-block check below O(1) — no reverse map is needed.
    /// </remarks>
    public bool TryAdd(int chunkId, ReplicationBlockHeader* block)
    {
        _affinity.Enter(nameof(ReplicationDirectory), nameof(TryAdd));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(block);

            if (chunkId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkId), chunkId,
                    "A cluster chunk id is never negative; negatives are the block's own sentinels.");
            }

            if (block->ChunkId >= 0)
            {
                throw new ArgumentException(
                    $"Block already describes cluster {block->ChunkId}; a block describes exactly one cluster.", nameof(block));
            }

            if (!_map.TryAdd(chunkId, (nint)block))
            {
                return false;
            }

            block->ChunkId = chunkId;
            return true;
        }
        finally
        {
            _affinity.Exit();
        }
    }

    /// <summary>Finds the block describing <paramref name="chunkId"/>.</summary>
    /// <returns><see langword="false"/> — with <paramref name="block"/> null — when no block is registered, which is the common case in a large archetype.</returns>
    public bool TryGetBlock(int chunkId, out ReplicationBlockHeader* block)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_map.TryGetValue(chunkId, out var address))
        {
            block = (ReplicationBlockHeader*)address;

            // The stamp is the directory's own cross-check, so it is worth actually reading. A disagreement means the block was re-registered behind the
            // directory's back or the entry outlived its cluster — both silent corruption rather than a miss, which is why this is an assert and not a
            // recoverable return.
            Debug.Assert(block->ChunkId == chunkId, $"Directory entry {chunkId} names a block stamped {block->ChunkId}");
            return true;
        }

        block = null;
        return false;
    }

    /// <summary>
    /// Drops the entry for <paramref name="chunkId"/> and hands back the block it held, so the caller can return it to the pool.
    /// </summary>
    /// <remarks>
    /// This is the call the cluster-drain hook makes <b>before</b> <c>FreeChunk</c> returns the id to the allocator. Chunk ids are recycled through a LIFO
    /// free list, so a block left at a freed id would be found by hits into the <i>new</i> cluster that inherits it — the engine already states this
    /// convention for its own per-cluster side tables, and the directory follows it rather than inventing a weaker one. The block's stamp is reset, which
    /// both makes it legal to register again and keeps a removed-but-not-yet-returned block from looking like a live entry.
    /// </remarks>
    public bool TryRemove(int chunkId, out ReplicationBlockHeader* block)
    {
        _affinity.Enter(nameof(ReplicationDirectory), nameof(TryRemove));
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_map.TryRemove(chunkId, out var address))
            {
                block = (ReplicationBlockHeader*)address;
                block->ChunkId = ReplicationBlockPool.UnassignedChunkId;
                return true;
            }

            block = null;
            return false;
        }
        finally
        {
            _affinity.Exit();
        }
    }

    // There is deliberately no Clear(). Dropping every entry without returning the blocks leaks all of them — unrecoverably, because the pool's free list is
    // intrusive and it has no sweep — so the only safe bulk operation is drain-and-return, which needs an enumeration seam this type does not yet expose.
    // That seam belongs with block eviction (design § 6), and it has a constraint worth writing down before anyone builds it: the backing map deletes by
    // backward shift, moving later entries BACKWARDS along their probe runs, so removing while enumerating silently skips entries. An eviction pass must
    // collect the doomed chunk ids first and remove them afterwards.

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _map.Dispose();
    }
}
