using JetBrains.Annotations;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

[PublicAPI]
internal unsafe class UnmanagedStructAllocator<T> : BlockAllocatorBase where T : unmanaged
{
    public ref T Allocate(out int blockId) => ref Unsafe.AsRef<T>(AllocateBlockInternal(out blockId));
    public ref T Get(int blockId) => ref Unsafe.AsRef<T>(GetBlockInternal(blockId));
    public void Free(int blockId) => FreeBlockInternal(blockId);

    public UnmanagedStructAllocator(int entryCountPerPage, IResource parent, IMemoryAllocator memoryAllocator)
        : base(sizeof(T), entryCountPerPage, parent, memoryAllocator)
    {
    }
}