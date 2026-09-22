using JetBrains.Annotations;
using System;
using System.Buffers;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>A block of GC-managed memory, addressed as a span or <see cref="Memory{T}"/> — never by pointer.</summary>
/// <remarks>
/// Raw pointers address page-cache and engine-allocator memory only (CLAUDE.md, Unsafe Code). <see cref="Pin"/> used to pin the array with a
/// <c>GCHandle</c> and hand out its address. A block that must be addressed by pointer comes from <see cref="IMemoryAllocator.AllocatePinned"/>.
/// </remarks>
[PublicAPI]
internal class MemoryBlockArray : MemoryBlockBase
{
    public byte[] DataAsArray { get; private set; }
    internal MemoryBlockArray(MemoryAllocator allocator, byte[] block, string resourceId, IResource parent, ushort sourceTag = 0) :
        base(allocator, resourceId ?? Guid.NewGuid().ToString(), parent, sourceTag)
    {
        DataAsArray = block;
    }

    public override int EstimatedMemorySize => DataAsArray?.Length ?? 0;
    public override int MemoryBlockSize => DataAsArray?.Length ?? 0;
    public override bool IsDisposed => DataAsArray == null;
    public override Span<byte> DataAsSpan => DataAsArray.AsSpan();
    public override Memory<byte> DataAsMemory => DataAsArray.AsMemory();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        DataAsArray = null;
    }

    public override Span<byte> GetSpan() => DataAsSpan;

    public override MemoryHandle Pin(int elementIndex = 0) =>
        throw new NotSupportedException("A MemoryBlockArray is GC memory and is never addressed by pointer. Allocate with IMemoryAllocator.AllocatePinned "
            + "for a block that must be.");

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
            ["Kind"] = "Array",
        };
}
