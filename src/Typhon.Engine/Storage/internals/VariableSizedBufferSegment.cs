// CS1591: this file declares public-accessibility types that live in the internal namespace (Phase 2b entanglement, see
// claude/research/PublicVsInternalApiClassification.md). They are excluded from the published API reference, so consumer-facing
// doc coverage is not enforced here.
#pragma warning disable 1591

// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

[StructLayout(LayoutKind.Sequential)]
internal struct VariableSizedBufferRootHeader
{
    public VariableSizedBufferChunkHeader Header;   // Must be first member
    public AccessControl Lock;
    public int FirstFreeChunkId;
    public int FirstStoredChunkId;
    public int TotalCount;
    public short TotalFreeChunk;
    public short RefCounter;

    internal void EnterBufferLockForTest() => Lock.EnterExclusiveAccess(ref WaitContext.Null);
    internal void ExitBufferLockForTest() => Lock.ExitExclusiveAccess();
}

[StructLayout(LayoutKind.Sequential)]
internal struct VariableSizedBufferChunkHeader
{
    public int NextChunkId;
    public int ElementCount;
}

[PublicAPI]
public unsafe class VariableSizedBufferSegmentBase<TStore> where TStore : struct, IPageStore
{
    protected internal readonly int ElementCountRootChunk;
    protected internal readonly int ElementCountPerChunk;
    protected internal readonly int RootHeaderTotalSize;
    public readonly ChunkBasedSegment<TStore> Segment;

    /// <summary>Fixed byte size of one element in this buffer (the generic <c>T</c>). Surfaced for storage introspection (Module 15 A6).</summary>
    internal int ElementSize { get; }

    protected VariableSizedBufferSegmentBase(ChunkBasedSegment<TStore> segment, int elementSize) : this(segment, elementSize, sizeof(VariableSizedBufferRootHeader))
    {
    }

    protected VariableSizedBufferSegmentBase(ChunkBasedSegment<TStore> segment, int elementSize, int rootHeaderTotalSize)
    {
        ElementSize = elementSize;
        RootHeaderTotalSize = rootHeaderTotalSize;
        var stride = segment.Stride;
        Debug.Assert(rootHeaderTotalSize <= stride, $"Error, stride is too small, should be at least, {rootHeaderTotalSize} bytes.");

        ElementCountRootChunk = (stride - rootHeaderTotalSize) / ElementSize;
        ElementCountPerChunk = (stride - sizeof(VariableSizedBufferChunkHeader)) / ElementSize;
        Segment = segment;
    }

    public int AllocateBuffer(ref ChunkAccessor<TStore> accessor)
    {
        // Allocate and initialize the first chunk of the Buffer
        var segment = accessor.Segment;
        var chunkId = segment.AllocateChunk(false, accessor.ChangeSet);
        var addr = accessor.GetChunkAddress(chunkId, true);
        ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(addr);
        rh.Lock.Reset();
        rh.FirstFreeChunkId = 0;
        rh.FirstStoredChunkId = chunkId;
        rh.TotalCount = 0;
        rh.TotalFreeChunk = 0;
        rh.RefCounter = 1;
        rh.Header.NextChunkId = 0;
        rh.Header.ElementCount = 0;

        // Zero-initialize any extra header bytes beyond the standard root header
        var extraSize = RootHeaderTotalSize - sizeof(VariableSizedBufferRootHeader);
        if (extraSize > 0)
        {
            Unsafe.InitBlockUnaligned(addr + sizeof(VariableSizedBufferRootHeader), 0, (uint)extraSize);
        }

        return chunkId;
    }

    public int BufferAddRef(int bufferId, ref ChunkAccessor<TStore> accessor)
    {
        ref var rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
        try
        {
            // Lock the whole buffer as we are going to update it
            LockBuffer(ref rh);

            return ++rh.RefCounter;
        }
        finally
        {
            // Re-fetch rh — defensive, in case future changes add slot-evicting calls in the try block
            rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
            ReleaseLockOnBuffer(ref rh);
        }
    }

