using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

[PublicAPI]
internal class ChainedBlockAllocator : ChainedBlockAllocatorBase
{
    public ChainedBlockAllocator(int stride, int entryCountPerPage, IResource parent, IMemoryAllocator memoryAllocator)
        : base(stride, entryCountPerPage, parent, memoryAllocator)
    {
    }
    public new Span<byte> AllocateBlock(out int blockId, bool rootChain) => base.AllocateBlock(out blockId, rootChain);
    public new Span<byte> GetBlockData(int blockId) => base.GetBlockData(blockId);
    public Span<byte> NextBlock(int blockId, out int nextBlockId)
    {
        nextBlockId = base.NextBlock(blockId);
        return nextBlockId == 0 ? Span<byte>.Empty : GetBlockData(nextBlockId);
    }

    public new int Stride => base.Stride;
}

[PublicAPI]
internal class ChainedBlockAllocator<T> : ChainedBlockAllocatorBase where T : struct
{
    public ChainedBlockAllocator(int entryCountPerPage, IResource parent, IMemoryAllocator memoryAllocator, int? strideOverride = null)
        : base(strideOverride ?? Unsafe.SizeOf<T>(), entryCountPerPage, parent, memoryAllocator)
    {
        Debug.Assert(Stride >= Unsafe.SizeOf<T>(), "If you override the stride, it must be at least the size of the type you want to allocate");
    }

    public ref T Allocate(out int blockId, bool rootChain)
    {
        ref var o = ref base.AllocateBlock(out blockId, rootChain).Cast<byte, T>()[0];
        o = default;                                // Clear the content
        return ref o;
    }

    public ref T Get(int blockId) => ref base.GetBlockData(blockId).Cast<byte, T>()[0];
    public ref T Next(ref T blockData)
    {
        var asSpan = MemoryMarshal.CreateSpan(ref blockData, 1).Cast<T, byte>();
        var nextSpan = base.NextBlock(asSpan);
        if (nextSpan.IsEmpty)
        {
            return ref Unsafe.NullRef<T>();
        }
        return ref nextSpan.Cast<byte, T>()[0];
    }

    public unsafe ref T SafeAppend(ref T block)
    {
        var headerPtr = (BlockHeader*)Unsafe.AsPointer(ref block) - 1;
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.SegmentAllocationLockTimeout);
        if (!headerPtr->AccessControl.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("SegmentAllocation/SafeAppend", TimeoutOptions.Current.SegmentAllocationLockTimeout);
        }
        if (headerPtr->NextBlockId != 0)
        {
            headerPtr->AccessControl.ExitExclusiveAccess();
            return ref Get(headerPtr->NextBlockId);
        }
        
        var span = AllocateBlockAsSpanInternal(out var newBlockId);
        span.Clear();
        span.Cast<byte, BlockHeader>()[0].ChainGeneration = headerPtr->ChainGeneration;

        headerPtr->NextBlockId = newBlockId;
        headerPtr->AccessControl.ExitExclusiveAccess();
        
        // Skip header
        return ref span.Slice(BlockHeaderSize).Cast<byte, T>()[0];
    }

    [PublicAPI]
    public new readonly struct Enumerable
    {
        private readonly ChainedBlockAllocator<T> _owner;
        private readonly int _blockId;

        public Enumerable(ChainedBlockAllocator<T> owner, int blockId)
        {
            _owner = owner;
            _blockId = blockId;
        }

        public Enumerator GetEnumerator() => new(_owner, _blockId);
    }

    [PublicAPI]
    public new ref struct Enumerator
    {
        private readonly ChainedBlockAllocator<T> _owner;
        private int _currentBlockId;
        private int _nextBlockId;
        private int _blockSize;

        public Enumerator(ChainedBlockAllocator<T> owner, int blockId)
        {
            _owner = owner;
            _currentBlockId = 0;
            _nextBlockId = blockId;
            _blockSize = _owner.Stride;
        }

        public ref T Current => ref _owner.Get(_currentBlockId);

        public bool MoveNext()
        {
            if (_nextBlockId == 0)
            {
                return false;
            }

            _currentBlockId = _nextBlockId;
            _nextBlockId = _owner.GetBlockAsSpanInternal(_nextBlockId).Cast<byte, int>().Slice(0, 1)[0];

            return true;
        }
    }
}
