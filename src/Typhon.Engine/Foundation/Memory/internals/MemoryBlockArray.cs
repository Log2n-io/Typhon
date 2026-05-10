using JetBrains.Annotations;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

[PublicAPI]
public class MemoryBlockArray : MemoryBlockBase
{
    private int _pinCount;
    private GCHandle _handle;
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

    public unsafe override MemoryHandle Pin(int elementIndex = 0)
    {
        if (_pinCount == 0)
        {
            _handle = GCHandle.Alloc(DataAsArray, GCHandleType.Pinned);
        }

        _pinCount++;

        return new MemoryHandle((byte*)_handle.AddrOfPinnedObject() + elementIndex, _handle, this);
    }

    public override void Unpin()
    {
        --_pinCount;
        if (_pinCount == 0)
        {
            _handle.Free();
            _handle = default;
        }
    }

    public override IEnumerable<IResource> Children => [];

    public override IReadOnlyDictionary<string, object> GetDebugProperties() =>
        new Dictionary<string, object>
        {
            ["Size"] = EstimatedMemorySize,
            ["IsDisposed"] = IsDisposed,
            ["Allocator"] = Allocator.Id,
            ["Kind"] = "Array",
            ["IsPinned"] = _pinCount > 0,
        };
}