    /// <summary>
    /// Reads the buffer's reference count without locking. Safe for the copy-on-write decision: the count only decreases concurrently (background revision
    /// cleanup of a sharing revision), never increases (only the owning transaction's COW increments it, synchronously before any mutation). So a read of 1
    /// means sole ownership → safe to mutate in place; a read of >1 means another revision shares the buffer → must clone before mutating.
    /// </summary>
    public short GetRefCounter(int bufferId, ref ChunkAccessor<TStore> accessor) => accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, false).RefCounter;

    public int BufferRelease(int bufferId, ref ChunkAccessor<TStore> accessor)
    {
        ref var rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
        var deleted = false;
        try
        {
            LockBuffer(ref rh);

            var newValue = --rh.RefCounter;
            if (newValue == 0)
            {
                // Chain cleanup inline — do NOT call DeleteBuffer here, it would double-decrement RefCounter.
                FreeChunkChains(bufferId, rh.FirstFreeChunkId, ref accessor);
                deleted = true;
            }
            return newValue;
        }
        finally
        {
            // Re-fetch rh — chain traversal may have evicted its slot
            rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
            ReleaseLockOnBuffer(ref rh);
            if (deleted)
            {
                accessor.Segment.FreeChunk(bufferId);
            }
        }
    }

    public void DeleteBuffer(int bufferId, ref ChunkAccessor<TStore> accessor)
    {
        // Fetch the root chunk — epoch protects page lifetime
        var unlock = false;
        ref var rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
        try
        {
            // Lock the whole buffer as we are going to update it
            if (!rh.Lock.IsLockedByCurrentThread)
            {
                LockBuffer(ref rh);
                unlock = true;
            }

            if (--rh.RefCounter == 0)
            {
                FreeChunkChains(bufferId, rh.FirstFreeChunkId, ref accessor);
            }
        }
        finally
        {
            if (unlock)
            {
                // Re-fetch rh — GetChunkAddress calls in the loop may have evicted its slot
                rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
                ReleaseLockOnBuffer(ref rh);
            }
            accessor.Segment.FreeChunk(bufferId);
        }
    }

    // ── Raw (type-erased) element access — #389 ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Both #389 call sites hold a VariableSizedBufferSegmentBase<PersistentStore> and nothing more: it is the element type of
    // ComponentTable.CollectionFields, because a ComponentTable is not generic over its fields' element types. The commit emitter must READ a collection's
    // content to log it, and RecoveryApplier must REPLACE one wholesale when it flushes the fold. Neither can name T, so a Set<T> on the generic subclass
    // would be unreachable from both.
    //
    // Working in bytes costs nothing: every operation below is a memcpy at ElementSize stride, which the base already knows, and the elements are unmanaged
    // by constraint. The generic subclass keeps typed forwarders for callers that do have T.

    /// <summary>Total number of elements currently stored in <paramref name="bufferId"/>; <c>0</c> for the null buffer.</summary>
    internal int GetElementCount(int bufferId, ref ChunkAccessor<TStore> accessor)
    {
        if (bufferId == 0)
        {
            return 0;
        }

        return accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, false).TotalCount;
    }

    /// <summary>
    /// Copies every element of <paramref name="bufferId"/> into <paramref name="dest"/> as raw bytes, and returns the number of elements copied.
    /// </summary>
    /// <remarks>
    /// A hand-rolled chunk walk rather than <c>VariableSizedBufferAccessor.NextChunk</c>, deliberately: that "read-only" enumerator MUTATES — it promotes the
    /// buffer lock to exclusive and frees or free-lists empty chunks as it goes. That is fine for a reader that owns the buffer, but this runs inside
    /// <c>BuildCommitBatch</c>, on the commit path, where structural mutation of a buffer other revisions may share is not something a LOG step should do.
    /// </remarks>
    internal int ReadAllElementsRaw(int bufferId, Span<byte> dest, ref ChunkAccessor<TStore> accessor)
    {
        if (bufferId == 0)
        {
            return 0;
        }

        ref var rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, false);
        var total = rh.TotalCount;
        if (total == 0)
        {
            return 0;
        }

        if (dest.Length < total * ElementSize)
        {
            ThrowHelper.ThrowInvalidOp($"ReadAllElementsRaw: destination holds {dest.Length} bytes, need {total * ElementSize} for {total} element(s).");
        }

        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.SegmentAllocationLockTimeout);
        if (!rh.Lock.EnterSharedAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("SegmentAllocation/BufferRead", TimeoutOptions.Current.SegmentAllocationLockTimeout);
        }

        var copied = 0;
        try
        {
            var curChunkId = bufferId;   // FirstStoredChunkId names the chunk last APPENDED to, not the head of the chain — the chain always starts at the root
            while (curChunkId != 0 && copied < total)
            {
                var addr = accessor.GetChunkAddress(curChunkId, false);
                var header = (VariableSizedBufferChunkHeader*)addr;
                var count = header->ElementCount;
                if (count > 0)
                {
                    var payload = addr + (curChunkId == bufferId ? RootHeaderTotalSize : sizeof(VariableSizedBufferChunkHeader));
                    new ReadOnlySpan<byte>(payload, count * ElementSize).CopyTo(dest[(copied * ElementSize)..]);
                    copied += count;
                }

                curChunkId = header->NextChunkId;
            }
        }
        finally
        {
            rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, false);
            rh.Lock.ExitSharedAccess();
        }

        return copied;
    }

    /// <summary>
    /// Replaces a collection's entire content: allocates a fresh buffer holding <paramref name="elements"/>, releases
    /// <paramref name="bufferId"/>, and returns the NEW buffer id (<c>0</c> when <paramref name="elements"/> is empty).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The primitive <c>RecoveryApplier</c>'s fold-flush needs and the one operation the collection API never had — the whole mutation surface was
    /// <c>Add</c>. Allocate-then-release rather than truncate-in-place, for three reasons that all matter here:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Copy-on-write safety falls out for free.</b> A buffer shared with another MVCC revision has <c>RefCounter &gt; 1</c>; writing into it would corrupt
    /// the other revision. Never touching the old buffer's bytes makes that unreachable by construction rather than by a check someone can forget.
    /// </description></item>
    /// <item><description>
    /// <b>Idempotence (AP-12).</b> Set(x) twice yields the same content and the same refcount as Set(x) once — the second call simply allocates again and
    /// releases the first result. Only the buffer ID differs, which AP-13 explicitly tolerates: placements chosen at apply may differ from pre-crash.
    /// </description></item>
    /// <item><description>
    /// <b>Empty means no buffer</b>, matching the live shape exactly: a collection that was never appended to has <c>_bufferId == 0</c>, so setting one to
    /// empty must produce 0 too, not a fresh empty root chunk that would leak one allocation per empty collection on every recovery.
    /// </description></item>
    /// </list>
    /// <para>
    /// Release goes through <see cref="BufferRelease"/>, never <c>DeleteBuffer</c> — the latter decrements the refcount a second time (see the comment at
    /// its chain-cleanup branch), which on a shared buffer frees it under a live holder.
    /// </para>
    /// </remarks>
    internal int SetElementsRaw(int bufferId, ReadOnlySpan<byte> elements, ref ChunkAccessor<TStore> accessor)
    {
        if (elements.Length % ElementSize != 0)
        {
            ThrowHelper.ThrowInvalidOp($"SetElementsRaw: {elements.Length} bytes is not a whole number of {ElementSize}-byte elements.");
        }

        var newBufferId = elements.Length == 0 ? 0 : AllocateBuffer(ref accessor);
        if (newBufferId != 0)
        {
            FillFreshBufferRaw(newBufferId, elements, ref accessor);
        }

        if (bufferId != 0)
        {
            BufferRelease(bufferId, ref accessor);
        }

        return newBufferId;
    }

    /// <summary>
    /// Writes <paramref name="elements"/> into a buffer that was just allocated and is therefore empty, unshared and unreachable by any other thread.
    /// </summary>
    /// <remarks>
    /// No buffer lock is taken and no free-list is consulted, because neither can matter for a buffer nobody else has seen yet. That is also why this is not
    /// expressed in terms of the general append path: <c>AddElements</c> carries free-list bookkeeping and re-fetch discipline that exist for the shared case
    /// and would be pure noise here.
    /// </remarks>
    private void FillFreshBufferRaw(int bufferId, ReadOnlySpan<byte> elements, ref ChunkAccessor<TStore> accessor)
    {
        var remaining = elements.Length / ElementSize;
        var srcElement = 0;
        var curChunkId = bufferId;

        while (true)
        {
            var isRoot = curChunkId == bufferId;
            var capacity = isRoot ? ElementCountRootChunk : ElementCountPerChunk;
            var take = Math.Min(capacity, remaining);

            // Copy first, then take the next chunk: AllocateChunk can evict the current chunk's slot from the accessor's cache, so the address must not
            // outlive it. Every address below is re-fetched after any call that can allocate.
            var addr = accessor.GetChunkAddress(curChunkId, true);
            if (take > 0)
            {
                var payload = addr + (isRoot ? RootHeaderTotalSize : sizeof(VariableSizedBufferChunkHeader));
                elements.Slice(srcElement * ElementSize, take * ElementSize).CopyTo(new Span<byte>(payload, take * ElementSize));
            }

            ((VariableSizedBufferChunkHeader*)addr)->ElementCount = take;
            ((VariableSizedBufferChunkHeader*)addr)->NextChunkId = 0;
            srcElement += take;
            remaining -= take;

            if (remaining == 0)
            {
                break;
            }

            var nextChunkId = accessor.Segment.AllocateChunk(false, accessor.ChangeSet);
            addr = accessor.GetChunkAddress(curChunkId, true);   // re-fetch: AllocateChunk may have evicted this slot
            ((VariableSizedBufferChunkHeader*)addr)->NextChunkId = nextChunkId;
            curChunkId = nextChunkId;
        }

        ref var rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
        rh.FirstStoredChunkId = curChunkId;   // the chunk a subsequent Add appends into — the last one written, as AddElements leaves it
        rh.FirstFreeChunkId = 0;
        rh.TotalFreeChunk = 0;
        rh.TotalCount = elements.Length / ElementSize;
    }

    /// <summary>
    /// Frees every chunk a dying buffer owns — its storage chain and its free-chunk chain — except the root, which the caller frees last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The walk starts at the root, NOT at <c>FirstStoredChunkId</c>.</b> Both release paths used to start there, and the field does not mean what its
    /// name suggests: <c>AddElement</c> assigns it the chunk it just appended into, so it is the TAIL of the chain — the append cursor. The head is the root
    /// chunk itself, which is exactly where <see cref="VariableSizedBufferAccessor{T,TStore}"/> starts reading. Walking from the tail therefore freed the tail
    /// and nothing else, orphaning every intermediate chunk of a multi-chunk buffer. A single-chunk buffer has root == tail, which is why this survived: the
    /// defect is invisible until a collection outgrows one chunk, and it leaks silently rather than failing.
    /// </para>
    /// <para>
    /// The free-chunk chain is walked too. <c>VariableSizedBufferAccessor.NextChunk</c> unlinks empty chunks from the storage chain and parks them on
    /// <c>FirstFreeChunkId</c> for reuse; those are still owned by this buffer, so a release that only walked the storage chain leaked them as well.
    /// </para>
    /// </remarks>
    private void FreeChunkChains(int bufferId, int firstFreeChunkId, ref ChunkAccessor<TStore> accessor)
    {
        // Two passes over one loop body rather than a local function: `accessor` is a ref parameter and cannot be captured by one.
        for (var pass = 0; pass < 2; pass++)
        {
            var curChunkId = pass == 0 ? bufferId : firstFreeChunkId;
            while (curChunkId != 0)
            {
                // Read NextChunkId into a local before any further accessor call — FreeChunk/GetChunkAddress can evict this chunk's slot from the accessor's
                // 16-entry cache, and a ref taken beforehand would then point at another page's bytes.
                var curChunkAddr = accessor.GetChunkAddress(curChunkId, true);
                var toDeleteChunkId = curChunkId;
                curChunkId = ((VariableSizedBufferChunkHeader*)curChunkAddr)->NextChunkId;

                if (toDeleteChunkId != bufferId)
                {
                    accessor.Segment.FreeChunk(toDeleteChunkId);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void LockBuffer(ref VariableSizedBufferRootHeader rh)
    {
        // Fast path: uncontended lock — no timestamp syscall needed
        if (rh.Lock.TryEnterExclusiveAccess())
        {
            return;
        }

        // Slow path: contended — create WaitContext for timeout
        LockBufferSlow(ref rh);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LockBufferSlow(ref VariableSizedBufferRootHeader rh)
    {
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.SegmentAllocationLockTimeout);
        if (!rh.Lock.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("SegmentAllocation/LockBuffer", TimeoutOptions.Current.SegmentAllocationLockTimeout);
        }
    }
    internal void ReleaseLockOnBuffer(ref VariableSizedBufferRootHeader header) => header.Lock.ExitExclusiveAccess();
}

/// <summary>
/// Segment to store variable size buffer of elements
/// </summary>
/// <remarks>
/// The segment stores multiple buffers containing a variable size of a uniform element type.
/// The internal structure is simple:
///  - The segment is based from <see cref="ChunkBasedSegment{TStore}"/>, each chunk stores a given number of elements (may be variable because we also use
///    the chunk's data for internal data storage).
///  - Chunks are linked together to form a forward linked list allowing a sequential processing of the buffer (we maintain two linked-list, one for enumeration
///    using the Accessor and the other one to locate free chunks).
///  - Grow is fast as it's just allocating one more chunk and link it. Append is relatively fast as we know where to put the element using a linked-list or
///    chunks containing free entries.
///  - Elements can be removed, the chunk is then packed to store the occupied entries at first positions, elements are located by their ChunkId and then
///    a linear search into it.
///  - Reading the whole buffer requires nested loop pattern using the <see cref="VariableSizedBufferAccessor{T, TStore}"/> accessor.
///  - Empty chunks are being removed (if exclusive access can be made) during enumeration via the ReadOnlyAccessor.
///  - There is no API for Random access of an element inside a given buffer, it could be done but would be slow.
/// </remarks>
[PublicAPI]
public class VariableSizedBufferSegment<T, TStore> : VariableSizedBufferSegmentBase<TStore> where T : unmanaged where TStore : struct, IPageStore
{
    // protected ChunkRandomAccessor ChunkAccessor<TStore>;

    unsafe public VariableSizedBufferSegment(ChunkBasedSegment<TStore> segment) : base(segment, sizeof(T))
    {
    }

    unsafe protected VariableSizedBufferSegment(ChunkBasedSegment<TStore> segment, int rootHeaderTotalSize) : base(segment, sizeof(T), rootHeaderTotalSize)
    {
    }

    unsafe public int AddElement(int bufferId, T value, ref ChunkAccessor<TStore> accessor)
    {
        // Fetch the root chunk — epoch protects page lifetime
        ref var rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);

        try
        {
            // Lock the whole buffer as we are going to update it
            LockBuffer(ref rh);

            // Detect use-after-free: refCount should be >= 1 for any live buffer.
            // refCount=0 means the buffer was freed via BufferRelease/DeleteBuffer and
            // the chunk may have been returned to the segment's free pool and reused.
            if (rh.RefCounter <= 0)
            {
                throw new InvalidOperationException(
                    $"VSBS.AddElement: use-after-free detected! bufferId={bufferId} refCount={rh.RefCounter} " +
                    $"isAllocated={accessor.Segment.IsChunkAllocated(bufferId)} capacity={accessor.Segment.ChunkCapacity} " +
                    $"FirstStoredChunkId={rh.FirstStoredChunkId} TotalCount={rh.TotalCount}");
            }

            // Copy structural fields to locals BEFORE any GetChunkAddress/AllocateChunk calls.
            // These calls can evict rh's slot from the 16-slot accessor cache, making rh point
            // to a different page's data. Working with locals is always safe (stack-allocated).
            int curChunkId = rh.FirstStoredChunkId;
            int firstFreeChunkId = rh.FirstFreeChunkId;
            short totalFreeChunk = rh.TotalFreeChunk;

            // Validate that the root header contains a valid FirstStoredChunkId
            if ((uint)curChunkId >= (uint)accessor.Segment.ChunkCapacity)
            {
                throw new InvalidOperationException(
                    $"VSBS.AddElement: root header at bufferId={bufferId} has stale FirstStoredChunkId={curChunkId} " +
                    $"(capacity={accessor.Segment.ChunkCapacity}, firstFree={firstFreeChunkId}, totalFree={totalFreeChunk}, " +
                    $"totalCount={rh.TotalCount}, refCount={rh.RefCounter})");
            }

            var curChunkAddr = accessor.GetChunkAddress(curChunkId, true);
            ref var curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);

            var isRoot = bufferId == curChunkId;
            var chunkCapacity = isRoot ? ElementCountRootChunk : ElementCountPerChunk;

            // If we reached capacity, get a new chunk
            if (curChunkHeader.ElementCount == chunkCapacity)
            {
                // Take a free chunk or allocate a new one
                if (firstFreeChunkId != 0)
                {
                    curChunkId = firstFreeChunkId;
                    --totalFreeChunk;
                }
                else
                {
                    curChunkId = accessor.Segment.AllocateChunk(false, accessor.ChangeSet);
                }

                curChunkHeader.NextChunkId = curChunkId;

                // Fetch the new chunk
                curChunkAddr = accessor.GetChunkAddress(curChunkId, true);
                curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);

                curChunkHeader.ElementCount = 0;
                curChunkHeader.NextChunkId = 0;

                // Update local: the free chunk we took has no next free (just zeroed above)
                firstFreeChunkId = curChunkHeader.NextChunkId;

                // Update root and capacity as we switched to a new chunk
                isRoot = bufferId == curChunkId;
            }

            // Add our element to the chunk
            var baseElementAddr = (T*)(curChunkAddr + (isRoot ? RootHeaderTotalSize : sizeof(VariableSizedBufferChunkHeader)));
            baseElementAddr[curChunkHeader.ElementCount++] = value;

            // Write back structural fields via a fresh ref — rh's slot may have been evicted
            rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
            rh.FirstStoredChunkId = curChunkId;
            rh.FirstFreeChunkId = firstFreeChunkId;
            rh.TotalFreeChunk = totalFreeChunk;
            ++rh.TotalCount;

            return curChunkId;
        }
        finally
        {
            // Re-fetch for unlock — slot may have been evicted during the try block
            rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
            ReleaseLockOnBuffer(ref rh);
        }
    }

    unsafe public void AddElements(int bufferId, ReadOnlySpan<T> items, ref ChunkAccessor<TStore> accessor)
    {
        // Fetch the root chunk — epoch protects page lifetime
        ref var rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
        try
        {
            // Lock the whole buffer as we are going to update it
            LockBuffer(ref rh);

            // Copy structural fields to locals BEFORE any GetChunkAddress/AllocateChunk calls.
            // These calls can evict rh's slot from the 16-slot accessor cache.
            int curChunkId = rh.FirstStoredChunkId;
            int firstFreeChunkId = rh.FirstFreeChunkId;
            short totalFreeChunk = rh.TotalFreeChunk;
            int totalCount = rh.TotalCount;

            var curChunkAddr = accessor.GetChunkAddress(curChunkId, true);
            ref var curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);

            var curSourceIndex = 0;
            var itemsLeftToCopy = items.Length;
            while (itemsLeftToCopy > 0)
            {
                var isRoot = bufferId == curChunkId;
                var chunkCapacity = isRoot ? ElementCountRootChunk : ElementCountPerChunk;

                // If we reached capacity, get a new chunk
                if (curChunkHeader.ElementCount == chunkCapacity)
                {
                    // Take a free chunk or allocate a new one
                    if (firstFreeChunkId != 0)
                    {
                        curChunkId = firstFreeChunkId;
                        --totalFreeChunk;
                    }
                    else
                    {
                        curChunkId = accessor.Segment.AllocateChunk(false, accessor.ChangeSet);
                    }

                    curChunkHeader.NextChunkId = curChunkId;

                    // Fetch the new chunk
                    curChunkAddr = accessor.GetChunkAddress(curChunkId, true);
                    curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);

                    curChunkHeader.ElementCount = 0;
                    curChunkHeader.NextChunkId = 0;

                    // Update local: the free chunk we took has no next free (just zeroed above)
                    firstFreeChunkId = curChunkHeader.NextChunkId;

                    // Update root and capacity as we switched to a new chunk
                    isRoot = bufferId == curChunkId;
                }

                var copyLength = Math.Min(chunkCapacity - curChunkHeader.ElementCount, itemsLeftToCopy);
                var dstSpan = new Span<T>((curChunkAddr + (isRoot ? RootHeaderTotalSize : sizeof(VariableSizedBufferChunkHeader))),
                    chunkCapacity);
                items.Slice(curSourceIndex, copyLength).CopyTo(dstSpan.Slice(curChunkHeader.ElementCount));

                totalCount += copyLength;
                curChunkHeader.ElementCount += copyLength;
                itemsLeftToCopy -= copyLength;
            }

            // Write back structural fields via a fresh ref — rh's slot may have been evicted
            rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
            rh.FirstStoredChunkId = curChunkId;
            rh.FirstFreeChunkId = firstFreeChunkId;
            rh.TotalFreeChunk = totalFreeChunk;
            rh.TotalCount = totalCount;
        }
        finally
        {
            // Re-fetch for unlock — slot may have been evicted during the try block
            rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
            ReleaseLockOnBuffer(ref rh);
        }
    }

    unsafe public int DeleteElement(int bufferId, int elementId, T element, ref ChunkAccessor<TStore> accessor)
    {
        // Fetch the root chunk — epoch protects page lifetime
        ref var rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
        try
        {
            // Lock the whole buffer as we are going to update it
            LockBuffer(ref rh);

            // Fetch the chunk storing the element — this can evict rh's slot
            var elementChunk = accessor.GetChunkAddress(elementId, true);
            ref var elementChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(elementChunk);
            var isRoot = bufferId == elementId;
            var baseElementAddr = (T*)(elementChunk + (isRoot ? RootHeaderTotalSize : sizeof(VariableSizedBufferChunkHeader)));

            // Look for our element
            var count = elementChunkHeader.ElementCount;
            int i;
            for (i = 0; i < count; i++)
            {
                if (EqualityComparer<T>.Default.Equals(baseElementAddr[i], element))
                {
                    break;
                }
            }

            if (i == count) return -1;

            // Replace this slot by the last element to keep an un-fragmented collection
            baseElementAddr[i] = baseElementAddr[count - 1];
#if DEBUG
            baseElementAddr[count - 1] = default(T);
#endif
            --elementChunkHeader.ElementCount;

            // Re-fetch rh before writing TotalCount — GetChunkAddress(elementId) may have evicted its slot
            rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
            --rh.TotalCount;

            return rh.TotalCount;
        }
        finally
        {
            // Re-fetch for unlock — slot may have been evicted
            rh = ref accessor.GetChunk<VariableSizedBufferRootHeader>(bufferId, true);
            ReleaseLockOnBuffer(ref rh);
        }
    }

    public VariableSizedBufferAccessor<T, TStore> GetReadOnlyAccessor(int bufferId) => new(this, bufferId);
    public VariableSizedBufferAccessor<T, TStore> GetAccessor(int bufferId, ChangeSet changeSet) => new(this, bufferId, changeSet);

    /// <summary>
    /// Returns a zero-allocation enumerator for iterating over all elements in the buffer.
    /// </summary>
    /// <param name="bufferId">The buffer identifier</param>
    /// <returns>A ref struct enumerator that can be used in foreach loops</returns>
    public BufferEnumerator<T, TStore> EnumerateBuffer(int bufferId) => new(this, bufferId);

    /// <summary>
    /// Typed forwarder to <see cref="VariableSizedBufferSegmentBase{TStore}.SetElementsRaw"/> — replaces the buffer's whole content and returns the new id.
    /// </summary>
    public int SetElements(int bufferId, ReadOnlySpan<T> elements, ref ChunkAccessor<TStore> accessor) =>
        SetElementsRaw(bufferId, MemoryMarshal.AsBytes(elements), ref accessor);

    /// <summary>
    /// Typed forwarder to <see cref="VariableSizedBufferSegmentBase{TStore}.ReadAllElementsRaw"/> — returns the number of elements copied into
    /// <paramref name="dest"/>.
    /// </summary>
    public int ReadAllElements(int bufferId, Span<T> dest, ref ChunkAccessor<TStore> accessor) =>
        ReadAllElementsRaw(bufferId, MemoryMarshal.AsBytes(dest), ref accessor);

    public int CloneBuffer(int sourceBufferId, ref ChunkAccessor<TStore> accessor)
    {
        var destBufferId = AllocateBuffer(ref accessor);
        using var source = GetReadOnlyAccessor(sourceBufferId);
        do
        {
            AddElements(destBufferId, source.Elements, ref accessor);
        } while (source.NextChunk());

        return destBufferId;
    }
}

