using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Transaction-scoped staging store for the component payloads of spawned-but-unpublished entities.
/// </summary>
/// <remarks>
/// <para>
/// A spawned entity has no address until <c>FinalizeSpawns</c> claims its cluster slot at commit, so its payload has to live somewhere in the meantime.
/// That used to be a real <c>ComponentSegment</c> chunk, which for a cluster-backed SingleVersion or Transient component then became structurally unreachable —
/// the persisted <c>ClusterEntityRecord</c> has no field that could hold the id, so nothing could ever free it and the file grew with cumulative spawns (#839).
/// This arena is that address space instead: no page cache, no occupancy bitmap, nothing to leak, and it costs the transaction one native block for as long as
/// it lives.
/// </para>
/// <para>
/// <b>Blocks are never moved, resized or reallocated.</b> That is the whole design constraint, and it is why this is not simply <c>NativeMemory.Realloc</c>
/// over one buffer the way <c>_commitStagingBuffer</c> is. A write returns a <c>ref T</c> into a staging slot, and spawn-spawn-write is ordinary usage — it is
/// exactly what <c>SpawnBatch</c> does:
/// <code>
/// var a = tx.Spawn&lt;T&gt;(...);
/// ref var c = ref tx.OpenMut(a).Write(T.Comp);
/// var b = tx.Spawn&lt;T&gt;(...);   // must NOT invalidate c
/// c.X = 42;
/// </code>
/// A growing realloc would hand that caller freed memory. Appending a fresh block leaves every outstanding pointer valid, so growth is invisible to callers.
/// <c>_commitStagingBuffer</c> can accept the invalidation because its documented idiom is write-then-commit with nothing in between; this one cannot.
/// </para>
/// <para>
/// Handles are opaque <c>int</c>s so they fit <c>SpawnEntry</c>'s fixed buffers, and <b>0 is never a valid handle</b> — the first slot of the first block is
/// reserved, mirroring the reserved chunk 0 convention the callers already test for. A slot comes back ZEROED, which matters: the old chunk path allocated with
/// <c>clearContent: false</c> and so carried recycled bytes into the cluster for a component the caller did not supply — survivable only because that
/// slot's enabled bit stays clear. Fresh native memory is uninitialised, so this is required rather than a courtesy, and it makes an unsupplied component
/// deterministic. It costs nothing: blocks are <c>AllocZeroed</c> and a bump allocator hands out each slot exactly once, so no explicit clear is needed on the
/// allocation path.
/// </para>
/// </remarks>
internal sealed unsafe class SpawnStagingArena : IDisposable
{
    /// <summary>
    /// Bytes per block — ~340 24-byte payloads, which covers an ordinary tick's spawns in one allocation.
    /// </summary>
    /// <remarks>
    /// Deliberately small. <see cref="Reset"/> frees every block rather than retaining one, so this size is paid by each spawning transaction; at 8 KiB that
    /// is a single small allocation on a commit path measured in microseconds, where a 64 KiB zeroed block would mean first-touch faults across sixteen pages.
    /// Retaining a block instead would avoid the allocation but pin memory in every pooled transaction that ever spawned, and would put back the per-allocation
    /// clear that block reuse requires. Growing by appending more small blocks costs one allocation per 8 KiB of staged payload, which only a very large batch
    /// reaches.
    /// </remarks>
    private const int BlockSize = 8 * 1024;

    /// <summary>Bits of a handle given to the in-block offset; the rest select the block. Exactly addresses <see cref="BlockSize"/>.</summary>
    private const int OffsetBits = 13;
    private const int OffsetMask = (1 << OffsetBits) - 1;

    /// <summary>Blocks addressable before the handle's sign bit would be set — see <see cref="AppendBlock"/>.</summary>
    private const int MaxBlocks = 1 << (31 - OffsetBits);

    /// <summary>Every payload starts 8-byte aligned, so a <c>ref T</c> into a slot is naturally aligned for any blittable component.</summary>
    private const int Alignment = 8;

    private readonly List<IntPtr> _blocks = [];

    private int _currentBlock = -1;
    private int _currentUsed;

    /// <summary>
    /// Reserves <paramref name="size"/> zeroed bytes and returns a handle that stays valid until <see cref="Reset"/> or <see cref="Dispose"/>. Never returns 0.
    /// The bytes are zero because every block is <c>AllocZeroed</c> and a slot is handed out exactly once in that block's lifetime.
    /// </summary>
    internal int Alloc(int size)
    {
        var need = (size + (Alignment - 1)) & ~(Alignment - 1);

        // An oversized payload gets a block to itself rather than forcing BlockSize up for everyone. Its offset is 0, so its handle is non-zero only if its
        // block index is — which is NOT automatic: in a fresh arena, or after a Reset that dropped an oversized block 0, this would be block 0 and encode to
        // handle 0, i.e. "no payload". Make sure an ordinary block exists first, which also keeps the reserved slot-0 invariant in one place.
        if (need > BlockSize)
        {
            if (_blocks.Count == 0)
            {
                AppendBlock();
            }

            EnsureBlockIndexFits();
            var big = (byte*)NativeMemory.Alloc((nuint)need);
            NativeMemory.Clear(big, (nuint)need);
            _blocks.Add((IntPtr)big);
            return Encode(_blocks.Count - 1, 0);
        }

        // Re-test after appending rather than assuming the fresh block fits: block 0 reserves its first slot, so a request of exactly BlockSize would otherwise
        // overrun it by the reservation.
        if (_currentBlock < 0 || _currentUsed + need > BlockSize)
        {
            AppendBlock();
            if (_currentUsed + need > BlockSize)
            {
                AppendBlock();
            }
        }

        var offset = _currentUsed;
        _currentUsed += need;

        // Zero HERE, not on rewind. The caller allocates in order to write, so this slot's lines are about to be pulled into L1 anyway and the clear rides on
        // the write-allocate that was going to happen regardless. Clearing the used extent in Reset() instead would be one large memset over bytes the next
        // transaction may never touch, evicting live data to do it — and it would put that cost on the reset path, which is on every transaction whether it
        // spawned or not. Zeroing per slot also keeps the guarantee exact: a slot is clean because it was cleaned, not because its block happened to be fresh.
        var slot = (byte*)_blocks[_currentBlock] + offset;
        NativeMemory.Clear(slot, (nuint)need);
        return Encode(_currentBlock, offset);
    }

