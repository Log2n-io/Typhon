// unset

using JetBrains.Annotations;
using System;

namespace Typhon.Engine.Internals;

[PublicAPI]
internal unsafe class BlockAllocator : BlockAllocatorBase
{
    public BlockAllocator(int stride, int entryCountPerPage, IResource parent, IMemoryAllocator memoryAllocator) : 
        base(stride, entryCountPerPage, parent, memoryAllocator)
    {
    }

    public Span<byte> AllocateBlock(out int blockId) => new(AllocateBlockInternal(out blockId), Stride);
    public Span<byte> GetBlock(int blockId) => new(GetBlockInternal(blockId), Stride);
    public void FreeBlock(int blockId) => FreeBlockInternal(blockId);
}