/// <summary>
/// Zero-allocation enumerator for iterating over all elements in a variable-sized buffer.
/// This is a ref struct to ensure stack allocation and zero GC pressure.
/// </summary>
/// <typeparam name="T">The unmanaged element type</typeparam>
/// <typeparam name="TStore">The <see cref="IPageStore"/> implementation backing the buffer's chunks.</typeparam>
[PublicAPI]
public ref struct BufferEnumerator<T, TStore> where T : unmanaged where TStore : struct, IPageStore
{
    private VariableSizedBufferAccessor<T, TStore> _accessor;
    private int _currentIndex;
    private int _currentChunkLength;
    private bool _isValid;

    internal BufferEnumerator(VariableSizedBufferSegment<T, TStore> owner, int bufferId)
    {
        _accessor = owner.GetReadOnlyAccessor(bufferId);
        _currentIndex = -1;
        _currentChunkLength = _accessor.ReadOnlyElements.Length;
        _isValid = _currentChunkLength > 0;
    }

    /// <summary>
    /// Returns this enumerator (required for ForEach pattern)
    /// </summary>
    public BufferEnumerator<T, TStore> GetEnumerator() => this;

    /// <summary>
    /// Gets the current element as a readonly reference (zero-copy)
    /// </summary>
    public ref readonly T Current
    {
        get => ref _accessor.ReadOnlyElements[_currentIndex];
    }

    /// <summary>
    /// Advances to the next element, automatically traversing chunks as needed
    /// </summary>
    public bool MoveNext()
    {
        if (!_isValid)
        {
            return false;
        }

        _currentIndex++;

        // Check if we're still within the current chunk
        if (_currentIndex < _currentChunkLength)
        {
            return true;
        }

        // Try to move to the next chunk
        if (_accessor.NextChunk())
        {
            _currentIndex = 0;
            _currentChunkLength = _accessor.ReadOnlyElements.Length;
            return _currentChunkLength > 0;
        }

        _isValid = false;
        return false;
    }

    /// <summary>
    /// Disposes the underlying accessor and releases locks
    /// </summary>
    public void Dispose() => _accessor.Dispose();
}

