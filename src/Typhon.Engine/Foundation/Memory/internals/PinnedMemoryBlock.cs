using JetBrains.Annotations;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

[PublicAPI]
public unsafe class PinnedMemoryBlock : MemoryBlockBase
{
    public int Alignment { get; }
    public byte* DataAsPointer { get; private set; }
    public IntPtr DataAsIntPtr => (IntPtr)DataAsPointer;

    public override int EstimatedMemorySize => MemoryBlockSize;
    public override int MemoryBlockSize { get; }
    public override bool IsDisposed => DataAsPointer == null;
    public override Span<byte> DataAsSpan => new(DataAsPointer, MemoryBlockSize);
    public override Memory<byte> DataAsMemory => Memory;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            NativeMemory.AlignedFree(DataAsPointer);
            DataAsPointer = null;
        }
    }

    public override Span<byte> GetSpan() => new(DataAsPointer, MemoryBlockSize);

    public override MemoryHandle Pin(int elementIndex = 0) => new(DataAsPointer + elementIndex);

    public override void Unpin()
    {
        
    }

    public override IEnumerable<IResource> Children => [];

    public override IReadOnlyDictionary<string, object> GetDebugProperties() =>
        new Dictionary<string, object>
        {
            ["Size"] = EstimatedMemorySize,
            ["IsDisposed"] = IsDisposed,
            ["Allocator"] = Allocator.Id,
            ["Kind"] = "Pinned",
            ["Alignment"] = Alignment,
            ["Address"] = DataAsPointer != null ? $"0x{(long)DataAsPointer:X}" : "(freed)",
        };

    internal PinnedMemoryBlock(MemoryAllocator allocator, int size, int alignment, string resourceId, IResource parent, ushort sourceTag = 0) :
        base(allocator, resourceId, parent, sourceTag)
    {
        DataAsPointer = (byte*)NativeMemory.AlignedAlloc((UIntPtr)size, (UIntPtr)alignment);
        Alignment = alignment;
        MemoryBlockSize = size;
    }
}