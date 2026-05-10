using JetBrains.Annotations;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Typhon.Engine.Internals;

[PublicAPI]
internal unsafe abstract class ChainedBlockAllocatorBase : BlockAllocatorBase
{
    public static readonly int BlockHeaderSize = sizeof(BlockHeader);
    private static int NextChainGeneration;

    protected struct BlockHeader
    {
        // If the bit is set, there's a pending free request, which must prevent further usage
        const uint FreeRequested    = 0x8000_0000U;

        // 15 bits that store the chain length
        const uint ChainLengthMask = 0x7FFF_0000U;
        const int ChainLengthShift = 16;

        // 16 bits that store:
        // - 0xFFFF: block is free
        // - 0x0000: block is allocated but not linked to a chain
        // - 0x0001 - 0xFFFE: generation of the chain the block is linked to
        const uint ChainGenerationMask   = 0x0000_FFFFU;
        
        const uint IsChainRootFlag = 0x8000_0000U;
        
        public int ChainGeneration
        {
            get => (int)(_data & ChainGenerationMask);
            set => _data = (_data & ~ChainGenerationMask) | (uint)(value & ChainGenerationMask);
        }

        public int ChainLength
        {
            get => (int)((_data & ChainLengthMask) >> ChainLengthShift);
            set => _data = (_data & ~ChainLengthMask) | ((uint)(value & 0x7FFF) << ChainLengthShift);
        }

        public bool IsChainRoot
        {
            get => (_data2 & IsChainRootFlag) != 0;
            set => _data2 = value ? (_data2 | IsChainRootFlag) : (_data2 & ~IsChainRootFlag);
        }

        public int ChainRootBlockId
        {
            get
            {
                Debug.Assert(!IsChainRoot);
                return (int)(_data2 & ~IsChainRootFlag);
            }
            set
            {
                Debug.Assert(!IsChainRoot);
                _data2 = (_data2 & IsChainRootFlag) | (uint)(value & ~IsChainRootFlag);
            }
        }

        public int GetChainRootBlockId(int blockId) => IsChainRoot ? blockId : ChainRootBlockId;

        public int LastBlockId
        {
            get
            {
                Debug.Assert(IsChainRoot);
                return (int)(_data2 & ~IsChainRootFlag);
            }
            set
            {
                Debug.Assert(IsChainRoot);
                _data2 = (_data2 & IsChainRootFlag) | (uint)(value & ~IsChainRootFlag);
            }
        }
        
        private uint _data;
        private uint _data2;
        public int NextBlockId;
        public AccessControlSmall AccessControl;

        public bool RequestEnumeration(out int chainGeneration)
        {
            var wc = WaitContext.FromTimeout(TimeoutOptions.Current.SegmentAllocationLockTimeout);
            if (!AccessControl.EnterSharedAccess(ref wc))
            {
                ThrowHelper.ThrowLockTimeout("SegmentAllocation/RequestEnumeration", TimeoutOptions.Current.SegmentAllocationLockTimeout);
            }
            var newData = _data;
            if ((newData & FreeRequested) != 0)
            {
                chainGeneration = 0;
                return false;
            }
            chainGeneration = (int)(newData & ChainGenerationMask);
            return true;
        }

        public void EndEnumeration(int chainGeneration)
        {
            if (chainGeneration != (_data & ChainGenerationMask))
            {
                return;
            }
            AccessControl.ExitSharedAccess();
        }

        public void MarkFree()
        {
            NextBlockId = 0;
            _data = 0xFFFFFFFF;
            _data2 = 0;
        }

        public void RequestFree() => Interlocked.Or(ref _data, FreeRequested);
    }

    /// <summary>
    /// Create a new instance
    /// </summary>
    /// <param name="stride">The size of each allocated block. An extra 8 bytes will be added to store the block header</param>
    /// <param name="entryCountPerPage">The count of block to allocate memory as one bulk (1 GC block allocated
    /// for <paramref name="entryCountPerPage"/> blocks).</param>
    /// <param name="parent">Parent resource for the internal bitmap.</param>
    /// <param name="memoryAllocator">Memory allocator for internal bitmap storage.</param>
    protected ChainedBlockAllocatorBase(int stride, int entryCountPerPage, IResource parent, IMemoryAllocator memoryAllocator)
        : base(stride + BlockHeaderSize, entryCountPerPage, parent, memoryAllocator)
    {
        // Reserve block 0 as sentinel - 0 means "no next block" in the chain header
        AllocateBlockInternal(out _);
    }

