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
    protected readonly int ElementCountPerChunk;
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
                // Copy FirstStoredChunkId to local — rh may go stale during the loop
                int curChunkId = rh.FirstStoredChunkId;
                while (curChunkId != 0)
                {
                    var curChunkAddr = accessor.GetChunkAddress(curChunkId, true);
                    ref var curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);
                    var toDeleteChunkId = curChunkId;
                    curChunkId = curChunkHeader.NextChunkId;
                    if (toDeleteChunkId != bufferId)
                    {
                        accessor.Segment.FreeChunk(toDeleteChunkId);
                    }
                }
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
                // Copy FirstStoredChunkId to local — rh may go stale during the loop
                int curChunkId = rh.FirstStoredChunkId;

                while (curChunkId != 0)
                {
                    var curChunkAddr = accessor.GetChunkAddress(curChunkId, true);
                    ref var curChunkHeader = ref Unsafe.AsRef<VariableSizedBufferChunkHeader>(curChunkAddr);

                    var toDeleteChunkId = curChunkId;
                    // Read NextChunkId immediately to local before any further accessor calls
                    curChunkId = curChunkHeader.NextChunkId;

                    if (toDeleteChunkId != bufferId)
                    {
                        accessor.Segment.FreeChunk(toDeleteChunkId);
                    }
                }
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
/// Extended VSBS that appends a typed extra header after the standard <see cref="VariableSizedBufferRootHeader"/>.
/// The extra header is automatically zeroed on buffer allocation.
/// </summary>
/// <typeparam name="T">The unmanaged element type stored in the buffer.</typeparam>
/// <typeparam name="TExtraHeader">The unmanaged struct appended after the root header in the root chunk.</typeparam>
/// <typeparam name="TStore">The <see cref="IPageStore"/> implementation backing the segment's chunks (persistent or transient).</typeparam>
[PublicAPI]
public class VariableSizedBufferSegment<T, TExtraHeader, TStore> : VariableSizedBufferSegment<T, TStore> where T : unmanaged where TExtraHeader : unmanaged where TStore : struct, IPageStore
{
    public unsafe VariableSizedBufferSegment(ChunkBasedSegment<TStore> segment) : base(segment, sizeof(VariableSizedBufferRootHeader) + sizeof(TExtraHeader))
    {
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