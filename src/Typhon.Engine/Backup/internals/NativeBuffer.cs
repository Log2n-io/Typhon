using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// A page-aligned block of native memory, freed deterministically by <see cref="IDisposable.Dispose"/>, exposed as <see cref="Span{T}"/> and as
/// <see cref="Memory{T}"/> so an asynchronous file write can use it. The backup's I/O buffers live here, never in the GC heap: a 4 MiB array
/// per buffer per backup would be large-object garbage on every point.
/// </summary>
/// <remarks>
/// The owner must not dispose it while a write that uses <see cref="MemoryManager{T}.Memory"/> is in flight.
/// </remarks>
internal sealed unsafe class NativeBuffer : MemoryManager<byte>
{
    private byte* _pointer;

    public NativeBuffer(int length, int alignment = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        Length = length;
        _pointer = (byte*)NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment);
    }

    public int Length { get; }

    public Span<byte> Span => GetSpan();

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_pointer == null, this);
        return new Span<byte>(_pointer, Length);
    }

    public override MemoryHandle Pin(int elementIndex = 0) => new(_pointer + elementIndex);

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (_pointer != null)
        {
            NativeMemory.AlignedFree(_pointer);
            _pointer = null;
        }
    }
}