    /// <summary>
    /// The block size, it excludes the 8 bytes used for the block header.
    /// </summary>
    protected new int Stride => base.Stride - BlockHeaderSize;

    /// <summary>
    /// Allocate a new block
    /// </summary>
    /// <param name="blockId">The id of the allocated block</param>
    /// <param name="chainRoot"><c>true</c> if the block should be the root of a new chain, <c>false</c> otherwise.</param>
    /// <returns>Return span of the block data.</returns>
    protected Span<byte> AllocateBlock(out int blockId, bool chainRoot)
    {
        var span = AllocateBlockAsSpanInternal(out blockId);
        var bh = span.Cast<byte, BlockHeader>();
        bh.Clear();
        ref var header = ref bh[0];

        if (chainRoot)
        {
            header.ChainGeneration = Interlocked.Increment(ref NextChainGeneration);
            header.ChainLength = 1;
            header.IsChainRoot = true;
            header.LastBlockId = blockId;
        }

        return span.Slice(BlockHeaderSize);  // Skip the 8-byte chain header
    }

    /// <summary>
    /// Chain a list of blocks together.
    /// </summary>
    /// <param name="blockIds">The given block Ids will be chained together.</param>
    public void Chain(params int[] blockIds)
    {
        if (blockIds.Length < 2)
        {
            return;
        }
        
        var left = blockIds[0];
        for (int i = 1; i < blockIds.Length; i++)
        {
            Chain(left, blockIds[i]);
            left = blockIds[i];
        }
    }

    /// <summary>
    /// Retrieve the length of a chain
    /// </summary>
    /// <param name="rootBlockId">The root block id, the first block of the chain.</param>
    /// <returns>The chain length</returns>
    /// <exception cref="InvalidOperationException">If the method is called with a block ID that is not a chain root</exception>
    public int GetChainLength(int rootBlockId)
    {
        if (rootBlockId == 0)
        {
            InvalidOperationException("0 is not a valid root block id");
        }
        
        ref var blockHeader = ref GetBlockAsSpanInternal(rootBlockId).Cast<byte, BlockHeader>()[0];
        if (!blockHeader.IsChainRoot)
        {
            InvalidOperationException($"Method must be called with a rootBlockId, {rootBlockId} is not");
        }
        return blockHeader.ChainLength;
    }

    /// <summary>
    /// Get the generation of a block.
    /// </summary>
    /// <param name="blockId">ID of the block, it can be a chain root or any other.</param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException">If the method is called with a block ID of 0</exception>
    public int GetBlockChainGeneration(int blockId)
    {
        if (blockId == 0)
        {
            InvalidOperationException("0 is not a valid root block id");
        }
        
        ref var blockHeader = ref GetBlockAsSpanInternal(blockId).Cast<byte, BlockHeader>()[0];
        return blockHeader.ChainGeneration;
    }

    /// <summary>
    /// Retrieve the last block ID of a chain.
    /// </summary>
    /// <param name="rootBlockId">The root block id, the first block of the chain.</param>
    /// <returns>The ID of the last block in the chain.</returns>
    /// <exception cref="InvalidOperationException">If the method is called with a block ID of 0</exception>
    public int GetLastBlockInChain(int rootBlockId)
    {
        if (rootBlockId == 0)
        {
            InvalidOperationException("0 is not a valid root block id");
        }
        
        ref var blockHeader = ref GetBlockAs<BlockHeader>(rootBlockId);
        if (!blockHeader.IsChainRoot)
        {
            InvalidOperationException($"Method must be called with a rootBlockId, {rootBlockId} is not");
        }
        return blockHeader.LastBlockId;
    }