[PublicAPI]
public ref struct VariableSizedBufferAccessor<T, TStore> : IDisposable where T : unmanaged where TStore : struct, IPageStore
{
    private readonly VariableSizedBufferSegment<T, TStore> _owner;
    private readonly ChunkBasedSegment<TStore> _segment;
    private readonly int _rootHeaderTotalSize;

    private int _rootChunkId;
    private unsafe byte* _rootChunkAddr;
    private ChunkAccessor<TStore> _accessor;

    private int _curChunkId;
    private unsafe byte* _curChunkAddr;

    private unsafe byte* _elementAddr;
    private int _elementCount;

    public bool IsValid => _rootChunkId != 0;
    public unsafe ReadOnlySpan<T> ReadOnlyElements => _elementAddr==null ? default : new(_elementAddr, _elementCount);
    public unsafe Span<T> Elements => new(_elementAddr, _elementCount);
    public void DirtyChunk() => _accessor.DirtyChunk(_curChunkId);

    unsafe public int TotalCount
    {
        get
        {
            ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(_rootChunkAddr);
            return rh.TotalCount;
        }
    }

    unsafe public int RefCounter
    {
        get
        {
            ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(_rootChunkAddr);
            return rh.RefCounter;
        }
    }

    unsafe public VariableSizedBufferAccessor(VariableSizedBufferSegment<T, TStore> owner, int rootChunkId, ChangeSet changeSet = null)
    {
        _owner = owner;
        _segment = owner.Segment;
        _rootHeaderTotalSize = owner.RootHeaderTotalSize;
        _rootChunkId = rootChunkId;

        _accessor = _segment.CreateChunkAccessor(changeSet);

        _rootChunkAddr = _accessor.GetChunkAddress(rootChunkId);
        ref var rh = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(_rootChunkAddr);

        // Enter read mode
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.SegmentAllocationLockTimeout);
        if (!rh.Lock.EnterSharedAccess(ref wc))
        {
            _accessor.Dispose();
            ThrowHelper.ThrowLockTimeout("SegmentAllocation/BufferRead", TimeoutOptions.Current.SegmentAllocationLockTimeout);
        }

        // Switch to the first chunk that contains stored data
        _curChunkId = _rootChunkId;
        _curChunkAddr = _accessor.GetChunkAddress(_curChunkId);

        _elementAddr = _curChunkAddr + (_curChunkId==rootChunkId ? _rootHeaderTotalSize : sizeof(VariableSizedBufferChunkHeader));
        _elementCount = ((VariableSizedBufferChunkHeader*)_curChunkAddr)->ElementCount;

        if (_elementCount == 0) NextChunk();
    }

    unsafe public bool NextChunk()
    {
        // Read next chunk from the current header
        var nextChunkId = ((VariableSizedBufferChunkHeader*)_curChunkAddr)->NextChunkId;
        var prevChunkId = _curChunkId;
        var prevChunk = (VariableSizedBufferChunkHeader*)_curChunkAddr;

        // Quit if there's no more
        if (nextChunkId == 0)
        {
            _curChunkId = 0;
            _elementAddr = null;
            return false;
        }

        // Fetch the new chunk
        var nextChunkAddr = _accessor.GetChunkAddress(nextChunkId, true);
        var nextChunkElementCount = ((VariableSizedBufferChunkHeader*)nextChunkAddr)->ElementCount;

        // Check if the chunk is empty, then try to remove it from the storage list
        if (nextChunkElementCount == 0)
        {
            ref var rootChunk = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(_rootChunkAddr);

            // Try to promote the Buffer from read to read/write because we need to make changes
            var wcPromote = WaitContext.FromTimeout(TimeoutOptions.Current.SegmentAllocationLockTimeout);
            if (rootChunk.Lock.TryPromoteToExclusiveAccess(ref wcPromote))
            {
                // Try to latch the root chunk for exclusive write access
                if (_accessor.TryLatchExclusive(_rootChunkId))
                {
                    // Setup our forward link list info
                    var curChunkId  = nextChunkId;
                    var curChunk  = (VariableSizedBufferChunkHeader*)nextChunkAddr;

                    // We don't want to chain to the free-list all the empty chunks, would be a waste of space.
                    // Let's keep to grow the current count by 25%, approximately, with a minimum of 8 free chunks
                    var epc = _owner.ElementCountRootChunk;
                    var tc = rootChunk.TotalCount;
                    var freeChunkThreshold = Math.Max(tc / (epc * 4), 8);

                    // To collect an empty chunk we need to latch both the previous and current chunks.
                    // We can't make modifications otherwise
                    // BEWARE: Each successful latch needs its corresponding unlatch call!
                    if (_accessor.TryLatchExclusive(prevChunkId))
                    {
                        // We jump over empty chunks as long as there are some
                        while ((curChunk != null) && (curChunk->ElementCount == 0))
                        {
                            if (_accessor.TryLatchExclusive(curChunkId))
                            {
                                // Fix the storage link-list by removing the empty chunk
                                prevChunk->NextChunkId = curChunk->NextChunkId;

                                // Check if we must free the chunk or link it to the free list
                                if (rootChunk.TotalFreeChunk > freeChunkThreshold)
                                {
                                    _segment.FreeChunk(curChunkId);
                                }
                                else
                                {
                                    // Link the empty chunk to the rest of the free link-list
                                    curChunk->NextChunkId = rootChunk.FirstFreeChunkId;

                                    // First empty chunk is pointing to the one we just pop
                                    rootChunk.FirstFreeChunkId = curChunkId;
                                    ++rootChunk.TotalFreeChunk;
                                }

                                _accessor.UnlatchExclusive(curChunkId);
                            }

                            // Update the new current chunk to be the next in line
                            curChunkId = prevChunk->NextChunkId;
                            curChunk = (curChunkId != 0) ? (VariableSizedBufferChunkHeader*)_accessor.GetChunkAddress(curChunkId, true) : null;
                        }

                        _accessor.UnlatchExclusive(prevChunkId);
                    }

                    // Update members needed for the end of the method
                    nextChunkId = curChunkId;
                    nextChunkAddr = (byte*)curChunk;

                    // Release exclusive latch on root
                    _accessor.UnlatchExclusive(_rootChunkId);
                }
                rootChunk.Lock.DemoteFromExclusiveAccess();
            }
        }

        // Check if we reached the end of the VSB
        if (nextChunkAddr == null)
        {
            _curChunkId = 0;
            _elementAddr = null;
            return false;
        }

        _curChunkId = nextChunkId;
        _curChunkAddr = _accessor.GetChunkAddress(_curChunkId);
        _elementAddr = _curChunkAddr + (_curChunkId == _rootChunkId ? _rootHeaderTotalSize : sizeof(VariableSizedBufferChunkHeader));
        _elementCount = ((VariableSizedBufferChunkHeader*)_curChunkAddr)->ElementCount;

        return true;
    }

    public unsafe void Dispose()
    {
        if (!IsValid)
        {
            // Still need to dispose accessor if it was created
            _accessor.Dispose();
            return;
        }

        ref var h = ref Unsafe.AsRef<VariableSizedBufferRootHeader>(_rootChunkAddr);
        h.Lock.ExitSharedAccess();

        _accessor.Dispose();
        _rootChunkId = 0;
        _curChunkId = 0;
    }
}