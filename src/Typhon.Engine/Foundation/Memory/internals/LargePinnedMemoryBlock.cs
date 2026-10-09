using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>What a <see cref="LargePinnedMemoryBlock"/> holds when it is handed out.</summary>
internal enum LargeBlockContents
{
    /// <summary>Whatever the platform gives. Nothing may read a byte it has not written.</summary>
    Undefined,

    /// <summary>All zero, by contract, without a clear: the OS hands out zeroed pages lazily, so a large block costs nothing at allocation.</summary>
    Zeroed,
}

/// <summary>
/// A native block whose size is a <c>long</c>, for the page cache and its per-slot tables once they can pass 2 GiB (#945).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a <see cref="System.Buffers.MemoryManager{T}"/> and not a <see cref="MemoryBlockBase"/>: those expose a whole-block
/// <see cref="Span{T}"/> and <see cref="Memory{T}"/>, whose lengths are <c>int</c>, so a block past 2 GiB could only implement them by throwing.
/// This type cannot express a whole-block span at all. Its user addresses it by pointer, and the page cache builds its I/O windows over it
/// (<see cref="PageCacheWindow"/>).
/// </para>
/// <para>
/// <see cref="LargeBlockContents.Undefined"/> is <c>NativeMemory.AlignedAlloc</c>, freed with <c>AlignedFree</c>.
/// <see cref="LargeBlockContents.Zeroed"/> is <c>NativeMemory.AllocZeroed</c> over size + alignment − 1, rounded up to the alignment, the raw
/// pointer kept for <c>Free</c>: <c>AllocZeroed</c> is zero by contract and lazy for large blocks, but only 16-byte aligned on Linux, and
/// there is no aligned zeroed allocation. The two pairs never cross.
/// </para>
/// <para>
/// Freed at dispose, which the resource tree runs when its parent is disposed. Like every engine block, its memory is gone once its owner is.
/// </para>
/// </remarks>
internal sealed unsafe class LargePinnedMemoryBlock : IMemoryResource, IDebugPropertiesProvider
{
    // The pointer to free: the aligned one for Undefined, the raw AllocZeroed one for Zeroed. Exchanged to 0 once, so a second Dispose frees nothing.
    private nint _raw;

    public MemoryAllocator Allocator { get; }

    /// <summary>The block's first byte, aligned to <see cref="Alignment"/>; <c>null</c> once disposed.</summary>
    public byte* DataAsPointer { get; private set; }

    /// <summary>The block's size in bytes.</summary>
    public long Size { get; }

    public int Alignment { get; }
    public LargeBlockContents Contents { get; }
    public ushort SourceTag { get; }
    public bool IsDisposed => DataAsPointer == null;
    public long EstimatedMemorySize => Size;

    /// <summary>The pointer <see cref="Dispose"/> frees, for a test that checks the zeroed round-up keeps it; 0 once disposed.</summary>
    internal nint RawPointerForTests => _raw;

    public string Id { get; }
    public string Name => Id;
    public int? Count => null;
    public ResourceType Type => ResourceType.Memory;
    public IResource Parent { get; }
    public IEnumerable<IResource> Children => [];
    public DateTime CreatedAt { get; }
    public IResourceRegistry Owner { get; }
    public bool RegisterChild(IResource child) => false;
    public bool RemoveChild(IResource resource) => false;

    internal LargePinnedMemoryBlock(MemoryAllocator allocator, long size, int alignment, LargeBlockContents contents, string id, IResource parent,
        ushort sourceTag)
    {
        Allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        Parent = parent ?? throw new ArgumentNullException(nameof(parent), "Parent resource cannot be null. Resources must have an explicit parent.");
        Id = id ?? throw new ArgumentNullException(nameof(id));

        if (contents == LargeBlockContents.Zeroed)
        {
            // native-alloc: this IS the allocator's 64-bit block (IMemoryAllocator.AllocateLargePinned), tracked by it and freed at dispose (#945).
            _raw = (nint)NativeMemory.AllocZeroed((nuint)(size + alignment - 1));
            DataAsPointer = (byte*)(((nuint)_raw + (nuint)(alignment - 1)) & ~(nuint)(alignment - 1));
        }
        else
        {
            // native-alloc: this IS the allocator's 64-bit block (IMemoryAllocator.AllocateLargePinned), tracked by it and freed at dispose (#945).
            _raw = (nint)NativeMemory.AlignedAlloc((nuint)size, (nuint)alignment);
            DataAsPointer = (byte*)_raw;
        }

        Size = size;
        Alignment = alignment;
        Contents = contents;
        SourceTag = sourceTag;
        CreatedAt = DateTime.UtcNow;
        Owner = Parent.Owner;
        Parent.RegisterChild(this);
    }

    public void Dispose()
    {
        var raw = Interlocked.Exchange(ref _raw, 0);
        if (raw == 0)
        {
            return;
        }

        DataAsPointer = null;
        if (Contents == LargeBlockContents.Zeroed)
        {
            NativeMemory.Free((void*)raw);
        }
        else
        {
            NativeMemory.AlignedFree((void*)raw);
        }

        Allocator.RemoveLarge(this);
        Parent.RemoveChild(this);
    }

    public IReadOnlyDictionary<string, object> GetDebugProperties() =>
        new Dictionary<string, object>
        {
            ["Size"] = Size,
            ["IsDisposed"] = IsDisposed,
            ["Allocator"] = Allocator.Id,
            ["Kind"] = "LargePinned",
            ["Contents"] = Contents.ToString(),
            ["Alignment"] = Alignment,
            ["Address"] = DataAsPointer != null ? $"0x{(long)DataAsPointer:X}" : "(freed)",
        };
}