    /// <summary>
    /// Get the block ID of the root block of a chain from any block that is part of it.
    /// </summary>
    /// <param name="blockId">The block to get the chain root block ID from.</param>
    /// <returns>The ID of the root block of the chain.</returns>
    /// <exception cref="InvalidOperationException">If the method is called with a block ID of 0</exception>
    public int GetChainRoot(int blockId)
    {
        if (blockId == 0)
        {
            InvalidOperationException("0 is not a valid root block id");
        }

        ref var blockHeader = ref GetBlockAsSpanInternal(blockId).Cast<byte, BlockHeader>()[0];
        return blockHeader.GetChainRootBlockId(blockId);
    }
    
    /// <summary>
    /// Chain two blocks together.
    /// </summary>
    /// <param name="blockId">The id of the block to chain another next to it.</param>
    /// <param name="nextBlockId">The id of the block to chain after <paramref name="blockId"/>. Can be 0 to break the chain.</param>
    /// <returns>The id of the block that was previously chained after <paramref name="blockId"/>. If <paramref name="nextBlockId"/> was 0, every block
    /// following <paramref name="blockId"/> were detached and are now making a new separate chain. It is the responsibility of the caller to free
    /// these blocks or to chain them somewhere else.</returns>
    /// <remarks>If <paramref name="blockId"/> is already chained to a following block, this block will be added at the end of the chain
    /// of <paramref name="nextBlockId"/>.
    /// For instance if <paramref name="blockId"/> was: A with the chain [A, B, C] and <paramref name="nextBlockId"/> was D with the chain [D, E, F],
    /// the resulted chain will be [A, D, E, F, B, C]
    /// The chained blocks will use the same chain generation than the one they are chained to.
    /// </remarks>
    /// <exception cref="InvalidOperationException">If the method is called with a <paramref name="blockId"/> of 0</exception>
    public int Chain(int blockId, int nextBlockId)
    {
        if (blockId == 0)
        {
            InvalidOperationException("0 is not a valid root block id");
        }
        
        ref var blockHeader = ref GetBlockAsSpanInternal(blockId).Cast<byte, BlockHeader>()[0];
        
        var chainRootBlockId = blockHeader.GetChainRootBlockId(blockId);
        ref var chainRootHeader = ref GetBlockAsSpanInternal(chainRootBlockId).Cast<byte, BlockHeader>()[0];
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.SegmentAllocationLockTimeout);
        if (!chainRootHeader.AccessControl.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("SegmentAllocation/Chain", TimeoutOptions.Current.SegmentAllocationLockTimeout);
        }

        var oldNextBlockId = blockHeader.NextBlockId;
        var chainGen = chainRootHeader.ChainGeneration;

        // Find the end of the right chain to link the old next of the left chain. E.g.: goes, D, E, F then link B to F.
        // We replace the chain generation of the right chain to match the one of the left chain.
        if (nextBlockId != 0)
        {
            ref var nextChainHeader = ref GetBlockAsSpanInternal(nextBlockId).Cast<byte, BlockHeader>()[0];
            nextChainHeader.ChainGeneration = chainGen;
            nextChainHeader.IsChainRoot = false;
            nextChainHeader.ChainRootBlockId = chainRootBlockId;
            var rightChainLength = 1;
            var lastBlockId = nextBlockId;
            while (nextChainHeader.NextBlockId != 0)
            {
                lastBlockId = nextChainHeader.NextBlockId;
                nextChainHeader = ref GetBlockAsSpanInternal(lastBlockId).Cast<byte, BlockHeader>()[0];
                nextChainHeader.ChainGeneration = chainGen;
                nextChainHeader.ChainRootBlockId = chainRootBlockId;
                ++rightChainLength;
            }

            nextChainHeader.NextBlockId = oldNextBlockId;
            chainRootHeader.ChainLength += rightChainLength;

            if (oldNextBlockId == 0)
            {
                chainRootHeader.LastBlockId = lastBlockId;
            }
        }