    /// <summary>Resolves a handle to its payload. The pointer is stable for the lifetime of the transaction.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte* Resolve(int handle) => (byte*)_blocks[handle >> OffsetBits] + (handle & OffsetMask);

    /// <summary>
    /// Drops every staged payload and rewinds to an empty arena, RETAINING one block for the next transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reset is not Dispose. This runs between uses of a POOLED transaction, so freeing here would return every block to the OS and re-allocate it on the very
    /// next spawn — a per-transaction <c>NativeMemory</c> round trip, which is exactly the per-operation allocation cost this arena exists to remove. An
    /// earlier draft aliased the two (<c>Reset() =&gt; Dispose()</c>) and quietly turned the pool into a bump allocator with the allocation still in it.
    /// </para>
    /// <para>
    /// Only block 0 is retained, and that is load-bearing rather than arbitrary. Block 0 is always an ORDINARY block — the oversized path appends its own block
    /// but calls <see cref="AppendBlock"/> first when the arena is empty — so retaining index 0 cannot leave an oversized block at index 0, where the next
    /// oversized allocation would take offset 0 and encode to handle 0, which every consumer reads as "this slot has no payload". Retaining the whole block
    /// list instead would also let one batch-spawn transaction pin its high-water mark in the pool for the process lifetime.
    /// </para>
    /// <para>
    /// The retained block is released by <see cref="Dispose"/>, which the transaction lifecycle now calls — see <c>TransactionChain.Remove</c> for the
    /// not-pooled path and the chain's own teardown for the pooled ones. Without those, retention would be a native-memory leak per pooled transaction.
    /// </para>
    /// </remarks>
    /// <summary>Live native blocks. Test observability: the point of Reset is that this does NOT grow with the number of resets.</summary>
    internal int BlockCount => _blocks.Count;

    internal void Reset()
    {
        for (var i = _blocks.Count - 1; i >= 1; i--)
        {
            NativeMemory.Free((void*)_blocks[i]);
            _blocks.RemoveAt(i);
        }

        if (_blocks.Count == 0)
        {
            _currentBlock = -1;
            _currentUsed = 0;
            return;
        }

        // Block 0 keeps its reserved first slot so handle 0 stays "no payload" across the rewind.
        _currentBlock = 0;
        _currentUsed = Alignment;
    }

    public void Dispose()
    {
        for (var i = 0; i < _blocks.Count; i++)
        {
            NativeMemory.Free((void*)_blocks[i]);
        }
        _blocks.Clear();
        _currentBlock = -1;
        _currentUsed = 0;
    }

    /// <summary>
    /// Refuses to add a block whose index would not fit the handle's positive range.
    /// </summary>
    /// <remarks>
    /// A handle is a signed int packing <c>(blockIndex &lt;&lt; OffsetBits) | offset</c>, so one block past the limit sets the sign bit and every
    /// <c>handle == 0</c> / <c>handle &lt;= 0</c> guard downstream misreads it as "no payload". Both block-adding paths must call this — the ordinary one AND
    /// the oversized one, which appends to <c>_blocks</c> itself. Guarding only the common path is how the check gets bypassed by the rarer route.
    /// The transaction that trips this cannot be retried, only abandoned: the entity being staged is abandoned mid-slot and never reaches
    /// <c>_spawnedEntities</c>, so rollback will not see it.
    /// </remarks>
    private void EnsureBlockIndexFits()
    {
        if (_blocks.Count >= MaxBlocks)
        {
            ThrowHelper.ThrowInvalidOp(
                $"Spawn staging arena exceeded {MaxBlocks} blocks in one transaction; abandon it and spawn in smaller batches.");
        }
    }

    private void AppendBlock()
    {
        EnsureBlockIndexFits();
        var block = (byte*)NativeMemory.Alloc(BlockSize);
        _blocks.Add((IntPtr)block);
        _currentBlock = _blocks.Count - 1;

        // Reserve the first slot of the FIRST block only, so that handle 0 can mean "no payload" the way chunk id 0 does.
        _currentUsed = _currentBlock == 0 ? Alignment : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Encode(int blockIndex, int offset) => (blockIndex << OffsetBits) | offset;
}