        // If the given nextBlockId is 0, it means we are detaching everything after, creating a new chain
        else if (oldNextBlockId != 0)
        {
            var orphanChainGeneration = Interlocked.Increment(ref NextChainGeneration);
            var orphanChainLength = 1;
            var orphanLastBlockId = oldNextBlockId;
            
            ref var rootOrphanChainHeader = ref GetBlockAsSpanInternal(oldNextBlockId).Cast<byte, BlockHeader>()[0];
            rootOrphanChainHeader.IsChainRoot = true;
            rootOrphanChainHeader.ChainGeneration = orphanChainGeneration;
            
            ref var nextChainHeader = ref rootOrphanChainHeader;
            nextChainHeader.ChainGeneration = orphanChainGeneration;
            while (nextChainHeader.NextBlockId != 0)
            {
                orphanLastBlockId = nextChainHeader.NextBlockId;
                
                nextChainHeader = ref GetBlockAsSpanInternal(nextChainHeader.NextBlockId).Cast<byte, BlockHeader>()[0];
                nextChainHeader.ChainGeneration = orphanChainGeneration;
                nextChainHeader.IsChainRoot = false;
                nextChainHeader.ChainRootBlockId = oldNextBlockId;
                
                ++orphanChainLength;
            }
            chainRootHeader.ChainLength -= orphanChainLength;
            chainRootHeader.LastBlockId = blockId;
            rootOrphanChainHeader.ChainLength = orphanChainLength;
            rootOrphanChainHeader.LastBlockId = orphanLastBlockId;
        }

        blockHeader.NextBlockId = nextBlockId;
        chainRootHeader.AccessControl.ExitExclusiveAccess();

        return oldNextBlockId;
    }

    /// <summary>
    /// Thread-safe append a new block after the given one
    /// </summary>
    /// <param name="blockId">The id of the block to append after</param>
    /// <param name="newBlockId">The id of the block that is after</param>
    /// <returns>The Span data of the block that is after</returns>
    /// <remarks>If there's already a block following, nothing will be changed.</remarks>
    public Span<byte> SafeAppend(int blockId, out int newBlockId)
    {
        ref var rootHeader = ref GetBlockAsSpanInternal(blockId).Cast<byte, BlockHeader>()[0];
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.SegmentAllocationLockTimeout);
        if (!rootHeader.AccessControl.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("SegmentAllocation/SafeAppend", TimeoutOptions.Current.SegmentAllocationLockTimeout);
        }

        Span<byte> span;
        if (rootHeader.NextBlockId == 0)
        {
            span = AllocateBlock(out newBlockId, false);
            rootHeader.NextBlockId = newBlockId;
        }
        else
        {
            newBlockId = rootHeader.NextBlockId;
            span = GetBlockAsSpanInternal(newBlockId);
        }
        
        rootHeader.AccessControl.ExitExclusiveAccess();
        
        // Skip header
        return span.Slice(BlockHeaderSize);
    }

    /// <summary>
    /// Get the address of a block's data
    /// </summary>
    /// <param name="blockId"></param>
    /// <returns>The address of the block data, excluding the chain header.</returns>
    protected Span<byte> GetBlockData(int blockId)
    {
        if (blockId == 0)
        {
            return null;
        }
        return GetBlockAsSpanInternal(blockId).Slice(BlockHeaderSize);
    }

    protected Span<byte> NextBlock(Span<byte> blockDataSpan)
    {
        var nextBlockId = GetNextBlockInternal(blockDataSpan);
        return nextBlockId == 0 ? Span<byte>.Empty : GetBlockData(nextBlockId);
    }

    protected int NextBlock(int blockId) => GetNextBlockInternal(GetBlockData(blockId));

    private int GetNextBlockInternal(Span<byte> blockDataSpan)
    {
        fixed (byte* blockPtr = blockDataSpan)
        {
            var blockHeader = (BlockHeader*)blockPtr - 1;
            if (blockHeader->NextBlockId == 0)
            {
                return 0;
            }

            var nextChainGeneration = GetBlockAsSpanInternal(blockHeader->NextBlockId).Cast<byte, BlockHeader>()[0].ChainGeneration;
            if (blockHeader->ChainGeneration != nextChainGeneration)
            {
                blockHeader->NextBlockId = 0;
                return 0;
            }

            return blockHeader->NextBlockId;
        }
    }

    /// <summary>
    /// Free a whole chain
    /// </summary>
    /// <param name="rootBlockId">The root block id, the first block of the chain.</param>
    /// <remarks>This method does nothing if <paramref name="rootBlockId"/> is 0 (not a valid block ID)</remarks>
    /// <exception cref="InvalidOperationException">If the method is called with a block ID that is not a chain root</exception>
    public void FreeChain(int rootBlockId)
    {
        if (rootBlockId == 0)
        {
            return;
        }

        var curSpan = GetBlockAsSpanInternal(rootBlockId);
        ref var rootHeader = ref curSpan.Cast<byte, BlockHeader>()[0];
        if (!rootHeader.IsChainRoot)
        {
            InvalidOperationException($"Method must be called with a rootBlockId. {rootBlockId} is not");
        }
        var generation = rootHeader.ChainGeneration;
        
        // Signal we want to free the block
        rootHeader.RequestFree();
        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.SegmentAllocationLockTimeout);
        if (!rootHeader.AccessControl.EnterExclusiveAccess(ref wc))
        {
            ThrowHelper.ThrowLockTimeout("SegmentAllocation/FreeChain", TimeoutOptions.Current.SegmentAllocationLockTimeout);
        }

        if (generation == rootHeader.ChainGeneration)
        {
            var curBlockId = NextBlock(rootBlockId);
            ref var curHeader = ref GetBlockAsSpanInternal(curBlockId).Cast<byte, BlockHeader>()[0];
            
            while (curBlockId != 0)
            {
                var nextBlockId = NextBlock(curBlockId);

                // We are ready to free the block
                curHeader.MarkFree();
                base.FreeBlockInternal(curBlockId);

                curBlockId = nextBlockId;
                curHeader = ref GetBlockAsSpanInternal(curBlockId).Cast<byte, BlockHeader>()[0];
            }
        }
        
        rootHeader.AccessControl.ExitExclusiveAccess();
        rootHeader.MarkFree();
        base.FreeBlockInternal(rootBlockId);
    }

    public bool RequestEnumeration(int blockId, out int chainGeneration) => GetBlockAsSpanInternal(blockId).Cast<byte, BlockHeader>()[0].RequestEnumeration(out chainGeneration);
    public void EndEnumeration(int blockId, int chainGeneration) => GetBlockAsSpanInternal(blockId).Cast<byte, BlockHeader>()[0].EndEnumeration(chainGeneration);

    public Enumerable EnumerateChainedBlock(int rootBlockId) => new(this, rootBlockId);

    internal IntPtr AsIntPtr(int blockId) => (IntPtr)GetBlockInternal(blockId);
    
    [DoesNotReturn]
    private static void InvalidOperationException(string msg) => throw new InvalidOperationException(msg);

    [PublicAPI]
    public readonly struct Enumerable
    {
        private readonly ChainedBlockAllocatorBase _owner;
        private readonly int _blockId;

        public Enumerable(ChainedBlockAllocatorBase owner, int blockId)
        {
            _owner = owner;
            _blockId = blockId;
        }

        public Enumerator GetEnumerator() => new(_owner, _blockId);
    }

    [PublicAPI]
    public ref struct Enumerator : IDisposable
    {
        private readonly ChainedBlockAllocatorBase _owner;
        private int _nextBlockId;
        private ref BlockHeader _rootHeader;
        private int _chainGeneration;

        public Enumerator(ChainedBlockAllocatorBase owner, int blockId)
        {
            Current = 0;
            _nextBlockId = blockId;
            _rootHeader = ref owner.GetBlockAsSpanInternal(blockId).Cast<byte, BlockHeader>()[0];
            _owner = _rootHeader.RequestEnumeration(out _chainGeneration) ? owner : null;
        }

        public int Current { get; private set; }

        public bool MoveNext()
        {
            if (_owner == null || _nextBlockId == 0)
            {
                return false;
            }

            Current = _nextBlockId;

            _nextBlockId =  _owner.GetBlockAsSpanInternal(_nextBlockId).Cast<byte, BlockHeader>()[0].NextBlockId;
            if (_owner.GetBlockAsSpanInternal(_nextBlockId).Cast<byte, BlockHeader>()[0].ChainGeneration != _chainGeneration)
            {
                _nextBlockId = 0;
            }

            return true;
        }

        public void Dispose()
        {
            if (_owner != null)
            {
                _rootHeader.EndEnumeration(_chainGeneration);
            }
        }
    }
}